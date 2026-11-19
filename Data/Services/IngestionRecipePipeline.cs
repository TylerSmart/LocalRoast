using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using App.Core.FoodCatalog;
using App.Core.Ingestion;
using App.Core.Recipes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace App.Data.Services;

/// <summary>
/// Orchestrates the 5-stage LLM recipe ingestion pipeline (Structured Extract -> Conditional
/// Summary Condense -> Hybrid Vector/Lexical Matching -> Validated Step Tokenization ->
/// Conditional Targeted Polish) and compiles an <see cref="IngestionExtractedDraftDto"/>.
/// </summary>
public sealed partial class IngestionRecipePipeline(
    AppDbContext dbContext,
    ILlmExtractionClient llmClient,
    ICatalogVectorSearchService catalogSearch,
    ILogger<IngestionRecipePipeline>? logger = null
) : IIngestionRecipePipeline
{
    private const int DescriptionMaxLength = 250;
    private const int Stage3BatchSize = 10;
    private const int Stage4BatchSize = 8;
    private const int MaxStage4SingleStepRepairTurns = 2;

    public async Task<IngestionPipelineResult> RunAsync(
        IngestionPipelineRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SourceText))
        {
            throw new ArgumentException(
                "SourceText cannot be empty when running ingestion pipeline.",
                nameof(request)
            );
        }

        // ============================================================
        // STAGE 1: Structured Extraction
        // ============================================================
        await ReportProgressAsync(
            request.OnStageProgress,
            "Stage 1/5: Extracting recipe structure..."
        );

        var stage1 = await RunStage1Async(request.Config, request.SourceText, cancellationToken);

        var workIngredients = FlattenIngredients(stage1, request.SourceText);
        var workSteps = FlattenSteps(stage1);
        var incompatibleAlts = FindAllIncompatibleAlts(workIngredients);

        var title = string.IsNullOrWhiteSpace(stage1.Title)
            ? "Untitled Recipe"
            : IngestionMetadataAndTagHelper.Truncate(stage1.Title.Trim(), 200);
        var description = string.IsNullOrWhiteSpace(stage1.Description)
            ? null
            : stage1.Description.Trim();
        var servings = stage1.Servings;
        var yieldDisplay = IngestionMetadataAndTagHelper.ResolveYieldDisplay(
            stage1.Yield,
            request.SourceText
        );
        var timing = stage1.Timing;
        var rawTags = stage1.Tags?.ToList() ?? [];

        // ============================================================
        // STAGE 2: Conditional Description Condensing
        // ============================================================
        if (!string.IsNullOrWhiteSpace(description) && ShouldCondenseDescription(description))
        {
            await ReportProgressAsync(
                request.OnStageProgress,
                "Stage 2/5: Condensing recipe summary..."
            );

            description = await RunStage2ShortenDescriptionAsync(
                request.Config,
                title,
                description,
                cancellationToken
            );
        }

        // ============================================================
        // STAGE 3: Hybrid Catalog Search & Batch Matching
        // ============================================================
        await ReportProgressAsync(
            request.OnStageProgress,
            "Stage 3/5: Matching ingredients to food catalog..."
        );

        var catalogFoods = await dbContext
            .FoodItems.AsNoTracking()
            .Include(f => f.Aliases)
            .Select(f => new CatalogFoodLookupItem(
                f.Id,
                f.Name,
                f.NormalizedName,
                f.IsPantryStaple,
                f.Aliases.Select(a => a.NormalizedAlias).ToList(),
                f.Category
            ))
            .ToListAsync(cancellationToken);

        var catalogTags = await dbContext
            .Tags.AsNoTracking()
            .Select(t => new CatalogTagLookupItem(t.Id, t.Name, t.NormalizedName, t.Slug))
            .ToListAsync(cancellationToken);

        var rawRecipes = await dbContext
            .Recipes.AsNoTracking()
            .Where(r =>
                r.Status != RecipeStatus.Archived
                && (!request.ExcludeRecipeId.HasValue || r.Id != request.ExcludeRecipeId.Value)
            )
            .Select(r => new { r.Id, r.Title })
            .ToListAsync(cancellationToken);

        var catalogRecipes = rawRecipes
            .Select(r => new CatalogRecipeLookupItem(
                r.Id,
                r.Title,
                RecipeUnitAndSectionHelpers.NormalizeRecipeLookupKey(r.Title)
            ))
            .ToList();

        var (reconciledPrimary, reconciledReplacements) = await RunStage3Async(
            request.Config,
            workIngredients,
            catalogFoods,
            catalogRecipes,
            cancellationToken
        );

        // ============================================================
        // STAGE 4: Batch Step Tokenization + Multi-Turn Repair
        // ============================================================
        await ReportProgressAsync(
            request.OnStageProgress,
            "Stage 4/5: Tokenizing step instructions..."
        );

        var validatedSteps = await RunStage4Async(
            request.Config,
            workIngredients,
            workSteps,
            cancellationToken
        );

        // ============================================================
        // STAGE 5: Conditional Targeted Polish
        // ============================================================
        var remainingStepWarnings = validatedSteps
            .Where(s => s.ValidationWarnings.Count > 0)
            .ToList();
        var missingMetadata =
            (timing?.Prep is null && timing?.Cook is null) || servings is null or <= 0m;

        if (incompatibleAlts.Count > 0 || remainingStepWarnings.Count > 0 || missingMetadata)
        {
            // Stage 5 Targeted Polish runs on the primary Text workflow profile; the optional
            // Refinement workflow runs as its own dedicated post-polish pass controlled by the
            // RefineDuringIngestion / RefineDuringIdle settings.
            var polishConfig = request.Config;

            await ReportProgressAsync(
                request.OnStageProgress,
                "Stage 5/5: Polishing flagged recipe details..."
            );

            Stage5TargetedPolishResponseDto? polishRes = null;
            polishRes = await RunStage5PolishAsync(
                polishConfig,
                request.SourceText,
                title,
                description,
                servings,
                yieldDisplay,
                timing,
                rawTags,
                workIngredients,
                validatedSteps,
                incompatibleAlts,
                remainingStepWarnings,
                missingMetadata,
                cancellationToken
            );

            if (polishRes is not null)
            {
                if (!string.IsNullOrWhiteSpace(polishRes.Title))
                {
                    title = IngestionMetadataAndTagHelper.Truncate(polishRes.Title.Trim(), 200);
                }

                if (!string.IsNullOrWhiteSpace(polishRes.Description))
                {
                    var polishedDesc = polishRes.Description.Trim();
                    description =
                        ShouldCondenseDescription(polishedDesc)
                        && description is not null
                        && polishedDesc.Length > 300
                            ? description
                            : polishedDesc;
                }

                if (polishRes.Servings is > 0m and <= 999m)
                {
                    servings = polishRes.Servings;
                }

                if (!string.IsNullOrWhiteSpace(polishRes.Yield))
                {
                    yieldDisplay = IngestionMetadataAndTagHelper.CleanOptionalText(
                        polishRes.Yield,
                        100
                    );
                }

                if (polishRes.Timing is not null)
                {
                    timing = new Stage1TimingDto(
                        polishRes.Timing.Prep ?? timing?.Prep,
                        polishRes.Timing.Cook ?? timing?.Cook,
                        polishRes.Timing.Total ?? timing?.Total
                    );
                }

                if (polishRes.Tags is { Count: > 0 })
                {
                    rawTags = polishRes.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
                }

                foreach (var patch in polishRes.IngredientPatches ?? [])
                {
                    if (patch.Index < 0 || patch.Index >= workIngredients.Count)
                    {
                        continue;
                    }

                    var target = workIngredients[patch.Index];
                    var proposedName = !string.IsNullOrWhiteSpace(patch.Name)
                        ? patch.Name.Trim()
                        : target.Name;
                    if (
                        !string.IsNullOrWhiteSpace(patch.Name)
                        && OrAlternativeRegex().IsMatch(patch.Name)
                    )
                    {
                        logger?.LogInformation(
                            "Stage 5b guardrail rejected unsplit 'or' alternative in ingredientPatch [{Index}] name='{Name}'.",
                            patch.Index,
                            patch.Name
                        );
                        proposedName = target.Name;
                    }

                    var proposedQty = patch.Quantity ?? target.Quantity;
                    var proposedMax = patch.MaxQuantity ?? target.MaxQuantity;
                    if (
                        patch.MaxQuantity.HasValue
                        && (!proposedQty.HasValue || proposedMax!.Value < proposedQty.Value)
                    )
                    {
                        logger?.LogInformation(
                            "Stage 5b guardrail rejected invalid range in ingredientPatch [{Index}] ({Qty}..{Max}).",
                            patch.Index,
                            proposedQty,
                            proposedMax
                        );
                        proposedMax = target.MaxQuantity;
                    }

                    var proposedUnit = patch.Unit ?? target.Unit;
                    var proposedAlt = patch.AlternateMeasurements ?? target.AlternateMeasurements;
                    if (
                        patch.AlternateMeasurements?.Any(a =>
                            !string.IsNullOrWhiteSpace(a.Unit)
                            && DimensionOrTempUnits.Contains(a.Unit.Trim())
                        ) == true
                    )
                    {
                        logger?.LogInformation(
                            "Stage 5b guardrail rejected dimension/temperature unit in alternateMeasurements for ingredientPatch [{Index}].",
                            patch.Index
                        );
                        proposedAlt = target.AlternateMeasurements;
                    }
                    else if (
                        StepTokenValidator.TryFindIncompatibleAlternateMeasurement(
                            proposedQty,
                            proposedMax,
                            proposedUnit,
                            proposedAlt,
                            out var incompatReason
                        )
                    )
                    {
                        logger?.LogInformation(
                            "Stage 5b guardrail rejected non-equivalent alternateMeasurements in ingredientPatch [{Index}]: {Reason}",
                            patch.Index,
                            incompatReason
                        );
                        proposedAlt = target.AlternateMeasurements;
                    }

                    var nameChanged = !string.Equals(
                        target.Name,
                        proposedName,
                        StringComparison.OrdinalIgnoreCase
                    );

                    target.Name = proposedName;
                    target.Quantity = proposedQty;
                    target.MaxQuantity = proposedMax;
                    target.Unit = proposedUnit;
                    target.AlternateMeasurements = proposedAlt;

                    if (
                        nameChanged
                        && reconciledPrimary.TryGetValue(patch.Index, out var existingMatch)
                        && !existingMatch.IsRecipeReference
                        && existingMatch.IsNewFoodItem
                    )
                    {
                        var updatedCanonical = FoodNameSanitizer.NormalizeCanonicalFoodName(
                            proposedName
                        );
                        if (!string.IsNullOrWhiteSpace(updatedCanonical))
                        {
                            var exactExisting = catalogFoods.FirstOrDefault(f =>
                                string.Equals(
                                    f.NormalizedName,
                                    updatedCanonical,
                                    StringComparison.OrdinalIgnoreCase
                                )
                                || f.NormalizedAliases.Any(a =>
                                    string.Equals(
                                        a,
                                        updatedCanonical,
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                )
                            );
                            if (exactExisting is not null)
                            {
                                reconciledPrimary[patch.Index] = existingMatch with
                                {
                                    MatchedFoodItemId = exactExisting.Id,
                                    MatchedFoodItemName = exactExisting.Name,
                                    IsNewFoodItem = false,
                                    CreateNewFoodItem = false,
                                    ProposedFoodItemName = null,
                                    SuggestedCategory = exactExisting.Category,
                                    SuggestedIsPantryStaple = exactExisting.IsPantryStaple,
                                };
                            }
                            else
                            {
                                var updatedCategory = FoodCategoryClassifier.Classify(
                                    updatedCanonical
                                );
                                reconciledPrimary[patch.Index] = existingMatch with
                                {
                                    ProposedFoodItemName = updatedCanonical,
                                    SuggestedCategory = updatedCategory,
                                    SuggestedIsPantryStaple =
                                        FoodCategoryClassifier.IsLikelyPantryStaple(
                                            updatedCanonical,
                                            updatedCategory
                                        ),
                                };
                            }
                        }
                    }

                    if (patch.PrepNote is not null)
                    {
                        target.PrepNote = string.IsNullOrWhiteSpace(patch.PrepNote)
                            ? null
                            : patch.PrepNote.Trim();
                    }
                    if (patch.IsOptional.HasValue)
                    {
                        target.IsOptional = patch.IsOptional.Value;
                    }
                }

                foreach (var stepPatch in polishRes.StepPatches ?? [])
                {
                    var idx = validatedSteps.FindIndex(s => s.StepNumber == stepPatch.StepNumber);
                    if (idx < 0 || stepPatch.Tokens is null)
                    {
                        continue;
                    }

                    var existing = validatedSteps[idx];
                    var candidate = StepTokenValidator.BuildAndValidateStep(
                        existing.StepNumber,
                        existing.SectionName,
                        existing.OriginalInstruction,
                        stepPatch.Tokens,
                        workIngredients.Count
                    );

                    if (
                        candidate.ParsedTokens.Count > 0
                        && candidate.ValidationWarnings.Count <= existing.ValidationWarnings.Count
                    )
                    {
                        validatedSteps[idx] = candidate;
                    }
                }

                incompatibleAlts = FindAllIncompatibleAlts(workIngredients);
            }
        }

        // ============================================================
        // COMPILE FINAL DRAFT
        // ============================================================
        var draft = CompileDraft(
            request,
            title,
            description,
            servings,
            yieldDisplay,
            timing,
            rawTags,
            workIngredients,
            reconciledPrimary,
            reconciledReplacements,
            validatedSteps,
            incompatibleAlts,
            catalogTags
        );

        var isPlausible =
            !string.IsNullOrWhiteSpace(title)
            && !title.Equals("Untitled Recipe", StringComparison.Ordinal)
            && (draft.Ingredients?.Count ?? 0) > 0
            && (draft.Steps?.Count ?? 0) > 0;

        return new IngestionPipelineResult(
            Draft: draft,
            IsPlausibleRecipe: isPlausible,
            ExtractedModel: request.Config.TextModel?.Trim() ?? "unknown"
        );
    }

    private async Task<Stage1RecipeDto> RunStage1Async(
        EffectiveLlmConnectionConfig config,
        string sourceText,
        CancellationToken cancellationToken
    )
    {
        var prompt = IngestionPromptLoader.LoadPrompt(IngestionPromptLoader.Stage1ExtractRecipe);
        var messages = new List<LlmStageChatMessage>
        {
            new("system", prompt),
            new("user", sourceText),
        };

        string? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var res = await llmClient.ExecuteStructuredStageAsync<Stage1RecipeDto>(
                config,
                stageName: "stage1_extract_recipe",
                messages: messages,
                jsonSchema: MultiStageSchemas.Stage1ExtractRecipeSchema,
                overrideThinking: null,
                maxTokens: 16384,
                cancellationToken: cancellationToken
            );

            var parsed = res.Parsed;
            var ingCount = parsed?.Sections?.Sum(s => s.Ingredients?.Count ?? 0) ?? 0;
            var stepCount = parsed?.Sections?.Sum(s => s.Instructions?.Count ?? 0) ?? 0;

            var hasMinimumStructure =
                string.IsNullOrWhiteSpace(res.Error)
                && parsed is not null
                && !string.IsNullOrWhiteSpace(parsed.Title)
                && ingCount > 0
                && stepCount > 0;

            if (hasMinimumStructure)
            {
                var missingStepsFeedback = FindMissingSourceInstructionFeedback(
                    sourceText,
                    parsed!
                );
                var ingredientFeedback = ValidateStage1IngredientsFeedback(parsed!, sourceText);
                var combinedFeedback = string.Join(
                    " ",
                    new[] { missingStepsFeedback, ingredientFeedback }.Where(f =>
                        !string.IsNullOrWhiteSpace(f)
                    )
                );

                if (string.IsNullOrWhiteSpace(combinedFeedback) || attempt == 3)
                {
                    return parsed!;
                }

                lastError = combinedFeedback;
            }
            else
            {
                lastError =
                    res.Error
                    ?? (
                        parsed is null ? "Stage 1 returned null recipe payload."
                        : ingCount == 0 && stepCount == 0
                            ? "Stage 1 returned ZERO ingredients and ZERO instructions."
                        : stepCount == 0
                            ? "Stage 1 returned ZERO instructions (preserve EVERY section's ingredients AND extract ALL instruction steps)."
                        : $"Stage 1 returned incomplete recipe (title='{parsed.Title}', ingredients={ingCount}, steps={stepCount})."
                    );
            }

            if (attempt < 3)
            {
                logger?.LogWarning(
                    "Stage 1 attempt {Attempt} incomplete or invalid ({Error}); retrying.",
                    attempt,
                    lastError
                );
                messages.Add(
                    new(
                        "assistant",
                        string.IsNullOrWhiteSpace(res.RawContent) ? "{}" : res.RawContent
                    )
                );
                messages.Add(
                    new(
                        "user",
                        hasMinimumStructure
                            ? lastError
                            : $"The previous extraction failed validation: {lastError}. Please return a complete valid JSON recipe with title, sections containing all ingredients and all instruction steps, timing, and tags."
                    )
                );
            }
        }

        throw new InvalidOperationException(
            $"Multi-stage recipe extraction failed at Stage 1: {lastError}"
        );
    }

    private async Task<string> RunStage2ShortenDescriptionAsync(
        EffectiveLlmConnectionConfig config,
        string title,
        string originalDescription,
        CancellationToken cancellationToken
    )
    {
        var targetLength = Math.Max(100, (int)Math.Floor(DescriptionMaxLength * 0.75));
        var stage2Prompt = IngestionPromptLoader.LoadShortenDescriptionPrompt(DescriptionMaxLength);
        var stage2User = JsonSerializer.Serialize(
            new
            {
                title,
                maxLength = DescriptionMaxLength,
                targetLength,
                currentLength = originalDescription.Length,
                description = originalDescription,
            }
        );

        var messages = new List<LlmStageChatMessage>
        {
            new("system", stage2Prompt),
            new("user", stage2User),
        };

        string? bestShortened = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var stage2Res =
                await llmClient.ExecuteStructuredStageAsync<Stage2ShortenedDescriptionDto>(
                    config,
                    stageName: "stage2_shorten_description",
                    messages: messages,
                    jsonSchema: MultiStageSchemas.Stage2ShortenDescriptionSchema,
                    overrideThinking: false,
                    maxTokens: 1024,
                    cancellationToken: cancellationToken
                );

            if (stage2Res.Parsed is null || string.IsNullOrWhiteSpace(stage2Res.Parsed.Description))
            {
                if (attempt < 3)
                {
                    messages.Add(
                        new(
                            "assistant",
                            string.IsNullOrWhiteSpace(stage2Res.RawContent)
                                ? "{}"
                                : stage2Res.RawContent
                        )
                    );
                    messages.Add(
                        new(
                            "user",
                            $"Return valid JSON with a concise non-empty description strictly <= {DescriptionMaxLength} characters."
                        )
                    );
                }
                continue;
            }

            var candidate = stage2Res.Parsed.Description.Trim();
            if (bestShortened is null || candidate.Length < bestShortened.Length)
            {
                bestShortened = candidate;
            }

            if (candidate.Length <= DescriptionMaxLength)
            {
                return candidate;
            }

            if (attempt < 3)
            {
                messages.Add(
                    new(
                        "assistant",
                        string.IsNullOrWhiteSpace(stage2Res.RawContent)
                            ? "{}"
                            : stage2Res.RawContent
                    )
                );
                messages.Add(
                    new(
                        "user",
                        $"Your shortened description is {candidate.Length} characters, which still exceeds the {DescriptionMaxLength}-character limit by {candidate.Length - DescriptionMaxLength} characters. Trim it down to roughly {targetLength} characters (strictly <= {DescriptionMaxLength})."
                    )
                );
            }
        }

        if (
            bestShortened is not null
            && (bestShortened.Length <= originalDescription.Length || bestShortened.Length <= 280)
        )
        {
            return bestShortened;
        }

        return originalDescription;
    }

    private async Task<(
        Dictionary<int, ReconciledMatch> PrimaryMatches,
        Dictionary<(int IngIndex, int RepIndex), ReconciledMatch> ReplacementMatches
    )> RunStage3Async(
        EffectiveLlmConnectionConfig config,
        List<WorkIngredient> ingredients,
        IReadOnlyList<CatalogFoodLookupItem> catalogFoods,
        IReadOnlyList<CatalogRecipeLookupItem> catalogRecipes,
        CancellationToken cancellationToken
    )
    {
        var queryItems = new List<Stage3SearchQueryItem>();
        var replacementKeyMap = new Dictionary<(int IngIndex, int RepIndex), int>();

        for (var i = 0; i < ingredients.Count; i++)
        {
            var ing = ingredients[i];
            queryItems.Add(new Stage3SearchQueryItem(i, ing.Name, ing.PrepNote, ing.Unit));
        }

        var nextKey = ingredients.Count;
        for (var i = 0; i < ingredients.Count; i++)
        {
            var reps = ingredients[i].Replacements;
            if (reps is null || reps.Count == 0)
            {
                continue;
            }

            for (var r = 0; r < reps.Count; r++)
            {
                var rep = reps[r];
                if (string.IsNullOrWhiteSpace(rep.Name))
                {
                    continue;
                }
                replacementKeyMap[(i, r)] = nextKey;
                queryItems.Add(
                    new Stage3SearchQueryItem(nextKey, rep.Name, rep.PrepNote, rep.Unit)
                );
                nextKey++;
            }
        }

        var searchResult = await catalogSearch.SearchAsync(
            config,
            queryItems,
            catalogFoods,
            catalogRecipes,
            cancellationToken
        );

        var foodsById = catalogFoods.ToDictionary(f => f.Id);
        var foodsByNormName = new Dictionary<string, CatalogFoodLookupItem>(
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var f in catalogFoods)
        {
            foodsByNormName.TryAdd(f.NormalizedName, f);
        }
        var recipesById = catalogRecipes.ToDictionary(r => r.Id);

        var rawDecisions = new Dictionary<int, Stage3MatchDecisionDto>();
        var batchPrompt = IngestionPromptLoader.LoadPrompt(IngestionPromptLoader.Stage3MatchBatch);

        foreach (var chunk in queryItems.Chunk(Stage3BatchSize))
        {
            var batchPayload = JsonSerializer.Serialize(
                new
                {
                    items = chunk.Select(q =>
                    {
                        searchResult.CandidatesByKey.TryGetValue(q.Key, out var cand);
                        return new
                        {
                            ingredientIndex = q.Key,
                            name = q.Name,
                            unit = q.Unit,
                            prepNote = q.PrepNote,
                            existingIngredients = (cand?.FoodCandidates ?? []).Select(c => new
                            {
                                id = c.Id.ToString(),
                                name = c.Name,
                                category = c.Category.ToString(),
                                score = c.Score,
                            }),
                            existingRecipes = (cand?.RecipeCandidates ?? []).Select(r => new
                            {
                                id = r.Id.ToString(),
                                title = r.Title,
                                score = r.Score,
                            }),
                        };
                    }),
                }
            );

            var batchRes = await llmClient.ExecuteStructuredStageAsync<Stage3BatchMatchResponseDto>(
                config,
                stageName: "stage3c_match_batch",
                messages: [new("system", batchPrompt), new("user", batchPayload)],
                jsonSchema: MultiStageSchemas.Stage3MatchBatchSchema,
                overrideThinking: false,
                maxTokens: 4096,
                cancellationToken: cancellationToken
            );

            if (batchRes.Parsed?.Matches is { Count: > 0 } matches)
            {
                foreach (var m in matches)
                {
                    rawDecisions[m.IngredientIndex] = new Stage3MatchDecisionDto(
                        m.MatchType,
                        m.Id,
                        m.Name,
                        m.Category
                    );
                }
            }
        }

        // Single-item retry via 03b for any missing or ID-invalid entries
        var singlePrompt = IngestionPromptLoader.LoadPrompt(
            IngestionPromptLoader.Stage3MatchIngredient
        );
        foreach (var q in queryItems)
        {
            var needsSingleRetry =
                !rawDecisions.TryGetValue(q.Key, out var existingDec)
                || !HasValidReferencedIdIfRequired(existingDec, foodsById, recipesById);

            if (!needsSingleRetry)
            {
                continue;
            }

            searchResult.CandidatesByKey.TryGetValue(q.Key, out var cand);
            var singlePayload = JsonSerializer.Serialize(
                new
                {
                    name = q.Name,
                    unit = q.Unit,
                    prepNote = q.PrepNote,
                    existingIngredients = (cand?.FoodCandidates ?? []).Select(c => new
                    {
                        id = c.Id.ToString(),
                        name = c.Name,
                        category = c.Category.ToString(),
                        score = c.Score,
                    }),
                    existingRecipes = (cand?.RecipeCandidates ?? []).Select(r => new
                    {
                        id = r.Id.ToString(),
                        title = r.Title,
                        score = r.Score,
                    }),
                }
            );

            var singleRes = await llmClient.ExecuteStructuredStageAsync<Stage3MatchDecisionDto>(
                config,
                stageName: "stage3b_match_ingredient",
                messages: [new("system", singlePrompt), new("user", singlePayload)],
                jsonSchema: MultiStageSchemas.Stage3MatchIngredientSchema,
                overrideThinking: false,
                maxTokens: 1024,
                cancellationToken: cancellationToken
            );

            if (singleRes.Parsed is not null)
            {
                rawDecisions[q.Key] = singleRes.Parsed;
            }
        }

        var withinRecipeNewFoods = new Dictionary<string, (string Name, FoodCategory Category)>(
            StringComparer.OrdinalIgnoreCase
        );

        var primaryMatches = new Dictionary<int, ReconciledMatch>(ingredients.Count);
        for (var i = 0; i < ingredients.Count; i++)
        {
            rawDecisions.TryGetValue(i, out var dec);
            searchResult.CandidatesByKey.TryGetValue(i, out var cand);
            primaryMatches[i] = ReconcileStage3Decision(
                ingredients[i].Name,
                ingredients[i].Unit,
                ingredients[i].PrepNote,
                dec,
                cand,
                foodsById,
                foodsByNormName,
                recipesById,
                withinRecipeNewFoods,
                allowRecipeReference: true
            );
        }

        var replacementMatches = new Dictionary<(int IngIndex, int RepIndex), ReconciledMatch>(
            replacementKeyMap.Count
        );
        foreach (var ((ingIdx, repIdx), key) in replacementKeyMap)
        {
            var rep = ingredients[ingIdx].Replacements![repIdx];
            rawDecisions.TryGetValue(key, out var dec);
            searchResult.CandidatesByKey.TryGetValue(key, out var cand);
            replacementMatches[(ingIdx, repIdx)] = ReconcileStage3Decision(
                rep.Name,
                rep.Unit,
                rep.PrepNote,
                dec,
                cand,
                foodsById,
                foodsByNormName,
                recipesById,
                withinRecipeNewFoods,
                allowRecipeReference: false
            );
        }

        return (primaryMatches, replacementMatches);
    }

    private async Task<List<ValidatedStage4StepResult>> RunStage4Async(
        EffectiveLlmConnectionConfig config,
        List<WorkIngredient> ingredients,
        List<WorkStep> steps,
        CancellationToken cancellationToken
    )
    {
        if (steps.Count == 0)
        {
            return [];
        }

        var masterIngredientsPayload = ingredients
            .Select(ing => new
            {
                index = ing.Index,
                section = ing.SectionName,
                name = ing.Name,
                prepNote = ing.PrepNote,
            })
            .ToList();

        var resultsByStepNum = new Dictionary<int, ValidatedStage4StepResult>(steps.Count);
        var batchPrompt = IngestionPromptLoader.LoadPrompt(
            IngestionPromptLoader.Stage4TokenizeStepsBatch
        );

        foreach (var chunk in steps.Chunk(Stage4BatchSize))
        {
            var payloadJson = JsonSerializer.Serialize(
                new
                {
                    ingredients = masterIngredientsPayload,
                    steps = chunk.Select(s => new
                    {
                        stepNumber = s.StepNumber,
                        sectionName = s.SectionName,
                        instruction = s.Instruction,
                    }),
                }
            );

            var batchRes = await llmClient.ExecuteStructuredStageAsync<Stage4BatchResponseDto>(
                config,
                stageName: "stage4b_tokenize_batch",
                messages: [new("system", batchPrompt), new("user", payloadJson)],
                jsonSchema: MultiStageSchemas.Stage4BatchStepsSchema,
                overrideThinking: null,
                maxTokens: 16384,
                cancellationToken: cancellationToken
            );

            if (batchRes.Parsed?.Steps is { Count: > 0 } batchSteps)
            {
                var chunkStepMap = chunk.ToDictionary(s => s.StepNumber);
                foreach (var bs in batchSteps)
                {
                    if (!chunkStepMap.TryGetValue(bs.StepNumber, out var srcStep))
                    {
                        continue;
                    }

                    var tokensArray = bs.Tokens ?? [];
                    resultsByStepNum[bs.StepNumber] = StepTokenValidator.BuildAndValidateStep(
                        srcStep.StepNumber,
                        srcStep.SectionName,
                        srcStep.Instruction,
                        tokensArray,
                        ingredients.Count
                    );
                }
            }
        }

        var singlePrompt = IngestionPromptLoader.LoadPrompt(
            IngestionPromptLoader.Stage4TokenizeStep
        );
        var finalSteps = new List<ValidatedStage4StepResult>(steps.Count);

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            resultsByStepNum.TryGetValue(step.StepNumber, out var bestResult);

            var needsRepair =
                bestResult is null
                || bestResult.ParsedTokens.Count == 0
                || bestResult.ValidationWarnings.Count > 0;

            if (needsRepair)
            {
                var priorSteps = steps
                    .Take(i)
                    .Select(s => new { stepNumber = s.StepNumber, instruction = s.Instruction })
                    .ToList();

                var initialUserPayload = JsonSerializer.Serialize(
                    new
                    {
                        instruction = step.Instruction,
                        ingredients = masterIngredientsPayload,
                        priorSteps,
                    }
                );

                var messages = new List<LlmStageChatMessage>
                {
                    new("system", singlePrompt),
                    new("user", initialUserPayload),
                };

                for (var attempt = 0; attempt <= MaxStage4SingleStepRepairTurns; attempt++)
                {
                    var singleRes = await llmClient.ExecuteStructuredStageAsync<JsonObject>(
                        config,
                        stageName: "stage4_tokenize_step",
                        messages: messages,
                        jsonSchema: MultiStageSchemas.Stage4TokenizeStepSchema,
                        overrideThinking: null,
                        maxTokens: 4096,
                        cancellationToken: cancellationToken
                    );

                    if (
                        singleRes.Parsed is not null
                        && singleRes.Parsed.TryGetPropertyValue("tokens", out var tokNode)
                        && tokNode is JsonArray rawTokens
                    )
                    {
                        var candidate = StepTokenValidator.BuildAndValidateStep(
                            step.StepNumber,
                            step.SectionName,
                            step.Instruction,
                            rawTokens,
                            ingredients.Count
                        );

                        if (
                            bestResult is null
                            || bestResult.ParsedTokens.Count == 0
                            || (
                                candidate.ParsedTokens.Count > 0
                                && candidate.ValidationWarnings.Count
                                    < bestResult.ValidationWarnings.Count
                            )
                        )
                        {
                            bestResult = candidate;
                        }

                        if (
                            candidate.ParsedTokens.Count > 0
                            && candidate.ValidationWarnings.Count == 0
                        )
                        {
                            break;
                        }

                        if (attempt < MaxStage4SingleStepRepairTurns)
                        {
                            messages.Add(
                                new(
                                    "assistant",
                                    string.IsNullOrWhiteSpace(singleRes.RawContent)
                                        ? singleRes.Parsed.ToJsonString()
                                        : singleRes.RawContent
                                )
                            );
                            var warningFeedback = string.Join("\n- ", candidate.ValidationWarnings);
                            messages.Add(
                                new(
                                    "user",
                                    $"The tokenized step had validation issues:\n- {warningFeedback}\n\nPlease fix these issues while keeping all narrative prose verbatim and returning complete valid JSON matching the schema."
                                )
                            );
                        }
                    }
                    else if (attempt < MaxStage4SingleStepRepairTurns)
                    {
                        var err = singleRes.Error ?? "Response did not include a 'tokens' array.";
                        messages.Add(
                            new(
                                "assistant",
                                string.IsNullOrWhiteSpace(singleRes.RawContent)
                                    ? "{}"
                                    : singleRes.RawContent
                            )
                        );
                        messages.Add(
                            new(
                                "user",
                                $"Invalid JSON response ({err}). Please return only valid JSON with a 'tokens' array."
                            )
                        );
                    }
                }
            }

            if (bestResult is null || bestResult.ParsedTokens.Count == 0)
            {
                bestResult = new ValidatedStage4StepResult(
                    StepNumber: step.StepNumber,
                    SectionName: step.SectionName,
                    OriginalInstruction: step.Instruction,
                    SanitizedTokensJson: [step.Instruction],
                    ParsedTokens: [new Stage4TextToken(step.Instruction)],
                    CompiledInstruction: step.Instruction,
                    Timers: [],
                    ValidationWarnings:
                    [
                        "Step could not be tokenized; preserved verbatim plain text.",
                    ]
                );
            }

            finalSteps.Add(bestResult);
        }

        return finalSteps;
    }

    private async Task<Stage5TargetedPolishResponseDto?> RunStage5PolishAsync(
        EffectiveLlmConnectionConfig polishConfig,
        string sourceText,
        string title,
        string? description,
        decimal? servings,
        string? yieldDisplay,
        Stage1TimingDto? timing,
        IReadOnlyList<string> rawTags,
        List<WorkIngredient> ingredients,
        List<ValidatedStage4StepResult> steps,
        List<(int Index, string Name, string Reason)> incompatibleAlts,
        List<ValidatedStage4StepResult> stepWarnings,
        bool missingMetadata,
        CancellationToken cancellationToken
    )
    {
        var polishPrompt = IngestionPromptLoader.LoadPrompt(
            IngestionPromptLoader.Stage5PolishTargeted
        );
        var payloadJson = JsonSerializer.Serialize(
            new
            {
                originalSourceText = sourceText,
                currentRecipe = new
                {
                    title,
                    description,
                    servings,
                    yield = yieldDisplay,
                    timing,
                    tags = rawTags,
                    ingredients = ingredients.Select(i => new
                    {
                        index = i.Index,
                        section = i.SectionName,
                        name = i.Name,
                        quantity = i.Quantity,
                        maxQuantity = i.MaxQuantity,
                        unit = i.Unit,
                        prepNote = i.PrepNote,
                        isOptional = i.IsOptional,
                        replacements = i.Replacements,
                        alternateMeasurements = i.AlternateMeasurements,
                    }),
                    steps = steps.Select(s => new
                    {
                        stepNumber = s.StepNumber,
                        sectionName = s.SectionName,
                        originalInstruction = s.OriginalInstruction,
                        renderedOriginal = StepTokenValidator.RenderForValidation(s.ParsedTokens),
                        validationWarnings = s.ValidationWarnings,
                        tokens = s.SanitizedTokensJson,
                    }),
                },
                flaggedIssues = new
                {
                    incompatibleAlternateMeasurements = incompatibleAlts.Select(a => new
                    {
                        ingredientIndex = a.Index,
                        ingredientName = a.Name,
                        reason = a.Reason,
                    }),
                    stepValidationIssues = stepWarnings.Select(s => new
                    {
                        stepNumber = s.StepNumber,
                        warnings = s.ValidationWarnings,
                    }),
                    missingMetadata,
                },
            }
        );

        var res = await llmClient.ExecuteStructuredStageAsync<Stage5TargetedPolishResponseDto>(
            polishConfig,
            stageName: "stage5b_polish_targeted",
            messages: [new("system", polishPrompt), new("user", payloadJson)],
            jsonSchema: MultiStageSchemas.Stage5PolishTargetedSchema,
            overrideThinking: null,
            maxTokens: 16384,
            cancellationToken: cancellationToken
        );

        return res.Parsed;
    }

    internal static bool QuotaExhaustionAffectsPrimaryTextWorkflow(
        LlmHostUnavailableException ex,
        EffectiveLlmConnectionConfig primaryTextConfig
    )
    {
        if (ex.ExhaustedQuotaScope is null || string.IsNullOrWhiteSpace(ex.ExhaustedGateKey))
        {
            return true;
        }

        return ex.ExhaustedQuotaScope switch
        {
            LlmQuotaScope.Connection => string.Equals(
                ex.ExhaustedGateKey,
                primaryTextConfig.ConnectionKey,
                StringComparison.Ordinal
            ),
            LlmQuotaScope.Model => string.Equals(
                ex.ExhaustedGateKey,
                primaryTextConfig.ModelKey,
                StringComparison.Ordinal
            ),
            _ => true,
        };
    }

    private static IngestionExtractedDraftDto CompileDraft(
        IngestionPipelineRequest request,
        string title,
        string? description,
        decimal? servings,
        string? yieldDisplay,
        Stage1TimingDto? timing,
        IReadOnlyList<string> rawTags,
        List<WorkIngredient> workIngredients,
        Dictionary<int, ReconciledMatch> primaryMatches,
        Dictionary<(int IngIndex, int RepIndex), ReconciledMatch> replacementMatches,
        List<ValidatedStage4StepResult> validatedSteps,
        List<(int Index, string Name, string Reason)> incompatibleAlts,
        IReadOnlyList<CatalogTagLookupItem> catalogTags
    )
    {
        var incompatibleSet = incompatibleAlts.Select(x => x.Index).ToHashSet();
        var measurementWarnings = new List<string>();
        var qualityWarnings = new List<string>();
        var validationIssues = new List<string>();

        var compiledIngredients = new List<IngestionDraftIngredientDto>(workIngredients.Count);

        for (var i = 0; i < workIngredients.Count; i++)
        {
            var ing = workIngredients[i];
            var match = primaryMatches[i];
            var (cleanQty, cleanMaxQty) = RecipeUnitAndSectionHelpers.NormalizeQuantities(
                ing.Quantity,
                ing.MaxQuantity
            );
            var (cleanUnit, cleanCustomUnit) = RecipeUnitAndSectionHelpers.NormalizeUnit(ing.Unit);
            var foodOrIngName = match.MatchedFoodItemName ?? match.ProposedFoodItemName;
            if (
                cleanUnit == MeasurementUnit.Custom
                && (
                    string.IsNullOrWhiteSpace(cleanCustomUnit)
                    || RecipeUnitAndSectionHelpers.IsDisallowedUnitPlaceholder(cleanCustomUnit)
                    || RecipeUnitAndSectionHelpers.IsRedundantSelfCustomUnit(
                        cleanCustomUnit,
                        ing.Name
                    )
                    || RecipeUnitAndSectionHelpers.IsRedundantSelfCustomUnit(
                        cleanCustomUnit,
                        foodOrIngName
                    )
                )
            )
            {
                cleanUnit = null;
                cleanCustomUnit = null;
            }
            if (
                (cleanUnit is MeasurementUnit.Pinch or MeasurementUnit.Dash)
                && (cleanQty is null or <= 0.1m)
            )
            {
                cleanQty = 1m;
            }

            RecipeMeasurement? altMeasurement = null;
            if (
                !incompatibleSet.Contains(i)
                && cleanQty is > 0m
                && ing.AlternateMeasurements is { Count: > 0 }
            )
            {
                var firstAlt = ing.AlternateMeasurements[0];
                if (firstAlt.Quantity is > 0m)
                {
                    var (altUnit, altCustom) = RecipeUnitAndSectionHelpers.NormalizeUnit(
                        firstAlt.Unit
                    );
                    if (
                        altUnit is not null
                        && (
                            altUnit != MeasurementUnit.Custom
                            || !string.IsNullOrWhiteSpace(altCustom)
                        )
                    )
                    {
                        altMeasurement = new RecipeMeasurement(
                            Quantity: decimal.Round(firstAlt.Quantity.Value, 4),
                            Unit: altUnit.Value,
                            MaxQuantity: null,
                            CustomUnitName: altUnit == MeasurementUnit.Custom
                                ? IngestionMetadataAndTagHelper.CleanOptionalText(altCustom, 50)
                                : null
                        );
                        if (cleanUnit is null)
                        {
                            cleanUnit = MeasurementUnit.Whole;
                        }
                    }
                }
            }
            else if (incompatibleSet.Contains(i))
            {
                var inc = incompatibleAlts.First(x => x.Index == i);
                measurementWarnings.Add($"Ingredient #{i + 1} ('{ing.Name}'): {inc.Reason}");
            }

            var compiledReplacements = new List<IngestionDraftReplacementDto>();
            if (!match.IsRecipeReference && ing.Replacements is { Count: > 0 })
            {
                for (var r = 0; r < ing.Replacements.Count; r++)
                {
                    var rep = ing.Replacements[r];
                    if (
                        string.IsNullOrWhiteSpace(rep.Name)
                        || string.Equals(
                            rep.Unit?.Trim(),
                            "recipe",
                            StringComparison.OrdinalIgnoreCase
                        )
                        || string.Equals(
                            rep.Unit?.Trim(),
                            "batch",
                            StringComparison.OrdinalIgnoreCase
                        )
                        || (
                            !string.IsNullOrWhiteSpace(rep.PrepNote)
                            && RecipeCueRegex().IsMatch(rep.PrepNote)
                        )
                    )
                    {
                        continue;
                    }

                    replacementMatches.TryGetValue((i, r), out var repMatch);
                    var (repQty, repMaxQty) = RecipeUnitAndSectionHelpers.NormalizeQuantities(
                        rep.Quantity,
                        rep.MaxQuantity
                    );
                    var (repUnit, repCustom) = RecipeUnitAndSectionHelpers.NormalizeUnit(rep.Unit);
                    var repFoodOrName =
                        repMatch?.MatchedFoodItemName ?? repMatch?.ProposedFoodItemName;
                    if (
                        repUnit == MeasurementUnit.Custom
                        && (
                            string.IsNullOrWhiteSpace(repCustom)
                            || RecipeUnitAndSectionHelpers.IsDisallowedUnitPlaceholder(repCustom)
                            || RecipeUnitAndSectionHelpers.IsRedundantSelfCustomUnit(
                                repCustom,
                                rep.Name
                            )
                            || RecipeUnitAndSectionHelpers.IsRedundantSelfCustomUnit(
                                repCustom,
                                repFoodOrName
                            )
                        )
                    )
                    {
                        repUnit = null;
                        repCustom = null;
                    }

                    var repDisplay = IngestionMetadataAndTagHelper.Truncate(
                        RecipeUnitAndSectionHelpers.StripLeadingBulletOrIndex(rep.Name.Trim()),
                        200
                    );
                    var repFoodName =
                        repMatch?.ProposedFoodItemName
                        ?? repMatch?.MatchedFoodItemName
                        ?? repDisplay;

                    compiledReplacements.Add(
                        new IngestionDraftReplacementDto(
                            DisplayName: repDisplay,
                            Quantity: repQty,
                            MaxQuantity: repMaxQty,
                            Unit: repUnit,
                            CustomUnitName: repUnit == MeasurementUnit.Custom ? repCustom : null,
                            PreparationNote: IngestionMetadataAndTagHelper.CleanOptionalText(
                                rep.PrepNote,
                                300
                            ),
                            MatchedFoodItemId: repMatch?.MatchedFoodItemId,
                            MatchedFoodItemName: repMatch?.MatchedFoodItemName,
                            IsNewFoodItem: repMatch?.IsNewFoodItem ?? true,
                            CreateNewFoodItem: repMatch?.CreateNewFoodItem ?? true,
                            ProposedFoodItemName: IngestionMetadataAndTagHelper.CleanOptionalText(
                                repFoodName,
                                150
                            ),
                            SuggestedCategory: repMatch?.SuggestedCategory
                                ?? FoodCategoryClassifier.Classify(repDisplay),
                            SuggestedIsPantryStaple: repMatch?.SuggestedIsPantryStaple ?? false
                        )
                    );
                }
            }

            var displayName = match.IsRecipeReference
                ? (match.ReferencedRecipeTitle ?? ing.Name.Trim())
                : IngestionMetadataAndTagHelper.Truncate(
                    RecipeUnitAndSectionHelpers.StripLeadingBulletOrIndex(ing.Name.Trim()),
                    200
                );

            var rawLine = BuildOriginalRawLine(
                cleanQty,
                cleanMaxQty,
                ing.Unit,
                displayName,
                ing.PrepNote,
                ing.IsOptional
            );

            compiledIngredients.Add(
                new IngestionDraftIngredientDto(
                    SectionName: IngestionMetadataAndTagHelper.CleanOptionalText(
                        ing.SectionName,
                        150
                    ),
                    Quantity: cleanQty,
                    MaxQuantity: cleanMaxQty,
                    Unit: cleanUnit,
                    CustomUnitName: cleanUnit == MeasurementUnit.Custom ? cleanCustomUnit : null,
                    MatchedFoodItemId: match.MatchedFoodItemId,
                    MatchedFoodItemName: match.MatchedFoodItemName,
                    IsNewFoodItem: match.IsNewFoodItem,
                    ProposedFoodItemName: IngestionMetadataAndTagHelper.CleanOptionalText(
                        match.ProposedFoodItemName,
                        150
                    ),
                    SuggestedIsPantryStaple: match.SuggestedIsPantryStaple,
                    CreateNewFoodItem: match.CreateNewFoodItem,
                    DisplayName: displayName,
                    PreparationNote: IngestionMetadataAndTagHelper.CleanOptionalText(
                        ing.PrepNote,
                        200
                    ),
                    IsOptional: ing.IsOptional,
                    OriginalRawText: IngestionMetadataAndTagHelper.CleanOptionalText(rawLine, 500),
                    AlternativeMeasurement: altMeasurement,
                    PreferAlternativeMeasurement: false,
                    SuggestedCategory: match.SuggestedCategory,
                    IsRecipeReference: match.IsRecipeReference,
                    ReferencedRecipeTitle: IngestionMetadataAndTagHelper.CleanOptionalText(
                        match.ReferencedRecipeTitle,
                        200
                    ),
                    MatchedRecipeId: match.MatchedRecipeId,
                    MatchedRecipeTitle: IngestionMetadataAndTagHelper.CleanOptionalText(
                        match.MatchedRecipeTitle,
                        200
                    ),
                    MatchedRecipeSlug: null,
                    Replacements: compiledReplacements.Count > 0 ? compiledReplacements : null
                )
            );
        }

        // Compile steps & step diagnostics
        var compiledSteps = new List<IngestionDraftStepDto>(validatedSteps.Count);
        foreach (var step in validatedSteps)
        {
            foreach (var warning in step.ValidationWarnings)
            {
                qualityWarnings.Add($"Step {step.StepNumber}: {warning}");
            }

            compiledSteps.Add(
                new IngestionDraftStepDto(
                    SectionName: IngestionMetadataAndTagHelper.CleanOptionalText(
                        step.SectionName,
                        150
                    ),
                    Instruction: IngestionMetadataAndTagHelper.Truncate(
                        step.CompiledInstruction,
                        4000
                    ),
                    Timers: step.Timers,
                    ImagePath: null
                )
            );
        }

        var prepMinutes = IngestionMetadataAndTagHelper.ClampMinutes(timing?.Prep);
        var cookMinutes = IngestionMetadataAndTagHelper.ClampMinutes(timing?.Cook);
        var totalMinutes =
            IngestionMetadataAndTagHelper.ClampMinutes(timing?.Total)
            ?? (
                prepMinutes.HasValue && cookMinutes.HasValue
                    ? IngestionMetadataAndTagHelper.ClampMinutes(
                        prepMinutes.Value + cookMinutes.Value
                    )
                    : null
            );

        var baseServings = servings is >= 0.25m and <= 999m ? decimal.Round(servings.Value, 2) : 4m;
        var difficulty = InferDifficulty(
            compiledIngredients.Count,
            compiledSteps.Count,
            totalMinutes
        );

        var (tagIds, proposedTagNames) = IngestionMetadataAndTagHelper.ResolveAndGuardTags(
            title,
            description,
            rawTags,
            catalogTags,
            compiledIngredients,
            compiledSteps,
            cookMinutes,
            totalMinutes
        );

        // Compute unresolved recipe issues
        for (var i = 0; i < compiledIngredients.Count; i++)
        {
            var ing = compiledIngredients[i];
            if (ing.IsRecipeReference && !ing.MatchedRecipeId.HasValue)
            {
                validationIssues.Add(
                    $"Ingredient #{i + 1} ('{ing.DisplayName}') references sub-recipe '{ing.ReferencedRecipeTitle ?? ing.DisplayName}' which does not exist in the catalog yet."
                );
            }
        }

        if (compiledIngredients.Count == 0)
        {
            validationIssues.Add("Recipe has no ingredients.");
        }
        if (compiledSteps.Count == 0)
        {
            validationIssues.Add("Recipe has no preparation steps.");
        }

        var confidence = ComputeConfidenceScore(
            servings,
            prepMinutes,
            cookMinutes,
            validationIssues.Count,
            qualityWarnings.Count,
            measurementWarnings.Count
        );

        return new IngestionExtractedDraftDto(
            Title: title,
            Description: IngestionMetadataAndTagHelper.CleanOptionalText(description, 2000),
            BaseServings: baseServings,
            YieldDisplay: yieldDisplay,
            PrepTimeMinutes: prepMinutes,
            CookTimeMinutes: cookMinutes,
            TotalTimeMinutes: totalMinutes,
            Difficulty: difficulty,
            SourceUrl: IngestionMetadataAndTagHelper.CleanOptionalText(request.SourceUrl, 2048),
            SourceName: IngestionMetadataAndTagHelper.CleanOptionalText(request.SourceName, 200),
            SourceAuthor: IngestionMetadataAndTagHelper.CleanOptionalText(
                request.SourceAuthor,
                200
            ),
            CoverImagePath: IngestionMetadataAndTagHelper.CleanOptionalText(
                request.CoverImagePath,
                500
            ),
            ThumbnailImagePath: IngestionMetadataAndTagHelper.CleanOptionalText(
                request.ThumbnailImagePath,
                500
            ),
            SuggestedCollectionIds: request.SuggestedCollectionIds ?? [],
            SuggestedTagIds: tagIds,
            Ingredients: compiledIngredients,
            Steps: compiledSteps,
            ConfidenceScore: confidence,
            SourceBookTitle: IngestionMetadataAndTagHelper.CleanOptionalText(
                request.SourceBookTitle,
                200
            ),
            SourceBookPage: request.SourceBookPage is >= 1 and <= 10000
                ? request.SourceBookPage
                : null,
            RetainSourceAttachments: request.RetainSourceAttachments,
            ExtractedModel: IngestionMetadataAndTagHelper.CleanOptionalText(
                request.Config.TextModel,
                200
            ),
            ProposedTagNames: proposedTagNames,
            MeasurementWarnings: measurementWarnings.Count > 0 ? measurementWarnings : null,
            QualityWarnings: qualityWarnings.Count > 0 ? qualityWarnings : null,
            ValidationIssues: validationIssues.Count > 0 ? validationIssues : null
        );
    }

    private static bool HasValidReferencedIdIfRequired(
        Stage3MatchDecisionDto decision,
        Dictionary<Guid, CatalogFoodLookupItem> foodsById,
        Dictionary<Guid, CatalogRecipeLookupItem> recipesById
    )
    {
        var type = decision.MatchType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (type is "existing_ingredient" or "existing_food")
        {
            return Guid.TryParse(decision.Id, out var id) && foodsById.ContainsKey(id);
        }
        if (type is "existing_recipe")
        {
            return Guid.TryParse(decision.Id, out var id) && recipesById.ContainsKey(id);
        }
        return type is "new_ingredient" or "new_food" or "unresolved_recipe";
    }

    private static ReconciledMatch ReconcileStage3Decision(
        string rawName,
        string? rawUnit,
        string? rawPrepNote,
        Stage3MatchDecisionDto? decision,
        Stage3ItemCandidatesDto? candidates,
        Dictionary<Guid, CatalogFoodLookupItem> foodsById,
        Dictionary<string, CatalogFoodLookupItem> foodsByNormName,
        Dictionary<Guid, CatalogRecipeLookupItem> recipesById,
        Dictionary<string, (string Name, FoodCategory Category)> withinRecipeNewFoods,
        bool allowRecipeReference
    )
    {
        var matchType = decision?.MatchType?.Trim().ToLowerInvariant() ?? "new_ingredient";
        var rejectedGenericMatch = false;

        if (matchType is "existing_ingredient" or "existing_food")
        {
            CatalogFoodLookupItem? food = null;
            if (Guid.TryParse(decision?.Id, out var fid))
            {
                foodsById.TryGetValue(fid, out food);
            }
            food ??= candidates
                ?.FoodCandidates.Where(c =>
                    string.Equals(
                        c.Name,
                        decision?.Name?.Trim(),
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                .Select(c => foodsById.GetValueOrDefault(c.Id))
                .FirstOrDefault(f => f is not null);

            if (
                food is not null
                && food.Name.Equals("egg", StringComparison.OrdinalIgnoreCase)
                && EggPartWordRegex().IsMatch(rawName)
            )
            {
                food = null;
                rejectedGenericMatch = true;
            }

            if (food is not null)
            {
                return new ReconciledMatch(
                    IsRecipeReference: false,
                    MatchedFoodItemId: food.Id,
                    MatchedFoodItemName: food.Name,
                    IsNewFoodItem: false,
                    CreateNewFoodItem: false,
                    ProposedFoodItemName: null,
                    SuggestedCategory: food.Category,
                    SuggestedIsPantryStaple: food.IsPantryStaple,
                    ReferencedRecipeTitle: null,
                    MatchedRecipeId: null,
                    MatchedRecipeTitle: null
                );
            }
        }

        if (allowRecipeReference && matchType is "existing_recipe")
        {
            CatalogRecipeLookupItem? recipe = null;
            if (Guid.TryParse(decision?.Id, out var rid))
            {
                recipesById.TryGetValue(rid, out recipe);
            }
            recipe ??= candidates
                ?.RecipeCandidates.Where(r =>
                    string.Equals(
                        r.Title,
                        decision?.Name?.Trim(),
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                .Select(r => recipesById.GetValueOrDefault(r.Id))
                .FirstOrDefault(r => r is not null);

            var combinedLine = $"{rawName} {rawPrepNote}".Trim();
            if (
                recipe is not null
                && !IngestionMetadataAndTagHelper.IsRawGrocerySeePageNote(combinedLine)
            )
            {
                return new ReconciledMatch(
                    IsRecipeReference: true,
                    MatchedFoodItemId: null,
                    MatchedFoodItemName: null,
                    IsNewFoodItem: false,
                    CreateNewFoodItem: false,
                    ProposedFoodItemName: null,
                    SuggestedCategory: null,
                    SuggestedIsPantryStaple: false,
                    ReferencedRecipeTitle: recipe.Title,
                    MatchedRecipeId: recipe.Id,
                    MatchedRecipeTitle: recipe.Title
                );
            }
        }

        if (allowRecipeReference && matchType is "unresolved_recipe")
        {
            var combinedLine = $"{rawName} {rawPrepNote}".Trim();
            var hasRecipeUnit =
                string.Equals(rawUnit?.Trim(), "recipe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(rawUnit?.Trim(), "batch", StringComparison.OrdinalIgnoreCase);
            var hasExplicitCue =
                RecipeCueRegex().IsMatch(combinedLine)
                && !IngestionMetadataAndTagHelper.IsRawGrocerySeePageNote(combinedLine);

            if (hasRecipeUnit || hasExplicitCue)
            {
                var cleanRefTitle =
                    RecipeUnitAndSectionHelpers.NormalizeRecipeReferenceTitle(
                        decision?.Name ?? rawName
                    ) ?? (decision?.Name ?? rawName).Trim();

                return new ReconciledMatch(
                    IsRecipeReference: true,
                    MatchedFoodItemId: null,
                    MatchedFoodItemName: null,
                    IsNewFoodItem: false,
                    CreateNewFoodItem: false,
                    ProposedFoodItemName: null,
                    SuggestedCategory: null,
                    SuggestedIsPantryStaple: false,
                    ReferencedRecipeTitle: cleanRefTitle,
                    MatchedRecipeId: null,
                    MatchedRecipeTitle: null
                );
            }
        }

        // New grocery ingredient (or safety exact-normalized match when decision was missing/invalid)
        var candidateName =
            !rejectedGenericMatch && !string.IsNullOrWhiteSpace(decision?.Name)
                ? decision!.Name.Trim()
                : rawName.Trim();
        var canonical = FoodNameSanitizer.NormalizeCanonicalFoodName(candidateName);
        if (string.IsNullOrWhiteSpace(canonical))
        {
            canonical = RecipeUnitAndSectionHelpers.StripLeadingBulletOrIndex(candidateName);
        }
        if (string.IsNullOrWhiteSpace(canonical))
        {
            canonical = "ingredient";
        }

        if (
            canonical.Equals("pepper", StringComparison.OrdinalIgnoreCase)
            && IsBareSpicePepper(rawName, rawPrepNote, rawUnit)
        )
        {
            canonical = "black pepper";
        }

        if (foodsByNormName.TryGetValue(canonical, out var exactNormalizedFood))
        {
            return new ReconciledMatch(
                IsRecipeReference: false,
                MatchedFoodItemId: exactNormalizedFood.Id,
                MatchedFoodItemName: exactNormalizedFood.Name,
                IsNewFoodItem: false,
                CreateNewFoodItem: false,
                ProposedFoodItemName: null,
                SuggestedCategory: exactNormalizedFood.Category,
                SuggestedIsPantryStaple: exactNormalizedFood.IsPantryStaple,
                ReferencedRecipeTitle: null,
                MatchedRecipeId: null,
                MatchedRecipeTitle: null
            );
        }

        var category =
            FoodCategories.Parse(decision?.Category) ?? FoodCategoryClassifier.Classify(canonical);

        // Ensure identical new ingredients across divided sections share one canonical name & category
        if (withinRecipeNewFoods.TryGetValue(canonical, out var existingNew))
        {
            canonical = existingNew.Name;
            category = existingNew.Category;
        }
        else
        {
            withinRecipeNewFoods[canonical] = (canonical, category);
        }

        var isPantry = FoodCategoryClassifier.IsLikelyPantryStaple(canonical, category);
        return new ReconciledMatch(
            IsRecipeReference: false,
            MatchedFoodItemId: null,
            MatchedFoodItemName: null,
            IsNewFoodItem: true,
            CreateNewFoodItem: true,
            ProposedFoodItemName: canonical,
            SuggestedCategory: category,
            SuggestedIsPantryStaple: isPantry,
            ReferencedRecipeTitle: null,
            MatchedRecipeId: null,
            MatchedRecipeTitle: null
        );
    }

    private static List<WorkIngredient> FlattenIngredients(
        Stage1RecipeDto stage1,
        string sourceText
    )
    {
        var list = new List<WorkIngredient>();
        var idx = 0;
        foreach (var section in stage1.Sections ?? [])
        {
            var secName = NormalizeSection(section.Name);
            foreach (var ing in section.Ingredients ?? [])
            {
                var ingName = ing.Name?.Trim() ?? string.Empty;
                var ingQty = ing.Quantity;
                var ingMaxQty = ing.MaxQuantity;
                var ingUnit = ing.Unit;
                var (uKind, uCustom) = RecipeUnitAndSectionHelpers.NormalizeUnit(ingUnit);
                if (
                    uKind == MeasurementUnit.Custom
                    && RecipeUnitAndSectionHelpers.IsRedundantSelfCustomUnit(uCustom, ingName)
                )
                {
                    ingUnit = null;
                }

                if (
                    ingQty.HasValue
                    && ingMaxQty.HasValue
                    && ingMaxQty.Value > ingQty.Value
                    && !StepTokenValidator.HasPrintedNumericRangeInSource(
                        sourceText,
                        ingQty.Value,
                        ingMaxQty.Value
                    )
                )
                {
                    ingMaxQty = null;
                }

                if (
                    (
                        string.Equals(ingUnit?.Trim(), "recipe", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(
                            ingUnit?.Trim(),
                            "batch",
                            StringComparison.OrdinalIgnoreCase
                        )
                    ) && !HasSourceRecipeCueForIngredient(sourceText, ingName)
                )
                {
                    ingUnit = null;
                }

                if (
                    ingQty == 1m
                    && ingMaxQty is null
                    && string.IsNullOrWhiteSpace(ingUnit)
                    && (ing.AlternateMeasurements is null || ing.AlternateMeasurements.Count == 0)
                    && UnquantifiedSeasoningOrSprayRegex().IsMatch(ingName)
                )
                {
                    ingQty = null;
                }

                if (
                    ingQty is null
                    && ingMaxQty is null
                    && string.IsNullOrWhiteSpace(ingUnit)
                    && (ing.AlternateMeasurements is null || ing.AlternateMeasurements.Count == 0)
                    && SaltAndPepperSplitRegex().Match(ingName) is { Success: true } spMatch
                )
                {
                    list.Add(
                        new WorkIngredient
                        {
                            Index = idx++,
                            SectionName = secName,
                            Name = spMatch.Groups["salt"].Value.Trim(),
                            Quantity = null,
                            MaxQuantity = null,
                            Unit = null,
                            PrepNote = string.Equals(
                                ing.PrepNote?.Trim(),
                                "to taste",
                                StringComparison.OrdinalIgnoreCase
                            )
                                ? ing.PrepNote
                                : null,
                            IsOptional = ing.IsOptional,
                            Replacements = null,
                            AlternateMeasurements = null,
                        }
                    );
                    list.Add(
                        new WorkIngredient
                        {
                            Index = idx++,
                            SectionName = secName,
                            Name = spMatch.Groups["pep"].Value.Trim(),
                            Quantity = null,
                            MaxQuantity = null,
                            Unit = null,
                            PrepNote = ing.PrepNote,
                            IsOptional = ing.IsOptional,
                            Replacements = null,
                            AlternateMeasurements = null,
                        }
                    );
                    continue;
                }

                list.Add(
                    new WorkIngredient
                    {
                        Index = idx++,
                        SectionName = secName,
                        Name = ingName,
                        Quantity = ingQty,
                        MaxQuantity = ingMaxQty,
                        Unit = ingUnit,
                        PrepNote = ing.PrepNote,
                        IsOptional = ing.IsOptional,
                        Replacements = ing.Replacements,
                        AlternateMeasurements = ing.AlternateMeasurements,
                    }
                );
            }
        }
        return list;
    }

    private static List<WorkStep> FlattenSteps(Stage1RecipeDto stage1)
    {
        var list = new List<WorkStep>();
        var num = 1;
        foreach (var section in stage1.Sections ?? [])
        {
            var currentSecName = NormalizeSection(section.Name);
            foreach (var instruction in section.Instructions ?? [])
            {
                if (string.IsNullOrWhiteSpace(instruction))
                {
                    continue;
                }

                var cleanInstruction = LeadStepPrefixRegex()
                    .Replace(instruction.Trim(), string.Empty)
                    .Trim();
                if (cleanInstruction.EndsWith(".).", StringComparison.Ordinal))
                {
                    cleanInstruction = cleanInstruction[..^3] + ".)";
                }

                var stepSecName = currentSecName;
                var forTheMatch = LeadForTheSectionRegex().Match(cleanInstruction);
                if (forTheMatch.Success)
                {
                    cleanInstruction = cleanInstruction[forTheMatch.Length..].TrimStart();
                    if (stepSecName is null)
                    {
                        var parsedSec = NormalizeSection(forTheMatch.Groups["hdr"].Value);
                        if (!string.IsNullOrWhiteSpace(parsedSec))
                        {
                            currentSecName = parsedSec;
                            stepSecName = parsedSec;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(cleanInstruction))
                {
                    continue;
                }

                list.Add(new WorkStep(num++, stepSecName, cleanInstruction));
            }
        }
        return list;
    }

    private static string? NormalizeSection(string? rawSection)
    {
        if (string.IsNullOrWhiteSpace(rawSection))
        {
            return null;
        }

        var trimmed = rawSection.Trim().Trim('[', ']').Trim();
        if (
            string.IsNullOrWhiteSpace(trimmed)
            || RecipeUnitAndSectionHelpers.IsGenericTopLevelSection(trimmed)
        )
        {
            return null;
        }
        return trimmed.Length > 150 ? trimmed[..150] : trimmed;
    }

    private static List<(int Index, string Name, string Reason)> FindAllIncompatibleAlts(
        List<WorkIngredient> ingredients
    )
    {
        var result = new List<(int, string, string)>();
        for (var i = 0; i < ingredients.Count; i++)
        {
            var ing = ingredients[i];
            if (
                StepTokenValidator.TryFindIncompatibleAlternateMeasurement(
                    ing.Quantity,
                    ing.MaxQuantity,
                    ing.Unit,
                    ing.AlternateMeasurements,
                    out var reason
                ) && !string.IsNullOrWhiteSpace(reason)
            )
            {
                result.Add((i, ing.Name, reason));
            }
        }
        return result;
    }

    private static bool ShouldCondenseDescription(string description)
    {
        if (description.Length > DescriptionMaxLength)
        {
            return true;
        }

        var sentenceCount = SentenceEndRegex().Matches(description).Count;
        return sentenceCount > 2;
    }

    private static RecipeDifficulty? InferDifficulty(
        int ingredientCount,
        int stepCount,
        int? totalMin
    )
    {
        if (totalMin is > 90 || stepCount >= 10 || ingredientCount >= 16)
        {
            return RecipeDifficulty.Project;
        }
        if (stepCount <= 4 && ingredientCount <= 8 && (totalMin is null or <= 40))
        {
            return RecipeDifficulty.Easy;
        }
        return RecipeDifficulty.Medium;
    }

    private static decimal ComputeConfidenceScore(
        decimal? servings,
        int? prepMin,
        int? cookMin,
        int validationIssueCount,
        int qualityWarningCount,
        int measurementWarningCount
    )
    {
        var score = 0.96m;
        if (servings is null or <= 0m)
        {
            score -= 0.04m;
        }
        if (prepMin is null && cookMin is null)
        {
            score -= 0.04m;
        }
        score -= validationIssueCount * 0.15m;
        score -= qualityWarningCount * 0.04m;
        score -= measurementWarningCount * 0.05m;
        return decimal.Round(Math.Clamp(score, 0.15m, 0.99m), 2);
    }

    private static string BuildOriginalRawLine(
        decimal? qty,
        decimal? maxQty,
        string? rawUnit,
        string displayName,
        string? prepNote,
        bool isOptional
    )
    {
        var parts = new List<string>();
        if (qty.HasValue)
        {
            parts.Add(
                maxQty.HasValue && maxQty > qty
                    ? $"{qty.Value:0.####} to {maxQty.Value:0.####}"
                    : $"{qty.Value:0.####}"
            );
        }
        if (!string.IsNullOrWhiteSpace(rawUnit))
        {
            parts.Add(rawUnit.Trim());
        }
        parts.Add(displayName);
        if (!string.IsNullOrWhiteSpace(prepNote))
        {
            parts.Add($", {prepNote.Trim()}");
        }
        if (isOptional)
        {
            parts.Add("(optional)");
        }
        return string.Join(" ", parts).Replace(" ,", ",", StringComparison.Ordinal).Trim();
    }

    private static bool IsBareSpicePepper(string name, string? prepNote, string? rawUnit)
    {
        var combined = $"{name} {prepNote}".Trim();
        if (SpicePepperCueRegex().IsMatch(combined))
        {
            return true;
        }
        if (FreshPepperCueRegex().IsMatch(combined))
        {
            return false;
        }

        var (unit, _) = RecipeUnitAndSectionHelpers.NormalizeUnit(rawUnit);
        return unit
                is MeasurementUnit.Tsp
                    or MeasurementUnit.Tbsp
                    or MeasurementUnit.Pinch
                    or MeasurementUnit.Dash
            || unit is null;
    }

    private static async Task ReportProgressAsync(Func<string, Task>? callback, string message)
    {
        if (callback is not null)
        {
            await callback(message);
        }
    }

    private sealed class WorkIngredient
    {
        public int Index { get; init; }
        public string? SectionName { get; set; }
        public string Name { get; set; } = string.Empty;
        public decimal? Quantity { get; set; }
        public decimal? MaxQuantity { get; set; }
        public string? Unit { get; set; }
        public string? PrepNote { get; set; }
        public bool IsOptional { get; set; }
        public IReadOnlyList<Stage1ReplacementDto>? Replacements { get; set; }
        public IReadOnlyList<Stage1AlternateMeasurementDto>? AlternateMeasurements { get; set; }
    }

    private sealed record WorkStep(int StepNumber, string? SectionName, string Instruction);

    private sealed record ReconciledMatch(
        bool IsRecipeReference,
        Guid? MatchedFoodItemId,
        string? MatchedFoodItemName,
        bool IsNewFoodItem,
        bool CreateNewFoodItem,
        string? ProposedFoodItemName,
        FoodCategory? SuggestedCategory,
        bool SuggestedIsPantryStaple,
        string? ReferencedRecipeTitle,
        Guid? MatchedRecipeId,
        string? MatchedRecipeTitle
    );

    private static string? FindMissingSourceInstructionFeedback(
        string normalizedText,
        Stage1RecipeDto dto
    )
    {
        var lines = normalizedText.Replace("\r\n", "\n").Split('\n');
        var inInstructions = false;
        var numberedSteps = new List<(int Num, string Text)>();

        foreach (var raw in lines)
        {
            var trimmed = raw.Trim();
            if (!inInstructions)
            {
                if (InstructionsHeaderRegex().IsMatch(trimmed))
                {
                    inInstructions = true;
                }
                continue;
            }

            if (NonInstructionSectionHeaderRegex().IsMatch(trimmed))
            {
                break;
            }

            var match = NumberedInstructionLineRegex().Match(trimmed);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var num))
            {
                var expectedNext = numberedSteps.Count == 0 ? 1 : numberedSteps[^1].Num + 1;
                if (num == expectedNext)
                {
                    numberedSteps.Add((num, match.Groups[2].Value.Trim()));
                }
            }
        }

        if (numberedSteps.Count == 0)
        {
            return null;
        }

        if (numberedSteps.Count >= 3)
        {
            var extractedLower = StepWordNonAlnumRegex()
                .Replace(
                    string.Join(" ", (dto.Sections ?? []).SelectMany(s => s.Instructions ?? []))
                        .ToLowerInvariant(),
                    " "
                );
            var extractedTokens = extractedLower
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.Ordinal);

            var omitted = new List<string>();
            foreach (var (num, text) in numberedSteps)
            {
                if (text.Length < 30)
                {
                    continue;
                }

                var sigWords = StepWordNonAlnumRegex()
                    .Replace(text.ToLowerInvariant(), " ")
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length >= 5)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (sigWords.Count < 3)
                {
                    continue;
                }

                var hits = sigWords.Count(w => extractedTokens.Contains(w));
                if (hits * 10 < sigWords.Count * 7)
                {
                    var snippet = text.Length > 60 ? text[..60] + "..." : text;
                    omitted.Add($"Step {num} ('{snippet}')");
                }
            }

            if (omitted.Count > 0)
            {
                return $"You omitted source instruction step(s): {string.Join("; ", omitted.Take(3))}. Extract EVERY numbered instruction step 1-to-1 verbatim without skipping any step.";
            }
        }

        var extractedInstructions = (dto.Sections ?? [])
            .SelectMany(s => s.Instructions ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        if (extractedInstructions.Count < numberedSteps.Count)
        {
            return $"Source has {numberedSteps.Count} numbered instruction steps, but you extracted {extractedInstructions.Count}. Map EVERY source numbered step 1-to-1 into sections[].instructions without skipping or merging steps.";
        }

        if (extractedInstructions.Count == numberedSteps.Count)
        {
            for (var k = 0; k < numberedSteps.Count; k++)
            {
                var srcClean = LeadUpperSectionPrefixRegex()
                    .Replace(numberedSteps[k].Text.Trim(), string.Empty)
                    .Trim();
                var extClean = LeadUpperSectionPrefixRegex()
                    .Replace(
                        LeadStepPrefixRegex()
                            .Replace(extractedInstructions[k].Trim(), string.Empty)
                            .Trim(),
                        string.Empty
                    )
                    .Trim();

                if (
                    srcClean.Length >= 80
                    && srcClean.Length - extClean.Length >= 45
                    && extClean.Length * 100 < srcClean.Length * 82
                )
                {
                    var snippet = srcClean.Length > 70 ? srcClean[..70] + "..." : srcClean;
                    return $"Step {numberedSteps[k].Num} omitted sentences from the source ('{snippet}'). Copy EVERY sentence of Step {numberedSteps[k].Num} verbatim from start to finish.";
                }
            }
        }

        return null;
    }

    private static string? ValidateStage1IngredientsFeedback(Stage1RecipeDto dto, string sourceText)
    {
        var issues = new List<string>();
        foreach (var sec in dto.Sections ?? [])
        {
            var seenInSection =
                new HashSet<(string Name, decimal? Qty, decimal? Max, string Unit)>();
            foreach (var ing in sec.Ingredients ?? [])
            {
                var ingName = ing.Name?.Trim() ?? string.Empty;
                var dupKey = (
                    ingName.ToLowerInvariant(),
                    ing.Quantity,
                    ing.MaxQuantity,
                    (ing.Unit ?? string.Empty).Trim().ToLowerInvariant()
                );
                if (!seenInSection.Add(dupKey))
                {
                    issues.Add(
                        $"Duplicate '{ingName}' row (quantity={ing.Quantity} {ing.Unit}) in same section; keep a single-measurement line with multi-part prep as ONE ingredient row."
                    );
                }

                if (
                    ing.MaxQuantity.HasValue
                    && (!ing.Quantity.HasValue || ing.MaxQuantity.Value < ing.Quantity.Value)
                )
                {
                    issues.Add(
                        $"'{ingName}' has invalid range quantity={ing.Quantity}..maxQuantity={ing.MaxQuantity} (put parenthetical weight/stick conversions in alternateMeasurements and leave maxQuantity=null)."
                    );
                }
                else if (
                    ing.Quantity.HasValue
                    && ing.MaxQuantity.HasValue
                    && ing.MaxQuantity.Value > ing.Quantity.Value
                    && !StepTokenValidator.HasPrintedNumericRangeInSource(
                        sourceText,
                        ing.Quantity.Value,
                        ing.MaxQuantity.Value
                    )
                )
                {
                    issues.Add(
                        $"'{ingName}' has fabricated range {ing.Quantity}..{ing.MaxQuantity} {ing.Unit} that does not appear as a range in the source text; NEVER place per-pound counts ('21 to 25 per pound'), pack weights ('1 (5- to 7-ounce) can' or '2 (12- to 14-ounce) legs'), or fraction denominators into maxQuantity — set maxQuantity=null and use alternateMeasurements for pack weights or prepNote for size grades."
                    );
                }

                if (OrAlternativeRegex().IsMatch(ingName))
                {
                    issues.Add(
                        ing.Replacements is { Count: > 0 }
                            ? $"'{ingName}' has replacements populated; strip ' or ...' so name holds ONLY option A."
                            : $"'{ingName}' contains an 'A or B' alternative; put ONLY option A in name and move option B into replacements[]."
                    );
                }

                if (
                    ingName.Equals("juice", StringComparison.OrdinalIgnoreCase)
                    || ingName.Equals("zest", StringComparison.OrdinalIgnoreCase)
                )
                {
                    issues.Add(
                        $"'{ingName}' is missing its source fruit/ingredient qualifier; include the specific name (e.g., 'lime juice', 'lemon juice', 'lemon zest')."
                    );
                }

                if (BareEggPartRegex().IsMatch(ingName))
                {
                    issues.Add(
                        $"'{ingName}' is ambiguous without 'egg'; specify 'egg white' or 'egg yolk' instead of bare '{ingName}'."
                    );
                }

                var (unitKind, customUnitName) = RecipeUnitAndSectionHelpers.NormalizeUnit(
                    ing.Unit
                );
                if (
                    unitKind == MeasurementUnit.Custom
                    && RecipeUnitAndSectionHelpers.IsRedundantSelfCustomUnit(
                        customUnitName,
                        ingName
                    )
                )
                {
                    issues.Add(
                        $"'{ingName}' repeats its own noun in unit='{ing.Unit}'; set unit=null."
                    );
                }

                if (
                    (
                        string.Equals(
                            ing.Unit?.Trim(),
                            "recipe",
                            StringComparison.OrdinalIgnoreCase
                        )
                        || string.Equals(
                            ing.Unit?.Trim(),
                            "batch",
                            StringComparison.OrdinalIgnoreCase
                        )
                    ) && !HasSourceRecipeCueForIngredient(sourceText, ingName)
                )
                {
                    issues.Add(
                        $"'{ingName}' set unit='{ing.Unit}', but the source ingredient line does not reference a sub-recipe; set unit=null for whole items like roasts."
                    );
                }

                if (
                    ing.Quantity == 1m
                    && ing.MaxQuantity is null
                    && string.IsNullOrWhiteSpace(ing.Unit)
                    && (ing.AlternateMeasurements is null || ing.AlternateMeasurements.Count == 0)
                    && UnquantifiedSeasoningOrSprayRegex().IsMatch(ingName)
                )
                {
                    issues.Add(
                        $"'{ingName}' is an unquantified seasoning/condiment line with no printed number; set quantity=null instead of 1."
                    );
                }

                if (
                    ing.AlternateMeasurements?.Any(a =>
                        !string.IsNullOrWhiteSpace(a.Unit)
                        && DimensionOrTempUnits.Contains(a.Unit.Trim())
                    ) == true
                )
                {
                    issues.Add(
                        $"'{ingName}' placed a physical dimension/temperature unit in alternateMeasurements; put cut/width dimensions in prepNote instead."
                    );
                }
                else if (
                    StepTokenValidator.TryFindIncompatibleAlternateMeasurement(
                        ing.Quantity,
                        ing.MaxQuantity,
                        ing.Unit,
                        ing.AlternateMeasurements,
                        out var incompatReason
                    )
                )
                {
                    issues.Add($"'{ingName}': {incompatReason}");
                }
            }
        }

        return issues.Count > 0
            ? $"Fix these ingredient extraction errors cleanly: {string.Join(" | ", issues.Take(4))}"
            : null;
    }

    private static bool HasSourceRecipeCueForIngredient(string sourceText, string ingName)
    {
        var lines = sourceText.Replace("\r\n", "\n").Split('\n');
        var inIngredients = false;
        var foundIngredientsHeader = false;
        var ingLines = new List<string>();
        foreach (var raw in lines)
        {
            var trimmed = raw.Trim();
            if (!inIngredients)
            {
                if (IngredientsHeaderRegex().IsMatch(trimmed))
                {
                    inIngredients = true;
                    foundIngredientsHeader = true;
                }
                continue;
            }

            if (
                InstructionsHeaderRegex().IsMatch(trimmed)
                || NonInstructionSectionHeaderRegex().IsMatch(trimmed)
            )
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                ingLines.Add(trimmed);
            }
        }

        if (!foundIngredientsHeader)
        {
            return RecipeCueRegex().IsMatch(sourceText);
        }

        if (ingLines.Count == 0)
        {
            return false;
        }

        var ingWords = StepWordNonAlnumRegex()
            .Replace(ingName.ToLowerInvariant(), " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (ingWords.Count == 0)
        {
            return ingLines.Any(line =>
                RecipeCueRegex().IsMatch(line) || PageReferenceCueRegex().IsMatch(line)
            );
        }

        return ingLines.Any(line =>
            (RecipeCueRegex().IsMatch(line) || PageReferenceCueRegex().IsMatch(line))
            && ingWords.Any(w => line.Contains(w, StringComparison.OrdinalIgnoreCase))
        );
    }

    private static readonly HashSet<string> DimensionOrTempUnits = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "inch",
        "inches",
        "foot",
        "feet",
        "yard",
        "yards",
        "mm",
        "millimeter",
        "millimeters",
        "cm",
        "centimeter",
        "centimeters",
        "m",
        "meter",
        "meters",
        "fahrenheit",
        "celsius",
    };

    [GeneratedRegex(
        @"^Ingredients?\s*:\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex IngredientsHeaderRegex();

    [GeneratedRegex(
        @"^(?:Instructions?|Directions?|Method)\s*:\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex InstructionsHeaderRegex();

    [GeneratedRegex(
        @"^(?:Variation|Variations|Nutrition|Tips?|Notes?)\s*:\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex NonInstructionSectionHeaderRegex();

    [GeneratedRegex(@"^(\d+)\.\s+(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedInstructionLineRegex();

    [GeneratedRegex(@"^\s*\d+[.)]\s+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadStepPrefixRegex();

    [GeneratedRegex(@"^[A-Z][A-Z0-9\s,'’&-]{2,44}:\s*", RegexOptions.CultureInvariant)]
    private static partial Regex LeadUpperSectionPrefixRegex();

    [GeneratedRegex(
        @"^FOR\s+THE\s+(?<hdr>[A-Z][A-Z0-9\s,'’&/-]{1,50}):\s+",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex LeadForTheSectionRegex();

    [GeneratedRegex(
        @"^(?:(?:jumbo|extra[- ]large|large|medium|small)\s+)?(?:whites?|yolks?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex BareEggPartRegex();

    [GeneratedRegex(
        @"\b(?:yolks?|whites?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex EggPartWordRegex();

    [GeneratedRegex(@"\s+(?:and/)?or\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OrAlternativeRegex();

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex StepWordNonAlnumRegex();

    [GeneratedRegex(@"[.!?](?:\s+|$)", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceEndRegex();

    [GeneratedRegex(
        @"\b(?:recipe|follows|see\s+(?:this|facing)\s+page|page\s+\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex RecipeCueRegex();

    [GeneratedRegex(
        @"\b(?:this|facing)\s+page\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex PageReferenceCueRegex();

    [GeneratedRegex(
        @"^(?:(?:table|kosher|flake|flaky|sea|coarse|fine)\s+)?salt(?:\s+and\s+(?:(?:ground|black|freshly\s+ground)\s+)?pepper)?$|^(?:(?:freshly\s+)?(?:ground|cracked)\s+)?(?:black\s+|white\s+)?pepper$|^hot\s+sauce$|^(?:vegetable\s+|nonstick\s+|olive\s+)?(?:oil|cooking)\s+spray$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex UnquantifiedSeasoningOrSprayRegex();

    [GeneratedRegex(
        @"^(?<salt>(?:table|kosher|sea|fine|coarse|flak(?:e|y))\s+salt|salt)\s+and\s+(?<pep>(?:freshly\s+)?(?:ground|cracked)\s+(?:black\s+|white\s+)?pepper|(?:black|white)\s+pepper|pepper)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex SaltAndPepperSplitRegex();

    [GeneratedRegex(
        @"\b(?:ground|cracked|coarse(?:ly)?|freshly\s+ground|black|white|cayenne|red\s+pepper)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex SpicePepperCueRegex();

    [GeneratedRegex(
        @"\b(?:bell|sweet|jalape[nñ]o|serrano|poblano|habanero|anaheim|fresno|thai|chile|chili|banana)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex FreshPepperCueRegex();
}

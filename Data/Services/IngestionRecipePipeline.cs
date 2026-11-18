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

        var workIngredients = FlattenIngredients(stage1);
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

            var stage2Prompt = IngestionPromptLoader.LoadShortenDescriptionPrompt(
                DescriptionMaxLength
            );
            var stage2Res =
                await llmClient.ExecuteStructuredStageAsync<Stage2ShortenedDescriptionDto>(
                    request.Config,
                    stageName: "stage2_shorten_description",
                    messages: [new("system", stage2Prompt), new("user", description)],
                    jsonSchema: MultiStageSchemas.Stage2ShortenDescriptionSchema,
                    overrideThinking: false,
                    maxTokens: 1024,
                    cancellationToken: cancellationToken
                );

            if (
                stage2Res.Parsed is not null
                && !string.IsNullOrWhiteSpace(stage2Res.Parsed.Description)
            )
            {
                var shortened = stage2Res.Parsed.Description.Trim();
                if (shortened.Length <= description.Length || shortened.Length <= 280)
                {
                    description = shortened;
                }
            }
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
            var polishConfig = request.Config.HasDistinctRefinement()
                ? request.Config.ForRefinement()
                : request.Config;

            await ReportProgressAsync(
                request.OnStageProgress,
                "Stage 5/5: Polishing flagged recipe details..."
            );

            var polishRes = await RunStage5PolishAsync(
                polishConfig,
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
                    if (!string.IsNullOrWhiteSpace(patch.Name))
                    {
                        target.Name = patch.Name.Trim();
                    }
                    if (patch.Quantity.HasValue || patch.Unit is not null)
                    {
                        target.Quantity = patch.Quantity;
                        target.MaxQuantity = patch.MaxQuantity;
                        target.Unit = patch.Unit;
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
                    if (patch.AlternateMeasurements is not null)
                    {
                        target.AlternateMeasurements = patch.AlternateMeasurements;
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
        for (var attempt = 1; attempt <= 2; attempt++)
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

            if (
                string.IsNullOrWhiteSpace(res.Error)
                && parsed is not null
                && !string.IsNullOrWhiteSpace(parsed.Title)
                && ingCount > 0
                && stepCount > 0
            )
            {
                return parsed;
            }

            lastError =
                res.Error
                ?? (
                    parsed is null
                        ? "Stage 1 returned null recipe payload."
                        : $"Stage 1 returned incomplete recipe (title='{parsed.Title}', ingredients={ingCount}, steps={stepCount})."
                );

            if (attempt < 2)
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
                        $"The previous extraction failed validation: {lastError}. Please return a complete valid JSON recipe with title, sections containing all ingredients and all instruction steps, timing, and tags."
                    )
                );
            }
        }

        throw new InvalidOperationException(
            $"Multi-stage recipe extraction failed at Stage 1: {lastError}"
        );
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
                name = ing.Name,
                quantity = ing.Quantity,
                maxQuantity = ing.MaxQuantity,
                unit = ing.Unit,
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
                    IngredientIndices: [],
                    Timers: [],
                    IngredientAmounts: [],
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
                recipe = new
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
                        name = i.Name,
                        quantity = i.Quantity,
                        maxQuantity = i.MaxQuantity,
                        unit = i.Unit,
                        prepNote = i.PrepNote,
                        isOptional = i.IsOptional,
                        alternateMeasurements = i.AlternateMeasurements,
                    }),
                    steps = steps.Select(s => new
                    {
                        stepNumber = s.StepNumber,
                        originalInstruction = s.OriginalInstruction,
                        tokens = s.SanitizedTokensJson,
                        validationWarnings = s.ValidationWarnings,
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
            if (
                cleanUnit == MeasurementUnit.Custom
                && (
                    string.IsNullOrWhiteSpace(cleanCustomUnit)
                    || RecipeUnitAndSectionHelpers.IsDisallowedUnitPlaceholder(cleanCustomUnit)
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
            if (!incompatibleSet.Contains(i) && ing.AlternateMeasurements is { Count: > 0 })
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
                    if (string.IsNullOrWhiteSpace(rep.Name))
                    {
                        continue;
                    }

                    replacementMatches.TryGetValue((i, r), out var repMatch);
                    var (repQty, repMaxQty) = RecipeUnitAndSectionHelpers.NormalizeQuantities(
                        rep.Quantity,
                        rep.MaxQuantity
                    );
                    var (repUnit, repCustom) = RecipeUnitAndSectionHelpers.NormalizeUnit(rep.Unit);
                    if (
                        repUnit == MeasurementUnit.Custom
                        && (
                            string.IsNullOrWhiteSpace(repCustom)
                            || RecipeUnitAndSectionHelpers.IsDisallowedUnitPlaceholder(repCustom)
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

        // Compile steps & deterministically link unquantified prose mentions for Cook Mode + diagnostics
        var compiledSteps = new List<IngestionDraftStepDto>(validatedSteps.Count);
        foreach (var step in validatedSteps)
        {
            var plainText = StepInstructionParser.ToPlainText(step.CompiledInstruction);
            var combinedIndices = new SortedSet<int>(step.IngredientIndices);

            for (var i = 0; i < compiledIngredients.Count; i++)
            {
                if (combinedIndices.Contains(i))
                {
                    continue;
                }

                var candIng = compiledIngredients[i];
                var candName = candIng.DisplayName?.Trim() ?? string.Empty;
                if (
                    candName.Length >= 3
                    && !candName.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                    && WordBoundaryContains(plainText, candName)
                )
                {
                    // When duplicate ingredient names exist across sections, prefer the section match if one exists
                    var hasBetterSectionMatch = compiledIngredients
                        .Select((other, otherIdx) => (other, otherIdx))
                        .Any(x =>
                            x.otherIdx != i
                            && string.Equals(
                                x.other.DisplayName?.Trim(),
                                candName,
                                StringComparison.OrdinalIgnoreCase
                            )
                            && string.Equals(
                                x.other.SectionName,
                                step.SectionName,
                                StringComparison.OrdinalIgnoreCase
                            )
                            && !string.Equals(
                                candIng.SectionName,
                                step.SectionName,
                                StringComparison.OrdinalIgnoreCase
                            )
                        );
                    if (!hasBetterSectionMatch)
                    {
                        combinedIndices.Add(i);
                    }
                }
            }

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
                    IngredientIndices: [.. combinedIndices],
                    Timers: step.Timers,
                    ImagePath: null,
                    IngredientAmounts: step.IngredientAmounts
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
        var candidateName = !string.IsNullOrWhiteSpace(decision?.Name)
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

    private static List<WorkIngredient> FlattenIngredients(Stage1RecipeDto stage1)
    {
        var list = new List<WorkIngredient>();
        var idx = 0;
        foreach (var section in stage1.Sections ?? [])
        {
            var secName = NormalizeSection(section.Name);
            foreach (var ing in section.Ingredients ?? [])
            {
                list.Add(
                    new WorkIngredient
                    {
                        Index = idx++,
                        SectionName = secName,
                        Name = ing.Name?.Trim() ?? string.Empty,
                        Quantity = ing.Quantity,
                        MaxQuantity = ing.MaxQuantity,
                        Unit = ing.Unit,
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
            var secName = NormalizeSection(section.Name);
            foreach (var instruction in section.Instructions ?? [])
            {
                if (string.IsNullOrWhiteSpace(instruction))
                {
                    continue;
                }
                list.Add(new WorkStep(num++, secName, instruction.Trim()));
            }
        }
        return list;
    }

    private static string? NormalizeSection(string? rawSection)
    {
        if (
            string.IsNullOrWhiteSpace(rawSection)
            || RecipeUnitAndSectionHelpers.IsGenericTopLevelSection(rawSection)
        )
        {
            return null;
        }
        var trimmed = rawSection.Trim();
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

    private static bool WordBoundaryContains(string haystack, string needle) =>
        !string.IsNullOrWhiteSpace(haystack)
        && !string.IsNullOrWhiteSpace(needle)
        && Regex.IsMatch(haystack, $@"\b{Regex.Escape(needle)}\b", RegexOptions.IgnoreCase);

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

    [GeneratedRegex(@"[.!?](?:\s+|$)", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceEndRegex();

    [GeneratedRegex(
        @"\b(?:recipe|follows|see\s+(?:this|facing)\s+page|page\s+\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex RecipeCueRegex();

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

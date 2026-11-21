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
/// Runs the dedicated post-polish Stage 6 Refinement pass on a compiled
/// <see cref="IngestionExtractedDraftDto"/> using the configured Refinement workflow profile.
/// Audits food catalog canonical names, ingredient quantities/units, metadata, tags, and
/// tokenized steps, re-matching cleaned canonical food names against existing <c>FoodItems</c>
/// before catalog materialization.
/// </summary>
public sealed partial class IngestionRecipeRefiner(
    AppDbContext dbContext,
    ILlmExtractionClient llmClient,
    ILogger<IngestionRecipeRefiner>? logger = null,
    IIngestionLiveTracker? liveTracker = null
) : IIngestionRecipeRefiner
{
    private const int DescriptionMaxLength = 250;

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

    [GeneratedRegex(@"\s+(?:and/)?or\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OrAlternativeRegex();

    public async Task<IngestionRefineDraftResult> RefineDraftAsync(
        IngestionRefineDraftRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Draft);
        ArgumentNullException.ThrowIfNull(request.Config);

        var refineConfig = request.Config.ForRefinement();
        var refineModelName =
            IngestionMetadataAndTagHelper.CleanOptionalText(refineConfig.TextModel, 200)
            ?? IngestionMetadataAndTagHelper.CleanOptionalText(request.Config.RefineModel, 200)
            ?? "unknown";

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

        var foodsLookup = BuildFoodLookupMap(catalogFoods);
        var tagsById = catalogTags.ToDictionary(t => t.Id);

        var draft = request.Draft;
        var currentIngredients = (draft.Ingredients ?? []).ToList();
        var currentSteps = (draft.Steps ?? []).ToList();

        var existingTagNames = new List<string>();
        foreach (var tagId in draft.SuggestedTagIds ?? [])
        {
            if (tagsById.TryGetValue(tagId, out var tag))
            {
                existingTagNames.Add(tag.Name);
            }
        }
        foreach (var propTag in draft.ProposedTagNames ?? [])
        {
            if (
                !string.IsNullOrWhiteSpace(propTag)
                && !existingTagNames.Contains(propTag.Trim(), StringComparer.OrdinalIgnoreCase)
            )
            {
                existingTagNames.Add(propTag.Trim());
            }
        }

        // Convert current compiled steps (||{...}||) into Stage 4 token arrays for clean LLM auditing
        var baselineSteps = new List<BaselineStepState>(currentSteps.Count);
        for (var i = 0; i < currentSteps.Count; i++)
        {
            var step = currentSteps[i];
            var tokenArray = ConvertCompiledInstructionToTokenArray(
                step.Instruction,
                currentIngredients
            );
            var parsedOnly = StepTokenValidator.BuildAndValidateStep(
                i + 1,
                step.SectionName,
                string.Empty,
                tokenArray,
                currentIngredients.Count
            );
            var plainText = StepTokenValidator.RenderForValidation(parsedOnly.ParsedTokens);
            if (string.IsNullOrWhiteSpace(plainText))
            {
                plainText = StepInstructionParser.ToPlainText(step.Instruction);
            }
            var validated = StepTokenValidator.BuildAndValidateStep(
                i + 1,
                step.SectionName,
                plainText,
                tokenArray,
                currentIngredients.Count
            );
            baselineSteps.Add(new BaselineStepState(step, plainText, validated));
        }

        var refinePrompt = IngestionPromptLoader.LoadPrompt(
            IngestionPromptLoader.Stage6RefineRecipe
        );

        var payloadJson = JsonSerializer.Serialize(
            new
            {
                originalSourceText = request.SourceText,
                currentRecipe = new
                {
                    title = draft.Title,
                    description = draft.Description,
                    servings = draft.BaseServings,
                    yield = draft.YieldDisplay,
                    timing = new
                    {
                        prep = draft.PrepTimeMinutes,
                        cook = draft.CookTimeMinutes,
                        total = draft.TotalTimeMinutes,
                    },
                    tags = existingTagNames,
                    ingredients = currentIngredients.Select(
                        (ing, idx) =>
                            new
                            {
                                index = idx,
                                section = ing.SectionName,
                                displayName = ing.DisplayName,
                                canonicalFoodName = ing.IsRecipeReference
                                    ? null
                                    : (ing.MatchedFoodItemName ?? ing.ProposedFoodItemName),
                                isNewFoodItem = ing.IsNewFoodItem,
                                isLinkedToExistingFood = ing.MatchedFoodItemId.HasValue,
                                category = ing.SuggestedCategory?.ToString(),
                                isPantryStaple = ing.SuggestedIsPantryStaple,
                                quantity = ing.Quantity,
                                maxQuantity = ing.MaxQuantity,
                                unit = FormatMeasurementUnitString(ing.Unit, ing.CustomUnitName),
                                prepNote = ing.PreparationNote,
                                isOptional = ing.IsOptional,
                                isRecipeReference = ing.IsRecipeReference,
                                referencedRecipeTitle = ing.ReferencedRecipeTitle,
                                originalRawText = ing.OriginalRawText,
                                alternateMeasurements = ing.AlternativeMeasurement is not null
                                    ? new[]
                                    {
                                        new
                                        {
                                            quantity = ing.AlternativeMeasurement.Quantity,
                                            unit = FormatMeasurementUnitString(
                                                ing.AlternativeMeasurement.Unit,
                                                ing.AlternativeMeasurement.CustomUnitName
                                            ),
                                        },
                                    }
                                    : Array.Empty<object>(),
                                replacements = (ing.Replacements ?? []).Select(
                                    (rep, repIdx) =>
                                        new
                                        {
                                            replacementIndex = repIdx,
                                            displayName = rep.DisplayName,
                                            canonicalFoodName = rep.MatchedFoodItemName
                                                ?? rep.ProposedFoodItemName,
                                            isNewFoodItem = rep.IsNewFoodItem,
                                            category = rep.SuggestedCategory?.ToString(),
                                            isPantryStaple = rep.SuggestedIsPantryStaple,
                                            quantity = rep.Quantity,
                                            maxQuantity = rep.MaxQuantity,
                                            unit = FormatMeasurementUnitString(
                                                rep.Unit,
                                                rep.CustomUnitName
                                            ),
                                            prepNote = rep.PreparationNote,
                                        }
                                ),
                            }
                    ),
                    steps = baselineSteps.Select(s => new
                    {
                        stepNumber = s.Validated.StepNumber,
                        sectionName = s.Step.SectionName,
                        renderedInstruction = StepTokenValidator.RenderForValidation(
                            s.Validated.ParsedTokens
                        ),
                        validationWarnings = s.Validated.ValidationWarnings,
                        tokens = s.Validated.SanitizedTokensJson,
                    }),
                },
                diagnostics = new
                {
                    validationIssues = draft.ValidationIssues ?? [],
                    qualityWarnings = draft.QualityWarnings ?? [],
                    measurementWarnings = draft.MeasurementWarnings ?? [],
                },
            }
        );

        liveTracker?.CurrentWorkload?.UpdateSegmentSubStatus(
            "stage6",
            $"Auditing via {refineModelName}",
            $"Stage 6: Auditing recipe & catalog names via {refineModelName}..."
        );

        var stageResult =
            await llmClient.ExecuteStructuredStageAsync<Stage6RefineRecipeResponseDto>(
                refineConfig,
                stageName: "stage6_refine_recipe",
                messages: [new("system", refinePrompt), new("user", payloadJson)],
                jsonSchema: MultiStageSchemas.Stage6RefineRecipeSchema,
                overrideThinking: null,
                maxTokens: 16384,
                cancellationToken: cancellationToken
            );

        liveTracker?.CurrentWorkload?.UpdateSegmentSubStatus(
            "stage6",
            "Applying patches",
            $"Stage 6: Applying validated refinement patches..."
        );

        var response =
            stageResult.Parsed
            ?? throw new InvalidOperationException(
                $"Refinement stage returned no structured payload: {stageResult.Error ?? "empty response"}"
            );

        var madeChanges = false;
        var title = draft.Title;
        var description = draft.Description;
        var baseServings = draft.BaseServings;
        var yieldDisplay = draft.YieldDisplay;
        var prepMinutes = draft.PrepTimeMinutes;
        var cookMinutes = draft.CookTimeMinutes;
        var totalMinutes = draft.TotalTimeMinutes;

        if (
            !string.IsNullOrWhiteSpace(response.Title)
            && !string.Equals(response.Title.Trim(), title, StringComparison.Ordinal)
        )
        {
            title = IngestionMetadataAndTagHelper.Truncate(response.Title.Trim(), 200);
            madeChanges = true;
        }

        if (!string.IsNullOrWhiteSpace(response.Description))
        {
            var proposedDesc = response.Description.Trim();
            if (
                !(
                    description is not null
                    && proposedDesc.Length > 300
                    && description.Length <= DescriptionMaxLength
                ) && !string.Equals(proposedDesc, description, StringComparison.Ordinal)
            )
            {
                description = IngestionMetadataAndTagHelper.CleanOptionalText(proposedDesc, 2000);
                madeChanges = true;
            }
        }

        if (response.Servings is >= 0.25m and <= 999m)
        {
            var rounded = decimal.Round(response.Servings.Value, 2);
            if (rounded != baseServings)
            {
                baseServings = rounded;
                madeChanges = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(response.Yield))
        {
            var cleanYield = IngestionMetadataAndTagHelper.CleanOptionalText(response.Yield, 100);
            if (!string.Equals(cleanYield, yieldDisplay, StringComparison.Ordinal))
            {
                yieldDisplay = cleanYield;
                madeChanges = true;
            }
        }

        if (response.Timing is not null)
        {
            var newPrep = IngestionMetadataAndTagHelper.ClampMinutes(
                response.Timing.Prep ?? prepMinutes
            );
            var newCook = IngestionMetadataAndTagHelper.ClampMinutes(
                response.Timing.Cook ?? cookMinutes
            );
            var newTotal =
                IngestionMetadataAndTagHelper.ClampMinutes(response.Timing.Total)
                ?? (
                    newPrep.HasValue && newCook.HasValue
                        ? IngestionMetadataAndTagHelper.ClampMinutes(newPrep.Value + newCook.Value)
                        : totalMinutes
                );

            if (newPrep != prepMinutes || newCook != cookMinutes || newTotal != totalMinutes)
            {
                prepMinutes = newPrep;
                cookMinutes = newCook;
                totalMinutes = newTotal;
                madeChanges = true;
            }
        }

        // Apply ingredient & food catalog name patches
        var withinRecipeNewFoods = new Dictionary<string, (string Name, FoodCategory Category)>(
            StringComparer.OrdinalIgnoreCase
        );
        var measurementWarnings = new List<string>();
        var patchedIngredients = currentIngredients.ToList();

        foreach (var patch in response.IngredientPatches ?? [])
        {
            if (patch.Index < 0 || patch.Index >= patchedIngredients.Count)
            {
                continue;
            }

            var existing = patchedIngredients[patch.Index];
            var updated = ApplyIngredientPatch(
                existing,
                patch,
                foodsLookup,
                withinRecipeNewFoods,
                out var ingredientChanged
            );

            if (ingredientChanged)
            {
                patchedIngredients[patch.Index] = updated;
                madeChanges = true;
            }
        }

        // Re-check alternate measurement compatibility across all ingredients
        for (var i = 0; i < patchedIngredients.Count; i++)
        {
            var ing = patchedIngredients[i];
            if (ing.AlternativeMeasurement is not null)
            {
                var altUnitStr = FormatMeasurementUnitString(
                    ing.AlternativeMeasurement.Unit,
                    ing.AlternativeMeasurement.CustomUnitName
                );
                var primaryUnitStr = FormatMeasurementUnitString(ing.Unit, ing.CustomUnitName);
                var altList = new[]
                {
                    new Stage1AlternateMeasurementDto(
                        ing.AlternativeMeasurement.Quantity,
                        altUnitStr
                    ),
                };
                if (
                    StepTokenValidator.TryFindIncompatibleAlternateMeasurement(
                        ing.Quantity,
                        ing.MaxQuantity,
                        primaryUnitStr,
                        altList,
                        out var reason
                    )
                )
                {
                    measurementWarnings.Add($"Ingredient #{i + 1} ('{ing.DisplayName}'): {reason}");
                }
            }
        }

        // Apply step patches with strict validation guardrails
        var updatedSteps = currentSteps.ToList();
        var qualityWarnings = new List<string>();
        var stepStates = baselineSteps.ToList();

        foreach (var stepPatch in response.StepPatches ?? [])
        {
            var idx = stepPatch.StepNumber - 1;
            if (idx < 0 || idx >= stepStates.Count || stepPatch.Tokens is null)
            {
                continue;
            }

            var existingState = stepStates[idx];
            var candidate = StepTokenValidator.BuildAndValidateStep(
                existingState.Validated.StepNumber,
                existingState.Step.SectionName,
                existingState.PlainText,
                stepPatch.Tokens,
                patchedIngredients.Count
            );

            if (
                candidate.ParsedTokens.Count > 0
                && candidate.ValidationWarnings.Count
                    <= existingState.Validated.ValidationWarnings.Count
            )
            {
                var newInstruction = IngestionMetadataAndTagHelper.Truncate(
                    candidate.CompiledInstruction,
                    4000
                );
                if (
                    !string.Equals(
                        newInstruction,
                        existingState.Step.Instruction,
                        StringComparison.Ordinal
                    )
                )
                {
                    updatedSteps[idx] = existingState.Step with
                    {
                        Instruction = newInstruction,
                        Timers = candidate.Timers,
                    };
                    stepStates[idx] = existingState with
                    {
                        Step = updatedSteps[idx],
                        Validated = candidate,
                    };
                    madeChanges = true;
                }
            }
            else
            {
                logger?.LogInformation(
                    "Stage 6 refinement guardrail rejected stepPatch for step {StepNumber} (warnings before={Before}, after={After}).",
                    stepPatch.StepNumber,
                    existingState.Validated.ValidationWarnings.Count,
                    candidate.ValidationWarnings.Count
                );
            }
        }

        foreach (var state in stepStates)
        {
            foreach (var warning in state.Validated.ValidationWarnings)
            {
                qualityWarnings.Add($"Step {state.Validated.StepNumber}: {warning}");
            }
        }

        // Resolve tags if the Refinement model patched them; otherwise preserve existing tags
        var suggestedTagIds = draft.SuggestedTagIds ?? [];
        var proposedTagNames = draft.ProposedTagNames;
        if (response.Tags is { Count: > 0 })
        {
            var cleanedTags = response.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            if (cleanedTags.Count > 0)
            {
                var (resolvedTagIds, resolvedProposedTags) =
                    IngestionMetadataAndTagHelper.ResolveAndGuardTags(
                        title,
                        description,
                        cleanedTags,
                        catalogTags,
                        patchedIngredients,
                        updatedSteps,
                        cookMinutes,
                        totalMinutes
                    );

                if (
                    !resolvedTagIds.SequenceEqual(suggestedTagIds)
                    || !(resolvedProposedTags ?? []).SequenceEqual(proposedTagNames ?? [])
                )
                {
                    suggestedTagIds = resolvedTagIds;
                    proposedTagNames = resolvedProposedTags;
                    madeChanges = true;
                }
            }
        }

        var refinedDraft = draft with
        {
            Title = title,
            Description = description,
            BaseServings = baseServings,
            YieldDisplay = yieldDisplay,
            PrepTimeMinutes = prepMinutes,
            CookTimeMinutes = cookMinutes,
            TotalTimeMinutes = totalMinutes,
            SuggestedTagIds = suggestedTagIds,
            ProposedTagNames = proposedTagNames,
            Ingredients = patchedIngredients,
            Steps = updatedSteps,
            RefinedModel = refineModelName,
            MeasurementWarnings = measurementWarnings.Count > 0 ? measurementWarnings : null,
            QualityWarnings = qualityWarnings.Count > 0 ? qualityWarnings : null,
        };

        // Re-resolve sub-recipe references & compute final diagnostics
        var validationIssues = new List<string>();
        if (refinedDraft.Ingredients is { Count: > 0 })
        {
            var (resolvedDraft, refIssues) =
                await IngestionCatalogMaterializer.ResolveDraftRecipeReferencesAsync(
                    dbContext,
                    refinedDraft,
                    request.ExcludeRecipeId,
                    cancellationToken
                );
            refinedDraft = resolvedDraft;
            validationIssues.AddRange(refIssues);
        }

        if (string.IsNullOrWhiteSpace(refinedDraft.Title))
        {
            validationIssues.Add("Recipe title is missing.");
        }
        if ((refinedDraft.Ingredients?.Count ?? 0) == 0)
        {
            validationIssues.Add("Recipe has no ingredients.");
        }
        if ((refinedDraft.Steps?.Count ?? 0) == 0)
        {
            validationIssues.Add("Recipe has no preparation steps.");
        }

        foreach (var (ing, idx) in (refinedDraft.Ingredients ?? []).Select((x, i) => (x, i)))
        {
            if (!ing.IsRecipeReference && !ing.MatchedFoodItemId.HasValue)
            {
                var candidateName = !string.IsNullOrWhiteSpace(ing.ProposedFoodItemName)
                    ? ing.ProposedFoodItemName
                    : ing.DisplayName;
                if (!FoodNameSanitizer.IsValidFoodName(candidateName))
                {
                    validationIssues.Add(
                        $"Ingredient #{idx + 1} ('{ing.DisplayName}') does not have a meaningful food name."
                    );
                }
            }
        }

        var distinctValidationIssues = validationIssues
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var filteredQualityWarnings = IngestionProcessor.FilterBenignSubRecipeQualityWarnings(
            refinedDraft.QualityWarnings,
            refinedDraft.Ingredients
        );

        var draftWithDiagnostics = refinedDraft with
        {
            QualityWarnings = filteredQualityWarnings.Count > 0 ? filteredQualityWarnings : null,
            ValidationIssues = distinctValidationIssues.Count > 0 ? distinctValidationIssues : null,
        };

        var confidence = IngestionConfidenceScorer.Compute(
            draftWithDiagnostics,
            draftWithDiagnostics.PipelineRepairCount
        );

        refinedDraft = draftWithDiagnostics with { ConfidenceScore = confidence };

        var notes = (response.RefinementNotes ?? [])
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .ToList();

        return new IngestionRefineDraftResult(
            Draft: refinedDraft,
            RefinedModel: refineModelName,
            RefinementNotes: notes,
            MadeChanges: madeChanges
        );
    }

    private IngestionDraftIngredientDto ApplyIngredientPatch(
        IngestionDraftIngredientDto existing,
        Stage6IngredientPatchDto patch,
        Dictionary<string, CatalogFoodLookupItem> foodsLookup,
        Dictionary<string, (string Name, FoodCategory Category)> withinRecipeNewFoods,
        out bool changed
    )
    {
        var updated = existing;

        // 1. Display name
        if (!string.IsNullOrWhiteSpace(patch.DisplayName))
        {
            var candidateDisplay = IngestionMetadataAndTagHelper.Truncate(
                RecipeUnitAndSectionHelpers.StripLeadingBulletOrIndex(patch.DisplayName.Trim()),
                200
            );
            var hasReplacements = existing.Replacements is { Count: > 0 };
            if (
                !string.IsNullOrWhiteSpace(candidateDisplay)
                && !(hasReplacements && OrAlternativeRegex().IsMatch(candidateDisplay))
            )
            {
                updated = updated with { DisplayName = candidateDisplay };
            }
        }

        // 2. Quantity, MaxQuantity, Unit
        var proposedQty = patch.Quantity ?? updated.Quantity;
        var proposedMax = patch.MaxQuantity ?? updated.MaxQuantity;
        if (
            patch.MaxQuantity.HasValue
            && (!proposedQty.HasValue || proposedMax!.Value < proposedQty.Value)
        )
        {
            logger?.LogInformation(
                "Stage 6 refinement guardrail rejected invalid range on ingredient [{Index}] ({Qty}..{Max}).",
                patch.Index,
                proposedQty,
                proposedMax
            );
            proposedMax = updated.MaxQuantity;
        }
        var (cleanQty, cleanMaxQty) = RecipeUnitAndSectionHelpers.NormalizeQuantities(
            proposedQty,
            proposedMax
        );
        updated = updated with { Quantity = cleanQty, MaxQuantity = cleanMaxQty };

        if (patch.Unit is not null)
        {
            if (string.IsNullOrWhiteSpace(patch.Unit))
            {
                updated = updated with { Unit = null, CustomUnitName = null };
            }
            else
            {
                var (normUnit, normCustom) = RecipeUnitAndSectionHelpers.NormalizeUnit(patch.Unit);
                if (
                    normUnit == MeasurementUnit.Custom
                    && (
                        string.IsNullOrWhiteSpace(normCustom)
                        || RecipeUnitAndSectionHelpers.IsDisallowedUnitPlaceholder(normCustom)
                        || RecipeUnitAndSectionHelpers.IsRedundantSelfCustomUnit(
                            normCustom,
                            updated.DisplayName
                        )
                    )
                )
                {
                    updated = updated with { Unit = null, CustomUnitName = null };
                }
                else
                {
                    updated = updated with
                    {
                        Unit = normUnit,
                        CustomUnitName = normUnit == MeasurementUnit.Custom ? normCustom : null,
                    };
                }
            }
        }

        // 3. Prep note & optional
        if (patch.PrepNote is not null)
        {
            updated = updated with
            {
                PreparationNote = RecipeUnitAndSectionHelpers.CleanPreparationNote(
                    patch.PrepNote,
                    200
                ),
            };
        }

        if (patch.IsOptional.HasValue)
        {
            updated = updated with { IsOptional = patch.IsOptional.Value };
        }

        // 4. Alternate measurements
        if (patch.AlternateMeasurements is not null)
        {
            if (patch.AlternateMeasurements.Count == 0)
            {
                updated = updated with { AlternativeMeasurement = null };
            }
            else
            {
                var hasDimOrTemp = patch.AlternateMeasurements.Any(a =>
                    !string.IsNullOrWhiteSpace(a.Unit)
                    && DimensionOrTempUnits.Contains(a.Unit.Trim())
                );
                var primaryUnitStr = FormatMeasurementUnitString(
                    updated.Unit,
                    updated.CustomUnitName
                );
                var incompatible = StepTokenValidator.TryFindIncompatibleAlternateMeasurement(
                    updated.Quantity,
                    updated.MaxQuantity,
                    primaryUnitStr,
                    patch.AlternateMeasurements,
                    out _
                );
                if (!hasDimOrTemp && !incompatible)
                {
                    var firstAlt = patch.AlternateMeasurements[0];
                    if (firstAlt.Quantity is > 0m)
                    {
                        var (altUnit, altCustom) = RecipeUnitAndSectionHelpers.NormalizeUnit(
                            firstAlt.Unit
                        );
                        if (
                            altUnit.HasValue
                            && (
                                altUnit != MeasurementUnit.Custom
                                || !string.IsNullOrWhiteSpace(altCustom)
                            )
                        )
                        {
                            updated = updated with
                            {
                                AlternativeMeasurement = new RecipeMeasurement(
                                    Quantity: decimal.Round(firstAlt.Quantity.Value, 4),
                                    Unit: altUnit.Value,
                                    MaxQuantity: null,
                                    CustomUnitName: altUnit == MeasurementUnit.Custom
                                        ? IngestionMetadataAndTagHelper.CleanOptionalText(
                                            altCustom,
                                            50
                                        )
                                        : null
                                ),
                                Unit = updated.Unit ?? MeasurementUnit.Whole,
                            };
                        }
                    }
                }
            }
        }

        // 5. Recipe reference toggle & Food Catalog canonical name resolution
        var isRecipeRef = patch.IsRecipeReference ?? updated.IsRecipeReference;
        if (isRecipeRef)
        {
            var cleanRefTitle =
                RecipeUnitAndSectionHelpers.NormalizeRecipeReferenceTitle(
                    patch.ReferencedRecipeTitle
                        ?? updated.ReferencedRecipeTitle
                        ?? updated.DisplayName
                )
                ?? (
                    patch.ReferencedRecipeTitle
                    ?? updated.ReferencedRecipeTitle
                    ?? updated.DisplayName
                ).Trim();
            updated = updated with
            {
                IsRecipeReference = true,
                ReferencedRecipeTitle = IngestionMetadataAndTagHelper.CleanOptionalText(
                    cleanRefTitle,
                    200
                ),
                MatchedRecipeId = !string.Equals(
                    cleanRefTitle,
                    updated.ReferencedRecipeTitle,
                    StringComparison.OrdinalIgnoreCase
                )
                    ? null
                    : updated.MatchedRecipeId,
                MatchedRecipeTitle = !string.Equals(
                    cleanRefTitle,
                    updated.ReferencedRecipeTitle,
                    StringComparison.OrdinalIgnoreCase
                )
                    ? null
                    : updated.MatchedRecipeTitle,
                MatchedRecipeSlug = !string.Equals(
                    cleanRefTitle,
                    updated.ReferencedRecipeTitle,
                    StringComparison.OrdinalIgnoreCase
                )
                    ? null
                    : updated.MatchedRecipeSlug,
                MatchedFoodItemId = null,
                MatchedFoodItemName = null,
                IsNewFoodItem = false,
                CreateNewFoodItem = false,
                ProposedFoodItemName = null,
            };
        }
        else
        {
            if (updated.IsRecipeReference && patch.IsRecipeReference == false)
            {
                updated = updated with
                {
                    IsRecipeReference = false,
                    ReferencedRecipeTitle = null,
                    MatchedRecipeId = null,
                    MatchedRecipeTitle = null,
                    MatchedRecipeSlug = null,
                };
            }

            var patchedCategory = FoodCategories.Parse(patch.Category);
            var canonicalPatched = !string.IsNullOrWhiteSpace(patch.CanonicalFoodName);
            var needsReResolution =
                canonicalPatched
                || (existing.IsRecipeReference && !isRecipeRef)
                || (
                    updated.IsNewFoodItem
                    && !string.IsNullOrWhiteSpace(patch.DisplayName)
                    && !string.Equals(
                        existing.DisplayName,
                        updated.DisplayName,
                        StringComparison.OrdinalIgnoreCase
                    )
                );

            if (needsReResolution)
            {
                var rawCandidate = canonicalPatched
                    ? patch.CanonicalFoodName!.Trim()
                    : (
                        !string.IsNullOrWhiteSpace(updated.ProposedFoodItemName)
                            ? updated.ProposedFoodItemName
                            : updated.DisplayName
                    );

                if (
                    TryResolveCanonicalFood(
                        rawCandidate,
                        patchedCategory ?? updated.SuggestedCategory,
                        patch.IsPantryStaple,
                        foodsLookup,
                        withinRecipeNewFoods,
                        out var matchedFood,
                        out var cleanCanonical,
                        out var resolvedCategory,
                        out var resolvedPantry
                    )
                )
                {
                    if (matchedFood is not null)
                    {
                        updated = updated with
                        {
                            MatchedFoodItemId = matchedFood.Id,
                            MatchedFoodItemName = matchedFood.Name,
                            IsNewFoodItem = false,
                            CreateNewFoodItem = false,
                            ProposedFoodItemName = null,
                            SuggestedCategory = matchedFood.Category ?? resolvedCategory,
                            SuggestedIsPantryStaple = matchedFood.IsPantryStaple,
                        };
                    }
                    else
                    {
                        updated = updated with
                        {
                            MatchedFoodItemId = null,
                            MatchedFoodItemName = null,
                            IsNewFoodItem = true,
                            CreateNewFoodItem = true,
                            ProposedFoodItemName = IngestionMetadataAndTagHelper.CleanOptionalText(
                                cleanCanonical,
                                150
                            ),
                            SuggestedCategory = resolvedCategory,
                            SuggestedIsPantryStaple = resolvedPantry,
                        };
                    }
                }
            }
            else
            {
                if (patchedCategory.HasValue)
                {
                    updated = updated with { SuggestedCategory = patchedCategory.Value };
                }
                if (patch.IsPantryStaple.HasValue)
                {
                    updated = updated with { SuggestedIsPantryStaple = patch.IsPantryStaple.Value };
                }
            }
        }

        // 6. Replacement patches
        if (
            patch.ReplacementPatches is { Count: > 0 }
            && updated.Replacements is { Count: > 0 } existingReps
        )
        {
            var patchedReps = existingReps.ToList();
            var repsChanged = false;
            foreach (var repPatch in patch.ReplacementPatches)
            {
                if (repPatch.ReplacementIndex < 0 || repPatch.ReplacementIndex >= patchedReps.Count)
                {
                    continue;
                }

                var rep = patchedReps[repPatch.ReplacementIndex];
                var updatedRep = ApplyReplacementPatch(
                    rep,
                    repPatch,
                    foodsLookup,
                    withinRecipeNewFoods,
                    out var repChanged
                );
                if (repChanged)
                {
                    patchedReps[repPatch.ReplacementIndex] = updatedRep;
                    repsChanged = true;
                }
            }

            if (repsChanged)
            {
                updated = updated with { Replacements = patchedReps };
            }
        }

        changed = !Equals(existing, updated);
        return updated;
    }

    private static IngestionDraftReplacementDto ApplyReplacementPatch(
        IngestionDraftReplacementDto existing,
        Stage6ReplacementPatchDto patch,
        Dictionary<string, CatalogFoodLookupItem> foodsLookup,
        Dictionary<string, (string Name, FoodCategory Category)> withinRecipeNewFoods,
        out bool changed
    )
    {
        var updated = existing;

        if (!string.IsNullOrWhiteSpace(patch.DisplayName))
        {
            updated = updated with
            {
                DisplayName = IngestionMetadataAndTagHelper.Truncate(
                    RecipeUnitAndSectionHelpers.StripLeadingBulletOrIndex(patch.DisplayName.Trim()),
                    200
                ),
            };
        }

        var proposedQty = patch.Quantity ?? updated.Quantity;
        var proposedMax = patch.MaxQuantity ?? updated.MaxQuantity;
        if (
            patch.MaxQuantity.HasValue
            && (!proposedQty.HasValue || proposedMax!.Value < proposedQty.Value)
        )
        {
            proposedMax = updated.MaxQuantity;
        }
        var (cleanQty, cleanMaxQty) = RecipeUnitAndSectionHelpers.NormalizeQuantities(
            proposedQty,
            proposedMax
        );
        updated = updated with { Quantity = cleanQty, MaxQuantity = cleanMaxQty };

        if (patch.Unit is not null)
        {
            if (string.IsNullOrWhiteSpace(patch.Unit))
            {
                updated = updated with { Unit = null, CustomUnitName = null };
            }
            else
            {
                var (normUnit, normCustom) = RecipeUnitAndSectionHelpers.NormalizeUnit(patch.Unit);
                updated = updated with
                {
                    Unit = normUnit,
                    CustomUnitName = normUnit == MeasurementUnit.Custom ? normCustom : null,
                };
            }
        }

        if (patch.PrepNote is not null)
        {
            updated = updated with
            {
                PreparationNote = RecipeUnitAndSectionHelpers.CleanPreparationNote(
                    patch.PrepNote,
                    300
                ),
            };
        }

        var patchedCategory = FoodCategories.Parse(patch.Category);
        if (!string.IsNullOrWhiteSpace(patch.CanonicalFoodName))
        {
            if (
                TryResolveCanonicalFood(
                    patch.CanonicalFoodName.Trim(),
                    patchedCategory ?? updated.SuggestedCategory,
                    patch.IsPantryStaple,
                    foodsLookup,
                    withinRecipeNewFoods,
                    out var matchedFood,
                    out var cleanCanonical,
                    out var resolvedCategory,
                    out var resolvedPantry
                )
            )
            {
                if (matchedFood is not null)
                {
                    updated = updated with
                    {
                        MatchedFoodItemId = matchedFood.Id,
                        MatchedFoodItemName = matchedFood.Name,
                        IsNewFoodItem = false,
                        CreateNewFoodItem = false,
                        ProposedFoodItemName = null,
                        SuggestedCategory = matchedFood.Category ?? resolvedCategory,
                        SuggestedIsPantryStaple = matchedFood.IsPantryStaple,
                    };
                }
                else
                {
                    updated = updated with
                    {
                        MatchedFoodItemId = null,
                        MatchedFoodItemName = null,
                        IsNewFoodItem = true,
                        CreateNewFoodItem = true,
                        ProposedFoodItemName = IngestionMetadataAndTagHelper.CleanOptionalText(
                            cleanCanonical,
                            150
                        ),
                        SuggestedCategory = resolvedCategory,
                        SuggestedIsPantryStaple = resolvedPantry,
                    };
                }
            }
        }
        else
        {
            if (patchedCategory.HasValue)
            {
                updated = updated with { SuggestedCategory = patchedCategory.Value };
            }
            if (patch.IsPantryStaple.HasValue)
            {
                updated = updated with { SuggestedIsPantryStaple = patch.IsPantryStaple.Value };
            }
        }

        changed = !Equals(existing, updated);
        return updated;
    }

    private static bool TryResolveCanonicalFood(
        string rawCandidate,
        FoodCategory? preferredCategory,
        bool? preferredIsPantryStaple,
        Dictionary<string, CatalogFoodLookupItem> foodsLookup,
        Dictionary<string, (string Name, FoodCategory Category)> withinRecipeNewFoods,
        out CatalogFoodLookupItem? matchedFood,
        out string cleanCanonical,
        out FoodCategory resolvedCategory,
        out bool resolvedPantry
    )
    {
        matchedFood = null;
        cleanCanonical = string.Empty;
        resolvedCategory = FoodCategory.Other;
        resolvedPantry = false;

        if (OrAlternativeRegex().IsMatch(rawCandidate))
        {
            return false;
        }

        var normalized = FoodNameSanitizer.NormalizeCanonicalFoodName(rawCandidate);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = RecipeUnitAndSectionHelpers.StripLeadingBulletOrIndex(rawCandidate.Trim());
        }

        if (!FoodNameSanitizer.IsValidFoodName(normalized))
        {
            return false;
        }

        var lower = normalized.ToLowerInvariant();
        var singular = RecipeUnitAndSectionHelpers.Singularize(lower);
        var lookupKeys = new[] { lower, singular, $"{singular}s", $"{singular}es" }.Distinct();

        foreach (var key in lookupKeys)
        {
            if (foodsLookup.TryGetValue(key, out var existing))
            {
                matchedFood = existing;
                cleanCanonical = existing.Name;
                resolvedCategory =
                    existing.Category ?? FoodCategoryClassifier.Classify(existing.Name);
                resolvedPantry = existing.IsPantryStaple;
                return true;
            }
        }

        var category = preferredCategory ?? FoodCategoryClassifier.Classify(normalized);
        if (withinRecipeNewFoods.TryGetValue(lower, out var existingNew))
        {
            cleanCanonical = existingNew.Name;
            resolvedCategory = existingNew.Category;
        }
        else
        {
            cleanCanonical = normalized;
            resolvedCategory = category;
            withinRecipeNewFoods[lower] = (cleanCanonical, resolvedCategory);
        }

        resolvedPantry =
            preferredIsPantryStaple
            ?? FoodCategoryClassifier.IsLikelyPantryStaple(cleanCanonical, resolvedCategory);
        return true;
    }

    private static Dictionary<string, CatalogFoodLookupItem> BuildFoodLookupMap(
        IReadOnlyList<CatalogFoodLookupItem> catalogFoods
    )
    {
        var map = new Dictionary<string, CatalogFoodLookupItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var food in catalogFoods)
        {
            map.TryAdd(food.NormalizedName, food);
            var singular = RecipeUnitAndSectionHelpers.Singularize(food.NormalizedName);
            map.TryAdd(singular, food);
            foreach (var alias in food.NormalizedAliases)
            {
                if (!string.IsNullOrWhiteSpace(alias))
                {
                    map.TryAdd(alias, food);
                }
            }
        }
        return map;
    }

    private static JsonArray ConvertCompiledInstructionToTokenArray(
        string instruction,
        IReadOnlyList<IngestionDraftIngredientDto> ingredients
    )
    {
        var segments = StepInstructionParser.ParseSegments(instruction);
        var arr = new JsonArray();

        foreach (var seg in segments)
        {
            if (seg.Token is null)
            {
                if (!string.IsNullOrEmpty(seg.Text))
                {
                    arr.Add(JsonValue.Create(seg.Text));
                }
                continue;
            }

            switch (seg.Token)
            {
                case StepIngredientToken ing:
                {
                    var matchedIdx = FindMatchingIngredientIndex(ing.Ingredient, ingredients);
                    var ingObj = new JsonObject
                    {
                        ["type"] = "ingredient",
                        ["ingredientIndex"] = matchedIdx,
                        ["name"] = ing.Ingredient,
                        ["quantity"] = ing.Amount,
                        ["maxQuantity"] = ing.MaxAmount,
                        ["unit"] = FormatMeasurementUnitString(ing.Unit, ing.CustomUnitName),
                    };
                    if (ing.Fixed)
                    {
                        ingObj["fixed"] = true;
                    }
                    arr.Add(ingObj);
                    break;
                }
                case StepMixtureToken mix:
                    arr.Add(
                        new JsonObject
                        {
                            ["type"] = "mixture",
                            ["name"] = mix.Name,
                            ["quantity"] = mix.Amount,
                            ["maxQuantity"] = mix.MaxAmount,
                            ["unit"] = FormatMeasurementUnitString(mix.Unit, mix.CustomUnitName),
                        }
                    );
                    break;
                case StepTimerToken timer:
                {
                    var (amt, maxAmt, unit) = ConvertTimerSecondsToAmount(
                        timer.Seconds,
                        timer.MaxSeconds
                    );
                    arr.Add(
                        new JsonObject
                        {
                            ["type"] = "timer",
                            ["amount"] = amt,
                            ["maxAmount"] = maxAmt,
                            ["unit"] = unit,
                            ["label"] = timer.Label ?? string.Empty,
                        }
                    );
                    break;
                }
                case StepTemperatureToken temp:
                {
                    var isFahrenheit = temp.Fahrenheit.HasValue || !temp.Celsius.HasValue;
                    var amount = isFahrenheit ? (temp.Fahrenheit ?? 0m) : (temp.Celsius ?? 0m);
                    var maxAmount = isFahrenheit ? temp.MaxFahrenheit : temp.MaxCelsius;
                    arr.Add(
                        new JsonObject
                        {
                            ["type"] = "temperature",
                            ["amount"] = amount,
                            ["maxAmount"] = maxAmount,
                            ["unit"] = isFahrenheit ? "fahrenheit" : "celsius",
                        }
                    );
                    break;
                }
                case StepDimensionToken dim:
                    arr.Add(
                        new JsonObject
                        {
                            ["type"] = "dimension",
                            ["amount"] = dim.Value,
                            ["maxAmount"] = dim.MaxValue,
                            ["unit"] = FormatDimensionUnitFullName(dim.Unit),
                            ["hyphenated"] = dim.Hyphenated,
                        }
                    );
                    break;
                case StepFixedToken fx:
                    arr.Add(
                        new JsonObject
                        {
                            ["type"] = "fixed",
                            ["name"] = fx.Name,
                            ["amount"] = fx.Amount,
                            ["maxAmount"] = fx.MaxAmount,
                            ["unit"] = FormatMeasurementUnitString(fx.Unit, fx.CustomUnitName),
                            ["hyphenated"] = fx.Hyphenated,
                        }
                    );
                    break;
                default:
                    if (!string.IsNullOrEmpty(seg.Text))
                    {
                        arr.Add(JsonValue.Create(seg.Text));
                    }
                    break;
            }
        }

        return arr;
    }

    private static int? FindMatchingIngredientIndex(
        string tokenName,
        IReadOnlyList<IngestionDraftIngredientDto> ingredients
    )
    {
        if (string.IsNullOrWhiteSpace(tokenName))
        {
            return null;
        }

        var trimmed = tokenName.Trim();
        for (var i = 0; i < ingredients.Count; i++)
        {
            var ing = ingredients[i];
            if (
                string.Equals(ing.DisplayName, trimmed, StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    ing.MatchedFoodItemName,
                    trimmed,
                    StringComparison.OrdinalIgnoreCase
                )
                || string.Equals(
                    ing.ProposedFoodItemName,
                    trimmed,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return i;
            }
        }

        for (var i = 0; i < ingredients.Count; i++)
        {
            var ing = ingredients[i];
            if (
                ing.DisplayName.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains(ing.DisplayName, StringComparison.OrdinalIgnoreCase)
            )
            {
                return i;
            }
        }

        return null;
    }

    private static (decimal Amount, decimal? MaxAmount, string Unit) ConvertTimerSecondsToAmount(
        int seconds,
        int? maxSeconds
    )
    {
        var max = maxSeconds is > 0 && maxSeconds > seconds ? maxSeconds : null;

        if (seconds >= 86400 && seconds % 86400 == 0 && (max is null || max.Value % 86400 == 0))
        {
            return (seconds / 86400m, max.HasValue ? max.Value / 86400m : null, "day");
        }
        if (seconds >= 3600 && seconds % 3600 == 0 && (max is null || max.Value % 3600 == 0))
        {
            return (seconds / 3600m, max.HasValue ? max.Value / 3600m : null, "hour");
        }
        if (seconds >= 60 && seconds % 60 == 0 && (max is null || max.Value % 60 == 0))
        {
            return (seconds / 60m, max.HasValue ? max.Value / 60m : null, "minute");
        }

        return (seconds, max, "second");
    }

    private static string FormatDimensionUnitFullName(DimensionUnit unit) =>
        unit switch
        {
            DimensionUnit.Inch => "inch",
            DimensionUnit.Foot => "foot",
            DimensionUnit.Yard => "yard",
            DimensionUnit.Mm => "millimeter",
            DimensionUnit.Cm => "centimeter",
            DimensionUnit.M => "meter",
            _ => "inch",
        };

    private static string? FormatMeasurementUnitString(
        MeasurementUnit? unit,
        string? customUnitName
    ) =>
        unit switch
        {
            null => null,
            MeasurementUnit.Custom => string.IsNullOrWhiteSpace(customUnitName)
                ? null
                : customUnitName.Trim(),
            MeasurementUnit.Tsp => "teaspoon",
            MeasurementUnit.Tbsp => "tablespoon",
            MeasurementUnit.FlOz => "fluid ounce",
            MeasurementUnit.Cup => "cup",
            MeasurementUnit.Pt => "pint",
            MeasurementUnit.Qt => "quart",
            MeasurementUnit.Gal => "gallon",
            MeasurementUnit.Ml => "milliliter",
            MeasurementUnit.L => "liter",
            MeasurementUnit.Oz => "ounce",
            MeasurementUnit.Lb => "pound",
            MeasurementUnit.G => "gram",
            MeasurementUnit.Kg => "kilogram",
            MeasurementUnit.Pinch => "pinch",
            MeasurementUnit.Dash => "dash",
            MeasurementUnit.Clove => "clove",
            MeasurementUnit.Can => "can",
            MeasurementUnit.Bunch => "bunch",
            MeasurementUnit.Slice => "slice",
            MeasurementUnit.Whole => null,
            _ => unit.Value.ToString().ToLowerInvariant(),
        };

    private sealed record BaselineStepState(
        IngestionDraftStepDto Step,
        string PlainText,
        ValidatedStage4StepResult Validated
    );
}

using App.Core.Recipes;

namespace App.Core.Ingestion;

/// <summary>
/// Computes a multi-signal ingestion confidence score in [0.10, 0.99] reflecting validation
/// errors, quality/measurement warnings, pipeline repair attempts, metadata completeness,
/// step-to-ingredient linking & reconciliation, catalog grounding, and section symmetry.
/// </summary>
public static class IngestionConfidenceScorer
{
    public static decimal Compute(IngestionExtractedDraftDto draft, int pipelineRepairCount = 0)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var effectiveRepairs =
            pipelineRepairCount > 0 ? pipelineRepairCount : draft.PipelineRepairCount;

        return Compute(
            description: draft.Description,
            baseServings: draft.BaseServings,
            hasExplicitServings: draft.BaseServings > 0m,
            yieldDisplay: draft.YieldDisplay,
            prepTimeMinutes: draft.PrepTimeMinutes,
            cookTimeMinutes: draft.CookTimeMinutes,
            totalTimeMinutes: draft.TotalTimeMinutes,
            ingredients: draft.Ingredients ?? [],
            steps: draft.Steps ?? [],
            validationIssues: draft.ValidationIssues ?? [],
            qualityWarnings: draft.QualityWarnings ?? [],
            measurementWarnings: draft.MeasurementWarnings ?? [],
            pipelineRepairCount: effectiveRepairs
        );
    }

    public static decimal Compute(
        string? description,
        decimal baseServings,
        bool hasExplicitServings,
        string? yieldDisplay,
        int? prepTimeMinutes,
        int? cookTimeMinutes,
        int? totalTimeMinutes,
        IReadOnlyList<IngestionDraftIngredientDto> ingredients,
        IReadOnlyList<IngestionDraftStepDto> steps,
        IReadOnlyList<string> validationIssues,
        IReadOnlyList<string> qualityWarnings,
        IReadOnlyList<string> measurementWarnings,
        int pipelineRepairCount = 0
    )
    {
        var score = 0.99m;

        // 1. Diagnostic & repair deductions
        score -= validationIssues.Count * 0.16m;
        score -= qualityWarnings.Count * 0.10m;
        score -= measurementWarnings.Count * 0.06m;
        score -= Math.Min(Math.Max(0, pipelineRepairCount), 4) * 0.01m;

        // 2. Metadata completeness deductions
        if (!hasExplicitServings || baseServings <= 0m)
        {
            score -= 0.02m;
        }
        if (string.IsNullOrWhiteSpace(yieldDisplay))
        {
            score -= 0.01m;
        }
        if (prepTimeMinutes is null && cookTimeMinutes is null && totalTimeMinutes is null)
        {
            score -= 0.02m;
        }
        if (string.IsNullOrWhiteSpace(description))
        {
            score -= 0.01m;
        }

        // 3. Step-to-ingredient coverage & quantity reconciliation
        if (ingredients.Count > 0 && steps.Count > 0)
        {
            var stepIngTokensByIndex = new Dictionary<int, List<StepIngredientToken>>();
            var unlinkedNonWaterTokenCount = 0;
            var plainCorpusParts = new List<string>(steps.Count);

            foreach (var step in steps)
            {
                plainCorpusParts.Add(
                    StepIngredientEnricher.NormalizeTextForReferenceScan(
                        StepInstructionParser.ToPlainText(step.Instruction)
                    )
                );

                foreach (
                    var tok in StepInstructionParser
                        .ExtractTokens(step.Instruction)
                        .OfType<StepIngredientToken>()
                )
                {
                    if (tok.IngredientIndex is int idx && idx >= 0 && idx < ingredients.Count)
                    {
                        if (!stepIngTokensByIndex.TryGetValue(idx, out var list))
                        {
                            list = [];
                            stepIngTokensByIndex[idx] = list;
                        }
                        list.Add(tok);
                    }
                    else if (!tok.IngredientId.HasValue)
                    {
                        var normTokName = StepIngredientEnricher.NormalizeTextForReferenceScan(
                            tok.Ingredient
                        );
                        var isIncidentalWaterOrIce =
                            normTokName.EndsWith("water", StringComparison.Ordinal)
                            || string.Equals(normTokName, "ice", StringComparison.Ordinal)
                            || normTokName.StartsWith("ice cube", StringComparison.Ordinal);
                        if (!isIncidentalWaterOrIce)
                        {
                            unlinkedNonWaterTokenCount++;
                        }
                    }
                }
            }

            var normalizedPlainCorpus = string.Join(' ', plainCorpusParts);

            var nonOptionalCount = 0;
            var referencedCount = 0;
            var quantifiedCount = 0;
            var reconciledCount = 0;

            for (var i = 0; i < ingredients.Count; i++)
            {
                var ing = ingredients[i];
                stepIngTokensByIndex.TryGetValue(i, out var linkedTokens);

                if (!ing.IsOptional)
                {
                    nonOptionalCount++;
                    var hasTokenRef = linkedTokens is { Count: > 0 };
                    if (
                        hasTokenRef
                        || StepIngredientEnricher.IsIngredientReferencedInText(
                            ing,
                            normalizedPlainCorpus
                        )
                    )
                    {
                        referencedCount++;
                    }
                }

                if (!ing.IsOptional && !ing.IsRecipeReference && ing.Quantity is > 0m)
                {
                    quantifiedCount++;
                    if (
                        linkedTokens is { Count: > 0 }
                        && StepIngredientEnricher.IsQuantifiedIngredientReconciled(
                            ing,
                            linkedTokens
                        )
                    )
                    {
                        reconciledCount++;
                    }
                }
            }

            if (nonOptionalCount > 0)
            {
                var refRatio = referencedCount / (decimal)nonOptionalCount;
                score -= (1m - refRatio) * 0.03m;
            }

            if (quantifiedCount > 0)
            {
                var recRatio = reconciledCount / (decimal)quantifiedCount;
                score -= (1m - recRatio) * 0.025m;
            }

            score -= Math.Min(unlinkedNonWaterTokenCount, 2) * 0.01m;

            // 4. Section symmetry
            if (steps.Count >= 2)
            {
                var hasIngSections = ingredients.Any(i =>
                    !string.IsNullOrWhiteSpace(i.SectionName)
                );
                var hasStepSections = steps.Any(s => !string.IsNullOrWhiteSpace(s.SectionName));
                if (hasIngSections != hasStepSections)
                {
                    score -= 0.01m;
                }
            }
        }

        // 5. Catalog grounding
        var foodIngredients = ingredients.Where(i => !i.IsRecipeReference).ToList();
        if (foodIngredients.Count > 0)
        {
            var newFoodCount = foodIngredients.Count(i =>
                i.IsNewFoodItem && !i.MatchedFoodItemId.HasValue
            );
            var newFoodRatio = newFoodCount / (decimal)foodIngredients.Count;
            score -= newFoodRatio * 0.015m;
        }

        return decimal.Round(Math.Clamp(score, 0.10m, 0.99m), 2, MidpointRounding.AwayFromZero);
    }
}

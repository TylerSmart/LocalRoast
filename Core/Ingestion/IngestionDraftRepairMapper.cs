using System.ComponentModel.DataAnnotations;
using System.Reflection;
using App.Core.Exceptions;
using App.Core.Recipes;

namespace App.Core.Ingestion;

public static class IngestionDraftRepairMapper
{
    public static LlmExtractedRecipePayload ToPayload(IngestionExtractedDraftDto draft)
    {
        if (
            (draft.Ingredients ?? []).Any(ingredient => ingredient is null)
            || (draft.Steps ?? []).Any(step =>
                step is null
                || (step.Timers ?? []).Any(timer => timer is null)
                || (step.IngredientAmounts ?? []).Any(amount =>
                    amount is null || amount.Measurement is null
                )
                || (step.InlineAmounts ?? []).Any(amount =>
                    amount is null || amount.Measurement is null
                )
            )
        )
        {
            throw new DomainValidationException(
                "The review draft contains an empty ingredient, step, timer, or ingredient amount."
            );
        }
        return new(
            Title: draft.Title,
            Description: draft.Description,
            BaseServings: draft.BaseServings,
            YieldDisplay: draft.YieldDisplay,
            PrepTimeMinutes: draft.PrepTimeMinutes,
            CookTimeMinutes: draft.CookTimeMinutes,
            TotalTimeMinutes: draft.TotalTimeMinutes,
            Difficulty: draft.Difficulty?.ToString(),
            SourceName: draft.SourceName,
            SourceAuthor: draft.SourceAuthor,
            ConfidenceScore: draft.ConfidenceScore,
            Ingredients: (draft.Ingredients ?? [])
                .Select(ingredient => new LlmExtractedIngredientItem(
                    SectionName: ingredient.SectionName,
                    Quantity: ingredient.Quantity,
                    MaxQuantity: ingredient.MaxQuantity,
                    Unit: ingredient.Unit?.ToString(),
                    CanonicalFoodName: ingredient.MatchedFoodItemName
                        ?? ingredient.ProposedFoodItemName
                        ?? ingredient.DisplayName,
                    DisplayName: ingredient.DisplayName,
                    PreparationNote: ingredient.PreparationNote,
                    IsOptional: ingredient.IsOptional,
                    OriginalRawText: ingredient.OriginalRawText,
                    SuggestedIsPantryStaple: ingredient.SuggestedIsPantryStaple,
                    AlternativeMeasurement: ToPayloadMeasurement(ingredient.AlternativeMeasurement),
                    SuggestedCategory: ingredient.SuggestedCategory?.ToString(),
                    IsRecipeReference: ingredient.IsRecipeReference,
                    ReferencedRecipeTitle: ingredient.ReferencedRecipeTitle,
                    CustomUnitName: ingredient.CustomUnitName
                ))
                .ToList(),
            Steps: (draft.Steps ?? [])
                .Select(step => new LlmExtractedStepItem(
                    SectionName: step.SectionName,
                    Instruction: step.Instruction,
                    IngredientIndices: step.IngredientIndices,
                    Timers: (step.Timers ?? [])
                        .Select(timer => new LlmExtractedTimerItem(
                            timer.DurationSeconds,
                            timer.Label
                        ))
                        .ToList(),
                    IngredientAmounts: (step.IngredientAmounts ?? [])
                        .Select(amount => new LlmExtractedStepIngredientAmountItem(
                            amount.IngredientIndex,
                            ToPayloadMeasurement(amount.Measurement)!
                        ))
                        .ToList(),
                    InlineAmounts: ToPayloadInlineAmounts(step)
                ))
                .ToList(),
            SourceBookTitle: draft.SourceBookTitle,
            SourceBookPage: draft.SourceBookPage,
            ExtractedModel: draft.ExtractedModel
        );
    }

    public static (
        string Title,
        IReadOnlyList<LlmExtractedIngredientItem> Ingredients,
        IReadOnlyList<LlmExtractedStepItem> Steps
    ) ValidateProposalStructure(LlmExtractedRecipePayload proposed)
    {
        if (
            string.IsNullOrWhiteSpace(proposed.Title)
            || proposed.Ingredients is not { Count: > 0 }
            || proposed.Steps is not { Count: > 0 }
            || proposed.Ingredients.Any(ingredient =>
                ingredient is null || string.IsNullOrWhiteSpace(ingredient.DisplayName)
            )
            || proposed.Steps.Any(step =>
                step is null
                || string.IsNullOrWhiteSpace(step.Instruction)
                || (step.Timers ?? []).Any(timer => timer is null)
                || (step.IngredientAmounts ?? []).Any(amount =>
                    amount is null || amount.Measurement is null
                )
            )
        )
        {
            throw new DomainValidationException(
                "The AI returned an incomplete recipe. Your review draft has not been changed."
            );
        }
        for (var stepIndex = 0; stepIndex < proposed.Steps.Count; stepIndex++)
        {
            var step = proposed.Steps[stepIndex];
            var invalidIndex = (step.IngredientIndices ?? [])
                .Select(index => (int?)index)
                .Concat((step.IngredientAmounts ?? []).Select(amount => amount.IngredientIndex))
                .FirstOrDefault(index =>
                    index is null or < 0 || index >= proposed.Ingredients.Count
                );
            if (
                invalidIndex is not null
                || (step.IngredientAmounts ?? []).Any(amount => amount.IngredientIndex is null)
            )
            {
                var invalidLabel = invalidIndex?.ToString() ?? "(missing)";
                throw new DomainValidationException(
                    $"Step {stepIndex + 1} uses ingredient index {invalidLabel}, but the final ingredient array has {proposed.Ingredients.Count} entries indexed 0 through {proposed.Ingredients.Count - 1}. Link an existing row or restore the missing ingredient."
                );
            }
            if (
                (step.IngredientAmounts ?? [])
                    .GroupBy(amount => amount.IngredientIndex)
                    .Any(group => group.Count() > 1)
            )
            {
                throw new DomainValidationException(
                    $"Step {stepIndex + 1} repeats an ingredient amount index. Use at most one amount per ingredient in each step."
                );
            }
        }
        return (proposed.Title, proposed.Ingredients, proposed.Steps);
    }

    public static IngestionExtractedDraftDto Merge(
        IngestionExtractedDraftDto original,
        LlmExtractedRecipePayload proposed,
        IReadOnlyList<CatalogFoodLookupItem> catalogFoods,
        string? source
    )
    {
        var (title, rawIngredients, rawSteps) = ValidateProposalStructure(proposed);

        var normalized = RecipeDraftNormalizer.Normalize(
            proposed,
            catalogFoods,
            [],
            original.SourceUrl,
            original.SourceName,
            original.SourceAuthor,
            original.CoverImagePath,
            original.ThumbnailImagePath,
            rawSourceText: source
        );
        var oldIngredients = original.Ingredients ?? [];
        var oldPayloadIngredients = ToPayload(original).Ingredients ?? [];
        var newIngredients = new List<IngestionDraftIngredientDto>();
        var measurementWarnings = (original.MeasurementWarnings ?? []).ToList();

        for (var index = 0; index < rawIngredients.Count; index++)
        {
            var raw = rawIngredients[index];
            var match = FindOriginalIngredient(raw, index, oldPayloadIngredients, oldIngredients);
            var previous = match.HasValue ? oldIngredients[match.Value] : null;
            var item = normalized.Ingredients![index];
            var (unit, customUnit) = RecipeDraftNormalizer.NormalizeUnit(
                raw.Unit,
                raw.CustomUnitName
            );
            var alternative =
                FromPayloadMeasurement(raw.AlternativeMeasurement)
                ?? previous?.AlternativeMeasurement;
            if (
                raw.AlternativeMeasurement is not null
                && alternative != previous?.AlternativeMeasurement
            )
            {
                measurementWarnings.Add(
                    $"{raw.DisplayName}: verify the AI-proposed alternative measurement against the source."
                );
            }
            var isReference = raw.IsRecipeReference || previous?.IsRecipeReference == true;

            item = item with
            {
                SectionName = raw.SectionName?.Trim(),
                Quantity = raw.Quantity,
                MaxQuantity = raw.MaxQuantity,
                Unit = unit,
                CustomUnitName = customUnit,
                DisplayName = raw.DisplayName!.Trim(),
                PreparationNote = raw.PreparationNote?.Trim(),
                OriginalRawText = raw.OriginalRawText?.Trim(),
                IsOptional = raw.IsOptional,
                AlternativeMeasurement = alternative,
                PreferAlternativeMeasurement =
                    alternative is not null && previous?.PreferAlternativeMeasurement == true,
                IsRecipeReference = isReference,
                ReferencedRecipeTitle = isReference
                    ? raw.ReferencedRecipeTitle ?? previous?.ReferencedRecipeTitle
                    : null,
            };
            if (previous is not null)
            {
                item = item with
                {
                    MatchedFoodItemId = previous.MatchedFoodItemId,
                    MatchedFoodItemName = previous.MatchedFoodItemName,
                    IsNewFoodItem = previous.IsNewFoodItem,
                    ProposedFoodItemName = previous.ProposedFoodItemName,
                    SuggestedIsPantryStaple = previous.SuggestedIsPantryStaple,
                    SuggestedCategory = previous.SuggestedCategory,
                    CreateNewFoodItem = previous.CreateNewFoodItem,
                };
            }
            if (isReference)
            {
                item = item with
                {
                    MatchedFoodItemId = null,
                    MatchedFoodItemName = null,
                    IsNewFoodItem = false,
                    ProposedFoodItemName = null,
                    SuggestedIsPantryStaple = false,
                    SuggestedCategory = null,
                    CreateNewFoodItem = false,
                };
            }
            Validate(item);
            newIngredients.Add(item);
        }

        var images = MatchStepImages(original.Steps ?? [], rawSteps);
        var newSteps = new List<IngestionDraftStepDto>();
        for (var index = 0; index < rawSteps.Count; index++)
        {
            var raw = rawSteps[index];
            var indices = (raw.IngredientIndices ?? []).Distinct().ToList();
            if (
                indices.Any(ingredientIndex =>
                    ingredientIndex < 0 || ingredientIndex >= newIngredients.Count
                )
            )
            {
                throw new DomainValidationException(
                    $"The AI returned an unknown ingredient index in step {index + 1}."
                );
            }
            var amounts = new List<RecipeStepIngredientAmountInput>();
            foreach (var amount in raw.IngredientAmounts ?? [])
            {
                if (
                    amount?.IngredientIndex is null
                    || amount.IngredientIndex < 0
                    || amount.IngredientIndex >= newIngredients.Count
                    || amount.Measurement is null
                    || amounts.Any(existing => existing.IngredientIndex == amount.IngredientIndex)
                )
                {
                    throw new DomainValidationException(
                        $"The AI returned an incomplete ingredient amount in step {index + 1}."
                    );
                }
                var measurement = FromPayloadMeasurement(amount.Measurement);
                if (measurement is null)
                {
                    throw new DomainValidationException(
                        $"The AI returned an incomplete ingredient amount in step {index + 1}."
                    );
                }
                amounts.Add(
                    new RecipeStepIngredientAmountInput(amount.IngredientIndex.Value, measurement)
                );
                if (!indices.Contains(amount.IngredientIndex.Value))
                {
                    indices.Add(amount.IngredientIndex.Value);
                }
            }
            var previousAmounts =
                index < (original.Steps?.Count ?? 0)
                    ? original.Steps![index].IngredientAmounts ?? []
                    : [];
            if (
                (amounts.Count > 0 || previousAmounts.Count > 0)
                && !amounts.SequenceEqual(previousAmounts)
            )
            {
                measurementWarnings.Add(
                    $"Step {index + 1}: verify AI-proposed ingredient portions against the source."
                );
            }
            var timers = (raw.Timers ?? [])
                .Select(timer => new RecipeStepTimerInput(timer.DurationSeconds, timer.Label ?? ""))
                .ToList();
            var instruction = raw.Instruction!.Trim();
            var inlineAmounts = RecipeDraftNormalizer.NormalizeInlineAmounts(
                raw.InlineAmounts,
                raw.Instruction.Length - raw.Instruction.TrimStart().Length,
                instruction,
                newIngredients.Count,
                measurementWarnings,
                $"Step {index + 1}"
            );
            foreach (var amount in inlineAmounts.Where(a => a.IngredientIndex is not null))
            {
                if (!indices.Contains(amount.IngredientIndex!.Value))
                {
                    indices.Add(amount.IngredientIndex.Value);
                }
            }
            var step = new IngestionDraftStepDto(
                SectionName: raw.SectionName?.Trim(),
                Instruction: instruction,
                IngredientIndices: indices,
                Timers: timers,
                ImagePath: images[index],
                IngredientAmounts: amounts,
                InlineAmounts: inlineAmounts
            );
            Validate(step);
            foreach (var timer in timers)
            {
                Validate(timer);
            }
            newSteps.Add(step);
        }

        RecipeDifficulty? difficulty = original.Difficulty;
        if (!string.IsNullOrWhiteSpace(proposed.Difficulty))
        {
            if (
                !Enum.TryParse<RecipeDifficulty>(proposed.Difficulty, true, out var parsed)
                || !Enum.IsDefined(parsed)
            )
            {
                throw new DomainValidationException(
                    "The AI returned an invalid recipe difficulty."
                );
            }
            difficulty = parsed;
        }

        var result = original with
        {
            Title = title.Trim(),
            Description = proposed.Description ?? original.Description,
            BaseServings = proposed.BaseServings ?? original.BaseServings,
            YieldDisplay = proposed.YieldDisplay ?? original.YieldDisplay,
            PrepTimeMinutes = proposed.PrepTimeMinutes ?? original.PrepTimeMinutes,
            CookTimeMinutes = proposed.CookTimeMinutes ?? original.CookTimeMinutes,
            TotalTimeMinutes = proposed.TotalTimeMinutes ?? original.TotalTimeMinutes,
            Difficulty = difficulty,
            SourceName = proposed.SourceName ?? original.SourceName,
            SourceAuthor = proposed.SourceAuthor ?? original.SourceAuthor,
            Ingredients = newIngredients,
            Steps = newSteps,
            MeasurementWarnings = measurementWarnings.Distinct(StringComparer.Ordinal).ToList(),
        };
        Validate(result);
        return result;
    }

    /// <summary>Draft offsets index the trimmed instruction; payload offsets index it verbatim.</summary>
    private static List<InlineStepAmount>? ToPayloadInlineAmounts(IngestionDraftStepDto step)
    {
        if (step.InlineAmounts is null)
        {
            return null;
        }
        var instruction = step.Instruction ?? "";
        var lead = instruction.Length - instruction.TrimStart().Length;
        return step
            .InlineAmounts.Select(amount =>
            {
                var start = amount.Start + lead;
                var text =
                    amount.Start >= 0
                    && amount.Length > 0
                    && start + amount.Length <= instruction.Length
                        ? instruction.Substring(start, amount.Length)
                        : "";
                var ingStart = amount.IngredientSpanStart.HasValue
                    ? amount.IngredientSpanStart.Value + lead
                    : (int?)null;
                return new InlineStepAmount(
                    start,
                    amount.Length,
                    text,
                    amount.Kind,
                    amount.IngredientIndex,
                    amount.Measurement,
                    amount.AlternativeMeasurement,
                    ingStart,
                    amount.IngredientSpanLength
                );
            })
            .ToList();
    }

    private static LlmExtractedMeasurementItem? ToPayloadMeasurement(
        RecipeMeasurement? measurement
    ) =>
        measurement is null
            ? null
            : new LlmExtractedMeasurementItem(
                measurement.Quantity,
                measurement.Unit == MeasurementUnit.Custom
                    ? measurement.CustomUnitName
                    : measurement.Unit?.ToString(),
                measurement.MaxQuantity
            );

    private static RecipeMeasurement? FromPayloadMeasurement(LlmExtractedMeasurementItem? raw)
    {
        if (raw is null)
        {
            return null;
        }
        var (unit, customUnit) = RecipeDraftNormalizer.NormalizeUnit(raw.Unit);
        var measurement = new RecipeMeasurement(raw.Quantity, unit, raw.MaxQuantity, customUnit);
        RecipeMeasurementValidation.Validate(measurement);
        return measurement;
    }

    private static int? FindOriginalIngredient(
        LlmExtractedIngredientItem item,
        int index,
        IReadOnlyList<LlmExtractedIngredientItem> original,
        IReadOnlyList<IngestionDraftIngredientDto> originalDraft
    )
    {
        static bool Same(string? left, string? right) =>
            !string.IsNullOrWhiteSpace(left)
            && string.Equals(left.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

        bool Matches(LlmExtractedIngredientItem previous) =>
            Same(previous.CanonicalFoodName, item.CanonicalFoodName)
            || (
                Same(previous.DisplayName, item.DisplayName)
                && (
                    string.IsNullOrWhiteSpace(item.CanonicalFoodName)
                    || Same(previous.CanonicalFoodName, item.CanonicalFoodName)
                )
            );

        var matches = Enumerable.Range(0, original.Count).Where(i => Matches(original[i])).ToList();
        if (matches.Count == 1)
        {
            return matches[0];
        }
        var exact = matches
            .Where(i =>
                original[i].Quantity == item.Quantity
                && original[i].MaxQuantity == item.MaxQuantity
                && string.Equals(
                    original[i].Unit?.Trim(),
                    item.Unit?.Trim(),
                    StringComparison.OrdinalIgnoreCase
                )
                && string.Equals(
                    original[i].OriginalRawText?.Trim(),
                    item.OriginalRawText?.Trim(),
                    StringComparison.OrdinalIgnoreCase
                )
                && string.Equals(
                    original[i].PreparationNote?.Trim(),
                    item.PreparationNote?.Trim(),
                    StringComparison.OrdinalIgnoreCase
                )
                && string.Equals(
                    original[i].SectionName?.Trim(),
                    item.SectionName?.Trim(),
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToList();
        if (exact.Count == 1)
        {
            return exact[0];
        }
        var candidates = exact.Count > 0 ? exact : matches;
        if (candidates.Count == 0)
        {
            return null;
        }
        if (
            candidates
                .Skip(1)
                .Any(candidate =>
                    !SamePreservedIngredientMetadata(
                        originalDraft[candidates[0]],
                        originalDraft[candidate]
                    )
                )
        )
        {
            throw new DomainValidationException(
                $"The AI changed duplicate ingredient rows for {item.DisplayName} in a way that cannot safely preserve their review settings. Your draft has not been changed."
            );
        }
        return candidates.Contains(index) ? index : candidates[0];
    }

    private static bool SamePreservedIngredientMetadata(
        IngestionDraftIngredientDto left,
        IngestionDraftIngredientDto right
    ) =>
        left.MatchedFoodItemId == right.MatchedFoodItemId
        && left.MatchedFoodItemName == right.MatchedFoodItemName
        && left.IsNewFoodItem == right.IsNewFoodItem
        && left.CreateNewFoodItem == right.CreateNewFoodItem
        && left.ProposedFoodItemName == right.ProposedFoodItemName
        && left.SuggestedIsPantryStaple == right.SuggestedIsPantryStaple
        && left.SuggestedCategory == right.SuggestedCategory
        && left.AlternativeMeasurement == right.AlternativeMeasurement
        && left.PreferAlternativeMeasurement == right.PreferAlternativeMeasurement
        && left.IsRecipeReference == right.IsRecipeReference
        && left.ReferencedRecipeTitle == right.ReferencedRecipeTitle;

    private static string?[] MatchStepImages(
        IReadOnlyList<IngestionDraftStepDto> original,
        IReadOnlyList<LlmExtractedStepItem> proposed
    )
    {
        var images = new string?[proposed.Count];
        var exactMatches = new Dictionary<int, int>();
        for (var index = 0; index < original.Count; index++)
        {
            var targets = Enumerable
                .Range(0, proposed.Count)
                .Where(i =>
                    string.Equals(
                        original[index].Instruction.Trim(),
                        proposed[i].Instruction?.Trim(),
                        StringComparison.Ordinal
                    )
                )
                .ToList();
            if (
                targets.Count == 1
                && original.Count(step =>
                    string.Equals(
                        step.Instruction.Trim(),
                        proposed[targets[0]].Instruction?.Trim(),
                        StringComparison.Ordinal
                    )
                ) == 1
            )
            {
                exactMatches[index] = targets[0];
            }
        }
        var remainingOriginal = Enumerable
            .Range(0, original.Count)
            .Except(exactMatches.Keys)
            .ToList();
        var remainingTargets = Enumerable
            .Range(0, proposed.Count)
            .Except(exactMatches.Values)
            .ToList();
        for (var index = 0; index < original.Count; index++)
        {
            var image = original[index].ImagePath;
            if (string.IsNullOrWhiteSpace(image))
            {
                continue;
            }
            var target =
                exactMatches.TryGetValue(index, out var exactTarget) ? exactTarget
                : remainingOriginal.Count == 1 && remainingTargets.Count == 1 ? remainingTargets[0]
                : -1;
            if (target < 0 || images[target] is not null)
            {
                throw new DomainValidationException(
                    "The AI rearranged a step with an attached image, so its image could not be preserved safely. Your draft has not been changed."
                );
            }
            images[target] = image;
        }
        return images;
    }

    private static void Validate(object value)
    {
        var type = value.GetType();
        var constructor = type.GetConstructors().Single();
        foreach (var parameter in constructor.GetParameters())
        {
            var property = type.GetProperty(parameter.Name!);
            if (property is null)
            {
                throw new InvalidOperationException(
                    $"Missing property for {type.Name}.{parameter.Name}."
                );
            }
            var context = new ValidationContext(value) { MemberName = property.Name };
            foreach (var attribute in parameter.GetCustomAttributes<ValidationAttribute>())
            {
                var failure = attribute.GetValidationResult(property.GetValue(value), context);
                if (failure != ValidationResult.Success)
                {
                    throw new DomainValidationException(
                        $"The AI returned an invalid recipe field: {failure!.ErrorMessage}"
                    );
                }
            }
        }
    }
}

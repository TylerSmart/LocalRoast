using System.Globalization;
using System.Text.RegularExpressions;
using App.Core.Recipes;

namespace App.Core.Ingestion;

public record CatalogFoodLookupItem(
    Guid Id,
    string Name,
    string NormalizedName,
    Guid? DefaultStoreAisleId,
    bool IsPantryStaple,
    IReadOnlyList<string> NormalizedAliases
);

public record CatalogAisleLookupItem(Guid Id, string Name, string NormalizedName);

public record CatalogTagLookupItem(Guid Id, string Name, string NormalizedName, string Slug);

public static class RecipeDraftNormalizer
{
    public static (MeasurementUnit? Unit, string? CustomUnitName) NormalizeUnit(string? rawUnit)
    {
        if (string.IsNullOrWhiteSpace(rawUnit))
        {
            return (null, null);
        }

        var trimmed = rawUnit.Trim();
        var lower = trimmed.ToLowerInvariant();

        return lower switch
        {
            "tsp" or "teaspoon" or "teaspoons" or "t" => (MeasurementUnit.Tsp, null),
            "tbsp" or "tablespoon" or "tablespoons" or "tb" => (MeasurementUnit.Tbsp, null),
            "fl oz" or "fl_oz" or "floz" or "fluid ounce" or "fluid ounces" => (
                MeasurementUnit.FlOz,
                null
            ),
            "cup" or "cups" or "c" => (MeasurementUnit.Cup, null),
            "pt" or "pint" or "pints" => (MeasurementUnit.Pt, null),
            "qt" or "quart" or "quarts" => (MeasurementUnit.Qt, null),
            "gal" or "gallon" or "gallons" => (MeasurementUnit.Gal, null),
            "ml" or "milliliter" or "milliliters" => (MeasurementUnit.Ml, null),
            "l" or "liter" or "liters" => (MeasurementUnit.L, null),
            "oz" or "ounce" or "ounces" => (MeasurementUnit.Oz, null),
            "lb" or "lbs" or "pound" or "pounds" => (MeasurementUnit.Lb, null),
            "g" or "gram" or "grams" => (MeasurementUnit.G, null),
            "kg" or "kilogram" or "kilograms" => (MeasurementUnit.Kg, null),
            "pinch" or "pinches" => (MeasurementUnit.Pinch, null),
            "dash" or "dashes" => (MeasurementUnit.Dash, null),
            "clove" or "cloves" => (MeasurementUnit.Clove, null),
            "can" or "cans" => (MeasurementUnit.Can, null),
            "bunch" or "bunches" => (MeasurementUnit.Bunch, null),
            "slice" or "slices" => (MeasurementUnit.Slice, null),
            "whole" or "large" or "medium" or "small" => (MeasurementUnit.Whole, null),
            _ => (MeasurementUnit.Custom, trimmed[..Math.Min(50, trimmed.Length)]),
        };
    }

    public static IngestionExtractedDraftDto Normalize(
        LlmExtractedRecipePayload raw,
        IReadOnlyList<CatalogFoodLookupItem> catalogFoods,
        IReadOnlyList<CatalogAisleLookupItem> aisles,
        IReadOnlyList<CatalogTagLookupItem> tags,
        string? sourceUrl,
        string? fallbackSourceName,
        string? fallbackSourceAuthor,
        string? coverImagePath,
        string? thumbnailImagePath
    )
    {
        var title = string.IsNullOrWhiteSpace(raw.Title) ? "Untitled Recipe" : raw.Title.Trim();
        var baseServings = raw.BaseServings is >= 0.25m and <= 999m ? raw.BaseServings.Value : 4m;

        RecipeDifficulty? difficulty = null;
        if (
            !string.IsNullOrWhiteSpace(raw.Difficulty)
            && Enum.TryParse<RecipeDifficulty>(raw.Difficulty.Trim(), true, out var parsedDiff)
        )
        {
            difficulty = parsedDiff;
        }

        var prepTime = raw.PrepTimeMinutes is >= 0 and <= 10080 ? raw.PrepTimeMinutes : null;
        var cookTime = raw.CookTimeMinutes is >= 0 and <= 10080 ? raw.CookTimeMinutes : null;
        var totalTime = raw.TotalTimeMinutes is >= 0 and <= 10080 ? raw.TotalTimeMinutes : null;
        if (totalTime == null && (prepTime.HasValue || cookTime.HasValue))
        {
            totalTime = (prepTime ?? 0) + (cookTime ?? 0);
        }

        var sourceName = !string.IsNullOrWhiteSpace(raw.SourceName)
            ? raw.SourceName.Trim()
            : fallbackSourceName;
        var sourceAuthor = !string.IsNullOrWhiteSpace(raw.SourceAuthor)
            ? raw.SourceAuthor.Trim()
            : fallbackSourceAuthor;

        // Find "Other" aisle as fallback
        var otherAisle = aisles.FirstOrDefault(a => a.NormalizedName == "other");

        var normalizedIngredients = new List<IngestionDraftIngredientDto>();
        var rawIngredients = raw.Ingredients ?? [];

        foreach (var rawIng in rawIngredients)
        {
            var (unit, customUnit) = NormalizeUnit(rawIng.Unit);
            var displayName = string.IsNullOrWhiteSpace(rawIng.DisplayName)
                ? "Ingredient"
                : rawIng.DisplayName.Trim();
            var candidateFoodName = !string.IsNullOrWhiteSpace(rawIng.CanonicalFoodName)
                ? rawIng.CanonicalFoodName.Trim()
                : displayName;

            var match = FindMatchingFoodItem(candidateFoodName, catalogFoods);

            if (match is not null)
            {
                normalizedIngredients.Add(
                    new IngestionDraftIngredientDto(
                        SectionName: string.IsNullOrWhiteSpace(rawIng.SectionName)
                            ? null
                            : rawIng.SectionName.Trim(),
                        Quantity: rawIng.Quantity,
                        MaxQuantity: rawIng.MaxQuantity,
                        Unit: unit,
                        CustomUnitName: customUnit,
                        MatchedFoodItemId: match.Id,
                        MatchedFoodItemName: match.Name,
                        IsNewFoodItem: false,
                        ProposedFoodItemName: null,
                        SuggestedStoreAisleId: match.DefaultStoreAisleId,
                        SuggestedIsPantryStaple: match.IsPantryStaple,
                        CreateNewFoodItem: false,
                        DisplayName: displayName,
                        PreparationNote: string.IsNullOrWhiteSpace(rawIng.PreparationNote)
                            ? null
                            : rawIng.PreparationNote.Trim(),
                        IsOptional: rawIng.IsOptional,
                        OriginalRawText: string.IsNullOrWhiteSpace(rawIng.OriginalRawText)
                            ? null
                            : rawIng.OriginalRawText.Trim()
                    )
                );
            }
            else
            {
                var proposedName = ToTitleCase(candidateFoodName);
                var suggestedAisleId =
                    ResolveSuggestedAisle(rawIng.SuggestedAisleName, aisles) ?? otherAisle?.Id;

                normalizedIngredients.Add(
                    new IngestionDraftIngredientDto(
                        SectionName: string.IsNullOrWhiteSpace(rawIng.SectionName)
                            ? null
                            : rawIng.SectionName.Trim(),
                        Quantity: rawIng.Quantity,
                        MaxQuantity: rawIng.MaxQuantity,
                        Unit: unit,
                        CustomUnitName: customUnit,
                        MatchedFoodItemId: null,
                        MatchedFoodItemName: null,
                        IsNewFoodItem: true,
                        ProposedFoodItemName: proposedName,
                        SuggestedStoreAisleId: suggestedAisleId,
                        SuggestedIsPantryStaple: rawIng.SuggestedIsPantryStaple,
                        CreateNewFoodItem: true,
                        DisplayName: displayName,
                        PreparationNote: string.IsNullOrWhiteSpace(rawIng.PreparationNote)
                            ? null
                            : rawIng.PreparationNote.Trim(),
                        IsOptional: rawIng.IsOptional,
                        OriginalRawText: string.IsNullOrWhiteSpace(rawIng.OriginalRawText)
                            ? null
                            : rawIng.OriginalRawText.Trim()
                    )
                );
            }
        }

        var normalizedSteps = new List<IngestionDraftStepDto>();
        var rawSteps = raw.Steps ?? [];
        var ingredientCount = normalizedIngredients.Count;

        for (var i = 0; i < rawSteps.Count; i++)
        {
            var rawStep = rawSteps[i];
            var instruction = string.IsNullOrWhiteSpace(rawStep.Instruction)
                ? ""
                : rawStep.Instruction.Trim();
            if (string.IsNullOrWhiteSpace(instruction))
            {
                continue;
            }

            // Valid ingredient indices
            var validIndices = (rawStep.IngredientIndices ?? [])
                .Where(idx => idx >= 0 && idx < ingredientCount)
                .Distinct()
                .OrderBy(idx => idx)
                .ToList();

            // Timers: LLM provided vs StepTimerExtractor detection
            var timers = new List<RecipeStepTimerInput>();
            if (rawStep.Timers is { Count: > 0 })
            {
                foreach (var t in rawStep.Timers)
                {
                    if (t.DurationSeconds >= 1 && !string.IsNullOrWhiteSpace(t.Label))
                    {
                        timers.Add(new RecipeStepTimerInput(t.DurationSeconds, t.Label.Trim()));
                    }
                }
            }

            if (timers.Count == 0)
            {
                timers.AddRange(StepTimerExtractor.DetectTimers(instruction, i + 1));
            }

            normalizedSteps.Add(
                new IngestionDraftStepDto(
                    SectionName: string.IsNullOrWhiteSpace(rawStep.SectionName)
                        ? null
                        : rawStep.SectionName.Trim(),
                    Instruction: instruction,
                    IngredientIndices: validIndices,
                    Timers: timers,
                    ImagePath: null
                )
            );
        }

        // Suggested tags
        var suggestedTagIds = new List<Guid>();
        if (raw.SuggestedTagNames is { Count: > 0 })
        {
            foreach (var tagName in raw.SuggestedTagNames)
            {
                var normTagName = tagName.Trim().ToLowerInvariant();
                var matchedTag = tags.FirstOrDefault(t =>
                    t.NormalizedName == normTagName
                    || t.Slug.Equals(normTagName, StringComparison.OrdinalIgnoreCase)
                    || t.Slug.EndsWith($"-{normTagName}", StringComparison.OrdinalIgnoreCase)
                );

                if (matchedTag is not null && !suggestedTagIds.Contains(matchedTag.Id))
                {
                    suggestedTagIds.Add(matchedTag.Id);
                }
            }
        }

        var confidence = raw.ConfidenceScore is >= 0.0m and <= 1.0m
            ? raw.ConfidenceScore.Value
            : 0.8m;

        return new IngestionExtractedDraftDto(
            Title: title,
            Description: string.IsNullOrWhiteSpace(raw.Description) ? null : raw.Description.Trim(),
            BaseServings: baseServings,
            YieldDisplay: string.IsNullOrWhiteSpace(raw.YieldDisplay)
                ? null
                : raw.YieldDisplay.Trim(),
            PrepTimeMinutes: prepTime,
            CookTimeMinutes: cookTime,
            TotalTimeMinutes: totalTime,
            Difficulty: difficulty,
            SourceUrl: string.IsNullOrWhiteSpace(sourceUrl) ? null : sourceUrl.Trim(),
            SourceName: string.IsNullOrWhiteSpace(sourceName) ? null : sourceName.Trim(),
            SourceAuthor: string.IsNullOrWhiteSpace(sourceAuthor) ? null : sourceAuthor.Trim(),
            CoverImagePath: coverImagePath,
            ThumbnailImagePath: thumbnailImagePath,
            SuggestedCollectionIds: [],
            SuggestedTagIds: suggestedTagIds,
            Ingredients: normalizedIngredients,
            Steps: normalizedSteps,
            ConfidenceScore: confidence
        );
    }

    private static CatalogFoodLookupItem? FindMatchingFoodItem(
        string rawName,
        IReadOnlyList<CatalogFoodLookupItem> catalogFoods
    )
    {
        var norm = rawName.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(norm))
        {
            return null;
        }

        // 1. Direct match
        var direct = catalogFoods.FirstOrDefault(f =>
            f.NormalizedName == norm || f.NormalizedAliases.Contains(norm)
        );
        if (direct is not null)
        {
            return direct;
        }

        // 2. Singularize
        var singular = Singularize(norm);
        if (singular != norm)
        {
            var singMatch = catalogFoods.FirstOrDefault(f =>
                f.NormalizedName == singular || f.NormalizedAliases.Contains(singular)
            );
            if (singMatch is not null)
            {
                return singMatch;
            }
        }

        return null;
    }

    private static string Singularize(string word)
    {
        if (word.EndsWith("ies") && word.Length > 4)
        {
            return word[..^3] + "y";
        }
        if (
            (
                word.EndsWith("oes")
                || word.EndsWith("ses")
                || word.EndsWith("xes")
                || word.EndsWith("ches")
                || word.EndsWith("shes")
            )
            && word.Length > 4
        )
        {
            return word[..^2];
        }
        if (word.EndsWith('s') && !word.EndsWith("ss") && word.Length > 3)
        {
            return word[..^1];
        }
        return word;
    }

    private static Guid? ResolveSuggestedAisle(
        string? suggestedAisleName,
        IReadOnlyList<CatalogAisleLookupItem> aisles
    )
    {
        if (string.IsNullOrWhiteSpace(suggestedAisleName))
        {
            return null;
        }

        var norm = suggestedAisleName.Trim().ToLowerInvariant();
        var direct = aisles.FirstOrDefault(a =>
            a.NormalizedName == norm
            || a.NormalizedName.Contains(norm)
            || norm.Contains(a.NormalizedName)
        );
        return direct?.Id;
    }

    private static string ToTitleCase(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var words = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i].ToLowerInvariant();
            words[i] = char.ToUpperInvariant(w[0]) + w[1..];
        }
        return string.Join(" ", words);
    }
}

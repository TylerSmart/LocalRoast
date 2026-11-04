using System.Globalization;
using System.Text.RegularExpressions;
using App.Core.FoodCatalog;
using App.Core.Recipes;

namespace App.Core.Ingestion;

public record CatalogFoodLookupItem(
    Guid Id,
    string Name,
    string NormalizedName,
    bool IsPantryStaple,
    IReadOnlyList<string> NormalizedAliases
);

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

    public static (decimal? Quantity, decimal? MaxQuantity) NormalizeQuantities(
        decimal? rawQuantity,
        decimal? rawMaxQuantity
    )
    {
        var quantity = rawQuantity is > 0m ? rawQuantity : null;
        var maxQuantity = rawMaxQuantity is > 0m ? rawMaxQuantity : null;

        if (maxQuantity.HasValue && !quantity.HasValue)
        {
            quantity = maxQuantity;
            maxQuantity = null;
        }
        else if (maxQuantity.HasValue && quantity.HasValue)
        {
            if (maxQuantity.Value < quantity.Value)
            {
                (quantity, maxQuantity) = (maxQuantity, quantity);
            }
            else if (maxQuantity.Value == quantity.Value)
            {
                maxQuantity = null;
            }
        }

        return (quantity, maxQuantity);
    }

    private static readonly Regex WhyThisRecipeWorksPrefixRegex = new(
        @"^\s*(?:why\s+this\s+recipe\s+works|why\s+it\s+works)\s*[:\-\u2013\u2014.]*\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex AtkHeaderMatchRegex = new(
        @"(?:^|\r?\n)\s*(?:why\s+this\s+recipe\s+works|why\s+it\s+works)\s*[:\-\u2013\u2014.]*\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex AtkTerminatorRegex = new(
        @"(?:\r?\n)\s*(?:"
            + @"(?:SERVES|Serves|MAKES|Makes|YIELD|Yield)\b"
            + @"|(?:BEFORE\s+YOU\s+BEGIN|Before\s+You\s+Begin)\b"
            + @"|(?:TOTAL\s+TIME|Total\s+Time|PREP\s+TIME|Prep\s+Time|COOK\s+TIME|Cook\s+Time|TIME)\b"
            + @"|(?:INGREDIENTS|Ingredients)\b"
            + @"|(?:INSTRUCTIONS|Instructions|DIRECTIONS|Directions)\b"
            + @"|(?:NOTES?|Notes?|NOTE)\b"
            + @"|\d+(?:[\/\.]\d+)?\s*(?:cups?|tbsp|tablespoons?|tsp|teaspoons?|pounds?|lbs?|oz|ounces?|cloves?|cans?|bunch(?:es)?|slices?|stalks?)\b"
            + @")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    public static string? NormalizeDescription(string? rawDescription)
    {
        if (string.IsNullOrWhiteSpace(rawDescription))
        {
            return null;
        }

        var cleaned = rawDescription.Trim();

        while (WhyThisRecipeWorksPrefixRegex.IsMatch(cleaned))
        {
            cleaned = WhyThisRecipeWorksPrefixRegex.Replace(cleaned, "").TrimStart();
        }

        cleaned = cleaned.TrimStart(':', '-', '–', '—', '.', ' ', '\t', '\r', '\n').TrimEnd();

        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    public static string? TryExtractAtkHeadnote(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return null;
        }

        var headerMatch = AtkHeaderMatchRegex.Match(rawText);
        if (!headerMatch.Success)
        {
            return null;
        }

        var startIndex = headerMatch.Index + headerMatch.Length;
        if (startIndex >= rawText.Length)
        {
            return null;
        }

        var remaining = rawText[startIndex..];
        var terminatorMatch = AtkTerminatorRegex.Match(remaining);

        var candidate = terminatorMatch.Success ? remaining[..terminatorMatch.Index] : remaining;

        var cleaned = NormalizeDescription(candidate);
        return string.IsNullOrWhiteSpace(cleaned) || cleaned.Length < 15 ? null : cleaned;
    }

    public static IngestionExtractedDraftDto Normalize(
        LlmExtractedRecipePayload raw,
        IReadOnlyList<CatalogFoodLookupItem> catalogFoods,
        IReadOnlyList<CatalogTagLookupItem> tags,
        string? sourceUrl,
        string? fallbackSourceName,
        string? fallbackSourceAuthor,
        string? coverImagePath,
        string? thumbnailImagePath,
        string? fallbackSourceBookTitle = null,
        int? fallbackSourceBookPage = null,
        bool retainSourceAttachments = true,
        string? extractedModel = null,
        IReadOnlyList<Guid>? fallbackCollectionIds = null,
        string? rawSourceText = null
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

        var sourceBookTitle = !string.IsNullOrWhiteSpace(raw.SourceBookTitle)
            ? raw.SourceBookTitle.Trim()
            : fallbackSourceBookTitle;
        var sourceBookPage = raw.SourceBookPage is >= 1 and <= 10000
            ? raw.SourceBookPage
            : fallbackSourceBookPage;

        var normalizedIngredients = new List<IngestionDraftIngredientDto>();
        var rawIngredients = raw.Ingredients ?? [];

        foreach (var rawIng in rawIngredients)
        {
            var (unit, customUnit) = NormalizeUnit(rawIng.Unit);
            var (cleanQuantity, cleanMaxQuantity) = NormalizeQuantities(
                rawIng.Quantity,
                rawIng.MaxQuantity
            );
            var cleanCanonical = FoodNameSanitizer.SanitizeFoodName(rawIng.CanonicalFoodName);
            var cleanDisplayName = FoodNameSanitizer.SanitizeFoodName(rawIng.DisplayName);
            var displayName = !string.IsNullOrWhiteSpace(rawIng.DisplayName)
                ? rawIng.DisplayName.Trim()
                : "Ingredient";
            var candidateFoodName = !string.IsNullOrWhiteSpace(rawIng.CanonicalFoodName)
                ? cleanCanonical
                : cleanDisplayName;

            CatalogFoodLookupItem? match = null;
            if (!string.IsNullOrWhiteSpace(candidateFoodName))
            {
                match = FindMatchingFoodItem(candidateFoodName, catalogFoods);
            }

            if (match is not null)
            {
                normalizedIngredients.Add(
                    new IngestionDraftIngredientDto(
                        SectionName: string.IsNullOrWhiteSpace(rawIng.SectionName)
                            ? null
                            : rawIng.SectionName.Trim(),
                        Quantity: cleanQuantity,
                        MaxQuantity: cleanMaxQuantity,
                        Unit: unit,
                        CustomUnitName: customUnit,
                        MatchedFoodItemId: match.Id,
                        MatchedFoodItemName: match.Name,
                        IsNewFoodItem: false,
                        ProposedFoodItemName: null,
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
            else if (!string.IsNullOrWhiteSpace(candidateFoodName))
            {
                var proposedName = ToTitleCase(candidateFoodName);

                normalizedIngredients.Add(
                    new IngestionDraftIngredientDto(
                        SectionName: string.IsNullOrWhiteSpace(rawIng.SectionName)
                            ? null
                            : rawIng.SectionName.Trim(),
                        Quantity: cleanQuantity,
                        MaxQuantity: cleanMaxQuantity,
                        Unit: unit,
                        CustomUnitName: customUnit,
                        MatchedFoodItemId: null,
                        MatchedFoodItemName: null,
                        IsNewFoodItem: true,
                        ProposedFoodItemName: proposedName,
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
            else
            {
                normalizedIngredients.Add(
                    new IngestionDraftIngredientDto(
                        SectionName: string.IsNullOrWhiteSpace(rawIng.SectionName)
                            ? null
                            : rawIng.SectionName.Trim(),
                        Quantity: cleanQuantity,
                        MaxQuantity: cleanMaxQuantity,
                        Unit: unit,
                        CustomUnitName: customUnit,
                        MatchedFoodItemId: null,
                        MatchedFoodItemName: null,
                        IsNewFoodItem: false,
                        ProposedFoodItemName: null,
                        SuggestedIsPantryStaple: false,
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

        // Suggested & proposed tags
        var suggestedTagIds = new List<Guid>();
        var proposedTagNames = new List<string>();
        var seenProposedTagKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (raw.SuggestedTagNames is { Count: > 0 })
        {
            foreach (var rawTag in raw.SuggestedTagNames)
            {
                if (string.IsNullOrWhiteSpace(rawTag))
                {
                    continue;
                }

                var cleanedTag = rawTag.Trim().TrimStart('#').Trim();
                if (string.IsNullOrWhiteSpace(cleanedTag) || cleanedTag.Length > 80)
                {
                    continue;
                }

                var normTagName = cleanedTag.ToLowerInvariant();
                var singularTag = Singularize(normTagName);

                var matchedTag = tags.FirstOrDefault(t =>
                    t.NormalizedName == normTagName
                    || Singularize(t.NormalizedName) == singularTag
                    || t.Slug.Equals(normTagName, StringComparison.OrdinalIgnoreCase)
                    || t.Slug.EndsWith($"-{normTagName}", StringComparison.OrdinalIgnoreCase)
                );

                if (matchedTag is not null)
                {
                    if (!suggestedTagIds.Contains(matchedTag.Id))
                    {
                        suggestedTagIds.Add(matchedTag.Id);
                    }
                }
                else if (seenProposedTagKeys.Add(singularTag))
                {
                    var displayTag = ToTagDisplayCase(cleanedTag);
                    if (!string.IsNullOrWhiteSpace(displayTag))
                    {
                        proposedTagNames.Add(displayTag);
                    }
                }
            }
        }

        var suggestedCollectionIds = fallbackCollectionIds is { Count: > 0 }
            ? fallbackCollectionIds.Distinct().ToList()
            : [];

        var confidence = raw.ConfidenceScore switch
        {
            >= 0.0m and <= 1.0m => raw.ConfidenceScore.Value,
            > 1.0m and <= 100.0m => Math.Round(raw.ConfidenceScore.Value / 100.0m, 2),
            _ => 0.8m,
        };
        if (normalizedIngredients.Count == 0 || normalizedSteps.Count == 0)
        {
            confidence = Math.Min(confidence, 0.25m);
        } //

        var normalizedDescription = NormalizeDescription(raw.Description);
        var atkHeadnote = TryExtractAtkHeadnote(rawSourceText);
        if (!string.IsNullOrWhiteSpace(atkHeadnote))
        {
            if (
                string.IsNullOrWhiteSpace(normalizedDescription)
                || atkHeadnote.Length > (normalizedDescription.Length * 1.5)
            )
            {
                normalizedDescription = atkHeadnote;
            }
        }

        return new IngestionExtractedDraftDto(
            Title: title,
            Description: normalizedDescription,
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
            SuggestedCollectionIds: suggestedCollectionIds,
            SuggestedTagIds: suggestedTagIds,
            Ingredients: normalizedIngredients,
            Steps: normalizedSteps,
            ConfidenceScore: confidence,
            SourceBookTitle: sourceBookTitle,
            SourceBookPage: sourceBookPage,
            RetainSourceAttachments: retainSourceAttachments,
            ExtractedModel: extractedModel ?? raw.ExtractedModel,
            ProposedTagNames: proposedTagNames
        );
    }

    private static CatalogFoodLookupItem? FindMatchingFoodItem(
        string rawName,
        IReadOnlyList<CatalogFoodLookupItem> catalogFoods
    )
    {
        if (string.IsNullOrWhiteSpace(rawName) || !FoodNameSanitizer.IsValidFoodName(rawName))
        {
            return null;
        }

        var norm = rawName.Trim().ToLowerInvariant();
        var validCatalogFoods = catalogFoods
            .Where(f =>
                FoodNameSanitizer.IsValidFoodName(f.NormalizedName)
                && FoodNameSanitizer.IsValidFoodName(f.Name)
            )
            .ToList();

        // 1. Direct match
        var direct = validCatalogFoods.FirstOrDefault(f =>
            f.NormalizedName == norm || f.NormalizedAliases.Contains(norm)
        );
        if (direct is not null)
        {
            return direct;
        }

        // 2. Bidirectional singularized match
        var singular = Singularize(norm);
        var singMatch = validCatalogFoods.FirstOrDefault(f =>
            Singularize(f.NormalizedName) == singular
            || f.NormalizedAliases.Any(a => Singularize(a) == singular)
        );
        if (singMatch is not null)
        {
            return singMatch;
        }

        return null;
    }

    public static string Singularize(string word)
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

    public static string ToTagDisplayCase(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var words = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var segments = words[i].Split('-', StringSplitOptions.RemoveEmptyEntries);
            for (var j = 0; j < segments.Length; j++)
            {
                var s = segments[j].ToLowerInvariant();
                segments[j] = char.ToUpperInvariant(s[0]) + s[1..];
            }
            words[i] = string.Join('-', segments);
        }
        var joined = string.Join(' ', words);
        return joined.Length > 80 ? joined[..80].Trim() : joined;
    }

    public static TagCategory InferTagCategory(string tagName)
    {
        var norm = Singularize(tagName.Trim().ToLowerInvariant());

        return norm switch
        {
            "breakfast"
            or "brunch"
            or "lunch"
            or "dinner"
            or "main"
            or "main dish"
            or "entree"
            or "side"
            or "side dish"
            or "appetizer"
            or "starter"
            or "snack"
            or "dessert"
            or "beverage"
            or "drink"
            or "cocktail"
            or "soup"
            or "stew"
            or "salad"
            or "sandwich"
            or "bread"
            or "sauce"
            or "condiment"
            or "dip"
            or "dressing" => TagCategory.Course,

            "american"
            or "southern"
            or "cajun"
            or "creole"
            or "tex-mex"
            or "mexican"
            or "latin"
            or "caribbean"
            or "italian"
            or "french"
            or "spanish"
            or "greek"
            or "mediterranean"
            or "middle eastern"
            or "moroccan"
            or "indian"
            or "thai"
            or "vietnamese"
            or "chinese"
            or "japanese"
            or "korean"
            or "asian"
            or "german"
            or "british"
            or "irish" => TagCategory.Cuisine,

            "vegetarian"
            or "vegan"
            or "gluten-free"
            or "gluten free"
            or "dairy-free"
            or "dairy free"
            or "nut-free"
            or "nut free"
            or "egg-free"
            or "low-carb"
            or "low carb"
            or "keto"
            or "paleo"
            or "whole30"
            or "high-protein"
            or "healthy" => TagCategory.Dietary,

            "slow cooker"
            or "instant pot"
            or "pressure cooker"
            or "air fryer"
            or "grill"
            or "grilling"
            or "smoker"
            or "dutch oven"
            or "sheet pan"
            or "one-pot"
            or "one pot"
            or "skillet"
            or "cast iron"
            or "wok"
            or "sous vide"
            or "stand mixer"
            or "food processor"
            or "blender"
            or "no-cook" => TagCategory.Equipment,

            _ => TagCategory.Custom,
        };
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

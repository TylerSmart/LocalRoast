using System.Globalization;
using System.Text.RegularExpressions;
using App.Core.Exceptions;
using App.Core.FoodCatalog;
using App.Core.Recipes;

namespace App.Core.Ingestion;

public record CatalogFoodLookupItem(
    Guid Id,
    string Name,
    string NormalizedName,
    bool IsPantryStaple,
    IReadOnlyList<string> NormalizedAliases,
    FoodCategory? Category = null
);

public record CatalogTagLookupItem(Guid Id, string Name, string NormalizedName, string Slug);

public static class RecipeDraftNormalizer
{
    private static readonly Regex SubStepPrefix = new(
        @"^\s*(?:step\s+)?\d+[A-Za-z][.):]\s+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    /// <summary>
    /// Maps tagged spans onto the trimmed instruction. Spans that fall outside the text or
    /// overlap an earlier span are dropped; an ingredient span without a valid row becomes Fixed.
    /// </summary>
    public static List<RecipeStepInlineAmountInput> NormalizeInlineAmounts(
        IReadOnlyList<InlineStepAmount>? raw,
        int leadingTrim,
        string instruction,
        int ingredientCount,
        ICollection<string> warnings,
        string label
    )
    {
        var result = new List<RecipeStepInlineAmountInput>();
        var end = 0;
        foreach (var amount in (raw ?? []).OrderBy(a => a.Start))
        {
            var start = amount.Start - leadingTrim;
            if (
                start < end
                || amount.Length is < 1 or > 200
                || start + amount.Length > instruction.Length
                || !IsValidMeasurement(amount.Measurement)
            )
            {
                warnings.Add(
                    $"{label}: an inline amount did not match the step text and was omitted."
                );
                continue;
            }
            var validIndex =
                amount.IngredientIndex is { } index && index >= 0 && index < ingredientCount;
            var kind =
                amount.Kind == InlineStepAmountKind.Ingredient && !validIndex
                    ? InlineStepAmountKind.Fixed
                    : amount.Kind;
            result.Add(
                new RecipeStepInlineAmountInput(
                    start,
                    amount.Length,
                    kind,
                    kind == InlineStepAmountKind.Ingredient ? amount.IngredientIndex : null,
                    amount.Measurement,
                    IsValidMeasurement(amount.AlternativeMeasurement)
                        ? amount.AlternativeMeasurement
                        : null
                )
            );
            end = start + amount.Length;
        }
        return result;
    }

    private static bool IsValidMeasurement(RecipeMeasurement? measurement)
    {
        if (measurement is null)
        {
            return false;
        }
        try
        {
            RecipeMeasurementValidation.Validate(measurement);
            return true;
        }
        catch (DomainValidationException)
        {
            return false;
        }
    }

    private static RecipeMeasurement? NormalizeMeasurement(
        LlmExtractedMeasurementItem? raw,
        ICollection<string> warnings,
        string label
    )
    {
        if (raw is null)
        {
            warnings.Add($"{label}: missing measurement was omitted.");
            return null;
        }
        var (unit, custom) = NormalizeUnit(raw.Unit);
        var measurement = new RecipeMeasurement(raw.Quantity, unit, raw.MaxQuantity, custom);
        try
        {
            RecipeMeasurementValidation.Validate(measurement);
            return measurement;
        }
        catch (DomainValidationException)
        {
            warnings.Add(
                $"{label}: invalid measurement was omitted; verify the amount in the source."
            );
            return null;
        }
    }

    private static readonly HashSet<string> DisallowedUnitPlaceholders = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "null",
        "none",
        "nil",
        "n/a",
        "na",
        "undefined",
        "unknown",
        "empty",
        "-",
        "--",
        "???",
        "?",
        "to taste",
        "taste",
        "as needed",
        "optional",
        "to-taste",
        "at taste",
    };

    private static readonly Regex LeadingPinchRegex = new(
        @"^[\s\-*•]*(?:a\s+)?pinch(?:\s+of)?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex LeadingDashRegex = new(
        @"^[\s\-*•]*(?:a\s+)?dash(?:\s+of)?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex LeadingCloveRegex = new(
        @"^[\s\-*•]*(?:a\s+)?clove\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex LeadingSourceAmountRegex = new(
        @"^[\s\-*•]*(?<quantity>\d+(?:\s+\d+/\d+)?|\d+/\d+|\d+(?:\.\d+)?)(?:\s*(?:to|through|or|[-–—])\s*(?<maxQuantity>\d+(?:\s+\d+/\d+)?|\d+/\d+|\d+(?:\.\d+)?))?(?=\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    public static readonly Regex RecipeReferenceRegex = new(
        @"^\s*(?:(?:\d+(?:\s+\d+/\d+|/\d+)?|\d+(?:\.\d+)?)\s*(?:recipe|batch(?:\s+of)?)\s+(?<title>.+?)(?:\s*\((?:optional\s*;\s*)?(?:see\s+)?(?:recipes?\s+follows?|this\s+page(?:\s+and\s+this\s+page)?|pages?\s+\d+)\))?|(?<title>.+?)\s*\((?:optional\s*;\s*)?(?:see\s+)?(?:recipes?\s+follows?|this\s+page(?:\s+and\s+this\s+page)?|pages?\s+\d+)\))\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex ParentheticalMeasurementRegex = new(
        @"\((?<measurement>[^()]*\b(?:ounces?|pounds?|grams?|kilograms?|sticks?)\b[^()]*)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1)
    );

    public static bool IsDisallowedUnitPlaceholder(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit))
        {
            return true;
        }

        var trimmed = unit.Trim();
        return DisallowedUnitPlaceholders.Contains(trimmed);
    }

    private static string StripLeadingUnitPrefix(string displayName, Regex regex)
    {
        var cleaned = regex.Replace(displayName, "").Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? displayName : cleaned;
    }

    public static string? NormalizeRecipeReferenceTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var normalized = title.Trim();
        normalized = Regex.Replace(
            normalized,
            @",\s*(?:warm|room\s+temperature|chilled|divided|optional|melted|cooled|thawed|at\s+room\s+temperature)\s*$",
            "",
            RegexOptions.IgnoreCase
        );
        normalized = Regex.Replace(
            normalized,
            @"\s+for\s+serving\s*$",
            "",
            RegexOptions.IgnoreCase
        );
        normalized = Regex.Replace(
            normalized,
            @"\s*\((?:optional\s*;\s*)?(?:see\s+)?(?:recipes?\s+follows?|this\s+page(?:\s+and\s+this\s+page)?|pages?\s+\d+)\)\s*$",
            "",
            RegexOptions.IgnoreCase
        );
        normalized = Regex.Replace(
            normalized,
            @",\s*(?:warm|room\s+temperature|chilled|divided|optional|melted|cooled|thawed|at\s+room\s+temperature)\s*$",
            "",
            RegexOptions.IgnoreCase
        );
        normalized = Regex.Replace(
            normalized,
            @"^(?:\d+(?:\s+\d+/\d+|/\d+)?|\d+(?:\.\d+)?)?\s*(?:recipe|batch(?:\s+of)?)\s+",
            "",
            RegexOptions.IgnoreCase
        );
        var spans = SourceMeasurementParser.FindSpans(normalized);
        if (spans.Count > 0 && spans[0].Index == 0)
        {
            normalized = normalized[spans[0].Length..].TrimStart();
        }
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized.Trim();
    }

    public static (MeasurementUnit? Unit, string? CustomUnitName) NormalizeUnit(
        string? rawUnit,
        string? customUnitName = null
    )
    {
        if (
            string.Equals(rawUnit, "custom", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(customUnitName)
        )
        {
            rawUnit = customUnitName;
        }
        if (string.IsNullOrWhiteSpace(rawUnit))
        {
            return (null, null);
        }

        var trimmed = rawUnit.Trim();
        if (DisallowedUnitPlaceholders.Contains(trimmed))
        {
            return (null, null);
        }

        var lower = trimmed.ToLowerInvariant();
        if (DisallowedUnitPlaceholders.Contains(lower))
        {
            return (null, null);
        }

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
            "ml" or "milliliter" or "milliliters" or "millilitre" or "millilitres" => (
                MeasurementUnit.Ml,
                null
            ),
            "l" or "liter" or "liters" or "litre" or "litres" => (MeasurementUnit.L, null),
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
            "whole"
            or "large"
            or "medium"
            or "small"
            or "recipe"
            or "recipes"
            or "batch"
            or "batches" => (MeasurementUnit.Whole, null),
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

    private static (decimal Quantity, decimal? MaxQuantity)? TryGetSourceAmount(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return null;
        }

        var match = LeadingSourceAmountRegex.Match(rawText);
        if (!match.Success || !TryParseAmount(match.Groups["quantity"].Value, out var quantity))
        {
            return null;
        }

        decimal? maxQuantity = null;
        if (
            match.Groups["maxQuantity"].Success
            && TryParseAmount(match.Groups["maxQuantity"].Value, out var parsedMax)
        )
        {
            maxQuantity = parsedMax;
        }

        return quantity > 0m ? (quantity, maxQuantity) : null;
    }

    private static bool TryParseAmount(string token, out decimal amount)
    {
        var parts = token.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && TryParseFraction(parts[1], out var fractionalPart))
        {
            if (
                decimal.TryParse(
                    parts[0],
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var wholePart
                )
            )
            {
                amount = wholePart + fractionalPart;
                return true;
            }
        }
        else if (parts.Length == 1 && TryParseFraction(parts[0], out amount))
        {
            return true;
        }

        amount = 0m;
        return false;
    }

    private static bool TryParseFraction(string token, out decimal amount)
    {
        var fractionParts = token.Split('/');
        if (
            fractionParts.Length == 2
            && decimal.TryParse(
                fractionParts[0],
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var numerator
            )
            && decimal.TryParse(
                fractionParts[1],
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var denominator
            )
            && denominator != 0m
        )
        {
            amount = numerator / denominator;
            return true;
        }

        return decimal.TryParse(
            token,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out amount
        );
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

        if (cleaned.Length > 2000)
        {
            cleaned = cleaned[..2000].TrimEnd();
        }

        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    public static string? NormalizePreparationNote(
        LlmExtractedIngredientItem ingredient,
        IReadOnlyList<LlmExtractedIngredientItem> ingredients
    )
    {
        var result = string.IsNullOrWhiteSpace(ingredient.PreparationNote)
            ? null
            : ingredient.PreparationNote.Trim();
        // A full source-line weight is not necessarily the weight of a split portion or food.
        if (
            SourceMeasurementParser.FindAlternative(ingredient.OriginalRawText) is not null
            || ingredients.Count(other =>
                string.Equals(
                    other.OriginalRawText?.Trim(),
                    ingredient.OriginalRawText?.Trim(),
                    StringComparison.OrdinalIgnoreCase
                )
            ) != 1
        )
        {
            return result;
        }
        foreach (
            Match match in ParentheticalMeasurementRegex.Matches(ingredient.OriginalRawText ?? "")
        )
        {
            var measurement = match.Groups["measurement"].Value.Trim();
            if (result?.Contains(measurement, StringComparison.OrdinalIgnoreCase) != true)
            {
                result = result is null ? measurement : $"{result}; {measurement}";
            }
        }
        return result;
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
        var measurementWarnings = new List<string>();
        var rawIngredients = raw.Ingredients ?? [];

        foreach (var rawIng in rawIngredients)
        {
            var sameLine = string.IsNullOrWhiteSpace(rawIng.OriginalRawText)
                ? []
                : rawIngredients
                    .Where(other =>
                        string.Equals(
                            other.OriginalRawText?.Trim(),
                            rawIng.OriginalRawText.Trim(),
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    .ToList();
            // "5 cups (25 ounces) flour, plus extra" keeps its equivalent on the one measured row.
            var isSplitSourceLine =
                sameLine.Count > 1
                && !(
                    rawIng.Quantity is not null && sameLine.Count(o => o.Quantity is not null) == 1
                );
            var isUnmeasuredSplitRow = sameLine.Count > 1 && rawIng.Quantity is null;
            var (unit, customUnit) = NormalizeUnit(rawIng.Unit, rawIng.CustomUnitName);
            var (cleanQuantity, cleanMaxQuantity) = NormalizeQuantities(
                rawIng.Quantity,
                rawIng.MaxQuantity
            );
            var sourceAmount = TryGetSourceAmount(rawIng.OriginalRawText);
            if (!isSplitSourceLine && sourceAmount is { } parsedAmount)
            {
                cleanQuantity = parsedAmount.Quantity;
                cleanMaxQuantity = parsedAmount.MaxQuantity;
            }
            else if (!isSplitSourceLine && !string.IsNullOrWhiteSpace(rawIng.OriginalRawText))
            {
                cleanQuantity =
                    unit is MeasurementUnit.Pinch or MeasurementUnit.Dash
                    || (
                        unit is MeasurementUnit.Clove
                        && LeadingCloveRegex.IsMatch(rawIng.OriginalRawText)
                    )
                        ? 1m
                        : null;
                cleanMaxQuantity = null;
            }

            var cleanCanonical = FoodNameSanitizer.SanitizeFoodName(rawIng.CanonicalFoodName);
            var cleanDisplayName = FoodNameSanitizer.SanitizeFoodName(rawIng.DisplayName);
            var displayName = !string.IsNullOrWhiteSpace(rawIng.DisplayName)
                ? rawIng.DisplayName.Trim()
                : "Ingredient";
            var candidateFoodName = !string.IsNullOrWhiteSpace(rawIng.CanonicalFoodName)
                ? cleanCanonical
                : cleanDisplayName;
            var rawRecipeReferenceTitle = NormalizeRecipeReferenceTitle(
                rawIng.ReferencedRecipeTitle
            );
            var sourceIngredientText = !string.IsNullOrWhiteSpace(rawIng.OriginalRawText)
                ? rawIng.OriginalRawText
                : displayName;
            var recipeReferenceMatch = RecipeReferenceRegex.Match(sourceIngredientText);
            var isRecipeReference =
                rawIng.IsRecipeReference
                || recipeReferenceMatch.Success
                || rawRecipeReferenceTitle is not null;
            var recipeReferenceTitle =
                rawRecipeReferenceTitle
                ?? (
                    recipeReferenceMatch.Success
                        ? NormalizeRecipeReferenceTitle(recipeReferenceMatch.Groups["title"].Value)
                        : null
                )
                ?? (rawIng.IsRecipeReference ? NormalizeRecipeReferenceTitle(displayName) : null);

            var prepNote = NormalizePreparationNote(rawIng, rawIngredients);

            if (prepNote is null && !string.IsNullOrWhiteSpace(rawIng.Unit))
            {
                var trimmedUnit = rawIng.Unit.Trim().ToLowerInvariant();
                if (trimmedUnit is "to taste" or "taste" or "as needed")
                {
                    prepNote = trimmedUnit;
                }
            }

            // If the unit is Pinch or Dash and quantity is missing or tiny (<= 0.1, e.g. LLM 0.01 artifact for "pinch"), default quantity to 1.
            if (
                (unit is MeasurementUnit.Pinch or MeasurementUnit.Dash)
                && (cleanQuantity is null or <= 0.1m)
            )
            {
                cleanQuantity = 1m;
            }
            // Only infer pinch/dash from source wording; small explicit measurements are valid.
            if (unit is null || cleanQuantity is null or <= 0.05m)
            {
                var textToCheck = !string.IsNullOrWhiteSpace(rawIng.OriginalRawText)
                    ? rawIng.OriginalRawText
                    : displayName;

                var pinchMatch = LeadingPinchRegex.Match(textToCheck);
                if (pinchMatch.Success)
                {
                    unit = MeasurementUnit.Pinch;
                    customUnit = null;
                    cleanQuantity = (cleanQuantity is null or <= 0.1m) ? 1m : cleanQuantity;
                    displayName = StripLeadingUnitPrefix(displayName, LeadingPinchRegex);
                }
                else
                {
                    var dashMatch = LeadingDashRegex.Match(textToCheck);
                    if (dashMatch.Success)
                    {
                        unit = MeasurementUnit.Dash;
                        customUnit = null;
                        cleanQuantity = (cleanQuantity is null or <= 0.1m) ? 1m : cleanQuantity;
                        displayName = StripLeadingUnitPrefix(displayName, LeadingDashRegex);
                    }
                }
            }

            if (isRecipeReference)
            {
                if (
                    unit == MeasurementUnit.Custom
                    && (
                        string.IsNullOrWhiteSpace(customUnit)
                        || customUnit.Equals("custom", StringComparison.OrdinalIgnoreCase)
                        || customUnit.Equals("recipe", StringComparison.OrdinalIgnoreCase)
                        || customUnit.Equals("batch", StringComparison.OrdinalIgnoreCase)
                    )
                )
                {
                    unit = MeasurementUnit.Whole;
                    customUnit = null;
                }
                if (cleanQuantity is null && unit is MeasurementUnit.Whole)
                {
                    cleanQuantity = 1m;
                }
                displayName = recipeReferenceTitle ?? displayName;
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
                        PreparationNote: prepNote,
                        IsOptional: rawIng.IsOptional,
                        OriginalRawText: string.IsNullOrWhiteSpace(rawIng.OriginalRawText)
                            ? null
                            : rawIng.OriginalRawText.Trim(),
                        IsRecipeReference: true,
                        ReferencedRecipeTitle: recipeReferenceTitle
                    )
                );
                continue;
            }

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
                        PreparationNote: prepNote,
                        IsOptional: rawIng.IsOptional,
                        OriginalRawText: string.IsNullOrWhiteSpace(rawIng.OriginalRawText)
                            ? null
                            : rawIng.OriginalRawText.Trim(),
                        SuggestedCategory: match.Category
                            ?? FoodCategoryClassifier.Classify(match.Name)
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
                        PreparationNote: prepNote,
                        IsOptional: rawIng.IsOptional,
                        OriginalRawText: string.IsNullOrWhiteSpace(rawIng.OriginalRawText)
                            ? null
                            : rawIng.OriginalRawText.Trim(),
                        SuggestedCategory: FoodCategories.Parse(rawIng.SuggestedCategory)
                            ?? FoodCategoryClassifier.Classify(proposedName)
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
                        PreparationNote: prepNote,
                        IsOptional: rawIng.IsOptional,
                        OriginalRawText: string.IsNullOrWhiteSpace(rawIng.OriginalRawText)
                            ? null
                            : rawIng.OriginalRawText.Trim()
                    )
                );
            }
            var ingredient = normalizedIngredients[^1];
            var sourcePair = SourceMeasurementParser.FindAlternative(rawIng.OriginalRawText);
            if (sourcePair is not null && isSplitSourceLine)
            {
                if (rawIng.AlternativeMeasurement is not null && !isUnmeasuredSplitRow)
                {
                    var alt = NormalizeMeasurement(
                        rawIng.AlternativeMeasurement,
                        measurementWarnings,
                        displayName
                    );
                    if (alt is not null && MatchesMeasurement(alt, sourcePair.Value.Alternative))
                    {
                        measurementWarnings.Add(
                            $"{displayName}: the source equivalent applies to a whole ingredient line, not each split portion; verify portion measurements."
                        );
                    }
                }
            }
            else if (sourcePair is { } pair)
            {
                if (
                    !string.IsNullOrWhiteSpace(rawSourceText)
                    && !IsSourceExcerpt(rawIng.OriginalRawText, rawSourceText)
                )
                    measurementWarnings.Add(
                        $"{displayName}: verify the extracted measurement pair; its source excerpt could not be confirmed."
                    );
                normalizedIngredients[^1] = ingredient with
                {
                    Quantity = pair.Primary.Quantity,
                    MaxQuantity = pair.Primary.MaxQuantity,
                    Unit = pair.Primary.Unit,
                    CustomUnitName = pair.Primary.CustomUnitName,
                    AlternativeMeasurement = pair.Alternative,
                };
            }
            else if (rawIng.AlternativeMeasurement is not null)
            {
                var alternative = NormalizeMeasurement(
                    rawIng.AlternativeMeasurement,
                    measurementWarnings,
                    displayName
                );
                if (
                    alternative is not null
                    && ingredient.Quantity is > 0
                    && ingredient.Unit.HasValue
                )
                {
                    normalizedIngredients[^1] = ingredient with
                    {
                        AlternativeMeasurement = alternative,
                    };
                    if (!IsAlternativeConfirmedBySource(rawIng.OriginalRawText, alternative))
                    {
                        measurementWarnings.Add(
                            $"{displayName}: verify the model-proposed alternative measurement against the source."
                        );
                    }
                }
                else if (alternative is not null)
                {
                    measurementWarnings.Add(
                        $"{displayName}: alternative measurement needs a measured primary amount and was omitted."
                    );
                }
            }
        }

        var normalizedSteps = new List<IngestionDraftStepDto>();
        var rawSteps = raw.Steps ?? [];
        var ingredientCount = normalizedIngredients.Count;

        for (var i = 0; i < rawSteps.Count; i++)
        {
            var rawStep = rawSteps[i];
            var rawInstruction = rawStep.Instruction ?? "";
            var instruction = rawInstruction.Trim();
            var tagged = rawStep.InlineAmounts is { Count: > 0 };
            // Tagged spans index the verbatim text, so only surrounding whitespace may change.
            if (!tagged)
                instruction = SubStepPrefix.Replace(instruction, "");
            if (string.IsNullOrWhiteSpace(instruction))
            {
                continue;
            }
            var inlineAmounts = NormalizeInlineAmounts(
                rawStep.InlineAmounts,
                rawInstruction.Length - rawInstruction.TrimStart().Length,
                instruction,
                ingredientCount,
                measurementWarnings,
                $"Step {i + 1}"
            );

            // Valid ingredient indices
            if ((rawStep.IngredientIndices ?? []).Any(idx => idx < 0 || idx >= ingredientCount))
            {
                measurementWarnings.Add(
                    $"Step {i + 1}: an unknown ingredient index was omitted; verify its ingredient links."
                );
            }
            var validIndices = (rawStep.IngredientIndices ?? [])
                .Where(idx => idx >= 0 && idx < ingredientCount)
                .Distinct()
                .OrderBy(idx => idx)
                .ToList();

            var amounts = new List<RecipeStepIngredientAmountInput>();
            var sourceAmounts = SourceMeasurementParser.Parse(
                rawStep.OriginalRawText ?? rawStep.Instruction
            );
            foreach (var rawAmount in rawStep.IngredientAmounts ?? [])
            {
                var label = $"Step {i + 1}";
                if (
                    rawAmount is null
                    || rawAmount.IngredientIndex is null or < 0
                    || rawAmount.IngredientIndex >= ingredientCount
                    || amounts.Any(a => a.IngredientIndex == rawAmount.IngredientIndex)
                )
                {
                    measurementWarnings.Add(
                        $"{label}: invalid or duplicate ingredient reference was omitted."
                    );
                    continue;
                }
                var amount = NormalizeMeasurement(
                    rawAmount.Measurement,
                    measurementWarnings,
                    label
                );
                if (amount is null)
                    continue;
                // Tagged steps compute their amounts in code from the verbatim spans.
                var modelProposed = rawStep.InlineAmounts is null;
                if (
                    modelProposed
                    && (
                        sourceAmounts.Count == 0
                        || (
                            !string.IsNullOrWhiteSpace(rawSourceText)
                            && !IsSourceExcerpt(
                                rawStep.OriginalRawText ?? rawStep.Instruction,
                                rawSourceText
                            )
                        )
                    )
                )
                {
                    measurementWarnings.Add(
                        $"{label}: verify the model-proposed amount against the source."
                    );
                }
                else if (
                    modelProposed
                    && !sourceAmounts.Any(source =>
                        source.Unit == amount.Unit
                        && Math.Abs(source.Quantity!.Value - amount.Quantity!.Value) < 0.0001m
                        && source.MaxQuantity == amount.MaxQuantity
                    )
                )
                {
                    measurementWarnings.Add(
                        $"{label}: amount did not match the source instruction and was omitted."
                    );
                    continue;
                }
                amounts.Add(
                    new RecipeStepIngredientAmountInput(rawAmount.IngredientIndex.Value, amount)
                );
                if (!validIndices.Contains(rawAmount.IngredientIndex.Value))
                    validIndices.Add(rawAmount.IngredientIndex.Value);
            }

            foreach (
                var index in inlineAmounts
                    .Where(a => a.IngredientIndex is not null)
                    .Select(a => a.IngredientIndex!.Value)
            )
            {
                if (!validIndices.Contains(index))
                    validIndices.Add(index);
            }
            validIndices.Sort();

            // Timers: LLM provided vs StepTimerExtractor detection
            var timers = new List<RecipeStepTimerInput>();
            if (rawStep.Timers is { Count: > 0 })
            {
                foreach (var t in rawStep.Timers)
                {
                    if (t.DurationSeconds >= 1 && !string.IsNullOrWhiteSpace(t.Label))
                    {
                        timers.Add(
                            new RecipeStepTimerInput(
                                StepTimerExtractor.UseRangeLowerBound(
                                    t.DurationSeconds,
                                    instruction
                                ),
                                t.Label.Trim()
                            )
                        );
                    }
                }
            }

            if (timers.Count == 0)
            {
                timers.AddRange(StepTimerExtractor.DetectTimers(instruction, i + 1));
            }

            if (timers.Count > 1)
            {
                var labelGroups = timers
                    .GroupBy(t => t.Label, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (labelGroups.Count > 0)
                {
                    var disambiguated = new List<RecipeStepTimerInput>(timers.Count);
                    var usedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var timer in timers)
                    {
                        var label = timer.Label;
                        if (labelGroups.Contains(label))
                        {
                            var formatted = FormatTimerDuration(timer.DurationSeconds);
                            label = $"{label} ({formatted})";
                        }
                        var baseLabel = label;
                        var suffix = 1;
                        while (!usedLabels.Add(label))
                        {
                            label = $"{baseLabel} {++suffix}";
                        }
                        disambiguated.Add(timer with { Label = label });
                    }
                    timers = disambiguated;
                }
            }

            normalizedSteps.Add(
                new IngestionDraftStepDto(
                    SectionName: string.IsNullOrWhiteSpace(rawStep.SectionName)
                        ? null
                        : rawStep.SectionName.Trim(),
                    Instruction: instruction,
                    IngredientIndices: validIndices,
                    Timers: timers,
                    ImagePath: null,
                    IngredientAmounts: amounts,
                    InlineAmounts: inlineAmounts
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

        var confidence = CalculateConfidenceScore(
            raw.ConfidenceScore,
            normalizedIngredients.Count,
            normalizedSteps.Count,
            validationIssueCount: 0,
            measurementWarningCount: measurementWarnings.Count,
            qualityWarningCount: 0
        );

        var normalizedDescription = NormalizeDescription(raw.Description);
        var atkHeadnote = TryExtractAtkHeadnote(rawSourceText);
        if (!string.IsNullOrWhiteSpace(atkHeadnote))
        {
            if (
                string.IsNullOrWhiteSpace(normalizedDescription)
                || atkHeadnote.Length > (normalizedDescription.Length * 1.5)
            )
            {
                normalizedDescription = NormalizeDescription(atkHeadnote);
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
            ProposedTagNames: proposedTagNames,
            MeasurementWarnings: measurementWarnings
        );
    }

    private static bool IsSourceExcerpt(string? excerpt, string source) =>
        !string.IsNullOrWhiteSpace(excerpt)
        && Regex
            .Replace(source, @"\s+", " ")
            .Contains(
                Regex.Replace(excerpt, @"\s+", " ").Trim(),
                StringComparison.OrdinalIgnoreCase
            );

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

    public static string FormatTimerDuration(int seconds)
    {
        if (seconds >= 3600 && seconds % 3600 == 0)
        {
            var hours = seconds / 3600;
            return hours == 1 ? "1 hr" : $"{hours} hrs";
        }
        if (seconds >= 3600)
        {
            var hours = seconds / 3600;
            var mins = (seconds % 3600) / 60;
            return $"{hours}h {mins}m";
        }
        if (seconds >= 60)
        {
            var mins = seconds / 60;
            var secs = seconds % 60;
            return secs > 0 ? $"{mins}m {secs}s" : $"{mins}m";
        }
        return $"{seconds}s";
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

    public static decimal CalculateConfidenceScore(
        decimal? rawConfidence,
        int ingredientCount,
        int stepCount,
        int validationIssueCount,
        int measurementWarningCount,
        int qualityWarningCount
    )
    {
        if (ingredientCount == 0 || stepCount == 0)
        {
            return 0.25m;
        }

        var normalizedRaw = rawConfidence switch
        {
            >= 0.0m and <= 1.0m => rawConfidence.Value,
            > 1.0m and <= 100.0m => Math.Round(rawConfidence.Value / 100.0m, 2),
            _ => (decimal?)null,
        };

        var baseScore = normalizedRaw is >= 0.50m and <= 1.0m
            ? Math.Max(normalizedRaw.Value, 0.92m)
            : 0.95m;

        var deductions =
            (0.08m * validationIssueCount)
            + (0.03m * (measurementWarningCount + qualityWarningCount));
        var score = baseScore - deductions;
        return Math.Clamp(Math.Round(score, 2), 0.15m, 1.00m);
    }

    private static bool IsAlternativeConfirmedBySource(
        string? originalRawText,
        RecipeMeasurement alternative
    )
    {
        if (string.IsNullOrWhiteSpace(originalRawText))
        {
            return false;
        }

        var confirmables = SourceMeasurementParser.FindAllConfirmableMeasurements(originalRawText);
        foreach (var measurement in confirmables)
        {
            if (MatchesMeasurement(measurement, alternative))
            {
                return true;
            }
        }

        if (Regex.IsMatch(originalRawText, @"\bsticks?\b", RegexOptions.IgnoreCase))
        {
            var stickMatch = Regex.Match(
                originalRawText,
                @"(?<qty>(?:\d+\s*)?[½¼¾⅓⅔⅛⅜⅝⅞]|\d+(?:[\s-]+\d+/\d+|/\d+|\.\d+)?)\s*sticks?\b",
                RegexOptions.IgnoreCase
            );
            var stickQty = stickMatch.Success
                ? FlexibleNullableDecimalJsonConverter.ParseNumber(stickMatch.Groups["qty"].Value)
                : 1m;
            if (stickQty is > 0)
            {
                var n = stickQty.Value;
                if (
                    alternative.Unit == MeasurementUnit.Tbsp
                    && Math.Abs((alternative.Quantity ?? 0) - n * 8m) <= 0.05m
                )
                {
                    return true;
                }
                if (
                    alternative.Unit == MeasurementUnit.Oz
                    && Math.Abs((alternative.Quantity ?? 0) - n * 4m) <= 0.05m
                )
                {
                    return true;
                }
                if (
                    alternative.Unit == MeasurementUnit.Cup
                    && Math.Abs((alternative.Quantity ?? 0) - n * 0.5m) <= 0.05m
                )
                {
                    return true;
                }
                if (
                    string.Equals(
                        alternative.CustomUnitName,
                        "stick",
                        StringComparison.OrdinalIgnoreCase
                    )
                    && Math.Abs((alternative.Quantity ?? 0) - n) <= 0.01m
                )
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool MatchesMeasurement(RecipeMeasurement a, RecipeMeasurement b)
    {
        if (a.Unit == b.Unit)
        {
            if (
                a.Unit == MeasurementUnit.Custom
                && !string.Equals(
                    a.CustomUnitName,
                    b.CustomUnitName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return false;
            }
            if (a.Quantity is null || b.Quantity is null)
            {
                return false;
            }
            return Math.Abs(a.Quantity.Value - b.Quantity.Value) <= 0.05m;
        }

        if (a.Quantity is null || b.Quantity is null || !a.Unit.HasValue || !b.Unit.HasValue)
        {
            return false;
        }

        if (
            CulinaryMath.TryConvertQuantity(
                a.Quantity.Value,
                a.Unit.Value,
                b.Unit.Value,
                out var converted
            )
        )
        {
            return Math.Abs(converted - b.Quantity.Value) <= 0.05m;
        }

        return false;
    }
}

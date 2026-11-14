using System.Globalization;
using System.Text;
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

public readonly record struct IngredientCandidateInfo(
    int Index,
    string? SectionName,
    string? DisplayName,
    string? CanonicalOrMatchedName,
    decimal? Quantity,
    decimal? MaxQuantity,
    MeasurementUnit? Unit,
    string? CustomUnitName,
    Guid? Id = null,
    string? PreparationNote = null
);

public static class RecipeDraftNormalizer
{
    private static readonly Regex SubStepPrefix = new(
        @"^\s*(?:step\s+)?\d+[A-Za-z][.):]\s+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    public static string NormalizeSectionName(string? section)
    {
        if (string.IsNullOrWhiteSpace(section))
            return string.Empty;

        var s = section.Trim().ToLowerInvariant();
        string[] prefixes =
        [
            "for the ",
            "to make the ",
            "to assemble the ",
            "for assembling the ",
            "to make ",
            "for ",
            "the ",
            "make the ",
            "prepare the ",
        ];

        bool changed;
        do
        {
            changed = false;
            foreach (var p in prefixes)
            {
                if (s.StartsWith(p, StringComparison.Ordinal))
                {
                    s = s[p.Length..].Trim();
                    changed = true;
                }
            }
        } while (changed);

        return s.TrimEnd(':', '-', ' ', ';');
    }

    public static int FindBestMatchingIngredientIndex(
        StepIngredientToken token,
        string? stepSectionName,
        IReadOnlyList<IngredientCandidateInfo> candidates,
        IReadOnlyCollection<int>? currentStepIndices = null,
        string? stepInstruction = null
    )
    {
        if (candidates is null || candidates.Count == 0)
        {
            return -1;
        }

        if (token.IngredientId.HasValue)
        {
            var idMatch = candidates.FirstOrDefault(c => c.Id == token.IngredientId.Value);
            if (idMatch.Index >= 0)
            {
                return idMatch.Index;
            }
        }

        if (string.IsNullOrWhiteSpace(token.Ingredient))
        {
            return -1;
        }

        var tokenName = token.Ingredient.Trim();
        var matching = new List<(IngredientCandidateInfo Candidate, int NameScore)>();

        foreach (var c in candidates)
        {
            var disp = c.DisplayName?.Trim();
            var alt = c.CanonicalOrMatchedName?.Trim();
            var nameScore = 0;

            if (!string.IsNullOrWhiteSpace(disp))
            {
                if (string.Equals(tokenName, disp, StringComparison.OrdinalIgnoreCase))
                {
                    nameScore = 100;
                }
                else if (
                    disp.Contains(tokenName, StringComparison.OrdinalIgnoreCase)
                    || tokenName.Contains(disp, StringComparison.OrdinalIgnoreCase)
                )
                {
                    nameScore = 50;
                }
            }

            if (nameScore < 90 && !string.IsNullOrWhiteSpace(alt))
            {
                if (string.Equals(tokenName, alt, StringComparison.OrdinalIgnoreCase))
                {
                    nameScore = Math.Max(nameScore, 90);
                }
                else if (
                    alt.Contains(tokenName, StringComparison.OrdinalIgnoreCase)
                    || tokenName.Contains(alt, StringComparison.OrdinalIgnoreCase)
                )
                {
                    nameScore = Math.Max(nameScore, 40);
                }
            }

            if (nameScore == 0 && IsEggPartMatch(tokenName, disp, alt, candidates))
            {
                nameScore =
                    !string.IsNullOrWhiteSpace(c.PreparationNote)
                    && c.PreparationNote.Contains("separated", StringComparison.OrdinalIgnoreCase)
                        ? 85
                        : 75;
            }

            if (nameScore > 0)
            {
                matching.Add((c, nameScore));
            }
        }

        if (matching.Count == 0)
        {
            return -1;
        }

        if (matching.Count == 1)
        {
            return matching[0].Candidate.Index;
        }

        var stepSectionNorm = NormalizeSectionName(stepSectionName);
        var bestScore = int.MinValue;
        var bestIdx = matching[0].Candidate.Index;

        foreach (var (cand, nameScore) in matching)
        {
            var score = nameScore;
            var candSectionNorm = NormalizeSectionName(cand.SectionName);

            // 1. Section matching
            if (!string.IsNullOrEmpty(stepSectionNorm) && !string.IsNullOrEmpty(candSectionNorm))
            {
                if (
                    string.Equals(
                        stepSectionNorm,
                        candSectionNorm,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    score += 250;
                }
                else if (
                    candSectionNorm.Contains(stepSectionNorm, StringComparison.OrdinalIgnoreCase)
                    || stepSectionNorm.Contains(candSectionNorm, StringComparison.OrdinalIgnoreCase)
                )
                {
                    score += 180;
                }
                else
                {
                    score -= 100;
                }
            }
            else if (
                string.IsNullOrEmpty(stepSectionNorm) && !string.IsNullOrEmpty(candSectionNorm)
            )
            {
                if (
                    !string.IsNullOrWhiteSpace(stepInstruction)
                    && stepInstruction.Contains(candSectionNorm, StringComparison.OrdinalIgnoreCase)
                )
                {
                    score += 80;
                }
            }

            // 2. Amount matching
            if (token.Amount.HasValue && cand.Quantity.HasValue && cand.Quantity.Value > 0)
            {
                var tokenAmt = token.Amount.Value;
                var candQty = cand.Quantity.Value;
                var candMax = cand.MaxQuantity ?? candQty;

                decimal? convertedAmt = null;
                if (token.Unit == cand.Unit)
                {
                    convertedAmt = tokenAmt;
                }
                else if (token.Unit.HasValue && cand.Unit.HasValue)
                {
                    if (
                        CulinaryMath.TryConvertQuantity(
                            tokenAmt,
                            token.Unit.Value,
                            cand.Unit.Value,
                            out var cAmt
                        )
                    )
                    {
                        convertedAmt = cAmt;
                    }
                }

                if (convertedAmt.HasValue)
                {
                    var val = convertedAmt.Value;
                    var tolerance = Math.Max(0.001m, candQty * 0.01m);

                    if (Math.Abs(val - candQty) <= tolerance)
                    {
                        score += 150;
                    }
                    else if (
                        cand.MaxQuantity.HasValue
                        && val >= candQty - tolerance
                        && val <= candMax + tolerance
                    )
                    {
                        score += 120;
                    }
                    else if (val < candQty - tolerance)
                    {
                        score += 60;
                    }
                    else if (val > candMax + tolerance)
                    {
                        score -= 120;
                    }
                }
            }

            // 3. Direct Unit match
            if (token.Unit.HasValue && cand.Unit.HasValue && token.Unit == cand.Unit)
            {
                score += 40;
            }

            // 4. In current step indices
            if (currentStepIndices?.Contains(cand.Index) == true)
            {
                score += 30;
            }

            // 5. Divided / remaining matching
            var hasRemaining =
                !string.IsNullOrWhiteSpace(stepInstruction)
                && Regex.IsMatch(stepInstruction, @"\bremaining\b", RegexOptions.IgnoreCase);
            var candIsDivided =
                !string.IsNullOrWhiteSpace(cand.PreparationNote)
                && cand.PreparationNote.Contains("divided", StringComparison.OrdinalIgnoreCase);

            if (hasRemaining)
            {
                if (candIsDivided)
                {
                    score += 150;
                }
                else
                {
                    score -= 80;
                }
            }
            else if (candIsDivided)
            {
                score += 20;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestIdx = cand.Index;
            }
        }

        return bestIdx;
    }

    public static int FindBestMatchingIngredientIndex(
        StepIngredientToken token,
        string? stepSectionName,
        IReadOnlyList<IngestionDraftIngredientDto> ingredients,
        IReadOnlyCollection<int>? currentStepIndices = null,
        string? stepInstruction = null
    )
    {
        if (ingredients is null || ingredients.Count == 0)
        {
            return -1;
        }

        var candidates = new IngredientCandidateInfo[ingredients.Count];
        for (var i = 0; i < ingredients.Count; i++)
        {
            var ing = ingredients[i];
            candidates[i] = new IngredientCandidateInfo(
                i,
                ing.SectionName,
                ing.DisplayName,
                ing.MatchedFoodItemName ?? ing.ProposedFoodItemName,
                ing.Quantity,
                ing.MaxQuantity,
                ing.Unit,
                ing.CustomUnitName,
                PreparationNote: ing.PreparationNote
            );
        }

        return FindBestMatchingIngredientIndex(
            token,
            stepSectionName,
            candidates,
            currentStepIndices,
            stepInstruction
        );
    }

    public static int FindBestMatchingIngredientIndex(
        StepIngredientToken token,
        string? stepSectionName,
        IReadOnlyList<LlmExtractedIngredientItem> ingredients,
        IReadOnlyCollection<int>? currentStepIndices = null,
        string? stepInstruction = null
    )
    {
        if (ingredients is null || ingredients.Count == 0)
        {
            return -1;
        }

        var candidates = new IngredientCandidateInfo[ingredients.Count];
        for (var i = 0; i < ingredients.Count; i++)
        {
            var ing = ingredients[i];
            var (normUnit, custom) = NormalizeUnit(ing.Unit, ing.CustomUnitName);
            candidates[i] = new IngredientCandidateInfo(
                i,
                ing.SectionName,
                ing.DisplayName,
                ing.CanonicalFoodName,
                ing.Quantity,
                ing.MaxQuantity,
                normUnit,
                custom,
                PreparationNote: ing.PreparationNote
            );
        }

        return FindBestMatchingIngredientIndex(
            token,
            stepSectionName,
            candidates,
            currentStepIndices,
            stepInstruction
        );
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

    private const string SourceNumberPattern =
        @"(?:(?:\d+\s*)?[½¼¾⅓⅔⅛⅜⅝⅞]|\d+(?:\s+\d+/\d+)?|\d+/\d+|\d+(?:\.\d+)?|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|dozen|half)";

    private static readonly Regex LeadingSourceAmountRegex = new(
        $@"^[\s\-*•]*(?<quantity>{SourceNumberPattern})(?:\s*(?:to|through|or|[-–—])\s*(?<maxQuantity>{SourceNumberPattern}))?(?=\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex CountableProduceCutRegex = new(
        @"\b(?:wedges?|slices?|halves?|quarters?|rounds?|twists?|peels?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex CountableWholeProduceFoodRegex = new(
        @"\b(?:lemons?|limes?|oranges?|grapefruits?|apples?|pears?|peaches?|plums?|tomatoes?|avocados?|bananas?|onions?|shallots?|potatoes?|cucumbers?|radishes?|scallions?|green\s+onions?|chiles?|chilies?|jalapeños?|jalapenos?|bell\s+peppers?|tortillas?|pitas?|rolls?|buns?|baguettes?|eggs?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    public static readonly Regex RecipeReferenceRegex = new(
        @"^\s*(?:(?:\d+(?:\s+\d+/\d+|/\d+)?|\d+(?:\.\d+)?)\s*(?:recipe|batch(?:\s+of)?)\s+(?<title>.+?)(?:\s*\((?:optional\s*;\s*)?(?:see\s+)?(?:recipes?\s+follows?|this\s+page(?:\s+and\s+this\s+page)?|pages?\s+\d+)\)(?:\s*,\s*[^()]*)?)?|(?<title>.+?)\s*\((?:optional\s*;\s*)?(?:see\s+)?(?:recipes?\s+follows?|this\s+page(?:\s+and\s+this\s+page)?|pages?\s+\d+)\)(?:\s*,\s*[^()]*)?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly HashSet<string> GenericRecipeReferenceCategories = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "glaze",
        "sauce",
        "pan sauce",
        "barbecue sauce",
        "bbq sauce",
        "relish",
        "topping",
        "marinade",
        "wet paste marinade",
        "dry rub",
        "spice rub",
        "rub",
        "flavored butter",
        "compound butter",
        "butter",
        "dressing",
        "vinaigrette",
        "filling",
        "frosting",
        "icing",
        "ganache",
        "syrup",
        "brine",
        "salsa",
        "dip",
        "crust",
    };

    private static readonly TimeSpan HelperRegexTimeout = TimeSpan.FromSeconds(1);

    public static bool IsWholeEggIngredient(string? displayName, string? canonicalName)
    {
        var combined = $"{displayName} {canonicalName}".Trim();
        if (string.IsNullOrWhiteSpace(combined))
        {
            return false;
        }

        return Regex.IsMatch(combined, @"\beggs?\b", RegexOptions.IgnoreCase, HelperRegexTimeout)
            && !Regex.IsMatch(
                combined,
                @"\b(?:noodles?|pasta|yolks?|whites?|replacer|substitute|wash|powder)\b",
                RegexOptions.IgnoreCase,
                HelperRegexTimeout
            );
    }

    public static bool IsSeparatedEggPartTerm(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && Regex.IsMatch(
            text,
            @"\b(?:egg\s+yolks?|egg\s+whites?|yolks?|whites)\b",
            RegexOptions.IgnoreCase,
            HelperRegexTimeout
        );

    public static bool IsEggPartMatch(
        string? tokenName,
        string? candidateDisplayName,
        string? candidateCanonicalName,
        IReadOnlyList<IngredientCandidateInfo> allCandidates
    ) =>
        IsEggPartMatch(
            tokenName,
            candidateDisplayName,
            candidateCanonicalName,
            allCandidates.Select(c => (c.DisplayName, c.CanonicalOrMatchedName))
        );

    public static bool IsEggPartMatch(
        string? tokenName,
        string? candidateDisplayName,
        string? candidateCanonicalName,
        IEnumerable<(string? DisplayName, string? CanonicalName)> allIngredients
    )
    {
        if (
            string.IsNullOrWhiteSpace(tokenName)
            || !IsSeparatedEggPartTerm(tokenName)
            || !IsWholeEggIngredient(candidateDisplayName, candidateCanonicalName)
        )
        {
            return false;
        }

        var wantsYolk = Regex.IsMatch(
            tokenName,
            @"\byolks?\b",
            RegexOptions.IgnoreCase,
            HelperRegexTimeout
        );
        var wantsWhite = Regex.IsMatch(
            tokenName,
            @"\b(?:egg\s+)?whites?\b",
            RegexOptions.IgnoreCase,
            HelperRegexTimeout
        );

        foreach (var (disp, canonical) in allIngredients)
        {
            var other = $"{disp} {canonical}".Trim();
            if (string.IsNullOrWhiteSpace(other))
            {
                continue;
            }

            if (
                wantsYolk
                && Regex.IsMatch(other, @"\byolks?\b", RegexOptions.IgnoreCase, HelperRegexTimeout)
            )
            {
                return false;
            }

            if (
                wantsWhite
                && Regex.IsMatch(
                    other,
                    @"\b(?:egg\s+whites?|whites)\b",
                    RegexOptions.IgnoreCase,
                    HelperRegexTimeout
                )
            )
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsGenericOrAlternativeRecipeReference(
        string? referenceTitle,
        string? sourceIngredientText,
        string? currentRecipeTitle
    )
    {
        if (string.IsNullOrWhiteSpace(referenceTitle))
        {
            return true;
        }

        var normalizedTitle =
            NormalizeRecipeReferenceTitle(referenceTitle) ?? referenceTitle.Trim();
        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return true;
        }

        if (
            !string.IsNullOrWhiteSpace(currentRecipeTitle)
            && string.Equals(
                normalizedTitle,
                currentRecipeTitle.Trim(),
                StringComparison.OrdinalIgnoreCase
            )
            && !RecipeReferenceRegex.IsMatch(sourceIngredientText ?? string.Empty)
        )
        {
            return true;
        }

        if (
            Regex.IsMatch(normalizedTitle, @"\bor\b", RegexOptions.IgnoreCase, HelperRegexTimeout)
            || (
                !string.IsNullOrWhiteSpace(sourceIngredientText)
                && Regex.IsMatch(
                    sourceIngredientText,
                    @"(?:\)|recipes?\s+follows?|this\s+page|\b(?:recipe|batch)\b)\s+or\s+\b|\bor\s+(?:\d+(?:\s+\d+/\d+|/\d+|\.\d+)?\s*)?(?:recipe|batch|tbsp|tablespoons?|tsp|teaspoons?|cups?|oz|ounces?|pounds?|lbs?|store-bought)\b|\bwedges?\s+or\b",
                    RegexOptions.IgnoreCase,
                    HelperRegexTimeout
                )
            )
        )
        {
            return true;
        }

        return GenericRecipeReferenceCategories.Contains(normalizedTitle);
    }

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

    /// <summary>
    /// "1 pound shrimp, peeled and deveined (see this page)" points to a technique, not a
    /// sub-recipe: a page cross-reference after a preparation clause, without "1 recipe".
    /// </summary>
    public static bool IsTechniqueCrossReference(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var page = Regex.Match(
            text,
            @"\((?:see\s+)?(?:this\s+page(?:\s+and\s+this\s+page)?|pages?\s+\d+)\)\s*$",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1)
        );
        return page.Success
            && text[..page.Index].IndexOfAny([',', ';']) >= 0
            && !Regex.IsMatch(
                text,
                @"^\s*[-•]?\s*\d+(?:\s+\d+/\d+|/\d+|\.\d+)?\s*(?:recipe|batch)\b",
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1)
            );
    }

    public static string? NormalizeRecipeReferenceTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var normalized = title.Trim();
        normalized = Regex.Replace(normalized, @"^\s*\([^()]*\d[^()]*\)\s*", "");
        normalized = Regex.Replace(
            normalized,
            @"\s*\((?:optional\s*;\s*)?(?:see\s+)?(?:recipes?\s+follows?|this\s+page(?:\s+and\s+this\s+page)?|pages?\s+\d+)\)",
            "",
            RegexOptions.IgnoreCase
        );
        normalized = Regex.Replace(
            normalized,
            @"\s*\(\s*optional\s*\)\s*$",
            "",
            RegexOptions.IgnoreCase
        );
        const string trailingPrepPattern =
            @",\s*(?:fitted\s+into\b.*|prebaked\b.*|baked\b.*|unbaked\b.*|rolled\b.*|cut\s+into\b.*|warm|room\s+temperature|chilled|divided|optional|melted|cooled|thawed|at\s+room\s+temperature|for\s+serving|recipes?\s+follows?)\s*$";
        normalized = Regex.Replace(normalized, trailingPrepPattern, "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(
            normalized,
            @"^(?:\d+(?:\s+\d+/\d+|/\d+)?|\d+(?:\.\d+)?)?\s*(?:recipe|batch(?:\s+of)?)\s+",
            "",
            RegexOptions.IgnoreCase
        );
        normalized = Regex.Replace(normalized, trailingPrepPattern, "", RegexOptions.IgnoreCase);
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
        var parsed = FlexibleNullableDecimalJsonConverter.ParseNumber(token);
        if (parsed.HasValue)
        {
            amount = parsed.Value;
            return true;
        }

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

        if (
            cookTime >= 180
            && !string.IsNullOrWhiteSpace(rawSourceText)
            && !IsTimeDurationInSource(rawSourceText, cookTime.Value)
        )
        {
            cookTime = null;
        }

        if (
            prepTime >= 180
            && !string.IsNullOrWhiteSpace(rawSourceText)
            && !IsTimeDurationInSource(rawSourceText, prepTime.Value)
        )
        {
            prepTime = null;
        }

        if (
            totalTime >= 180
            && (
                (
                    !string.IsNullOrWhiteSpace(rawSourceText)
                    && !IsTimeDurationInSource(rawSourceText, totalTime.Value)
                )
                || totalTime >= 3600
            )
        )
        {
            totalTime =
                (prepTime.HasValue || cookTime.HasValue) ? (prepTime ?? 0) + (cookTime ?? 0) : null;
        }
        else if (totalTime == null && (prepTime.HasValue || cookTime.HasValue))
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
        var rawIngredients = RestoreVerbatimSourceLines(raw.Ingredients ?? [], rawSourceText);

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
            var compoundLeading = !isSplitSourceLine
                ? SourceMeasurementParser.TrySumCompoundLeadingMeasurement(rawIng.OriginalRawText)
                : null;
            if (compoundLeading is not null)
            {
                cleanQuantity = compoundLeading.Quantity;
                cleanMaxQuantity = compoundLeading.MaxQuantity;
                unit = compoundLeading.Unit;
                customUnit = compoundLeading.CustomUnitName;
            }
            else if (
                isSplitSourceLine
                && cleanMaxQuantity.HasValue
                && !string.IsNullOrWhiteSpace(rawIng.OriginalRawText)
            )
            {
                var spans = SourceMeasurementParser.FindSpans(rawIng.OriginalRawText);
                var span = spans.FirstOrDefault(s =>
                    s.Measurement.Quantity is { } sq && Math.Abs(sq - (cleanQuantity ?? 0)) <= 0.02m
                );
                if (span is not null && span.Measurement.MaxQuantity is null)
                {
                    cleanMaxQuantity = null;
                }
            }
            var sourceAmount = TryGetSourceAmount(rawIng.OriginalRawText);
            var isAutoHealedDivided =
                cleanQuantity.HasValue
                && sourceAmount is { } sa
                && cleanQuantity.Value > sa.Quantity
                && Regex.IsMatch(
                    rawIng.OriginalRawText ?? "",
                    @"\b(?:divided|plus\s+(?:more|extra|[\w\s]+for))\b",
                    RegexOptions.IgnoreCase
                );

            var isRepairedMeasurement =
                raw.IsRepaired
                && cleanQuantity.HasValue
                && sourceAmount is { } sAmount
                && cleanQuantity.Value != sAmount.Quantity;

            var isConfirmedInSourceText =
                cleanQuantity is > 0
                && unit.HasValue
                && !string.IsNullOrWhiteSpace(rawSourceText)
                && SourceMeasurementParser
                    .FindAllConfirmableMeasurements(rawSourceText)
                    .Any(m =>
                        MatchesMeasurement(
                            m,
                            new RecipeMeasurement(cleanQuantity, unit, cleanMaxQuantity, customUnit)
                        )
                    );

            if (
                compoundLeading is not null
                || isAutoHealedDivided
                || isRepairedMeasurement
                || isConfirmedInSourceText
            )
            {
                // Compound leading measurement (e.g. "2 tablespoons plus 4 teaspoons"), auto-healed
                // divided amount, deliberate AI repair, or step-confirmed measurement; preserve it.
            }
            else if (!isSplitSourceLine && sourceAmount is { } parsedAmount)
            {
                cleanQuantity = parsedAmount.Quantity;
                cleanMaxQuantity = parsedAmount.MaxQuantity;
            }
            else if (!isSplitSourceLine && !string.IsNullOrWhiteSpace(rawIng.OriginalRawText))
            {
                if (
                    unit is MeasurementUnit.Pinch or MeasurementUnit.Dash
                    || (
                        unit is MeasurementUnit.Clove
                        && LeadingCloveRegex.IsMatch(rawIng.OriginalRawText)
                    )
                )
                {
                    cleanQuantity = 1m;
                    cleanMaxQuantity = null;
                }
                else if (
                    IsUnmeasuredCountableProduce(
                        rawIng.OriginalRawText,
                        rawIng.DisplayName,
                        rawIng.PreparationNote
                    )
                )
                {
                    cleanQuantity = 1m;
                    cleanMaxQuantity = null;
                    unit = MeasurementUnit.Whole;
                    customUnit = null;
                }
                else
                {
                    cleanQuantity = null;
                    cleanMaxQuantity = null;
                }
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
            var isTechniqueReference = IsTechniqueCrossReference(sourceIngredientText);
            var recipeReferenceMatch = isTechniqueReference
                ? Match.Empty
                : RecipeReferenceRegex.Match(sourceIngredientText);
            var isRecipeReference =
                !isTechniqueReference
                && (
                    recipeReferenceMatch.Success
                    || (
                        !string.IsNullOrWhiteSpace(rawRecipeReferenceTitle)
                        && !string.Equals(
                            rawRecipeReferenceTitle,
                            displayName,
                            StringComparison.OrdinalIgnoreCase
                        )
                        && !string.Equals(
                            rawRecipeReferenceTitle,
                            rawIng.CanonicalFoodName,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                );
            var recipeReferenceTitle =
                rawRecipeReferenceTitle
                ?? (
                    recipeReferenceMatch.Success
                        ? NormalizeRecipeReferenceTitle(recipeReferenceMatch.Groups["title"].Value)
                        : null
                );

            if (
                isRecipeReference
                && IsGenericOrAlternativeRecipeReference(
                    recipeReferenceTitle,
                    sourceIngredientText,
                    title
                )
            )
            {
                isRecipeReference = false;
                recipeReferenceTitle = null;
                displayName = CleanAlternativeFoodName(displayName);
                candidateFoodName = CleanAlternativeFoodName(candidateFoodName ?? displayName);
                cleanCanonical = candidateFoodName;
                cleanDisplayName = displayName;
            }

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
            var instruction = StepInstructionParser.CleanStepInstruction(rawInstruction.Trim());
            instruction = SubStepPrefix.Replace(instruction, "");
            if (string.IsNullOrWhiteSpace(instruction))
            {
                continue;
            }

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
                var modelProposed = true;
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

            // Timers: LLM provided vs StepTimerExtractor detection
            var timers = new List<RecipeStepTimerInput>();
            if (rawStep.Timers is { Count: > 0 })
            {
                foreach (var t in rawStep.Timers)
                {
                    if (t.DurationSeconds >= 1 && !string.IsNullOrWhiteSpace(t.Label))
                    {
                        var normSec = StepTimerExtractor.UseRangeLowerBound(
                            t.DurationSeconds,
                            instruction
                        );
                        var normLabel = StepTimerExtractor.CleanTimerLabel(
                            t.Label,
                            instruction,
                            i + 1,
                            normSec
                        );
                        if (
                            !timers.Any(existing =>
                                existing.DurationSeconds == t.DurationSeconds
                                && string.Equals(
                                    existing.Label,
                                    t.Label.Trim(),
                                    StringComparison.OrdinalIgnoreCase
                                )
                            )
                        )
                        {
                            timers.Add(new RecipeStepTimerInput(normSec, normLabel));
                        }
                    }
                }
            }

            var tokens = StepInstructionParser.ExtractTokens(instruction);
            foreach (var token in tokens)
            {
                if (token is StepIngredientToken ingToken)
                {
                    var matchIdx = FindBestMatchingIngredientIndex(
                        ingToken,
                        rawStep.SectionName,
                        normalizedIngredients,
                        validIndices,
                        instruction
                    );

                    if (matchIdx >= 0)
                    {
                        var candidate = normalizedIngredients[matchIdx];
                        var otherMismatched = validIndices
                            .Where(idx =>
                                idx != matchIdx
                                && idx >= 0
                                && idx < normalizedIngredients.Count
                                && (
                                    string.Equals(
                                        normalizedIngredients[idx].DisplayName,
                                        candidate.DisplayName,
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                    || (
                                        !string.IsNullOrEmpty(candidate.MatchedFoodItemName)
                                        && string.Equals(
                                            normalizedIngredients[idx].MatchedFoodItemName,
                                            candidate.MatchedFoodItemName,
                                            StringComparison.OrdinalIgnoreCase
                                        )
                                    )
                                )
                            )
                            .ToList();

                        foreach (var oldIdx in otherMismatched)
                        {
                            validIndices.Remove(oldIdx);
                        }

                        if (!validIndices.Contains(matchIdx))
                        {
                            validIndices.Add(matchIdx);
                        }
                    }
                }
                else if (token is StepTimerToken timerToken && timerToken.Seconds >= 1)
                {
                    var timerLabel = StepTimerExtractor.CleanTimerLabel(
                        timerToken.Label,
                        instruction,
                        i + 1,
                        timerToken.Seconds
                    );

                    var existingIdx = timers.FindIndex(existing =>
                        existing.DurationSeconds == timerToken.Seconds
                    );

                    if (existingIdx < 0)
                    {
                        timers.Add(new RecipeStepTimerInput(timerToken.Seconds, timerLabel));
                    }
                    else if (
                        timers[existingIdx]
                            .Label.StartsWith(
                                $"Step {i + 1} timer",
                                StringComparison.OrdinalIgnoreCase
                            )
                        && !timerLabel.StartsWith(
                            $"Step {i + 1} timer",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        // Upgrade generic label with more specific action label
                        timers[existingIdx] = timers[existingIdx] with
                        {
                            Label = timerLabel,
                        };
                    }
                }
            }
            validIndices.Sort();

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

            // Keep inline StepTimerToken labels synchronized with cleaned/disambiguated timers
            if (timers.Count > 0)
            {
                instruction = SyncInlineTimerLabels(instruction, timers);
            }

            normalizedSteps.Add(
                new IngestionDraftStepDto(
                    SectionName: string.IsNullOrWhiteSpace(rawStep.SectionName)
                        ? null
                        : rawStep.SectionName.Trim(),
                    Instruction: instruction,
                    IngredientIndices: validIndices.OrderBy(idx => idx).ToList(),
                    Timers: timers,
                    ImagePath: null,
                    IngredientAmounts: amounts
                )
            );
        }

        (normalizedIngredients, normalizedSteps) = ReconcileIngredientsAndSteps(
            normalizedIngredients,
            normalizedSteps,
            rawSourceText
        );

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

                // 1. Direct match against existing tags
                var matchedTag = tags.FirstOrDefault(t =>
                    t.NormalizedName == normTagName
                    || Singularize(t.NormalizedName) == singularTag
                    || t.Slug.Equals(normTagName, StringComparison.OrdinalIgnoreCase)
                    || t.Slug.EndsWith($"-{normTagName}", StringComparison.OrdinalIgnoreCase)
                );

                // 2. If no direct match, check alias mapping or PascalCase deconcatenation
                if (matchedTag is null)
                {
                    string? aliasedName = null;
                    if (BuiltInTags.CanonicalAliases.TryGetValue(cleanedTag, out var directAlias))
                    {
                        aliasedName = directAlias;
                    }
                    else
                    {
                        var splitTag = SplitPascalCase(cleanedTag);
                        if (BuiltInTags.CanonicalAliases.TryGetValue(splitTag, out var splitAlias))
                        {
                            aliasedName = splitAlias;
                        }
                    }

                    if (aliasedName is not null)
                    {
                        cleanedTag = aliasedName;
                        normTagName = cleanedTag.ToLowerInvariant();
                        singularTag = Singularize(normTagName);

                        matchedTag = tags.FirstOrDefault(t =>
                            t.NormalizedName == normTagName
                            || Singularize(t.NormalizedName) == singularTag
                            || t.Slug.Equals(normTagName, StringComparison.OrdinalIgnoreCase)
                            || t.Slug.EndsWith(
                                $"-{normTagName}",
                                StringComparison.OrdinalIgnoreCase
                            )
                        );
                    }
                }

                if (matchedTag is not null)
                {
                    if (!suggestedTagIds.Contains(matchedTag.Id))
                    {
                        suggestedTagIds.Add(matchedTag.Id);
                    }
                }
                else
                {
                    // Filter disallowed grocery sections and raw pantry staples
                    if (
                        BuiltInTags.DisallowedGrocerySectionNames.Contains(cleanedTag)
                        || BuiltInTags.DisallowedGenericStaples.Contains(cleanedTag)
                    )
                    {
                        continue;
                    }

                    if (seenProposedTagKeys.Add(singularTag))
                    {
                        var displayTag = ToTagDisplayCase(cleanedTag);
                        if (!string.IsNullOrWhiteSpace(displayTag))
                        {
                            proposedTagNames.Add(displayTag);
                        }
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

        normalizedIngredients = CleanIngredientSections(normalizedIngredients);
        normalizedSteps = CleanStepSections(normalizedSteps, rawSourceText);

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

    private static readonly HashSet<string> GenericTopLevelSectionNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "ingredients",
        "ingredient",
        "ingredient list",
        "ingredients list",
        "base ingredients",
        "main ingredients",
        "all ingredients",
        "recipe ingredients",
        "instructions",
        "instruction",
        "directions",
        "direction",
        "method",
        "methods",
        "preparation",
        "prep",
        "steps",
        "step",
        "recipe",
        "main",
        "general",
    };

    public static bool IsGenericTopLevelSection(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var trimmed = name.Trim().TrimEnd(':');
        return GenericTopLevelSectionNames.Contains(trimmed);
    }

    public static List<IngestionDraftIngredientDto> CleanIngredientSections(
        IReadOnlyList<IngestionDraftIngredientDto> ingredients
    )
    {
        if (ingredients == null || ingredients.Count == 0)
        {
            return [];
        }

        // 1. Strip generic top-level headings ("Ingredients", "Base Ingredients", "Main", etc.)
        var cleaned = ingredients
            .Select(i =>
            {
                if (IsGenericTopLevelSection(i.SectionName))
                {
                    return i with { SectionName = null };
                }
                return i;
            })
            .ToList();

        var withSection = cleaned.Where(x => !string.IsNullOrWhiteSpace(x.SectionName)).ToList();

        if (withSection.Count == 0)
        {
            return cleaned;
        }

        // Group by section name (case-insensitive)
        var groups = withSection
            .GroupBy(x => x.SectionName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToList();

        // If all ingredients with a section belong to the exact same single section,
        // and there are no other ingredients or sections:
        if (groups.Count == 1 && withSection.Count == cleaned.Count)
        {
            return cleaned.Select(i => i with { SectionName = null }).ToList();
        }

        // Check if all non-null sections are singletons (each section has only 1 ingredient)
        var allSingletons = groups.All(g => g.Count() == 1);
        var anySelfReferential = groups.Any(g =>
            g.Count() == 1 && IsIngredientSelfReferential(g.Key, g.First())
        );

        // If every section has only 1 ingredient and (multiple sections exist with self-reference, or >= 3 singletons):
        if (allSingletons && ((groups.Count >= 2 && anySelfReferential) || groups.Count >= 3))
        {
            return cleaned.Select(i => i with { SectionName = null }).ToList();
        }

        // Strip individual self-referential singletons
        var result = new List<IngestionDraftIngredientDto>(cleaned.Count);
        foreach (var ing in cleaned)
        {
            if (!string.IsNullOrWhiteSpace(ing.SectionName))
            {
                var groupCount =
                    groups
                        .FirstOrDefault(g =>
                            string.Equals(
                                g.Key,
                                ing.SectionName.Trim(),
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        ?.Count()
                    ?? 0;

                if (groupCount == 1 && IsIngredientSelfReferential(ing.SectionName, ing))
                {
                    result.Add(ing with { SectionName = null });
                    continue;
                }
            }
            result.Add(ing);
        }

        return result;
    }

    public static bool IsIngredientSelfReferential(
        string sectionName,
        IngestionDraftIngredientDto ing
    )
    {
        if (string.IsNullOrWhiteSpace(sectionName))
        {
            return false;
        }

        var secClean = CleanForComparison(sectionName);
        if (string.IsNullOrWhiteSpace(secClean))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(ing.DisplayName))
        {
            var dispClean = CleanForComparison(ing.DisplayName);
            if (
                dispClean == secClean
                || dispClean.Contains(secClean)
                || secClean.Contains(dispClean)
            )
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(ing.ProposedFoodItemName))
        {
            var foodClean = CleanForComparison(ing.ProposedFoodItemName);
            if (
                foodClean == secClean
                || foodClean.Contains(secClean)
                || secClean.Contains(foodClean)
            )
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(ing.MatchedFoodItemName))
        {
            var matchClean = CleanForComparison(ing.MatchedFoodItemName);
            if (
                matchClean == secClean
                || matchClean.Contains(secClean)
                || secClean.Contains(matchClean)
            )
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(ing.OriginalRawText))
        {
            var rawClean = CleanForComparison(ing.OriginalRawText);
            if (rawClean.Contains(secClean))
            {
                return true;
            }
        }

        return false;
    }

    private static string CleanForComparison(string text)
    {
        var cleaned = Regex.Replace(text.ToLowerInvariant(), @"[^\w\s]", " ").Trim();
        var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Singularize);
        return string.Join(" ", words);
    }

    public static List<IngestionDraftStepDto> CleanStepSections(
        IReadOnlyList<IngestionDraftStepDto> steps,
        string? rawSourceText
    )
    {
        if (steps == null || steps.Count == 0)
        {
            return [];
        }

        // 1. Strip generic top-level headings ("Instructions", "Preparation", "Directions", "Method", "Steps", etc.)
        var cleaned = steps
            .Select(s =>
            {
                if (IsGenericTopLevelSection(s.SectionName))
                {
                    return s with { SectionName = null };
                }
                return s;
            })
            .ToList();

        var withSection = cleaned.Where(x => !string.IsNullOrWhiteSpace(x.SectionName)).ToList();

        if (withSection.Count == 0)
        {
            return cleaned;
        }

        var groups = withSection
            .GroupBy(x => x.SectionName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToList();

        // If all steps with a section share the exact same section name, clear it
        if (groups.Count == 1 && withSection.Count == cleaned.Count)
        {
            return cleaned.Select(s => s with { SectionName = null }).ToList();
        }

        // Check if all non-null sections are singletons (each section has only 1 step)
        var allSingletons = groups.All(g => g.Count() == 1);
        if (allSingletons && groups.Count >= 2)
        {
            var hasGerund = groups.Any(g => IsGerundOrActionPhrase(g.Key));
            var inSourceHeadings =
                rawSourceText != null
                && groups.All(g => IsSectionHeadingInSource(g.Key, rawSourceText));

            if (hasGerund || !inSourceHeadings)
            {
                return cleaned.Select(s => s with { SectionName = null }).ToList();
            }
        }

        return cleaned;
    }

    private static bool IsGerundOrActionPhrase(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var firstWord = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return firstWord.EndsWith("ing", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSectionHeadingInSource(string sectionName, string rawSourceText)
    {
        if (string.IsNullOrWhiteSpace(rawSourceText) || string.IsNullOrWhiteSpace(sectionName))
        {
            return false;
        }
        var pattern = $@"(?m)^\s*{Regex.Escape(sectionName.Trim())}\s*:?\s*$";
        return Regex.IsMatch(rawSourceText, pattern, RegexOptions.IgnoreCase);
    }

    private static bool IsUnmeasuredCountableProduce(
        string? originalRawText,
        string? displayName,
        string? preparationNote
    )
    {
        var combined = $"{originalRawText} {displayName} {preparationNote}".Trim();
        if (string.IsNullOrWhiteSpace(combined))
        {
            return false;
        }

        if (!CountableProduceCutRegex.IsMatch(combined))
        {
            return false;
        }

        return CountableWholeProduceFoodRegex.IsMatch(combined);
    }

    private static string SyncInlineTimerLabels(
        string instruction,
        IReadOnlyList<RecipeStepTimerInput> timers
    )
    {
        var segments = StepInstructionParser.ParseSegments(instruction);
        if (segments.Count == 0)
        {
            return instruction;
        }

        var usedTimerIndices = new HashSet<int>();
        var changed = false;
        var builder = new StringBuilder(instruction.Length);

        foreach (var seg in segments)
        {
            if (seg.Token is StepTimerToken timerToken)
            {
                var matchIdx = -1;
                for (var t = 0; t < timers.Count; t++)
                {
                    if (
                        !usedTimerIndices.Contains(t)
                        && timers[t].DurationSeconds == timerToken.Seconds
                    )
                    {
                        matchIdx = t;
                        break;
                    }
                }

                if (matchIdx >= 0)
                {
                    usedTimerIndices.Add(matchIdx);
                    var targetLabel = timers[matchIdx].Label;
                    if (!string.Equals(timerToken.Label, targetLabel, StringComparison.Ordinal))
                    {
                        builder.Append(
                            StepInstructionParser.FormatToken(
                                timerToken with
                                {
                                    Label = targetLabel,
                                }
                            )
                        );
                        changed = true;
                        continue;
                    }
                }
            }
            builder.Append(seg.Text);
        }

        return changed ? builder.ToString() : instruction;
    }

    private static (
        List<IngestionDraftIngredientDto> Ingredients,
        List<IngestionDraftStepDto> Steps
    ) ReconcileIngredientsAndSteps(
        List<IngestionDraftIngredientDto> ingredients,
        List<IngestionDraftStepDto> steps,
        string? rawSourceText
    )
    {
        if (ingredients.Count == 0)
        {
            return (ingredients, steps);
        }

        var workingIngredients = new List<IngestionDraftIngredientDto>(ingredients);
        var workingSteps = new List<IngestionDraftStepDto>(steps);
        var sourceLines = string.IsNullOrWhiteSpace(rawSourceText)
            ? []
            : RecipeExtractionDiagnostics
                .GetSourceIngredientLines(rawSourceText)
                .Select(l => Regex.Replace(l.Trim().TrimStart('-', ' '), @"\s+", " "))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 1. Merge duplicate measured + unmeasured rows for the same food in the same section
        // (e.g. "Kosher salt" with null qty + "1 1/2 teaspoons salt" with 1.5 tsp in POTATOES)
        // and collapse duplicate "or" alternative rows sharing the exact same OriginalRawText.
        var remap = new Dictionary<int, int>();
        var removed = new HashSet<int>();

        for (var i = 0; i < workingIngredients.Count; i++)
        {
            if (removed.Contains(i))
            {
                continue;
            }
            var first = workingIngredients[i];

            for (var j = i + 1; j < workingIngredients.Count; j++)
            {
                if (removed.Contains(j))
                {
                    continue;
                }
                var second = workingIngredients[j];

                // Case A: Exact same OriginalRawText containing " or " where both are unmeasured
                var sameRawOr =
                    !string.IsNullOrWhiteSpace(first.OriginalRawText)
                    && string.Equals(
                        first.OriginalRawText.Trim(),
                        second.OriginalRawText?.Trim(),
                        StringComparison.OrdinalIgnoreCase
                    )
                    && Regex.IsMatch(first.OriginalRawText, @"\bor\b", RegexOptions.IgnoreCase)
                    && first.Quantity is null
                    && second.Quantity is null;

                if (sameRawOr)
                {
                    var altNote = $"or {second.DisplayName}";
                    var mergedPrep =
                        string.IsNullOrWhiteSpace(first.PreparationNote) ? altNote
                        : first.PreparationNote.Contains(
                            second.DisplayName,
                            StringComparison.OrdinalIgnoreCase
                        )
                            ? first.PreparationNote
                        : $"{first.PreparationNote}; {altNote}";
                    workingIngredients[i] = first with { PreparationNote = mergedPrep };
                    first = workingIngredients[i];
                    remap[j] = i;
                    removed.Add(j);
                    continue;
                }

                // Case B: Same food & same section, one is measured and one is unmeasured,
                // and the measured row's OriginalRawText is not its own distinct source line
                var sameSection = string.Equals(
                    NormalizeSectionName(first.SectionName),
                    NormalizeSectionName(second.SectionName),
                    StringComparison.OrdinalIgnoreCase
                );
                var sameFood =
                    string.Equals(
                        first.DisplayName,
                        second.DisplayName,
                        StringComparison.OrdinalIgnoreCase
                    )
                    || (
                        first.MatchedFoodItemId.HasValue
                        && first.MatchedFoodItemId == second.MatchedFoodItemId
                    )
                    || (
                        !string.IsNullOrWhiteSpace(first.ProposedFoodItemName)
                        && string.Equals(
                            first.ProposedFoodItemName,
                            second.ProposedFoodItemName,
                            StringComparison.OrdinalIgnoreCase
                        )
                    );

                if (
                    sameSection
                    && sameFood
                    && (first.Quantity is null) != (second.Quantity is null)
                )
                {
                    var measuredIdx = first.Quantity.HasValue ? i : j;
                    var unmeasuredIdx = first.Quantity.HasValue ? j : i;
                    var measured = workingIngredients[measuredIdx];
                    var unmeasured = workingIngredients[unmeasuredIdx];

                    var unmeasuredIsVerbatim =
                        !string.IsNullOrWhiteSpace(unmeasured.OriginalRawText)
                        && (
                            sourceLines.Count == 0
                            || sourceLines.Contains(
                                Regex.Replace(
                                    unmeasured.OriginalRawText.Trim().TrimStart('-', ' '),
                                    @"\s+",
                                    " "
                                )
                            )
                        );
                    var measuredIsVerbatim =
                        !string.IsNullOrWhiteSpace(measured.OriginalRawText)
                        && (
                            sourceLines.Count == 0
                            || sourceLines.Contains(
                                Regex.Replace(
                                    measured.OriginalRawText.Trim().TrimStart('-', ' '),
                                    @"\s+",
                                    " "
                                )
                            )
                        );

                    if (unmeasuredIsVerbatim && !measuredIsVerbatim)
                    {
                        workingIngredients[unmeasuredIdx] = unmeasured with
                        {
                            Quantity = measured.Quantity,
                            MaxQuantity = measured.MaxQuantity,
                            Unit = measured.Unit,
                            CustomUnitName = measured.CustomUnitName,
                            AlternativeMeasurement =
                                unmeasured.AlternativeMeasurement
                                ?? measured.AlternativeMeasurement,
                        };
                        remap[measuredIdx] = unmeasuredIdx;
                        removed.Add(measuredIdx);
                        if (measuredIdx == i)
                        {
                            break;
                        }
                    }
                }
            }
        }

        if (removed.Count > 0)
        {
            var indexMap = new Dictionary<int, int>();
            var compacted = new List<IngestionDraftIngredientDto>(
                workingIngredients.Count - removed.Count
            );
            for (var oldIdx = 0; oldIdx < workingIngredients.Count; oldIdx++)
            {
                if (removed.Contains(oldIdx))
                {
                    continue;
                }
                indexMap[oldIdx] = compacted.Count;
                compacted.Add(workingIngredients[oldIdx]);
            }

            foreach (var (fromIdx, toIdx) in remap)
            {
                if (indexMap.TryGetValue(toIdx, out var mappedTarget))
                {
                    indexMap[fromIdx] = mappedTarget;
                }
            }

            for (var s = 0; s < workingSteps.Count; s++)
            {
                var step = workingSteps[s];
                var newIndices = (step.IngredientIndices ?? [])
                    .Where(idx => indexMap.ContainsKey(idx))
                    .Select(idx => indexMap[idx])
                    .Distinct()
                    .OrderBy(idx => idx)
                    .ToList();

                var newAmounts = new List<RecipeStepIngredientAmountInput>();
                var seenAmountIdx = new HashSet<int>();
                foreach (var amt in step.IngredientAmounts ?? [])
                {
                    if (
                        indexMap.TryGetValue(amt.IngredientIndex, out var mappedIdx)
                        && seenAmountIdx.Add(mappedIdx)
                    )
                    {
                        newAmounts.Add(amt with { IngredientIndex = mappedIdx });
                    }
                }

                workingSteps[s] = step with
                {
                    IngredientIndices = newIndices,
                    IngredientAmounts = newAmounts,
                };
            }

            workingIngredients = compacted;
        }

        CleanAndReconcileStepTokens(workingIngredients, workingSteps);

        // 2. Backfill unmeasured ingredient rows from concrete measured inline step tokens
        for (var i = 0; i < workingIngredients.Count; i++)
        {
            var ing = workingIngredients[i];
            if (ing.Quantity.HasValue)
            {
                continue;
            }

            var stepMeasurements = new List<(decimal Qty, MeasurementUnit Unit, string? Custom)>();
            foreach (var step in workingSteps)
            {
                var tokens = StepInstructionParser.ExtractTokens(step.Instruction);
                foreach (var token in tokens)
                {
                    if (token is not StepIngredientToken ingToken)
                    {
                        continue;
                    }
                    if (
                        !ingToken.Amount.HasValue
                        || ingToken.Amount.Value <= 0
                        || !ingToken.Unit.HasValue
                    )
                    {
                        continue;
                    }
                    // Ignore LLM "0.01 pinch/dash" to-taste placeholders
                    if (
                        ingToken.Unit.Value is MeasurementUnit.Pinch or MeasurementUnit.Dash
                        && ingToken.Amount.Value <= 0.1m
                    )
                    {
                        continue;
                    }

                    var matchedIdx = FindBestMatchingIngredientIndex(
                        ingToken,
                        step.SectionName,
                        workingIngredients,
                        step.IngredientIndices,
                        step.Instruction
                    );
                    if (matchedIdx == i)
                    {
                        stepMeasurements.Add(
                            (ingToken.Amount.Value, ingToken.Unit.Value, ingToken.CustomUnitName)
                        );
                    }
                }
            }

            if (stepMeasurements.Count > 0)
            {
                var targetUnit = stepMeasurements[0].Unit;
                var customUnit = stepMeasurements[0].Custom;
                decimal sum = 0m;
                var allCompatible = true;
                foreach (var (qty, unit, custom) in stepMeasurements)
                {
                    if (
                        unit == targetUnit
                        && string.Equals(custom, customUnit, StringComparison.OrdinalIgnoreCase)
                    )
                    {
                        sum += qty;
                    }
                    else if (
                        unit != MeasurementUnit.Custom
                        && targetUnit != MeasurementUnit.Custom
                        && CulinaryMath.TryConvertQuantity(qty, unit, targetUnit, out var converted)
                    )
                    {
                        sum += converted;
                    }
                    else
                    {
                        allCompatible = false;
                        break;
                    }
                }

                if (allCompatible && sum > 0m)
                {
                    workingIngredients[i] = ing with
                    {
                        Quantity = Math.Round(sum, 4),
                        Unit = targetUnit,
                        CustomUnitName = customUnit,
                    };
                }
            }
        }

        AutoLinkMissingIngredientUsage(workingIngredients, workingSteps);

        return (workingIngredients, workingSteps);
    }

    public static string NormalizeLineForSourceMatch(string? line) =>
        Regex.Replace((line ?? "").Trim().TrimStart('-', ' '), @"\s+", " ");

    private static IReadOnlyList<LlmExtractedIngredientItem> RestoreVerbatimSourceLines(
        IReadOnlyList<LlmExtractedIngredientItem> rawIngredients,
        string? rawSourceText
    )
    {
        if (rawIngredients.Count == 0 || string.IsNullOrWhiteSpace(rawSourceText))
        {
            return rawIngredients;
        }

        var sourceLines = RecipeExtractionDiagnostics.GetSourceIngredientLines(rawSourceText);
        if (sourceLines.Count == 0)
        {
            return rawIngredients;
        }

        var sourceByNorm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in sourceLines)
        {
            var norm = NormalizeLineForSourceMatch(line);
            if (!string.IsNullOrWhiteSpace(norm) && !sourceByNorm.ContainsKey(norm))
            {
                sourceByNorm[norm] = line;
            }
        }

        var matchedSourceNorms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ing in rawIngredients)
        {
            var norm = NormalizeLineForSourceMatch(ing.OriginalRawText);
            if (!string.IsNullOrWhiteSpace(norm) && sourceByNorm.ContainsKey(norm))
            {
                matchedSourceNorms.Add(norm);
            }
        }

        var result = new List<LlmExtractedIngredientItem>(rawIngredients.Count);
        foreach (var ing in rawIngredients)
        {
            var rawText = ing.OriginalRawText?.Trim();
            if (string.IsNullOrWhiteSpace(rawText))
            {
                result.Add(ing);
                continue;
            }

            var normRaw = NormalizeLineForSourceMatch(rawText);
            if (sourceByNorm.ContainsKey(normRaw))
            {
                result.Add(ing);
                continue;
            }

            // Case 1: Strip trailing LLM parenthetical note e.g. "4 large eggs, separated (used as yolks)"
            var strippedParen = Regex.Replace(rawText, @"\s*\([^()]+\)\s*$", "").Trim();
            var normStripped = NormalizeLineForSourceMatch(strippedParen);
            if (
                !string.IsNullOrWhiteSpace(normStripped)
                && sourceByNorm.TryGetValue(normStripped, out var matchedFromParen)
            )
            {
                matchedSourceNorms.Add(normStripped);
                result.Add(ing with { OriginalRawText = matchedFromParen });
                continue;
            }

            // Case 2: Split compound "X and Y" / "X mixed with Y" source line
            string? matchedCompound = null;
            if (normRaw.Length >= 3)
            {
                var escapedRaw = Regex.Escape(normRaw);
                foreach (var sourceLine in sourceLines)
                {
                    var normSource = NormalizeLineForSourceMatch(sourceLine);
                    if (
                        matchedSourceNorms.Contains(normSource)
                        || (
                            !normSource.Contains(" and ", StringComparison.OrdinalIgnoreCase)
                            && !normSource.Contains(" with ", StringComparison.OrdinalIgnoreCase)
                        )
                    )
                    {
                        continue;
                    }

                    if (
                        Regex.IsMatch(
                            normSource,
                            $@"(?:^{escapedRaw}\s+(?:and|(?:mixed\s+)?with)\b|\b(?:and|(?:mixed\s+)?with)\s+{escapedRaw}$)",
                            RegexOptions.IgnoreCase,
                            HelperRegexTimeout
                        )
                    )
                    {
                        matchedCompound = sourceLine;
                        break;
                    }
                }
            }

            result.Add(
                matchedCompound is not null ? ing with { OriginalRawText = matchedCompound } : ing
            );
        }

        return result;
    }

    private static string CleanAlternativeFoodName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return "Ingredient";
        }

        var cleaned = rawName.Trim();
        if (Regex.IsMatch(cleaned, @"\s+\bor\b\s+", RegexOptions.IgnoreCase, HelperRegexTimeout))
        {
            var parts = Regex.Split(
                cleaned,
                @"\s+\bor\b\s+",
                RegexOptions.IgnoreCase,
                HelperRegexTimeout
            );
            var chosen =
                parts.FirstOrDefault(p =>
                    !string.IsNullOrWhiteSpace(p)
                    && !Regex.IsMatch(
                        p,
                        @"\b(?:recipes?\s+follows?|this\s+page|recipe|batch)\b",
                        RegexOptions.IgnoreCase,
                        HelperRegexTimeout
                    )
                ) ?? parts.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
            if (!string.IsNullOrWhiteSpace(chosen))
            {
                cleaned = chosen.Trim();
            }
        }

        cleaned = Regex.Replace(
            cleaned,
            @"\s*\((?:optional|recipes?\s+follows?|this\s+page[^()]*)\)\s*",
            " ",
            RegexOptions.IgnoreCase,
            HelperRegexTimeout
        );
        var spans = SourceMeasurementParser.FindSpans(cleaned);
        if (spans.Count > 0 && spans[0].Index == 0)
        {
            cleaned = cleaned[spans[0].Length..].TrimStart();
        }
        cleaned = Regex.Replace(
            cleaned,
            @"^store-bought\s+",
            "",
            RegexOptions.IgnoreCase,
            HelperRegexTimeout
        );
        return string.IsNullOrWhiteSpace(cleaned) ? "Ingredient" : cleaned.Trim();
    }

    private sealed class StepTokenOccurrence(
        int stepIndex,
        int segmentIndex,
        StepIngredientToken token,
        int matchedIngredientIndex,
        decimal? convertedAmountInRowUnit
    )
    {
        public int StepIndex { get; } = stepIndex;
        public int SegmentIndex { get; } = segmentIndex;
        public StepIngredientToken Token { get; set; } = token;
        public int MatchedIngredientIndex { get; } = matchedIngredientIndex;
        public decimal? ConvertedAmountInRowUnit { get; set; } = convertedAmountInRowUnit;
        public bool IsModified { get; set; }
        public bool IsDemoted { get; set; }
    }

    private static bool TryConvertAmountToIngredientRowUnit(
        decimal amount,
        MeasurementUnit unit,
        IngestionDraftIngredientDto ing,
        out decimal converted
    )
    {
        converted = 0m;
        if (!ing.Unit.HasValue)
        {
            return false;
        }

        if (unit == ing.Unit.Value)
        {
            converted = amount;
            return true;
        }

        if (
            unit != MeasurementUnit.Custom
            && ing.Unit.Value != MeasurementUnit.Custom
            && CulinaryMath.TryConvertQuantity(amount, unit, ing.Unit.Value, out converted)
        )
        {
            return true;
        }

        if (
            ing.Quantity is > 0
            && ing.AlternativeMeasurement is { Quantity: > 0, Unit: { } altUnit } alt
            && CulinaryMath.TryConvertQuantity(amount, unit, altUnit, out var inAlt)
        )
        {
            converted = inAlt * ing.Quantity.Value / alt.Quantity!.Value;
            return true;
        }

        return false;
    }

    private static void CleanAndReconcileStepTokens(
        List<IngestionDraftIngredientDto> workingIngredients,
        List<IngestionDraftStepDto> workingSteps
    )
    {
        if (workingSteps.Count == 0 || workingIngredients.Count == 0)
        {
            return;
        }

        var stepSegments = new List<IReadOnlyList<StepInstructionSegment>>(workingSteps.Count);
        var occurrences = new List<StepTokenOccurrence>();

        for (var sIdx = 0; sIdx < workingSteps.Count; sIdx++)
        {
            var step = workingSteps[sIdx];
            var segments = StepInstructionParser.ParseSegments(step.Instruction);
            stepSegments.Add(segments);

            for (var segIdx = 0; segIdx < segments.Count; segIdx++)
            {
                if (segments[segIdx].Token is not StepIngredientToken ingToken)
                {
                    continue;
                }

                var matchedIdx = FindBestMatchingIngredientIndex(
                    ingToken,
                    step.SectionName,
                    workingIngredients,
                    step.IngredientIndices,
                    step.Instruction
                );

                decimal? convertedAmt = null;
                if (
                    matchedIdx >= 0
                    && ingToken.Amount is > 0
                    && ingToken.Unit.HasValue
                    && TryConvertAmountToIngredientRowUnit(
                        ingToken.Amount.Value,
                        ingToken.Unit.Value,
                        workingIngredients[matchedIdx],
                        out var amtInRow
                    )
                )
                {
                    convertedAmt = amtInRow;
                }

                occurrences.Add(
                    new StepTokenOccurrence(sIdx, segIdx, ingToken, matchedIdx, convertedAmt)
                );
            }
        }

        // 1. Invert Amount <-> DisplayAmount when Amount repeats full ingredient row while DisplayAmount < Amount causing sum > rowHigh
        foreach (
            var group in occurrences
                .Where(o => o.MatchedIngredientIndex >= 0)
                .GroupBy(o => o.MatchedIngredientIndex)
        )
        {
            var ing = workingIngredients[group.Key];
            if (ing.Quantity is not > 0 || !ing.Unit.HasValue)
            {
                continue;
            }

            var rowLow = ing.Quantity.Value;
            var rowHigh = ing.MaxQuantity ?? rowLow;
            var tol = Math.Max(0.01m, rowHigh * 0.02m);
            var totalSum = group
                .Where(o => o.ConvertedAmountInRowUnit.HasValue)
                .Sum(o => o.ConvertedAmountInRowUnit!.Value);

            if (totalSum <= rowHigh + tol)
            {
                continue;
            }

            foreach (var occ in group)
            {
                if (
                    occ.Token.Amount is > 0
                    && occ.Token.Unit.HasValue
                    && occ.Token.DisplayAmount is > 0
                    && occ.ConvertedAmountInRowUnit is { } fullCandidate
                    && fullCandidate >= rowLow - tol
                    && fullCandidate <= rowHigh + tol
                )
                {
                    var dispUnit = occ.Token.DisplayUnit ?? occ.Token.Unit.Value;
                    if (
                        TryConvertAmountToIngredientRowUnit(
                            occ.Token.DisplayAmount.Value,
                            dispUnit,
                            ing,
                            out var dispInRowUnit
                        )
                        && dispInRowUnit < fullCandidate - tol
                    )
                    {
                        occ.Token = occ.Token with
                        {
                            Amount = occ.Token.DisplayAmount,
                            Unit = dispUnit,
                            CustomUnitName =
                                occ.Token.DisplayCustomUnitName ?? occ.Token.CustomUnitName,
                        };
                        occ.ConvertedAmountInRowUnit = dispInRowUnit;
                        occ.IsModified = true;
                    }
                }
            }
        }

        // 2. Same-step duplicate tag deduplication
        foreach (
            var stepGroup in occurrences
                .Where(o => !o.IsDemoted && o.MatchedIngredientIndex >= 0)
                .GroupBy(o => (o.StepIndex, o.MatchedIngredientIndex))
        )
        {
            var items = stepGroup.ToList();
            if (items.Count < 2)
            {
                continue;
            }

            var ing = workingIngredients[stepGroup.Key.MatchedIngredientIndex];
            var rowLow = ing.Quantity ?? 0m;
            var rowHigh = ing.MaxQuantity ?? rowLow;
            var tol = Math.Max(0.01m, rowHigh * 0.02m);

            var measured = items
                .Where(o => o.Token.Amount is > 0 && o.Token.Unit.HasValue)
                .ToList();

            if (measured.Count > 0)
            {
                foreach (
                    var unmeasured in items.Where(o =>
                        !(o.Token.Amount is > 0 && o.Token.Unit.HasValue)
                    )
                )
                {
                    unmeasured.IsDemoted = true;
                }

                if (measured.Count >= 2 && rowHigh > 0)
                {
                    var measuredWithConv = measured
                        .Where(m => m.ConvertedAmountInRowUnit.HasValue)
                        .ToList();
                    var stepSum = measuredWithConv.Sum(m => m.ConvertedAmountInRowUnit!.Value);
                    var allFullBatch =
                        measuredWithConv.Count == measured.Count
                        && measuredWithConv.All(m =>
                            m.ConvertedAmountInRowUnit!.Value >= rowLow - tol
                            && m.ConvertedAmountInRowUnit!.Value <= rowHigh + tol
                        );
                    var hasEqualOverflow =
                        stepSum > rowHigh + tol
                        && measured.All(m =>
                            m.Token.Amount == measured[0].Token.Amount
                            && m.Token.Unit == measured[0].Token.Unit
                        );

                    if (allFullBatch || hasEqualOverflow)
                    {
                        for (var k = 1; k < measured.Count; k++)
                        {
                            measured[k].IsDemoted = true;
                        }
                    }
                }
            }
            else
            {
                for (var k = 1; k < items.Count; k++)
                {
                    items[k].IsDemoted = true;
                }
            }
        }

        // 3. Cross-step repeat full-batch mentions and unmeasured narrative over-tagging cap
        foreach (
            var ingGroup in occurrences
                .Where(o => !o.IsDemoted && o.MatchedIngredientIndex >= 0)
                .GroupBy(o => o.MatchedIngredientIndex)
        )
        {
            var active = ingGroup.ToList();
            var ing = workingIngredients[ingGroup.Key];
            var measured = active
                .Where(o => o.Token.Amount is > 0 && o.Token.Unit.HasValue)
                .ToList();

            if (ing.Quantity is > 0 && measured.Count >= 2)
            {
                var rowLow = ing.Quantity.Value;
                var rowHigh = ing.MaxQuantity ?? rowLow;
                var tol = Math.Max(0.01m, rowHigh * 0.02m);
                var comparable = measured.Where(m => m.ConvertedAmountInRowUnit.HasValue).ToList();
                var totalSum = comparable.Sum(m => m.ConvertedAmountInRowUnit!.Value);

                if (comparable.Count == measured.Count && totalSum > rowHigh + tol)
                {
                    var allFullBatch = comparable.All(m =>
                        m.ConvertedAmountInRowUnit!.Value >= rowLow - tol
                        && m.ConvertedAmountInRowUnit!.Value <= rowHigh + tol
                    );
                    var firstBatchThenDivided =
                        !allFullBatch
                        && comparable[0].ConvertedAmountInRowUnit!.Value >= rowLow - tol
                        && comparable[0].ConvertedAmountInRowUnit!.Value <= rowHigh + tol
                        && comparable.Skip(1).Sum(m => m.ConvertedAmountInRowUnit!.Value)
                            <= rowHigh + tol;

                    if (allFullBatch)
                    {
                        StripTokenMeasurement(comparable[1]);
                        for (var k = 2; k < comparable.Count; k++)
                        {
                            comparable[k].IsDemoted = true;
                        }
                    }
                    else if (firstBatchThenDivided)
                    {
                        StripTokenMeasurement(comparable[0]);
                    }
                }
            }

            var remainingUnmeasured = active
                .Where(o =>
                    !o.IsDemoted && !o.Token.Amount.HasValue && !o.Token.DisplayAmount.HasValue
                )
                .ToList();
            if (remainingUnmeasured.Count >= 3)
            {
                for (var k = 2; k < remainingUnmeasured.Count; k++)
                {
                    remainingUnmeasured[k].IsDemoted = true;
                }
            }
        }

        // 4. Rebuild modified instructions and synchronize IngredientIndices
        var occByStepSeg = occurrences.ToDictionary(o => (o.StepIndex, o.SegmentIndex));
        for (var sIdx = 0; sIdx < workingSteps.Count; sIdx++)
        {
            var segments = stepSegments[sIdx];
            var stepChanged = occurrences.Any(o =>
                o.StepIndex == sIdx && (o.IsModified || o.IsDemoted)
            );
            if (!stepChanged)
            {
                continue;
            }

            var sb = new StringBuilder();
            var activeTokenIndices = new HashSet<int>();
            for (var segIdx = 0; segIdx < segments.Count; segIdx++)
            {
                if (occByStepSeg.TryGetValue((sIdx, segIdx), out var occ))
                {
                    if (occ.IsDemoted)
                    {
                        var plain = !string.IsNullOrWhiteSpace(occ.Token.DisplayText)
                            ? occ.Token.DisplayText.Trim()
                            : occ.Token.Ingredient.Trim();
                        sb.Append(plain);
                    }
                    else
                    {
                        sb.Append(
                            occ.IsModified
                                ? StepInstructionParser.FormatToken(occ.Token)
                                : segments[segIdx].Text
                        );
                        if (occ.MatchedIngredientIndex >= 0)
                        {
                            activeTokenIndices.Add(occ.MatchedIngredientIndex);
                        }
                    }
                }
                else
                {
                    sb.Append(segments[segIdx].Text);
                }
            }

            var currentStep = workingSteps[sIdx];
            var mergedIndices = (currentStep.IngredientIndices ?? [])
                .Concat(activeTokenIndices)
                .Where(idx => idx >= 0 && idx < workingIngredients.Count)
                .Distinct()
                .OrderBy(idx => idx)
                .ToList();

            workingSteps[sIdx] = currentStep with
            {
                Instruction = sb.ToString(),
                IngredientIndices = mergedIndices,
            };
        }
    }

    private static void StripTokenMeasurement(StepTokenOccurrence occ)
    {
        occ.Token = occ.Token with
        {
            Amount = null,
            Unit = null,
            CustomUnitName = null,
            DisplayAmount = null,
            DisplayUnit = null,
            DisplayCustomUnitName = null,
        };
        occ.ConvertedAmountInRowUnit = null;
        occ.IsModified = true;
    }

    private static void AutoLinkMissingIngredientUsage(
        List<IngestionDraftIngredientDto> workingIngredients,
        List<IngestionDraftStepDto> workingSteps
    )
    {
        if (workingIngredients.Count == 0 || workingSteps.Count == 0)
        {
            return;
        }

        var stepUsedIndices = new HashSet<int>[workingSteps.Count];
        for (var sIdx = 0; sIdx < workingSteps.Count; sIdx++)
        {
            var step = workingSteps[sIdx];
            var used = new HashSet<int>();
            foreach (var idx in step.IngredientIndices ?? [])
            {
                if (idx >= 0 && idx < workingIngredients.Count)
                {
                    used.Add(idx);
                }
            }
            foreach (var amt in step.IngredientAmounts ?? [])
            {
                if (amt.IngredientIndex >= 0 && amt.IngredientIndex < workingIngredients.Count)
                {
                    used.Add(amt.IngredientIndex);
                }
            }
            foreach (
                var token in StepInstructionParser
                    .ExtractTokens(step.Instruction)
                    .OfType<StepIngredientToken>()
            )
            {
                var matchedIdx = FindBestMatchingIngredientIndex(
                    token,
                    step.SectionName,
                    workingIngredients,
                    step.IngredientIndices,
                    step.Instruction
                );
                if (matchedIdx >= 0)
                {
                    used.Add(matchedIdx);
                }
            }
            stepUsedIndices[sIdx] = used;
        }

        var allIngredientNames = workingIngredients
            .Select(i =>
                ((string?)i.DisplayName, (string?)(i.MatchedFoodItemName ?? i.ProposedFoodItemName))
            )
            .ToList();

        for (var i = 0; i < workingIngredients.Count; i++)
        {
            var ing = workingIngredients[i];
            if (ing.Quantity is not > 0 || stepUsedIndices.Any(set => set.Contains(i)))
            {
                continue;
            }

            // Fallback 1: Co-split compound source line sharing OriginalRawText
            var linked = false;
            var normRaw = NormalizeLineForSourceMatch(ing.OriginalRawText);
            if (!string.IsNullOrWhiteSpace(normRaw))
            {
                for (var j = 0; j < workingIngredients.Count; j++)
                {
                    if (
                        j != i
                        && string.Equals(
                            NormalizeLineForSourceMatch(workingIngredients[j].OriginalRawText),
                            normRaw,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        for (var sIdx = 0; sIdx < workingSteps.Count; sIdx++)
                        {
                            if (stepUsedIndices[sIdx].Contains(j))
                            {
                                AddStepIngredientIndex(workingSteps, stepUsedIndices, sIdx, i);
                                linked = true;
                            }
                        }
                    }
                }
            }

            if (linked)
            {
                continue;
            }

            // Fallback 2: Separated whole eggs mentioned in step prose as egg yolks / egg whites
            var isWholeEgg = IsWholeEggIngredient(
                ing.DisplayName,
                ing.MatchedFoodItemName ?? ing.ProposedFoodItemName
            );
            var hasExplicitPart =
                isWholeEgg
                && allIngredientNames.Any(pair =>
                {
                    var other = $"{pair.Item1} {pair.Item2}".Trim();
                    return Regex.IsMatch(
                        other,
                        @"\b(?:egg\s+)?(?:yolks?|egg\s+whites?)\b",
                        RegexOptions.IgnoreCase,
                        HelperRegexTimeout
                    );
                });

            for (var sIdx = 0; sIdx < workingSteps.Count; sIdx++)
            {
                var plainText = StepInstructionParser.ToPlainText(workingSteps[sIdx].Instruction);
                if (string.IsNullOrWhiteSpace(plainText))
                {
                    continue;
                }

                var isEggMatch =
                    isWholeEgg && !hasExplicitPart && IsSeparatedEggPartTerm(plainText);
                var isProseMatch =
                    !string.IsNullOrWhiteSpace(ing.DisplayName)
                    && ContainsWholeWordTerm(plainText, ing.DisplayName.Trim());

                if (isEggMatch || isProseMatch)
                {
                    AddStepIngredientIndex(workingSteps, stepUsedIndices, sIdx, i);
                }
            }
        }
    }

    private static bool ContainsWholeWordTerm(string text, string term) =>
        term.Length >= 3
        && Regex.IsMatch(
            text,
            $@"\b{Regex.Escape(term)}(?:e?s)?\b",
            RegexOptions.IgnoreCase,
            HelperRegexTimeout
        );

    private static void AddStepIngredientIndex(
        List<IngestionDraftStepDto> workingSteps,
        HashSet<int>[] stepUsedIndices,
        int stepIndex,
        int ingredientIndex
    )
    {
        stepUsedIndices[stepIndex].Add(ingredientIndex);
        var current = workingSteps[stepIndex];
        var next = (current.IngredientIndices ?? [])
            .Append(ingredientIndex)
            .Distinct()
            .OrderBy(idx => idx)
            .ToList();
        workingSteps[stepIndex] = current with { IngredientIndices = next };
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

    public static string SplitPascalCase(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        // If the string already contains whitespace or hyphens, return trimmed
        if (text.Contains(' ') || text.Contains('-') || text.Contains('_'))
        {
            return text.Trim();
        }

        // Match lower-to-upper transition e.g. "OnePot" -> "One Pot", "BBQSauce" -> "BBQ Sauce"
        var result = Regex.Replace(text, @"(?<=[a-z0-9])([A-Z])", " $1");
        result = Regex.Replace(result, @"(?<=[A-Z])([A-Z][a-z])", " $1");
        return result.Trim();
    }

    public static string ToTagDisplayCase(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var split = SplitPascalCase(text.Trim());
        var words = split.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var segments = words[i].Split('-', StringSplitOptions.RemoveEmptyEntries);
            for (var j = 0; j < segments.Length; j++)
            {
                var s = segments[j];
                if (s.Equals("bbq", StringComparison.OrdinalIgnoreCase))
                {
                    segments[j] = "BBQ";
                }
                else
                {
                    var lower = s.ToLowerInvariant();
                    segments[j] = char.ToUpperInvariant(lower[0]) + lower[1..];
                }
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
            // Course
            "breakfast"
            or "brunch"
            or "lunch"
            or "dinner"
            or "main"
            or "main dish"
            or "main course"
            or "entree"
            or "side"
            or "side dish"
            or "appetizer"
            or "starter"
            or "snack"
            or "midnight snack"
            or "dessert"
            or "beverage"
            or "drink"
            or "cocktail"
            or "mocktail"
            or "soup"
            or "stew"
            or "salad"
            or "sandwich"
            or "bread"
            or "baked good"
            or "baked goods"
            or "sauce"
            or "condiment"
            or "dip"
            or "dressing"
            or "spread"
            or "marinade" => TagCategory.Course,

            // Cuisine
            "american"
            or "southern"
            or "bbq"
            or "tex-mex"
            or "mexican"
            or "cajun"
            or "creole"
            or "caribbean"
            or "south american"
            or "cuban"
            or "brazilian"
            or "hawaiian"
            or "italian"
            or "french"
            or "spanish"
            or "tapas"
            or "greek"
            or "german"
            or "eastern european"
            or "british"
            or "irish"
            or "scandinavian"
            or "nordic"
            or "east asian"
            or "chinese"
            or "japanese"
            or "korean"
            or "southeast asian"
            or "thai"
            or "vietnamese"
            or "indonesian"
            or "filipino"
            or "south asian"
            or "indian"
            or "pakistani"
            or "mediterranean"
            or "middle eastern"
            or "lebanese"
            or "turkish"
            or "persian"
            or "north african"
            or "moroccan"
            or "asian"
            or "latin" => TagCategory.Cuisine,

            // Dietary
            "vegetarian"
            or "vegan"
            or "pescatarian"
            or "gluten-free"
            or "gluten free"
            or "dairy-free"
            or "dairy free"
            or "nut-free"
            or "nut free"
            or "peanut-free"
            or "peanut free"
            or "tree-nut-free"
            or "tree nut free"
            or "egg-free"
            or "egg free"
            or "soy-free"
            or "soy free"
            or "shellfish-free"
            or "shellfish free"
            or "fish-free"
            or "fish free"
            or "sesame-free"
            or "sesame free"
            or "low-carb"
            or "low carb"
            or "keto"
            or "paleo"
            or "whole30"
            or "halal"
            or "kosher"
            or "high-protein"
            or "high protein"
            or "low-sodium"
            or "low sodium"
            or "low-fat"
            or "low fat"
            or "low-sugar"
            or "low sugar"
            or "sugar-free"
            or "sugar free"
            or "diabetic-friendly"
            or "diabetic friendly"
            or "gut-friendly"
            or "gut friendly"
            or "heart-healthy"
            or "heart healthy"
            or "high-fiber"
            or "high fiber"
            or "healthy"
            or "alcohol"
            or "alocohol"
            or "alcoholic"
            or "contains alcohol" => TagCategory.Dietary,

            // Equipment
            "slow cooker"
            or "instant pot"
            or "pressure cooker"
            or "air fryer"
            or "grill"
            or "grilling"
            or "charcoal grill"
            or "gas grill"
            or "smoker"
            or "smoking"
            or "campfire"
            or "dutch oven"
            or "sheet pan"
            or "one-pot"
            or "one pot"
            or "one-pan"
            or "one pan"
            or "no-cook"
            or "no cook"
            or "minimal dish"
            or "minimal dishes"
            or "skillet"
            or "cast iron"
            or "carbon steel"
            or "stainless steel"
            or "wok"
            or "sous vide"
            or "stand mixer"
            or "food processor"
            or "blender"
            or "toaster oven"
            or "microwave"
            or "waffle maker"
            or "sauté"
            or "saute"
            or "braise"
            or "deep-fry"
            or "deep fry"
            or "shallow-fry"
            or "shallow fry"
            or "pan-sear"
            or "pan sear"
            or "stir-fry"
            or "stir fry"
            or "boil & simmer"
            or "boil"
            or "simmer"
            or "steamed"
            or "steam"
            or "baking"
            or "bake"
            or "roasting"
            or "roast"
            or "broiling"
            or "broil" => TagCategory.Equipment,

            // Protein & Base
            "chicken breast"
            or "chicken thigh"
            or "chicken"
            or "turkey"
            or "duck"
            or "poultry"
            or "beef"
            or "steak"
            or "ground beef"
            or "pork"
            or "pork chop"
            or "pork tenderloin"
            or "pork shoulder"
            or "pulled pork"
            or "lamb"
            or "sausage"
            or "bacon"
            or "ham"
            or "veal"
            or "salmon"
            or "white fish"
            or "cod"
            or "halibut"
            or "tilapia"
            or "shrimp"
            or "prawn"
            or "scallop"
            or "tuna"
            or "crab"
            or "lobster"
            or "mussel"
            or "clam"
            or "seafood"
            or "fish"
            or "tofu"
            or "tempeh"
            or "lentil"
            or "bean"
            or "black bean"
            or "chickpea"
            or "edamame"
            or "pasta"
            or "noodle"
            or "rice"
            or "potato"
            or "sweet potato"
            or "grain"
            or "quinoa"
            or "farro"
            or "couscous"
            or "pizza"
            or "polenta" => TagCategory.Protein,

            // Occasion & Season
            "spring"
            or "summer"
            or "autumn"
            or "fall"
            or "winter"
            or "comfort food"
            or "light & refreshing"
            or "cozy & warming"
            or "cozy"
            or "warming"
            or "rainy day"
            or "crowd-pleaser"
            or "crowd pleaser"
            or "feeding a crowd"
            or "potluck"
            or "date night"
            or "dinner party"
            or "holiday"
            or "thanksgiving"
            or "christmas"
            or "4th of july"
            or "game day"
            or "finger food"
            or "weeknight"
            or "weeknight dinner"
            or "camping"
            or "picnic"
            or "road trip"
            or "packed lunch"
            or "bento" => TagCategory.Occasion,

            // Meal Prep & Preservation
            "freezer-friendly"
            or "freezer friendly"
            or "meal prep"
            or "batch cooking"
            or "batch cook"
            or "leftover-friendly"
            or "leftover friendly"
            or "reheats well"
            or "make-ahead"
            or "make ahead"
            or "overnight marinate"
            or "quick prep"
            or "slow-cooked"
            or "slow cook"
            or "30-minute meal"
            or "30 minute meal"
            or "canning & pickling"
            or "canning"
            or "pickling"
            or "dehydrating"
            or "fermented"
            or "fermentation" => TagCategory.MealPrep,

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

    private static bool IsTimeDurationInSource(string? sourceText, int minutes)
    {
        if (string.IsNullOrWhiteSpace(sourceText) || minutes <= 0)
        {
            return false;
        }

        // Direct minutes mention, e.g. "180 minutes", "200 min", "400 mins"
        if (
            Regex.IsMatch(
                sourceText,
                $@"\b{minutes}\s*(?:minutes?|mins?)\b",
                RegexOptions.IgnoreCase
            )
        )
        {
            return true;
        }

        // Hours mention, e.g. "3 hours", "4 hrs", "8 hours"
        if (minutes % 60 == 0)
        {
            var hours = minutes / 60;
            if (
                Regex.IsMatch(
                    sourceText,
                    $@"\b{hours}\s*(?:hours?|hrs?)\b",
                    RegexOptions.IgnoreCase
                )
            )
            {
                return true;
            }
        }
        else if (minutes % 30 == 0)
        {
            var whole = minutes / 60;
            var decimalHours = minutes / 60.0;
            if (
                Regex.IsMatch(
                    sourceText,
                    $@"\b{whole}\s*1/2\s*(?:hours?|hrs?)\b",
                    RegexOptions.IgnoreCase
                )
                || Regex.IsMatch(
                    sourceText,
                    $@"\b{decimalHours:F1}\s*(?:hours?|hrs?)\b",
                    RegexOptions.IgnoreCase
                )
            )
            {
                return true;
            }
        }

        return false;
    }
}

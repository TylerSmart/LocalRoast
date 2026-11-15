using System.Text.RegularExpressions;
using App.Core.Recipes;

namespace App.Core.Ingestion;

/// <summary>
/// Deterministic validator for Stage 2 LLM-extracted recipes before catalog resolution or retries.
/// </summary>
public static partial class RecipeValidator
{
    private const decimal QuantityMismatchToleranceFactor = 1.05m;
    private const decimal QuantityMismatchToleranceAbsolute = 0.01m;

    private static readonly HashSet<string> BasicSeasoningsAndWater = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "salt",
        "kosher salt",
        "sea salt",
        "table salt",
        "fine salt",
        "pepper",
        "black pepper",
        "freshly ground black pepper",
        "white pepper",
        "water",
        "cold water",
        "warm water",
        "hot water",
        "ice",
        "ice water",
    };

    [GeneratedRegex(@"\{\{([^{}\r\n]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(
        @"\b(?:to\s+taste|for\s+(?:serving|garnish|dusting|greasing|topping)|as\s+needed|optional)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex GarnishOrOptionalPhraseRegex();

    [GeneratedRegex(
        @"\b(?:microwave|power)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex MicrowaveOrPowerContextRegex();

    [GeneratedRegex(@"^\s+pulses?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingPulseAfterTokenRegex();

    [GeneratedRegex(
        @"^\s*(?:about\s+)?(?:\d+(?:\s*(?:to|[-\u2013\u2014])\s*\d+)?\s+)?(?:quick\s+|short\s+)?pulses?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex BarePulseLabelRegex();

    [GeneratedRegex(
        @"\bpulse(?:s|d|ing)?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex PulseWordRegex();

    [GeneratedRegex(
        @"\b(?:overnight|several\s+hours|all\s+day|all\s+night)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex QualitativeLongDurationRegex();

    [GeneratedRegex(
        @"(?:\bbetween\s+(?<min>(?:\d+\s*)?[\u00BC-\u00BE\u2153\u2154\u215B-\u215E]|(?:\d+\s+)?\d+/\d+|\d+(?:\.\d+)?)\s+and\s+|(?<![A-Za-z0-9.])(?<min>(?:\d+\s*)?[\u00BC-\u00BE\u2153\u2154\u215B-\u215E]|(?:\d+\s+)?\d+/\d+|\d+(?:\.\d+)?)\s*(?:-\s*to\b|to\b|\bor\b|[-\u2013\u2014])\s*)?(?<![A-Za-z0-9.])(?<val>(?:\d+\s*)?[\u00BC-\u00BE\u2153\u2154\u215B-\u215E]|(?:\d+\s+)?\d+/\d+|\d+(?:\.\d+)?)(?:\s*-|\s+)(?<unit>days?|hours?|hrs?|hr|minutes?|mins?|min|seconds?|secs?|sec)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex SourceDurationRegex();

    [GeneratedRegex(
        @"\b(?<hours>\d+(?:\.\d+)?)\s*(?:hours?|hrs?|hr)(?:\s*(?:,|and)?\s*)(?<minutes>\d+(?:\.\d+)?)\s*(?:minutes?|mins?|min)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex CompoundHoursMinutesRegex();

    [GeneratedRegex(
        @"\b(?<minutes>\d+(?:\.\d+)?)\s*(?:minutes?|mins?|min)(?:\s*(?:,|and)?\s*)(?<seconds>\d+(?:\.\d+)?)\s*(?:seconds?|secs?|sec)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex CompoundMinutesSecondsRegex();

    [GeneratedRegex(
        @"\b(?<word>half\s+(?:an|a)|half|quarter(?:\s+of\s+an?)?|an|a|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)\s+(?<unit>days?|hours?|hrs?|hr|minutes?|mins?|min|seconds?|secs?|sec)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex WordDurationRegex();

    [GeneratedRegex(@"\b(?:plus|or)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AlternativeOrPlusRawRegex();

    [GeneratedRegex(
        @"\b(?:additional|more|extra|another|divided)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex AdditiveSemicolonCueRegex();

    [GeneratedRegex(
        @"^\s*(?:,\s*)?plus\s+(?:(?:\d+\s+)?\d+/\d+|\d+(?:\.\d+)?)\s+\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex LeadingPlusPrepNoteRegex();

    [GeneratedRegex(
        @"(?:\b1/3\b|\u2153|\b(?:one[- ]third|thirds?)\b)(?!\s*-?\s*(?:cup|cups|tbsp|tablespoons?|tsp|teaspoons?|fl\.?\s*oz|fluid\s+ounces?|oz|ounces?|lbs?|pounds?|g|grams?|kg|ml|lit(?:er|re)s?|l|pinch(?:es)?|dash(?:es)?|cloves?|cans?|bunch(?:es)?|slices?|inch(?:es)?|cm)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex ThirdFractionTextRegex();

    [GeneratedRegex(
        @"(?:\b2/3\b|\u2154|\btwo[- ]thirds?\b)(?!\s*-?\s*(?:cup|cups|tbsp|tablespoons?|tsp|teaspoons?|fl\.?\s*oz|fluid\s+ounces?|oz|ounces?|lbs?|pounds?|g|grams?|kg|ml|lit(?:er|re)s?|l|pinch(?:es)?|dash(?:es)?|cloves?|cans?|bunch(?:es)?|slices?|inch(?:es)?|cm)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex TwoThirdsFractionTextRegex();

    [GeneratedRegex(
        @"\b(?:water(?!\s*chestnuts?\b)|ice(?!\s*cream\b)(?:\s+cubes?|\s+water|\s+bath)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex WaterOrIceTermRegex();

    [GeneratedRegex(
        @"\b(?:cooking\s+spray|vegetable\s+oil\s+spray|nonstick\s+spray)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex SprayTermRegex();

    [GeneratedRegex(
        @"\b(?:bacon\s+fat|reserved\s+fat|rendered\s+fat|bacon\s+grease|pan\s+drippings|drippings)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex RenderedFatTermRegex();

    [GeneratedRegex(
        @"\b(?:fat|grease|drippings?|lard|tallow|schmaltz|shortening|oil|butter)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex FatOrDrippingMasterRegex();

    [GeneratedRegex(
        @"^(?:inch(?:es)?|in|cm|mm|centimeters?|millimeters?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex LengthUnitRegex();

    [GeneratedRegex(
        @"^(?:degrees?|deg|fahrenheit|celsius|f|c|°f|°c)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex TemperatureUnitRegex();

    [GeneratedRegex(
        @"(?:\(\s*(?:see\s+)?(?:this|facing)\s+page\s*\)|\b(?:see\s+)?(?:this|facing)\s+page\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex SeeThisPageRegex();

    [GeneratedRegex(
        @"\b(?:rack|inches?|inch|cm|mm|from\s+(?:broiler|heat|element)|apart)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex DistanceOrRackTempContextRegex();

    [GeneratedRegex(
        @"^\s*(?:[-–—]\s*)?(?:inch(?:es)?|in\.?|cm|mm|apart|from\s+(?:the\s+)?(?:broiler|heat|element))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex AdjacentDistanceAfterTempRegex();

    [GeneratedRegex(
        @"\b\d+(?:\s+\d+/\d+|/\d+|\.\d+)?\s*[-\u2013]?\s*(?:inch(?:es)?|in|cm|mm)\b|\b(?:upper|middle|lower|bottom|top|center)(?:[-\s]+(?:middle|third))?\s+(?:rack\s+)?position\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex ExplicitNumberedLengthOrRackPosRegex();

    [GeneratedRegex(
        @"\b(?:heat(?:ed)?|preheat(?:ed)?|bake|baking|roast(?:ing)?|broil(?:er|ing)?|set\s+(?:oven|thermometer)|oven|oil|skillet|pan|water|thermometer|registers?)\b[^.!?\n;]*?\b(?:to|at)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex ThermalVerbBeforeTempRegex();

    [GeneratedRegex(
        @"\b(?:oven|preheat|heat(?:\s+oven|\s+oil|\s+skillet)?|broil(?:er)?|bake|roast|fry|skillet|oil)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex ThermalTempContextRegex();

    [GeneratedRegex(
        @"\b(?:freez\w*|ice|chill\w*|cold|cool|room\s+temp\w*|proof\w*|ferment\w*|refrigerat\w*|lukewarm|tepid|warm\s+water|yeast|chocolate|temper\w*|internal|center|rare|medium|well[- ]done|thermometer|registers?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex ColdOrLowTempContextRegex();

    [GeneratedRegex(
        @"\b(?:1|a|an|one|each)\s+(?:portion|piece|ball|patty|slice|wedge|square|strip|half)\s+(?:of|with)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex SingularPortionOfRegex();

    [GeneratedRegex(
        @"(?ms)^\s*(?<num>\d+)\.\s+(?<body>.+?)(?=^\s*\d+\.\s+|\z)",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex NumberedSourceStepRegex();

    [GeneratedRegex(
        @"\b(?:can\s+be\s+(?:made|prepared|refrigerat|frozen|stored|kept|wrapped)|make[- ]ahead|leftovers|to\s+(?:store|reheat|crisp|serve)|serve\s+(?:warm|at\s+room\s+temp))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex StorageOrNoteParagraphRegex();

    [GeneratedRegex(@"[a-z]{5,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContentWordRegex();

    private static readonly HashSet<string> StepCoverageStopwords = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "about",
        "after",
        "along",
        "another",
        "around",
        "because",
        "before",
        "being",
        "between",
        "clean",
        "continue",
        "could",
        "covered",
        "degrees",
        "every",
        "first",
        "heated",
        "hours",
        "including",
        "large",
        "lightly",
        "medium",
        "minutes",
        "mixture",
        "other",
        "pieces",
        "place",
        "prepared",
        "remaining",
        "second",
        "seconds",
        "serve",
        "several",
        "should",
        "since",
        "slightly",
        "small",
        "spoon",
        "still",
        "their",
        "there",
        "these",
        "thick",
        "thing",
        "things",
        "those",
        "through",
        "together",
        "transfer",
        "under",
        "until",
        "using",
        "water",
        "where",
        "which",
        "while",
        "whole",
        "without",
        "would",
    };

    /// <summary>
    /// Returns true when <paramref name="rawText"/> refers to "(see this page)" / "facing page"
    /// alongside raw grocery indicators or physical weights/measures without a recipe cue.
    /// </summary>
    public static bool IsRawGrocerySeePageNote(string? rawText)
    {
        if (
            string.IsNullOrWhiteSpace(rawText)
            || !SeeThisPageRegex().IsMatch(rawText)
            || RecipeOrFollowsRegex().IsMatch(rawText)
        )
        {
            return false;
        }

        if (RawGroceryIndicatorRegex().IsMatch(rawText))
        {
            return true;
        }

        var spans = SourceMeasurementParser.FindSpans(rawText);
        return spans.Any(s => s.Measurement.Unit is not null and not MeasurementUnit.Whole);
    }

    [GeneratedRegex(
        @"\b(?:recipes?|follows?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex RecipeOrFollowsRegex();

    [GeneratedRegex(
        @"\b(?:lb|lbs|pounds?|oz|ounces?|g|grams?|kg|per\s+pound|peeled|deveined|tails?|trimmed|cored|seeded|stemmed)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex RawGroceryIndicatorRegex();

    [GeneratedRegex(
        @"\b(?:measure|depth|inch(?:es)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex NonTimeTimerLabelRegex();

    [GeneratedRegex(
        @"^[^()\r\n]*\([^()\r\n]*\)\s*plus\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex ParenBeforePlusRegex();

    [GeneratedRegex(
        @"(?m)^(?:Title|Author|Source|Cookbook|Page|Chapter|Yield):[^\r\n]*\r?\n?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex MetadataHeaderLineRegex();

    [GeneratedRegex(
        @"\b(?:quick\s+|short\s+)?pulses?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex LocalPulseMentionRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(
        @"\b(?:and|then|before|after)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex LocalClauseBreakWordRegex();

    [GeneratedRegex(
        @"^\s*(?:,|or|to|[-\u2013\u2014])\s*(?:or|up\s+to|at\s+least|about|roughly|approximately)?\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex ChainedDurationConnectorRegex();

    [GeneratedRegex(
        @"^(?:for|about|approximately|approx\.?|roughly|at\s+least|up\s+to|around|nearly)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex TrailingTimerPrepositionRegex();

    public static RecipeValidationResult Validate(ExtractedRecipeDto? recipe) =>
        Validate(recipe, sourceText: null);

    public static RecipeValidationResult Validate(ExtractedRecipeDto? recipe, string? sourceText)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (recipe is null)
        {
            errors.Add("Extracted recipe payload is null.");
            return new RecipeValidationResult(errors, warnings);
        }

        if (string.IsNullOrWhiteSpace(recipe.Title))
        {
            errors.Add("Recipe Title is missing or empty.");
        }

        if (
            !string.IsNullOrWhiteSpace(recipe.Description)
            && PlaceholderRegex().IsMatch(recipe.Description)
        )
        {
            errors.Add(
                "Recipe Description must be plain text and must not contain {{...}} token placeholders."
            );
        }

        if (recipe.Ingredients is null || recipe.Ingredients.Count == 0)
        {
            errors.Add("Ingredients list is empty.");
        }

        if (recipe.Steps is null || recipe.Steps.Count == 0)
        {
            errors.Add("Steps list is empty.");
        }

        var ingredientsByRefId = ValidateIngredients(recipe.Ingredients ?? [], errors);
        var referencedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stepQuantitiesByRefId = new Dictionary<string, List<(decimal Quantity, string? Unit)>>(
            StringComparer.OrdinalIgnoreCase
        );
        var sourceDurations = ParseSourceDurations(sourceText);

        ValidateSteps(
            recipe.Steps ?? [],
            ingredientsByRefId,
            referencedIds,
            stepQuantitiesByRefId,
            sourceDurations,
            sourceText,
            recipe.Servings,
            errors
        );

        ValidateIngredientQuantitySums(ingredientsByRefId, stepQuantitiesByRefId, errors);
        CollectUnreferencedIngredientWarnings(recipe.Ingredients ?? [], referencedIds, warnings);

        return new RecipeValidationResult(errors, warnings);
    }

    /// <summary>
    /// Deterministically folds leading compound "X plus Y" measurements (e.g., "6 tbsp plus 1 tsp")
    /// when an extraction left the additive term in AltQuantity/AltUnit or dropped it.
    /// </summary>
    public static ExtractedRecipeDto NormalizeCompoundIngredients(ExtractedRecipeDto recipe)
    {
        if (recipe.Ingredients is null || recipe.Ingredients.Count == 0)
        {
            return recipe;
        }

        var rawTextCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var ing in recipe.Ingredients)
        {
            if (!string.IsNullOrWhiteSpace(ing.RawText))
            {
                var key = ing.RawText.Trim();
                rawTextCounts[key] = rawTextCounts.TryGetValue(key, out var c) ? c + 1 : 1;
            }
        }

        List<ExtractedIngredientDto>? updated = null;
        for (var i = 0; i < recipe.Ingredients.Count; i++)
        {
            var ing = recipe.Ingredients[i];
            if (string.IsNullOrWhiteSpace(ing.RawText))
            {
                continue;
            }

            var rawKey = ing.RawText.Trim();
            var sharedRawCount = rawTextCounts.TryGetValue(rawKey, out var cnt) ? cnt : 0;

            var compound = SourceMeasurementParser.TrySumCompoundLeadingMeasurement(ing.RawText);
            if (compound?.Quantity is > 0m && compound.Unit.HasValue)
            {
                if (sharedRawCount > 1 && ing.AltQuantity is null or <= 0m)
                {
                    continue;
                }

                var shouldRewrite = false;
                var shouldUpgradeAlt = false;
                var (mUnit, mCustom) = RecipeUnitAndSectionHelpers.NormalizeUnit(ing.Unit);

                if (ing.AltQuantity is > 0m && ing.Quantity is > 0m)
                {
                    var (aUnit, aCustom) = RecipeUnitAndSectionHelpers.NormalizeUnit(ing.AltUnit);
                    if (
                        TryConvertStepQuantityToTarget(
                            ing.Quantity.Value,
                            mUnit,
                            mCustom,
                            compound.Unit,
                            null,
                            out var mConv
                        )
                        && TryConvertStepQuantityToTarget(
                            ing.AltQuantity.Value,
                            aUnit,
                            aCustom,
                            compound.Unit,
                            null,
                            out var aConv
                        )
                        && Math.Abs((mConv + aConv) - compound.Quantity.Value) <= 0.05m
                    )
                    {
                        shouldRewrite = true;
                    }
                    else if (
                        TryConvertStepQuantityToTarget(
                            ing.Quantity.Value,
                            mUnit,
                            mCustom,
                            compound.Unit,
                            null,
                            out mConv
                        ) && ExceedsRoundingTolerance(mConv, compound.Quantity.Value)
                    )
                    {
                        shouldRewrite = true;
                    }
                    else if (
                        TryConvertStepQuantityToTarget(
                            ing.AltQuantity.Value,
                            aUnit,
                            aCustom,
                            compound.Unit,
                            null,
                            out aConv
                        )
                        && aConv < compound.Quantity.Value * 0.98m
                    )
                    {
                        shouldUpgradeAlt = true;
                    }
                }
                else if (ing.Quantity is > 0m)
                {
                    if (
                        TryConvertStepQuantityToTarget(
                            ing.Quantity.Value,
                            mUnit,
                            mCustom,
                            compound.Unit,
                            null,
                            out var mConv
                        ) && ExceedsRoundingTolerance(mConv, compound.Quantity.Value)
                    )
                    {
                        shouldRewrite = true;
                    }
                }

                if (shouldRewrite)
                {
                    var keepAltMeasurement = false;
                    if (ing.AltQuantity is > 0m && !ParenBeforePlusRegex().IsMatch(ing.RawText))
                    {
                        var (aUnit, aCustom) = RecipeUnitAndSectionHelpers.NormalizeUnit(
                            ing.AltUnit
                        );
                        var altIsConvertibleToPrimary =
                            aUnit.HasValue
                            && TryConvertStepQuantityToTarget(
                                ing.AltQuantity.Value,
                                aUnit,
                                aCustom,
                                compound.Unit,
                                null,
                                out _
                            );
                        if (
                            !altIsConvertibleToPrimary
                            && aUnit.HasValue
                            && aUnit != MeasurementUnit.Custom
                        )
                        {
                            keepAltMeasurement = true;
                        }
                    }

                    updated ??= [.. recipe.Ingredients];
                    updated[i] = ing with
                    {
                        Quantity = compound.Quantity.Value,
                        MaxQuantity = null,
                        Unit = compound.Unit.Value.ToString().ToLowerInvariant(),
                        AltQuantity = keepAltMeasurement ? ing.AltQuantity : null,
                        AltUnit = keepAltMeasurement ? ing.AltUnit : null,
                        PreparationNotes = StripLeadingPlusPrepNote(ing.PreparationNotes),
                    };
                    continue;
                }

                if (shouldUpgradeAlt)
                {
                    updated ??= [.. recipe.Ingredients];
                    updated[i] = ing with
                    {
                        MaxQuantity = null,
                        AltQuantity = compound.Quantity.Value,
                        AltUnit = compound.Unit.Value.ToString().ToLowerInvariant(),
                        PreparationNotes = StripLeadingPlusPrepNote(ing.PreparationNotes),
                    };
                    continue;
                }
            }

            if (
                ing.MaxQuantity.HasValue
                && ing.Quantity is > 0m
                && sharedRawCount == 1
                && !AlternativeOrPlusRawRegex().IsMatch(ing.RawText)
            )
            {
                var spans = SourceMeasurementParser.FindSpans(ing.RawText);
                var confirmable = SourceMeasurementParser.FindAllConfirmableMeasurements(
                    ing.RawText
                );
                var (mUnit, mCustom) = RecipeUnitAndSectionHelpers.NormalizeUnit(ing.Unit);
                var sourceHasConvertibleRange = spans
                    .Select(s => s.Measurement)
                    .Concat(confirmable)
                    .Any(m =>
                        m.MaxQuantity.HasValue
                        && m.Quantity is > 0m
                        && TryConvertStepQuantityToTarget(
                            m.Quantity.Value,
                            m.Unit,
                            m.CustomUnitName,
                            mUnit,
                            mCustom,
                            out _
                        )
                    );

                if (!sourceHasConvertibleRange)
                {
                    var matchesSingleMeasure = confirmable.Any(m =>
                        m.MaxQuantity is null
                        && m.Quantity is > 0m
                        && TryConvertStepQuantityToTarget(
                            m.Quantity.Value,
                            m.Unit,
                            m.CustomUnitName,
                            mUnit,
                            mCustom,
                            out var expectedPrimary
                        )
                        && Math.Abs(ing.Quantity.Value - expectedPrimary)
                            <= Math.Max(0.03m, expectedPrimary * 0.04m)
                    );

                    if (matchesSingleMeasure)
                    {
                        updated ??= [.. recipe.Ingredients];
                        updated[i] = ing with { MaxQuantity = null };
                    }
                }
            }
        }

        return updated is not null ? recipe with { Ingredients = updated } : recipe;
    }

    private static bool ExceedsRoundingTolerance(decimal actual, decimal expected) =>
        Math.Abs(actual - expected) > Math.Max(0.005m, expected * 0.005m);

    private static string? StripLeadingPlusPrepNote(string? preparationNotes)
    {
        if (string.IsNullOrWhiteSpace(preparationNotes))
        {
            return preparationNotes;
        }

        var stripped = LeadingPlusPrepNoteRegex()
            .Replace(preparationNotes, string.Empty)
            .TrimStart(' ', ',', ';');
        return string.IsNullOrWhiteSpace(stripped) ? null : stripped;
    }

    public static string NormalizeTokenKey(string? tokenKey)
    {
        if (string.IsNullOrWhiteSpace(tokenKey))
        {
            return string.Empty;
        }

        return tokenKey.Trim().Trim('{', '}').Trim();
    }

    private static Dictionary<string, ExtractedIngredientDto> ValidateIngredients(
        IReadOnlyList<ExtractedIngredientDto> ingredients,
        List<string> errors
    )
    {
        var map = new Dictionary<string, ExtractedIngredientDto>(StringComparer.OrdinalIgnoreCase);
        var rawTextCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in ingredients)
        {
            if (!string.IsNullOrWhiteSpace(item.RawText))
            {
                var key = item.RawText.Trim();
                rawTextCounts[key] = rawTextCounts.TryGetValue(key, out var c) ? c + 1 : 1;
            }
        }

        for (var i = 0; i < ingredients.Count; i++)
        {
            var ing = ingredients[i];
            var refKey = ing.RefId?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(refKey))
            {
                errors.Add($"Ingredient #{i + 1} is missing a RefId.");
            }
            else if (!map.TryAdd(refKey, ing))
            {
                errors.Add($"Duplicate ingredient RefId '{refKey}'.");
            }

            if (string.IsNullOrWhiteSpace(ing.CanonicalFoodItem))
            {
                errors.Add(
                    $"Ingredient #{i + 1} ({(string.IsNullOrWhiteSpace(refKey) ? "no RefId" : refKey)}) has an empty CanonicalFoodItem."
                );
            }

            if (ing.Quantity.HasValue && ing.Quantity.Value <= 0m)
            {
                errors.Add(
                    $"Ingredient '{ing.CanonicalFoodItem}' ({refKey}) has non-positive Quantity {ing.Quantity.Value}."
                );
            }

            if (ing.MaxQuantity.HasValue && ing.MaxQuantity.Value <= 0m)
            {
                errors.Add(
                    $"Ingredient '{ing.CanonicalFoodItem}' ({refKey}) has non-positive MaxQuantity {ing.MaxQuantity.Value}."
                );
            }

            if (
                ing.Quantity.HasValue
                && ing.MaxQuantity.HasValue
                && ing.MaxQuantity.Value < ing.Quantity.Value
            )
            {
                errors.Add(
                    $"Ingredient '{ing.CanonicalFoodItem}' ({refKey}) has MaxQuantity {ing.MaxQuantity.Value} less than Quantity {ing.Quantity.Value}."
                );
            }

            if (!string.IsNullOrWhiteSpace(ing.PreparationNotes))
            {
                foreach (Match prepPlaceholder in PlaceholderRegex().Matches(ing.PreparationNotes))
                {
                    var key = NormalizeTokenKey(prepPlaceholder.Groups[1].Value);
                    errors.Add(
                        $"Ingredient '{ing.CanonicalFoodItem}' ({refKey}) preparationNotes must be plain text and must not contain placeholder '{{{{{key}}}}}'."
                    );
                }
            }

            if (ing.IsRecipeReference && IsRawGrocerySeePageNote(ing.RawText))
            {
                errors.Add(
                    $"Ingredient '{ing.CanonicalFoodItem}' ({refKey}) sets isRecipeReference=true solely from '(see this page)' on a raw grocery item ('{ing.RawText}'). Set isRecipeReference=false and referencedRecipeTitle=null for raw groceries with prep/substitution notes."
                );
            }

            if (string.Equals(ing.Unit?.Trim(), "custom", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"Ingredient '{ing.CanonicalFoodItem}' ({refKey}) uses literal 'custom' as unit. Output the concrete unit or container word (e.g., 'wheel', 'sheet', 'can', 'package') or 'whole' instead."
                );
            }

            if (ing.AltQuantity is > 0m)
            {
                var trimmedAltUnit = ing.AltUnit?.Trim();
                var isGenericCustom = string.Equals(
                    trimmedAltUnit,
                    "custom",
                    StringComparison.OrdinalIgnoreCase
                );
                var isLengthOrTempAlt =
                    !string.IsNullOrWhiteSpace(trimmedAltUnit)
                    && (
                        LengthUnitRegex().IsMatch(trimmedAltUnit)
                        || TemperatureUnitRegex().IsMatch(trimmedAltUnit)
                    );
                if (isGenericCustom || isLengthOrTempAlt)
                {
                    errors.Add(
                        $"Ingredient '{ing.CanonicalFoodItem}' ({refKey}) has invalid altQuantity/altUnit ({ing.AltQuantity.Value:0.####} '{ing.AltUnit}'). Never put physical dimensions (inches), temperatures (degrees), or generic 'custom' into altQuantity/altUnit; set altQuantity and altUnit to null."
                    );
                }
                else if (ing.Quantity is > 0m)
                {
                    var (mUnitAlt, _) = RecipeUnitAndSectionHelpers.NormalizeUnit(ing.Unit);
                    var (aUnitAlt, _) = RecipeUnitAndSectionHelpers.NormalizeUnit(ing.AltUnit);
                    if (
                        mUnitAlt is { } priU
                        && aUnitAlt is { } altU
                        && priU != MeasurementUnit.Custom
                        && altU != MeasurementUnit.Custom
                        && CulinaryMath.TryConvertQuantity(
                            ing.AltQuantity.Value,
                            altU,
                            priU,
                            out var convertedAlt
                        )
                        && Math.Abs(convertedAlt - ing.Quantity.Value)
                            > Math.Max(0.1m, ing.Quantity.Value * 0.15m)
                    )
                    {
                        errors.Add(
                            $"Ingredient '{ing.CanonicalFoodItem}' ({refKey}) has altQuantity/altUnit ({ing.AltQuantity.Value:0.####} '{ing.AltUnit}') that converts to {convertedAlt:0.####} {priU}, contradicting primary {ing.Quantity.Value:0.####} {priU}. Set altQuantity and altUnit to null when parenthetical text is a temperature or dimension."
                        );
                    }
                }
            }

            var leadingPack = SourceMeasurementParser.TryFindLeadingContainerPack(ing.RawText);
            if (leadingPack is not null && ing.Quantity is > 0m)
            {
                var (mUnitPack, _) = RecipeUnitAndSectionHelpers.NormalizeUnit(ing.Unit);
                if (
                    mUnitPack == leadingPack.SizeUnit
                    && Math.Abs(ing.Quantity.Value - leadingPack.Count) <= 0.01m
                    && Math.Abs(leadingPack.SizeQuantity - leadingPack.Count) > 0.05m
                )
                {
                    var totalPackSize = leadingPack.Count * leadingPack.SizeQuantity;
                    var sizeUnitLabel = leadingPack.SizeUnit.ToString().ToLowerInvariant();
                    errors.Add(
                        $"Ingredient '{ing.CanonicalFoodItem}' ({refKey}) has quantity={ing.Quantity.Value:0.####} {ing.Unit}, mixing container count ({leadingPack.Count:0.####} {leadingPack.ContainerUnit}) with per-container unit '{sizeUnitLabel}'. Set quantity={leadingPack.Count:0.####}, unit='{leadingPack.ContainerUnit}', altQuantity={totalPackSize:0.####}, altUnit='{sizeUnitLabel}' (or use total weight quantity={totalPackSize:0.####}, unit='{sizeUnitLabel}')."
                    );
                }
            }

            if (
                ing.AltQuantity is > 0m
                && RecipeUnitAndSectionHelpers.NormalizeUnit(ing.AltUnit).Unit.HasValue
                && (
                    ing.Quantity is null or <= 0m
                    || !RecipeUnitAndSectionHelpers.NormalizeUnit(ing.Unit).Unit.HasValue
                )
            )
            {
                errors.Add(
                    $"Ingredient '{ing.CanonicalFoodItem}' ({refKey}) has altQuantity/altUnit without a measured primary quantity and unit. Move altQuantity/altUnit into quantity/unit or set them to null."
                );
            }

            if (ing.AltQuantity is > 0m && !string.IsNullOrWhiteSpace(ing.RawText))
            {
                var compound = SourceMeasurementParser.TrySumCompoundLeadingMeasurement(
                    ing.RawText
                );
                if (compound?.Quantity is > 0m && compound.Unit.HasValue)
                {
                    var (mUnit, mCustom) = RecipeUnitAndSectionHelpers.NormalizeUnit(ing.Unit);
                    var (aUnit, aCustom) = RecipeUnitAndSectionHelpers.NormalizeUnit(ing.AltUnit);
                    if (
                        ing.Quantity is > 0m
                        && TryConvertStepQuantityToTarget(
                            ing.Quantity.Value,
                            mUnit,
                            mCustom,
                            compound.Unit,
                            null,
                            out var mConv
                        )
                        && TryConvertStepQuantityToTarget(
                            ing.AltQuantity.Value,
                            aUnit,
                            aCustom,
                            compound.Unit,
                            null,
                            out var aConv
                        )
                        && Math.Abs((mConv + aConv) - compound.Quantity.Value) <= 0.05m
                    )
                    {
                        errors.Add(
                            $"Ingredient '{ing.CanonicalFoodItem}' ({refKey}) split compound 'plus' measurement ('{ing.RawText}') into altQuantity/altUnit. Sum compound quantities into quantity={compound.Quantity.Value:0.####}, unit='{compound.Unit.Value.ToString().ToLowerInvariant()}', and set altQuantity and altUnit to null."
                        );
                    }
                }
            }

            if (
                ing.Quantity is > 0m
                && !string.IsNullOrWhiteSpace(ing.RawText)
                && rawTextCounts.TryGetValue(ing.RawText.Trim(), out var rawCount)
                && rawCount == 1
                && !AlternativeOrPlusRawRegex().IsMatch(ing.RawText)
            )
            {
                var (mUnit, mCustom) = RecipeUnitAndSectionHelpers.NormalizeUnit(ing.Unit);
                var candidates = SourceMeasurementParser
                    .FindAllConfirmableMeasurements(ing.RawText)
                    .ToList();
                var spans = SourceMeasurementParser.FindSpans(ing.RawText);
                if (spans.Count > 0)
                {
                    var container = SourceMeasurementParser.FindContainerUnitSize(ing.RawText);
                    if (container?.Quantity is > 0m && spans[0].Measurement.Quantity is > 0m)
                    {
                        var primaryQty = spans[0].Measurement.Quantity!.Value;
                        candidates.Add(
                            new RecipeMeasurement(
                                container.Quantity.Value * primaryQty,
                                container.Unit,
                                container.MaxQuantity.HasValue
                                    ? container.MaxQuantity.Value * primaryQty
                                    : null,
                                container.CustomUnitName
                            )
                        );
                    }
                }

                if (
                    ing.RawText.Contains(';', StringComparison.Ordinal)
                    && TrySumSemicolonAdditiveMeasurements(
                        ing.RawText,
                        mUnit,
                        mCustom,
                        out var sumMin,
                        out var sumMax
                    )
                )
                {
                    candidates.Add(new RecipeMeasurement(sumMin, mUnit, sumMax, mCustom));
                }

                if (candidates.Count > 0)
                {
                    var hasConvertibleCandidate = false;
                    var matchesAnyCandidate = false;
                    foreach (var cand in candidates)
                    {
                        if (
                            cand.Quantity is > 0m
                            && TryConvertStepQuantityToTarget(
                                cand.Quantity.Value,
                                cand.Unit,
                                cand.CustomUnitName,
                                mUnit,
                                mCustom,
                                out var expectedMin
                            )
                        )
                        {
                            hasConvertibleCandidate = true;
                            var expectedMax = expectedMin;
                            if (
                                cand.MaxQuantity is > 0m
                                && TryConvertStepQuantityToTarget(
                                    cand.MaxQuantity.Value,
                                    cand.Unit,
                                    cand.CustomUnitName,
                                    mUnit,
                                    mCustom,
                                    out var convMax
                                )
                            )
                            {
                                expectedMax = Math.Max(expectedMin, convMax);
                            }

                            var tol = Math.Max(0.03m, expectedMin * 0.04m);
                            if (
                                ing.Quantity.Value >= expectedMin - tol
                                && ing.Quantity.Value <= expectedMax + tol
                            )
                            {
                                matchesAnyCandidate = true;
                                break;
                            }
                        }
                    }

                    if (hasConvertibleCandidate && !matchesAnyCandidate)
                    {
                        errors.Add(
                            $"Ingredient '{ing.CanonicalFoodItem}' ({refKey}) has quantity={ing.Quantity.Value:0.####} {ing.Unit}, which does not match leading measurement in rawText '{ing.RawText}' (use exact decimals such as 1/3 = 0.3333 and 2/3 = 0.6667)."
                        );
                    }
                }
            }
        }

        ValidateCompoundPlusDuplicateRows(ingredients, rawTextCounts, errors);

        return map;
    }

    private static bool TrySumSemicolonAdditiveMeasurements(
        string rawText,
        MeasurementUnit? targetUnit,
        string? targetCustomUnit,
        out decimal sumMin,
        out decimal? sumMax
    )
    {
        sumMin = 0m;
        sumMax = null;

        var segments = rawText.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        if (segments.Length < 2)
        {
            return false;
        }

        var hasAdditiveCue = AdditiveSemicolonCueRegex().IsMatch(rawText);
        var seenUnits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasDuplicateUnit = false;
        decimal minTotal = 0m;
        decimal maxTotal = 0m;
        var hasRange = false;
        var convertedCount = 0;

        foreach (var segment in segments)
        {
            var cand = SourceMeasurementParser
                .FindAllConfirmableMeasurements(segment)
                .FirstOrDefault(m => m.Quantity is > 0m);
            if (cand?.Quantity is not > 0m)
            {
                return false;
            }

            if (
                !TryConvertStepQuantityToTarget(
                    cand.Quantity.Value,
                    cand.Unit,
                    cand.CustomUnitName,
                    targetUnit,
                    targetCustomUnit,
                    out var convMin
                )
            )
            {
                return false;
            }

            var unitKey =
                cand.Unit == MeasurementUnit.Custom
                    ? $"custom:{cand.CustomUnitName?.Trim()}"
                    : cand.Unit?.ToString() ?? string.Empty;
            if (!seenUnits.Add(unitKey))
            {
                hasDuplicateUnit = true;
            }

            minTotal += convMin;
            if (
                cand.MaxQuantity is > 0m
                && TryConvertStepQuantityToTarget(
                    cand.MaxQuantity.Value,
                    cand.Unit,
                    cand.CustomUnitName,
                    targetUnit,
                    targetCustomUnit,
                    out var convMax
                )
                && convMax >= convMin
            )
            {
                hasRange = true;
                maxTotal += convMax;
            }
            else
            {
                maxTotal += convMin;
            }

            convertedCount++;
        }

        if (convertedCount < 2 || (!hasDuplicateUnit && !hasAdditiveCue))
        {
            return false;
        }

        sumMin = minTotal;
        sumMax = hasRange ? maxTotal : null;
        return true;
    }

    private static void ValidateCompoundPlusDuplicateRows(
        IReadOnlyList<ExtractedIngredientDto> ingredients,
        Dictionary<string, int> rawTextCounts,
        List<string> errors
    )
    {
        var seenRawKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ing in ingredients)
        {
            if (string.IsNullOrWhiteSpace(ing.RawText))
            {
                continue;
            }

            var rawKey = ing.RawText.Trim();
            if (
                !rawTextCounts.TryGetValue(rawKey, out var count)
                || count <= 1
                || !seenRawKeys.Add(rawKey)
            )
            {
                continue;
            }

            var compound = SourceMeasurementParser.TrySumCompoundLeadingMeasurement(rawKey);
            if (compound?.Quantity is not > 0m || !compound.Unit.HasValue)
            {
                continue;
            }

            var sameFoodRows = ingredients
                .Where(x =>
                    !string.IsNullOrWhiteSpace(x.RawText)
                    && string.Equals(x.RawText.Trim(), rawKey, StringComparison.OrdinalIgnoreCase)
                )
                .GroupBy(
                    x =>
                        App.Core.FoodCatalog.FoodNameSanitizer.NormalizeCanonicalFoodName(
                            x.CanonicalFoodItem
                        ),
                    StringComparer.OrdinalIgnoreCase
                );

            foreach (var group in sameFoodRows)
            {
                var rows = group.ToList();
                if (rows.Count <= 1)
                {
                    continue;
                }

                decimal sumInCompoundUnit = 0m;
                var allConvertible = true;
                foreach (var row in rows)
                {
                    if (row.Quantity is not > 0m)
                    {
                        allConvertible = false;
                        break;
                    }

                    var (rUnit, rCustom) = RecipeUnitAndSectionHelpers.NormalizeUnit(row.Unit);
                    if (
                        !TryConvertStepQuantityToTarget(
                            row.Quantity.Value,
                            rUnit,
                            rCustom,
                            compound.Unit,
                            null,
                            out var converted
                        )
                    )
                    {
                        allConvertible = false;
                        break;
                    }

                    sumInCompoundUnit += converted;
                }

                if (allConvertible && sumInCompoundUnit > compound.Quantity.Value + 0.05m)
                {
                    var unitLabel = compound.Unit.Value.ToString().ToLowerInvariant();
                    errors.Add(
                        $"Multiple ingredient rows share compound rawText '{rawKey}', and their quantities sum to {sumInCompoundUnit:0.####} {unitLabel}, exceeding the source total {compound.Quantity.Value:0.####} {unitLabel}. Keep divided uses of '{group.Key}' on ONE master row with quantity={compound.Quantity.Value:0.####}, unit='{unitLabel}'."
                    );
                }
            }
        }
    }

    private sealed record SourceDurationIndex(
        HashSet<int> ExplicitMinuteIntegers,
        HashSet<int> ExplicitHourIntegers,
        HashSet<int> ExplicitSecondIntegers,
        HashSet<int> ConvertedSeconds,
        int MaxDurationSeconds,
        bool HasQualitativeLongDuration,
        string NormalizedAnchorText
    );

    private static SourceDurationIndex ParseSourceDurations(string? sourceText)
    {
        var minutes = new HashSet<int>();
        var hours = new HashSet<int>();
        var seconds = new HashSet<int>();
        var convertedSeconds = new HashSet<int>();
        var maxSeconds = 0;

        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return new SourceDurationIndex(
                minutes,
                hours,
                seconds,
                convertedSeconds,
                0,
                false,
                string.Empty
            );
        }

        var bodyText = MetadataHeaderLineRegex().Replace(sourceText, string.Empty);
        if (string.IsNullOrWhiteSpace(bodyText))
        {
            bodyText = sourceText;
        }

        CollectDurations(bodyText, minutes, hours, seconds, convertedSeconds, ref maxSeconds);

        var instrIdx = bodyText.IndexOf("Instructions:", StringComparison.OrdinalIgnoreCase);
        var anchorSource =
            instrIdx >= 0 ? bodyText[(instrIdx + "Instructions:".Length)..] : bodyText;
        var normalizedAnchorText = MultiSpaceRegex().Replace(anchorSource, " ").Trim();
        var hasQualitativeLong = QualitativeLongDurationRegex().IsMatch(bodyText);

        return new SourceDurationIndex(
            minutes,
            hours,
            seconds,
            convertedSeconds,
            maxSeconds,
            hasQualitativeLong,
            normalizedAnchorText
        );
    }

    private static void CollectDurations(
        string text,
        HashSet<int>? minutes,
        HashSet<int>? hours,
        HashSet<int>? seconds,
        HashSet<int> convertedSeconds,
        ref int maxSeconds
    )
    {
        foreach (Match match in SourceDurationRegex().Matches(text))
        {
            var unitRaw = match.Groups["unit"].Value.Trim().ToLowerInvariant();
            var multiplier = unitRaw[0] switch
            {
                'd' => 86400m,
                'h' => 3600m,
                'm' => 60m,
                _ => 1m,
            };

            foreach (var groupName in new[] { "min", "val" })
            {
                var group = match.Groups[groupName];
                if (!group.Success)
                {
                    continue;
                }

                var parsed = FlexibleNullableDecimalJsonConverter.ParseNumber(group.Value);
                if (parsed is not > 0m)
                {
                    continue;
                }

                var wholeInt =
                    parsed.Value == decimal.Truncate(parsed.Value) ? (int)parsed.Value : (int?)null;

                if (unitRaw[0] == 'h')
                {
                    if (wholeInt.HasValue)
                    {
                        hours?.Add(wholeInt.Value);
                    }
                }
                else if (unitRaw[0] == 'm')
                {
                    if (wholeInt.HasValue)
                    {
                        minutes?.Add(wholeInt.Value);
                    }
                }
                else if (unitRaw[0] == 's')
                {
                    if (wholeInt.HasValue)
                    {
                        seconds?.Add(wholeInt.Value);
                    }
                }

                var secs = (int)Math.Round(parsed.Value * multiplier);
                if (secs > 0)
                {
                    convertedSeconds.Add(secs);
                    if (secs > maxSeconds)
                    {
                        maxSeconds = secs;
                    }
                }
            }
        }

        foreach (Match match in CompoundHoursMinutesRegex().Matches(text))
        {
            var parsedHours = FlexibleNullableDecimalJsonConverter.ParseNumber(
                match.Groups["hours"].Value
            );
            var parsedMinutes = FlexibleNullableDecimalJsonConverter.ParseNumber(
                match.Groups["minutes"].Value
            );
            if (parsedHours is > 0m && parsedMinutes is >= 0m)
            {
                var secs = (int)
                    Math.Round((parsedHours.Value * 3600m) + (parsedMinutes.Value * 60m));
                if (secs > 0)
                {
                    convertedSeconds.Add(secs);
                    if (secs > maxSeconds)
                    {
                        maxSeconds = secs;
                    }
                }
            }
        }

        foreach (Match match in CompoundMinutesSecondsRegex().Matches(text))
        {
            var parsedMinutes = FlexibleNullableDecimalJsonConverter.ParseNumber(
                match.Groups["minutes"].Value
            );
            var parsedSeconds = FlexibleNullableDecimalJsonConverter.ParseNumber(
                match.Groups["seconds"].Value
            );
            if (parsedMinutes is > 0m && parsedSeconds is >= 0m)
            {
                var secs = (int)Math.Round((parsedMinutes.Value * 60m) + parsedSeconds.Value);
                if (secs > 0)
                {
                    convertedSeconds.Add(secs);
                    if (secs > maxSeconds)
                    {
                        maxSeconds = secs;
                    }
                }
            }
        }

        foreach (Match match in WordDurationRegex().Matches(text))
        {
            var word = match.Groups["word"].Value.Trim();
            var unitRaw = match.Groups["unit"].Value.Trim().ToLowerInvariant();
            decimal? wordVal = word.ToLowerInvariant() switch
            {
                var w when w.StartsWith("half", StringComparison.Ordinal) => 0.5m,
                var w when w.StartsWith("quarter", StringComparison.Ordinal) => 0.25m,
                "a" or "an" => 1m,
                _ => FlexibleNullableDecimalJsonConverter.ParseNumber(word),
            };
            if (wordVal is not > 0m)
            {
                continue;
            }

            var multiplier = unitRaw[0] switch
            {
                'd' => 86400m,
                'h' => 3600m,
                'm' => 60m,
                _ => 1m,
            };
            var secs = (int)Math.Round(wordVal.Value * multiplier);
            if (secs > 0)
            {
                convertedSeconds.Add(secs);
                if (secs > maxSeconds)
                {
                    maxSeconds = secs;
                }
            }
        }
    }

    private static void ValidateTimerAgainstLocalSourceClause(
        int stepNum,
        string tokenKey,
        StepTimerTokenDto tok,
        string templateText,
        string normalizedAnchorText,
        List<string> errors
    )
    {
        if (
            string.IsNullOrWhiteSpace(templateText)
            || string.IsNullOrWhiteSpace(normalizedAnchorText)
        )
        {
            return;
        }

        Match? tokenMatch = null;
        foreach (Match m in PlaceholderRegex().Matches(templateText))
        {
            if (
                string.Equals(
                    NormalizeTokenKey(m.Groups[1].Value),
                    tokenKey,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                tokenMatch = m;
                break;
            }
        }

        if (tokenMatch is null)
        {
            return;
        }

        var clauseStart = FindAnchorClauseStart(templateText, tokenMatch.Index);
        var rawPrefix = templateText[clauseStart..tokenMatch.Index];
        var cleanedWords = MultiSpaceRegex()
            .Replace(rawPrefix, " ")
            .Trim(' ', ',', ':', '-', '\u2013', '\u2014', '(')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        while (
            cleanedWords.Count > 0
            && (
                TrailingTimerPrepositionRegex().IsMatch(cleanedWords[^1])
                || (
                    cleanedWords.Count >= 2
                    && TrailingTimerPrepositionRegex()
                        .IsMatch($"{cleanedWords[^2]} {cleanedWords[^1]}")
                )
            )
        )
        {
            if (
                cleanedWords.Count >= 2
                && TrailingTimerPrepositionRegex().IsMatch($"{cleanedWords[^2]} {cleanedWords[^1]}")
            )
            {
                cleanedWords.RemoveAt(cleanedWords.Count - 1);
                cleanedWords.RemoveAt(cleanedWords.Count - 1);
            }
            else
            {
                cleanedWords.RemoveAt(cleanedWords.Count - 1);
            }
        }

        var matchEnd = -1;
        for (var w = Math.Min(4, cleanedWords.Count); w >= 2; w--)
        {
            var window = cleanedWords.GetRange(cleanedWords.Count - w, w);
            var anchor = string.Join(' ', window).Trim(',', '.', '(', ')');
            if (anchor.Length < 10)
            {
                continue;
            }

            var longWordCount = window.Count(word => word.Trim(',', '.', '(', ')').Length >= 4);
            if (longWordCount < 2)
            {
                continue;
            }

            var occurrenceCount = CountWholePhraseMatches(
                normalizedAnchorText,
                anchor,
                out var foundEnd
            );
            if (occurrenceCount >= 2)
            {
                return;
            }

            if (occurrenceCount == 1)
            {
                matchEnd = foundEnd;
                break;
            }
        }

        if (matchEnd < 0 || matchEnd >= normalizedAnchorText.Length)
        {
            return;
        }

        var tailLimit = FindTailClauseEnd(normalizedAnchorText, matchEnd, maxLen: 90);
        if (tailLimit <= matchEnd)
        {
            return;
        }

        var tail = normalizedAnchorText[matchEnd..tailLimit];
        var firstDuration = FindEarliestDurationMatch(tail);
        var pulseMatch = LocalPulseMentionRegex().Match(tail);

        if (
            pulseMatch.Success
            && (!firstDuration.Success || pulseMatch.Index < firstDuration.Index)
            && IsTightConnectorPrefix(tail[..pulseMatch.Index])
        )
        {
            errors.Add(
                $"Step {stepNum}: TimerToken '{{{{{tokenKey}}}}}' is anchored to a source pulse instruction rather than a timed duration. Remove it from timerTokens and write pulses as plain text."
            );
            return;
        }

        if (!firstDuration.Success || !IsTightConnectorPrefix(tail[..firstDuration.Index]))
        {
            return;
        }

        var localEnd = firstDuration.Index + firstDuration.Length;
        while (localEnd < tail.Length)
        {
            var connector = ChainedDurationConnectorRegex().Match(tail[localEnd..]);
            if (!connector.Success)
            {
                break;
            }

            var afterConnector = tail[(localEnd + connector.Length)..];
            var nextDuration = FindEarliestDurationMatch(afterConnector);
            if (!nextDuration.Success || nextDuration.Index != 0)
            {
                break;
            }

            localEnd += connector.Length + nextDuration.Length;
        }

        var localSpan = tail[firstDuration.Index..localEnd];
        var localSeconds = new HashSet<int>();
        var dummyMax = 0;
        CollectDurations(localSpan, null, null, null, localSeconds, ref dummyMax);

        if (localSeconds.Count == 0)
        {
            return;
        }

        if (
            tok.DurationSeconds >= 3600
            && localEnd < tail.Length
            && QualitativeLongDurationRegex().IsMatch(tail[localEnd..])
        )
        {
            return;
        }

        var minLocal = localSeconds.Min();
        var maxLocal = localSeconds.Max();
        var matchesExact = localSeconds.Any(s => Math.Abs(s - tok.DurationSeconds) <= 2);
        var withinRange =
            localSeconds.Count >= 2
            && tok.DurationSeconds >= minLocal - 2
            && tok.DurationSeconds <= maxLocal + 2;

        if (!matchesExact && !withinRange)
        {
            var expectedDesc = minLocal == maxLocal ? $"{minLocal}s" : $"{minLocal}s–{maxLocal}s";
            errors.Add(
                $"Step {stepNum}: TimerToken '{{{{{tokenKey}}}}}' has durationSeconds={tok.DurationSeconds}, which does not match the source duration for this step ({expectedDesc}: '{localSpan.Trim()}')."
            );
        }
    }

    private static int FindAnchorClauseStart(string text, int beforeIndex)
    {
        for (var i = beforeIndex - 1; i >= 0; i--)
        {
            var ch = text[i];
            if (ch is '}' or ';' or '\r' or '\n' or '(' or '!' or '?')
            {
                return i + 1;
            }

            if (ch == '.')
            {
                var isDecimal =
                    i > 0
                    && i + 1 < beforeIndex
                    && char.IsDigit(text[i - 1])
                    && char.IsDigit(text[i + 1]);
                if (!isDecimal)
                {
                    return i + 1;
                }
            }
        }

        return 0;
    }

    private static int FindTailClauseEnd(string text, int startIndex, int maxLen)
    {
        var limit = Math.Min(text.Length, startIndex + maxLen);
        for (var i = startIndex; i < limit; i++)
        {
            var ch = text[i];
            if (ch is ';' or '\r' or '\n' or '!' or '?')
            {
                return i;
            }

            if (ch == '.')
            {
                var isDecimal =
                    i > 0
                    && i + 1 < text.Length
                    && char.IsDigit(text[i - 1])
                    && char.IsDigit(text[i + 1]);
                if (!isDecimal)
                {
                    return i;
                }
            }
        }

        return limit;
    }

    private static int CountWholePhraseMatches(string text, string phrase, out int firstMatchEnd)
    {
        firstMatchEnd = -1;
        var count = 0;
        var searchFrom = 0;

        while (searchFrom < text.Length)
        {
            var idx = text.IndexOf(phrase, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                break;
            }

            var beforeOk = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]);
            var end = idx + phrase.Length;
            var afterOk = end >= text.Length || !char.IsLetterOrDigit(text[end]);

            if (beforeOk && afterOk)
            {
                if (count == 0)
                {
                    firstMatchEnd = end;
                }
                count++;
                if (count >= 2)
                {
                    return count;
                }
            }

            searchFrom = idx + 1;
        }

        return count;
    }

    private static Match FindEarliestDurationMatch(string text)
    {
        var best = Match.Empty;
        Regex[] regexes =
        [
            CompoundHoursMinutesRegex(),
            CompoundMinutesSecondsRegex(),
            SourceDurationRegex(),
            WordDurationRegex(),
        ];

        foreach (var regex in regexes)
        {
            var m = regex.Match(text);
            if (!m.Success)
            {
                continue;
            }

            if (
                !best.Success
                || m.Index < best.Index
                || (m.Index == best.Index && m.Length > best.Length)
            )
            {
                best = m;
            }
        }

        return best;
    }

    private static (int Start, int End) FindSentenceBounds(string text, int startIdx, int endIdx)
    {
        var s = startIdx;
        while (s > 0)
        {
            var ch = text[s - 1];
            if (ch is '\r' or '\n' or '!' or '?' or ';')
            {
                break;
            }
            if (ch == '.' && (s >= text.Length || char.IsWhiteSpace(text[s])))
            {
                break;
            }
            s--;
        }

        var e = endIdx;
        while (e < text.Length)
        {
            var ch = text[e];
            if (ch is '\r' or '\n' or '!' or '?' or ';')
            {
                break;
            }
            if (ch == '.' && (e + 1 >= text.Length || char.IsWhiteSpace(text[e + 1])))
            {
                break;
            }
            e++;
        }

        return (s, e);
    }

    private static bool IsTightConnectorPrefix(string prefix)
    {
        var trimmed = prefix.TrimStart(' ', ',', ':', '-', '\u2013', '\u2014');
        return trimmed.Length <= 22
            && !trimmed.Contains(',')
            && !LocalClauseBreakWordRegex().IsMatch(trimmed);
    }

    private static void ValidateSteps(
        IReadOnlyList<ExtractedStepDto> steps,
        Dictionary<string, ExtractedIngredientDto> ingredientsByRefId,
        HashSet<string> referencedIds,
        Dictionary<string, List<(decimal Quantity, string? Unit)>> stepQuantitiesByRefId,
        SourceDurationIndex sourceDurations,
        string? sourceText,
        decimal? servings,
        List<string> errors
    )
    {
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var stepNum = step.StepNumber > 0 ? step.StepNumber : i + 1;

            if (string.IsNullOrWhiteSpace(step.TemplateText))
            {
                errors.Add($"Step {stepNum}: TemplateText is empty.");
                continue;
            }

            if (step.TemplateText.Contains("||{", StringComparison.Ordinal))
            {
                errors.Add(
                    $"Step {stepNum}: TemplateText must use {{{{token_key}}}} placeholders, not ||{{...}}|| syntax."
                );
            }

            var definedTokenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var timerTokensByKey = new Dictionary<string, StepTimerTokenDto>(
                StringComparer.OrdinalIgnoreCase
            );
            var placeholderMatchesByKey = new Dictionary<string, Match>(
                StringComparer.OrdinalIgnoreCase
            );
            foreach (Match pm in PlaceholderRegex().Matches(step.TemplateText))
            {
                var pKey = NormalizeTokenKey(pm.Groups[1].Value);
                placeholderMatchesByKey.TryAdd(pKey, pm);
            }

            foreach (var tok in step.IngredientTokens ?? [])
            {
                var key = NormalizeTokenKey(tok.TokenKey);
                if (string.IsNullOrWhiteSpace(key))
                {
                    errors.Add($"Step {stepNum}: IngredientToken has an empty TokenKey.");
                    continue;
                }

                if (!definedTokenKeys.Add(key))
                {
                    errors.Add(
                        $"Step {stepNum}: Duplicate token key '{{{{{key}}}}}' across step token arrays."
                    );
                }

                var refId = tok.IngredientRefId?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(refId) || !ingredientsByRefId.ContainsKey(refId))
                {
                    errors.Add(
                        $"Step {stepNum}: IngredientToken '{{{{{key}}}}}' references unknown IngredientRefId '{tok.IngredientRefId}'."
                    );
                }
                else
                {
                    referencedIds.Add(refId);
                    var masterIng = ingredientsByRefId[refId];

                    if (tok.StepQuantity.HasValue)
                    {
                        if (!stepQuantitiesByRefId.TryGetValue(refId, out var qtyList))
                        {
                            qtyList = [];
                            stepQuantitiesByRefId[refId] = qtyList;
                        }
                        qtyList.Add((tok.StepQuantity.Value, tok.StepUnit));
                    }

                    if (tok.StepQuantity is > 0m && !string.IsNullOrWhiteSpace(sourceText))
                    {
                        var (masterUnit, masterCustom) = RecipeUnitAndSectionHelpers.NormalizeUnit(
                            masterIng.Unit
                        );
                        var (stepUnit, stepCustom) = RecipeUnitAndSectionHelpers.NormalizeUnit(
                            tok.StepUnit
                        );
                        var isWholeOrUnitlessMaster =
                            masterUnit == MeasurementUnit.Whole
                            || (!masterUnit.HasValue && string.IsNullOrWhiteSpace(masterCustom));
                        var isWholeOrUnitlessStep =
                            stepUnit == MeasurementUnit.Whole
                            || (!stepUnit.HasValue && string.IsNullOrWhiteSpace(stepCustom));

                        if (isWholeOrUnitlessMaster && isWholeOrUnitlessStep)
                        {
                            if (
                                Math.Abs(tok.StepQuantity.Value - 0.3333m) <= 0.005m
                                && !ThirdFractionTextRegex().IsMatch(sourceText)
                            )
                            {
                                errors.Add(
                                    $"Step {stepNum}: IngredientToken '{{{{{key}}}}}' uses suspicious whole-item fraction stepQuantity={tok.StepQuantity.Value} for '{masterIng.CanonicalFoodItem}' ({refId}), which has no 1/3 fraction in the source. Use the actual whole count (e.g., 1, not 0.3333) or set stepQuantity and stepUnit to null when re-referencing an already-prepped item."
                                );
                            }
                            else if (
                                Math.Abs(tok.StepQuantity.Value - 0.6667m) <= 0.005m
                                && !TwoThirdsFractionTextRegex().IsMatch(sourceText)
                            )
                            {
                                errors.Add(
                                    $"Step {stepNum}: IngredientToken '{{{{{key}}}}}' uses suspicious whole-item fraction stepQuantity={tok.StepQuantity.Value} for '{masterIng.CanonicalFoodItem}' ({refId}), which has no 2/3 fraction in the source. Use the actual whole count or set stepQuantity and stepUnit to null."
                                );
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(tok.FallbackDisplay))
                    {
                        var masterText =
                            $"{masterIng.CanonicalFoodItem} {masterIng.DisplayName} {masterIng.RawText}";
                        if (
                            WaterOrIceTermRegex().IsMatch(tok.FallbackDisplay)
                            && !WaterOrIceTermRegex().IsMatch(masterText)
                        )
                        {
                            errors.Add(
                                $"Step {stepNum}: IngredientToken '{{{{{key}}}}}' ('{tok.FallbackDisplay}') binds water/ice to non-water ingredient '{masterIng.CanonicalFoodItem}' ({refId}). Leave un-listed tap water or ice as plain text in templateText."
                            );
                        }
                        else if (
                            SprayTermRegex().IsMatch(tok.FallbackDisplay)
                            && !masterText.Contains("spray", StringComparison.OrdinalIgnoreCase)
                        )
                        {
                            errors.Add(
                                $"Step {stepNum}: IngredientToken '{{{{{key}}}}}' ('{tok.FallbackDisplay}') binds cooking spray to non-spray ingredient '{masterIng.CanonicalFoodItem}' ({refId}). Use the matching spray ingredient or leave as plain text."
                            );
                        }
                        else if (
                            RenderedFatTermRegex().IsMatch(tok.FallbackDisplay)
                            && !FatOrDrippingMasterRegex().IsMatch(masterText)
                        )
                        {
                            errors.Add(
                                $"Step {stepNum}: IngredientToken '{{{{{key}}}}}' ('{tok.FallbackDisplay}') binds rendered fat/drippings to '{masterIng.CanonicalFoodItem}' ({refId}). Keep derived cooking fat as plain text or reference the meat with stepQuantity: null."
                            );
                        }
                    }

                    if (
                        string.Equals(
                            tok.StepUnit?.Trim(),
                            "custom",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        errors.Add(
                            $"Step {stepNum}: IngredientToken '{{{{{key}}}}}' uses literal 'custom' as stepUnit. Output the concrete unit or container word (e.g., 'wheel', 'sheet', 'can') or 'whole' instead."
                        );
                    }

                    if (
                        !string.IsNullOrWhiteSpace(tok.StepUnit)
                        && LengthUnitRegex().IsMatch(tok.StepUnit.Trim())
                        && (
                            string.IsNullOrWhiteSpace(masterIng.Unit)
                            || !LengthUnitRegex().IsMatch(masterIng.Unit.Trim())
                        )
                    )
                    {
                        errors.Add(
                            $"Step {stepNum}: IngredientToken '{{{{{key}}}}}' uses physical length unit '{tok.StepUnit}' for '{masterIng.CanonicalFoodItem}' ({refId}). Length measurements (inches/cm) belong as plain text in templateText, not step quantities."
                        );
                    }

                    if (
                        servings is >= 2m
                        && tok.StepQuantity is >= 2m
                        && masterIng.Quantity is >= 2m
                        && placeholderMatchesByKey.TryGetValue(key, out var tokMatch)
                    )
                    {
                        var prefixStart = Math.Max(0, tokMatch.Index - 32);
                        var prefix = step.TemplateText[prefixStart..tokMatch.Index];
                        var (mUnitPortion, mCustomPortion) =
                            RecipeUnitAndSectionHelpers.NormalizeUnit(masterIng.Unit);
                        var (sUnitPortion, sCustomPortion) =
                            RecipeUnitAndSectionHelpers.NormalizeUnit(tok.StepUnit);
                        if (
                            SingularPortionOfRegex().IsMatch(prefix)
                            && TryConvertStepQuantityToTarget(
                                tok.StepQuantity.Value,
                                sUnitPortion,
                                sCustomPortion,
                                mUnitPortion,
                                mCustomPortion,
                                out var convPortion
                            )
                            && Math.Abs(convPortion - masterIng.Quantity.Value) <= 0.01m
                        )
                        {
                            errors.Add(
                                $"Step {stepNum}: IngredientToken '{{{{{key}}}}}' places full batch quantity ({tok.StepQuantity.Value:0.###} {tok.StepUnit}) inside a singular per-portion phrase ('{prefix.Trim()}') for '{masterIng.CanonicalFoodItem}'. Put the total batch quantity on the portioning step instead and use the per-portion amount or null here."
                            );
                        }
                    }
                }

                if (tok.StepQuantity.HasValue && tok.StepQuantity.Value <= 0m)
                {
                    errors.Add(
                        $"Step {stepNum}: IngredientToken '{{{{{key}}}}}' has non-positive StepQuantity {tok.StepQuantity.Value}. Use null (not 0) for stepQuantity and stepUnit when re-referencing an already-added item or omitting an amount."
                    );
                }
            }

            foreach (var tok in step.TimerTokens ?? [])
            {
                var key = NormalizeTokenKey(tok.TokenKey);
                if (string.IsNullOrWhiteSpace(key))
                {
                    errors.Add($"Step {stepNum}: TimerToken has an empty TokenKey.");
                    continue;
                }

                if (!definedTokenKeys.Add(key))
                {
                    errors.Add(
                        $"Step {stepNum}: Duplicate token key '{{{{{key}}}}}' across step token arrays."
                    );
                }
                else
                {
                    timerTokensByKey[key] = tok;
                }

                if (tok.DurationSeconds <= 0)
                {
                    errors.Add(
                        $"Step {stepNum}: TimerToken '{{{{{key}}}}}' must have positive DurationSeconds."
                    );
                }
                else
                {
                    if (
                        sourceDurations.ExplicitMinuteIntegers.Contains(tok.DurationSeconds)
                        && !sourceDurations.ExplicitSecondIntegers.Contains(tok.DurationSeconds)
                    )
                    {
                        errors.Add(
                            $"Step {stepNum}: TimerToken '{{{{{key}}}}}' has durationSeconds={tok.DurationSeconds}, which matches a raw minute count in the source ('{tok.DurationSeconds} minutes'). Convert minutes to seconds ({tok.DurationSeconds} * 60 = {tok.DurationSeconds * 60})."
                        );
                    }
                    else if (
                        tok.DurationSeconds <= 24
                        && sourceDurations.ExplicitHourIntegers.Contains(tok.DurationSeconds)
                        && !sourceDurations.ExplicitSecondIntegers.Contains(tok.DurationSeconds)
                        && !sourceDurations.ExplicitMinuteIntegers.Contains(tok.DurationSeconds)
                    )
                    {
                        errors.Add(
                            $"Step {stepNum}: TimerToken '{{{{{key}}}}}' has durationSeconds={tok.DurationSeconds}, which matches a raw hour count in the source ('{tok.DurationSeconds} hours'). Convert hours to seconds ({tok.DurationSeconds} * 3600 = {tok.DurationSeconds * 3600})."
                        );
                    }
                    else if (
                        !sourceDurations.HasQualitativeLongDuration
                        && sourceDurations.MaxDurationSeconds > 0
                        && tok.DurationSeconds > 1800
                        && tok.DurationSeconds > sourceDurations.MaxDurationSeconds * 2
                    )
                    {
                        errors.Add(
                            $"Step {stepNum}: TimerToken '{{{{{key}}}}}' has durationSeconds={tok.DurationSeconds} ({tok.DurationSeconds / 60} min), which exceeds twice the longest duration in the source recipe ({sourceDurations.MaxDurationSeconds}s / {sourceDurations.MaxDurationSeconds / 60} min). Re-check unit conversion."
                        );
                    }
                    else if (
                        !string.IsNullOrWhiteSpace(sourceText)
                        && sourceDurations.ConvertedSeconds.Count == 0
                        && !sourceDurations.HasQualitativeLongDuration
                    )
                    {
                        errors.Add(
                            $"Step {stepNum}: TimerToken '{{{{{key}}}}}' has durationSeconds={tok.DurationSeconds}, but the source recipe specifies no timed durations. Remove invented timerTokens and keep pulses or untimed actions as plain text."
                        );
                    }
                    else if (
                        !string.IsNullOrWhiteSpace(sourceText)
                        && sourceDurations.ConvertedSeconds.Count > 0
                        && !(
                            sourceDurations.HasQualitativeLongDuration
                            && tok.DurationSeconds >= 3600
                        )
                        && !sourceDurations.ConvertedSeconds.Any(s =>
                            Math.Abs(s - tok.DurationSeconds) <= 2
                        )
                    )
                    {
                        errors.Add(
                            $"Step {stepNum}: TimerToken '{{{{{key}}}}}' has durationSeconds={tok.DurationSeconds} ({tok.DurationSeconds / 60m:0.##} min), which does not match any timed duration in the source recipe. Keep only true source durations in timerTokens (never convert pulses or inch measurements into timers, and never multiply seconds by 6 or 10)."
                        );
                    }
                    else
                    {
                        ValidateTimerAgainstLocalSourceClause(
                            stepNum,
                            key,
                            tok,
                            step.TemplateText,
                            sourceDurations.NormalizedAnchorText,
                            errors
                        );
                    }
                }

                if (string.IsNullOrWhiteSpace(tok.Label))
                {
                    errors.Add(
                        $"Step {stepNum}: TimerToken '{{{{{key}}}}}' must have a descriptive Label."
                    );
                }
                else if (NonTimeTimerLabelRegex().IsMatch(tok.Label))
                {
                    errors.Add(
                        $"Step {stepNum}: TimerToken '{{{{{key}}}}}' ('{tok.Label}') represents a physical measurement rather than a timed duration. Remove it from timerTokens and keep measurements as plain text."
                    );
                }
                else if (
                    BarePulseLabelRegex().IsMatch(tok.Label)
                    || (
                        PulseWordRegex().IsMatch(tok.Label)
                        && (
                            tok.DurationSeconds > 5
                            || !sourceDurations.ExplicitSecondIntegers.Contains(tok.DurationSeconds)
                        )
                    )
                )
                {
                    errors.Add(
                        $"Step {stepNum}: TimerToken '{{{{{key}}}}}' ('{tok.Label}') represents food-processor pulses rather than a timed duration. Remove it from timerTokens and write pulses as plain text."
                    );
                }
            }

            foreach (var tok in step.TemperatureTokens ?? [])
            {
                var key = NormalizeTokenKey(tok.TokenKey);
                if (string.IsNullOrWhiteSpace(key))
                {
                    errors.Add($"Step {stepNum}: TemperatureToken has an empty TokenKey.");
                    continue;
                }

                if (!definedTokenKeys.Add(key))
                {
                    errors.Add(
                        $"Step {stepNum}: Duplicate token key '{{{{{key}}}}}' across step token arrays."
                    );
                }

                if (!tok.Value.HasValue || tok.Value.Value <= 0m)
                {
                    errors.Add(
                        $"Step {stepNum}: TemperatureToken '{{{{{key}}}}}' must have positive Value."
                    );
                }
                else if (
                    tok.Value.Value <= 100m
                    && MicrowaveOrPowerContextRegex().IsMatch(step.TemplateText)
                )
                {
                    errors.Add(
                        $"Step {stepNum}: TemperatureToken '{{{{{key}}}}}' has value {tok.Value.Value:0.###} in a microwave/power instruction. Keep microwave power percentages (e.g., '40 percent power') as plain text instead of extracting a temperature token."
                    );
                }

                var unit = tok.BaseUnit?.Trim();
                if (
                    !string.Equals(unit, "F", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(unit, "C", StringComparison.OrdinalIgnoreCase)
                )
                {
                    errors.Add(
                        $"Step {stepNum}: TemperatureToken '{{{{{key}}}}}' BaseUnit must be 'F' or 'C'."
                    );
                }
                else if (tok.Value is > 0m)
                {
                    var fEquiv = string.Equals(unit, "C", StringComparison.OrdinalIgnoreCase)
                        ? (tok.Value.Value * 9m / 5m) + 32m
                        : tok.Value.Value;
                    var isDistanceContext = false;
                    if (placeholderMatchesByKey.TryGetValue(key, out var tempMatch))
                    {
                        var (sentStart, sentEnd) = FindSentenceBounds(
                            step.TemplateText,
                            tempMatch.Index,
                            tempMatch.Index + tempMatch.Length
                        );
                        var beforeInSentence = step.TemplateText[sentStart..tempMatch.Index];
                        var afterInSentence = step.TemplateText[
                            (tempMatch.Index + tempMatch.Length)..sentEnd
                        ];

                        if (AdjacentDistanceAfterTempRegex().IsMatch(afterInSentence))
                        {
                            isDistanceContext = true;
                        }
                        else if (
                            !(
                                fEquiv >= 100m
                                && ThermalVerbBeforeTempRegex().IsMatch(beforeInSentence)
                            )
                        )
                        {
                            var localLeft = beforeInSentence[
                                Math.Max(0, beforeInSentence.Length - 35)..
                            ];
                            var localRight = afterInSentence[
                                ..Math.Min(afterInSentence.Length, 35)
                            ];
                            var strippedWindow = ExplicitNumberedLengthOrRackPosRegex()
                                .Replace($"{localLeft} {localRight}", " ");
                            isDistanceContext = DistanceOrRackTempContextRegex()
                                .IsMatch(strippedWindow);
                        }
                    }
                    else
                    {
                        isDistanceContext = DistanceOrRackTempContextRegex()
                            .IsMatch(
                                ExplicitNumberedLengthOrRackPosRegex()
                                    .Replace(step.TemplateText, " ")
                            );
                    }

                    if (isDistanceContext)
                    {
                        errors.Add(
                            $"Step {stepNum}: TemperatureToken '{{{{{key}}}}}' ({tok.Value.Value:0.###}°{unit!.ToUpperInvariant()}) appears next to a physical rack or length distance rather than a cooking temperature. Keep distances (e.g., '4 inches from broiler element') as plain text in templateText."
                        );
                    }
                    else if (
                        !(
                            tok.Value.Value <= 100m
                            && MicrowaveOrPowerContextRegex().IsMatch(step.TemplateText)
                        )
                        && (
                            (
                                fEquiv < 100m
                                && ThermalTempContextRegex().IsMatch(step.TemplateText)
                                && !ColdOrLowTempContextRegex().IsMatch(step.TemplateText)
                            )
                            || (
                                fEquiv < 32m
                                && !ColdOrLowTempContextRegex().IsMatch(step.TemplateText)
                            )
                        )
                    )
                    {
                        errors.Add(
                            $"Step {stepNum}: TemperatureToken '{{{{{key}}}}}' has implausibly low temperature {tok.Value.Value:0.###}°{unit!.ToUpperInvariant()} without freezer/ice/proofing context. Keep non-temperature numbers or distances as plain text."
                        );
                    }
                }
            }

            var templateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var reportedDuplicateTemplateKeys = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase
            );
            foreach (Match match in PlaceholderRegex().Matches(step.TemplateText))
            {
                var placeholderKey = NormalizeTokenKey(match.Groups[1].Value);
                if (
                    !templateKeys.Add(placeholderKey)
                    && reportedDuplicateTemplateKeys.Add(placeholderKey)
                )
                {
                    errors.Add(
                        $"Step {stepNum}: Token placeholder '{{{{{placeholderKey}}}}}' appears more than once in TemplateText. Each mention must use a distinct tokenKey (e.g., append _b) with stepQuantity and stepUnit set to null on subsequent mentions."
                    );
                }

                if (!definedTokenKeys.Contains(placeholderKey))
                {
                    errors.Add(
                        $"Step {stepNum} TemplateText contains token '{{{{{placeholderKey}}}}}', which is missing from Step {stepNum} token arrays."
                    );
                }
                else if (timerTokensByKey.ContainsKey(placeholderKey))
                {
                    var trailing = step.TemplateText[(match.Index + match.Length)..];
                    if (LeadingPulseAfterTokenRegex().IsMatch(trailing))
                    {
                        errors.Add(
                            $"Step {stepNum}: TimerToken '{{{{{placeholderKey}}}}}' precedes 'pulses', which is a count rather than a timed duration. Remove this timer token and keep pulses as plain text."
                        );
                    }
                }
            }

            foreach (var definedKey in definedTokenKeys)
            {
                if (!templateKeys.Contains(definedKey))
                {
                    errors.Add(
                        $"Step {stepNum}: Token '{{{{{definedKey}}}}}' is defined in step token arrays but missing from TemplateText. Either place '{{{{{definedKey}}}}}' in TemplateText if that step adds the ingredient, OR remove it from token arrays if referenced only as a combined mixture (such as 'sauce', 'batter', or 'dough')."
                    );
                }
            }
        }

        ValidateNumberedSourceStepCoverage(steps, ingredientsByRefId, sourceText, errors);
    }

    private static void ValidateNumberedSourceStepCoverage(
        IReadOnlyList<ExtractedStepDto> steps,
        Dictionary<string, ExtractedIngredientDto> ingredientsByRefId,
        string? sourceText,
        List<string> errors
    )
    {
        if (string.IsNullOrWhiteSpace(sourceText) || steps.Count == 0)
        {
            return;
        }

        var body = MetadataHeaderLineRegex().Replace(sourceText, string.Empty);
        var instrIdx = body.IndexOf("Instructions:", StringComparison.OrdinalIgnoreCase);
        if (instrIdx < 0)
        {
            return;
        }

        var instrBody = body[(instrIdx + "Instructions:".Length)..].Trim();
        var numberedMatches = NumberedSourceStepRegex().Matches(instrBody);
        if (numberedMatches.Count < 3)
        {
            return;
        }

        var combinedBuilder = new global::System.Text.StringBuilder();
        foreach (var step in steps)
        {
            var tokenMap = BuildFirstByNormalizedKey(
                step.IngredientTokens,
                t => t.TokenKey,
                t =>
                {
                    if (!string.IsNullOrWhiteSpace(t.FallbackDisplay))
                    {
                        return t.FallbackDisplay;
                    }

                    if (
                        !string.IsNullOrWhiteSpace(t.IngredientRefId)
                        && ingredientsByRefId.TryGetValue(t.IngredientRefId.Trim(), out var m)
                    )
                    {
                        return !string.IsNullOrWhiteSpace(m.DisplayName)
                            ? m.DisplayName
                            : m.CanonicalFoodItem;
                    }

                    return string.Empty;
                }
            );

            var expanded = PlaceholderRegex()
                .Replace(
                    step.TemplateText ?? string.Empty,
                    m =>
                    {
                        var key = NormalizeTokenKey(m.Groups[1].Value);
                        return tokenMap.TryGetValue(key, out var val) ? val : string.Empty;
                    }
                );
            combinedBuilder.AppendLine(expanded);
        }

        var extractedWords = new HashSet<string>(
            ContentWordRegex()
                .Matches(combinedBuilder.ToString())
                .Select(m => m.Value.ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase
        );

        foreach (Match srcStep in numberedMatches)
        {
            var stepNumStr = srcStep.Groups["num"].Value;
            var stepText = MultiSpaceRegex().Replace(srcStep.Groups["body"].Value, " ").Trim();
            if (stepText.Length < 55 || StorageOrNoteParagraphRegex().IsMatch(stepText))
            {
                continue;
            }

            var contentWords = ContentWordRegex()
                .Matches(stepText)
                .Select(m => m.Value.ToLowerInvariant())
                .Where(w => !StepCoverageStopwords.Contains(w))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (contentWords.Count < 5)
            {
                continue;
            }

            var matched = contentWords.Count(w => extractedWords.Contains(w));
            if (matched == 0 || (matched * 100 / contentWords.Count) < 20)
            {
                var snippet = stepText.Length > 70 ? stepText[..70] + "..." : stepText;
                errors.Add(
                    $"Source Instruction Step {stepNumStr} ('{snippet}') appears to have been omitted from Steps. Preserve every action step and sentence in chronological order."
                );
            }
        }
    }

    /// <summary>
    /// Last-resort deterministic cleanup after validation retries are exhausted: splits duplicate
    /// <c>{{ing_X}}</c> placeholders within a single step into distinct <c>_2</c>/<c>_3</c> tokens
    /// with <c>StepQuantity = null</c>, and prunes orphan tokens missing from <c>TemplateText</c>
    /// when the ingredient carries no step quantity or is already accounted for in <c>TemplateText</c>.
    /// </summary>
    [GeneratedRegex(
        @"\b(to|at)\s+at\s+(?=\{\{([^{}\r\n]+)\}\})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex DuplicateTempPrepositionRegex();

    [GeneratedRegex(
        @"\{\{([^{}\r\n]+)\}\}(?:\s*[-–—]\s*(?:degrees?(?:\s*(?:[FC]|Fahrenheit|Celsius))?|deg\.?(?:\s*(?:[FC]|Fahrenheit|Celsius))?)|\s+(?:degrees?(?:\s*(?:[FC]|Fahrenheit|Celsius))?|deg\.?(?:\s*(?:[FC]|Fahrenheit|Celsius))?)|\s*°\s*[FC])(?=$|[\s\.,:;!\?\)\]])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex DuplicateTempUnitSuffixRegex();

    [GeneratedRegex(
        @"\b(?:half(?:\s+of)?|other\s+half(?:\s+of)?|remaining|remainder(?:\s+of)?|rest\s+of|portion(?:\s+of)?|all\s+of|reserved)\s+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex BatchSplitPrefixRegex();

    private static bool IsNullOrUnlistedRefId(string? refId)
    {
        if (string.IsNullOrWhiteSpace(refId))
        {
            return true;
        }

        var trimmed = refId.Trim();
        return string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "unlisted", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "n/a", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "na", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanInlineExplicitUnlistedToken(
        StepIngredientTokenDto tok,
        Dictionary<string, ExtractedIngredientDto> ingredientsByRefId,
        HashSet<string> unreferencedMasterWords
    )
    {
        if (tok.StepQuantity is > 0m || string.IsNullOrWhiteSpace(tok.FallbackDisplay))
        {
            return false;
        }

        var trimmedRef = tok.IngredientRefId?.Trim() ?? string.Empty;
        var isUnlisted =
            IsNullOrUnlistedRefId(trimmedRef)
            || (
                NormalizeTokenKey(tok.TokenKey)
                    .StartsWith("unlisted_", StringComparison.OrdinalIgnoreCase)
                && !ingredientsByRefId.ContainsKey(trimmedRef)
            );
        if (!isUnlisted)
        {
            return false;
        }

        if (unreferencedMasterWords.Count == 0)
        {
            return true;
        }

        foreach (Match w in ContentWordRegex().Matches(tok.FallbackDisplay))
        {
            if (unreferencedMasterWords.Contains(w.Value.ToLowerInvariant()))
            {
                return false;
            }
        }

        return true;
    }

    public static ExtractedRecipeDto RepairSafeStepTokenGlitches(
        ExtractedRecipeDto recipe,
        bool repairBatchSplitDoubleCounts = false
    )
    {
        if (recipe.Steps is null || recipe.Steps.Count == 0)
        {
            return recipe;
        }

        var ingredientsByRefId = (recipe.Ingredients ?? [])
            .Where(i => !string.IsNullOrWhiteSpace(i.RefId))
            .GroupBy(i => i.RefId!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var usedTemplateIngRefIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in recipe.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.TemplateText))
            {
                continue;
            }

            var stepIngByKey = BuildFirstByNormalizedKey(
                step.IngredientTokens,
                t => t.TokenKey,
                t => t
            );
            foreach (Match m in PlaceholderRegex().Matches(step.TemplateText))
            {
                var key = NormalizeTokenKey(m.Groups[1].Value);
                if (
                    stepIngByKey.TryGetValue(key, out var tok)
                    && !string.IsNullOrWhiteSpace(tok.IngredientRefId)
                    && ingredientsByRefId.ContainsKey(tok.IngredientRefId.Trim())
                )
                {
                    usedTemplateIngRefIds.Add(tok.IngredientRefId.Trim());
                }
            }
        }

        var unreferencedMasterWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ing in recipe.Ingredients ?? [])
        {
            var refId = ing.RefId?.Trim() ?? string.Empty;
            if (
                string.IsNullOrWhiteSpace(refId)
                || usedTemplateIngRefIds.Contains(refId)
                || IsBenignUnreferencedIngredient(ing)
            )
            {
                continue;
            }

            foreach (var src in new[] { ing.CanonicalFoodItem, ing.DisplayName })
            {
                if (string.IsNullOrWhiteSpace(src))
                {
                    continue;
                }

                foreach (Match w in ContentWordRegex().Matches(src))
                {
                    unreferencedMasterWords.Add(w.Value.ToLowerInvariant());
                }
            }
        }

        // Pre-track cumulative step quantities per ingredient (in step order) when post-retry
        // batch-split repair is requested (e.g., Step 2 added 1 lb; Step 3 says "Fry half of {{ing_X}}").
        var fullBatchAlreadyAdded = new Dictionary<string, decimal>(
            StringComparer.OrdinalIgnoreCase
        );

        var updatedSteps = new List<ExtractedStepDto>(recipe.Steps.Count);
        var changedAny = false;

        foreach (
            var step in recipe
                .Steps.Select((s, idx) => (Step: s, Order: idx))
                .OrderBy(x => x.Step.StepNumber > 0 ? x.Step.StepNumber : x.Order + 1)
                .ThenBy(x => x.Order)
                .Select(x => x.Step)
        )
        {
            var template = step.TemplateText ?? string.Empty;
            var ingTokens = DeduplicateTokensByKey(
                step.IngredientTokens,
                t => t.TokenKey,
                t => t.StepQuantity is > 0m,
                out var dedupIngChanged
            );
            var timerTokens = DeduplicateTokensByKey(
                step.TimerTokens,
                t => t.TokenKey,
                t => t.DurationSeconds > 0,
                out var dedupTimerChanged
            );
            var tempTokens = DeduplicateTokensByKey(
                step.TemperatureTokens,
                t => t.TokenKey,
                t => t.Value is > 0m,
                out var dedupTempChanged
            );

            var ingMap = BuildFirstByNormalizedKey(ingTokens, t => t.TokenKey, t => t);
            var timerMap = BuildFirstByNormalizedKey(timerTokens, t => t.TokenKey, t => t);
            var tempMap = BuildFirstByNormalizedKey(tempTokens, t => t.TokenKey, t => t);

            var allDefinedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in ingMap.Keys)
                allDefinedKeys.Add(k);
            foreach (var k in timerMap.Keys)
                allDefinedKeys.Add(k);
            foreach (var k in tempMap.Keys)
                allDefinedKeys.Add(k);

            var seenInTemplate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stepChanged = dedupIngChanged || dedupTimerChanged || dedupTempChanged;
            var keysToInlineRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var rewrittenTemplate = PlaceholderRegex()
                .Replace(
                    template,
                    m =>
                    {
                        var rawKey = m.Groups[1].Value;
                        var key = NormalizeTokenKey(rawKey);

                        if (
                            ingMap.TryGetValue(key, out var maybeUnlisted)
                            && CanInlineExplicitUnlistedToken(
                                maybeUnlisted,
                                ingredientsByRefId,
                                unreferencedMasterWords
                            )
                        )
                        {
                            keysToInlineRemove.Add(key);
                            stepChanged = true;
                            return maybeUnlisted.FallbackDisplay!.Trim();
                        }

                        if (seenInTemplate.Add(key))
                        {
                            return m.Value;
                        }

                        if (ingMap.TryGetValue(key, out var origIng))
                        {
                            var suffix = 2;
                            var candidateKey = $"{key}_{suffix}";
                            while (!allDefinedKeys.Add(candidateKey))
                            {
                                suffix++;
                                candidateKey = $"{key}_{suffix}";
                            }

                            var cloned = origIng with
                            {
                                TokenKey = candidateKey,
                                StepQuantity = null,
                                StepUnit = null,
                            };
                            ingTokens.Add(cloned);
                            ingMap[candidateKey] = cloned;
                            seenInTemplate.Add(candidateKey);
                            stepChanged = true;
                            return $"{{{{{candidateKey}}}}}";
                        }

                        if (
                            timerMap.TryGetValue(key, out var timerTok)
                            && timerTok.DurationSeconds > 0
                        )
                        {
                            stepChanged = true;
                            return FormatSimpleDurationText(timerTok.DurationSeconds);
                        }

                        if (tempMap.TryGetValue(key, out var tempTok) && tempTok.Value is > 0m)
                        {
                            stepChanged = true;
                            var rounded = (int)
                                Math.Round(tempTok.Value.Value, MidpointRounding.AwayFromZero);
                            var unit = string.Equals(
                                tempTok.BaseUnit?.Trim(),
                                "C",
                                StringComparison.OrdinalIgnoreCase
                            )
                                ? "°C"
                                : "°F";
                            return $"{rounded}{unit}";
                        }

                        return m.Value;
                    }
                );

            if (keysToInlineRemove.Count > 0)
            {
                ingTokens.RemoveAll(t =>
                    keysToInlineRemove.Contains(NormalizeTokenKey(t.TokenKey))
                );
            }

            if (repairBatchSplitDoubleCounts && !string.IsNullOrEmpty(rewrittenTemplate))
            {
                var ingTokenIdxByKey = new Dictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase
                );
                for (var idx = 0; idx < ingTokens.Count; idx++)
                {
                    ingTokenIdxByKey.TryAdd(NormalizeTokenKey(ingTokens[idx].TokenKey), idx);
                }

                foreach (Match pm in PlaceholderRegex().Matches(rewrittenTemplate))
                {
                    var key = NormalizeTokenKey(pm.Groups[1].Value);
                    if (!ingTokenIdxByKey.TryGetValue(key, out var tokIdx))
                    {
                        continue;
                    }

                    var tok = ingTokens[tokIdx];
                    var refId = tok.IngredientRefId?.Trim() ?? string.Empty;
                    if (
                        string.IsNullOrWhiteSpace(refId)
                        || !ingredientsByRefId.TryGetValue(refId, out var master)
                        || !master.Quantity.HasValue
                        || master.Quantity.Value <= 0m
                        || tok.StepQuantity is not > 0m
                    )
                    {
                        continue;
                    }

                    var upperLimit = Math.Max(
                        master.Quantity.Value,
                        master.MaxQuantity ?? master.Quantity.Value
                    );
                    var (mUnit, mCustom) = RecipeUnitAndSectionHelpers.NormalizeUnit(master.Unit);
                    var (sUnit, sCustom) = RecipeUnitAndSectionHelpers.NormalizeUnit(tok.StepUnit);
                    if (!sUnit.HasValue && string.IsNullOrWhiteSpace(sCustom))
                    {
                        sUnit = mUnit;
                        sCustom = mCustom;
                    }

                    if (
                        !TryConvertStepQuantityToTarget(
                            tok.StepQuantity.Value,
                            sUnit,
                            sCustom,
                            mUnit,
                            mCustom,
                            out var convertedQty
                        )
                    )
                    {
                        continue;
                    }

                    fullBatchAlreadyAdded.TryGetValue(refId, out var priorSum);
                    var beforeMatch = rewrittenTemplate[..pm.Index];
                    if (
                        priorSum >= upperLimit * 0.99m
                        && priorSum <= upperLimit * 1.01m
                        && BatchSplitPrefixRegex().IsMatch(beforeMatch)
                    )
                    {
                        ingTokens[tokIdx] = tok with { StepQuantity = null, StepUnit = null };
                        stepChanged = true;
                        continue;
                    }

                    fullBatchAlreadyAdded[refId] = priorSum + convertedQty;
                }
            }

            if (tempMap.Count > 0 && !string.IsNullOrEmpty(rewrittenTemplate))
            {
                var cleanedTempPrep = DuplicateTempPrepositionRegex()
                    .Replace(
                        rewrittenTemplate,
                        m =>
                            tempMap.ContainsKey(NormalizeTokenKey(m.Groups[2].Value))
                                ? $"{m.Groups[1].Value} "
                                : m.Value
                    );
                var cleanedTempSuffix = DuplicateTempUnitSuffixRegex()
                    .Replace(
                        cleanedTempPrep,
                        m =>
                            tempMap.ContainsKey(NormalizeTokenKey(m.Groups[1].Value))
                                ? $"{{{{{NormalizeTokenKey(m.Groups[1].Value)}}}}}"
                                : m.Value
                    );
                if (!string.Equals(cleanedTempSuffix, rewrittenTemplate, StringComparison.Ordinal))
                {
                    rewrittenTemplate = cleanedTempSuffix;
                    stepChanged = true;
                }
            }

            var templateKeySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in PlaceholderRegex().Matches(rewrittenTemplate))
            {
                templateKeySet.Add(NormalizeTokenKey(m.Groups[1].Value));
            }

            var filteredIngs = ingTokens
                .Where(t =>
                {
                    var key = NormalizeTokenKey(t.TokenKey);
                    if (templateKeySet.Contains(key))
                    {
                        return true;
                    }

                    var isOrphanNullOrAlreadyUsed =
                        t.StepQuantity is null
                        || (
                            !string.IsNullOrWhiteSpace(t.IngredientRefId)
                            && usedTemplateIngRefIds.Contains(t.IngredientRefId.Trim())
                        );
                    if (isOrphanNullOrAlreadyUsed)
                    {
                        stepChanged = true;
                        return false;
                    }

                    return true;
                })
                .ToList();

            var filteredTimers = timerTokens
                .Where(t =>
                {
                    var keep = templateKeySet.Contains(NormalizeTokenKey(t.TokenKey));
                    if (!keep)
                    {
                        stepChanged = true;
                    }
                    return keep;
                })
                .ToList();

            var filteredTemps = tempTokens
                .Where(t =>
                {
                    var keep = templateKeySet.Contains(NormalizeTokenKey(t.TokenKey));
                    if (!keep)
                    {
                        stepChanged = true;
                    }
                    return keep;
                })
                .ToList();

            if (stepChanged)
            {
                changedAny = true;
                updatedSteps.Add(
                    step with
                    {
                        TemplateText = rewrittenTemplate,
                        IngredientTokens = filteredIngs,
                        TimerTokens = filteredTimers,
                        TemperatureTokens = filteredTemps,
                    }
                );
            }
            else
            {
                updatedSteps.Add(step);
            }
        }

        return changedAny ? recipe with { Steps = updatedSteps } : recipe;
    }

    private static Dictionary<string, TValue> BuildFirstByNormalizedKey<TToken, TValue>(
        IEnumerable<TToken>? tokens,
        Func<TToken, string?> keySelector,
        Func<TToken, TValue> valueSelector
    )
    {
        var map = new Dictionary<string, TValue>(StringComparer.OrdinalIgnoreCase);
        if (tokens is null)
        {
            return map;
        }

        foreach (var token in tokens)
        {
            var key = NormalizeTokenKey(keySelector(token));
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (!map.ContainsKey(key))
            {
                map[key] = valueSelector(token);
            }
        }

        return map;
    }

    private static List<TToken> DeduplicateTokensByKey<TToken>(
        IReadOnlyList<TToken>? tokens,
        Func<TToken, string?> keySelector,
        Func<TToken, bool> preferOverExisting,
        out bool changed
    )
    {
        changed = false;
        if (tokens is null || tokens.Count == 0)
        {
            return [];
        }

        var result = new List<TToken>(tokens.Count);
        var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens)
        {
            var key = NormalizeTokenKey(keySelector(token));
            if (string.IsNullOrWhiteSpace(key))
            {
                result.Add(token);
                continue;
            }

            if (indexByKey.TryGetValue(key, out var existingIdx))
            {
                changed = true;
                if (!preferOverExisting(result[existingIdx]) && preferOverExisting(token))
                {
                    result[existingIdx] = token;
                }
                continue;
            }

            indexByKey[key] = result.Count;
            result.Add(token);
        }

        return result;
    }

    private static string FormatSimpleDurationText(int durationSeconds)
    {
        if (durationSeconds % 3600 == 0)
        {
            var h = durationSeconds / 3600;
            return h == 1 ? "1 hour" : $"{h} hours";
        }

        if (durationSeconds % 60 == 0)
        {
            var m = durationSeconds / 60;
            return m == 1 ? "1 minute" : $"{m} minutes";
        }

        return durationSeconds == 1 ? "1 second" : $"{durationSeconds} seconds";
    }

    private static void ValidateIngredientQuantitySums(
        Dictionary<string, ExtractedIngredientDto> ingredientsByRefId,
        Dictionary<string, List<(decimal Quantity, string? Unit)>> stepQuantitiesByRefId,
        List<string> errors
    )
    {
        foreach (var (refId, stepUsages) in stepQuantitiesByRefId)
        {
            if (
                !ingredientsByRefId.TryGetValue(refId, out var master)
                || !master.Quantity.HasValue
                || master.Quantity.Value <= 0m
            )
            {
                continue;
            }

            var upperLimit = Math.Max(
                master.Quantity.Value,
                master.MaxQuantity ?? master.Quantity.Value
            );
            var (masterUnit, masterCustomUnit) = RecipeUnitAndSectionHelpers.NormalizeUnit(
                master.Unit
            );
            var (altUnit, altCustomUnit) = master.AltQuantity is > 0m
                ? RecipeUnitAndSectionHelpers.NormalizeUnit(master.AltUnit)
                : (null, null);

            decimal summedInMasterUnit = 0m;
            var comparableCount = 0;
            decimal summedInAltUnit = 0m;
            var comparableAltCount = 0;

            foreach (var (stepQty, stepUnitRaw) in stepUsages)
            {
                if (stepQty <= 0m)
                {
                    continue;
                }

                var (stepUnit, stepCustomUnit) = RecipeUnitAndSectionHelpers.NormalizeUnit(
                    stepUnitRaw
                );
                if (!stepUnit.HasValue && string.IsNullOrWhiteSpace(stepCustomUnit))
                {
                    stepUnit = masterUnit;
                    stepCustomUnit = masterCustomUnit;
                }

                if (
                    TryConvertStepQuantityToTarget(
                        stepQty,
                        stepUnit,
                        stepCustomUnit,
                        masterUnit,
                        masterCustomUnit,
                        out var convertedMaster
                    )
                )
                {
                    summedInMasterUnit += convertedMaster;
                    comparableCount++;
                }
                else if (
                    master.AltQuantity is > 0m
                    && TryConvertStepQuantityToTarget(
                        stepQty,
                        stepUnit,
                        stepCustomUnit,
                        altUnit,
                        altCustomUnit,
                        out var convertedAlt
                    )
                )
                {
                    summedInAltUnit += convertedAlt;
                    comparableAltCount++;
                }
                else if (
                    (
                        masterUnit == MeasurementUnit.Lb
                        && stepUnit is MeasurementUnit.Tsp or MeasurementUnit.Tbsp
                    )
                    || (
                        masterUnit == MeasurementUnit.Slice
                        && stepUnit
                            is MeasurementUnit.Tsp
                                or MeasurementUnit.Tbsp
                                or MeasurementUnit.Cup
                    )
                )
                {
                    errors.Add(
                        $"Step quantity unit '{stepUnitRaw ?? stepUnit.ToString()}' for '{master.CanonicalFoodItem}' ({refId}) is incompatible with master unit '{master.Unit}'. Use a compatible unit or set stepQuantity and stepUnit to null when referencing rendered fat or unmeasured portions."
                    );
                }
            }

            if (
                comparableCount >= 1
                && summedInMasterUnit
                    > (upperLimit * QuantityMismatchToleranceFactor)
                        + QuantityMismatchToleranceAbsolute
            )
            {
                errors.Add(
                    $"Quantity sum mismatch for '{master.CanonicalFoodItem}' ({refId}): step quantities sum to {summedInMasterUnit:0.###}, exceeding master quantity {upperLimit:0.###}. If a later step re-references an ingredient already added in full earlier (e.g., cooking, draining, stirring, or assembling), set stepQuantity and stepUnit to null on subsequent mentions. If one source line had two separate parts/units (e.g., zest + juice), split them into separate master ingredients."
                );
            }
            else if (
                comparableAltCount >= 1
                && master.AltQuantity is > 0m
                && summedInAltUnit
                    > (master.AltQuantity.Value * QuantityMismatchToleranceFactor)
                        + QuantityMismatchToleranceAbsolute
            )
            {
                errors.Add(
                    $"Quantity sum mismatch for '{master.CanonicalFoodItem}' ({refId}): step quantities sum to {summedInAltUnit:0.###}, exceeding alternate master quantity {master.AltQuantity.Value:0.###}. If a later step re-references an already-added ingredient, set stepQuantity and stepUnit to null on subsequent mentions."
                );
            }
            else if (
                comparableCount >= 1
                && comparableAltCount >= 1
                && master.AltQuantity is > 0m
                && (
                    (summedInMasterUnit / upperLimit) + (summedInAltUnit / master.AltQuantity.Value)
                ) > QuantityMismatchToleranceFactor
            )
            {
                errors.Add(
                    $"Quantity sum mismatch for '{master.CanonicalFoodItem}' ({refId}): step quantities consume {summedInMasterUnit:0.###} of {upperLimit:0.###} ({master.Unit}) plus {summedInAltUnit:0.###} of {master.AltQuantity.Value:0.###} ({master.AltUnit}), exceeding 100% of the ingredient across primary and alternate units. Use one consistent unit or set stepQuantity and stepUnit to null on subsequent mentions."
                );
            }
        }
    }

    private static bool TryConvertStepQuantityToTarget(
        decimal stepQty,
        MeasurementUnit? stepUnit,
        string? stepCustomUnit,
        MeasurementUnit? targetUnit,
        string? targetCustomUnit,
        out decimal convertedQty
    )
    {
        convertedQty = 0m;

        // Both unitless counts (e.g., "2 eggs" split into "1 egg" + "1 egg")
        if (
            targetUnit is null
            && targetCustomUnit is null
            && stepUnit is null
            && stepCustomUnit is null
        )
        {
            convertedQty = stepQty;
            return true;
        }

        if (
            targetUnit.HasValue
            && stepUnit.HasValue
            && targetUnit != MeasurementUnit.Custom
            && stepUnit != MeasurementUnit.Custom
        )
        {
            if (targetUnit == stepUnit)
            {
                convertedQty = stepQty;
                return true;
            }

            return CulinaryMath.TryConvertQuantity(
                stepQty,
                stepUnit.Value,
                targetUnit.Value,
                out convertedQty
            );
        }

        if (
            !string.IsNullOrWhiteSpace(targetCustomUnit)
            && string.Equals(targetCustomUnit, stepCustomUnit, StringComparison.OrdinalIgnoreCase)
        )
        {
            convertedQty = stepQty;
            return true;
        }

        return false;
    }

    private static void CollectUnreferencedIngredientWarnings(
        IReadOnlyList<ExtractedIngredientDto> ingredients,
        HashSet<string> referencedIds,
        List<string> warnings
    )
    {
        foreach (var ing in ingredients)
        {
            var refId = ing.RefId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(refId) || referencedIds.Contains(refId))
            {
                continue;
            }

            if (IsBenignUnreferencedIngredient(ing))
            {
                continue;
            }

            var display = !string.IsNullOrWhiteSpace(ing.DisplayName)
                ? ing.DisplayName
                : ing.CanonicalFoodItem;
            warnings.Add($"Ingredient '{display}' ({refId}) is not referenced in any step.");
        }
    }

    private static bool IsBenignUnreferencedIngredient(ExtractedIngredientDto ing)
    {
        if (ing.IsOptional || ing.IsRecipeReference)
        {
            return true;
        }

        if (
            (
                !string.IsNullOrWhiteSpace(ing.PreparationNotes)
                && GarnishOrOptionalPhraseRegex().IsMatch(ing.PreparationNotes)
            )
            || (
                !string.IsNullOrWhiteSpace(ing.RawText)
                && GarnishOrOptionalPhraseRegex().IsMatch(ing.RawText)
            )
        )
        {
            return true;
        }

        return !ing.Quantity.HasValue
            && !string.IsNullOrWhiteSpace(ing.CanonicalFoodItem)
            && BasicSeasoningsAndWater.Contains(ing.CanonicalFoodItem.Trim());
    }
}

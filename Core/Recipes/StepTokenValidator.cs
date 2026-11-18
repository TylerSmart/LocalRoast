using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using App.Core.Ingestion;

namespace App.Core.Recipes;

public static partial class StepTokenValidator
{
    private static readonly HashSet<string> PluralUnitNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "drops",
        "smidgens",
        "pinches",
        "dashes",
        "teaspoons",
        "tablespoons",
        "fluid ounces",
        "cups",
        "pints",
        "quarts",
        "gallons",
        "milliliters",
        "centiliters",
        "deciliters",
        "liters",
        "ounces",
        "pounds",
        "milligrams",
        "grams",
        "kilograms",
        "inches",
        "feet",
        "yards",
        "millimeters",
        "centimeters",
        "meters",
        "seconds",
        "minutes",
        "hours",
        "days",
        "cloves",
        "cans",
        "slices",
        "bunches",
        "sticks",
        "sprigs",
        "leaves",
    };

    [GeneratedRegex(
        @"\b\d+(?:\s+\d+/\d+|/\d+|\.\d+)?\s*(?:teaspoons?|tsp\.?|tablespoons?|tbsp\.?|cups?|pints?|quarts?|gallons?|fl(?:uid)?\s+oz\.?|ounces?|oz\.?|pounds?|lbs?\.?|grams?|g\b|kilograms?|kg|milliliters?|ml|liters?|minutes?|mins?|seconds?|secs?|hours?|hrs?|degrees?|°\s*[FC]|deg\s*[FC]|inch(?:es)?|cm|mm)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex UntokenizedQuantityRegex();

    [GeneratedRegex(@"[a-zA-Z]{4,}", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    [GeneratedRegex(
        @"^\s*(?:to\s+)?(?:seconds?|minutes?|hours?|days?|weeks?|months?|years?|secs?|mins?|hrs?|wks?|mos?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex DuplicateTimeUnitPrefixRegex();

    [GeneratedRegex(@"^\s*\d+\.\s+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadStepNumberRegex();

    [GeneratedRegex(@"\b(?:each|both)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DistributiveQuantifierRegex();

    [GeneratedRegex(
        @"\b(?:between\s+)?(?:\d+(?:\s+\d+/\d+|\.\d+|/\d+)?|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|twenty|thirty|forty|fifty|sixty|half|halves|third|thirds|quarter|quarters|dozen|couple)(?:\s*-?\s*(?:to|or|and|–|-)\s*(?:\d+(?:\s+\d+/\d+|\.\d+|/\d+)?|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|twenty|thirty|forty|fifty|sixty|half|halves|third|thirds|quarter|quarters|dozen|couple))?\b|\b(?:a|an)\s+(?:[a-z]+\s+)?(?:pinch|dash|smidgen|drop|handful|splash|drizzle|second|minute|hour|day|inch|foot|cup|clove|can|sprig|slice|sheet|bunch|leaf|stalk|stick)\b|\b(?:pinch|dash|smidgen)(?:\s+of)?\s+(?!the\b|it\b|them\b|edges?\b|sides?\b|corners?\b|ends?\b|dough\b|crust\b|pleats?\b|seams?\b)[a-z]{3,}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex ExplicitQuantityCueRegex();

    [GeneratedRegex(@"[a-zA-Z]{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex NormalizedWordRegex();

    [GeneratedRegex(@"^\s+([a-zA-Z]{2,}(?:\s+[a-zA-Z]{2,}){0,3})", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingWordsPrefixRegex();

    [GeneratedRegex(
        @"\b\d+(?:\s+\d+/\d+|\.\d+|/\d+)?\s+by\s+\d+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex MultiAxisByDimensionRegex();

    [GeneratedRegex(@"\bby\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WordByRegex();

    [GeneratedRegex(
        @"\b(one|two|three|four|five|six|seven|eight|nine|ten|half)\s+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex TrailingSpelledNumberRegex();

    [GeneratedRegex(
        @"\b(?:rotate[sd]?|rotating|turn(?:s|ed|ing)?)\b[^.\n]{0,80}(?:90|180)\s+degrees?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex RotationAngleRegex();

    [GeneratedRegex(
        @"\b(?<noun>[a-zA-Z]{3,})\s+(?<prep>in|into|to|on|over)\s+(?:a|an|the)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex PrepositionalArticlePhraseRegex();

    private static readonly HashSet<string> NonCommaListWordsAfterToken = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "and",
        "or",
        "to",
        "in",
        "into",
        "on",
        "over",
        "for",
        "with",
        "until",
        "from",
        "of",
        "per",
        "total",
        "longer",
        "more",
        "each",
        "at",
        "before",
        "after",
        "between",
        "apart",
        "together",
        "evenly",
        "thick",
        "wide",
        "long",
        "pieces",
        "slices",
        "portions",
    };

    private static readonly (
        string CanonicalUnit,
        Regex Pattern,
        Regex QuantifiedPattern
    )[] UnitMeasurementPatterns = BuildUnitMeasurementPatterns();

    private static (
        string CanonicalUnit,
        Regex Pattern,
        Regex QuantifiedPattern
    )[] BuildUnitMeasurementPatterns()
    {
        (string CanonicalUnit, string PatternBody)[] defs =
        [
            ("teaspoon", @"(?:teaspoons?|tsps?\.?)"),
            ("tablespoon", @"(?:tablespoons?|tbsps?\.?|tbs\.?)"),
            ("fluid ounce", @"(?:fl(?:uid)?\s+oz\.?|fluid\s+ounces?)"),
            ("cup", @"(?:cups?|c\.)"),
            ("pint", @"(?:pints?|pts?\.?)"),
            ("quart", @"(?:quarts?|qts?\.?)"),
            ("gallon", @"(?:gallons?|gals?\.?)"),
            ("ounce", @"(?:ounces?|ozs?\.?)"),
            ("pound", @"(?:pounds?|lbs?\.?)"),
            ("milligram", @"(?:milligrams?|mg)"),
            ("gram", @"(?:grams?|g)"),
            ("kilogram", @"(?:kilograms?|kgs?)"),
            ("milliliter", @"(?:milliliters?|millilitres?|ml)"),
            ("liter", @"(?:liters?|litres?)"),
            ("clove", @"cloves?"),
            ("can", @"cans?"),
            ("pinch", @"pinch(?:es)?"),
            ("dash", @"dash(?:es)?"),
            ("stick", @"sticks?"),
            ("sprig", @"sprigs?"),
            ("slice", @"slices?"),
            ("bunch", @"bunch(?:es)?"),
            ("head", @"heads?"),
        ];

        return defs.Select(d =>
                (
                    d.CanonicalUnit,
                    new Regex(
                        d.CanonicalUnit
                            is "cup"
                                or "liter"
                                or "clove"
                                or "can"
                                or "pinch"
                                or "dash"
                                or "stick"
                                or "sprig"
                                or "slice"
                                or "bunch"
                                or "head"
                            ? $@"\b{d.PatternBody}\b"
                            : $@"(?:\b|\d){d.PatternBody}\b",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                    ),
                    new Regex(
                        $@"\b\d+(?:\s+\d+/\d+|/\d+|\.\d+)?\s+{d.PatternBody}\b",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                    )
                )
            )
            .ToArray();
    }

    [GeneratedRegex(
        @"^\s*-?\s*(?:inch(?:es)?|feet|foot|yards?|millimeters?|centimeters?|meters?|mm\b|cm\b)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex DuplicateDimUnitPrefixRegex();

    [GeneratedRegex(
        @"^\s+-(?:wide|thick|long|deep|diameter|round|square)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex LeadingSpaceHyphenSuffixRegex();

    [GeneratedRegex(@"\bup to\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingUpToRegex();

    [GeneratedRegex(
        @"\b([a-zA-Z]{3,}(?:\s+[a-zA-Z]{2,}){0,3})\s+\1\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex RepeatedAdjacentWordRegex();

    [GeneratedRegex(
        @"-?\s*inch(?:es)?-(wide|thick|long|deep|diameter)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex DimensionQualifierSuffixRegex();

    [GeneratedRegex(
        @"^\s*(?:to|–|-)\s*(?<dupMax>\d+(?:\s+\d+/\d+|/\d+|\.\d+)?)\s*(?<uword>seconds?|minutes?|hours?|days?|secs?|mins?|hrs?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex DuplicateRangeMaxAfterTimerRegex();

    [GeneratedRegex(@"^\s*(?:to|–|-)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RangeConnectorOnlyRegex();

    [GeneratedRegex(
        @"(?<lead>(?:^|(?<=[\s(\[]))(?<dupMin>\d+(?:\s+\d+/\d+|/\d+|\.\d+)?)\s*(?:to|–|-)\s*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex LeadingDanglingRangeRegex();

    [GeneratedRegex(
        @"^\s+of\s+the\s*(?=[,.;:)\]])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex DanglingOfTheBeforePunctRegex();

    [GeneratedRegex(
        @"^\s+of\s+the(?=\s+(?:in|into|to|on|over|left|with|and|or|from|until|for|at|as|before|after|then)\b|\s*\(|\s*$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex DanglingOfTheBeforePrepRegex();

    [GeneratedRegex(@"^\s+of\s+the\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DanglingOfThePrefixRegex();

    [GeneratedRegex(
        @"\([^)]*\b(?:should\s+have|yields?)(?:\s+about)?\s*(?:of\b[^)]*)?\)|\(\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex BrokenYieldParenRegex();

    [GeneratedRegex(@"^\s+(?<w>[a-zA-Z]{3,})\b", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingThreeLetterWordRegex();

    [GeneratedRegex(@"\bfrom\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingFromWordRegex();

    [GeneratedRegex(
        @"^\s*(?:°\s*[FC]|degrees?(?:\s+(?:F|C|Fahrenheit|Celsius))?\b|fahrenheit\b|celsius\b|[FC]\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex DuplicateTempUnitPrefixRegex();

    public static JsonArray SanitizeRawTokens(JsonArray tokensArray)
    {
        static JsonArray MergeAdjacentStrings(JsonArray source)
        {
            var merged = new JsonArray();
            foreach (var node in source)
            {
                if (
                    node is JsonValue val
                    && val.TryGetValue<string>(out var s)
                    && merged.Count > 0
                    && merged[^1] is JsonValue prevVal
                    && prevVal.TryGetValue<string>(out var prevStr)
                )
                {
                    merged[^1] = JsonValue.Create(prevStr + s);
                }
                else
                {
                    merged.Add(node?.DeepClone());
                }
            }
            return merged;
        }

        var sanitized = MergeAdjacentStrings(tokensArray);

        // Collapse duplicated range upper-bound tokens: [tok(min..max), " to ", tok(max)] -> [tok(min..max)]
        for (var i = 0; i + 2 < sanitized.Count; i++)
        {
            if (
                sanitized[i] is not JsonObject t1
                || sanitized[i + 1] is not JsonValue midVal
                || !midVal.TryGetValue<string>(out var midStr)
                || !RangeConnectorOnlyRegex().IsMatch(midStr)
                || sanitized[i + 2] is not JsonObject t2
            )
            {
                continue;
            }

            var type1 = GetNullableString(t1["type"])?.Trim().ToLowerInvariant();
            var type2 = GetNullableString(t2["type"])?.Trim().ToLowerInvariant();
            if (type1 != type2)
            {
                continue;
            }

            var shouldCollapse = false;
            if (
                type1 == "timer"
                && GetNullableDecimal(t1["amount"]) is decimal min1
                && GetNullableDecimal(t1["maxAmount"]) is decimal max1
                && max1 > min1
                && GetNullableDecimal(t2["maxAmount"]) is null
                && GetNullableDecimal(t2["amount"]) is decimal amt2
                && amt2 > 0m
                && ConvertTimerToSeconds(
                    max1,
                    NormalizeTimerUnitWord(GetNullableString(t1["unit"]) ?? "minute")
                )
                    == ConvertTimerToSeconds(
                        amt2,
                        NormalizeTimerUnitWord(GetNullableString(t2["unit"]) ?? "minute")
                    )
            )
            {
                shouldCollapse = true;
            }
            else if (
                type1 == "temperature"
                && GetNullableDecimal(t1["amount"]) is decimal tMin1
                && GetNullableDecimal(t1["maxAmount"]) is decimal tMax1
                && tMax1 > tMin1
                && GetNullableDecimal(t2["maxAmount"]) is null
                && GetNullableDecimal(t2["amount"]) is decimal tAmt2
                && tMax1 == tAmt2
                && string.Equals(
                    GetNullableString(t1["unit"]) ?? "fahrenheit",
                    GetNullableString(t2["unit"]) ?? "fahrenheit",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                shouldCollapse = true;
            }
            else if (
                type1 == "dimension"
                && GetNullableDecimal(t1["amount"]) is decimal dMin1
                && GetNullableDecimal(t1["maxAmount"]) is decimal dMax1
                && dMax1 > dMin1
                && GetNullableDecimal(t2["maxAmount"]) is null
                && GetNullableDecimal(t2["amount"]) is decimal dAmt2
                && dMax1 == dAmt2
                && string.Equals(
                    GetNullableString(t1["unit"]) ?? "inch",
                    GetNullableString(t2["unit"]) ?? "inch",
                    StringComparison.OrdinalIgnoreCase
                )
                && (
                    !GetBool(t1["hyphenated"])
                    || GetBool(t1["hyphenated"]) == GetBool(t2["hyphenated"])
                )
            )
            {
                shouldCollapse = true;
            }

            if (shouldCollapse)
            {
                sanitized.RemoveAt(i + 2);
                sanitized.RemoveAt(i + 1);
            }
        }

        sanitized = MergeAdjacentStrings(sanitized);

        // Strip leading duplicated lower bound before a range token: "(10 to " + {timer(10..11m)} -> "(" + {timer(10..11m)}
        for (var i = 0; i + 1 < sanitized.Count; i++)
        {
            if (
                sanitized[i] is not JsonValue leadVal
                || !leadVal.TryGetValue<string>(out var prevText)
                || sanitized[i + 1] is not JsonObject nextTok
            )
            {
                continue;
            }

            var leadMatch = LeadingDanglingRangeRegex().Match(prevText);
            if (!leadMatch.Success)
            {
                continue;
            }

            var minVal = GetNullableDecimal(nextTok["amount"] ?? nextTok["quantity"]);
            var maxVal = GetNullableDecimal(nextTok["maxAmount"] ?? nextTok["maxQuantity"]);
            if (
                minVal.HasValue
                && maxVal.HasValue
                && maxVal.Value > minVal.Value
                && TryParseSimpleAmount(leadMatch.Groups["dupMin"].Value, out var parsedMin)
                && parsedMin == minVal.Value
            )
            {
                var leadGroup = leadMatch.Groups["lead"];
                sanitized[i] = JsonValue.Create(prevText[..leadGroup.Index]);
            }
        }

        for (var i = 0; i + 1 < sanitized.Count; i++)
        {
            if (
                sanitized[i] is not JsonObject tokObj
                || sanitized[i + 1] is not JsonValue textVal
                || !textVal.TryGetValue<string>(out var nextText)
            )
            {
                continue;
            }

            var tokType = tokObj["type"]?.GetValue<string>()?.Trim().ToLowerInvariant();
            if (tokType == "timer")
            {
                var unit = NormalizeTimerUnitWord(GetNullableString(tokObj["unit"]) ?? "minute");
                var maxA = GetNullableDecimal(tokObj["maxAmount"]);
                var minA = GetNullableDecimal(tokObj["amount"]);
                var hasRange = maxA.HasValue && minA.HasValue && maxA.Value > minA.Value;

                if (hasRange)
                {
                    var dupRangeMatch = DuplicateRangeMaxAfterTimerRegex().Match(nextText);
                    if (
                        dupRangeMatch.Success
                        && TryParseSimpleAmount(
                            dupRangeMatch.Groups["dupMax"].Value,
                            out var dupVal
                        )
                    )
                    {
                        var dupUnit = NormalizeTimerUnitWord(dupRangeMatch.Groups["uword"].Value);
                        if (
                            ConvertTimerToSeconds(dupVal, dupUnit)
                                == ConvertTimerToSeconds(maxA!.Value, unit)
                            || (
                                dupVal == maxA.Value
                                && TimerUnitMatchesWord(
                                    unit,
                                    dupRangeMatch.Groups["uword"].Value.Trim().ToLowerInvariant()
                                )
                            )
                        )
                        {
                            nextText = nextText[dupRangeMatch.Length..];
                            sanitized[i + 1] = JsonValue.Create(nextText);
                        }
                    }
                }

                var match = DuplicateTimeUnitPrefixRegex().Match(nextText);
                if (match.Success)
                {
                    var candidateWord = match.Value.Trim().ToLowerInvariant();
                    if (hasRange && candidateWord.StartsWith("to ", StringComparison.Ordinal))
                    {
                        candidateWord = candidateWord[3..].TrimStart();
                    }

                    if (TimerUnitMatchesWord(unit, candidateWord))
                    {
                        sanitized[i + 1] = JsonValue.Create(nextText[match.Length..]);
                    }
                }
            }
            else if (tokType == "temperature" && !nextText.TrimStart().StartsWith('-'))
            {
                var unit = (GetNullableString(tokObj["unit"]) ?? "fahrenheit")
                    .Trim()
                    .ToLowerInvariant();
                var match = DuplicateTempUnitPrefixRegex().Match(nextText);
                if (
                    match.Success
                    && TempUnitMatchesWord(unit, match.Value.Trim().ToLowerInvariant())
                )
                {
                    sanitized[i + 1] = JsonValue.Create(nextText[match.Length..]);
                }
            }
            else if (
                tokType is "ingredient" or "mixture" or "fixed"
                && !string.IsNullOrWhiteSpace(GetNullableString(tokObj["name"]))
            )
            {
                var punctMatch = DanglingOfTheBeforePunctRegex().Match(nextText);
                if (punctMatch.Success)
                {
                    sanitized[i + 1] = JsonValue.Create(nextText[punctMatch.Length..]);
                }
                else
                {
                    var prepMatch = DanglingOfTheBeforePrepRegex().Match(nextText);
                    if (prepMatch.Success)
                    {
                        var rest = nextText[prepMatch.Length..];
                        var normalizedRest = string.IsNullOrEmpty(rest)
                            ? string.Empty
                            : (rest.StartsWith(' ') ? " " + rest.TrimStart() : rest);
                        sanitized[i + 1] = JsonValue.Create(normalizedRest);
                    }
                }
            }
        }

        return sanitized;
    }

    public static ValidatedStage4StepResult BuildAndValidateStep(
        int stepNumber,
        string? sectionName,
        string originalInstruction,
        JsonArray rawTokensJson,
        int ingredientCount,
        string? extraParseError = null
    )
    {
        var sanitized = SanitizeRawTokens(rawTokensJson);
        if (
            originalInstruction.TrimEnd().EndsWith(".)", StringComparison.Ordinal)
            && !originalInstruction.TrimEnd().EndsWith(".).", StringComparison.Ordinal)
            && sanitized.Count > 0
            && sanitized[^1] is JsonValue lastTextVal
            && lastTextVal.TryGetValue<string>(out var lastTextStr)
        )
        {
            var trimmedEnd = lastTextStr.TrimEnd();
            var trailingWs = lastTextStr[trimmedEnd.Length..];
            if (trimmedEnd.EndsWith(".).", StringComparison.Ordinal))
            {
                sanitized[^1] = JsonValue.Create(trimmedEnd[..^3] + ".)" + trailingWs);
            }
            else if (trimmedEnd.EndsWith(").", StringComparison.Ordinal))
            {
                sanitized[^1] = JsonValue.Create(trimmedEnd[..^2] + ".)" + trailingWs);
            }
        }

        var parsedTokens = ParseRawTokens(sanitized, out var parseErrors);
        if (!string.IsNullOrWhiteSpace(extraParseError))
        {
            parseErrors.Add(extraParseError);
        }

        var warnings = ValidateStepTokens(
            originalInstruction,
            parsedTokens,
            ingredientCount,
            parseErrors
        );

        var (compiledInstruction, ingredientIndices, timers, ingredientAmounts) =
            CompileToStepInstruction(parsedTokens, ingredientCount);

        return new ValidatedStage4StepResult(
            stepNumber,
            sectionName,
            originalInstruction,
            sanitized,
            parsedTokens,
            compiledInstruction,
            ingredientIndices,
            timers,
            ingredientAmounts,
            warnings
        );
    }

    private static (
        string CompiledInstruction,
        List<int> IngredientIndices,
        List<RecipeStepTimerInput> Timers,
        List<RecipeStepIngredientAmountInput> IngredientAmounts
    ) CompileToStepInstruction(IReadOnlyList<Stage4ParsedToken> parsedTokens, int ingredientCount)
    {
        var sb = new StringBuilder();
        var ingredientIndices = new List<int>();
        var seenIndices = new HashSet<int>();
        var timers = new List<RecipeStepTimerInput>();
        var seenTimerKeys = new HashSet<(int Seconds, string Label)>();
        var ingredientAmountsMap = new Dictionary<int, RecipeMeasurement>();

        foreach (var token in parsedTokens)
        {
            switch (token)
            {
                case Stage4TextToken textToken:
                    sb.Append(textToken.Text);
                    break;

                case Stage4IngredientToken ing:
                {
                    var (unit, customUnit) = RecipeUnitAndSectionHelpers.NormalizeUnit(ing.Unit);
                    if (
                        unit == MeasurementUnit.Custom
                        && RecipeUnitAndSectionHelpers.IsRedundantSelfCustomUnit(
                            customUnit,
                            ing.Name
                        )
                    )
                    {
                        unit = null;
                        customUnit = null;
                    }
                    var (qty, maxQty) = RecipeUnitAndSectionHelpers.NormalizeQuantities(
                        ing.Quantity,
                        ing.MaxQuantity
                    );
                    var stepToken = new StepIngredientToken(
                        Ingredient: ing.Name.Trim(),
                        Amount: qty,
                        Unit: unit,
                        CustomUnitName: unit == MeasurementUnit.Custom ? customUnit : null,
                        MaxAmount: maxQty
                    );
                    sb.Append(StepInstructionParser.FormatToken(stepToken));

                    if (ing.IngredientIndex is int idx && idx >= 0 && idx < ingredientCount)
                    {
                        if (seenIndices.Add(idx))
                        {
                            ingredientIndices.Add(idx);
                        }

                        if (qty.HasValue && unit.HasValue && !ingredientAmountsMap.ContainsKey(idx))
                        {
                            ingredientAmountsMap[idx] = new RecipeMeasurement(
                                qty,
                                unit,
                                maxQty,
                                unit == MeasurementUnit.Custom ? customUnit : null
                            );
                        }
                    }
                    break;
                }

                case Stage4MixtureToken mix:
                {
                    var (unit, customUnit) = RecipeUnitAndSectionHelpers.NormalizeUnit(mix.Unit);
                    if (
                        unit == MeasurementUnit.Custom
                        && RecipeUnitAndSectionHelpers.IsRedundantSelfCustomUnit(
                            customUnit,
                            mix.Name
                        )
                    )
                    {
                        unit = null;
                        customUnit = null;
                    }
                    var (qty, maxQty) = RecipeUnitAndSectionHelpers.NormalizeQuantities(
                        mix.Quantity,
                        mix.MaxQuantity
                    );
                    var stepToken = new StepMixtureToken(
                        Name: mix.Name.Trim(),
                        Amount: qty,
                        Unit: unit,
                        CustomUnitName: unit == MeasurementUnit.Custom ? customUnit : null,
                        MaxAmount: maxQty
                    );
                    sb.Append(StepInstructionParser.FormatToken(stepToken));
                    break;
                }

                case Stage4FixedToken fx:
                {
                    var (unit, customUnit) = RecipeUnitAndSectionHelpers.NormalizeUnit(fx.Unit);
                    var (qty, maxQty) = RecipeUnitAndSectionHelpers.NormalizeQuantities(
                        fx.Amount,
                        fx.MaxAmount
                    );
                    var stepToken = new StepFixedToken(
                        Amount: qty,
                        Unit: unit,
                        CustomUnitName: unit == MeasurementUnit.Custom ? customUnit : null,
                        MaxAmount: maxQty,
                        Name: string.IsNullOrWhiteSpace(fx.Name) ? null : fx.Name.Trim(),
                        Hyphenated: fx.Hyphenated
                    );
                    sb.Append(StepInstructionParser.FormatToken(stepToken));
                    break;
                }

                case Stage4DimensionToken dim:
                {
                    if (!DimensionUnitHelper.TryParse(dim.Unit, out var dimUnit))
                    {
                        dimUnit = DimensionUnit.Inch;
                    }
                    var (val, maxVal) = RecipeUnitAndSectionHelpers.NormalizeQuantities(
                        dim.Amount,
                        dim.MaxAmount
                    );
                    var stepToken = new StepDimensionToken(
                        Value: val ?? dim.Amount,
                        Unit: dimUnit,
                        MaxValue: maxVal,
                        Hyphenated: dim.Hyphenated
                    );
                    sb.Append(StepInstructionParser.FormatToken(stepToken));
                    break;
                }

                case Stage4TimerToken timer:
                {
                    var seconds = ConvertTimerToSeconds(timer.Amount, timer.Unit);
                    int? maxSeconds =
                        timer.MaxAmount.HasValue && timer.MaxAmount.Value > timer.Amount
                            ? ConvertTimerToSeconds(timer.MaxAmount.Value, timer.Unit)
                            : null;
                    var cleanLabel = string.IsNullOrWhiteSpace(timer.Label)
                        ? "Timer"
                        : timer.Label.Trim();
                    if (cleanLabel.Length > 150)
                    {
                        cleanLabel = cleanLabel[..150].Trim();
                    }

                    var tokenLabel = StepInstructionParser.IsDurationLabel(cleanLabel)
                        ? null
                        : cleanLabel;
                    var stepToken = new StepTimerToken(seconds, tokenLabel, maxSeconds);
                    sb.Append(StepInstructionParser.FormatToken(stepToken));

                    if (seenTimerKeys.Add((seconds, cleanLabel.ToLowerInvariant())))
                    {
                        timers.Add(new RecipeStepTimerInput(seconds, cleanLabel));
                    }
                    break;
                }

                case Stage4TemperatureToken temp:
                {
                    var isCelsius = string.Equals(
                        temp.Unit?.Trim(),
                        "celsius",
                        StringComparison.OrdinalIgnoreCase
                    );
                    var (val, maxVal) = RecipeUnitAndSectionHelpers.NormalizeQuantities(
                        temp.Amount,
                        temp.MaxAmount
                    );
                    var stepToken = isCelsius
                        ? new StepTemperatureToken(
                            Fahrenheit: null,
                            Celsius: val ?? temp.Amount,
                            MaxFahrenheit: null,
                            MaxCelsius: maxVal
                        )
                        : new StepTemperatureToken(
                            Fahrenheit: val ?? temp.Amount,
                            Celsius: null,
                            MaxFahrenheit: maxVal,
                            MaxCelsius: null
                        );
                    sb.Append(StepInstructionParser.FormatToken(stepToken));
                    break;
                }
            }
        }

        var compiledInstruction = sb.ToString().Trim();
        if (compiledInstruction.Length > 4000)
        {
            compiledInstruction = compiledInstruction[..4000];
        }

        var amountsList = ingredientAmountsMap
            .OrderBy(kvp => kvp.Key)
            .Select(kvp => new RecipeStepIngredientAmountInput(kvp.Key, kvp.Value))
            .ToList();

        return (compiledInstruction, ingredientIndices, timers, amountsList);
    }

    private static int ConvertTimerToSeconds(decimal amount, string? unit)
    {
        var multiplier = unit?.Trim().ToLowerInvariant() switch
        {
            "second" or "seconds" => 1m,
            "hour" or "hours" => 3600m,
            "day" or "days" => 86400m,
            _ => 60m,
        };
        var raw = (int)Math.Round(amount * multiplier, MidpointRounding.AwayFromZero);
        return Math.Clamp(raw, 1, 864000);
    }

    public static List<Stage4ParsedToken> ParseRawTokens(
        JsonArray tokensArray,
        out List<string> parseErrors
    )
    {
        parseErrors = [];
        var result = new List<Stage4ParsedToken>(tokensArray.Count);

        for (var i = 0; i < tokensArray.Count; i++)
        {
            var node = tokensArray[i];
            if (node is null)
            {
                parseErrors.Add($"Token[{i}] was null.");
                continue;
            }

            if (node is JsonValue val && val.TryGetValue<string>(out var text))
            {
                if (result.Count > 0 && result[^1] is Stage4TextToken prevTextTok)
                {
                    result[^1] = new Stage4TextToken(prevTextTok.Text + text);
                }
                else
                {
                    result.Add(new Stage4TextToken(text));
                }
                continue;
            }

            if (node is not JsonObject obj)
            {
                parseErrors.Add($"Token[{i}] was unexpected JSON node type {node.GetType().Name}.");
                continue;
            }

            var type = obj["type"]?.GetValue<string>()?.Trim().ToLowerInvariant();
            switch (type)
            {
                case "ingredient":
                    result.Add(
                        new Stage4IngredientToken(
                            GetNullableInt(obj["ingredientIndex"]),
                            obj["name"]?.GetValue<string>() ?? string.Empty,
                            GetNullableDecimal(obj["quantity"]),
                            GetNullableDecimal(obj["maxQuantity"]),
                            GetNullableString(obj["unit"])
                        )
                    );
                    break;
                case "mixture":
                    result.Add(
                        new Stage4MixtureToken(
                            obj["name"]?.GetValue<string>() ?? string.Empty,
                            GetNullableDecimal(obj["quantity"]) ?? 0m,
                            GetNullableDecimal(obj["maxQuantity"]),
                            GetNullableString(obj["unit"])
                        )
                    );
                    break;
                case "timer":
                    result.Add(
                        new Stage4TimerToken(
                            GetNullableDecimal(obj["amount"]) ?? 0m,
                            GetNullableDecimal(obj["maxAmount"]),
                            GetNullableString(obj["unit"]) ?? "minute",
                            GetNullableString(obj["label"]) ?? string.Empty
                        )
                    );
                    break;
                case "temperature":
                    result.Add(
                        new Stage4TemperatureToken(
                            GetNullableDecimal(obj["amount"]) ?? 0m,
                            GetNullableDecimal(obj["maxAmount"]),
                            GetNullableString(obj["unit"]) ?? "fahrenheit"
                        )
                    );
                    break;
                case "dimension":
                    result.Add(
                        new Stage4DimensionToken(
                            GetNullableDecimal(obj["amount"]) ?? 0m,
                            GetNullableDecimal(obj["maxAmount"]),
                            GetNullableString(obj["unit"]) ?? "inch",
                            GetBool(obj["hyphenated"])
                        )
                    );
                    break;
                case "fixed":
                    result.Add(
                        new Stage4FixedToken(
                            GetNullableString(obj["name"]) ?? string.Empty,
                            GetNullableDecimal(obj["amount"]) ?? 0m,
                            GetNullableDecimal(obj["maxAmount"]),
                            GetNullableString(obj["unit"]),
                            GetBool(obj["hyphenated"])
                        )
                    );
                    break;
                default:
                    parseErrors.Add($"Token[{i}] has unknown type '{type}'.");
                    break;
            }
        }

        return result;
    }

    public static string RenderForValidation(
        IReadOnlyList<Stage4ParsedToken> tokens,
        decimal scaleFactor = 1m,
        bool convertToMetric = false
    )
    {
        var sb = new StringBuilder();
        foreach (var token in tokens)
        {
            switch (token)
            {
                case Stage4TextToken t:
                    sb.Append(t.Text);
                    break;
                case Stage4IngredientToken ing:
                    sb.Append(
                        RenderAmountAndUnit(
                            ing.Quantity.HasValue ? ing.Quantity.Value * scaleFactor : null,
                            ing.MaxQuantity.HasValue ? ing.MaxQuantity.Value * scaleFactor : null,
                            ing.Unit,
                            convertToMetric,
                            hyphenated: false,
                            suffix: ing.Name
                        )
                    );
                    break;
                case Stage4MixtureToken mix:
                    sb.Append(
                        RenderAmountAndUnit(
                            mix.Quantity * scaleFactor,
                            mix.MaxQuantity.HasValue ? mix.MaxQuantity.Value * scaleFactor : null,
                            mix.Unit,
                            convertToMetric,
                            hyphenated: false,
                            suffix: mix.Name
                        )
                    );
                    break;
                case Stage4TimerToken timer:
                    sb.Append(
                        RenderAmountAndUnit(
                            timer.Amount,
                            timer.MaxAmount,
                            timer.Unit,
                            convertToMetric: false,
                            hyphenated: false,
                            suffix: null
                        )
                    );
                    break;
                case Stage4TemperatureToken temp:
                    sb.Append(RenderTemperature(temp, convertToMetric));
                    break;
                case Stage4DimensionToken dim:
                    sb.Append(
                        RenderAmountAndUnit(
                            dim.Amount,
                            dim.MaxAmount,
                            dim.Unit,
                            convertToMetric,
                            dim.Hyphenated,
                            suffix: null
                        )
                    );
                    break;
                case Stage4FixedToken fx:
                    var renderedMeasure = RenderAmountAndUnit(
                        fx.Amount,
                        fx.MaxAmount,
                        fx.Unit,
                        convertToMetric,
                        fx.Hyphenated,
                        suffix: null
                    );
                    sb.Append(
                        string.IsNullOrWhiteSpace(fx.Name)
                            ? renderedMeasure
                            : $"{renderedMeasure} {fx.Name}"
                    );
                    break;
            }
        }

        return sb.ToString();
    }

    public static List<string> ValidateStepTokens(
        string originalInstruction,
        IReadOnlyList<Stage4ParsedToken> tokens,
        int ingredientCount,
        IReadOnlyList<string> parseErrors
    )
    {
        var warnings = new List<string>(parseErrors);
        if (tokens.Count == 0)
        {
            warnings.Add("Tokens array is empty.");
            return warnings;
        }

        var rendered = RenderForValidation(tokens, scaleFactor: 1m, convertToMetric: false);
        if (string.IsNullOrWhiteSpace(rendered))
        {
            warnings.Add("Rendered step text is empty.");
            return warnings;
        }

        var origWords = WordRegex()
            .Matches(originalInstruction)
            .Select(m => m.Value.ToLowerInvariant())
            .Where(w => !IsUnitOrNumberWord(w))
            .ToHashSet();
        var renderedWords = WordRegex()
            .Matches(rendered)
            .Select(m => m.Value.ToLowerInvariant())
            .ToHashSet();

        var missingWords = origWords
            .Where(w => !renderedWords.Contains(w) && !IsTempOrTimeTextWord(w))
            .Take(8)
            .ToList();
        if (missingWords.Count >= 3)
        {
            warnings.Add(
                $"Possible dropped prose words ({missingWords.Count}): {string.Join(", ", missingWords)}"
            );
        }

        var origTrimmed = originalInstruction.TrimEnd();
        var rendTrimmed = rendered.TrimEnd();
        if (
            origTrimmed.EndsWith(".)", StringComparison.Ordinal)
            && !rendTrimmed.EndsWith(".)", StringComparison.Ordinal)
        )
        {
            warnings.Add(
                "Rendered step swapped or dropped trailing '.)' closing punctuation (source ends with '.)', not ').')."
            );
        }
        else if (origTrimmed.EndsWith('.') && !rendTrimmed.EndsWith('.'))
        {
            warnings.Add("Rendered step dropped trailing period '.'.");
        }

        if (
            BrokenYieldParenRegex().IsMatch(rendered)
            && !BrokenYieldParenRegex().IsMatch(originalInstruction)
        )
        {
            warnings.Add(
                "Rendered step has broken or empty parenthetical yield phrase (e.g., '(you should have )' or '(about of juice)'); emit type='fixed' with the measurement and unit inside the parentheses."
            );
        }

        var hasUntokenizedInText = tokens
            .OfType<Stage4TextToken>()
            .Any(txt =>
                UntokenizedQuantityRegex()
                    .Matches(txt.Text)
                    .Cast<Match>()
                    .Any(untok =>
                        !RotationAngleRegex()
                            .Matches(txt.Text)
                            .Cast<Match>()
                            .Any(rot =>
                                untok.Index >= rot.Index
                                && untok.Index + untok.Length <= rot.Index + rot.Length
                            )
                    )
            );

        var origClean = LeadStepNumberRegex().Replace(originalInstruction, string.Empty);
        var hasDistributive = DistributiveQuantifierRegex().IsMatch(origClean);
        var origCueCount = ExplicitQuantityCueRegex().Matches(origClean).Count;
        var quantifiedTokenCount = tokens.Count(t =>
            t
                is Stage4IngredientToken
                    or Stage4MixtureToken
                    or Stage4FixedToken
                    or Stage4TimerToken
                    or Stage4DimensionToken
                    or Stage4TemperatureToken
        );
        if (!hasDistributive && quantifiedTokenCount > origCueCount)
        {
            var suspectOneTokens = tokens
                .OfType<Stage4IngredientToken>()
                .Where(ing => ing.Quantity == 1m)
                .Select(ing => $"'{ing.Name}' (qty=1, unit={ing.Unit ?? "null"})")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList();
            var suspectHint =
                suspectOneTokens.Count > 0
                    ? $" Suspect unmeasured ingredient tokens: {string.Join(", ", suspectOneTokens)}."
                    : string.Empty;
            warnings.Add(
                $"Invented unprinted quantities: step emitted {quantifiedTokenCount} quantitative tokens but original instruction only contains {origCueCount} explicit numeric cues; leave unquantified mentions as plain text without pulling numbers from the ingredient list.{suspectHint}"
            );
        }

        foreach (var (unitName, unitPattern, quantifiedUnitPattern) in UnitMeasurementPatterns)
        {
            var tokenUnitCount = tokens.Count(t =>
                t switch
                {
                    Stage4IngredientToken ing => string.Equals(
                        ing.Unit?.Trim(),
                        unitName,
                        StringComparison.OrdinalIgnoreCase
                    ),
                    Stage4MixtureToken mix => string.Equals(
                        mix.Unit?.Trim(),
                        unitName,
                        StringComparison.OrdinalIgnoreCase
                    ),
                    Stage4FixedToken fx => string.Equals(
                        fx.Unit?.Trim(),
                        unitName,
                        StringComparison.OrdinalIgnoreCase
                    ),
                    _ => false,
                }
            );
            if (tokenUnitCount == 0)
            {
                continue;
            }

            var origUnitCount = unitPattern.Matches(origClean).Count;
            if (tokenUnitCount > origUnitCount && (origUnitCount == 0 || !hasDistributive))
            {
                warnings.Add(
                    $"Invented unit '{unitName}' ({tokenUnitCount} token(s) vs {origUnitCount} in original instruction); unquantified mentions must stay plain text."
                );
            }

            if (!hasUntokenizedInText && !hasDistributive)
            {
                var quantifiedOrigUnitCount = quantifiedUnitPattern.Matches(origClean).Count;
                if (tokenUnitCount < quantifiedOrigUnitCount)
                {
                    warnings.Add(
                        $"Dropped '{unitName}' measurement ({tokenUnitCount} tokenized vs {quantifiedOrigUnitCount} printed in original instruction); emit every printed numeric measurement as its own token instead of dropping a quantity/unit."
                    );
                }
            }
        }

        var origNormWordCounts = CountNormalizedWords(originalInstruction);
        var renderedNormWordCounts = CountNormalizedWords(rendered);

        if (missingWords.Count < 3)
        {
            var multisetDeficitCount = 0;
            var multisetDeficitSamples = new List<string>();
            foreach (var kvp in origNormWordCounts)
            {
                if (kvp.Key.Length < 4 || IsExcludedContentDropWord(kvp.Key))
                {
                    continue;
                }

                var deficit = kvp.Value - renderedNormWordCounts.GetValueOrDefault(kvp.Key);
                if (deficit > 0)
                {
                    multisetDeficitCount += deficit;
                    if (multisetDeficitSamples.Count < 6)
                    {
                        multisetDeficitSamples.Add($"{kvp.Key} (-{deficit})");
                    }
                }
            }

            if (multisetDeficitCount >= 4)
            {
                warnings.Add(
                    $"Dropped prose clause/words from instruction ({string.Join(", ", multisetDeficitSamples)}); preserve every sentence and clause verbatim."
                );
            }
        }

        var checkedPrepPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match pm in PrepositionalArticlePhraseRegex().Matches(originalInstruction))
        {
            var noun = pm.Groups["noun"].Value;
            var prep = pm.Groups["prep"].Value.ToLowerInvariant();
            var nounSing = SimpleSingular(noun);
            if (IsExcludedContentDropWord(nounSing) || !checkedPrepPairs.Add($"{nounSing}|{prep}"))
            {
                continue;
            }

            var nounDeficit =
                origNormWordCounts.GetValueOrDefault(nounSing)
                > renderedNormWordCounts.GetValueOrDefault(nounSing);
            if (!nounDeficit)
            {
                continue;
            }

            var prepPattern = $@"\b{Regex.Escape(prep)}\b";
            var origPrepCount = Regex
                .Matches(
                    originalInstruction,
                    prepPattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                )
                .Count;
            var rendPrepCount = Regex
                .Matches(
                    rendered,
                    prepPattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                )
                .Count;
            if (origPrepCount > rendPrepCount)
            {
                warnings.Add(
                    $"Dropped phrase '{pm.Value}' (missing '{noun} {prep}'); keep '{pm.Value}' in plain text before the container/equipment token."
                );
                break;
            }
        }

        if (
            MultiAxisByDimensionRegex().IsMatch(originalInstruction)
            && WordByRegex().Matches(originalInstruction).Count
                > WordByRegex().Matches(rendered).Count
        )
        {
            warnings.Add(
                "Dropped 'by' between multi-axis dimensions ('A by B'); emit TWO separate dimension tokens separated by ' by ' instead of collapsing into a 'to' range."
            );
        }

        var originalCommaCount = originalInstruction.Count(c => c == ',');
        var renderedCommaCount = rendered.Count(c => c == ',');
        var hasDroppedComma = originalCommaCount > renderedCommaCount;

        for (var i = 0; i < tokens.Count; i++)
        {
            decimal? tokenPrimaryVal = tokens[i] switch
            {
                Stage4IngredientToken ing => ing.Quantity,
                Stage4MixtureToken mix => mix.Quantity,
                Stage4FixedToken fx => fx.Amount,
                Stage4TimerToken tm => tm.Amount,
                Stage4DimensionToken dm => dm.Amount,
                _ => null,
            };
            if (
                i > 0
                && tokens[i - 1] is Stage4TextToken prevSpelledTxt
                && tokenPrimaryVal.HasValue
                && TryMatchSpelledNumber(
                    prevSpelledTxt.Text,
                    tokenPrimaryVal.Value,
                    out var wordNum
                )
            )
            {
                warnings.Add(
                    $"Duplicate spelled number '{wordNum}' right before [{i}]; consume '{wordNum}' inside the token so output does not read '{wordNum} {tokenPrimaryVal.Value}'."
                );
            }
            if (
                i > 0
                && tokens[i - 1] is Stage4TextToken prevRangeTxt
                && tokens[i] is not Stage4TextToken
                && LeadingDanglingRangeRegex().IsMatch(prevRangeTxt.Text)
            )
            {
                warnings.Add(
                    $"Dangling range start '{LeadingDanglingRangeRegex().Match(prevRangeTxt.Text).Value.Trim()}' left in text right before [{i}] ({tokens[i].Type}); combine the lower and upper bounds inside ONE token."
                );
            }
            if (
                i + 2 < tokens.Count
                && tokens[i] is Stage4TimerToken or Stage4DimensionToken or Stage4TemperatureToken
                && tokens[i + 1] is Stage4TextToken midMismatchConn
                && RangeConnectorOnlyRegex().IsMatch(midMismatchConn.Text)
                && tokens[i + 2]
                    is Stage4TimerToken
                        or Stage4DimensionToken
                        or Stage4TemperatureToken
                && tokens[i].Type != tokens[i + 2].Type
            )
            {
                warnings.Add(
                    $"Unexpected range connector '{midMismatchConn.Text.Trim()}' between mismatched token types [{i}] ({tokens[i].Type}) and [{i + 2}] ({tokens[i + 2].Type}); combine a physical range into ONE token of the matching type."
                );
            }
            if (
                i + 1 < tokens.Count
                && tokens[i] is not Stage4TextToken
                && tokens[i + 1] is not Stage4TextToken
            )
            {
                warnings.Add(
                    $"Adjacent non-text tokens [{i}] ({tokens[i].Type}) and [{i + 1}] ({tokens[i + 1].Type}) with no separator string."
                );
            }
            else if (
                i + 1 < tokens.Count
                && tokens[i] is Stage4TextToken prevTxt
                && tokens[i + 1] is not Stage4TextToken
                && prevTxt.Text.Length > 0
                && char.IsLetterOrDigit(prevTxt.Text[^1])
            )
            {
                warnings.Add(
                    $"Missing space between text ending with '{prevTxt.Text[^1]}' and [{i + 1}] ({tokens[i + 1].Type})."
                );
            }
            else if (
                i + 1 < tokens.Count
                && tokens[i] is not Stage4TextToken
                && tokens[i + 1] is Stage4TextToken nextTxt
                && nextTxt.Text.Length > 0
                && char.IsLetterOrDigit(nextTxt.Text[0])
            )
            {
                warnings.Add(
                    $"Missing space between [{i}] ({tokens[i].Type}) and text starting with '{nextTxt.Text[0]}'."
                );
            }

            switch (tokens[i])
            {
                case Stage4TextToken txt:
                    var untokCount = 0;
                    foreach (Match untok in UntokenizedQuantityRegex().Matches(txt.Text))
                    {
                        var isRotationAngle = false;
                        foreach (Match rot in RotationAngleRegex().Matches(txt.Text))
                        {
                            if (
                                untok.Index >= rot.Index
                                && untok.Index + untok.Length <= rot.Index + rot.Length
                            )
                            {
                                isRotationAngle = true;
                                break;
                            }
                        }

                        if (!isRotationAngle)
                        {
                            warnings.Add(
                                $"Untokenized amount/unit '{untok.Value}' left in plain text segment (for parenthetical/informational yields like '(you should have 5 to 6 cups)' or '(about 2/3 cup of juice)', emit type='fixed' with name=null or the yielded noun)."
                            );
                            untokCount++;
                            if (untokCount >= 3)
                            {
                                break;
                            }
                        }
                    }
                    break;
                case Stage4IngredientToken ing:
                    if (
                        ing.IngredientIndex is < 0
                        || (
                            ing.IngredientIndex.HasValue
                            && ing.IngredientIndex.Value >= ingredientCount
                        )
                    )
                    {
                        warnings.Add(
                            $"Ingredient token '{ing.Name}' has out-of-range ingredientIndex={ing.IngredientIndex} (count={ingredientCount})."
                        );
                    }
                    if (!ing.Quantity.HasValue)
                    {
                        warnings.Add(
                            $"Ingredient token '{ing.Name}' has quantity=null; unquantified ingredient mentions should remain plain text."
                        );
                    }
                    else if (ing.Quantity.Value <= 0m)
                    {
                        warnings.Add(
                            $"Ingredient token '{ing.Name}' has invalid quantity={ing.Quantity.Value}; quantity must be > 0 (for 'up to X', set quantity=X and maxQuantity=null)."
                        );
                    }
                    if (
                        ing.Quantity.HasValue
                        && ing.MaxQuantity.HasValue
                        && ing.MaxQuantity.Value < ing.Quantity.Value
                    )
                    {
                        warnings.Add(
                            $"Ingredient token '{ing.Name}' has inverted range {ing.Quantity}..{ing.MaxQuantity}."
                        );
                    }
                    if (
                        i > 0
                        && tokens[i - 1] is Stage4TextToken prevIngTxt
                        && TrailingUpToRegex().IsMatch(prevIngTxt.Text)
                        && ing.Quantity.HasValue
                        && ing.MaxQuantity.HasValue
                        && ing.MaxQuantity.Value > ing.Quantity.Value
                    )
                    {
                        warnings.Add(
                            $"Preceding text ends with 'up to', so set quantity={ing.MaxQuantity.Value} and maxQuantity=null instead of fabricated range {ing.Quantity.Value}..{ing.MaxQuantity.Value}."
                        );
                    }
                    if (
                        !string.IsNullOrWhiteSpace(ing.Unit)
                        && PluralUnitNames.Contains(ing.Unit.Trim())
                    )
                    {
                        warnings.Add(
                            $"Ingredient token '{ing.Name}' used plural unit '{ing.Unit}' instead of singular."
                        );
                    }
                    CheckUnitRepeatedInName(ing.Unit, ing.Name, warnings);
                    CheckBoundaryWordDuplication(
                        i,
                        ing.Name,
                        tokens,
                        origNormWordCounts,
                        renderedNormWordCounts,
                        warnings
                    );
                    break;
                case Stage4MixtureToken mix:
                    if (mix.Quantity <= 0m)
                    {
                        warnings.Add(
                            $"Mixture token '{mix.Name}' has invalid quantity={mix.Quantity}; quantity must be > 0."
                        );
                    }
                    if (mix.MaxQuantity.HasValue && mix.MaxQuantity.Value < mix.Quantity)
                    {
                        warnings.Add(
                            $"Mixture token '{mix.Name}' has inverted range {mix.Quantity}..{mix.MaxQuantity}."
                        );
                    }
                    if (
                        (
                            string.IsNullOrWhiteSpace(mix.Unit)
                            || string.Equals(
                                mix.Unit.Trim(),
                                "unit",
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        && mix.Quantity == 1m
                    )
                    {
                        warnings.Add(
                            $"Mixture token '{mix.Name}' has unit='{mix.Unit ?? "null"}' and quantity=1; unmeasured mixture references should stay plain text."
                        );
                    }
                    if (
                        !string.IsNullOrWhiteSpace(mix.Unit)
                        && PluralUnitNames.Contains(mix.Unit.Trim())
                    )
                    {
                        warnings.Add(
                            $"Mixture token '{mix.Name}' used plural unit '{mix.Unit}' instead of singular."
                        );
                    }
                    CheckUnitRepeatedInName(mix.Unit, mix.Name, warnings);
                    CheckBoundaryWordDuplication(
                        i,
                        mix.Name,
                        tokens,
                        origNormWordCounts,
                        renderedNormWordCounts,
                        warnings
                    );
                    break;
                case Stage4FixedToken fx:
                    if (fx.Amount <= 0m)
                    {
                        warnings.Add(
                            $"Fixed token '{fx.Name}' has invalid amount={fx.Amount}; amount must be > 0."
                        );
                    }
                    if (fx.MaxAmount.HasValue && fx.MaxAmount.Value < fx.Amount)
                    {
                        warnings.Add(
                            $"Fixed token '{fx.Name}' has inverted range {fx.Amount}..{fx.MaxAmount} {fx.Unit}."
                        );
                    }
                    CheckUnitRepeatedInName(fx.Unit, fx.Name, warnings, allowMeasuringVessel: true);
                    CheckBoundaryWordDuplication(
                        i,
                        fx.Name,
                        tokens,
                        origNormWordCounts,
                        renderedNormWordCounts,
                        warnings
                    );
                    break;
                case Stage4TimerToken timer:
                    var expectedTimerPattern = GetTimerUnitPattern(timer.Unit);
                    if (
                        expectedTimerPattern is not null
                        && !Regex.IsMatch(
                            originalInstruction,
                            expectedTimerPattern,
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                        )
                    )
                    {
                        warnings.Add(
                            $"Timer token '{timer.Label}' used unit '{timer.Unit}', which does not appear in original instruction (use the printed time unit, or leave 'weeks'/'months' storage durations as plain text without a timer token)."
                        );
                    }
                    if (timer.Amount <= 0m)
                    {
                        warnings.Add(
                            $"Timer token '{timer.Label}' has invalid amount={timer.Amount}; amount must be > 0."
                        );
                    }
                    if (string.IsNullOrWhiteSpace(timer.Label))
                    {
                        warnings.Add($"Timer token [{i}] has an empty label.");
                    }
                    if (timer.MaxAmount.HasValue && timer.MaxAmount.Value < timer.Amount)
                    {
                        warnings.Add(
                            $"Timer token '{timer.Label}' has inverted range {timer.Amount}..{timer.MaxAmount} {timer.Unit}."
                        );
                    }
                    if (
                        i > 0
                        && tokens[i - 1] is Stage4TextToken prevTimerTxt
                        && TrailingUpToRegex().IsMatch(prevTimerTxt.Text)
                        && timer.MaxAmount.HasValue
                        && timer.MaxAmount.Value > timer.Amount
                    )
                    {
                        warnings.Add(
                            $"Preceding text ends with 'up to', so set amount={timer.MaxAmount.Value} and maxAmount=null instead of fabricated range {timer.Amount}..{timer.MaxAmount.Value}."
                        );
                    }
                    if (
                        i + 2 < tokens.Count
                        && tokens[i + 1] is Stage4TextToken midRangeTxt
                        && RangeConnectorOnlyRegex().IsMatch(midRangeTxt.Text)
                        && tokens[i + 2] is Stage4TimerToken nextTimer
                        && string.Equals(
                            SimpleSingular(NormalizeTimerUnitWord(timer.Unit)),
                            SimpleSingular(NormalizeTimerUnitWord(nextTimer.Unit)),
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        warnings.Add(
                            $"Split single-unit time range across two timer tokens [{i}] and [{i + 2}]; combine into ONE timer token (amount={timer.Amount}, maxAmount={nextTimer.MaxAmount ?? nextTimer.Amount}, unit='{timer.Unit}') and remove '{midRangeTxt.Text}' and [{i + 2}]."
                        );
                    }
                    if (
                        i + 1 < tokens.Count
                        && tokens[i + 1] is Stage4TextToken trailingTimerTxt
                        && DuplicateTimeUnitPrefixRegex().IsMatch(trailingTimerTxt.Text)
                    )
                    {
                        warnings.Add(
                            $"Duplicate or leftover time unit '{DuplicateTimeUnitPrefixRegex().Match(trailingTimerTxt.Text).Value.Trim()}' after timer token [{i}] (if 'to minutes'/'to hours', absorb into maxAmount and remove trailing fragment; if source says 'weeks'/'months', delete the timer token and merge into one plain text phrase without repeating 'weeks')."
                        );
                    }
                    break;
                case Stage4DimensionToken dim:
                    if (
                        !Regex.IsMatch(
                            originalInstruction,
                            $@"(?:^|[^a-zA-Z]){GetDimensionUnitPattern(dim.Unit)}",
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                        )
                    )
                    {
                        warnings.Add(
                            $"Dimension token [{i}] used length unit '{dim.Unit}', which does not appear in original instruction (for volume/weight container capacities like '1-quart pitcher' or '12-cup Bundt pan', use 'fixed' instead of 'dimension')."
                        );
                    }
                    if (dim.MaxAmount.HasValue && dim.MaxAmount.Value < dim.Amount)
                    {
                        warnings.Add(
                            $"Dimension token has inverted range {dim.Amount}..{dim.MaxAmount} {dim.Unit}."
                        );
                    }
                    if (
                        i > 0
                        && tokens[i - 1] is Stage4TextToken prevDimTxt
                        && TrailingUpToRegex().IsMatch(prevDimTxt.Text)
                        && dim.MaxAmount.HasValue
                        && dim.MaxAmount.Value > dim.Amount
                    )
                    {
                        warnings.Add(
                            $"Preceding text ends with 'up to', so set amount={dim.MaxAmount.Value} and maxAmount=null instead of fabricated range {dim.Amount}..{dim.MaxAmount.Value}."
                        );
                    }
                    if (dim.Hyphenated)
                    {
                        var checkVal = dim.MaxAmount ?? dim.Amount;
                        var isMetricDim =
                            dim.Unit.Trim().ToLowerInvariant()
                            is "millimeter"
                                or "centimeter"
                                or "meter";
                        var checkStr = FormatNum(checkVal, isMetricDim);
                        var unitPat = GetDimensionUnitPattern(dim.Unit);
                        var esc = Regex.Escape(checkStr);
                        if (
                            Regex.IsMatch(
                                originalInstruction,
                                $@"(?:^|\W){esc}\s+{unitPat}",
                                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                            )
                            && !Regex.IsMatch(
                                originalInstruction,
                                $@"(?:^|\W){esc}(?:\s+to\s+[^-]+)?-",
                                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                            )
                        )
                        {
                            warnings.Add(
                                $"Dimension token [{i}] set hyphenated=true, but source '{checkStr} {dim.Unit}' is unhyphenated; set hyphenated=false."
                            );
                        }
                    }
                    if (
                        i + 2 < tokens.Count
                        && tokens[i + 1] is Stage4TextToken midDimRangeTxt
                        && RangeConnectorOnlyRegex().IsMatch(midDimRangeTxt.Text)
                        && tokens[i + 2] is Stage4DimensionToken nextDim
                        && !(
                            i > 0
                            && tokens[i - 1] is Stage4TextToken prevFromDim
                            && TrailingFromWordRegex().IsMatch(prevFromDim.Text)
                        )
                        && string.Equals(dim.Unit, nextDim.Unit, StringComparison.OrdinalIgnoreCase)
                        && (nextDim.MaxAmount ?? nextDim.Amount) >= dim.Amount
                    )
                    {
                        warnings.Add(
                            $"Split single-unit dimension range across two dimension tokens [{i}] and [{i + 2}]; combine into ONE dimension token (amount={dim.Amount}, maxAmount={nextDim.MaxAmount ?? nextDim.Amount}, unit='{dim.Unit}', hyphenated={nextDim.Hyphenated.ToString().ToLowerInvariant()}) and remove '{midDimRangeTxt.Text}' and [{i + 2}]."
                        );
                    }
                    if (i + 1 < tokens.Count && tokens[i + 1] is Stage4TextToken trailingDimTxt)
                    {
                        if (DuplicateDimUnitPrefixRegex().IsMatch(trailingDimTxt.Text))
                        {
                            warnings.Add(
                                $"Duplicate dimension unit '{DuplicateDimUnitPrefixRegex().Match(trailingDimTxt.Text).Value.Trim()}' after dimension token [{i}] (hyphenated dimension already renders '-inch')."
                            );
                        }
                        else if (
                            dim.Hyphenated
                            && LeadingSpaceHyphenSuffixRegex().IsMatch(trailingDimTxt.Text)
                        )
                        {
                            warnings.Add(
                                $"Extra space before hyphenated dimension suffix '{trailingDimTxt.Text.TrimStart().Split(' ')[0]}' after dimension token [{i}]; attach '-wide'/'-thick' with no leading space."
                            );
                        }
                    }
                    break;
                case Stage4TemperatureToken temp:
                    if (temp.MaxAmount.HasValue && temp.MaxAmount.Value < temp.Amount)
                    {
                        warnings.Add(
                            $"Temperature token has inverted range {temp.Amount}..{temp.MaxAmount} {temp.Unit}."
                        );
                    }
                    if (
                        i + 2 < tokens.Count
                        && tokens[i + 1] is Stage4TextToken midTempRangeTxt
                        && RangeConnectorOnlyRegex().IsMatch(midTempRangeTxt.Text)
                        && tokens[i + 2] is Stage4TemperatureToken nextTemp
                        && !(
                            i > 0
                            && tokens[i - 1] is Stage4TextToken prevFromTemp
                            && TrailingFromWordRegex().IsMatch(prevFromTemp.Text)
                        )
                        && string.Equals(
                            NormalizeTempUnit(temp.Unit),
                            NormalizeTempUnit(nextTemp.Unit),
                            StringComparison.OrdinalIgnoreCase
                        )
                        && (nextTemp.MaxAmount ?? nextTemp.Amount) >= temp.Amount
                    )
                    {
                        warnings.Add(
                            $"Split single-unit temperature range across two temperature tokens [{i}] and [{i + 2}]; combine into ONE temperature token (amount={temp.Amount}, maxAmount={nextTemp.MaxAmount ?? nextTemp.Amount}, unit='{temp.Unit}') and remove '{midTempRangeTxt.Text}' and [{i + 2}]."
                        );
                    }
                    if (
                        i + 1 < tokens.Count
                        && tokens[i + 1] is Stage4TextToken trailingTempTxt
                        && DuplicateTempUnitPrefixRegex().IsMatch(trailingTempTxt.Text)
                    )
                    {
                        warnings.Add(
                            $"Duplicate temperature unit '{DuplicateTempUnitPrefixRegex().Match(trailingTempTxt.Text).Value.Trim()}' after temperature token [{i}]."
                        );
                    }
                    break;
            }

            if (tokens[i] is Stage4IngredientToken or Stage4MixtureToken or Stage4FixedToken)
            {
                var tokenName = tokens[i] switch
                {
                    Stage4IngredientToken ingTok => ingTok.Name,
                    Stage4MixtureToken mixTok => mixTok.Name,
                    Stage4FixedToken fxTok => fxTok.Name,
                    _ => null,
                };
                CheckTrailingDanglingOrComma(
                    i,
                    tokens[i].Type,
                    tokenName,
                    tokens,
                    hasDroppedComma,
                    warnings
                );
            }
        }

        foreach (Match dup in RepeatedAdjacentWordRegex().Matches(rendered))
        {
            var word = dup.Groups[1].Value;
            if (
                !word.Equals("that", StringComparison.OrdinalIgnoreCase)
                && !word.Equals("had", StringComparison.OrdinalIgnoreCase)
            )
            {
                warnings.Add(
                    $"Duplicate adjacent phrase '{dup.Value}' in rendered step; consume unit/noun cleanly inside token."
                );
            }
        }

        foreach (Match qual in DimensionQualifierSuffixRegex().Matches(originalInstruction))
        {
            var suffix = qual.Groups[1].Value;
            if (
                !Regex.IsMatch(
                    rendered,
                    $@"-{Regex.Escape(suffix)}\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                )
            )
            {
                warnings.Add(
                    $"Dropped dimension qualifier '-{suffix}' from rendered step; preserve '-{suffix}' right after the hyphenated dimension token."
                );
            }
        }

        return warnings;
    }

    private static void CheckTrailingDanglingOrComma(
        int index,
        string tokenType,
        string? tokenName,
        IReadOnlyList<Stage4ParsedToken> tokens,
        bool hasDroppedComma,
        List<string> warnings
    )
    {
        if (index + 1 >= tokens.Count || tokens[index + 1] is not Stage4TextToken nextTxt)
        {
            return;
        }

        if (
            !string.IsNullOrWhiteSpace(tokenName)
            && DanglingOfThePrefixRegex().IsMatch(nextTxt.Text)
        )
        {
            warnings.Add(
                $"Dangling 'of the' after {tokenType} token '{tokenName}'; absorb 'of the' cleanly inside the token phrase so output does not read '{tokenName} of the ...'."
            );
        }

        if (hasDroppedComma)
        {
            var match = LeadingThreeLetterWordRegex().Match(nextTxt.Text);
            if (match.Success && !NonCommaListWordsAfterToken.Contains(match.Groups["w"].Value))
            {
                warnings.Add(
                    $"Dropped comma after token [{index}] ('{tokenName}') before '{match.Groups["w"].Value}'; restore the comma from the original instruction so list items stay separated."
                );
            }
        }
    }

    private static string NormalizeTempUnit(string? unit)
    {
        var trimmed = unit?.Trim().ToLowerInvariant() ?? "fahrenheit";
        return trimmed is "c" or "celsius" ? "celsius" : "fahrenheit";
    }

    public static bool TryFindIncompatibleAlternateMeasurement(
        decimal? primaryQty,
        decimal? primaryMaxQty,
        string? primaryUnit,
        IReadOnlyList<Stage1AlternateMeasurementDto>? alternates,
        out string? reason
    )
    {
        reason = null;
        if (!primaryQty.HasValue || primaryQty.Value <= 0m || alternates is not { Count: > 0 })
        {
            return false;
        }

        foreach (var alt in alternates)
        {
            var rawAltUnit = alt.Unit?.Trim();
            if (
                !string.IsNullOrWhiteSpace(rawAltUnit)
                && (
                    Regex.IsMatch(
                        rawAltUnit,
                        @"\bper\s+",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                    ) || rawAltUnit.Equals("each", StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                reason =
                    $"Alternate measurement unit '{rawAltUnit}' is a per-unit rate or size grade, not a total equivalent measurement; put size/count grades like '(26 to 30 per pound)' in prepNote and set alternateMeasurements=[].";
                return true;
            }
        }

        if (!TryGetVolumeOrWeightFactor(primaryUnit, out var primaryDim, out var primaryFactor))
        {
            return false;
        }

        var minPrimaryBase = primaryQty.Value * primaryFactor;
        var maxPrimaryBase = (primaryMaxQty ?? primaryQty.Value) * primaryFactor;
        foreach (var alt in alternates)
        {
            if (
                !alt.Quantity.HasValue
                || alt.Quantity.Value <= 0m
                || !TryGetVolumeOrWeightFactor(alt.Unit, out var altDim, out var altFactor)
                || !string.Equals(altDim, primaryDim, StringComparison.Ordinal)
            )
            {
                continue;
            }

            var altBase = alt.Quantity.Value * altFactor;
            if (altBase < minPrimaryBase * 0.70m || altBase > maxPrimaryBase * 1.45m)
            {
                reason =
                    $"{alt.Quantity.Value} {alt.Unit} is not equivalent to {primaryQty.Value} {primaryUnit} (sum 'A plus B' ingredients into primary quantity instead of splitting into alternateMeasurements).";
                return true;
            }
        }

        return false;
    }

    private static bool TempUnitMatchesWord(string unit, string phrase)
    {
        var compact = phrase
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        if (compact is "degree" or "degrees")
        {
            return true;
        }

        return unit switch
        {
            "fahrenheit" => compact
                is "°f"
                    or "f"
                    or "fahrenheit"
                    or "degreef"
                    or "degreesf"
                    or "degreefahrenheit"
                    or "degreesfahrenheit",
            "celsius" => compact
                is "°c"
                    or "c"
                    or "celsius"
                    or "degreec"
                    or "degreesc"
                    or "degreecelsius"
                    or "degreescelsius",
            _ => false,
        };
    }

    private static bool TimerUnitMatchesWord(string unit, string word) =>
        unit switch
        {
            "second" or "seconds" => word is "second" or "seconds" or "sec" or "secs",
            "minute" or "minutes" => word is "minute" or "minutes" or "min" or "mins",
            "hour" or "hours" => word is "hour" or "hours" or "hr" or "hrs",
            "day" or "days" => word is "day" or "days",
            _ => string.Equals(unit, word, StringComparison.OrdinalIgnoreCase),
        };

    private static string RenderTemperature(Stage4TemperatureToken temp, bool convertToMetric)
    {
        var amount = temp.Amount;
        var max = temp.MaxAmount;
        var isCelsius = string.Equals(temp.Unit, "celsius", StringComparison.OrdinalIgnoreCase);

        if (convertToMetric && !isCelsius)
        {
            amount = Math.Round((amount - 32m) * 5m / 9m, 0, MidpointRounding.AwayFromZero);
            if (max.HasValue)
            {
                max = Math.Round((max.Value - 32m) * 5m / 9m, 0, MidpointRounding.AwayFromZero);
            }
            isCelsius = true;
        }

        var suffix = isCelsius ? "°C" : "°F";
        return max.HasValue && max.Value > amount
            ? $"{FormatNum(amount, isCelsius)}–{FormatNum(max.Value, isCelsius)}{suffix}"
            : $"{FormatNum(amount, isCelsius)}{suffix}";
    }

    private static string RenderAmountAndUnit(
        decimal? qty,
        decimal? maxQty,
        string? unit,
        bool convertToMetric,
        bool hyphenated,
        string? suffix
    )
    {
        if (!qty.HasValue)
        {
            return suffix ?? string.Empty;
        }

        var q = qty.Value;
        var m = maxQty;
        var u = unit?.Trim().ToLowerInvariant();
        var isMetricOut = false;

        if (convertToMetric && !string.IsNullOrEmpty(u))
        {
            (q, m, u, isMetricOut) = ConvertUnitToMetric(q, m, u);
        }
        else if (
            u
            is "milliliter"
                or "centiliter"
                or "deciliter"
                or "liter"
                or "milligram"
                or "gram"
                or "kilogram"
                or "millimeter"
                or "centimeter"
                or "meter"
        )
        {
            isMetricOut = true;
        }

        var qtyStr =
            m.HasValue && m.Value > q
                ? $"{FormatNum(q, isMetricOut)} to {FormatNum(m.Value, isMetricOut)}"
                : FormatNum(q, isMetricOut);

        var unitDisplay = FormatUnitDisplay(u, m ?? q, hyphenated);
        var combined =
            string.IsNullOrEmpty(unitDisplay) ? qtyStr
            : hyphenated ? $"{qtyStr}-{unitDisplay}"
            : $"{qtyStr} {unitDisplay}";

        return string.IsNullOrWhiteSpace(suffix) ? combined : $"{combined} {suffix}";
    }

    private static bool TryGetVolumeOrWeightFactor(
        string? rawUnit,
        out string dimension,
        out decimal baseFactor
    )
    {
        switch (rawUnit?.Trim().ToLowerInvariant())
        {
            case "drop" or "drops":
                dimension = "volume";
                baseFactor = 0.05m;
                return true;
            case "smidgen" or "smidgens":
                dimension = "volume";
                baseFactor = 0.15m;
                return true;
            case "pinch" or "pinches":
                dimension = "volume";
                baseFactor = 0.308m;
                return true;
            case "dash" or "dashes":
                dimension = "volume";
                baseFactor = 0.616m;
                return true;
            case "teaspoon" or "teaspoons":
                dimension = "volume";
                baseFactor = 4.92892m;
                return true;
            case "tablespoon" or "tablespoons":
                dimension = "volume";
                baseFactor = 14.7868m;
                return true;
            case "fluid ounce" or "fluid ounces":
                dimension = "volume";
                baseFactor = 29.5735m;
                return true;
            case "cup" or "cups":
                dimension = "volume";
                baseFactor = 236.588m;
                return true;
            case "pint" or "pints":
                dimension = "volume";
                baseFactor = 473.176m;
                return true;
            case "quart" or "quarts":
                dimension = "volume";
                baseFactor = 946.353m;
                return true;
            case "gallon" or "gallons":
                dimension = "volume";
                baseFactor = 3785.41m;
                return true;
            case "milliliter" or "milliliters":
                dimension = "volume";
                baseFactor = 1m;
                return true;
            case "centiliter" or "centiliters":
                dimension = "volume";
                baseFactor = 10m;
                return true;
            case "deciliter" or "deciliters":
                dimension = "volume";
                baseFactor = 100m;
                return true;
            case "liter" or "liters":
                dimension = "volume";
                baseFactor = 1000m;
                return true;
            case "milligram" or "milligrams":
                dimension = "weight";
                baseFactor = 0.001m;
                return true;
            case "gram" or "grams":
                dimension = "weight";
                baseFactor = 1m;
                return true;
            case "kilogram" or "kilograms":
                dimension = "weight";
                baseFactor = 1000m;
                return true;
            case "ounce" or "ounces":
                dimension = "weight";
                baseFactor = 28.3495m;
                return true;
            case "pound" or "pounds":
                dimension = "weight";
                baseFactor = 453.592m;
                return true;
            default:
                dimension = string.Empty;
                baseFactor = 0m;
                return false;
        }
    }

    private static (decimal Q, decimal? M, string Unit, bool IsMetric) ConvertUnitToMetric(
        decimal q,
        decimal? m,
        string u
    )
    {
        decimal? Scale(decimal factor) => m.HasValue ? m.Value * factor : null;

        switch (u)
        {
            case "teaspoon":
            case "tablespoon":
            case "fluid ounce":
            case "cup":
            case "pint":
            case "quart":
            case "gallon":
            case "ounce":
            case "pound":
                if (TryGetVolumeOrWeightFactor(u, out var dim, out var factor))
                {
                    return dim == "volume"
                        ? ConvertMl(q * factor, Scale(factor))
                        : ConvertGrams(q * factor, Scale(factor));
                }
                return (q, m, u, false);
            case "inch":
                var cm = q * 2.54m;
                if (cm < 1m)
                {
                    return (
                        Math.Round(q * 25.4m, 0, MidpointRounding.AwayFromZero),
                        m.HasValue
                            ? Math.Round(m.Value * 25.4m, 0, MidpointRounding.AwayFromZero)
                            : null,
                        "millimeter",
                        true
                    );
                }
                return (
                    Math.Round(cm, 1, MidpointRounding.AwayFromZero),
                    m.HasValue
                        ? Math.Round(m.Value * 2.54m, 1, MidpointRounding.AwayFromZero)
                        : null,
                    "centimeter",
                    true
                );
            case "foot":
                return (
                    Math.Round(q * 30.48m, 0, MidpointRounding.AwayFromZero),
                    m.HasValue
                        ? Math.Round(m.Value * 30.48m, 0, MidpointRounding.AwayFromZero)
                        : null,
                    "centimeter",
                    true
                );
            case "yard":
                return (
                    Math.Round(q * 0.9144m, 2, MidpointRounding.AwayFromZero),
                    m.HasValue
                        ? Math.Round(m.Value * 0.9144m, 2, MidpointRounding.AwayFromZero)
                        : null,
                    "meter",
                    true
                );
            default:
                return (q, m, u, false);
        }
    }

    private static (decimal Q, decimal? M, string Unit, bool IsMetric) ConvertMl(
        decimal ml,
        decimal? maxMl
    )
    {
        if (ml >= 1000m)
        {
            return (
                Math.Round(ml / 1000m, 2, MidpointRounding.AwayFromZero),
                maxMl.HasValue
                    ? Math.Round(maxMl.Value / 1000m, 2, MidpointRounding.AwayFromZero)
                    : null,
                "liter",
                true
            );
        }
        return (
            Math.Round(ml, ml >= 10m ? 0 : 1, MidpointRounding.AwayFromZero),
            maxMl.HasValue
                ? Math.Round(maxMl.Value, maxMl.Value >= 10m ? 0 : 1, MidpointRounding.AwayFromZero)
                : null,
            "milliliter",
            true
        );
    }

    private static (decimal Q, decimal? M, string Unit, bool IsMetric) ConvertGrams(
        decimal g,
        decimal? maxG
    )
    {
        if (g >= 1000m)
        {
            return (
                Math.Round(g / 1000m, 2, MidpointRounding.AwayFromZero),
                maxG.HasValue
                    ? Math.Round(maxG.Value / 1000m, 2, MidpointRounding.AwayFromZero)
                    : null,
                "kilogram",
                true
            );
        }
        return (
            Math.Round(g, g >= 10m ? 0 : 1, MidpointRounding.AwayFromZero),
            maxG.HasValue
                ? Math.Round(maxG.Value, maxG.Value >= 10m ? 0 : 1, MidpointRounding.AwayFromZero)
                : null,
            "gram",
            true
        );
    }

    private static string FormatNum(decimal val, bool isMetric)
    {
        if (isMetric)
        {
            return val >= 10m
                ? Math.Round(val, 0, MidpointRounding.AwayFromZero)
                    .ToString("0", CultureInfo.InvariantCulture)
                : val.ToString("0.##", CultureInfo.InvariantCulture);
        }

        return CulinaryMath.FormatNumber(val, isMetric: false);
    }

    private static string FormatUnitDisplay(string? unit, decimal effectiveQty, bool hyphenated)
    {
        if (string.IsNullOrWhiteSpace(unit))
        {
            return string.Empty;
        }

        if (hyphenated || effectiveQty <= 1.05m)
        {
            return unit switch
            {
                "millimeter" => "mm",
                "centimeter" => "cm",
                "milliliter" => "ml",
                "gram" => "g",
                "kilogram" => "kg",
                _ => unit,
            };
        }

        return unit switch
        {
            "inch" => "inches",
            "foot" => "feet",
            "pinch" => "pinches",
            "dash" => "dashes",
            "bunch" => "bunches",
            "box" => "boxes",
            "pouch" => "pouches",
            "leaf" => "leaves",
            "millimeter" => "mm",
            "centimeter" => "cm",
            "milliliter" => "ml",
            "gram" => "g",
            "kilogram" => "kg",
            _ => unit.EndsWith('s') ? unit : unit + "s",
        };
    }

    private static bool IsUnitOrNumberWord(string w) =>
        w
            is "teaspoon"
                or "teaspoons"
                or "tablespoon"
                or "tablespoons"
                or "cup"
                or "cups"
                or "ounce"
                or "ounces"
                or "pound"
                or "pounds"
                or "gram"
                or "grams"
                or "inch"
                or "inches"
                or "degree"
                or "degrees"
                or "fahrenheit"
                or "celsius"
                or "minute"
                or "minutes"
                or "second"
                or "seconds"
                or "hour"
                or "hours"
                or "between"
                or "about";

    private static bool IsTempOrTimeTextWord(string w) =>
        w is "half" or "quarter" or "third" or "one" or "two" or "three";

    private static int? GetNullableInt(JsonNode? node)
    {
        if (node is not JsonValue v)
        {
            return null;
        }
        if (v.TryGetValue<int>(out var i))
        {
            return i;
        }
        if (v.TryGetValue<long>(out var l))
        {
            return (int)l;
        }
        if (v.TryGetValue<decimal>(out var d))
        {
            return (int)d;
        }
        if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var parsed))
        {
            return parsed;
        }
        return null;
    }

    private static decimal? GetNullableDecimal(JsonNode? node)
    {
        if (node is not JsonValue v)
        {
            return null;
        }
        if (v.TryGetValue<decimal>(out var d))
        {
            return d;
        }
        if (v.TryGetValue<int>(out var i))
        {
            return i;
        }
        if (v.TryGetValue<long>(out var l))
        {
            return l;
        }
        if (v.TryGetValue<double>(out var dbl))
        {
            return (decimal)dbl;
        }
        if (v.TryGetValue<float>(out var flt))
        {
            return (decimal)flt;
        }
        if (
            v.TryGetValue<string>(out var s)
            && decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
        )
        {
            return parsed;
        }
        return null;
    }

    private static void CheckUnitRepeatedInName(
        string? unit,
        string name,
        List<string> warnings,
        bool allowMeasuringVessel = false
    )
    {
        var u = unit?.Trim();
        if (string.IsNullOrWhiteSpace(u) || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (
            allowMeasuringVessel
            && Regex.IsMatch(
                name,
                $@"\b(?:measuring|measure)\s+{Regex.Escape(u)}s?\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        )
        {
            return;
        }

        if (
            Regex.IsMatch(
                name,
                $@"\b{Regex.Escape(u)}(?:e?s)?\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        )
        {
            warnings.Add(
                $"Token '{name}' repeats unit '{u}' inside its name (renders '{u} {name}'); keep unit ONLY in `unit` or set unit=null."
            );
        }
    }

    private static void CheckBoundaryWordDuplication(
        int tokenIndex,
        string tokenName,
        IReadOnlyList<Stage4ParsedToken> tokens,
        Dictionary<string, int> origWordCounts,
        Dictionary<string, int> renderedWordCounts,
        List<string> warnings
    )
    {
        if (
            tokenIndex + 1 >= tokens.Count
            || tokens[tokenIndex + 1] is not Stage4TextToken trailingTxt
            || string.IsNullOrWhiteSpace(tokenName)
        )
        {
            return;
        }

        var prefixMatch = TrailingWordsPrefixRegex().Match(trailingTxt.Text);
        if (!prefixMatch.Success)
        {
            return;
        }

        var tokenWords = NormalizedWordRegex()
            .Matches(tokenName)
            .Select(m => SimpleSingular(m.Value))
            .ToList();
        if (tokenWords.Count == 0)
        {
            return;
        }

        var lastTokWord = tokenWords[^1];
        if (
            origWordCounts.GetValueOrDefault(lastTokWord)
            >= renderedWordCounts.GetValueOrDefault(lastTokWord)
        )
        {
            return;
        }

        foreach (Match wMatch in NormalizedWordRegex().Matches(prefixMatch.Groups[1].Value))
        {
            if (SimpleSingular(wMatch.Value) == lastTokWord)
            {
                warnings.Add(
                    $"Duplicate/split noun '{lastTokWord}' after token [{tokenIndex}] ('{tokenName}' followed by '{prefixMatch.Groups[1].Value}'); remove the trailing duplicate or absorb leading qualifier like 'more {lastTokWord}' INTO token.name (do not leave ' more {lastTokWord}' or ' of the {lastTokWord}' after the token)."
                );
                return;
            }
        }
    }

    private static Dictionary<string, int> CountNormalizedWords(string text)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in NormalizedWordRegex().Matches(text))
        {
            var s = SimpleSingular(m.Value);
            counts[s] = counts.GetValueOrDefault(s) + 1;
        }
        return counts;
    }

    private static string SimpleSingular(string word)
    {
        var lower = word.ToLowerInvariant();
        return lower switch
        {
            "leaves" => "leaf",
            "inches" => "inch",
            "feet" => "foot",
            _ when lower.EndsWith("ies", StringComparison.Ordinal) && lower.Length > 4 =>
                string.Concat(lower.AsSpan(0, lower.Length - 3), "y"),
            _ when lower.EndsWith('s')
                    && lower.Length > 3
                    && !lower.EndsWith("ss", StringComparison.Ordinal) => lower[..^1],
            _ => lower,
        };
    }

    private static bool TryMatchSpelledNumber(string prevText, decimal val, out string word)
    {
        word = string.Empty;
        var m = TrailingSpelledNumberRegex().Match(prevText);
        if (!m.Success)
        {
            return false;
        }

        word = m.Groups[1].Value;
        decimal? spelledVal = word.ToLowerInvariant() switch
        {
            "half" => 0.5m,
            "one" => 1m,
            "two" => 2m,
            "three" => 3m,
            "four" => 4m,
            "five" => 5m,
            "six" => 6m,
            "seven" => 7m,
            "eight" => 8m,
            "nine" => 9m,
            "ten" => 10m,
            _ => null,
        };
        return spelledVal.HasValue && spelledVal.Value == val;
    }

    private static string NormalizeTimerUnitWord(string unit) =>
        unit.Trim().ToLowerInvariant() switch
        {
            "sec" or "secs" or "second" or "seconds" => "second",
            "min" or "mins" or "minute" or "minutes" => "minute",
            "hr" or "hrs" or "hour" or "hours" => "hour",
            "day" or "days" => "day",
            _ => unit.Trim().ToLowerInvariant(),
        };

    private static bool TryParseSimpleAmount(string raw, out decimal value)
    {
        value = 0m;
        var s = raw.Trim();
        if (string.IsNullOrEmpty(s))
        {
            return false;
        }

        if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (
            parts.Length == 2
            && decimal.TryParse(
                parts[0],
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out var whole
            )
            && TryParseSimpleAmount(parts[1], out var frac)
        )
        {
            value = whole + frac;
            return true;
        }

        var slash = s.IndexOf('/');
        if (
            slash > 0
            && slash < s.Length - 1
            && decimal.TryParse(
                s[..slash],
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out var num
            )
            && decimal.TryParse(
                s[(slash + 1)..],
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out var den
            )
            && den != 0m
        )
        {
            value = num / den;
            return true;
        }

        return false;
    }

    private static string? GetTimerUnitPattern(string? unit) =>
        unit?.Trim().ToLowerInvariant() switch
        {
            "second" or "seconds" => @"\b(?:seconds?|secs?)\b",
            "minute" or "minutes" => @"\b(?:minutes?|mins?)\b",
            "hour" or "hours" => @"\b(?:hours?|hrs?|overnight)\b",
            "day" or "days" => @"\b(?:days?|overnight)\b",
            _ => null,
        };

    private static bool IsExcludedContentDropWord(string singularWord)
    {
        if (IsUnitOrNumberWord(singularWord) || IsTempOrTimeTextWord(singularWord))
        {
            return true;
        }

        return singularWord
            is "quart"
                or "pint"
                or "gallon"
                or "liter"
                or "milliliter"
                or "kilogram"
                or "pinch"
                or "dash"
                or "clove"
                or "slice"
                or "bunch"
                or "foot"
                or "yard"
                or "day"
                or "percent"
                or "four"
                or "five"
                or "six"
                or "seven"
                or "eight"
                or "nine"
                or "ten"
                or "twelve"
                or "large"
                or "medium"
                or "small"
                or "fresh"
                or "freshly"
                or "ground"
                or "kosher"
                or "table"
                or "unsalted"
                or "extra"
                or "virgin"
                or "whole"
                or "with"
                or "from"
                or "into"
                or "over"
                or "that"
                or "this";
    }

    private static string GetDimensionUnitPattern(string unit) =>
        unit.Trim().ToLowerInvariant() switch
        {
            "inch" => @"inch(?:es)?\b",
            "foot" => @"(?:feet|foot)\b",
            "yard" => @"yards?\b",
            "millimeter" => @"(?:millimeters?|millimetres?|mm)\b",
            "centimeter" => @"(?:centimeters?|centimetres?|cm)\b",
            "meter" => @"(?:meters?|metres?)\b",
            _ => Regex.Escape(unit.Trim()) + @"\b",
        };

    private static string? GetNullableString(JsonNode? node)
    {
        if (node is not JsonValue v || !v.TryGetValue<string>(out var s))
        {
            return null;
        }

        var trimmed = s.Trim();
        return
            trimmed.Length == 0
            || trimmed.Equals("null", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("n/a", StringComparison.OrdinalIgnoreCase)
            ? null
            : s;
    }

    private static bool GetBool(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}

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
        @"\b(?:between\s+)?(?:\d+(?:\s+\d+/\d+|\.\d+|/\d+)?|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|twenty|thirty|forty|fifty|sixty|half|halves|third|thirds|quarter|quarters|dozen|couple)(?:\s*-?\s*(?:to|or|and|–|-)\s*(?:\d+(?:\s+\d+/\d+|\.\d+|/\d+)?|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|twenty|thirty|forty|fifty|sixty|half|halves|third|thirds|quarter|quarters|dozen|couple))?\b|\b(?:a|an)\s+(?:[a-z]+\s+)?(?:pinch|dash|smidgen|drop|handful|splash|drizzle|second|minute|hour|day|inch|foot|cup|clove|can|sprig|slice|sheet|bunch|leaf|stalk|stick)\b",
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
        @"\b(?:rotate|rotated|rotating)\b[^.\n]{0,80}(?:90|180)\s+degrees?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex RotationAngleRegex();

    private static readonly (string CanonicalUnit, Regex Pattern)[] UnitMeasurementPatterns =
    [
        (
            "teaspoon",
            new Regex(
                @"(?:\b|\d)(?:teaspoons?|tsps?\.?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            "tablespoon",
            new Regex(
                @"(?:\b|\d)(?:tablespoons?|tbsps?\.?|tbs\.?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            "fluid ounce",
            new Regex(
                @"(?:\b|\d)fl(?:uid)?\s+oz\.?\b|\bfluid\s+ounces?\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            "cup",
            new Regex(@"\b(?:cups?|c\.)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        ),
        (
            "pint",
            new Regex(
                @"(?:\b|\d)(?:pints?|pts?\.?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            "quart",
            new Regex(
                @"(?:\b|\d)(?:quarts?|qts?\.?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            "gallon",
            new Regex(
                @"(?:\b|\d)(?:gallons?|gals?\.?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            "ounce",
            new Regex(
                @"(?:\b|\d)(?:ounces?|ozs?\.?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            "pound",
            new Regex(
                @"(?:\b|\d)(?:pounds?|lbs?\.?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            "milligram",
            new Regex(
                @"(?:\b|\d)(?:milligrams?|mg)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            "gram",
            new Regex(
                @"(?:\b|\d)(?:grams?|g)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            "kilogram",
            new Regex(
                @"(?:\b|\d)(?:kilograms?|kgs?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            "milliliter",
            new Regex(
                @"(?:\b|\d)(?:milliliters?|millilitres?|ml)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            "liter",
            new Regex(
                @"\b(?:liters?|litres?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            "clove",
            new Regex(@"\bcloves?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        ),
        ("can", new Regex(@"\bcans?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (
            "pinch",
            new Regex(@"\bpinch(?:es)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        ),
        (
            "dash",
            new Regex(@"\bdash(?:es)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        ),
        (
            "stick",
            new Regex(@"\bsticks?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        ),
        (
            "sprig",
            new Regex(@"\bsprigs?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        ),
        (
            "slice",
            new Regex(@"\bslices?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        ),
        (
            "bunch",
            new Regex(@"\bbunch(?:es)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        ),
        ("head", new Regex(@"\bheads?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
    ];

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
        @"^\s*(?:°\s*[FC]|degrees?(?:\s+(?:F|C|Fahrenheit|Celsius))?\b|fahrenheit\b|celsius\b|[FC]\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex DuplicateTempUnitPrefixRegex();

    public static JsonArray SanitizeRawTokens(JsonArray tokensArray)
    {
        var sanitized = (JsonArray)tokensArray.DeepClone();
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
                var unit = (GetNullableString(tokObj["unit"]) ?? "minute")
                    .Trim()
                    .ToLowerInvariant();
                var match = DuplicateTimeUnitPrefixRegex().Match(nextText);
                if (
                    match.Success
                    && TimerUnitMatchesWord(unit, match.Value.Trim().ToLowerInvariant())
                )
                {
                    sanitized[i + 1] = JsonValue.Create(nextText[match.Length..]);
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
                result.Add(new Stage4TextToken(text));
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
                            obj["name"]?.GetValue<string>() ?? string.Empty,
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
            warnings.Add(
                $"Invented unprinted quantities: step emitted {quantifiedTokenCount} quantitative tokens but original instruction only contains {origCueCount} explicit numeric cues; leave unquantified mentions as plain text without pulling numbers from the ingredient list."
            );
        }

        foreach (var (unitName, unitPattern) in UnitMeasurementPatterns)
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
        }

        var origNormWordCounts = CountNormalizedWords(originalInstruction);
        var renderedNormWordCounts = CountNormalizedWords(rendered);

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
                                $"Untokenized amount/unit '{untok.Value}' left in plain text segment."
                            );
                            break;
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
                    CheckUnitRepeatedInName(fx.Unit, fx.Name, warnings);
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

    public static bool TryFindIncompatibleAlternateMeasurement(
        decimal? primaryQty,
        decimal? primaryMaxQty,
        string? primaryUnit,
        IReadOnlyList<Stage1AlternateMeasurementDto>? alternates,
        out string? reason
    )
    {
        reason = null;
        if (
            !primaryQty.HasValue
            || primaryQty.Value <= 0m
            || alternates is not { Count: > 0 }
            || !TryGetVolumeOrWeightFactor(primaryUnit, out var primaryDim, out var primaryFactor)
        )
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

    private static void CheckUnitRepeatedInName(string? unit, string name, List<string> warnings)
    {
        var u = unit?.Trim();
        if (string.IsNullOrWhiteSpace(u) || string.IsNullOrWhiteSpace(name))
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

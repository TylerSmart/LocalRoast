using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using App.Core.Ingestion;

namespace App.Core.Recipes;

public static partial class StepInstructionParser
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    [GeneratedRegex(
        @"\|\|(?<json>\{.*?\})(?<punct>[\.,:;!\?]*)\|\|",
        RegexOptions.Singleline,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex TokenRegex();

    [GeneratedRegex(
        @"(?:(?:\b(?<qty>\d+(?:[\s-]+\d+/\d+|/\d+|\.\d+)?|[½¼¾⅓⅔⅛⅜⅝⅞])\s*(?<unit>teaspoons?|tsp\.?|tablespoons?|tbsp\.?|cups?|fl(?:uid)?\.?\s*oz\.?|pints?|pt\.?|quarts?|qt\.?|gallons?|gal\.?|ounces?|oz\.?|pounds?|lbs?\.?|grams?|g|kilograms?|kg|milliliters?|ml|liters?|l|pinches?|dashes?|cloves?|slices?|cans?|bunches?|heads?)?(?:\s+of)?)|(?:(?:\ba\s+)?\b(?<unit>pinch(?:es)?|dash(?:es)?|clove(?:s)?|slice(?:s)?|can(?:s)?|bunch(?:es)?|head(?:s)?)\s+of))\s*$",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex TrailingMeasurementRegex();

    [GeneratedRegex(
        @"(?:\b(?<qty>\d+(?:[\s-]+\d+/\d+|/\d+|\.\d+)?|[½¼¾⅓⅔⅛⅜⅝⅞])\s*(?<unit>teaspoons?|tsp\.?|tablespoons?|tbsp\.?|cups?|fl(?:uid)?\.?\s*oz\.?|pints?|pt\.?|quarts?|qt\.?|gallons?|gal\.?|ounces?|oz\.?|pounds?|lbs?\.?|grams?|g|kilograms?|kg|milliliters?|ml|liters?|l|pinches?|dashes?|cloves?|slices?|cans?|bunches?|heads?))\s+(?<mod>(?:(?:lightly|finely|coarsely|freshly|thinly|roughly)\s+)?(?:beaten|melted|minced|chopped|grated|shredded|crumbled|toasted|crushed|peeled|sifted|sliced|diced|halved|quartered|softened|warmed?|chilled|cooled|reserved|remaining)\s+)$",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex TrailingMeasurementBeforeModifierRegex();

    [GeneratedRegex(
        @"^\s*of\s+(?:the\s+)?$",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex OfTheConnectorRegex();

    [GeneratedRegex(
        @"(?<keep>\b(?:to|at))\s+at\s+$",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex DuplicateAtBeforeTempRegex();

    [GeneratedRegex(
        @"(?<!\|)(\{\s*""(?:type|ingredient|displayName|canonicalFoodName|name|amount|maxAmount|displayAmount|displayText|unit|ingredientId|ingredientIndex|seconds|maxSeconds|fahrenheit|celsius|maxFahrenheit|maxCelsius|value|maxValue|hyphenated)""\s*:[^}\r\n]*?\})(?!\|)",
        RegexOptions.Singleline,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex BareJsonTokenRegex();

    [GeneratedRegex(
        @"\|\|\s*(?<json>\{[^{}|\r\n]*\})\s*__(?:\|\|)?",
        RegexOptions.Singleline,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex UnderscoreClosedTokenRegex();

    [GeneratedRegex(
        @"\|\|\s*(?<json>\{[^{}|\r\n]*)\s*__(?:\|\|)?",
        RegexOptions.Singleline,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex UnbracedUnderscoreTokenRegex();

    [GeneratedRegex(
        @"\|\|\s*(?<inner>\{[^{}|\r\n]*\}(?:\s*,\s*\{[^{}|\r\n]*\})+)\s*\|\|",
        RegexOptions.Singleline,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex SplitJsonObjectsTokenRegex();

    [GeneratedRegex(
        @"\|\|\s*\{(?<body>[^{}|\r\n]+?)(?<closing>\})?(?<punct>[\.,:;!\?]*)\|\|",
        RegexOptions.Singleline,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex DelimitedBodyRegex();

    [GeneratedRegex(
        @"(?<prefix>(?:^|,)\s*)""(?<key>type|ingredient|displayName|canonicalFoodName|name|amount|maxAmount|unit|displayAmount|displayUnit|customUnitName|displayCustomUnitName|displayText|notes|ingredientId|ingredientIndex|seconds|maxSeconds|label|fahrenheit|celsius|maxFahrenheit|maxCelsius|value|maxValue|hyphenated|temperature)""\s*(?=[0-9""\-]|true\b|false\b|null\b)",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex MissingJsonColonRegex();

    [GeneratedRegex(
        @"(?<prefix>(?:^|,)\s*)""(?<key>type|ingredient|displayName|canonicalFoodName|name|unit|displayUnit|customUnitName|displayCustomUnitName|displayText|notes|label)""(?!(?:true|false|null)\s*(?:[,}]|$))(?=[A-Za-z/][^""]*""\s*(?:[,}]|$))",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex MissingJsonColonAndOpeningQuoteRegex();

    [GeneratedRegex(
        @"^\s*(?:about|approx\.?|approximately)?\s*(?:\d+(?:\.\d+)?|\d+\s*(?:to|[-\u2013\u2014])\s*\d+)\s*(?:seconds?|secs?|s|minutes?|mins?|m|hours?|hrs?|h)\s*$",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex DurationLabelRegex();

    [GeneratedRegex(
        @"^\s*(?:[-–—]\s*(?:degrees?(?:\s*(?:[FC]|Fahrenheit|Celsius))?|deg\.?(?:\s*(?:[FC]|Fahrenheit|Celsius))?)|°\s*(?:[FC]|Fahrenheit|Celsius)\b|degrees?(?:\s*(?:[FC]|Fahrenheit|Celsius))?|deg\.?(?:\s*(?:[FC]|Fahrenheit|Celsius))?)(?=$|[\s\.,:;!\?\)\]])",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex TrailingDegreeAfterTempRegex();

    [GeneratedRegex(
        @"^\s*\(\s*(?:(?:about|approx\.?|approximately)\s+)?(?<qty>\d+(?:[\s-]+\d+/\d+|/\d+|\.\d+)?|[½¼¾⅓⅔⅛⅜⅝⅞])\s*(?<unit>seconds?|secs?|minutes?|mins?|hours?|hrs?)\s*\)(?<rest>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex TrailingParentheticalDurationRegex();

    [GeneratedRegex(
        @"^\s*\(\s*(?:(?:about|approx\.?|approximately)\s+)?(?<qty>\d+(?:[\s-]+\d+/\d+|/\d+|\.\d+)?|[½¼¾⅓⅔⅛⅜⅝⅞])\s*(?<unit>teaspoons?|tsp\.?|tablespoons?|tbsp\.?|cups?|fl(?:uid)?\.?\s*oz\.?|pints?|pt\.?|quarts?|qt\.?|gallons?|gal\.?|ounces?|oz\.?|pounds?|lbs?\.?|grams?|g|kilograms?|kg|milliliters?|ml|liters?|l|pinches?|dashes?|cloves?|slices?|cans?|bunches?|heads?|blocks?|stalks?|sprigs?)\s*\)(?<rest>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex TrailingParentheticalMeasurementRegex();

    [GeneratedRegex(
        @"(?<qualifier>\b(?:remaining|reserved|extra|more|additional|grated|chopped|minced|diced|sliced|melted|cooled|chilled|shredded|crumbled|toasted|crushed|beaten|peeled|sifted))\s*$",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex TrailingQualifierWordRegex();

    [GeneratedRegex(
        @"^\s*(?:seconds?|secs?|minutes?|mins?|hours?|hrs?)(?=$|[\s\.,:;!\?\)\]])",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex TrailingTimeUnitAfterTimerRegex();

    [GeneratedRegex(
        @"^\s*(?<word>[\p{L}-]{3,})(?<rest>.*)$",
        RegexOptions.Singleline,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex LeadingWordAfterTokenRegex();

    public static bool IsDurationLabel(string? label) =>
        !string.IsNullOrWhiteSpace(label) && DurationLabelRegex().IsMatch(label.Trim());

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string NormalizeBareTokens(string? instruction)
    {
        if (string.IsNullOrEmpty(instruction))
        {
            return string.Empty;
        }

        return BareJsonTokenRegex().Replace(instruction, "||$1||");
    }

    public static string NormalizeMalformedTokenSyntax(string? instruction)
    {
        if (string.IsNullOrEmpty(instruction))
        {
            return string.Empty;
        }

        // 1. Repair tokens closed with "__" or "__||" instead of "||"
        var result = UnderscoreClosedTokenRegex().Replace(instruction, "||${json}||");
        result = UnbracedUnderscoreTokenRegex().Replace(result, "||${json}||");

        // 2. Wrap bare unbracketed JSON tokens before repairing inner JSON syntax
        result = BareJsonTokenRegex().Replace(result, "||$1||");

        // 3. Merge split JSON objects inside ||{...}, {...}|| into a single JSON object
        result = SplitJsonObjectsTokenRegex()
            .Replace(
                result,
                match =>
                {
                    var inner = Regex.Replace(
                        match.Groups["inner"].Value,
                        @"\}\s*,\s*\{",
                        ", ",
                        RegexOptions.None,
                        RegexTimeout
                    );
                    return $"||{inner}||";
                }
            );

        // 4. Repair missing colons after known keys and missing closing braces inside ||{...||
        result = DelimitedBodyRegex()
            .Replace(
                result,
                match =>
                {
                    var body = match.Groups["body"].Value;
                    var hasClosing = match.Groups["closing"].Success;
                    var punct = match.Groups["punct"].Value;

                    var fixedBody = MissingJsonColonAndOpeningQuoteRegex()
                        .Replace(body, "${prefix}\"${key}\": \"");
                    fixedBody = MissingJsonColonRegex().Replace(fixedBody, "${prefix}\"${key}\": ");

                    if (string.Equals(fixedBody, body, StringComparison.Ordinal) && hasClosing)
                    {
                        return match.Value;
                    }

                    return $"||{{{fixedBody.TrimEnd()}}}{punct}||";
                }
            );

        return result;
    }

    public static IReadOnlyList<StepInstructionSegment> ParseSegments(string? instruction)
    {
        if (string.IsNullOrEmpty(instruction))
        {
            return Array.Empty<StepInstructionSegment>();
        }

        instruction = NormalizeMalformedTokenSyntax(instruction);

        var segments = new List<StepInstructionSegment>();
        var matches = TokenRegex().Matches(instruction);
        var lastIndex = 0;

        foreach (Match match in matches)
        {
            if (match.Index > lastIndex)
            {
                segments.Add(new StepInstructionSegment(instruction[lastIndex..match.Index]));
            }

            var jsonContent = match.Groups["json"].Value;
            var punct = match.Groups["punct"].Value;
            var token = TryParseToken(jsonContent);

            if (token != null)
            {
                var cleanTokenText = string.IsNullOrEmpty(punct)
                    ? match.Value
                    : $"||{jsonContent}||";
                segments.Add(new StepInstructionSegment(cleanTokenText, token));
                if (!string.IsNullOrEmpty(punct))
                {
                    segments.Add(new StepInstructionSegment(punct));
                }
            }
            else
            {
                // Fallback to literal text if JSON cannot be parsed
                segments.Add(new StepInstructionSegment(match.Value));
            }

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < instruction.Length)
        {
            segments.Add(new StepInstructionSegment(instruction[lastIndex..]));
        }

        return segments;
    }

    public static IReadOnlyList<StepToken> ExtractTokens(string? instruction)
    {
        return ParseSegments(instruction)
            .Where(s => s.Token != null)
            .Select(s => s.Token!)
            .ToList();
    }

    /// <summary>
    /// Replaces transient <see cref="StepIngredientToken.IngredientIndex"/> indices with
    /// persisted <see cref="StepIngredientToken.IngredientId"/> GUIDs when saving a recipe.
    /// </summary>
    public static string BindIngredientIds(
        string? instruction,
        IReadOnlyList<Guid> orderedIngredientIds
    )
    {
        if (string.IsNullOrEmpty(instruction))
        {
            return string.Empty;
        }

        if (orderedIngredientIds.Count == 0)
        {
            return instruction;
        }

        var validIds = orderedIngredientIds.ToHashSet();
        var normalized = NormalizeMalformedTokenSyntax(instruction);

        return TokenRegex()
            .Replace(
                normalized,
                match =>
                {
                    var token = TryParseToken(match.Groups["json"].Value);
                    if (token is not StepIngredientToken ing)
                    {
                        return match.Value;
                    }

                    Guid? resolvedId =
                        ing.IngredientId.HasValue && validIds.Contains(ing.IngredientId.Value)
                            ? ing.IngredientId.Value
                            : null;

                    if (
                        !resolvedId.HasValue
                        && ing.IngredientIndex is int idx
                        && idx >= 0
                        && idx < orderedIngredientIds.Count
                    )
                    {
                        resolvedId = orderedIngredientIds[idx];
                    }

                    if (!resolvedId.HasValue)
                    {
                        return match.Value;
                    }

                    if (resolvedId == ing.IngredientId && !ing.IngredientIndex.HasValue)
                    {
                        return match.Value;
                    }

                    var rebound = ing with { IngredientId = resolvedId, IngredientIndex = null };
                    var punct = match.Groups["punct"].Value;
                    return string.IsNullOrEmpty(punct)
                        ? FormatToken(rebound)
                        : $"{FormatToken(rebound)}{punct}";
                }
            );
    }

    public static string ToPlainText(string? instruction)
    {
        var segments = ParseSegments(instruction);
        if (segments.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var segment in segments)
        {
            if (segment.Token is null)
            {
                builder.Append(segment.Text);
                continue;
            }

            switch (segment.Token)
            {
                case StepIngredientToken ing:
                    if (!string.IsNullOrWhiteSpace(ing.DisplayText))
                    {
                        builder.Append(ing.DisplayText);
                    }
                    else
                    {
                        var amt = ing.Amount;
                        var maxAmt = ing.MaxAmount;
                        var unit = ing.Unit;
                        var customUnitName =
                            !string.IsNullOrEmpty(ing.CustomUnitName)
                            && (!ing.Unit.HasValue || ing.Unit == MeasurementUnit.Custom)
                                ? ing.CustomUnitName
                                : null;
                        var unitStr = !string.IsNullOrEmpty(customUnitName)
                            ? customUnitName
                            : (
                                unit == MeasurementUnit.Custom
                                    ? null
                                    : unit?.ToString()?.ToLowerInvariant()
                            );
                        var amtText = FormatPlainRange(amt, maxAmt);

                        if (!string.IsNullOrEmpty(amtText) && !string.IsNullOrEmpty(unitStr))
                        {
                            builder.Append(
                                string.IsNullOrWhiteSpace(ing.Ingredient)
                                    ? $"{amtText} {unitStr}"
                                    : $"{amtText} {unitStr} {ing.Ingredient}"
                            );
                        }
                        else if (!string.IsNullOrEmpty(amtText))
                        {
                            builder.Append(
                                string.IsNullOrWhiteSpace(ing.Ingredient)
                                    ? amtText
                                    : $"{amtText} {ing.Ingredient}"
                            );
                        }
                        else
                        {
                            builder.Append(ing.Ingredient);
                        }
                    }
                    break;

                case StepMixtureToken mix:
                {
                    var unitStr =
                        mix.Unit == MeasurementUnit.Custom
                            ? mix.CustomUnitName
                            : mix.Unit?.ToString()?.ToLowerInvariant();
                    var amtText = FormatPlainRange(mix.Amount, mix.MaxAmount);
                    var parts = new List<string>(3);
                    if (!string.IsNullOrEmpty(amtText))
                        parts.Add(amtText);
                    if (!string.IsNullOrEmpty(unitStr))
                        parts.Add(unitStr);
                    if (!string.IsNullOrWhiteSpace(mix.Name))
                        parts.Add(mix.Name);
                    builder.Append(string.Join(' ', parts));
                    break;
                }

                case StepFixedToken fx:
                {
                    var unitStr =
                        fx.Unit == MeasurementUnit.Custom
                            ? fx.CustomUnitName
                            : fx.Unit?.ToString()?.ToLowerInvariant();
                    var amtText = FormatPlainRange(fx.Amount, fx.MaxAmount);
                    var measure =
                        string.IsNullOrEmpty(unitStr) ? amtText
                        : fx.Hyphenated ? $"{amtText}-{unitStr}"
                        : $"{amtText} {unitStr}".Trim();
                    if (!string.IsNullOrWhiteSpace(fx.Name))
                    {
                        builder.Append(
                            string.IsNullOrEmpty(measure) ? fx.Name : $"{measure} {fx.Name}"
                        );
                    }
                    else
                    {
                        builder.Append(measure);
                    }
                    break;
                }

                case StepDimensionToken dim:
                {
                    var valText = FormatPlainRange(dim.Value, dim.MaxValue);
                    var unitLabel = DimensionUnitHelper.GetDisplayLabel(
                        dim.Unit,
                        dim.MaxValue ?? dim.Value,
                        dim.Hyphenated
                    );
                    builder.Append(
                        dim.Hyphenated ? $"{valText}-{unitLabel}" : $"{valText} {unitLabel}"
                    );
                    break;
                }

                case StepTimerToken timer:
                    if (!string.IsNullOrWhiteSpace(timer.Label) && !IsDurationLabel(timer.Label))
                    {
                        builder.Append(timer.Label);
                    }
                    else if (timer.MaxSeconds.HasValue && timer.MaxSeconds.Value > timer.Seconds)
                    {
                        builder.Append(
                            FormatPlainTimerRange(timer.Seconds, timer.MaxSeconds.Value)
                        );
                    }
                    else
                    {
                        var minutes = timer.Seconds / 60;
                        var seconds = timer.Seconds % 60;
                        if (minutes > 0 && seconds > 0)
                            builder.Append($"{minutes} min {seconds} sec");
                        else if (minutes > 0)
                            builder.Append($"{minutes} minutes");
                        else
                            builder.Append($"{seconds} seconds");
                    }
                    break;

                case StepTemperatureToken temp:
                    if (temp.Fahrenheit.HasValue)
                    {
                        var maxSuffix =
                            temp.MaxFahrenheit.HasValue
                            && temp.MaxFahrenheit.Value > temp.Fahrenheit.Value
                                ? $"–{FormatTempNumber(temp.MaxFahrenheit.Value)}"
                                : string.Empty;
                        builder.Append($"{FormatTempNumber(temp.Fahrenheit.Value)}{maxSuffix}°F");
                    }
                    else if (temp.Celsius.HasValue)
                    {
                        var maxSuffix =
                            temp.MaxCelsius.HasValue && temp.MaxCelsius.Value > temp.Celsius.Value
                                ? $"–{FormatTempNumber(temp.MaxCelsius.Value)}"
                                : string.Empty;
                        builder.Append($"{FormatTempNumber(temp.Celsius.Value)}{maxSuffix}°C");
                    }
                    break;

                default:
                    builder.Append(segment.Text);
                    break;
            }
        }

        return builder.ToString();
    }

    private static string FormatPlainRange(decimal? min, decimal? max)
    {
        if (!min.HasValue)
        {
            return string.Empty;
        }
        var minStr = min.Value.ToString(CultureInfo.InvariantCulture);
        if (max.HasValue && max.Value > min.Value)
        {
            return $"{minStr}–{max.Value.ToString(CultureInfo.InvariantCulture)}";
        }
        return minStr;
    }

    private static string FormatTempNumber(decimal value) =>
        value == Math.Truncate(value)
            ? ((int)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string FormatPlainTimerRange(int minSeconds, int maxSeconds)
    {
        if (minSeconds % 3600 == 0 && maxSeconds % 3600 == 0)
        {
            return $"{minSeconds / 3600} to {maxSeconds / 3600} hours";
        }
        if (minSeconds % 60 == 0 && maxSeconds % 60 == 0)
        {
            return $"{minSeconds / 60} to {maxSeconds / 60} minutes";
        }
        return $"{minSeconds} to {maxSeconds} seconds";
    }

    public static string FormatToken(StepToken token)
    {
        return token switch
        {
            StepIngredientToken ing => $"||{JsonSerializer.Serialize(ing, JsonOptions)}||",
            StepMixtureToken mix => $"||{JsonSerializer.Serialize(mix, JsonOptions)}||",
            StepFixedToken fx => $"||{JsonSerializer.Serialize(fx, JsonOptions)}||",
            StepDimensionToken dim => $"||{JsonSerializer.Serialize(dim, JsonOptions)}||",
            StepTimerToken timer => $"||{JsonSerializer.Serialize(timer, JsonOptions)}||",
            StepTemperatureToken temp => $"||{JsonSerializer.Serialize(temp, JsonOptions)}||",
            _ => string.Empty,
        };
    }

    public static StepToken? TryParseToken(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // Check explicit type if present
            if (
                root.TryGetProperty("type", out var typeProp)
                && typeProp.ValueKind == JsonValueKind.String
            )
            {
                var typeStr = typeProp.GetString();
                if (string.Equals(typeStr, "timer", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonSerializer.Deserialize<StepTimerToken>(json, JsonOptions);
                }
                if (string.Equals(typeStr, "temperature", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonSerializer.Deserialize<StepTemperatureToken>(json, JsonOptions);
                }
                if (string.Equals(typeStr, "mixture", StringComparison.OrdinalIgnoreCase))
                {
                    var mix = JsonSerializer.Deserialize<StepMixtureToken>(json, JsonOptions);
                    if (mix is not null && string.IsNullOrWhiteSpace(mix.Name))
                    {
                        var fallback = TryGetFallbackIngredientName(root);
                        if (fallback is not null)
                        {
                            return mix with { Name = fallback };
                        }
                    }
                    return mix;
                }
                if (string.Equals(typeStr, "fixed", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonSerializer.Deserialize<StepFixedToken>(json, JsonOptions);
                }
                if (string.Equals(typeStr, "dimension", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonSerializer.Deserialize<StepDimensionToken>(json, JsonOptions);
                }
                if (string.Equals(typeStr, "ingredient", StringComparison.OrdinalIgnoreCase))
                {
                    var typedIng = JsonSerializer.Deserialize<StepIngredientToken>(
                        json,
                        JsonOptions
                    );
                    if (typedIng is not null && string.IsNullOrWhiteSpace(typedIng.Ingredient))
                    {
                        var fallbackName = TryGetFallbackIngredientName(root);
                        if (fallbackName is not null)
                        {
                            return typedIng with { Ingredient = fallbackName };
                        }
                    }
                    return typedIng;
                }
            }

            // Infer from fields
            if (root.TryGetProperty("seconds", out _))
            {
                return JsonSerializer.Deserialize<StepTimerToken>(json, JsonOptions);
            }

            if (root.TryGetProperty("fahrenheit", out _) || root.TryGetProperty("celsius", out _))
            {
                return JsonSerializer.Deserialize<StepTemperatureToken>(json, JsonOptions);
            }

            if (root.TryGetProperty("value", out _))
            {
                return JsonSerializer.Deserialize<StepDimensionToken>(json, JsonOptions);
            }

            var inferredIngredientName = TryGetFallbackIngredientName(root);
            if (inferredIngredientName is not null)
            {
                var ingToken = JsonSerializer.Deserialize<StepIngredientToken>(json, JsonOptions);
                return ingToken is not null
                    ? ingToken with
                    {
                        Ingredient = string.IsNullOrWhiteSpace(ingToken.Ingredient)
                            ? inferredIngredientName
                            : ingToken.Ingredient,
                    }
                    : null;
            }

            // Fallback for ingredient token without explicit "ingredient" property (e.g. {"amount": 1, "unit": "tbsp"})
            if (
                root.TryGetProperty("amount", out _)
                || root.TryGetProperty("unit", out _)
                || root.TryGetProperty("displayAmount", out _)
                || root.TryGetProperty("displayUnit", out _)
            )
            {
                var partial = JsonSerializer.Deserialize<StepIngredientToken>(json, JsonOptions);
                return partial is not null
                    ? partial with
                    {
                        Ingredient = partial.Ingredient ?? string.Empty,
                    }
                    : null;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetFallbackIngredientName(JsonElement root)
    {
        foreach (var propName in new[] { "ingredient", "displayName", "canonicalFoodName", "name" })
        {
            foreach (var prop in root.EnumerateObject())
            {
                if (
                    string.Equals(prop.Name, propName, StringComparison.OrdinalIgnoreCase)
                    && prop.Value.ValueKind == JsonValueKind.String
                )
                {
                    var val = prop.Value.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        return val;
                    }
                }
            }
        }
        return null;
    }

    [GeneratedRegex(
        @"^\s*(?:to|or|[-–—])\s*$",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex RangeConnectorRegex();

    [GeneratedRegex(@"^\s*and\s*$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AndConnectorRegex();

    [GeneratedRegex(@"\bbetween\s*$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TrailingBetweenWordRegex();

    [GeneratedRegex(@"^\s*(?:/|\()\s*$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DualTempSeparatorRegex();

    [GeneratedRegex(
        @"^\s*(?:/|\()\s*(?<celsius>\d+(?:\.\d+)?)\s*°?\s*C(?:elsius)?\s*\)?",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex TrailingCelsiusTextRegex();

    public static string CleanStepInstruction(string? instruction)
    {
        if (string.IsNullOrWhiteSpace(instruction))
        {
            return string.Empty;
        }

        var segments = ParseSegments(instruction);
        if (segments.Count == 0)
        {
            return instruction;
        }

        var builder = new StringBuilder();
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (segment.Token is null)
            {
                var text = segment.Text;
                if (i + 1 < segments.Count && segments[i + 1].Token is StepIngredientToken ing)
                {
                    text = StripTrailingMeasurement(text, ing);
                }
                builder.Append(text);
            }
            else if (segment.Token is StepTimerToken timer)
            {
                // Collapse adjacent range timer tokens: "||t1|| to ||t2||" or "between ||t1|| and ||t2||" -> single timer with lower bound
                if (
                    i + 2 < segments.Count
                    && segments[i + 1].Token is null
                    && segments[i + 2].Token is StepTimerToken timer2
                )
                {
                    var isDirectRange = RangeConnectorRegex().IsMatch(segments[i + 1].Text);
                    Match? betweenMatch = null;
                    if (!isDirectRange && AndConnectorRegex().IsMatch(segments[i + 1].Text))
                    {
                        var candidate = TrailingBetweenWordRegex().Match(builder.ToString());
                        if (candidate.Success)
                        {
                            betweenMatch = candidate;
                        }
                    }

                    if (isDirectRange || betweenMatch is not null)
                    {
                        if (betweenMatch is not null)
                        {
                            builder.Length = betweenMatch.Index;
                            builder.Append("about ");
                        }

                        var minSeconds =
                            timer.Seconds > 0 && timer2.Seconds > 0
                                ? Math.Min(timer.Seconds, timer2.Seconds)
                                : Math.Max(timer.Seconds, timer2.Seconds);
                        var chosenLabel =
                            !string.IsNullOrWhiteSpace(timer.Label) && !IsDurationLabel(timer.Label)
                                ? timer.Label
                            : !string.IsNullOrWhiteSpace(timer2.Label)
                            && !IsDurationLabel(timer2.Label)
                                ? timer2.Label
                            : null;
                        builder.Append(FormatToken(new StepTimerToken(minSeconds, chosenLabel)));
                        i += 2;
                        SkipTrailingPattern(
                            segments,
                            ref i,
                            builder,
                            TrailingTimeUnitAfterTimerRegex()
                        );
                        SkipRedundantTimerParenthetical(segments, ref i, minSeconds, builder);
                        continue;
                    }
                }

                if (IsDurationLabel(timer.Label))
                {
                    builder.Append(FormatToken(timer with { Label = null }));
                }
                else
                {
                    builder.Append(segment.Text);
                }
                SkipTrailingPattern(segments, ref i, builder, TrailingTimeUnitAfterTimerRegex());
                SkipRedundantTimerParenthetical(segments, ref i, timer.Seconds, builder);
            }
            else if (segment.Token is StepTemperatureToken temp)
            {
                TrimDuplicatePrepositionBeforeTemp(builder);
                // Collapse dual F/C temperature tokens: ||F|| / ||C|| or ||F|| (||C||)
                if (
                    i + 2 < segments.Count
                    && segments[i + 1].Token is null
                    && DualTempSeparatorRegex().IsMatch(segments[i + 1].Text)
                    && segments[i + 2].Token is StepTemperatureToken temp2
                    && IsDualTemperaturePair(temp, temp2, out var primaryTemp)
                )
                {
                    builder.Append(FormatToken(primaryTemp));
                    var wasParen = segments[i + 1].Text.Contains('(');
                    i += 2;
                    if (
                        wasParen
                        && i + 1 < segments.Count
                        && segments[i + 1].Token is null
                        && segments[i + 1].Text.StartsWith(')')
                    )
                    {
                        var remainingAfterParen = segments[i + 1].Text[1..];
                        remainingAfterParen = StripLeadingPattern(
                            remainingAfterParen,
                            TrailingDegreeAfterTempRegex()
                        );
                        if (!string.IsNullOrEmpty(remainingAfterParen))
                        {
                            builder.Append(remainingAfterParen);
                        }
                        i += 1;
                    }
                    else
                    {
                        SkipTrailingPattern(
                            segments,
                            ref i,
                            builder,
                            TrailingDegreeAfterTempRegex()
                        );
                    }
                    continue;
                }

                // Also collapse trailing raw "/60°C" or " (60°C)" right after a temperature token
                if (
                    temp.Fahrenheit.HasValue
                    && i + 1 < segments.Count
                    && segments[i + 1].Token is null
                )
                {
                    var trailingMatch = TrailingCelsiusTextRegex().Match(segments[i + 1].Text);
                    if (
                        trailingMatch.Success
                        && decimal.TryParse(
                            trailingMatch.Groups["celsius"].Value,
                            NumberStyles.Number,
                            CultureInfo.InvariantCulture,
                            out var cVal
                        )
                        && IsFahrenheitCelsiusMatch(temp.Fahrenheit.Value, cVal)
                    )
                    {
                        builder.Append(segment.Text);
                        var remainder = StripLeadingPattern(
                            segments[i + 1].Text[trailingMatch.Length..],
                            TrailingDegreeAfterTempRegex()
                        );
                        builder.Append(remainder);
                        i += 1;
                        continue;
                    }
                }

                builder.Append(segment.Text);
                SkipTrailingPattern(segments, ref i, builder, TrailingDegreeAfterTempRegex());
            }
            else if (segment.Token is StepIngredientToken ingToken)
            {
                if (string.IsNullOrWhiteSpace(ingToken.Ingredient))
                {
                    builder.Append(ToPlainText(segment.Text));
                }
                else
                {
                    var sanitizedToken = SanitizeRedundantQualifierDisplayText(builder, ingToken);
                    builder.Append(
                        sanitizedToken == ingToken ? segment.Text : FormatToken(sanitizedToken)
                    );
                    if (
                        sanitizedToken.Amount.HasValue
                        && i + 2 < segments.Count
                        && segments[i + 1].Token is null
                        && OfTheConnectorRegex().IsMatch(segments[i + 1].Text)
                        && segments[i + 2].Token is StepIngredientToken duplicateIng
                        && IsSameIngredientReference(sanitizedToken, duplicateIng)
                    )
                    {
                        i += 2;
                    }
                    SkipRedundantIngredientParenthetical(segments, ref i, sanitizedToken, builder);
                    StripDuplicateTrailingNoun(segments, ref i, sanitizedToken, builder);
                }
            }
            else
            {
                builder.Append(segment.Text);
            }
        }

        return builder.ToString();
    }

    private static string StripLeadingPattern(string text, Regex pattern)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var match = pattern.Match(text);
        return match.Success && match.Index == 0 ? text[match.Length..] : text;
    }

    private static void SkipTrailingPattern(
        IReadOnlyList<StepInstructionSegment> segments,
        ref int index,
        StringBuilder builder,
        Regex pattern
    )
    {
        if (index + 1 >= segments.Count || segments[index + 1].Token is not null)
        {
            return;
        }

        var nextText = segments[index + 1].Text;
        var match = pattern.Match(nextText);
        if (!match.Success)
        {
            return;
        }

        var remainder = nextText[match.Length..];
        if (!string.IsNullOrEmpty(remainder))
        {
            builder.Append(remainder);
        }
        index += 1;
    }

    private static void SkipRedundantTimerParenthetical(
        IReadOnlyList<StepInstructionSegment> segments,
        ref int index,
        int timerSeconds,
        StringBuilder builder
    )
    {
        if (
            timerSeconds <= 0
            || index + 1 >= segments.Count
            || segments[index + 1].Token is not null
        )
        {
            return;
        }

        var nextText = segments[index + 1].Text;
        var match = TrailingParentheticalDurationRegex().Match(nextText);
        if (!match.Success)
        {
            return;
        }

        var qty = FlexibleNullableDecimalJsonConverter.ParseNumber(match.Groups["qty"].Value);
        if (!qty.HasValue)
        {
            return;
        }

        var unit = match.Groups["unit"].Value.Trim().ToLowerInvariant();
        var multiplier = unit switch
        {
            _ when unit.StartsWith("h") => 3600m,
            _ when unit.StartsWith("m") => 60m,
            _ => 1m,
        };
        var parsedSeconds = qty.Value * multiplier;
        if (Math.Abs(parsedSeconds - timerSeconds) > 5m)
        {
            return;
        }

        var remainder = match.Groups["rest"].Value;
        if (!string.IsNullOrEmpty(remainder))
        {
            builder.Append(remainder);
        }
        index += 1;
    }

    private static void SkipRedundantIngredientParenthetical(
        IReadOnlyList<StepInstructionSegment> segments,
        ref int index,
        StepIngredientToken token,
        StringBuilder builder
    )
    {
        if (index + 1 >= segments.Count || segments[index + 1].Token is not null)
        {
            return;
        }

        var tokenAmount = token.Amount;
        if (!tokenAmount.HasValue)
        {
            return;
        }

        var nextText = segments[index + 1].Text;
        var match = TrailingParentheticalMeasurementRegex().Match(nextText);
        if (!match.Success)
        {
            return;
        }

        var parsedQty = FlexibleNullableDecimalJsonConverter.ParseNumber(match.Groups["qty"].Value);
        if (!parsedQty.HasValue)
        {
            return;
        }

        var rawUnit = Regex.Replace(
            match.Groups["unit"].Value.Replace(".", ""),
            @"\s+",
            " ",
            RegexOptions.None,
            RegexTimeout
        );
        var (parsedUnit, parsedCustom) = RecipeUnitAndSectionHelpers.NormalizeUnit(rawUnit.Trim());
        var tokenUnit = token.Unit;
        var tokenCustom = token.CustomUnitName;

        var sameUnit =
            parsedUnit.HasValue
            && tokenUnit.HasValue
            && parsedUnit == tokenUnit
            && (
                parsedUnit != MeasurementUnit.Custom
                || string.Equals(parsedCustom, tokenCustom, StringComparison.OrdinalIgnoreCase)
            );
        if (!sameUnit || Math.Abs(parsedQty.Value - tokenAmount.Value) > 0.02m)
        {
            return;
        }

        var remainder = match.Groups["rest"].Value;
        if (!string.IsNullOrEmpty(remainder))
        {
            builder.Append(remainder);
        }
        index += 1;
    }

    private static StepIngredientToken SanitizeRedundantQualifierDisplayText(
        StringBuilder builder,
        StepIngredientToken token
    )
    {
        if (builder.Length == 0)
        {
            return token;
        }

        var preceding = builder.ToString();
        var qualifierMatch = TrailingQualifierWordRegex().Match(preceding);
        if (!qualifierMatch.Success)
        {
            return token;
        }

        var qualifier = qualifierMatch.Groups["qualifier"].Value;

        if (!string.IsNullOrWhiteSpace(token.DisplayText))
        {
            var displayText = token.DisplayText.Trim();
            if (
                displayText.StartsWith(qualifier + " ", StringComparison.OrdinalIgnoreCase)
                && displayText.Length > qualifier.Length + 1
            )
            {
                var trimmedDisplay = displayText[(qualifier.Length + 1)..].TrimStart();
                if (
                    string.Equals(
                        trimmedDisplay,
                        token.Ingredient,
                        StringComparison.OrdinalIgnoreCase
                    ) && !token.Amount.HasValue
                )
                {
                    return token with { DisplayText = null };
                }

                return token with
                {
                    DisplayText = trimmedDisplay,
                };
            }
        }

        if (!token.Amount.HasValue && !string.IsNullOrWhiteSpace(token.Ingredient))
        {
            var ingTrimmed = token.Ingredient.Trim();
            if (
                ingTrimmed.StartsWith(qualifier + " ", StringComparison.OrdinalIgnoreCase)
                && ingTrimmed.Length > qualifier.Length + 1
            )
            {
                return token with { Ingredient = ingTrimmed[(qualifier.Length + 1)..].TrimStart() };
            }
        }

        return token;
    }

    private static void StripDuplicateTrailingNoun(
        IReadOnlyList<StepInstructionSegment> segments,
        ref int index,
        StepIngredientToken token,
        StringBuilder builder
    )
    {
        if (
            index + 1 >= segments.Count
            || segments[index + 1].Token is not null
            || (
                !token.Amount.HasValue
                && !token.Unit.HasValue
                && string.IsNullOrWhiteSpace(token.CustomUnitName)
            )
        )
        {
            return;
        }

        var nextText = segments[index + 1].Text;
        var match = LeadingWordAfterTokenRegex().Match(nextText);
        if (!match.Success)
        {
            return;
        }

        var trailingWord = match.Groups["word"].Value;
        var rest = match.Groups["rest"].Value;

        var ingWords = token.Ingredient.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matchesLastIngWord =
            ingWords.Length > 0 && MatchesWordOrPlural(trailingWord, ingWords[^1]);

        var unitWord = token.CustomUnitName;
        if (string.IsNullOrWhiteSpace(unitWord))
        {
            var effectiveUnit = token.Unit;
            unitWord = effectiveUnit
                is MeasurementUnit.Clove
                    or MeasurementUnit.Slice
                    or MeasurementUnit.Can
                    or MeasurementUnit.Bunch
                    or MeasurementUnit.Pinch
                    or MeasurementUnit.Dash
                ? effectiveUnit.Value.ToString()
                : null;
        }

        var matchesUnitWord =
            !string.IsNullOrWhiteSpace(unitWord)
            && !string.Equals(unitWord, "whole", StringComparison.OrdinalIgnoreCase)
            && MatchesWordOrPlural(trailingWord, unitWord.Trim());

        if (!matchesLastIngWord && !matchesUnitWord)
        {
            return;
        }

        // Avoid stripping compound nouns like "egg whites", "water bath", or "tomato slices".
        if (
            matchesLastIngWord
            && !string.IsNullOrEmpty(rest)
            && char.IsWhiteSpace(rest[0])
            && Regex.IsMatch(
                rest,
                @"^\s+(?:whites?|yolks?|halves?|shells?|bath|stock|broth|sauce|paste|mixture|dressing|glaze|dough|crust|filling|topping|garnish|slices?|wedges?|chunks?|pieces?|strips?|cubes?|rings?|florets?|leaves?|stalks?|stems?|seeds?|zest|juice|peel|skin)\b",
                RegexOptions.IgnoreCase,
                RegexTimeout
            )
        )
        {
            return;
        }

        if (!string.IsNullOrEmpty(rest))
        {
            builder.Append(rest);
        }
        index += 1;
    }

    private static bool MatchesWordOrPlural(string actual, string expected)
    {
        if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (
            string.Equals(actual, expected + "s", StringComparison.OrdinalIgnoreCase)
            || string.Equals(actual + "s", expected, StringComparison.OrdinalIgnoreCase)
            || string.Equals(actual, expected + "es", StringComparison.OrdinalIgnoreCase)
            || string.Equals(actual + "es", expected, StringComparison.OrdinalIgnoreCase)
        )
        {
            return true;
        }

        if (
            expected.EndsWith("y", StringComparison.OrdinalIgnoreCase)
            && expected.Length > 2
            && string.Equals(actual, expected[..^1] + "ies", StringComparison.OrdinalIgnoreCase)
        )
        {
            return true;
        }

        if (
            actual.EndsWith("y", StringComparison.OrdinalIgnoreCase)
            && actual.Length > 2
            && string.Equals(expected, actual[..^1] + "ies", StringComparison.OrdinalIgnoreCase)
        )
        {
            return true;
        }

        return false;
    }

    private static bool IsFahrenheitCelsiusMatch(decimal fahrenheit, decimal celsius)
    {
        var expectedC = (fahrenheit - 32m) * 5m / 9m;
        return Math.Abs(expectedC - celsius) <= 3m;
    }

    private static bool IsDualTemperaturePair(
        StepTemperatureToken first,
        StepTemperatureToken second,
        out StepTemperatureToken primary
    )
    {
        primary = first;
        var f = first.Fahrenheit;
        // Case 1: second token explicitly has Celsius
        if (
            f.HasValue
            && second.Celsius.HasValue
            && IsFahrenheitCelsiusMatch(f.Value, second.Celsius.Value)
        )
        {
            primary = new StepTemperatureToken(f.Value, null);
            return true;
        }
        // Case 2: model mislabeled Celsius as Fahrenheit in the second token (e.g. 140°F / 60°F)
        if (
            f.HasValue
            && second.Fahrenheit.HasValue
            && !second.Celsius.HasValue
            && IsFahrenheitCelsiusMatch(f.Value, second.Fahrenheit.Value)
        )
        {
            primary = new StepTemperatureToken(f.Value, null);
            return true;
        }
        return false;
    }

    private static string StripTrailingMeasurement(string text, StepIngredientToken token)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var hasTokenQuantityOrUnit = token.Amount.HasValue || token.Unit.HasValue;

        if (!hasTokenQuantityOrUnit)
        {
            return text;
        }

        var match = TrailingMeasurementRegex().Match(text);
        if (match.Success && match.Length > 0)
        {
            var hasQty = match.Groups["qty"].Success;
            var hasUnit = match.Groups["unit"].Success;
            if (hasQty || hasUnit)
            {
                return text[..match.Index];
            }
        }

        var modMatch = TrailingMeasurementBeforeModifierRegex().Match(text);
        if (modMatch.Success && modMatch.Length > 0)
        {
            return text[..modMatch.Index] + modMatch.Groups["mod"].Value;
        }

        return text;
    }

    private static void TrimDuplicatePrepositionBeforeTemp(StringBuilder builder)
    {
        if (builder.Length == 0)
        {
            return;
        }

        var current = builder.ToString();
        var match = DuplicateAtBeforeTempRegex().Match(current);
        if (match.Success)
        {
            builder.Length = match.Index;
            builder.Append(match.Groups["keep"].Value);
            builder.Append(' ');
        }
    }

    private static bool IsSameIngredientReference(
        StepIngredientToken first,
        StepIngredientToken second
    )
    {
        if (first.IngredientId.HasValue && second.IngredientId.HasValue)
        {
            return first.IngredientId.Value == second.IngredientId.Value;
        }

        return !string.IsNullOrWhiteSpace(first.Ingredient)
            && string.Equals(
                first.Ingredient.Trim(),
                second.Ingredient?.Trim(),
                StringComparison.OrdinalIgnoreCase
            );
    }
}

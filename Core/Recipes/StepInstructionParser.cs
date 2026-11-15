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
        @"(?<!\|)(\{\s*""(?:type|ingredient|displayName|canonicalFoodName|name|amount|displayAmount|displayText|unit|seconds|fahrenheit|celsius)""\s*:[^}\r\n]*?\})(?!\|)",
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
        @"(?<prefix>(?:^|,)\s*)""(?<key>type|ingredient|displayName|canonicalFoodName|name|amount|unit|displayAmount|displayUnit|customUnitName|displayCustomUnitName|displayText|notes|ingredientId|seconds|label|fahrenheit|celsius|value|temperature)""\s*(?=[0-9""\-]|true\b|false\b|null\b)",
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
        @"^\s*(?:°\s*(?:[FC]|Fahrenheit|Celsius)\b|degrees?(?:\s*(?:[FC]|Fahrenheit|Celsius))?|deg\.?(?:\s*(?:[FC]|Fahrenheit|Celsius))?)(?=$|[\s\.,:;!\?\)\]])",
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
                        var amt = ing.DisplayAmount ?? ing.Amount;
                        var unit = ing.DisplayUnit ?? ing.Unit;
                        var customUnitName = !string.IsNullOrEmpty(ing.DisplayCustomUnitName)
                            ? ing.DisplayCustomUnitName
                            : (
                                !string.IsNullOrEmpty(ing.CustomUnitName)
                                && !ing.DisplayUnit.HasValue
                                && (!ing.Unit.HasValue || ing.Unit == MeasurementUnit.Custom)
                                    ? ing.CustomUnitName
                                    : null
                            );
                        var unitStr = !string.IsNullOrEmpty(customUnitName)
                            ? customUnitName
                            : (
                                unit == MeasurementUnit.Custom
                                    ? null
                                    : unit?.ToString()?.ToLowerInvariant()
                            );

                        if (amt.HasValue && !string.IsNullOrEmpty(unitStr))
                        {
                            builder.Append(
                                string.IsNullOrWhiteSpace(ing.Ingredient)
                                    ? $"{amt} {unitStr}"
                                    : $"{amt} {unitStr} {ing.Ingredient}"
                            );
                        }
                        else if (amt.HasValue)
                        {
                            builder.Append(
                                string.IsNullOrWhiteSpace(ing.Ingredient)
                                    ? $"{amt}"
                                    : $"{amt} {ing.Ingredient}"
                            );
                        }
                        else
                        {
                            builder.Append(ing.Ingredient);
                        }
                    }
                    break;

                case StepTimerToken timer:
                    if (!string.IsNullOrWhiteSpace(timer.Label) && !IsDurationLabel(timer.Label))
                    {
                        builder.Append(timer.Label);
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
                        builder.Append($"{temp.Fahrenheit}°F");
                    else if (temp.Celsius.HasValue)
                        builder.Append($"{temp.Celsius}°C");
                    break;

                default:
                    builder.Append(segment.Text);
                    break;
            }
        }

        return builder.ToString();
    }

    public static string FormatToken(StepToken token)
    {
        return token switch
        {
            StepIngredientToken ing => $"||{JsonSerializer.Serialize(ing, JsonOptions)}||",
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
                        (sanitizedToken.Amount.HasValue || sanitizedToken.DisplayAmount.HasValue)
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

        var tokenAmount = token.DisplayAmount ?? token.Amount;
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
        var tokenUnit = token.DisplayUnit ?? token.Unit;
        var tokenCustom = token.DisplayCustomUnitName ?? token.CustomUnitName;

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
                    )
                    && !token.Amount.HasValue
                    && !token.DisplayAmount.HasValue
                    && string.IsNullOrWhiteSpace(token.DisplayCustomUnitName)
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

        if (
            !token.Amount.HasValue
            && !token.DisplayAmount.HasValue
            && !string.IsNullOrWhiteSpace(token.Ingredient)
        )
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
                && !token.DisplayAmount.HasValue
                && !token.Unit.HasValue
                && !token.DisplayUnit.HasValue
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

        var unitWord = token.DisplayCustomUnitName ?? token.CustomUnitName;
        if (string.IsNullOrWhiteSpace(unitWord))
        {
            var effectiveUnit = token.DisplayUnit ?? token.Unit;
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

        var hasTokenQuantityOrUnit =
            token.Amount.HasValue
            || token.DisplayAmount.HasValue
            || token.Unit.HasValue
            || token.DisplayUnit.HasValue;

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

            if (
                nameScore == 0
                && RecipeUnitAndSectionHelpers.IsEggPartMatch(tokenName, disp, alt, candidates)
            )
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

        var stepSectionNorm = RecipeUnitAndSectionHelpers.NormalizeSectionName(stepSectionName);
        var bestScore = int.MinValue;
        var bestIdx = matching[0].Candidate.Index;

        foreach (var (cand, nameScore) in matching)
        {
            var score = nameScore;
            var candSectionNorm = RecipeUnitAndSectionHelpers.NormalizeSectionName(
                cand.SectionName
            );

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

            if (token.Unit.HasValue && cand.Unit.HasValue && token.Unit == cand.Unit)
            {
                score += 40;
            }

            if (currentStepIndices?.Contains(cand.Index) == true)
            {
                score += 30;
            }

            var hasRemaining =
                !string.IsNullOrWhiteSpace(stepInstruction)
                && Regex.IsMatch(
                    stepInstruction,
                    @"\bremaining\b",
                    RegexOptions.IgnoreCase,
                    RegexTimeout
                );
            var candIsDivided =
                !string.IsNullOrWhiteSpace(cand.PreparationNote)
                && cand.PreparationNote.Contains("divided", StringComparison.OrdinalIgnoreCase);

            if (hasRemaining)
            {
                score += candIsDivided ? 150 : -80;
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
}

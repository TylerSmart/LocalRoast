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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static IReadOnlyList<StepInstructionSegment> ParseSegments(string? instruction)
    {
        if (string.IsNullOrEmpty(instruction))
        {
            return Array.Empty<StepInstructionSegment>();
        }

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
                        var unitStr = !string.IsNullOrEmpty(ing.DisplayCustomUnitName)
                            ? ing.DisplayCustomUnitName
                            : unit?.ToString()?.ToLowerInvariant();

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
                    if (
                        !string.IsNullOrWhiteSpace(timer.Label)
                        && !StepTimerExtractor.IsDurationLabel(timer.Label)
                    )
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
                    return JsonSerializer.Deserialize<StepIngredientToken>(json, JsonOptions);
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

            if (root.TryGetProperty("ingredient", out _))
            {
                return JsonSerializer.Deserialize<StepIngredientToken>(json, JsonOptions);
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
            else if (
                segment.Token is StepTimerToken timer
                && StepTimerExtractor.IsDurationLabel(timer.Label)
            )
            {
                builder.Append(FormatToken(timer with { Label = null }));
            }
            else
            {
                builder.Append(segment.Text);
            }
        }

        return builder.ToString();
    }

    private static string StripTrailingMeasurement(string text, StepIngredientToken token)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var match = TrailingMeasurementRegex().Match(text);
        if (!match.Success || match.Length == 0)
        {
            return text;
        }

        var hasQty = match.Groups["qty"].Success;
        var hasUnit = match.Groups["unit"].Success;

        if (!hasQty && !hasUnit)
        {
            return text;
        }

        if (
            token.Amount.HasValue
            || token.DisplayAmount.HasValue
            || token.Unit.HasValue
            || token.DisplayUnit.HasValue
        )
        {
            return text[..match.Index];
        }

        return text;
    }
}

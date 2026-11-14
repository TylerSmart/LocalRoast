using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace App.Core.Ingestion;

public sealed partial class FlexibleNullableIntJsonConverter : JsonConverter<int?>
{
    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitsRegex();

    public override int? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            if (reader.TryGetInt32(out var intVal))
            {
                return intVal;
            }
            if (reader.TryGetDouble(out var doubleVal))
            {
                return (int)Math.Round(doubleVal);
            }
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var raw = reader.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var trimmed = raw.Trim();
            if (
                string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "n/a", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase)
            )
            {
                return null;
            }

            if (
                int.TryParse(
                    trimmed,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var directInt
                )
            )
            {
                return directInt;
            }

            if (
                double.TryParse(
                    trimmed,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var floatVal
                )
            )
            {
                return (int)Math.Round(floatVal);
            }

            var match = DigitsRegex().Match(trimmed);
            if (
                match.Success
                && int.TryParse(
                    match.Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsedMatch
                )
            )
            {
                return parsedMatch;
            }

            return null;
        }

        if (reader.TokenType == JsonTokenType.True)
        {
            return 1;
        }
        if (reader.TokenType == JsonTokenType.False)
        {
            return 0;
        }

        return null;
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteNumberValue(value.Value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

public sealed class FlexibleIntJsonConverter : JsonConverter<int>
{
    private static readonly FlexibleNullableIntJsonConverter InnerConverter = new();

    public override int Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        return InnerConverter.Read(ref reader, typeToConvert, options) ?? 0;
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
}

public sealed partial class FlexibleNullableDecimalJsonConverter : JsonConverter<decimal?>
{
    [GeneratedRegex(@"^(\d+(?:\.\d+)?)")]
    private static partial Regex LeadingNumberRegex();

    public override decimal? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            if (reader.TryGetDecimal(out var decVal))
            {
                return decVal;
            }
            if (reader.TryGetDouble(out var doubleVal))
            {
                return (decimal)doubleVal;
            }
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return ParseNumber(reader.GetString());
        }

        return null;
    }

    private static readonly Dictionary<string, decimal> NumberWords = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["one"] = 1m,
        ["two"] = 2m,
        ["three"] = 3m,
        ["four"] = 4m,
        ["five"] = 5m,
        ["six"] = 6m,
        ["seven"] = 7m,
        ["eight"] = 8m,
        ["nine"] = 9m,
        ["ten"] = 10m,
        ["eleven"] = 11m,
        ["twelve"] = 12m,
        ["dozen"] = 12m,
        ["half"] = 0.5m,
    };

    public static decimal? ParseNumber(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var trimmed = ReplaceUnicodeFractions(raw.Trim());
        if (
            trimmed.Equals("pinch", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("a pinch", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("dash", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("a dash", StringComparison.OrdinalIgnoreCase)
        )
        {
            return 1m;
        }
        if (NumberWords.TryGetValue(trimmed, out var wordValue))
        {
            return wordValue;
        }
        if (
            decimal.TryParse(
                trimmed,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var direct
            )
        )
        {
            return direct;
        }
        if (TryParseFraction(trimmed, out var fraction))
        {
            return fraction;
        }
        var match = LeadingNumberRegex().Match(trimmed);
        return
            match.Success
            && decimal.TryParse(
                match.Groups[1].Value,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var leading
            )
            ? leading
            : null;
    }

    private static string ReplaceUnicodeFractions(string input)
    {
        return input
            .Replace("½", " 1/2")
            .Replace("¼", " 1/4")
            .Replace("¾", " 3/4")
            .Replace("⅓", " 1/3")
            .Replace("⅔", " 2/3")
            .Replace("⅛", " 1/8")
            .Replace("⅜", " 3/8")
            .Replace("⅝", " 5/8")
            .Replace("⅞", " 7/8");
    }

    private static bool TryParseFraction(string input, out decimal result)
    {
        result = 0m;
        var parts = input.Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
        {
            if (
                decimal.TryParse(
                    parts[0],
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var whole
                )
            )
            {
                var slashParts = parts[1].Split('/');
                if (
                    slashParts.Length == 2
                    && decimal.TryParse(
                        slashParts[0],
                        NumberStyles.Number,
                        CultureInfo.InvariantCulture,
                        out var num
                    )
                    && decimal.TryParse(
                        slashParts[1],
                        NumberStyles.Number,
                        CultureInfo.InvariantCulture,
                        out var den
                    )
                    && den != 0
                )
                {
                    result = whole + (num / den);
                    return true;
                }
            }
        }
        else if (parts.Length == 1)
        {
            var slashParts = parts[0].Split('/');
            if (
                slashParts.Length == 2
                && decimal.TryParse(
                    slashParts[0],
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var num
                )
                && decimal.TryParse(
                    slashParts[1],
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var den
                )
                && den != 0
            )
            {
                result = num / den;
                return true;
            }
        }

        return false;
    }

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteNumberValue(value.Value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

using System.Text.RegularExpressions;
using App.Core.Recipes;

namespace App.Core.Ingestion;

public sealed record SourceMeasurementSpan(
    int Index,
    int Length,
    RecipeMeasurement Measurement,
    RecipeMeasurement? Alternative
);

public static class SourceMeasurementParser
{
    private const string Number = @"(?:\d+\s*)?[½¼¾⅓⅔⅛⅜⅝⅞]|\d+(?:[\s-]+\d+/\d+|/\d+|\.\d+)?";
    private const string Unit =
        @"fluid ounces?|fl[._ ]+oz|tablespoons?|tbsp|teaspoons?|tsp|cups?|c\b|pints?|pt|quarts?|qt|gallons?|gal|ounces?|oz|pounds?|lbs?|grams?|g|kilograms?|kg|millilit(?:er|re)s?|ml|lit(?:er|re)s?|l|pinch(?:es)?|dash(?:es)?|cloves?|cans?|bunch(?:es)?|slices?|whole";
    private static readonly Regex Measurement = new(
        $@"(?<![-−\d/])(?<quantity>{Number})(?:\s*(?:to|[-–—])\s*(?<maximum>{Number}))?\s*(?<unit>{Unit})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );
    private static readonly Regex AlternativeStart = new(
        @"(?:\(\s*|\bor\s+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex PlusJoin = new(
        @"^\s+plus\s+$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );
    private static readonly Regex AlternativeOpen = new(@"^\s*\(\s*$", RegexOptions.Compiled);
    private static readonly Regex AlternativeClose = new(@"^\s*\)", RegexOptions.Compiled);

    /// <summary>
    /// Finds every measured amount in prose with its exact span. "1/4 cup plus 2 tablespoons" is
    /// one amount (in the smaller unit) and a parenthetical equivalent such as "1 cup (5 ounces)"
    /// is included in the span as the alternative measurement.
    /// </summary>
    public static IReadOnlyList<SourceMeasurementSpan> FindSpans(string? text)
    {
        var result = new List<SourceMeasurementSpan>();
        if (string.IsNullOrWhiteSpace(text))
            return result;
        var matches = Measurement
            .Matches(text)
            .Select(match => (Match: match, Value: ParseMatch(match)))
            .Where(m => m.Value is not null)
            .ToList();
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Match.Index;
            var end = start + matches[i].Match.Length;
            var primary = matches[i].Value!;
            while (
                i + 1 < matches.Count
                && PlusJoin.IsMatch(text[end..matches[i + 1].Match.Index])
                && TryAdd(primary, matches[i + 1].Value!, out var combined)
            )
            {
                primary = combined;
                i++;
                end = matches[i].Match.Index + matches[i].Match.Length;
            }
            RecipeMeasurement? alternative = null;
            if (
                i + 1 < matches.Count
                && AlternativeOpen.IsMatch(text[end..matches[i + 1].Match.Index])
                && matches[i + 1].Value!.Unit != primary.Unit
            )
            {
                var alternativeEnd = matches[i + 1].Match.Index + matches[i + 1].Match.Length;
                var close = AlternativeClose.Match(text[alternativeEnd..]);
                if (close.Success)
                {
                    alternative = matches[i + 1].Value;
                    end = alternativeEnd + close.Length;
                    i++;
                }
            }
            result.Add(new SourceMeasurementSpan(start, end - start, primary, alternative));
        }
        return result;
    }

    private static bool TryAdd(
        RecipeMeasurement first,
        RecipeMeasurement second,
        out RecipeMeasurement combined
    )
    {
        combined = first;
        if (
            first.MaxQuantity.HasValue
            || second.MaxQuantity.HasValue
            || first.Quantity is not { } firstQuantity
            || second.Quantity is not { } secondQuantity
            || first.Unit is not { } firstUnit
            || second.Unit is not { } secondUnit
            || !CulinaryMath.TryConvertQuantity(1m, firstUnit, secondUnit, out var factor)
        )
            return false;
        // Express the sum in the smaller unit so it stays a readable kitchen amount.
        combined =
            factor >= 1m
                ? new RecipeMeasurement(firstQuantity * factor + secondQuantity, secondUnit)
                : new RecipeMeasurement(firstQuantity + secondQuantity / factor, firstUnit);
        return true;
    }

    public static IReadOnlyList<RecipeMeasurement> Parse(string? source) =>
        string.IsNullOrWhiteSpace(source)
            ? []
            : Measurement
                .Matches(source)
                .Select(ParseMatch)
                .Where(m => m is not null)
                .Select(m => m!)
                .ToList();

    public static (RecipeMeasurement Primary, RecipeMeasurement Alternative)? FindAlternative(
        string? source
    )
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;
        var trimmed = source.TrimStart(' ', '\t', '*', '•');
        if (trimmed.StartsWith("- "))
            trimmed = trimmed[2..].TrimStart();
        var primaryMatch = Measurement.Match(trimmed);
        if (!primaryMatch.Success || primaryMatch.Index != 0)
            return null;
        var remaining = trimmed[(primaryMatch.Index + primaryMatch.Length)..];
        var marker = AlternativeStart.Match(remaining);
        if (!marker.Success)
            return null;
        var alternativeMatch = Measurement.Match(remaining, marker.Index + marker.Length);
        if (!alternativeMatch.Success || alternativeMatch.Index != marker.Index + marker.Length)
            return null;
        if (
            marker.Value.TrimStart().StartsWith('(')
            && !remaining[(alternativeMatch.Index + alternativeMatch.Length)..]
                .TrimStart()
                .StartsWith(')')
        )
            return null;
        if (
            !marker.Value.TrimStart().StartsWith('(')
            && marker.Index != remaining.Length - remaining.TrimStart().Length
        )
            return null;
        var primary = ParseMatch(primaryMatch);
        var alternative = ParseMatch(alternativeMatch);
        return primary is not null && alternative is not null && primary.Unit != alternative.Unit
            ? (primary, alternative)
            : null;
    }

    private static RecipeMeasurement? ParseMatch(Match match)
    {
        var quantity = FlexibleNullableDecimalJsonConverter.ParseNumber(
            match.Groups["quantity"].Value
        );
        var maximum = match.Groups["maximum"].Success
            ? FlexibleNullableDecimalJsonConverter.ParseNumber(match.Groups["maximum"].Value)
            : null;
        var (unit, custom) = RecipeDraftNormalizer.NormalizeUnit(
            Regex.Replace(match.Groups["unit"].Value.Replace(".", ""), @"\s+", " ").Trim()
        );
        return
            quantity is >= 0.0001m and <= 999999.9999m
            && (!maximum.HasValue || (maximum >= quantity && maximum <= 999999.9999m))
            && unit.HasValue
            && unit != MeasurementUnit.Custom
            ? new RecipeMeasurement(quantity, unit, maximum, custom)
            : null;
    }
}

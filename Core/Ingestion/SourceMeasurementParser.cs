using System.Text.RegularExpressions;
using App.Core.Recipes;

namespace App.Core.Ingestion;

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

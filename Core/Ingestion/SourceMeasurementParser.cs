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
    private const string Number =
        @"(?:\d+\s*)?[½¼¾⅓⅔⅛⅜⅝⅞]|\d+(?:[\s-]+\d+/\d+|/\d+|\.\d+)?|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|dozen|half";
    private const string Unit =
        @"fluid ounces?|fl[._ ]+oz|tablespoons?|tbsp|teaspoons?|tsp|cups?|c\b|pints?|pt|quarts?|qt|gallons?|gal|ounces?|oz|pounds?|lbs?|grams?|g|kilograms?|kg|millilit(?:er|re)s?|ml|lit(?:er|re)s?|l|pinch(?:es)?|dash(?:es)?|cloves?|cans?|bunch(?:es)?|slices?|whole|sticks?|small|medium|large|recipes?|batch(?:es)?";
    private static readonly Regex Measurement = new(
        $@"(?<![-−\d/])(?<quantity>{Number})(?:[-–—]?\s*(?:to|[-–—])\s*(?<maximum>{Number}))?(?:[-–—]+|\s*)(?<unit>{Unit})\b",
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

    private static readonly Regex ParentheticalAlternative = new(
        @"\((?:about\s+|approx\.?\s+)?(?<alt>[^()]+)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex LeadingCount = new(
        @"^(?<quantity>" + Number + @")(?:\s*(?:to|[-–—])\s*(?<maximum>" + Number + @"))?\s+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex ContainerUnitRegex = new(
        @"\(\s*(?<quantity>"
            + Number
            + @")\s*[-–—\s]\s*(?<unit>ounces?|oz\.?|fluid ounces?|fl[._ ]+oz|grams?|g|millilit(?:er|re)s?|ml|lit(?:er|re)s?|l|pounds?|lbs?\.?)\s*\)\s*cans?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex EquipmentSuffix = new(
        @"^\s+(?:container|pot|stockpot|saucepan|skillet|bowl|dish|pan|baker|casserole|dutch\s+oven)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    public static RecipeMeasurement? TrySumCompoundLeadingMeasurement(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var trimmed = text.TrimStart(' ', '\t', '*', '•');
        if (trimmed.StartsWith("- "))
            trimmed = trimmed[2..].TrimStart();

        var matches = Measurement.Matches(trimmed);
        if (matches.Count < 2 || matches[0].Index != 0)
            return null;

        var measurements = new List<RecipeMeasurement>();
        var first = ParseMatch(matches[0]);
        if (first?.Quantity is null || first.Unit is null)
            return null;
        measurements.Add(first);

        var end = matches[0].Length;
        for (var i = 1; i < matches.Count; i++)
        {
            var match = matches[i];
            var between = trimmed[end..match.Index];
            if (!PlusJoin.IsMatch(between))
                break;
            var parsed = ParseMatch(match);
            if (parsed?.Quantity is null || parsed.Unit is null)
                break;
            measurements.Add(parsed);
            end = match.Index + match.Length;
        }

        if (measurements.Count < 2)
            return null;

        var largestUnit = measurements[0].Unit!.Value;
        for (var i = 1; i < measurements.Count; i++)
        {
            var currentUnit = measurements[i].Unit!.Value;
            if (
                CulinaryMath.TryConvertQuantity(1m, currentUnit, largestUnit, out var factor)
                && factor > 1m
            )
            {
                largestUnit = currentUnit;
            }
        }

        decimal totalInLargest = 0m;
        foreach (var m in measurements)
        {
            if (
                !CulinaryMath.TryConvertQuantity(
                    m.Quantity!.Value,
                    m.Unit!.Value,
                    largestUnit,
                    out var converted
                )
            )
                return null;
            totalInLargest += converted;
        }

        return new RecipeMeasurement(Math.Round(totalInLargest, 4), largestUnit);
    }

    public static LlmExtractedIngredientItem? TryParseSourceIngredientLine(
        string rawLine,
        string? sectionName = null
    )
    {
        if (string.IsNullOrWhiteSpace(rawLine))
            return null;

        var line = rawLine.Trim();
        var trimmed = line.TrimStart('-', '•', '*', ' ');
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;

        RecipeMeasurement? primary = TrySumCompoundLeadingMeasurement(trimmed);
        RecipeMeasurement? alternative = null;
        string foodText = trimmed;

        if (primary is not null)
        {
            var spans = FindSpans(trimmed);
            if (spans.Count > 0 && spans[0].Index == 0)
            {
                foodText = trimmed[spans[0].Length..].Trim();
            }
        }
        else
        {
            var pair = FindAlternative(trimmed);
            if (pair is not null)
            {
                primary = pair.Value.Primary;
                alternative = pair.Value.Alternative;
                var spans = FindSpans(trimmed);
                if (spans.Count > 0 && spans[0].Index == 0)
                {
                    foodText = trimmed[spans[0].Length..].Trim();
                }
            }
            else
            {
                var spans = FindSpans(trimmed);
                if (spans.Count > 0 && spans[0].Index == 0)
                {
                    primary = spans[0].Measurement;
                    alternative = spans[0].Alternative;
                    foodText = trimmed[spans[0].Length..].Trim();
                }
            }
        }

        foodText = Regex
            .Replace(
                foodText,
                @"^\((?:about\s+|approx\.?\s+)?[^()]+\)\s*",
                "",
                RegexOptions.IgnoreCase
            )
            .Trim();

        string displayName;
        string? prepNote = null;

        var commaIndex = foodText.IndexOf(',');
        if (commaIndex >= 0)
        {
            displayName = foodText[..commaIndex].Trim();
            prepNote = foodText[(commaIndex + 1)..].Trim();
        }
        else
        {
            var prepMatch = Regex.Match(
                foodText,
                @"\b(?<prep>divided|drained|minced|chopped|sliced|grated|peeled|rinsed|crushed|halved|trimmed|cut|beaten|thawed)\b.*",
                RegexOptions.IgnoreCase
            );
            if (prepMatch.Success && prepMatch.Index > 0)
            {
                displayName = foodText[..prepMatch.Index].Trim();
                prepNote = prepMatch.Value.Trim();
            }
            else
            {
                displayName = foodText.Trim();
            }
        }

        var isOptional = Regex.IsMatch(line, @"\boptional\b", RegexOptions.IgnoreCase);
        if (isOptional && prepNote is not null)
        {
            prepNote = Regex
                .Replace(prepNote, @"\(?\s*optional\s*\)?", "", RegexOptions.IgnoreCase)
                .Trim(' ', ',', ';', '.');
            if (string.IsNullOrWhiteSpace(prepNote))
                prepNote = null;
        }

        var isStaple = Regex.IsMatch(
            displayName,
            @"\b(?:salt|water|black\s+pepper|pepper|flour|sugar|olive\s+oil|vegetable\s+oil)\b",
            RegexOptions.IgnoreCase
        );

        LlmExtractedMeasurementItem? altItem = alternative is not null
            ? new LlmExtractedMeasurementItem(
                alternative.Quantity,
                alternative.Unit == MeasurementUnit.Custom
                    ? alternative.CustomUnitName
                    : alternative.Unit?.ToString().ToLowerInvariant(),
                alternative.MaxQuantity
            )
            : null;

        return new LlmExtractedIngredientItem(
            SectionName: sectionName,
            Quantity: primary?.Quantity,
            MaxQuantity: primary?.MaxQuantity,
            Unit: primary?.Unit == MeasurementUnit.Custom
                ? primary.CustomUnitName
                : primary?.Unit?.ToString().ToLowerInvariant(),
            CanonicalFoodName: displayName,
            DisplayName: displayName,
            PreparationNote: string.IsNullOrWhiteSpace(prepNote) ? null : prepNote,
            IsOptional: isOptional,
            OriginalRawText: line,
            SuggestedIsPantryStaple: isStaple,
            AlternativeMeasurement: altItem,
            CustomUnitName: primary?.Unit == MeasurementUnit.Custom ? primary.CustomUnitName : null
        );
    }

    public static RecipeMeasurement? FindContainerUnitSize(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;
        var match = ContainerUnitRegex.Match(source);
        if (!match.Success)
            return null;
        return ParseMatch(match);
    }

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
            if (EquipmentSuffix.IsMatch(text[end..]))
            {
                continue;
            }
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
                && (
                    matches[i + 1].Value!.Unit != primary.Unit
                    || matches[i + 1].Value!.CustomUnitName != primary.CustomUnitName
                )
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
        if (primaryMatch.Success && primaryMatch.Index == 0)
        {
            var remaining = trimmed[(primaryMatch.Index + primaryMatch.Length)..];
            var marker = AlternativeStart.Match(remaining);
            if (marker.Success)
            {
                var alternativeMatch = Measurement.Match(remaining, marker.Index + marker.Length);
                if (
                    alternativeMatch.Success
                    && alternativeMatch.Index == marker.Index + marker.Length
                )
                {
                    var isParen = marker.Value.TrimStart().StartsWith('(');
                    var afterAlt = remaining[(alternativeMatch.Index + alternativeMatch.Length)..];
                    var isImmediateOr =
                        !isParen && marker.Index == remaining.Length - remaining.TrimStart().Length;
                    var isValidParen = isParen && afterAlt.TrimStart().StartsWith(')');
                    if (isImmediateOr || isValidParen)
                    {
                        var primary = ParseMatch(primaryMatch);
                        var alternative = ParseMatch(alternativeMatch);
                        if (
                            primary is not null
                            && alternative is not null
                            && (
                                primary.Unit != alternative.Unit
                                || primary.CustomUnitName != alternative.CustomUnitName
                            )
                        )
                        {
                            return (primary, alternative);
                        }
                    }
                }
            }
        }

        // Secondary scan: check parenthetical expressions anywhere in the line
        RecipeMeasurement? basePrimary = null;
        int searchStart = 0;
        if (primaryMatch.Success && primaryMatch.Index == 0)
        {
            basePrimary = ParseMatch(primaryMatch);
            searchStart = primaryMatch.Length;
        }
        else
        {
            var countMatch = LeadingCount.Match(trimmed);
            if (countMatch.Success)
            {
                var qty = FlexibleNullableDecimalJsonConverter.ParseNumber(
                    countMatch.Groups["quantity"].Value
                );
                var max = countMatch.Groups["maximum"].Success
                    ? FlexibleNullableDecimalJsonConverter.ParseNumber(
                        countMatch.Groups["maximum"].Value
                    )
                    : null;
                if (qty is >= 0.0001m and <= 999999.9999m)
                {
                    basePrimary = new RecipeMeasurement(qty, MeasurementUnit.Whole, max);
                    searchStart = countMatch.Length;
                }
            }
        }

        if (basePrimary is null)
            return null;

        var parenMatches = ParentheticalAlternative.Matches(trimmed);
        foreach (Match paren in parenMatches)
        {
            if (paren.Index < searchStart)
                continue;
            // Skip container multipliers like "2 (15-ounce) cans"
            var afterParen = trimmed[(paren.Index + paren.Length)..].TrimStart();
            if (Regex.IsMatch(afterParen, @"^cans?\b", RegexOptions.IgnoreCase))
                continue;

            var altText = paren.Groups["alt"].Value.Trim();
            var segments = Regex.Split(altText, @"\s*\bor\s+", RegexOptions.IgnoreCase);
            foreach (var segment in segments)
            {
                var cleanSeg = segment.Trim();
                var altMatch = Measurement.Match(cleanSeg);
                if (altMatch.Success && altMatch.Length == cleanSeg.Length)
                {
                    var alt = ParseMatch(altMatch);
                    if (
                        alt is not null
                        && (
                            alt.Unit != basePrimary.Unit
                            || alt.CustomUnitName != basePrimary.CustomUnitName
                        )
                    )
                    {
                        return (basePrimary, alt);
                    }
                }
            }
        }

        return null;
    }

    public static IReadOnlyList<RecipeMeasurement> FindAllConfirmableMeasurements(string? source)
    {
        var result = new List<RecipeMeasurement>();
        if (string.IsNullOrWhiteSpace(source))
            return result;

        foreach (var span in FindSpans(source))
        {
            result.Add(span.Measurement);
            if (span.Alternative is not null)
                result.Add(span.Alternative);
        }

        foreach (var m in Parse(source))
        {
            if (!result.Any(r => RecipeDraftNormalizer.MatchesMeasurement(r, m)))
                result.Add(m);
        }

        // Check parentheticals for "or" variants e.g. "(2 1/2 cups or 5 ounces)"
        var parenMatches = Regex.Matches(source, @"\((?<content>[^()]+)\)");
        foreach (Match paren in parenMatches)
        {
            var content = paren.Groups["content"].Value;
            var segments = Regex.Split(content, @"\s*\bor\s+", RegexOptions.IgnoreCase);
            if (segments.Length > 1)
            {
                foreach (var seg in segments)
                {
                    foreach (var m in Parse(seg))
                    {
                        if (!result.Any(r => RecipeDraftNormalizer.MatchesMeasurement(r, m)))
                            result.Add(m);
                    }
                }
            }
        }

        // Check container or piece size multiplier: e.g. "8 (5- to 6-ounce) chicken thighs" or "1 (14.5-ounce) can"
        var trimmed = source.TrimStart(' ', '\t', '*', '•');
        var countMatch = LeadingCount.Match(trimmed);
        if (countMatch.Success)
        {
            var count = FlexibleNullableDecimalJsonConverter.ParseNumber(
                countMatch.Groups["quantity"].Value
            );
            if (count is > 0)
            {
                foreach (Match paren in parenMatches)
                {
                    var parenMeasurements = Parse(paren.Groups["content"].Value);
                    foreach (var pm in parenMeasurements)
                    {
                        if (
                            pm.Quantity.HasValue
                            && pm.Unit.HasValue
                            && pm.Unit != MeasurementUnit.Whole
                        )
                        {
                            var multiplied = new RecipeMeasurement(
                                pm.Quantity.Value * count.Value,
                                pm.Unit,
                                pm.MaxQuantity.HasValue ? pm.MaxQuantity.Value * count.Value : null,
                                pm.CustomUnitName
                            );
                            if (
                                !result.Any(r =>
                                    RecipeDraftNormalizer.MatchesMeasurement(r, multiplied)
                                )
                            )
                                result.Add(multiplied);
                        }
                    }
                }
            }
        }

        return result;
    }

    private static RecipeMeasurement? ParseMatch(Match match)
    {
        var quantity = FlexibleNullableDecimalJsonConverter.ParseNumber(
            match.Groups["quantity"].Value
        );
        var maximum = match.Groups["maximum"].Success
            ? FlexibleNullableDecimalJsonConverter.ParseNumber(match.Groups["maximum"].Value)
            : null;
        var rawUnit = Regex
            .Replace(match.Groups["unit"].Value.Replace(".", ""), @"\s+", " ")
            .Trim();
        var (unit, custom) = RecipeDraftNormalizer.NormalizeUnit(rawUnit);
        var isStick =
            string.Equals(rawUnit, "stick", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rawUnit, "sticks", StringComparison.OrdinalIgnoreCase)
            || string.Equals(custom, "stick", StringComparison.OrdinalIgnoreCase);

        return
            quantity is >= 0.0001m and <= 999999.9999m
            && (!maximum.HasValue || (maximum >= quantity && maximum <= 999999.9999m))
            && unit.HasValue
            && (unit != MeasurementUnit.Custom || isStick)
            ? new RecipeMeasurement(quantity, unit, maximum, isStick ? "stick" : custom)
            : null;
    }
}

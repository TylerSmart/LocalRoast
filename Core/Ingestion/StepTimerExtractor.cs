using System.Globalization;
using System.Text.RegularExpressions;
using App.Core.Recipes;

namespace App.Core.Ingestion;

public static class StepTimerExtractor
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private const string ExtractionVerbsPattern =
        @"bake|simmer|cook|boil|roast|fry|chill|rest|marinate|steep|refrigerate|cool";

    private const string ActionVerbsPattern =
        @"bake|simmer|cook|boil|roast|fry|chill|rest|marinate|steep|refrigerate|cool|saut[eé]|brown|steam|whisk|beat|knead|rise|proof|toast|microwave|reduce|sear|soak|warm|stand|sit";

    private static readonly Regex ActionVerbRegex = new(
        $@"\b({ActionVerbsPattern})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout
    );

    private static readonly Regex DurationLabelRegex = new(
        @"^\s*(?:about|approx\.?|approximately)?\s*(?:\d+(?:\.\d+)?|\d+\s*(?:to|[-\u2013\u2014])\s*\d+)\s*(?:seconds?|secs?|s|minutes?|mins?|m|hours?|hrs?|h)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout
    );

    private static readonly Regex VerbWithDurationRegex = new(
        $@"^([a-zA-Z\u00C0-\u017F]+)\s+(?:for\s+)?(?:about\s+|approx\.?\s*)?(?:\d+(?:\.\d+)?|\d+\s*(?:to|[-\u2013\u2014])\s*\d+)\s*(?:seconds?|secs?|s|minutes?|mins?|m|hours?|hrs?|h)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout
    );

    private static readonly Regex CompositeHoursMinutesRegex = new(
        $@"(?:\b({ExtractionVerbsPattern})\b[^\.\n;]{{0,30}}?)?(?:for\s+)?(\d+)\s*(?:hours?|hrs?|h)\s*(?:and\s+)?(\d+)\s*(?:minutes?|mins?|m)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout
    );

    private static readonly Regex RangeDurationRegex = new(
        $@"(?:\b({ExtractionVerbsPattern})\b[^\.\n;]{{0,30}}?)?(?:for\s+)?(\d+(?:\.\d+)?)\s*(?:[-\u2013\u2014]|to)\s*(\d+(?:\.\d+)?)\s*(hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout
    );

    private static readonly Regex SingleDurationRegex = new(
        $@"(?:\b({ExtractionVerbsPattern})\b[^\.\n;]{{0,30}}?)?(?:for\s+)?(\d+(?:\.\d+)?)\s*(hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout
    );

    public static IReadOnlyList<RecipeStepTimerInput> DetectTimers(
        string? instruction,
        int stepNumber = 1
    )
    {
        if (string.IsNullOrWhiteSpace(instruction))
        {
            return [];
        }

        var results = new List<RecipeStepTimerInput>();
        var matchedSpans = new List<(int Start, int End)>();

        void TryAddTimer(int seconds, string? verb)
        {
            if (seconds < 1 || seconds > 864000)
            {
                return;
            }

            var label = CleanVerbLabel(verb, stepNumber);
            var baseLabel = label;
            var suffix = 1;
            while (
                results.Any(timer => timer.Label.Equals(label, StringComparison.OrdinalIgnoreCase))
            )
            {
                label = $"{baseLabel} {++suffix}";
            }
            results.Add(new RecipeStepTimerInput(seconds, label));
        }

        // 1. Composite (e.g. 1 hour and 15 minutes)
        foreach (Match match in CompositeHoursMinutesRegex.Matches(instruction))
        {
            var verb = match.Groups[1].Success ? match.Groups[1].Value : null;
            if (
                int.TryParse(
                    match.Groups[2].Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var hours
                )
                && int.TryParse(
                    match.Groups[3].Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var minutes
                )
            )
            {
                var totalSeconds = (hours * 3600) + (minutes * 60);
                TryAddTimer(totalSeconds, verb);
                matchedSpans.Add((match.Index, match.Index + match.Length));
            }
        }

        // 2. Range (e.g. 25-30 minutes) -> use lower bound
        foreach (Match match in RangeDurationRegex.Matches(instruction))
        {
            if (matchedSpans.Any(s => match.Index >= s.Start && match.Index < s.End))
            {
                continue;
            }

            var verb = match.Groups[1].Success ? match.Groups[1].Value : null;
            if (
                double.TryParse(
                    match.Groups[2].Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var lowerVal
                )
            )
            {
                var unitStr = match.Groups[4].Value;
                var totalSeconds = ConvertToSeconds(lowerVal, unitStr);
                if (totalSeconds.HasValue)
                {
                    TryAddTimer(totalSeconds.Value, verb);
                    matchedSpans.Add((match.Index, match.Index + match.Length));
                }
            }
        }

        // 3. Single (e.g. 15 minutes, 1.5 hours, 45 seconds)
        foreach (Match match in SingleDurationRegex.Matches(instruction))
        {
            if (matchedSpans.Any(s => match.Index >= s.Start && match.Index < s.End))
            {
                continue;
            }

            var verb = match.Groups[1].Success ? match.Groups[1].Value : null;
            if (
                double.TryParse(
                    match.Groups[2].Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var val
                )
            )
            {
                var unitStr = match.Groups[3].Value;
                var totalSeconds = ConvertToSeconds(val, unitStr);
                if (totalSeconds.HasValue)
                {
                    TryAddTimer(totalSeconds.Value, verb);
                }
            }
        }

        return results;
    }

    private static int? ConvertToSeconds(double value, string unit)
    {
        var lower = unit.ToLowerInvariant();
        if (lower.StartsWith("h")) // hour, hours, hr, hrs, h
        {
            return (int)Math.Round(value * 3600);
        }
        if (lower.StartsWith("m")) // minute, minutes, min, mins, m
        {
            return (int)Math.Round(value * 60);
        }
        if (lower.StartsWith("s")) // second, seconds, sec, secs, s
        {
            return (int)Math.Round(value);
        }
        return null;
    }

    public static int UseRangeLowerBound(int durationSeconds, string instruction)
    {
        var ranges = RangeDurationRegex
            .Matches(instruction)
            .Select(match =>
                (
                    Lower: ConvertToSeconds(
                        double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                        match.Groups[4].Value
                    ),
                    Upper: ConvertToSeconds(
                        double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
                        match.Groups[4].Value
                    )
                )
            )
            .ToList();
        // A duration that is also another phase's lower bound is ambiguous; leave it unchanged.
        if (ranges.Any(range => range.Lower == durationSeconds))
        {
            return durationSeconds;
        }
        var lowerBounds = ranges
            .Where(range =>
                range.Upper == durationSeconds && range.Lower is > 0 && range.Lower < range.Upper
            )
            .Select(range => range.Lower!.Value)
            .Distinct()
            .ToList();
        return lowerBounds.Count == 1 ? lowerBounds[0] : durationSeconds;
    }

    public static bool IsDurationLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        return DurationLabelRegex.IsMatch(label.Trim());
    }

    public static string CleanVerbLabel(string? verb, int stepNumber)
    {
        if (string.IsNullOrWhiteSpace(verb))
        {
            return $"Step {stepNumber} timer";
        }

        var trimmed = verb.Trim().ToLowerInvariant();
        return trimmed switch
        {
            "sit" or "stand" => "Rest",
            "refrigerate" => "Chill",
            "proof" => "Rise",
            "saute" => "Sauté",
            _ => char.ToUpperInvariant(trimmed[0]) + trimmed[1..],
        };
    }

    public static string? FindActionVerbInInstruction(string? instruction, int? nearOffset = null)
    {
        if (string.IsNullOrWhiteSpace(instruction))
        {
            return null;
        }

        var text =
            nearOffset.HasValue && nearOffset.Value > 0 && nearOffset.Value <= instruction.Length
                ? instruction[..nearOffset.Value]
                : instruction;

        var matches = ActionVerbRegex.Matches(text);
        if (matches.Count > 0)
        {
            return matches[^1].Groups[1].Value;
        }

        return null;
    }

    public static string CleanTimerLabel(
        string? label,
        string? instruction = null,
        int stepNumber = 1,
        int? timerSeconds = null
    )
    {
        var trimmed = label?.Trim();

        // 1. If it's a verb with trailing duration (e.g. "Simmer for 5 minutes")
        if (!string.IsNullOrEmpty(trimmed))
        {
            var verbMatch = VerbWithDurationRegex.Match(trimmed);
            if (verbMatch.Success && ActionVerbRegex.IsMatch(verbMatch.Groups[1].Value))
            {
                return CleanVerbLabel(verbMatch.Groups[1].Value, stepNumber);
            }
        }

        // 2. If it's not a pure duration label and not empty, preserve it
        if (!string.IsNullOrWhiteSpace(trimmed) && !IsDurationLabel(trimmed))
        {
            return trimmed;
        }

        // 3. It's empty or a pure duration label; find a verb in instruction
        int? offset = null;
        if (!string.IsNullOrWhiteSpace(instruction) && timerSeconds.HasValue)
        {
            var tokenIdx = instruction.IndexOf(
                $"\"seconds\": {timerSeconds.Value}",
                StringComparison.OrdinalIgnoreCase
            );
            if (tokenIdx < 0)
            {
                tokenIdx = instruction.IndexOf(
                    $"\"seconds\":{timerSeconds.Value}",
                    StringComparison.OrdinalIgnoreCase
                );
            }
            if (tokenIdx >= 0)
            {
                offset = tokenIdx;
            }
        }

        var verb = FindActionVerbInInstruction(instruction, offset);
        return CleanVerbLabel(verb, stepNumber);
    }
}

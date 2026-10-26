using System.Globalization;
using System.Text.RegularExpressions;
using App.Core.Recipes;

namespace App.Core.Ingestion;

public static class StepTimerExtractor
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex CompositeHoursMinutesRegex = new(
        @"(?:\b(bake|simmer|cook|boil|roast|fry|chill|rest|marinate|steep|refrigerate|cool)\b[^\.\n;]{0,30}?)?(?:for\s+)?(\d+)\s*(?:hours?|hrs?|h)\s*(?:and\s+)?(\d+)\s*(?:minutes?|mins?|m)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout
    );

    private static readonly Regex RangeDurationRegex = new(
        @"(?:\b(bake|simmer|cook|boil|roast|fry|chill|rest|marinate|steep|refrigerate|cool)\b[^\.\n;]{0,30}?)?(?:for\s+)?(\d+(?:\.\d+)?)\s*(?:-|to)\s*(\d+(?:\.\d+)?)\s*(hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout
    );

    private static readonly Regex SingleDurationRegex = new(
        @"(?:\b(bake|simmer|cook|boil|roast|fry|chill|rest|marinate|steep|refrigerate|cool)\b[^\.\n;]{0,30}?)?(?:for\s+)?(\d+(?:\.\d+)?)\s*(hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)\b",
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
        var seenSeconds = new HashSet<int>();
        var matchedSpans = new List<(int Start, int End)>();

        void TryAddTimer(int seconds, string? verb)
        {
            if (seconds < 1 || seconds > 864000 || seenSeconds.Contains(seconds))
            {
                return;
            }

            seenSeconds.Add(seconds);
            var label = CleanVerbLabel(verb, stepNumber);
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

    private static string CleanVerbLabel(string? verb, int stepNumber)
    {
        if (string.IsNullOrWhiteSpace(verb))
        {
            return $"Step {stepNumber} timer";
        }

        var trimmed = verb.Trim();
        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..].ToLowerInvariant();
    }
}

namespace App.Core.Ingestion;

/// <summary>
/// Provides utilities for calculating daily LLM quota reset boundaries and current cycle dates
/// across configurable provider timezones and reset times of day (e.g. Google AI Studio resetting at midnight Pacific Time).
/// </summary>
public static class LlmQuotaSchedule
{
    public const string DefaultTimezone = "UTC";
    public const string DefaultResetTime = "00:00";
    public const string PacificTimezone = "America/Los_Angeles";

    public static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Utc;
        }

        if (string.Equals(timeZoneId, "Local", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    public static TimeOnly ResolveResetTime(string? resetTimeStr)
    {
        if (
            !string.IsNullOrWhiteSpace(resetTimeStr)
            && TimeOnly.TryParse(resetTimeStr, out var parsed)
        )
        {
            return parsed;
        }

        return TimeOnly.MinValue; // Default to 00:00 (midnight)
    }

    /// <summary>
    /// Calculates the next upcoming reset instant in UTC based on the provider's timezone and reset time of day.
    /// </summary>
    public static DateTimeOffset GetNextResetUtc(
        DateTimeOffset nowUtc,
        string? timeZoneId,
        string? resetTimeStr
    )
    {
        var tz = ResolveTimeZone(timeZoneId);
        var resetTime = ResolveResetTime(resetTimeStr);

        var localNow = TimeZoneInfo.ConvertTime(nowUtc, tz);
        var resetTodayLocal = localNow.Date.Add(resetTime.ToTimeSpan());

        var nextResetLocal =
            localNow < resetTodayLocal ? resetTodayLocal : resetTodayLocal.AddDays(1);

        var nextResetUtc = TimeZoneInfo.ConvertTimeToUtc(nextResetLocal, tz);
        return new DateTimeOffset(nextResetUtc, TimeSpan.Zero);
    }

    /// <summary>
    /// Returns the calendar date representing the current quota window in the provider's configured schedule.
    /// If local time has not yet reached today's reset time, the window still belongs to yesterday's date.
    /// </summary>
    public static DateOnly GetCurrentQuotaDate(
        DateTimeOffset nowUtc,
        string? timeZoneId,
        string? resetTimeStr
    )
    {
        var tz = ResolveTimeZone(timeZoneId);
        var resetTime = ResolveResetTime(resetTimeStr);

        var localNow = TimeZoneInfo.ConvertTime(nowUtc, tz);
        var resetTodayLocal = localNow.Date.Add(resetTime.ToTimeSpan());

        return localNow >= resetTodayLocal
            ? DateOnly.FromDateTime(localNow.Date)
            : DateOnly.FromDateTime(localNow.Date.AddDays(-1));
    }
}

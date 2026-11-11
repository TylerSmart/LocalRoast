using App.Core.Ingestion;

namespace App.Core.UnitTests;

[TestClass]
public sealed class LlmQuotaScheduleTests
{
    [TestMethod]
    public void ResolveTimeZone_ValidIana_ResolvesSuccessfully()
    {
        var tz = LlmQuotaSchedule.ResolveTimeZone("America/Los_Angeles");
        Assert.IsNotNull(tz);
        Assert.IsTrue(
            tz.Id.Contains("Pacific", StringComparison.OrdinalIgnoreCase)
                || tz.Id.Equals("America/Los_Angeles", StringComparison.OrdinalIgnoreCase)
        );
    }

    [TestMethod]
    public void ResolveTimeZone_UtcOrEmpty_ReturnsUtc()
    {
        Assert.AreEqual(TimeZoneInfo.Utc, LlmQuotaSchedule.ResolveTimeZone("UTC"));
        Assert.AreEqual(TimeZoneInfo.Utc, LlmQuotaSchedule.ResolveTimeZone(null));
        Assert.AreEqual(TimeZoneInfo.Utc, LlmQuotaSchedule.ResolveTimeZone(""));
        Assert.AreEqual(
            TimeZoneInfo.Utc,
            LlmQuotaSchedule.ResolveTimeZone("Invalid/NonExistent_Zone")
        );
    }

    [TestMethod]
    public void ResolveTimeZone_Local_ReturnsLocal()
    {
        var tz = LlmQuotaSchedule.ResolveTimeZone("Local");
        Assert.AreEqual(TimeZoneInfo.Local, tz);
    }

    [TestMethod]
    public void ResolveResetTime_ValidAndInvalid_ParsesOrDefaults()
    {
        Assert.AreEqual(new TimeOnly(8, 30), LlmQuotaSchedule.ResolveResetTime("08:30"));
        Assert.AreEqual(new TimeOnly(0, 0), LlmQuotaSchedule.ResolveResetTime("00:00"));
        Assert.AreEqual(TimeOnly.MinValue, LlmQuotaSchedule.ResolveResetTime("not-a-time"));
        Assert.AreEqual(TimeOnly.MinValue, LlmQuotaSchedule.ResolveResetTime(null));
    }

    [TestMethod]
    public void GetNextResetUtc_PacificMidnight_CalculatesCorrectUtcInstant()
    {
        // 2026-11-11 02:00:00 UTC is 2026-11-10 18:00:00 PST (UTC-8)
        var nowUtc = new DateTimeOffset(2026, 11, 11, 2, 0, 0, TimeSpan.Zero);

        var nextResetUtc = LlmQuotaSchedule.GetNextResetUtc(
            nowUtc,
            LlmQuotaSchedule.PacificTimezone,
            "00:00"
        );

        // Next midnight in Pacific is 2026-11-11 00:00:00 PST = 2026-11-11 08:00:00 UTC
        Assert.AreEqual(new DateTimeOffset(2026, 11, 11, 8, 0, 0, TimeSpan.Zero), nextResetUtc);
    }

    [TestMethod]
    public void GetNextResetUtc_JustAfterPacificMidnight_AdvancesToFollowingMidnight()
    {
        // 2026-11-11 08:05:00 UTC is 2026-11-11 00:05:00 PST (5 mins after midnight)
        var nowUtc = new DateTimeOffset(2026, 11, 11, 8, 5, 0, TimeSpan.Zero);

        var nextResetUtc = LlmQuotaSchedule.GetNextResetUtc(
            nowUtc,
            LlmQuotaSchedule.PacificTimezone,
            "00:00"
        );

        // Next midnight in Pacific is 2026-11-12 00:00:00 PST = 2026-11-12 08:00:00 UTC
        Assert.AreEqual(new DateTimeOffset(2026, 11, 12, 8, 0, 0, TimeSpan.Zero), nextResetUtc);
    }

    [TestMethod]
    public void GetCurrentQuotaDate_ReflectsConfiguredBoundary()
    {
        // In UTC with 00:00 reset
        var utcTime = new DateTimeOffset(2026, 11, 11, 15, 0, 0, TimeSpan.Zero);
        var quotaDate = LlmQuotaSchedule.GetCurrentQuotaDate(utcTime, "UTC", "00:00");
        Assert.AreEqual(new DateOnly(2026, 11, 11), quotaDate);

        // In Pacific time, 2026-11-11 02:00:00 UTC is still 2026-11-10 18:00:00 PST
        var ptTime = new DateTimeOffset(2026, 11, 11, 2, 0, 0, TimeSpan.Zero);
        var ptQuotaDate = LlmQuotaSchedule.GetCurrentQuotaDate(
            ptTime,
            LlmQuotaSchedule.PacificTimezone,
            "00:00"
        );
        Assert.AreEqual(new DateOnly(2026, 11, 10), ptQuotaDate);
    }
}

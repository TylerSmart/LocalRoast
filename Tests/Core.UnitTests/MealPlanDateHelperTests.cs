using App.Core.Exceptions;
using App.Core.MealPlans;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class MealPlanDateHelperTests
{
    [TestMethod]
    public void NormalizeToSunday_ReturnsCorrectSunday()
    {
        // 2026-11-01 is Sunday
        var sunday = new DateOnly(2026, 11, 1);
        Assert.AreEqual(sunday, MealPlanDateHelper.NormalizeToSunday(sunday));

        // 2026-11-04 is Wednesday -> should normalize to 2026-11-01
        var wednesday = new DateOnly(2026, 11, 4);
        Assert.AreEqual(sunday, MealPlanDateHelper.NormalizeToSunday(wednesday));

        // 2026-11-07 is Saturday -> should normalize to 2026-11-01
        var saturday = new DateOnly(2026, 11, 7);
        Assert.AreEqual(sunday, MealPlanDateHelper.NormalizeToSunday(saturday));

        // 2026-03-04 is Wednesday -> previous Sunday was 2026-03-01
        var march4 = new DateOnly(2026, 3, 4);
        Assert.AreEqual(new DateOnly(2026, 3, 1), MealPlanDateHelper.NormalizeToSunday(march4));
    }

    [TestMethod]
    public void GetDayOffsetFromSunday_ReturnsZeroToSix()
    {
        // 2026-11-01 is Sunday
        var sunday = new DateOnly(2026, 11, 1);
        Assert.AreEqual(0, MealPlanDateHelper.GetDayOffsetFromSunday(sunday));
        Assert.AreEqual(1, MealPlanDateHelper.GetDayOffsetFromSunday(sunday.AddDays(1)));
        Assert.AreEqual(2, MealPlanDateHelper.GetDayOffsetFromSunday(sunday.AddDays(2)));
        Assert.AreEqual(3, MealPlanDateHelper.GetDayOffsetFromSunday(sunday.AddDays(3)));
        Assert.AreEqual(4, MealPlanDateHelper.GetDayOffsetFromSunday(sunday.AddDays(4)));
        Assert.AreEqual(5, MealPlanDateHelper.GetDayOffsetFromSunday(sunday.AddDays(5)));
        Assert.AreEqual(6, MealPlanDateHelper.GetDayOffsetFromSunday(sunday.AddDays(6)));
    }

    [TestMethod]
    public void GetMonthGridBounds_ValidMonth_ReturnsSundayStartAndSaturdayEnd()
    {
        var (gridStart, gridEnd) = MealPlanDateHelper.GetMonthGridBounds(2026, 11);

        Assert.AreEqual(DayOfWeek.Sunday, gridStart.DayOfWeek);
        Assert.AreEqual(DayOfWeek.Saturday, gridEnd.DayOfWeek);
        Assert.IsTrue(gridStart <= new DateOnly(2026, 11, 1));
        Assert.IsTrue(gridEnd >= new DateOnly(2026, 11, 30));
    }

    [TestMethod]
    public void GetMonthGridBounds_InvalidYearOrMonth_ThrowsDomainValidationException()
    {
        Assert.Throws<DomainValidationException>(() =>
            MealPlanDateHelper.GetMonthGridBounds(1999, 5)
        );
        Assert.Throws<DomainValidationException>(() =>
            MealPlanDateHelper.GetMonthGridBounds(2101, 5)
        );
        Assert.Throws<DomainValidationException>(() =>
            MealPlanDateHelper.GetMonthGridBounds(2026, 0)
        );
        Assert.Throws<DomainValidationException>(() =>
            MealPlanDateHelper.GetMonthGridBounds(2026, 13)
        );
    }
}

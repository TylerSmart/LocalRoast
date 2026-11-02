using App.Core.Exceptions;
using App.Core.MealPlans;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class MealPlanDateHelperTests
{
    [TestMethod]
    public void NormalizeToMonday_ReturnsCorrectMonday()
    {
        // 2026-11-02 is Monday
        var monday = new DateOnly(2026, 11, 2);
        Assert.AreEqual(monday, MealPlanDateHelper.NormalizeToMonday(monday));

        // 2026-11-04 is Wednesday -> should normalize to 2026-11-02
        var wednesday = new DateOnly(2026, 11, 4);
        Assert.AreEqual(monday, MealPlanDateHelper.NormalizeToMonday(wednesday));

        // 2026-11-08 is Sunday -> should normalize to 2026-11-02
        var sunday = new DateOnly(2026, 11, 8);
        Assert.AreEqual(monday, MealPlanDateHelper.NormalizeToMonday(sunday));

        // 2026-03-01 is Sunday -> previous Monday was 2026-02-23
        var march1 = new DateOnly(2026, 3, 1);
        Assert.AreEqual(new DateOnly(2026, 2, 23), MealPlanDateHelper.NormalizeToMonday(march1));
    }

    [TestMethod]
    public void GetDayOffsetFromMonday_ReturnsZeroToSix()
    {
        // 2026-11-02 is Monday
        var monday = new DateOnly(2026, 11, 2);
        Assert.AreEqual(0, MealPlanDateHelper.GetDayOffsetFromMonday(monday));
        Assert.AreEqual(1, MealPlanDateHelper.GetDayOffsetFromMonday(monday.AddDays(1)));
        Assert.AreEqual(2, MealPlanDateHelper.GetDayOffsetFromMonday(monday.AddDays(2)));
        Assert.AreEqual(3, MealPlanDateHelper.GetDayOffsetFromMonday(monday.AddDays(3)));
        Assert.AreEqual(4, MealPlanDateHelper.GetDayOffsetFromMonday(monday.AddDays(4)));
        Assert.AreEqual(5, MealPlanDateHelper.GetDayOffsetFromMonday(monday.AddDays(5)));
        Assert.AreEqual(6, MealPlanDateHelper.GetDayOffsetFromMonday(monday.AddDays(6)));
    }

    [TestMethod]
    public void GetMonthGridBounds_ValidMonth_ReturnsMondayStartAndSundayEnd()
    {
        var (gridStart, gridEnd) = MealPlanDateHelper.GetMonthGridBounds(2026, 11);

        Assert.AreEqual(DayOfWeek.Monday, gridStart.DayOfWeek);
        Assert.AreEqual(DayOfWeek.Sunday, gridEnd.DayOfWeek);
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

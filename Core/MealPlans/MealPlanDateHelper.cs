using App.Core.Exceptions;

namespace App.Core.MealPlans;

public static class MealPlanDateHelper
{
    public static DateOnly NormalizeToMonday(DateOnly date)
    {
        int diff = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return date.AddDays(-diff);
    }

    public static int GetDayOffsetFromMonday(DateOnly date)
    {
        return ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
    }

    public static (DateOnly GridStartMonday, DateOnly GridEndSunday) GetMonthGridBounds(
        int year,
        int month
    )
    {
        if (year is < 2000 or > 2100)
        {
            throw new DomainValidationException("Year must be between 2000 and 2100.");
        }

        if (month is < 1 or > 12)
        {
            throw new DomainValidationException("Month must be between 1 and 12.");
        }

        var firstOfMonth = new DateOnly(year, month, 1);
        var lastOfMonth = new DateOnly(year, month, DateTime.DaysInMonth(year, month));

        return (NormalizeToMonday(firstOfMonth), NormalizeToMonday(lastOfMonth).AddDays(6));
    }
}

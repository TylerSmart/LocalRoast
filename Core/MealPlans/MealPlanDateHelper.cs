using App.Core.Exceptions;

namespace App.Core.MealPlans;

public static class MealPlanDateHelper
{
    public static DateOnly NormalizeToSunday(DateOnly date)
    {
        int diff = ((int)date.DayOfWeek - (int)DayOfWeek.Sunday + 7) % 7;
        return date.AddDays(-diff);
    }

    public static int GetDayOffsetFromSunday(DateOnly date)
    {
        return ((int)date.DayOfWeek - (int)DayOfWeek.Sunday + 7) % 7;
    }

    public static (DateOnly GridStartSunday, DateOnly GridEndSaturday) GetMonthGridBounds(
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

        return (NormalizeToSunday(firstOfMonth), NormalizeToSunday(lastOfMonth).AddDays(6));
    }
}

using System.Text.Json.Serialization;

namespace App.Core.MealPlans;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MealSlot
{
    Breakfast = 0,
    Lunch = 1,
    Dinner = 2,
    SnackPrep = 3,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MealPlanEntryType
{
    Recipe = 0,
    Leftovers = 1,
    FreeformNote = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApplyTemplateMode
{
    Merge = 0,
    Replace = 1,
}

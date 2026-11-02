using System.Text.Json.Serialization;

namespace App.Core.Shopping;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MeasurementDimension
{
    Volume = 0,
    Weight = 1,
    Discrete = 2,
    Unspecified = 3,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShoppingListSourceKind
{
    Manual = 0,
    MealPlanEntry = 1,
    SingleRecipe = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DuplicateMealPolicy
{
    SkipAlreadyImported = 0,
    ReplaceAlreadyImported = 1,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShoppingRealtimeEventType
{
    ListCreated = 0,
    ListUpdated = 1,
    ListDeleted = 2,
    ItemAdded = 3,
    ItemUpdated = 4,
    ItemCheckedChanged = 5,
    ItemPromoted = 6,
    ItemDeleted = 7,
    CheckedItemsCleared = 8,
    ItemsGenerated = 9,
}

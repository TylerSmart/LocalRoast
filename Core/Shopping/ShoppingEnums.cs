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

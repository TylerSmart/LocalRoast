using System.Text.Json.Serialization;

namespace App.Core.Recipes;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RecipeStatus
{
    DraftReview = 0,
    Published = 1,
    Archived = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RecipeDifficulty
{
    Easy = 0,
    Medium = 1,
    Project = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TagCategory
{
    Course = 0,
    Cuisine = 1,
    Dietary = 2,
    Equipment = 3,
    Custom = 4,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MeasurementUnit
{
    // Volume - US Customary
    Tsp = 0,
    Tbsp = 1,
    FlOz = 2,
    Cup = 3,
    Pt = 4,
    Qt = 5,
    Gal = 6,

    // Volume - Metric
    Ml = 7,
    L = 8,

    // Weight - US Customary
    Oz = 9,
    Lb = 10,

    // Weight - Metric
    G = 11,
    Kg = 12,

    // Culinary / Count / Custom (non-convertible across US/Metric)
    Pinch = 13,
    Dash = 14,
    Clove = 15,
    Can = 16,
    Bunch = 17,
    Slice = 18,
    Whole = 19,
    Custom = 20,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UnitSystem
{
    Original = 0,
    UsCustomary = 1,
    Metric = 2,
}

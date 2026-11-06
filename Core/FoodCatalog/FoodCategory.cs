using System.Text.Json.Serialization;

namespace App.Core.FoodCatalog;

/// <summary>
/// Fixed grocery-store categories used to group shopping lists. The declaration order is the
/// default store-walk order (perimeter fresh foods first, frozen last before "Other").
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FoodCategory
{
    Produce,
    MeatAndSeafood,
    DairyAndEggs,
    Refrigerated,
    Bakery,
    PantryDryGoods,
    CannedAndJarred,
    CondimentsAndSauces,
    SpicesAndSeasonings,
    Baking,
    Snacks,
    Beverages,
    Frozen,
    Other,
}

public static class FoodCategories
{
    public static readonly IReadOnlyList<FoodCategory> StoreWalkOrder =
        Enum.GetValues<FoodCategory>();

    public static string DisplayName(FoodCategory category) =>
        category switch
        {
            FoodCategory.Produce => "Produce",
            FoodCategory.MeatAndSeafood => "Meat & Seafood",
            FoodCategory.DairyAndEggs => "Dairy & Eggs",
            FoodCategory.Refrigerated => "Refrigerated",
            FoodCategory.Bakery => "Bakery & Bread",
            FoodCategory.PantryDryGoods => "Pantry & Dry Goods",
            FoodCategory.CannedAndJarred => "Canned & Jarred",
            FoodCategory.CondimentsAndSauces => "Condiments & Sauces",
            FoodCategory.SpicesAndSeasonings => "Spices & Seasonings",
            FoodCategory.Baking => "Baking",
            FoodCategory.Snacks => "Snacks",
            FoodCategory.Beverages => "Beverages",
            FoodCategory.Frozen => "Frozen",
            _ => "Other",
        };

    /// <summary>
    /// Tolerantly parses an LLM- or user-supplied category label ("Meat & Seafood",
    /// "meat_and_seafood", "spices", "dairy") into a <see cref="FoodCategory"/>.
    /// </summary>
    public static FoodCategory? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var key = new string(
            value
                .Replace("&", "and", StringComparison.Ordinal)
                .Where(char.IsLetter)
                .Select(char.ToLowerInvariant)
                .ToArray()
        );

        if (key.Length == 0)
        {
            return null;
        }

        foreach (var category in StoreWalkOrder)
        {
            var enumKey = category.ToString().ToLowerInvariant();
            var displayKey = new string(
                DisplayName(category)
                    .Replace("&", "and", StringComparison.Ordinal)
                    .Where(char.IsLetter)
                    .Select(char.ToLowerInvariant)
                    .ToArray()
            );
            if (key == enumKey || key == displayKey)
            {
                return category;
            }
        }

        return key switch
        {
            "fruit" or "fruits" or "vegetables" or "veggies" or "freshproduce" or "herbs" =>
                FoodCategory.Produce,
            "meat" or "meats" or "seafood" or "fish" or "poultry" or "butcher" or "deli" =>
                FoodCategory.MeatAndSeafood,
            "dairy" or "eggs" or "cheese" => FoodCategory.DairyAndEggs,
            "refrigerator" or "fridge" or "chilled" or "cold" => FoodCategory.Refrigerated,
            "bread" or "breads" or "bakeryandbread" => FoodCategory.Bakery,
            "pantry" or "drygoods" or "dryfoods" or "grains" or "pasta" or "pastaandgrains" =>
                FoodCategory.PantryDryGoods,
            "canned" or "cannedgoods" or "jarred" => FoodCategory.CannedAndJarred,
            "condiments" or "sauces" or "oilsandvinegars" or "oils" =>
                FoodCategory.CondimentsAndSauces,
            "spices" or "seasonings" or "spice" or "spicesandherbs" =>
                FoodCategory.SpicesAndSeasonings,
            "bakingsupplies" or "bakingandspices" => FoodCategory.Baking,
            "snack" or "sweets" or "candy" => FoodCategory.Snacks,
            "drinks" or "beverage" or "alcohol" or "wineandbeer" => FoodCategory.Beverages,
            "freezer" or "frozenfoods" => FoodCategory.Frozen,
            "misc" or "miscellaneous" or "uncategorized" or "unknown" or "none" =>
                FoodCategory.Other,
            _ => null,
        };
    }
}

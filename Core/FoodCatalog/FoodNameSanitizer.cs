namespace App.Core.FoodCatalog;

public static class FoodNameSanitizer
{
    private static readonly HashSet<string> DisallowedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "null",
        "none",
        "n/a",
        "na",
        "undefined",
        "unknown",
        "nil",
        "empty",
        "ingredient",
        "ingredients",
        "food",
        "food item",
        "item",
        "-",
        "--",
        "???",
        "various",
        "optional",
    };

    public static bool IsValidFoodName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var trimmed = name.Trim();
        if (trimmed.Length < 2 && !char.IsLetter(trimmed[0]))
        {
            return false;
        }

        return !DisallowedNames.Contains(trimmed);
    }

    public static string? SanitizeFoodName(string? name)
    {
        if (!IsValidFoodName(name))
        {
            return null;
        }

        return name!.Trim();
    }
}

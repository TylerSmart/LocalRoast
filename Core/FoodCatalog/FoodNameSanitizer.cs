using System.Text.RegularExpressions;

namespace App.Core.FoodCatalog;

public static partial class FoodNameSanitizer
{
    [GeneratedRegex(
        @"^(?:cold|warm|hot|lukewarm|tepid|boiling|iced|ice|chilled|cool|tap|filtered|room-temperature|room temperature)\s+water$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex WaterTemperaturePrefixRegex();

    [GeneratedRegex(
        @"^(?:jumbo|extra[- ]large|large|medium|small)\s+eggs?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex EggSizePrefixRegex();

    [GeneratedRegex(
        @"^(?:jumbo|extra[- ]large|large|medium|small)\s+egg\s+(?<part>whites?|yolks?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex EggPartSizePrefixRegex();

    [GeneratedRegex(
        @"^frozen\s+(?:chopped|diced|sliced|minced|cut|whole)\s+(?<item>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex FrozenPrepPrefixRegex();

    [GeneratedRegex(@"^garlic\s+cloves?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GarlicCloveRegex();

    [GeneratedRegex(
        @"^(?:table|fine)\s+salt$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex TableSaltRegex();

    [GeneratedRegex(
        @"^(?:freshly\s+)?(?:ground|cracked)\s+(?:black\s+)?pepper$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex GroundBlackPepperRegex();

    [GeneratedRegex(
        @"^(?<first>[A-Za-z][A-Za-z -]{1,30}?)\s+or\s+(?:[A-Za-z][A-Za-z -]{1,30}\s+)?(?<noun>oil|shortening|vinegar)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex OrAlternativeFatOrAcidRegex();

    private static readonly HashSet<string> StandaloneFatNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "butter",
        "ghee",
        "lard",
        "margarine",
        "shortening",
        "drippings",
    };

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

    /// <summary>
    /// Strips safe temperature/size/prep modifiers from canonical food names so minor recipe
    /// phrasing (e.g., "cold water", "large egg", "frozen chopped spinach") maps to clean catalog
    /// entries ("water", "egg", "frozen spinach") without altering distinct items like "hot sauce".
    /// </summary>
    public static string NormalizeCanonicalFoodName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var trimmed = name.Trim();
        if (WaterTemperaturePrefixRegex().IsMatch(trimmed))
        {
            return "water";
        }

        if (EggSizePrefixRegex().IsMatch(trimmed))
        {
            return "egg";
        }

        var eggPartMatch = EggPartSizePrefixRegex().Match(trimmed);
        if (eggPartMatch.Success)
        {
            return eggPartMatch
                .Groups["part"]
                .Value.StartsWith("white", StringComparison.OrdinalIgnoreCase)
                ? "egg white"
                : "egg yolk";
        }

        if (GarlicCloveRegex().IsMatch(trimmed))
        {
            return "garlic";
        }

        if (TableSaltRegex().IsMatch(trimmed))
        {
            return "salt";
        }

        if (GroundBlackPepperRegex().IsMatch(trimmed))
        {
            return "black pepper";
        }

        var orAltMatch = OrAlternativeFatOrAcidRegex().Match(trimmed);
        if (orAltMatch.Success)
        {
            var first = orAltMatch.Groups["first"].Value.Trim();
            var noun = orAltMatch.Groups["noun"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(first))
            {
                if (
                    first.EndsWith(" " + noun, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(first, noun, StringComparison.OrdinalIgnoreCase)
                    || StandaloneFatNames.Contains(first)
                )
                {
                    return first;
                }

                return $"{first} {noun}";
            }
        }

        var frozenMatch = FrozenPrepPrefixRegex().Match(trimmed);
        if (frozenMatch.Success)
        {
            var inner = frozenMatch.Groups["item"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(inner))
            {
                return $"frozen {inner}";
            }
        }

        return trimmed;
    }
}

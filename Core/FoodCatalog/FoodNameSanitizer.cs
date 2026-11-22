using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using App.Core.Recipes;

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

    [GeneratedRegex(
        @"^(?<state>jarred|pickled)\s+(?:sliced|chopped|diced|minced|cut|halved)\s+(?<item>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex JarredOrPickledPrepPrefixRegex();

    [GeneratedRegex(
        @"^dried\s+(?:minced|chopped|flaked)\s+(?<item>onions?|garlic)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex DriedPrepPrefixRegex();

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

        var driedMatch = DriedPrepPrefixRegex().Match(trimmed);
        if (driedMatch.Success)
        {
            return driedMatch
                .Groups["item"]
                .Value.StartsWith("onion", StringComparison.OrdinalIgnoreCase)
                ? "dried onion"
                : "dried garlic";
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

        var jarredMatch = JarredOrPickledPrepPrefixRegex().Match(trimmed);
        if (jarredMatch.Success)
        {
            var inner = jarredMatch.Groups["item"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(inner))
            {
                return $"{jarredMatch.Groups["state"].Value.ToLowerInvariant()} {inner}";
            }
        }

        return trimmed;
    }

    private static readonly HashSet<string> SkuPrepStripWords = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "minced",
        "chopped",
        "sliced",
        "thin",
        "thinly",
        "thick",
        "thickly",
        "finely",
        "coarsely",
        "roughly",
        "grated",
        "shredded",
        "melted",
        "softened",
        "chilled",
        "cold",
        "warm",
        "warmed",
        "lukewarm",
        "boiling",
        "tap",
        "filtered",
        "room",
        "temperature",
        "fresh",
        "freshly",
        "large",
        "medium",
        "small",
        "jumbo",
        "extra-large",
        "ripe",
        "firm",
        "peeled",
        "unpeeled",
        "seeded",
        "stemmed",
        "cored",
        "hulled",
        "trimmed",
        "halved",
        "quartered",
        "beaten",
        "whisked",
        "sifted",
        "drained",
        "rinsed",
        "scrubbed",
        "divided",
        "packed",
        "cooked",
        "lean",
        "percent",
        "bone",
        "boneless",
        "skin",
        "skinless",
        "stalk",
        "rib",
        "sprig",
        "floret",
        "wedge",
        "piece",
        "fillet",
        "cut",
    };

    private static readonly HashSet<string> SingularInvariantWords = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "hummus",
        "couscous",
        "asparagus",
        "citrus",
        "molasses",
        "watercress",
        "lemongrass",
        "Swiss",
        "paris",
    };

    private static readonly HashSet<string> DryGroundSpices = new(StringComparer.OrdinalIgnoreCase)
    {
        "cumin",
        "coriander",
        "cinnamon",
        "nutmeg",
        "allspice",
        "cardamom",
        "turmeric",
    };

    private static readonly HashSet<string> FreshPepperCueWords = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "bell",
        "sweet",
        "jalapeno",
        "serrano",
        "poblano",
        "habanero",
        "anaheim",
        "fresno",
        "thai",
        "chile",
        "chili",
        "banana",
        "green",
        "red",
        "yellow",
        "orange",
    };

    private static readonly HashSet<string> NonPlainMilkQualifiers = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "coconut",
        "buttermilk",
        "evaporated",
        "condensed",
        "skim",
        "low",
        "fat",
        "nonfat",
        "almond",
        "oat",
        "soy",
        "rice",
        "cashew",
        "hemp",
        "goat",
        "malted",
        "chocolate",
    };

    private static readonly HashSet<string> NonPlainButterQualifiers = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "salted",
        "cultured",
        "clarified",
        "brown",
        "peanut",
        "almond",
        "cashew",
        "sunflower",
        "apple",
        "pumpkin",
        "cocoa",
        "shea",
    };

    private static readonly HashSet<string> PreservedTomatoKeywords = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "diced",
        "crushed",
        "canned",
        "stewed",
        "paste",
        "sauce",
        "puree",
    };

    private static readonly HashSet<string> FreshTomatoCandidates = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "tomato",
        "plum tomato",
        "cherry tomato",
        "roma tomato",
        "grape tomato",
        "beefsteak tomato",
        "heirloom tomato",
    };

    /// <summary>
    /// Validates whether a matched catalog <see cref="FoodItem"/> name is a true grocery-SKU match
    /// for a raw extracted ingredient, blocking cross-SKU collisions (e.g. heavy cream vs sour cream,
    /// canned diced tomatoes vs fresh tomato, table salt vs kosher salt, or cultivar mismatches).
    /// </summary>
    public static bool IsCompatibleFoodItemMatch(
        string rawIngredientName,
        string? rawUnit,
        string? rawPrepNote,
        string candidateFoodName
    )
    {
        if (
            string.IsNullOrWhiteSpace(rawIngredientName)
            || string.IsNullOrWhiteSpace(candidateFoodName)
        )
        {
            return false;
        }

        if (
            Regex.IsMatch(
                candidateFoodName.Trim(),
                @"^eggs?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
            && Regex.IsMatch(
                rawIngredientName,
                @"\b(?:yolks?|whites?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        )
        {
            return false;
        }

        var rawNormFull = NormalizeAsciiLowerPhrase(rawIngredientName);
        var prepNormFull = NormalizeAsciiLowerPhrase(rawPrepNote);
        var combinedNormFull = string.IsNullOrEmpty(prepNormFull)
            ? rawNormFull
            : $"{rawNormFull} {prepNormFull}";
        var candNormFull = NormalizeAsciiLowerPhrase(candidateFoodName);
        var unitNorm = rawUnit?.Trim().ToLowerInvariant() ?? string.Empty;

        var rawTokensPre = SingularizeTokens(
            rawNormFull.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        );
        var candTokensPre = SingularizeTokens(
            candNormFull.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        );
        var combinedTokensPre = SingularizeTokens(
            combinedNormFull.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        );

        var rawIsContainer = unitNorm is "can" or "jar" or "tube";
        var rawHasTomato = rawTokensPre.Contains("tomato");
        var candHasTomato = candTokensPre.Contains("tomato");
        if (rawHasTomato || candHasTomato)
        {
            var rawIsPreservedTomato =
                (rawHasTomato && rawIsContainer)
                || rawTokensPre.Any(t => PreservedTomatoKeywords.Contains(t))
                || rawNormFull.Contains("sun dried tomato", StringComparison.Ordinal)
                || rawNormFull.Contains("fire roasted tomato", StringComparison.Ordinal)
                || rawNormFull.Contains("whole peeled tomato", StringComparison.Ordinal);
            var candIsPreservedTomato =
                candTokensPre.Any(t => PreservedTomatoKeywords.Contains(t))
                || candNormFull.Contains("sun dried tomato", StringComparison.Ordinal)
                || candNormFull.Contains("fire roasted tomato", StringComparison.Ordinal)
                || candNormFull.Contains("whole peeled tomato", StringComparison.Ordinal);

            if (
                rawIsPreservedTomato
                && (
                    FreshTomatoCandidates.Contains(string.Join(' ', candTokensPre))
                    || !candIsPreservedTomato
                )
            )
            {
                return false;
            }

            if (candIsPreservedTomato && !rawIsPreservedTomato)
            {
                return false;
            }
        }

        var rawSigWords = BuildSignificantWordSet(
            rawTokensPre,
            combinedTokensPre,
            unitNorm,
            applyContextualRawRules: true
        );
        var candSigWords = BuildSignificantWordSet(
            candTokensPre,
            candTokensPre,
            string.Empty,
            applyContextualRawRules: false
        );

        if (rawSigWords.Count == 0 || candSigWords.Count == 0)
        {
            return false;
        }

        if (rawSigWords.SetEquals(candSigWords))
        {
            return true;
        }

        // Unqualified "butter" (or "butter, unsalted") matches "unsalted butter"; never salted -> unsalted.
        if (
            rawSigWords.Count == 1
            && rawSigWords.Contains("butter")
            && candSigWords.Count == 2
            && candSigWords.Contains("unsalted")
            && candSigWords.Contains("butter")
            && !combinedTokensPre.Any(t => NonPlainButterQualifiers.Contains(t))
        )
        {
            return true;
        }

        // Unqualified "milk" matches "whole milk"; never coconut/buttermilk/skim/plant milks -> whole milk.
        if (
            rawSigWords.Count == 1
            && rawSigWords.Contains("milk")
            && candSigWords.Count == 2
            && candSigWords.Contains("whole")
            && candSigWords.Contains("milk")
            && !combinedTokensPre.Any(t => NonPlainMilkQualifiers.Contains(t))
        )
        {
            return true;
        }

        // Ground dry spices: e.g., "cumin" (prep "ground") <-> "ground cumin" or "cumin".
        if (
            AllowDrySpiceGroundEquivalence(rawSigWords, candSigWords, combinedTokensPre)
            || AllowDrySpiceGroundEquivalence(candSigWords, rawSigWords, candTokensPre)
        )
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Builds a clean singular canonical grocery name when an incompatible Stage 3 catalog match
    /// is rejected, preserving cultivar/variety modifiers while stripping pure prep/cut terms.
    /// </summary>
    public static string NormalizeRejectedFallbackFoodName(
        string rawName,
        string? rawUnit,
        string? rawPrepNote
    )
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return string.Empty;
        }

        var trimmed = rawName.Trim();
        var tokens = Regex
            .Split(trimmed, @"\s+")
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        var hasTomatoOrPepper = tokens.Any(t =>
        {
            var s = SingularizeFoodWord(t.ToLowerInvariant().Trim(',', ';', '.'));
            return s is "tomato" or "pepper";
        });
        var hasWater = tokens.Any(t => t.Equals("water", StringComparison.OrdinalIgnoreCase));
        var hasGarlicOrShallot = tokens.Any(t =>
        {
            var s = SingularizeFoodWord(t.ToLowerInvariant().Trim(',', ';', '.'));
            return s is "garlic" or "shallot";
        });

        // Strip leading numeric percent like "85% lean ..."
        if (tokens.Count > 1 && Regex.IsMatch(tokens[0], @"^\d+%?$", RegexOptions.CultureInvariant))
        {
            tokens.RemoveAt(0);
        }

        var kept = new List<string>(tokens.Count);
        for (var i = 0; i < tokens.Count; i++)
        {
            var rawTok = tokens[i];
            var strippedLower = StripDiacritics(rawTok.ToLowerInvariant()).Trim(',', ';', '.');
            if (
                IsStrippablePrepToken(
                    strippedLower,
                    hasTomatoOrPepper,
                    hasWater,
                    hasGarlicOrShallot
                )
            )
            {
                continue;
            }

            if (
                i > 0
                && strippedLower == "clove"
                && tokens.Any(t =>
                    SingularizeFoodWord(StripDiacritics(t.ToLowerInvariant()))
                        is "garlic"
                            or "shallot"
                )
            )
            {
                continue;
            }

            kept.Add(rawTok);
        }

        if (kept.Count == 0)
        {
            kept.AddRange(tokens);
        }

        // Singularize the final head word when plural (e.g. "Yukon Gold potatoes" -> "Yukon Gold potato").
        if (kept.Count > 0)
        {
            var last = kept[^1];
            var singLast = SingularizeFoodWord(last);
            if (!string.Equals(last.ToLowerInvariant(), singLast, StringComparison.Ordinal))
            {
                kept[^1] =
                    char.IsUpper(last[0]) && singLast.Length > 0 && char.IsLower(singLast[0])
                        ? char.ToUpperInvariant(singLast[0]) + singLast[1..]
                        : singLast;
            }
        }

        var result = string.Join(' ', kept).Trim();

        // Fold explicit "dried" from prepNote onto herbs/spices when absent from name.
        if (
            !string.IsNullOrWhiteSpace(rawPrepNote)
            && Regex.IsMatch(
                rawPrepNote,
                @"\bdried\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
            && !Regex.IsMatch(
                result,
                @"\bdried\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
            && !result.Contains("sun-dried", StringComparison.OrdinalIgnoreCase)
            && !result.Contains("freeze-dried", StringComparison.OrdinalIgnoreCase)
        )
        {
            result = $"dried {result}";
        }

        return NormalizeCanonicalFoodName(result);
    }

    private static bool AllowDrySpiceGroundEquivalence(
        HashSet<string> withGround,
        HashSet<string> withoutGround,
        List<string> combinedTokens
    )
    {
        if (withGround.Count != 2 || withoutGround.Count != 1 || !withGround.Contains("ground"))
        {
            return false;
        }

        var spice = withoutGround.First();
        if (DryGroundSpices.Contains(spice))
        {
            return withGround.Contains(spice);
        }

        if (spice is "ginger" or "clove")
        {
            return withGround.Contains(spice) && combinedTokens.Contains("ground");
        }

        return false;
    }

    private static HashSet<string> BuildSignificantWordSet(
        List<string> singularTokens,
        List<string> combinedSingularTokens,
        string unitNorm,
        bool applyContextualRawRules
    )
    {
        var hasTomatoOrRedPepper =
            singularTokens.Contains("tomato")
            || (singularTokens.Contains("red") && singularTokens.Contains("pepper"));
        var hasWater = singularTokens.Contains("water");
        var hasGarlicOrShallot =
            singularTokens.Contains("garlic") || singularTokens.Contains("shallot");
        var hasBay = singularTokens.Contains("bay");

        // Apply multi-word grocery synonyms BEFORE stripping "fresh"/"leaf".
        var working = CanonicalizeMultiWordPhraseTokens(singularTokens);

        // Strip prep/size/cut words.
        var filtered = new List<string>(working.Count);
        for (var i = 0; i < working.Count; i++)
        {
            var tok = working[i];
            if (
                tok.All(char.IsDigit)
                && (
                    working.Contains("percent")
                    || working.Contains("lean")
                    || working.Contains("beef")
                    || working.Contains("turkey")
                    || working.Contains("pork")
                )
            )
            {
                continue;
            }
            if (hasBay && tok == "leaf")
            {
                filtered.Add(tok);
                continue;
            }
            if (tok == "leaf" && filtered.Count > 0)
            {
                continue;
            }
            if (tok == "clove" && hasGarlicOrShallot)
            {
                continue;
            }
            if (tok is "bone" or "skin" && i + 1 < working.Count && working[i + 1] is "in" or "on")
            {
                i++;
                continue;
            }
            if (tok is "in" or "on" && i > 0 && working[i - 1] is "bone" or "skin")
            {
                continue;
            }
            if (IsStrippablePrepToken(tok, hasTomatoOrRedPepper, hasWater, hasGarlicOrShallot))
            {
                continue;
            }
            filtered.Add(tok);
        }

        var phrase = string.Join(' ', filtered);
        if (applyContextualRawRules)
        {
            if (phrase == "pepper")
            {
                var hasFreshCue = combinedSingularTokens.Any(t => FreshPepperCueWords.Contains(t));
                var hasSpiceCue =
                    combinedSingularTokens.Any(t =>
                        t is "ground" or "cracked" or "black" or "coarse" or "freshly"
                    )
                    || unitNorm
                        is ""
                            or "tsp"
                            or "teaspoon"
                            or "tbsp"
                            or "tablespoon"
                            or "pinch"
                            or "dash";
                if (!hasFreshCue && hasSpiceCue)
                {
                    filtered = ["black", "pepper"];
                }
            }
            else if (
                combinedSingularTokens.Contains("dried")
                && !filtered.Contains("dried")
                && !phrase.Contains("sun dried", StringComparison.Ordinal)
                && !phrase.Contains("freeze dried", StringComparison.Ordinal)
            )
            {
                filtered.Add("dried");
            }
            else if (
                combinedSingularTokens.Contains("unsalted")
                && phrase == "butter"
                && !filtered.Contains("unsalted")
            )
            {
                filtered.Add("unsalted");
            }
            else if (
                combinedSingularTokens.Contains("whole")
                && phrase == "milk"
                && !filtered.Contains("whole")
            )
            {
                filtered.Add("whole");
            }
            else if (
                (
                    combinedSingularTokens.Contains("ground")
                    || combinedSingularTokens.Contains("clove")
                )
                && phrase == "clove"
                && combinedSingularTokens.Contains("ground")
                && !filtered.Contains("ground")
            )
            {
                filtered.Add("ground");
            }
        }

        return filtered.ToHashSet(StringComparer.Ordinal);
    }

    private static List<string> CanonicalizeMultiWordPhraseTokens(List<string> tokens)
    {
        var phrase = string.Join(' ', tokens);
        phrase = phrase switch
        {
            "scallion" or "spring onion" => "green onion",
            "garbanzo bean" or "garbanzo" => "chickpea",
            "confectioner sugar" or "confectioners sugar" or "icing sugar" => "powdered sugar",
            "granulated sugar" or "white sugar" => "sugar",
            "table salt" or "fine salt" => "salt",
            "heavy whipping cream" or "whipping cream" => "heavy cream",
            "unbleached all purpose flour" or "all purpose flour" or "plain flour" =>
                "all purpose flour",
            "fresh coriander" or "coriander leaf" => "cilantro",
            "prawn" => "shrimp",
            _ => phrase,
        };

        phrase = Regex.Replace(
            phrase,
            @"\bunbleached all purpose flour\b",
            "all purpose flour",
            RegexOptions.CultureInvariant
        );
        phrase = Regex.Replace(
            phrase,
            @"\bconfectioner'?s sugar\b",
            "powdered sugar",
            RegexOptions.CultureInvariant
        );
        phrase = Regex.Replace(
            phrase,
            @"\bgarbanzo bean\b",
            "chickpea",
            RegexOptions.CultureInvariant
        );

        return phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static bool IsStrippablePrepToken(
        string tokenLower,
        bool hasTomatoOrRedPepper,
        bool hasWater,
        bool hasGarlicOrShallot
    )
    {
        if (string.IsNullOrWhiteSpace(tokenLower))
        {
            return true;
        }

        if (tokenLower.Contains('-'))
        {
            var parts = tokenLower.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (
                parts.Length > 0
                && parts.All(p => SkuPrepStripWords.Contains(p) || p is "in" or "on")
            )
            {
                return true;
            }
            return false;
        }

        if (SkuPrepStripWords.Contains(tokenLower))
        {
            return true;
        }

        if (hasWater && tokenLower is "hot" or "ice" or "iced" or "cool")
        {
            return true;
        }

        if (!hasTomatoOrRedPepper && tokenLower is "diced" or "crushed")
        {
            return true;
        }

        if (hasGarlicOrShallot && tokenLower == "clove")
        {
            return true;
        }

        return false;
    }

    private static string NormalizeAsciiLowerPhrase(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var ascii = StripDiacritics(raw.Trim().ToLowerInvariant());
        ascii = Regex.Replace(ascii, @"['’]s\b", "", RegexOptions.CultureInvariant);
        var cleaned = Regex.Replace(ascii, @"[^a-z0-9\s]+", " ", RegexOptions.CultureInvariant);
        return Regex.Replace(cleaned, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
    }

    private static string StripDiacritics(string text)
    {
        var formD = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (var ch in formD)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static List<string> SingularizeTokens(IEnumerable<string> tokens) =>
        tokens.Select(SingularizeFoodWord).ToList();

    private static string SingularizeFoodWord(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return string.Empty;
        }

        var lower = StripDiacritics(word.Trim().ToLowerInvariant());
        if (SingularInvariantWords.Contains(lower))
        {
            return lower;
        }

        return lower switch
        {
            "leaves" => "leaf",
            "loaves" => "loaf",
            "halves" => "half",
            "knives" => "knife",
            "wives" => "wife",
            "confectioners" => "confectioners",
            _ => RecipeUnitAndSectionHelpers.Singularize(lower),
        };
    }
}

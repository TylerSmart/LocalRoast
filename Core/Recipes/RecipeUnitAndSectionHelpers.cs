using System.Text.RegularExpressions;
using App.Core.Ingestion;

namespace App.Core.Recipes;

public static class RecipeUnitAndSectionHelpers
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly HashSet<string> DisallowedUnitPlaceholders = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "null",
        "none",
        "nil",
        "n/a",
        "na",
        "undefined",
        "unknown",
        "empty",
        "-",
        "--",
        "???",
        "?",
        "to taste",
        "taste",
        "as needed",
        "optional",
        "to-taste",
        "at taste",
        "custom",
    };

    private static readonly HashSet<string> GenericTopLevelSectionNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "ingredients",
        "ingredient",
        "ingredient list",
        "ingredients list",
        "base ingredients",
        "main ingredients",
        "all ingredients",
        "recipe ingredients",
        "instructions",
        "instruction",
        "directions",
        "direction",
        "method",
        "methods",
        "preparation",
        "prep",
        "steps",
        "step",
        "recipe",
        "main",
        "general",
    };

    public static bool IsDisallowedUnitPlaceholder(string? customUnitName)
    {
        if (string.IsNullOrWhiteSpace(customUnitName))
        {
            return true;
        }

        return DisallowedUnitPlaceholders.Contains(customUnitName.Trim());
    }

    public static (MeasurementUnit? Unit, string? CustomUnitName) NormalizeUnit(
        string? unitString,
        string? customUnitName = null
    )
    {
        if (
            string.Equals(unitString, "custom", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(customUnitName)
        )
        {
            unitString = customUnitName;
        }

        if (string.IsNullOrWhiteSpace(unitString))
        {
            return (null, null);
        }

        var trimmed = unitString.Trim();
        if (DisallowedUnitPlaceholders.Contains(trimmed))
        {
            return (null, null);
        }

        var lower = trimmed.ToLowerInvariant();
        if (DisallowedUnitPlaceholders.Contains(lower))
        {
            return (null, null);
        }

        return lower switch
        {
            "tsp" or "teaspoon" or "teaspoons" or "t" => (MeasurementUnit.Tsp, null),
            "tbsp" or "tablespoon" or "tablespoons" or "tb" => (MeasurementUnit.Tbsp, null),
            "fl oz" or "fl_oz" or "floz" or "fluid ounce" or "fluid ounces" => (
                MeasurementUnit.FlOz,
                null
            ),
            "cup" or "cups" or "c" => (MeasurementUnit.Cup, null),
            "pt" or "pint" or "pints" => (MeasurementUnit.Pt, null),
            "qt" or "quart" or "quarts" => (MeasurementUnit.Qt, null),
            "gal" or "gallon" or "gallons" => (MeasurementUnit.Gal, null),
            "ml" or "milliliter" or "milliliters" or "millilitre" or "millilitres" => (
                MeasurementUnit.Ml,
                null
            ),
            "l" or "liter" or "liters" or "litre" or "litres" => (MeasurementUnit.L, null),
            "oz" or "ounce" or "ounces" => (MeasurementUnit.Oz, null),
            "lb" or "lbs" or "pound" or "pounds" => (MeasurementUnit.Lb, null),
            "g" or "gram" or "grams" => (MeasurementUnit.G, null),
            "kg" or "kilogram" or "kilograms" => (MeasurementUnit.Kg, null),
            "pinch" or "pinches" => (MeasurementUnit.Pinch, null),
            "dash" or "dashes" => (MeasurementUnit.Dash, null),
            "clove" or "cloves" => (MeasurementUnit.Clove, null),
            "can" or "cans" => (MeasurementUnit.Can, null),
            "bunch" or "bunches" => (MeasurementUnit.Bunch, null),
            "slice" or "slices" => (MeasurementUnit.Slice, null),
            "whole"
            or "large"
            or "medium"
            or "small"
            or "recipe"
            or "recipes"
            or "batch"
            or "batches" => (MeasurementUnit.Whole, null),
            _ => (MeasurementUnit.Custom, trimmed[..Math.Min(50, trimmed.Length)]),
        };
    }

    public static (decimal? Quantity, decimal? MaxQuantity) NormalizeQuantities(
        decimal? quantity,
        decimal? maxQuantity
    )
    {
        var cleanQty = quantity is > 0m
            ? decimal.Round(quantity.Value, 4, MidpointRounding.AwayFromZero)
            : (decimal?)null;
        var cleanMax = maxQuantity is > 0m
            ? decimal.Round(maxQuantity.Value, 4, MidpointRounding.AwayFromZero)
            : (decimal?)null;

        if (cleanMax.HasValue && !cleanQty.HasValue)
        {
            cleanQty = cleanMax;
            cleanMax = null;
        }
        else if (cleanMax.HasValue && cleanQty.HasValue)
        {
            if (cleanMax.Value < cleanQty.Value)
            {
                (cleanQty, cleanMax) = (cleanMax, cleanQty);
            }
            else if (cleanMax.Value == cleanQty.Value)
            {
                cleanMax = null;
            }
        }

        return (cleanQty, cleanMax);
    }

    private static readonly HashSet<string> LowercaseTitleWords = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "a",
        "an",
        "and",
        "as",
        "at",
        "for",
        "from",
        "in",
        "of",
        "on",
        "or",
        "the",
        "to",
        "with",
    };

    public static bool IsGenericTopLevelSection(string? sectionName)
    {
        if (string.IsNullOrWhiteSpace(sectionName))
        {
            return false;
        }

        var trimmed = sectionName.Trim().Trim('[', ']').Trim().TrimEnd(':');
        return GenericTopLevelSectionNames.Contains(trimmed);
    }

    public static string? FormatSectionDisplayName(string? rawSection)
    {
        if (string.IsNullOrWhiteSpace(rawSection))
        {
            return null;
        }

        var cleaned = rawSection.Trim().Trim('[', ']').Trim().TrimEnd(':', '-', ';').Trim();
        if (string.IsNullOrWhiteSpace(cleaned) || IsGenericTopLevelSection(cleaned))
        {
            return null;
        }

        // Convert ALL-CAPS cookbook headers (e.g., "PICADA", "FOR THE SAUCE") to readable Title Case
        // while preserving deliberate mixed-case titles (e.g., "Crumble Topping", "For the Dough").
        var hasUpper = cleaned.Any(char.IsUpper);
        var hasLower = cleaned.Any(char.IsLower);
        if (hasUpper && !hasLower)
        {
            cleaned = ToSectionTitleCase(cleaned);
        }

        return cleaned.Length > 150 ? cleaned[..150] : cleaned;
    }

    private static string ToSectionTitleCase(string input)
    {
        var tokens = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (i > 0 && LowercaseTitleWords.Contains(token))
            {
                tokens[i] = token.ToLowerInvariant();
                continue;
            }

            tokens[i] = CapitalizeHyphenatedToken(token);
        }

        return string.Join(' ', tokens);
    }

    private static string CapitalizeHyphenatedToken(string token)
    {
        var parts = token.Split('-');
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0)
            {
                continue;
            }

            if (part.Length == 1)
            {
                parts[i] = char.ToUpperInvariant(part[0]).ToString();
            }
            else
            {
                parts[i] = char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant();
            }
        }

        return string.Join('-', parts);
    }

    public static string NormalizeSectionName(string? section)
    {
        if (string.IsNullOrWhiteSpace(section))
        {
            return string.Empty;
        }

        var s = section.Trim().ToLowerInvariant();
        string[] prefixes =
        [
            "for the ",
            "to make the ",
            "to assemble the ",
            "for assembling the ",
            "to make ",
            "for ",
            "the ",
            "make the ",
            "prepare the ",
        ];

        bool changed;
        do
        {
            changed = false;
            foreach (var p in prefixes)
            {
                if (s.StartsWith(p, StringComparison.Ordinal))
                {
                    s = s[p.Length..].Trim();
                    changed = true;
                }
            }
        } while (changed);

        return s.TrimEnd(':', '-', ' ', ';');
    }

    public static string Singularize(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return string.Empty;
        }

        if (word.EndsWith("ies", StringComparison.Ordinal) && word.Length > 4)
        {
            return word[..^3] + "y";
        }

        if (
            (
                word.EndsWith("oes", StringComparison.Ordinal)
                || word.EndsWith("ses", StringComparison.Ordinal)
                || word.EndsWith("xes", StringComparison.Ordinal)
                || word.EndsWith("ches", StringComparison.Ordinal)
                || word.EndsWith("shes", StringComparison.Ordinal)
            )
            && word.Length > 4
        )
        {
            return word[..^2];
        }

        if (word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) && word.Length > 3)
        {
            return word[..^1];
        }

        return word;
    }

    public static string NormalizeLookupKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = Regex.Replace(
            value.Trim().ToLowerInvariant(),
            @"[^\p{L}\p{N}\s-]",
            " ",
            RegexOptions.None,
            RegexTimeout
        );
        normalized = Regex.Replace(normalized, @"\s+", " ", RegexOptions.None, RegexTimeout);
        return normalized.Trim();
    }

    public static string NormalizeRecipeLookupKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value
            .Replace('\u2019', '\'')
            .Replace('\u2018', '\'')
            .Trim()
            .ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"\s+", " ", RegexOptions.None, RegexTimeout);
        return normalized;
    }

    public static string? NormalizeRecipeReferenceTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var normalized = title.Trim();
        normalized = Regex.Replace(
            normalized,
            @"^\s*\([^()]*\d[^()]*\)\s*",
            "",
            RegexOptions.None,
            RegexTimeout
        );
        normalized = Regex.Replace(
            normalized,
            @"\s*\((?:[^()]*;\s*)?(?:see\s+)?(?:recipes?\s+follows?|(?:this|facing)\s+page(?:\s+and\s+(?:this|facing)\s+page)?|pages?\s+\d+)\)",
            "",
            RegexOptions.IgnoreCase,
            RegexTimeout
        );
        normalized = Regex.Replace(
            normalized,
            @"\s*\(\s*optional\s*\)\s*$",
            "",
            RegexOptions.IgnoreCase,
            RegexTimeout
        );
        const string trailingPrepPattern =
            @",\s*(?:fitted\s+into\b.*|prebaked\b.*|baked\b.*|unbaked\b.*|rolled\b.*|cut\s+into\b.*|warm|room\s+temperature|chilled|divided|optional|melted|cooled|thawed|at\s+room\s+temperature|for\s+serving|recipes?\s+follows?)\s*$";
        normalized = Regex.Replace(
            normalized,
            trailingPrepPattern,
            "",
            RegexOptions.IgnoreCase,
            RegexTimeout
        );
        normalized = Regex.Replace(
            normalized,
            @"^(?:\d+(?:\s+\d+/\d+|/\d+)?|\d+(?:\.\d+)?)?\s*(?:recipe|batch(?:\s+of)?)\s+",
            "",
            RegexOptions.IgnoreCase,
            RegexTimeout
        );
        normalized = Regex.Replace(
            normalized,
            trailingPrepPattern,
            "",
            RegexOptions.IgnoreCase,
            RegexTimeout
        );
        var spans = SourceMeasurementParser.FindSpans(normalized);
        if (spans.Count > 0 && spans[0].Index == 0)
        {
            normalized = normalized[spans[0].Length..].TrimStart();
        }
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized.Trim();
    }

    public static string CleanRecipeReferenceTitle(string? rawTitle) =>
        NormalizeRecipeReferenceTitle(rawTitle) ?? rawTitle?.Trim() ?? string.Empty;

    public static string StripLeadingBulletOrIndex(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return Regex
            .Replace(text.Trim(), @"^(?:[-•*]|\d+[.)]\s+)", "", RegexOptions.None, RegexTimeout)
            .Trim();
    }

    public static string SplitPascalCase(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (text.Contains(' ') || text.Contains('-') || text.Contains('_'))
        {
            return text.Trim();
        }

        var result = Regex.Replace(
            text,
            @"(?<=[a-z0-9])([A-Z])",
            " $1",
            RegexOptions.None,
            RegexTimeout
        );
        result = Regex.Replace(
            result,
            @"(?<=[A-Z])([A-Z][a-z])",
            " $1",
            RegexOptions.None,
            RegexTimeout
        );
        return result.Trim();
    }

    public static string ToTagDisplayCase(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var split = SplitPascalCase(raw.Trim());
        var words = split.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var segments = words[i].Split('-', StringSplitOptions.RemoveEmptyEntries);
            for (var j = 0; j < segments.Length; j++)
            {
                var s = segments[j];
                if (s.Equals("bbq", StringComparison.OrdinalIgnoreCase))
                {
                    segments[j] = "BBQ";
                }
                else
                {
                    var lower = s.ToLowerInvariant();
                    segments[j] = char.ToUpperInvariant(lower[0]) + lower[1..];
                }
            }
            words[i] = string.Join('-', segments);
        }
        var joined = string.Join(' ', words);
        return joined.Length > 80 ? joined[..80].Trim() : joined;
    }

    public static TagCategory InferTagCategory(string tagName)
    {
        var norm = Singularize(tagName.Trim().ToLowerInvariant());

        return norm switch
        {
            // Course
            "breakfast"
            or "brunch"
            or "lunch"
            or "dinner"
            or "main"
            or "main dish"
            or "main course"
            or "entree"
            or "side"
            or "side dish"
            or "appetizer"
            or "starter"
            or "snack"
            or "midnight snack"
            or "dessert"
            or "beverage"
            or "drink"
            or "cocktail"
            or "mocktail"
            or "soup"
            or "stew"
            or "salad"
            or "sandwich"
            or "bread"
            or "baked good"
            or "baked goods"
            or "sauce"
            or "condiment"
            or "dip"
            or "dressing"
            or "spread"
            or "marinade" => TagCategory.Course,

            // Cuisine
            "american"
            or "southern"
            or "bbq"
            or "tex-mex"
            or "mexican"
            or "cajun"
            or "creole"
            or "caribbean"
            or "south american"
            or "cuban"
            or "brazilian"
            or "hawaiian"
            or "italian"
            or "french"
            or "spanish"
            or "tapas"
            or "greek"
            or "german"
            or "eastern european"
            or "british"
            or "irish"
            or "scandinavian"
            or "nordic"
            or "east asian"
            or "chinese"
            or "japanese"
            or "korean"
            or "southeast asian"
            or "thai"
            or "vietnamese"
            or "indonesian"
            or "filipino"
            or "south asian"
            or "indian"
            or "pakistani"
            or "mediterranean"
            or "middle eastern"
            or "lebanese"
            or "turkish"
            or "persian"
            or "north african"
            or "moroccan"
            or "asian"
            or "latin" => TagCategory.Cuisine,

            // Dietary
            "vegetarian"
            or "vegan"
            or "pescatarian"
            or "gluten-free"
            or "gluten free"
            or "dairy-free"
            or "dairy free"
            or "nut-free"
            or "nut free"
            or "peanut-free"
            or "peanut free"
            or "tree-nut-free"
            or "tree nut free"
            or "egg-free"
            or "egg free"
            or "soy-free"
            or "soy free"
            or "shellfish-free"
            or "shellfish free"
            or "fish-free"
            or "fish free"
            or "sesame-free"
            or "sesame free"
            or "low-carb"
            or "low carb"
            or "keto"
            or "paleo"
            or "whole30"
            or "halal"
            or "kosher"
            or "high-protein"
            or "high protein"
            or "low-sodium"
            or "low sodium"
            or "low-fat"
            or "low fat"
            or "low-sugar"
            or "low sugar"
            or "sugar-free"
            or "sugar free"
            or "diabetic-friendly"
            or "diabetic friendly"
            or "gut-friendly"
            or "gut friendly"
            or "heart-healthy"
            or "heart healthy"
            or "high-fiber"
            or "high fiber"
            or "healthy"
            or "alcohol"
            or "alocohol"
            or "alcoholic"
            or "contains alcohol" => TagCategory.Dietary,

            // Equipment
            "slow cooker"
            or "instant pot"
            or "pressure cooker"
            or "air fryer"
            or "grill"
            or "grilling"
            or "charcoal grill"
            or "gas grill"
            or "smoker"
            or "smoking"
            or "campfire"
            or "dutch oven"
            or "sheet pan"
            or "one-pot"
            or "one pot"
            or "one-pan"
            or "one pan"
            or "no-cook"
            or "no cook"
            or "minimal dish"
            or "minimal dishes"
            or "skillet"
            or "cast iron"
            or "carbon steel"
            or "stainless steel"
            or "wok"
            or "sous vide"
            or "stand mixer"
            or "food processor"
            or "blender"
            or "toaster oven"
            or "microwave"
            or "waffle maker"
            or "sauté"
            or "saute"
            or "braise"
            or "deep-fry"
            or "deep fry"
            or "shallow-fry"
            or "shallow fry"
            or "pan-sear"
            or "pan sear"
            or "stir-fry"
            or "stir fry"
            or "boil & simmer"
            or "boil"
            or "simmer"
            or "steamed"
            or "steam"
            or "baking"
            or "bake"
            or "roasting"
            or "roast"
            or "broiling"
            or "broil" => TagCategory.Equipment,

            // Protein & Base
            "chicken breast"
            or "chicken thigh"
            or "chicken"
            or "turkey"
            or "duck"
            or "poultry"
            or "beef"
            or "steak"
            or "ground beef"
            or "pork"
            or "pork chop"
            or "pork tenderloin"
            or "pork shoulder"
            or "pulled pork"
            or "lamb"
            or "sausage"
            or "bacon"
            or "ham"
            or "egg"
            or "eggs"
            or "veal"
            or "salmon"
            or "white fish"
            or "cod"
            or "halibut"
            or "tilapia"
            or "shrimp"
            or "prawn"
            or "scallop"
            or "tuna"
            or "crab"
            or "lobster"
            or "mussel"
            or "clam"
            or "seafood"
            or "fish"
            or "tofu"
            or "tempeh"
            or "lentil"
            or "bean"
            or "black bean"
            or "chickpea"
            or "edamame"
            or "pasta"
            or "noodle"
            or "rice"
            or "potato"
            or "sweet potato"
            or "grain"
            or "quinoa"
            or "farro"
            or "couscous"
            or "pizza"
            or "polenta" => TagCategory.Protein,

            // Occasion & Season
            "spring"
            or "summer"
            or "autumn"
            or "fall"
            or "winter"
            or "comfort food"
            or "light & refreshing"
            or "cozy & warming"
            or "cozy"
            or "warming"
            or "rainy day"
            or "crowd-pleaser"
            or "crowd pleaser"
            or "feeding a crowd"
            or "potluck"
            or "date night"
            or "dinner party"
            or "holiday"
            or "thanksgiving"
            or "christmas"
            or "4th of july"
            or "game day"
            or "finger food"
            or "weeknight"
            or "weeknight dinner"
            or "camping"
            or "picnic"
            or "road trip"
            or "packed lunch"
            or "bento" => TagCategory.Occasion,

            // Meal Prep & Preservation
            "freezer-friendly"
            or "freezer friendly"
            or "meal prep"
            or "batch cooking"
            or "batch cook"
            or "leftover-friendly"
            or "leftover friendly"
            or "reheats well"
            or "make-ahead"
            or "make ahead"
            or "overnight marinate"
            or "quick prep"
            or "slow-cooked"
            or "slow cook"
            or "30-minute meal"
            or "30 minute meal"
            or "canning & pickling"
            or "canning"
            or "pickling"
            or "dehydrating"
            or "fermented"
            or "fermentation" => TagCategory.MealPrep,

            _ => TagCategory.Custom,
        };
    }

    public static bool MatchesMeasurement(RecipeMeasurement a, RecipeMeasurement b)
    {
        if (a.Unit == b.Unit)
        {
            if (
                a.Unit == MeasurementUnit.Custom
                && !string.Equals(
                    a.CustomUnitName,
                    b.CustomUnitName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return false;
            }
            if (a.Quantity is null || b.Quantity is null)
            {
                return false;
            }
            return Math.Abs(a.Quantity.Value - b.Quantity.Value) <= 0.05m;
        }

        if (a.Quantity is null || b.Quantity is null || !a.Unit.HasValue || !b.Unit.HasValue)
        {
            return false;
        }

        if (
            CulinaryMath.TryConvertQuantity(
                a.Quantity.Value,
                a.Unit.Value,
                b.Unit.Value,
                out var converted
            )
        )
        {
            return Math.Abs(converted - b.Quantity.Value) <= 0.05m;
        }

        return false;
    }

    private const string TimerVerbsPattern =
        @"bake|simmer|cook|boil|roast|fry|chill|rest|marinate|steep|refrigerate|cool|freeze|broil|grill|sear|saut[eé]|brown|toast|steam|poach|braise|reduce|warm|heat|melt|whisk|beat|whip|mix|knead|process|pulse|blend|stir|stand|sit|rise|proof|bloom|soften|drain|press|soak|microwave";

    private static readonly Regex PlainTextDurationRegex = new(
        @"(?<low>\d+(?:\.\d+)?)(?:\s*(?:[-\u2013\u2014]|to)\s*(?:\d+(?:\.\d+)?))?\s*(?<unit>hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout
    );

    private static readonly Regex PlainTextTimerVerbRegex = new(
        $@"\b({TimerVerbsPattern})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout
    );

    /// <summary>
    /// Extracts duration timers from plain-text MCP recipe step instructions when explicit
    /// timers or inline tokens are omitted. Not used by the ingestion pipeline.
    /// </summary>
    public static IReadOnlyList<RecipeStepTimerInput> DetectPlainTextTimers(
        string? instruction,
        int stepNumber = 1
    )
    {
        if (string.IsNullOrWhiteSpace(instruction))
        {
            return [];
        }

        var results = new List<RecipeStepTimerInput>();
        foreach (Match match in PlainTextDurationRegex.Matches(instruction))
        {
            if (
                !double.TryParse(
                    match.Groups["low"].Value,
                    global::System.Globalization.NumberStyles.Float,
                    global::System.Globalization.CultureInfo.InvariantCulture,
                    out var value
                )
            )
            {
                continue;
            }

            var unit = match.Groups["unit"].Value.ToLowerInvariant();
            var multiplier = unit[0] switch
            {
                'h' => 3600d,
                'm' => 60d,
                _ => 1d,
            };

            var seconds = (int)Math.Round(value * multiplier);
            if (seconds < 1 || seconds > 864000)
            {
                continue;
            }

            var searchStart = Math.Max(0, match.Index - 160);
            var window = instruction[searchStart..match.Index];
            var boundary = window.LastIndexOfAny(['.', ';', '\n', '\r']);
            if (boundary >= 0 && boundary + 1 < window.Length)
            {
                window = window[(boundary + 1)..];
            }

            var verbMatches = PlainTextTimerVerbRegex.Matches(window);
            var verb =
                verbMatches.Count > 0 ? verbMatches[^1].Groups[1].Value.ToLowerInvariant() : null;
            var baseLabel = verb switch
            {
                "sit" or "stand" => "Rest",
                "refrigerate" => "Chill",
                "proof" => "Rise",
                "saute" => "Sauté",
                { Length: > 0 } v => char.ToUpperInvariant(v[0]) + v[1..],
                _ => $"Step {stepNumber} timer",
            };

            var label = baseLabel;
            var suffix = 1;
            while (
                results.Any(t => string.Equals(t.Label, label, StringComparison.OrdinalIgnoreCase))
            )
            {
                label = $"{baseLabel} {++suffix}";
            }

            results.Add(new RecipeStepTimerInput(seconds, label));
        }

        return results;
    }

    public static bool IsRedundantSelfCustomUnit(string? customUnitName, string? itemName)
    {
        if (string.IsNullOrWhiteSpace(customUnitName) || string.IsNullOrWhiteSpace(itemName))
        {
            return false;
        }

        var unitTrimmed = customUnitName.Trim();
        if (unitTrimmed.Contains(' ') || unitTrimmed.Length < 2)
        {
            return false;
        }

        var coreName = itemName.Trim();
        var commaOrParenIdx = coreName.IndexOfAny([',', '(', ';']);
        if (commaOrParenIdx > 0)
        {
            coreName = coreName[..commaOrParenIdx];
        }

        var lastWordMatch = Regex.Match(
            coreName,
            @"([a-zA-Z]{2,})[^a-zA-Z]*$",
            RegexOptions.CultureInvariant,
            RegexTimeout
        );
        if (!lastWordMatch.Success)
        {
            return false;
        }

        return string.Equals(
            SimpleSingularNoun(unitTrimmed),
            SimpleSingularNoun(lastWordMatch.Groups[1].Value),
            StringComparison.OrdinalIgnoreCase
        );
    }

    /// <summary>
    /// Trims and strips redundant outer matching parentheses from a preparation note
    /// (e.g., "(8-inch square)" or "((8-inch square))" -> "8-inch square") while keeping
    /// internal or leading-only parentheticals intact (e.g., "minced (about 1 tbsp)" or "(8-inch) square").
    /// </summary>
    public static string? CleanPreparationNote(string? value, int maxLength = 200)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var s = value.Trim().TrimStart(',', ';').Trim();
        while (s.Length >= 2 && s[0] == '(' && s[^1] == ')')
        {
            var depth = 0;
            var outerPairsAtEnd = true;
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] == '(')
                {
                    depth++;
                }
                else if (s[i] == ')')
                {
                    depth--;
                    if (depth == 0 && i < s.Length - 1)
                    {
                        outerPairsAtEnd = false;
                        break;
                    }
                    if (depth < 0)
                    {
                        outerPairsAtEnd = false;
                        break;
                    }
                }
            }

            if (!outerPairsAtEnd || depth != 0)
            {
                break;
            }

            s = s[1..^1].Trim();
        }

        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        return s.Length > maxLength ? s[..maxLength].TrimEnd() : s;
    }

    private static string SimpleSingularNoun(string word)
    {
        var lower = word.Trim().ToLowerInvariant();
        return lower switch
        {
            "leaves" => "leaf",
            "loaves" => "loaf",
            "halves" => "half",
            _ when lower.EndsWith("ies", StringComparison.Ordinal) && lower.Length > 4 =>
                string.Concat(lower.AsSpan(0, lower.Length - 3), "y"),
            _ when lower.Length > 4
                    && (
                        lower.EndsWith("ches", StringComparison.Ordinal)
                        || lower.EndsWith("shes", StringComparison.Ordinal)
                        || lower.EndsWith("xes", StringComparison.Ordinal)
                        || lower.EndsWith("zes", StringComparison.Ordinal)
                        || lower.EndsWith("ses", StringComparison.Ordinal)
                        || lower.EndsWith("oes", StringComparison.Ordinal)
                    ) => lower[..^2],
            _ when lower.EndsWith('s')
                    && lower.Length > 3
                    && !lower.EndsWith("ss", StringComparison.Ordinal) => lower[..^1],
            _ => lower,
        };
    }
}

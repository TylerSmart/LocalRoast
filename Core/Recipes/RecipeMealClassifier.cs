using System.Text.RegularExpressions;
using App.Core.MealPlans;

namespace App.Core.Recipes;

public static partial class RecipeMealClassifier
{
    public static readonly IReadOnlySet<string> RecognizedCourseTags = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "breakfast",
        "brunch",
        "lunch",
        "dinner",
        "snack",
        "midnight snack",
        "dessert",
        "appetizer",
        "starter",
        "side dish",
        "main course",
        "salad",
        "soup",
        "stew",
        "bread",
        "baked goods",
        "sandwich",
        "beverage",
        "drink",
        "cocktail",
        "mocktail",
        "condiment",
        "sauce",
        "dip",
        "dressing",
        "spread",
        "marinade",
        "topping",
        "seasoning",
        "glaze",
        "syrup",
        "frosting",
    };

    private static readonly HashSet<string> HardAccessoryTags = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "beverage",
        "drink",
        "cocktail",
        "mocktail",
        "condiment",
        "sauce",
        "marinade",
        "dressing",
        "topping",
        "seasoning",
        "glaze",
        "syrup",
        "frosting",
    };

    private static readonly HashSet<string> NonDinnerCourseTags = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "appetizer",
        "starter",
        "side dish",
        "snack",
        "midnight snack",
        "breakfast",
        "brunch",
        "bread",
        "baked goods",
    };

    private static readonly HashSet<string> DinnerPrimaryCourseTags = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "dinner",
        "main course",
        "stew",
        "soup",
    };

    private static readonly HashSet<string> LunchPrimaryCourseTags = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "lunch",
        "sandwich",
        "salad",
        "soup",
        "stew",
        "main course",
        "dinner",
    };

    private static readonly HashSet<string> BreakfastPrimaryCourseTags = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "breakfast",
        "brunch",
    };

    private static readonly HashSet<string> BreakfastHardExcludedTags = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "dessert",
        "dip",
        "spread",
        "side dish",
        "dinner",
    };

    private static readonly HashSet<string> SnackPrimaryCourseTags = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "snack",
        "midnight snack",
        "appetizer",
        "starter",
        "dip",
        "spread",
        "baked goods",
        "bread",
        "salad",
        "sandwich",
    };

    private static readonly HashSet<string> SnackExcludedTags = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "condiment",
        "marinade",
        "dressing",
        "sauce",
        "topping",
        "seasoning",
        "cocktail",
        "dinner",
        "main course",
    };

    private static readonly HashSet<string> ExplicitMealTagsForSubRecipe = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "main course",
        "dinner",
        "lunch",
        "breakfast",
        "brunch",
        "soup",
        "stew",
        "sandwich",
    };

    private static readonly HashSet<string> SnackMealTagsForSubRecipe = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "snack",
        "appetizer",
        "dip",
        "spread",
        "baked goods",
    };

    public static int ClassifySlotFit(
        IReadOnlySet<string> normalizedTags,
        string? title,
        string? yieldDisplay,
        bool isReferencedAsSubRecipe,
        MealSlot slot
    )
    {
        var tags = NormalizeTagSet(normalizedTags);
        var hasAnyCourseTag = tags.Overlaps(RecognizedCourseTags);

        return slot switch
        {
            MealSlot.Breakfast => ClassifyBreakfastFit(
                tags,
                hasAnyCourseTag,
                title,
                yieldDisplay,
                isReferencedAsSubRecipe
            ),
            MealSlot.Lunch => ClassifyLunchFit(
                tags,
                hasAnyCourseTag,
                title,
                yieldDisplay,
                isReferencedAsSubRecipe
            ),
            MealSlot.SnackPrep => ClassifySnackFit(
                tags,
                hasAnyCourseTag,
                title,
                yieldDisplay,
                isReferencedAsSubRecipe
            ),
            _ => ClassifyDinnerFit(
                tags,
                hasAnyCourseTag,
                title,
                yieldDisplay,
                isReferencedAsSubRecipe
            ),
        };
    }

    private static int ClassifyDinnerFit(
        HashSet<string> tags,
        bool hasAnyCourseTag,
        string? title,
        string? yieldDisplay,
        bool isReferencedAsSubRecipe
    )
    {
        if (
            tags.Overlaps(HardAccessoryTags)
            || tags.Contains("dessert")
            || tags.Contains("dip")
            || tags.Contains("spread")
        )
        {
            return 3;
        }

        if (!tags.Contains("main course") && tags.Overlaps(NonDinnerCourseTags))
        {
            return 3;
        }

        if (
            IsLikelyComponentOrNonMeal(
                tags,
                title,
                yieldDisplay,
                isReferencedAsSubRecipe,
                MealSlot.Dinner
            )
        )
        {
            return 3;
        }

        if (tags.Overlaps(DinnerPrimaryCourseTags))
        {
            return 1;
        }

        return hasAnyCourseTag ? 3 : 2;
    }

    private static int ClassifyLunchFit(
        HashSet<string> tags,
        bool hasAnyCourseTag,
        string? title,
        string? yieldDisplay,
        bool isReferencedAsSubRecipe
    )
    {
        if (
            tags.Overlaps(HardAccessoryTags)
            || tags.Contains("dessert")
            || tags.Contains("dip")
            || tags.Contains("spread")
        )
        {
            return 3;
        }

        var isExplicitMainOrLunch = tags.Contains("main course") || tags.Contains("lunch");
        if (!isExplicitMainOrLunch && tags.Overlaps(NonDinnerCourseTags))
        {
            return 3;
        }

        if (tags.Contains("side dish") && !tags.Contains("main course"))
        {
            return 3;
        }

        if (
            IsLikelyComponentOrNonMeal(
                tags,
                title,
                yieldDisplay,
                isReferencedAsSubRecipe,
                MealSlot.Lunch
            )
        )
        {
            return 3;
        }

        if (tags.Overlaps(LunchPrimaryCourseTags))
        {
            return 1;
        }

        return hasAnyCourseTag ? 3 : 2;
    }

    private static int ClassifyBreakfastFit(
        HashSet<string> tags,
        bool hasAnyCourseTag,
        string? title,
        string? yieldDisplay,
        bool isReferencedAsSubRecipe
    )
    {
        if (tags.Overlaps(HardAccessoryTags) || tags.Overlaps(BreakfastHardExcludedTags))
        {
            return 3;
        }

        var hasBreakfastTag = tags.Overlaps(BreakfastPrimaryCourseTags);
        if (tags.Contains("main course") && !hasBreakfastTag)
        {
            return 3;
        }

        if (
            IsLikelyComponentOrNonMeal(
                tags,
                title,
                yieldDisplay,
                isReferencedAsSubRecipe,
                MealSlot.Breakfast
            )
        )
        {
            return 3;
        }

        if (hasBreakfastTag)
        {
            return 1;
        }

        if (!hasAnyCourseTag)
        {
            return 2;
        }

        if ((tags.Contains("baked goods") || tags.Contains("bread")) && !tags.Contains("side dish"))
        {
            return 2;
        }

        return 3;
    }

    private static int ClassifySnackFit(
        HashSet<string> tags,
        bool hasAnyCourseTag,
        string? title,
        string? yieldDisplay,
        bool isReferencedAsSubRecipe
    )
    {
        if (tags.Overlaps(SnackExcludedTags))
        {
            return 3;
        }

        if (
            IsLikelyComponentOrNonMeal(
                tags,
                title,
                yieldDisplay,
                isReferencedAsSubRecipe,
                MealSlot.SnackPrep
            )
        )
        {
            return 3;
        }

        if (tags.Overlaps(SnackPrimaryCourseTags))
        {
            return 1;
        }

        return hasAnyCourseTag ? 3 : 2;
    }

    internal static bool IsLikelyComponentOrNonMeal(
        HashSet<string> tags,
        string? title,
        string? yieldDisplay,
        bool isReferencedAsSubRecipe,
        MealSlot slot
    )
    {
        var isExplicitEntreeYieldException =
            tags.Contains("main course") || tags.Contains("soup") || tags.Contains("stew");

        if (!string.IsNullOrWhiteSpace(yieldDisplay))
        {
            if (
                !isExplicitEntreeYieldException
                && (
                    BulkMeasurementYieldRegex().IsMatch(yieldDisplay)
                    || EnoughToYieldRegex().IsMatch(yieldDisplay)
                )
            )
            {
                return true;
            }

            if (slot != MealSlot.SnackPrep && BatchConfectionYieldRegex().IsMatch(yieldDisplay))
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            var trimmedTitle = title.Trim();
            if (slot == MealSlot.SnackPrep)
            {
                if (SnackComponentTitleSuffixRegex().IsMatch(trimmedTitle))
                {
                    return true;
                }
            }
            else if (MealComponentTitleSuffixRegex().IsMatch(trimmedTitle))
            {
                return true;
            }
        }

        if (isReferencedAsSubRecipe)
        {
            var hasMealTag =
                tags.Overlaps(ExplicitMealTagsForSubRecipe)
                || (slot == MealSlot.SnackPrep && tags.Overlaps(SnackMealTagsForSubRecipe));
            if (!hasMealTag)
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> NormalizeTagSet(IReadOnlySet<string> rawTags)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in rawTags)
        {
            if (!string.IsNullOrWhiteSpace(tag))
            {
                set.Add(tag.Trim());
            }
        }
        return set;
    }

    [GeneratedRegex(
        @"\bmakes\s+(?:about\s+|approx\.?\s+)?\d[\d\s/.,-]*(?:cup|tbsp|tablespoon|tsp|teaspoon|oz|ounce|lb|pound|jar|pint|quart)s?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex BulkMeasurementYieldRegex();

    [GeneratedRegex(
        @"\benough\s+(?:for|to\s+(?:garnish|season|coat|frost|fill|top))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex EnoughToYieldRegex();

    [GeneratedRegex(
        @"\bmakes\s+(?:about\s+)?\d[\d\s-]*\s+(?:cookies|bars|brownies|candies|truffles|cupcakes|frosting)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex BatchConfectionYieldRegex();

    [GeneratedRegex(
        @"\b(?:topping|seasoning|spice\s+blend|flour\s+blend|rub|marinade|vinaigrette|dressing|frosting|icing|glaze|simple\s+syrup|syrup|stock|broth|pie\s+dough|pizza\s+dough|pasta\s+dough|dough|pie\s+crust|tart\s+crust|garnish|compound\s+butter|cocktail|mocktail|margarita|martini|sangria|lemonade|limeade)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex MealComponentTitleSuffixRegex();

    [GeneratedRegex(
        @"\b(?:seasoning|spice\s+blend|flour\s+blend|rub|marinade|vinaigrette|dressing|stock|broth|dough|crust|garnish|cocktail|margarita|martini|sangria)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex SnackComponentTitleSuffixRegex();
}

using App.Core.MealPlans;
using App.Core.Recipes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class RecipeMealClassifierTests
{
    [TestMethod]
    public void ClassifySlotFit_Dinner_AcceptsMainCoursesAndUntaggedMeals_RejectsSidesDrinksCondimentsAndDesserts()
    {
        Assert.AreEqual(
            1,
            RecipeMealClassifier.ClassifySlotFit(
                Tags("Dinner", "Main Course"),
                "Weeknight Roast Chicken",
                "Serves 4",
                isReferencedAsSubRecipe: false,
                MealSlot.Dinner
            )
        );

        Assert.AreEqual(
            1,
            RecipeMealClassifier.ClassifySlotFit(
                Tags("Soup"),
                "Butternut Squash Soup",
                "Serves 4 to 6",
                isReferencedAsSubRecipe: false,
                MealSlot.Dinner
            )
        );

        Assert.AreEqual(
            2,
            RecipeMealClassifier.ClassifySlotFit(
                Tags(),
                "Peruvian Arroz con Pollo",
                "Serves 4 to 6",
                isReferencedAsSubRecipe: false,
                MealSlot.Dinner
            )
        );

        foreach (
            var disqualifiedTag in new[]
            {
                "Side Dish",
                "Beverage",
                "Drink",
                "Cocktail",
                "Mocktail",
                "Condiment",
                "Sauce",
                "Marinade",
                "Dressing",
                "Dip",
                "Spread",
                "Dessert",
                "Breakfast",
            }
        )
        {
            Assert.AreEqual(
                3,
                RecipeMealClassifier.ClassifySlotFit(
                    Tags(disqualifiedTag),
                    $"Sample {disqualifiedTag}",
                    "Serves 4",
                    isReferencedAsSubRecipe: false,
                    MealSlot.Dinner
                ),
                $"Tag '{disqualifiedTag}' should be disqualified for Dinner."
            );
        }
    }

    [TestMethod]
    public void ClassifySlotFit_Breakfast_RejectsAtkMultiTagSideAndSauceTraps()
    {
        Assert.AreEqual(
            1,
            RecipeMealClassifier.ClassifySlotFit(
                Tags("Breakfast"),
                "Fluffy Scrambled Eggs",
                "Serves 4",
                isReferencedAsSubRecipe: false,
                MealSlot.Breakfast
            )
        );

        // Multi-tag ATK breakfast side/condiment/dessert traps must be rejected for Breakfast
        Assert.AreEqual(
            3,
            RecipeMealClassifier.ClassifySlotFit(
                Tags("Side Dish", "Breakfast"),
                "Diner-Style Home Fries",
                "Serves 2 to 3",
                isReferencedAsSubRecipe: false,
                MealSlot.Breakfast
            )
        );
        Assert.AreEqual(
            3,
            RecipeMealClassifier.ClassifySlotFit(
                Tags("Side Dish", "Breakfast"),
                "Oven-Fried Bacon",
                "Serves 4 to 6",
                isReferencedAsSubRecipe: false,
                MealSlot.Breakfast
            )
        );
        Assert.AreEqual(
            3,
            RecipeMealClassifier.ClassifySlotFit(
                Tags("Baked Goods", "Side Dish", "Breakfast"),
                "Savory Corn Muffins",
                "Makes 12 muffins",
                isReferencedAsSubRecipe: false,
                MealSlot.Breakfast
            )
        );
        Assert.AreEqual(
            3,
            RecipeMealClassifier.ClassifySlotFit(
                Tags("Sauce", "Dessert", "Brunch", "Breakfast"),
                "Brown Sugar–Apple Topping",
                "Makes 2 cups",
                isReferencedAsSubRecipe: false,
                MealSlot.Breakfast
            )
        );
        Assert.AreEqual(
            3,
            RecipeMealClassifier.ClassifySlotFit(
                Tags("Condiment", "Breakfast"),
                "Breakfast Sausage Seasoning",
                "Makes about 2 tablespoons",
                isReferencedAsSubRecipe: false,
                MealSlot.Breakfast
            )
        );
    }

    [TestMethod]
    public void ClassifySlotFit_Lunch_AcceptsStandaloneSaladsAndSandwiches_RejectsSideSalads()
    {
        Assert.AreEqual(
            1,
            RecipeMealClassifier.ClassifySlotFit(
                Tags("Salad"),
                "Antipasto Pasta Salad",
                "Serves 6",
                isReferencedAsSubRecipe: false,
                MealSlot.Lunch
            )
        );
        Assert.AreEqual(
            1,
            RecipeMealClassifier.ClassifySlotFit(
                Tags("Sandwich"),
                "Classic Tuna Melt",
                "Serves 4",
                isReferencedAsSubRecipe: false,
                MealSlot.Lunch
            )
        );
        Assert.AreEqual(
            3,
            RecipeMealClassifier.ClassifySlotFit(
                Tags("Side Dish", "Salad"),
                "Simple Green Side Salad",
                "Serves 4",
                isReferencedAsSubRecipe: false,
                MealSlot.Lunch
            )
        );
    }

    [TestMethod]
    public void ClassifySlotFit_UntaggedComponentsAndBulkYields_AreDisqualifiedAsMeals()
    {
        Assert.AreEqual(
            3,
            RecipeMealClassifier.ClassifySlotFit(
                Tags(),
                "Fresh Pappardelle",
                "Makes 1 pound",
                isReferencedAsSubRecipe: false,
                MealSlot.Dinner
            )
        );
        Assert.AreEqual(
            3,
            RecipeMealClassifier.ClassifySlotFit(
                Tags(),
                "Red Chile Chicken Filling",
                "Makes enough for 18 tamales",
                isReferencedAsSubRecipe: false,
                MealSlot.Dinner
            )
        );
        Assert.AreEqual(
            3,
            RecipeMealClassifier.ClassifySlotFit(
                Tags(),
                "All-Purpose Gluten-Free Flour Blend",
                "Makes 42 ounces",
                isReferencedAsSubRecipe: false,
                MealSlot.Dinner
            )
        );
        Assert.AreEqual(
            3,
            RecipeMealClassifier.ClassifySlotFit(
                Tags(),
                "Sautéed Wild Mushrooms",
                "Makes enough to garnish 6 to 8 bowls of soup",
                isReferencedAsSubRecipe: false,
                MealSlot.Dinner
            )
        );
        Assert.AreEqual(
            3,
            RecipeMealClassifier.ClassifySlotFit(
                Tags(),
                "Gluten-Free Chocolate Chip Cookies",
                "Makes about 24 cookies",
                isReferencedAsSubRecipe: false,
                MealSlot.Dinner
            )
        );
        Assert.AreEqual(
            2,
            RecipeMealClassifier.ClassifySlotFit(
                Tags(),
                "Gluten-Free Chocolate Chip Cookies",
                "Makes about 24 cookies",
                isReferencedAsSubRecipe: false,
                MealSlot.SnackPrep
            )
        );
        Assert.AreEqual(
            3,
            RecipeMealClassifier.ClassifySlotFit(
                Tags(),
                "Garlic Herb Vinaigrette",
                "Serves 4",
                isReferencedAsSubRecipe: false,
                MealSlot.Lunch
            )
        );
        Assert.AreEqual(
            3,
            RecipeMealClassifier.ClassifySlotFit(
                Tags(),
                "Classic Margarita",
                "Serves 2",
                isReferencedAsSubRecipe: false,
                MealSlot.Dinner
            )
        );
        Assert.AreEqual(
            3,
            RecipeMealClassifier.ClassifySlotFit(
                Tags(),
                "Crispy Pickled Onions",
                "Serves 6",
                isReferencedAsSubRecipe: true,
                MealSlot.Dinner
            )
        );

        // Soup with cup yield still passes because Soup/Main Course is exempt from bulk yield rejection
        Assert.AreEqual(
            1,
            RecipeMealClassifier.ClassifySlotFit(
                Tags("Soup"),
                "Creamy Tomato Soup",
                "Makes 6 cups",
                isReferencedAsSubRecipe: false,
                MealSlot.Dinner
            )
        );
    }

    private static HashSet<string> Tags(params string[] values) =>
        new(values, StringComparer.OrdinalIgnoreCase);
}

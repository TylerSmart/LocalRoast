using App.Core.FoodCatalog;
using App.Core.Ingestion;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class FoodCategoryTests
{
    [TestMethod]
    [DataRow("Whole Milk", FoodCategory.DairyAndEggs)]
    [DataRow("Unsalted Butter", FoodCategory.DairyAndEggs)]
    [DataRow("Large Eggs", FoodCategory.DairyAndEggs)]
    [DataRow("Orange Juice", FoodCategory.Refrigerated)]
    [DataRow("Honeycrisp Apples", FoodCategory.Produce)]
    [DataRow("Romaine Lettuce", FoodCategory.Produce)]
    [DataRow("Lemon Juice", FoodCategory.Produce)]
    [DataRow("Fresh Thyme", FoodCategory.Produce)]
    [DataRow("Peanut Butter", FoodCategory.PantryDryGoods)]
    [DataRow("Egg Noodles", FoodCategory.PantryDryGoods)]
    [DataRow("Sourdough Bread", FoodCategory.Bakery)]
    [DataRow("Curry Powder", FoodCategory.SpicesAndSeasonings)]
    [DataRow("Garlic Salt", FoodCategory.SpicesAndSeasonings)]
    [DataRow("Garlic", FoodCategory.Produce)]
    [DataRow("Kosher Salt", FoodCategory.SpicesAndSeasonings)]
    [DataRow("Dried Thyme", FoodCategory.SpicesAndSeasonings)]
    [DataRow("Chicken Thigh", FoodCategory.MeatAndSeafood)]
    [DataRow("Chicken Broth", FoodCategory.CannedAndJarred)]
    [DataRow("Coconut Milk", FoodCategory.CannedAndJarred)]
    [DataRow("Frozen Peas", FoodCategory.Frozen)]
    [DataRow("All-Purpose Flour", FoodCategory.Baking)]
    [DataRow("Extra-Virgin Olive Oil", FoodCategory.CondimentsAndSauces)]
    [DataRow("Soy Sauce", FoodCategory.CondimentsAndSauces)]
    [DataRow("Rice Vinegar", FoodCategory.CondimentsAndSauces)]
    [DataRow("Tamarind Juice Concentrate", FoodCategory.CondimentsAndSauces)]
    [DataRow("Tamarind Concentrate", FoodCategory.CondimentsAndSauces)]
    [DataRow("Tamarind Paste", FoodCategory.CondimentsAndSauces)]
    [DataRow("Wonton Wrappers", FoodCategory.Refrigerated)]
    [DataRow("Egg Roll Wrappers", FoodCategory.Refrigerated)]
    [DataRow("Rice Paper Wrappers", FoodCategory.PantryDryGoods)]
    [DataRow("Lumpia Wrappers", FoodCategory.PantryDryGoods)]
    [DataRow("pepper", FoodCategory.SpicesAndSeasonings)]
    [DataRow("ground pepper", FoodCategory.SpicesAndSeasonings)]
    [DataRow("Bell Pepper", FoodCategory.Produce)]
    [DataRow("Red Bell Pepper", FoodCategory.Produce)]
    [DataRow("Jasmine Rice", FoodCategory.PantryDryGoods)]
    [DataRow("Pecan", FoodCategory.PantryDryGoods)]
    [DataRow("Oysters", FoodCategory.MeatAndSeafood)]
    [DataRow("Orange Liqueur", FoodCategory.Beverages)]
    [DataRow("Peach Schnapps", FoodCategory.Beverages)]
    [DataRow("Gin", FoodCategory.Beverages)]
    [DataRow("Fresh Ginger", FoodCategory.Produce)]
    [DataRow("Pomegranate Molasses", FoodCategory.CondimentsAndSauces)]
    [DataRow("Paper Towels", FoodCategory.Other)]
    [DataRow("", FoodCategory.Other)]
    public void Classify_KnownFoods_ReturnsExpectedCategory(string name, FoodCategory expected)
    {
        Assert.AreEqual(expected, FoodCategoryClassifier.Classify(name));
    }

    [TestMethod]
    [DataRow("MeatAndSeafood", FoodCategory.MeatAndSeafood)]
    [DataRow("Meat & Seafood", FoodCategory.MeatAndSeafood)]
    [DataRow("meat_and_seafood", FoodCategory.MeatAndSeafood)]
    [DataRow("Dairy", FoodCategory.DairyAndEggs)]
    [DataRow("spices", FoodCategory.SpicesAndSeasonings)]
    [DataRow("Dry Foods", FoodCategory.PantryDryGoods)]
    [DataRow("refrigerated", FoodCategory.Refrigerated)]
    [DataRow("Bakery & Bread", FoodCategory.Bakery)]
    public void Parse_TolerantLabels_MapToCategory(string label, FoodCategory expected)
    {
        Assert.AreEqual(expected, FoodCategories.Parse(label));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("Gadgets")]
    public void Parse_UnknownOrBlank_ReturnsNull(string? label)
    {
        Assert.IsNull(FoodCategories.Parse(label));
    }

    [TestMethod]
    public void StoreWalkOrder_StartsWithProduceAndEndsWithOther()
    {
        Assert.AreEqual(FoodCategory.Produce, FoodCategories.StoreWalkOrder[0]);
        Assert.AreEqual(FoodCategory.Other, FoodCategories.StoreWalkOrder[^1]);
        Assert.AreEqual("Meat & Seafood", FoodCategories.DisplayName(FoodCategory.MeatAndSeafood));
    }

    [TestMethod]
    public void Compile_AssignsCategories_FromLlmSuggestionFallbackAndCatalogMatch()
    {
        var catalog = new List<CatalogFoodLookupItem>
        {
            new(Guid.NewGuid(), "Garlic", "garlic", false, [], FoodCategory.Produce),
            new(Guid.NewGuid(), "Kosher Salt", "kosher salt", true, ["salt"], null),
        };

        var extracted = new ExtractedRecipeDto(
            Title: "Category Test",
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "garlic",
                    DisplayName: "garlic",
                    Quantity: 2m,
                    Unit: "clove",
                    RawText: "2 cloves garlic"
                ),
                new(
                    "ing_2",
                    CanonicalFoodItem: "kosher salt",
                    DisplayName: "kosher salt",
                    Quantity: 1m,
                    Unit: "tsp",
                    RawText: "1 tsp kosher salt"
                ),
                new(
                    "ing_3",
                    CanonicalFoodItem: "whole milk",
                    DisplayName: "whole milk",
                    Category: "Refrigerated",
                    Quantity: 1m,
                    Unit: "cup",
                    RawText: "1 cup whole milk"
                ),
                new(
                    "ing_4",
                    CanonicalFoodItem: "chicken thigh",
                    DisplayName: "chicken thighs",
                    Quantity: 1m,
                    Unit: "lb",
                    RawText: "1 lb chicken thighs"
                ),
                new(
                    "ing_5",
                    CanonicalFoodItem: "chickpeas",
                    DisplayName: "chickpeas",
                    Category: "not a real aisle",
                    Quantity: 1m,
                    Unit: "can",
                    RawText: "1 can chickpeas"
                ),
            ],
            Steps: [new(StepNumber: 1, TemplateText: "Cook.")]
        );

        var validation = RecipeValidator.Validate(extracted);
        var matches = EntityCandidateMatcher.Match(extracted, catalog, []);
        var draft = RecipeDraftCompiler.Compile(extracted, validation, matches);

        var ingredients = draft.Ingredients!;
        Assert.AreEqual(FoodCategory.Produce, ingredients[0].SuggestedCategory);
        Assert.AreEqual(FoodCategory.SpicesAndSeasonings, ingredients[1].SuggestedCategory);
        Assert.AreEqual(FoodCategory.Refrigerated, ingredients[2].SuggestedCategory);
        Assert.AreEqual(FoodCategory.MeatAndSeafood, ingredients[3].SuggestedCategory);
        Assert.AreEqual(FoodCategory.CannedAndJarred, ingredients[4].SuggestedCategory);
    }

    [TestMethod]
    [DataRow("extra-virgin olive oil", FoodCategory.CondimentsAndSauces, true)]
    [DataRow("vegetable oil", FoodCategory.CondimentsAndSauces, true)]
    [DataRow("baking soda", FoodCategory.Baking, true)]
    [DataRow("water", FoodCategory.Other, true)]
    [DataRow("black pepper", FoodCategory.SpicesAndSeasonings, true)]
    [DataRow("sugar snap peas", FoodCategory.Produce, false)]
    [DataRow("mustard greens", FoodCategory.Produce, false)]
    [DataRow("watercress", FoodCategory.Produce, false)]
    [DataRow("mayonnaise", FoodCategory.CondimentsAndSauces, false)]
    [DataRow("tahini", FoodCategory.PantryDryGoods, false)]
    [DataRow("egg", FoodCategory.DairyAndEggs, false)]
    [DataRow("ice cube", FoodCategory.Beverages, true)]
    [DataRow("chive", FoodCategory.Produce, false)]
    [DataRow("Kashmiri chile powder", FoodCategory.SpicesAndSeasonings, true)]
    public void IsLikelyPantryStaple_ClassifiesStaplesWithoutProduceFalsePositives(
        string foodName,
        FoodCategory category,
        bool expected
    )
    {
        Assert.AreEqual(expected, FoodCategoryClassifier.IsLikelyPantryStaple(foodName, category));
    }

    [TestMethod]
    [DataRow("ice cube", FoodCategory.Beverages)]
    [DataRow("chive", FoodCategory.Produce)]
    [DataRow("lemon", FoodCategory.Produce)]
    [DataRow("lemon juice", FoodCategory.Produce)]
    [DataRow("coconut milk yogurt", FoodCategory.DairyAndEggs)]
    [DataRow("coconut yogurt", FoodCategory.DairyAndEggs)]
    [DataRow("almond milk yogurt", FoodCategory.DairyAndEggs)]
    [DataRow("aquafaba", FoodCategory.CannedAndJarred)]
    [DataRow("Kashmiri chile powder", FoodCategory.SpicesAndSeasonings)]
    [DataRow("chile flakes", FoodCategory.SpicesAndSeasonings)]
    public void Classify_MapsAdditionalProduceAndBeverageItems(
        string foodName,
        FoodCategory expectedCategory
    )
    {
        Assert.AreEqual(expectedCategory, FoodCategoryClassifier.Classify(foodName));
    }

    [TestMethod]
    public void Compile_LlmStorageCategoryOverride_RequiresCuesOrNonConflictingDeterministicCategory()
    {
        var extracted = new ExtractedRecipeDto(
            Title: "Category Guard Test",
            PrepTimeMinutes: 10,
            CookTimeMinutes: 0,
            TotalTimeMinutes: 10,
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "baby spinach",
                    DisplayName: "baby spinach",
                    Category: "Frozen",
                    Quantity: 5m,
                    Unit: "ounce",
                    RawText: "5 ounces baby spinach"
                ),
                new(
                    "ing_2",
                    CanonicalFoodItem: "spinach",
                    DisplayName: "frozen chopped spinach",
                    Category: "Frozen",
                    Quantity: 10m,
                    Unit: "ounce",
                    PreparationNotes: "thawed",
                    RawText: "10 ounces frozen chopped spinach, thawed"
                ),
                new(
                    "ing_3",
                    CanonicalFoodItem: "whole milk",
                    DisplayName: "whole milk",
                    Category: "Refrigerated",
                    Quantity: 1m,
                    Unit: "cup",
                    RawText: "1 cup whole milk"
                ),
                new(
                    "ing_4",
                    CanonicalFoodItem: "kashmiri chile powder",
                    DisplayName: "Kashmiri chile powder",
                    Category: "Produce",
                    Quantity: 1m,
                    Unit: "teaspoon",
                    RawText: "1 teaspoon Kashmiri chile powder"
                ),
            ],
            Steps:
            [
                new(
                    1,
                    TemplateText: "Combine {{spinach}}, {{frozen_spinach}}, {{milk}}, and {{chile}}.",
                    IngredientTokens:
                    [
                        new("spinach", "ing_1", 5m, "ounce", "baby spinach"),
                        new("frozen_spinach", "ing_2", 10m, "ounce", "frozen chopped spinach"),
                        new("milk", "ing_3", 1m, "cup", "whole milk"),
                        new("chile", "ing_4", 1m, "teaspoon", "Kashmiri chile powder"),
                    ]
                ),
            ]
        );

        var validation = RecipeValidator.Validate(extracted);
        var matches = EntityCandidateMatcher.Match(extracted, [], []);
        var draft = RecipeDraftCompiler.Compile(extracted, validation, matches);

        Assert.IsNotNull(draft.Ingredients);
        Assert.AreEqual(FoodCategory.Produce, draft.Ingredients[0].SuggestedCategory);
        Assert.AreEqual(FoodCategory.Frozen, draft.Ingredients[1].SuggestedCategory);
        Assert.AreEqual(FoodCategory.Refrigerated, draft.Ingredients[2].SuggestedCategory);
        Assert.AreEqual(FoodCategory.SpicesAndSeasonings, draft.Ingredients[3].SuggestedCategory);
    }
}

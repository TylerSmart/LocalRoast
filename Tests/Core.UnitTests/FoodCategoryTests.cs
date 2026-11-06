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
    [DataRow("Jasmine Rice", FoodCategory.PantryDryGoods)]
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
    public void Normalize_AssignsCategories_FromLlmSuggestionFallbackAndCatalogMatch()
    {
        var catalog = new List<CatalogFoodLookupItem>
        {
            new(Guid.NewGuid(), "Garlic", "garlic", false, [], FoodCategory.Produce),
            new(Guid.NewGuid(), "Kosher Salt", "kosher salt", true, ["salt"], null),
        };

        var raw = new LlmExtractedRecipePayload(
            Title: "Category Test",
            Ingredients:
            [
                new(null, 2m, null, "clove", "garlic", "garlic"),
                new(null, 1m, null, "tsp", "kosher salt", "kosher salt"),
                new(
                    null,
                    1m,
                    null,
                    "cup",
                    "whole milk",
                    "whole milk",
                    SuggestedCategory: "Refrigerated"
                ),
                new(null, 1m, null, "lb", "chicken thigh", "chicken thighs"),
                new(
                    null,
                    1m,
                    null,
                    "can",
                    "chickpeas",
                    "chickpeas",
                    SuggestedCategory: "not a real aisle"
                ),
            ],
            Steps: [new(null, "Cook.", [], [])]
        );

        var draft = RecipeDraftNormalizer.Normalize(raw, catalog, [], null, null, null, null, null);

        var ingredients = draft.Ingredients!;
        Assert.AreEqual(FoodCategory.Produce, ingredients[0].SuggestedCategory);
        Assert.AreEqual(FoodCategory.SpicesAndSeasonings, ingredients[1].SuggestedCategory);
        Assert.AreEqual(FoodCategory.Refrigerated, ingredients[2].SuggestedCategory);
        Assert.AreEqual(FoodCategory.MeatAndSeafood, ingredients[3].SuggestedCategory);
        Assert.AreEqual(FoodCategory.CannedAndJarred, ingredients[4].SuggestedCategory);
    }
}

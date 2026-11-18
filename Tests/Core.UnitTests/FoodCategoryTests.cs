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
    public void Classify_MapsExpectedAislesForCommonAndFrozenIngredients()
    {
        Assert.AreEqual(FoodCategory.Produce, FoodCategoryClassifier.Classify("garlic"));
        Assert.AreEqual(
            FoodCategory.SpicesAndSeasonings,
            FoodCategoryClassifier.Classify("kosher salt")
        );
        Assert.AreEqual(FoodCategory.DairyAndEggs, FoodCategoryClassifier.Classify("whole milk"));
        Assert.AreEqual(
            FoodCategory.MeatAndSeafood,
            FoodCategoryClassifier.Classify("chicken thighs")
        );
        Assert.AreEqual(
            FoodCategory.Frozen,
            FoodCategoryClassifier.Classify("frozen chopped spinach")
        );
        Assert.AreEqual(FoodCategory.Produce, FoodCategoryClassifier.Classify("baby spinach"));
        Assert.AreEqual(
            FoodCategory.SpicesAndSeasonings,
            FoodCategoryClassifier.Classify("Kashmiri chile powder")
        );
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
}

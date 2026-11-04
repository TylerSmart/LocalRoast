using App.Core.FoodCatalog;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class FoodNameSanitizerTests
{
    [TestMethod]
    [DataRow(null, false)]
    [DataRow("", false)]
    [DataRow("   ", false)]
    [DataRow("null", false)]
    [DataRow("NULL", false)]
    [DataRow("Null", false)]
    [DataRow("none", false)]
    [DataRow("None", false)]
    [DataRow("n/a", false)]
    [DataRow("N/A", false)]
    [DataRow("na", false)]
    [DataRow("undefined", false)]
    [DataRow("unknown", false)]
    [DataRow("nil", false)]
    [DataRow("empty", false)]
    [DataRow("ingredient", false)]
    [DataRow("ingredients", false)]
    [DataRow("food", false)]
    [DataRow("food item", false)]
    [DataRow("item", false)]
    [DataRow("-", false)]
    [DataRow("--", false)]
    [DataRow("???", false)]
    [DataRow("optional", false)]
    [DataRow("various", false)]
    [DataRow("Garlic", true)]
    [DataRow("Kosher Salt", true)]
    [DataRow("extra virgin olive oil", true)]
    [DataRow("Egg", true)]
    [DataRow("a", true)]
    public void IsValidFoodName_ValidatesCorrectly(string? input, bool expected)
    {
        var result = FoodNameSanitizer.IsValidFoodName(input);
        Assert.AreEqual(expected, result);
    }

    [TestMethod]
    public void SanitizeFoodName_DisallowedOrInvalid_ReturnsNull()
    {
        Assert.IsNull(FoodNameSanitizer.SanitizeFoodName(null));
        Assert.IsNull(FoodNameSanitizer.SanitizeFoodName("   "));
        Assert.IsNull(FoodNameSanitizer.SanitizeFoodName("null"));
        Assert.IsNull(FoodNameSanitizer.SanitizeFoodName("NULL"));
        Assert.IsNull(FoodNameSanitizer.SanitizeFoodName("none"));
        Assert.IsNull(FoodNameSanitizer.SanitizeFoodName("N/A"));
        Assert.IsNull(FoodNameSanitizer.SanitizeFoodName("--"));
    }

    [TestMethod]
    public void SanitizeFoodName_Valid_ReturnsTrimmedName()
    {
        Assert.AreEqual("Garlic", FoodNameSanitizer.SanitizeFoodName("  Garlic  "));
        Assert.AreEqual("Kosher Salt", FoodNameSanitizer.SanitizeFoodName("Kosher Salt"));
    }
}

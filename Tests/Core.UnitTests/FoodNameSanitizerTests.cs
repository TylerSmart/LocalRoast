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

    [TestMethod]
    [DataRow("cold water", "water")]
    [DataRow("Warm Water", "water")]
    [DataRow("boiling water", "water")]
    [DataRow("hot sauce", "hot sauce")]
    [DataRow("large egg", "egg")]
    [DataRow("extra-large eggs", "egg")]
    [DataRow("garlic clove", "garlic")]
    [DataRow("garlic cloves", "garlic")]
    [DataRow("frozen chopped spinach", "frozen spinach")]
    [DataRow("table salt", "salt")]
    [DataRow("Fine Salt", "salt")]
    [DataRow("ground black pepper", "black pepper")]
    [DataRow("freshly ground black pepper", "black pepper")]
    [DataRow("ground pepper", "black pepper")]
    [DataRow("freshly ground pepper", "black pepper")]
    [DataRow("cracked pepper", "black pepper")]
    [DataRow("peppercorns", "peppercorns")]
    [DataRow("cayenne pepper", "cayenne pepper")]
    [DataRow("peanut or vegetable oil", "peanut oil")]
    [DataRow("olive oil or vegetable oil", "olive oil")]
    [DataRow("butter or vegetable oil", "butter")]
    public void NormalizeCanonicalFoodName_StripsSafeModifiersOnly(string input, string expected)
    {
        Assert.AreEqual(expected, FoodNameSanitizer.NormalizeCanonicalFoodName(input));
    }

    [TestMethod]
    [DataRow("heavy cream", null, null, "sour cream", false)]
    [DataRow("Thai green chile", null, "sliced", "jalapeño chile", false)]
    [DataRow("diced tomatoes", "can", "drained", "tomato", false)]
    [DataRow("crushed tomatoes", "can", null, "tomato", false)]
    [DataRow("thin-sliced cooked deli ham", "slice", "thin-sliced", "smoked ham", false)]
    [DataRow("table salt", null, null, "kosher salt", false)]
    [DataRow("Yukon Gold potatoes", null, "unpeeled", "potato", false)]
    [DataRow("mango", null, "peeled", "ataulfo mango", false)]
    [DataRow("egg yolks", null, null, "egg", false)]
    [DataRow("butter", null, null, "unsalted butter", true)]
    [DataRow("butter", null, "unsalted, divided", "unsalted butter", true)]
    [DataRow("thyme", "tsp", "dried", "dried thyme", true)]
    [DataRow("thyme", "tsp", "minced fresh", "dried thyme", false)]
    [DataRow("chopped onion", null, null, "onion", true)]
    [DataRow("garlic cloves", "clove", null, "garlic", true)]
    [DataRow("scallions", null, "sliced", "green onion", true)]
    [DataRow("strawberries", "cup", "hulled", "strawberry", true)]
    public void IsCompatibleFoodItemMatch_BlocksCrossSkuCollisionsAndAllowsTrueSynonyms(
        string rawName,
        string? rawUnit,
        string? rawPrepNote,
        string candidateFoodName,
        bool expected
    )
    {
        Assert.AreEqual(
            expected,
            FoodNameSanitizer.IsCompatibleFoodItemMatch(
                rawName,
                rawUnit,
                rawPrepNote,
                candidateFoodName
            )
        );
    }

    [TestMethod]
    [DataRow("heavy cream", null, null, "heavy cream")]
    [DataRow("diced tomatoes", "can", "drained", "diced tomato")]
    [DataRow("crushed tomatoes", "can", null, "crushed tomato")]
    [DataRow("thin-sliced cooked deli ham", "slice", "thin-sliced", "deli ham")]
    [DataRow("Yukon Gold potatoes", null, "unpeeled", "Yukon Gold potato")]
    [DataRow("thyme", "tsp", "dried", "dried thyme")]
    public void NormalizeRejectedFallbackFoodName_PreservesIdentityModifiersAndSingularizes(
        string rawName,
        string? rawUnit,
        string? rawPrepNote,
        string expected
    )
    {
        Assert.AreEqual(
            expected,
            FoodNameSanitizer.NormalizeRejectedFallbackFoodName(rawName, rawUnit, rawPrepNote)
        );
    }
}

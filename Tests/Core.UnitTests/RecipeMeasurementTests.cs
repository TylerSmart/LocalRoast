using System.Text.Json;
using App.Core.Exceptions;
using App.Core.Ingestion;
using App.Core.Recipes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class RecipeMeasurementTests
{
    private static RecipeIngredientDto Flour(bool preferAlternative = true) =>
        new(
            Guid.NewGuid(),
            0,
            null,
            5,
            null,
            MeasurementUnit.Cup,
            null,
            null,
            null,
            false,
            null,
            null,
            null,
            "Flour",
            null,
            false,
            null,
            new RecipeMeasurement(25, MeasurementUnit.Oz),
            preferAlternative
        );

    [TestMethod]
    [DataRow("5 cups (25 ounces) flour", 5, 25)]
    [DataRow("1 1/2 cups (7 1/2 oz) flour", 1.5, 7.5)]
    [DataRow("1½ cups (7½ ounces) flour", 1.5, 7.5)]
    [DataRow("5 cups flour (25 ounces)", 5, 25)]
    [DataRow("5 cups or 25 ounces flour", 5, 25)]
    public void SourceAlternatives_PreserveExplicitPairs(
        string text,
        double quantity,
        double alternative
    )
    {
        var pair = SourceMeasurementParser.FindAlternative(text);
        Assert.IsNotNull(pair);
        Assert.AreEqual((decimal)quantity, pair.Value.Primary.Quantity);
        Assert.AreEqual((decimal)alternative, pair.Value.Alternative.Quantity);
        Assert.AreEqual(MeasurementUnit.Cup, pair.Value.Primary.Unit);
        Assert.AreEqual(MeasurementUnit.Oz, pair.Value.Alternative.Unit);
    }

    [TestMethod]
    [DataRow("5 cups flour, plus 1 cup for feeding")]
    [DataRow("5 cups flour (plus 1 ounce for dusting)")]
    [DataRow("5 cups flour (1 ounce yeast)")]
    [DataRow("5 cups flour or 1 ounce yeast")]
    [DataRow("-5 cups (25 ounces) flour")]
    public void SourceAlternatives_DoNotInferExtrasOrDifferentIngredients(string text) =>
        Assert.IsNull(SourceMeasurementParser.FindAlternative(text));

    [TestMethod]
    public void SourceMeasurements_HandleRangesCountsAndOtherVolumeUnitsWithoutAcceptingInvalidNumbers()
    {
        var amounts = SourceMeasurementParser.Parse(
            "Add 1-2 cloves garlic, 1 quart water and 250 millilitres broth."
        );
        Assert.HasCount(3, amounts);
        Assert.AreEqual(MeasurementUnit.Clove, amounts[0].Unit);
        Assert.AreEqual(2m, amounts[0].MaxQuantity);
        Assert.AreEqual(MeasurementUnit.Qt, amounts[1].Unit);
        Assert.AreEqual(MeasurementUnit.Ml, amounts[2].Unit);
        Assert.IsEmpty(SourceMeasurementParser.Parse("−5 cups flour"));
        Assert.IsEmpty(SourceMeasurementParser.Parse("1-1000000 cups flour"));
    }

    [TestMethod]
    public void MeasurementValidation_RejectsInvalidValues()
    {
        foreach (
            var invalid in new RecipeMeasurement[]
            {
                new(0, MeasurementUnit.G),
                new(-1, MeasurementUnit.G),
                new(0.00001m, MeasurementUnit.G),
                new(1, null),
                new(1, (MeasurementUnit)999),
                new(2, MeasurementUnit.G, 1),
                new(1, MeasurementUnit.Custom),
                new(1, MeasurementUnit.G, CustomUnitName: new string('x', 51)),
            }
        )
            Assert.Throws<DomainValidationException>(() =>
                RecipeMeasurementValidation.Validate(invalid)
            );
    }

    [TestMethod]
    public void TrySumCompoundLeadingMeasurement_SumsAcrossParentheticalWeightOrVolumeEquivalent()
    {
        var flour = SourceMeasurementParser.TrySumCompoundLeadingMeasurement(
            "1 1/2 cups (7 1/2 ounces) plus 1 tablespoon all-purpose flour"
        );
        Assert.IsNotNull(flour);
        Assert.AreEqual(1.5625m, flour.Quantity);
        Assert.AreEqual(MeasurementUnit.Cup, flour.Unit);

        var spans = SourceMeasurementParser.FindSpans(
            "1 1/2 cups (7 1/2 ounces) plus 1 tablespoon all-purpose flour"
        );
        Assert.HasCount(1, spans);
        Assert.AreEqual(25m, spans[0].Measurement.Quantity);
        Assert.AreEqual(MeasurementUnit.Tbsp, spans[0].Measurement.Unit);
        Assert.IsNull(spans[0].Alternative);

        var oil = SourceMeasurementParser.TrySumCompoundLeadingMeasurement(
            "6 tablespoons plus 1 teaspoon extra-virgin olive oil"
        );
        Assert.IsNotNull(oil);
        Assert.AreEqual(6.3333m, oil.Quantity);
        Assert.AreEqual(MeasurementUnit.Tbsp, oil.Unit);

        Assert.IsNull(
            SourceMeasurementParser.TrySumCompoundLeadingMeasurement(
                "1 cup water plus more for rinsing"
            )
        );
    }

    [TestMethod]
    public void TrySumCompoundLeadingMeasurement_SumsAcrossInterveningCommaPlusPrepWithoutMergingDistinctParts()
    {
        var butter = SourceMeasurementParser.TrySumCompoundLeadingMeasurement(
            "1 tablespoon unsalted butter, softened, plus 4 tablespoons melted, divided"
        );
        Assert.IsNotNull(butter);
        Assert.AreEqual(5m, butter.Quantity);
        Assert.AreEqual(MeasurementUnit.Tbsp, butter.Unit);

        var repeatedButter = SourceMeasurementParser.TrySumCompoundLeadingMeasurement(
            "1 tablespoon unsalted butter, melted, plus 1 tablespoon unsalted butter"
        );
        Assert.IsNotNull(repeatedButter);
        Assert.AreEqual(2m, repeatedButter.Quantity);
        Assert.AreEqual(MeasurementUnit.Tbsp, repeatedButter.Unit);

        Assert.IsNull(
            SourceMeasurementParser.TrySumCompoundLeadingMeasurement(
                "2 large whole eggs, plus 2 large egg yolks"
            )
        );
        Assert.IsNull(
            SourceMeasurementParser.TrySumCompoundLeadingMeasurement(
                "1 tablespoon vegetable oil, plus 1 tablespoon unsalted butter"
            )
        );
    }

    [TestMethod]
    [DataRow("[PICADA]", "Picada")]
    [DataRow("FOR THE SAUCE:", "For the Sauce")]
    [DataRow("HERB-GARLIC BUTTER", "Herb-Garlic Butter")]
    [DataRow("For the Dough", "For the Dough")]
    [DataRow("Ingredients:", null)]
    [DataRow("General", null)]
    public void FormatSectionDisplayName_NormalizesCapsAndFiltersGenericHeaders(
        string? rawSection,
        string? expected
    )
    {
        Assert.AreEqual(expected, RecipeUnitAndSectionHelpers.FormatSectionDisplayName(rawSection));
    }

    [TestMethod]
    public void NormalizeRecipeReferenceTitle_StripsSectionAnnotatedPageReferences()
    {
        Assert.AreEqual(
            "Free-Form Summer Fruit Tart",
            RecipeUnitAndSectionHelpers.NormalizeRecipeReferenceTitle(
                "Free-Form Summer Fruit Tart (Rustic Tart Dough; this page)"
            )
        );
        Assert.AreEqual(
            "Classic Tart",
            RecipeUnitAndSectionHelpers.NormalizeRecipeReferenceTitle(
                "Classic Tart (Quick Crust; facing page)"
            )
        );
    }
}

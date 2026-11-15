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

    private static IngestionExtractedDraftDto Compile(ExtractedRecipeDto extracted)
    {
        var validation = RecipeValidator.Validate(extracted);
        var matches = EntityCandidateMatcher.Match(extracted, [], []);
        return RecipeDraftCompiler.Compile(extracted, validation, matches);
    }

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
    public void Compile_Starter_PreservesPairsAndIndividualFlatStepAmounts()
    {
        var extracted = new ExtractedRecipeDto(
            Title: "Synthetic Starter",
            Ingredients:
            [
                new(
                    RefId: "ing_1",
                    CanonicalFoodItem: "flour",
                    DisplayName: "flour",
                    Quantity: 5m,
                    Unit: "cup",
                    AltQuantity: 25m,
                    AltUnit: "oz",
                    RawText: "5 cups (25 ounces) flour"
                ),
            ],
            Steps:
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Combine {{ing_1_a}} with water.",
                    IngredientTokens: [new("ing_1_a", "ing_1", 1m, "cup")]
                ),
                new(
                    StepNumber: 2,
                    TemplateText: "Feed with {{ing_1_b}}.",
                    IngredientTokens: [new("ing_1_b", "ing_1", 4m, "cups")]
                ),
            ]
        );
        var draft = Compile(extracted);
        Assert.AreEqual(5m, draft.Ingredients![0].Quantity);
        Assert.AreEqual(25m, draft.Ingredients[0].AlternativeMeasurement!.Quantity);
        Assert.AreEqual(MeasurementUnit.Cup, draft.Ingredients[0].Unit);
        Assert.AreEqual(1m, draft.Steps![0].IngredientAmounts![0].Measurement.Quantity);
        Assert.AreEqual(4m, draft.Steps[1].IngredientAmounts![0].Measurement.Quantity);
        StringAssert.Contains(
            draft.Steps[0].Instruction,
            "||{\"ingredient\":\"flour\",\"amount\":1,\"unit\":\"cup\",\"type\":\"Ingredient\"}||"
        );
        StringAssert.Contains(
            draft.Steps[1].Instruction,
            "||{\"ingredient\":\"flour\",\"amount\":4,\"unit\":\"cup\",\"type\":\"Ingredient\"}||"
        );
        Assert.IsEmpty(draft.ValidationIssues!);
        Assert.AreEqual(0.96m, draft.ConfidenceScore);
    }

    [TestMethod]
    public void Compile_OverflowingStepAmounts_AreFlaggedInValidationIssuesAndLowerConfidence()
    {
        var extracted = new ExtractedRecipeDto(
            Title: "Synthetic Starter",
            Ingredients:
            [
                new(
                    RefId: "ing_1",
                    CanonicalFoodItem: "flour",
                    DisplayName: "flour",
                    Quantity: 5m,
                    Unit: "cup",
                    AltQuantity: 25m,
                    AltUnit: "oz",
                    RawText: "5 cups (25 oz) flour"
                ),
            ],
            Steps:
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Add {{ing_1_a}} then {{ing_1_b}}.",
                    IngredientTokens:
                    [
                        new("ing_1_a", "ing_1", 4m, "cup"),
                        new("ing_1_b", "ing_1", 3m, "cup"),
                    ]
                ),
            ]
        );
        var draft = Compile(extracted);
        Assert.IsNotEmpty(draft.ValidationIssues!);
        Assert.IsLessThan(0.90m, draft.ConfidenceScore);
    }

    [TestMethod]
    [DataRow(0.5, 2.5, "71")]
    [DataRow(1.0, 5.0, "142")]
    [DataRow(2.0, 10.0, "283")]
    public void StepAmounts_ScaleUsingRecipeSpecificWeightRatio(
        double scale,
        double ounces,
        string grams
    )
    {
        var ingredient = Flour();
        var amount = new RecipeMeasurement(1, MeasurementUnit.Cup);
        var original = CulinaryMath.ScaleStepIngredient(
            ingredient,
            amount,
            (decimal)scale,
            UnitSystem.Original
        );
        Assert.AreEqual((decimal)ounces, original.Quantity);
        Assert.AreEqual(MeasurementUnit.Oz, original.Unit);
        var metric = CulinaryMath.ScaleStepIngredient(
            ingredient,
            amount,
            (decimal)scale,
            UnitSystem.Metric
        );
        Assert.AreEqual(grams, metric.FormattedAmount);
        Assert.AreEqual(MeasurementUnit.G, metric.Unit);
    }

    [TestMethod]
    public void StepAmounts_HandleSameDimensionAndInverseAlternativeConversion()
    {
        var ingredient = Flour();
        var fromTablespoons = CulinaryMath.ScaleStepIngredient(
            ingredient,
            new RecipeMeasurement(16, MeasurementUnit.Tbsp),
            1,
            UnitSystem.Original
        );
        Assert.IsTrue(Math.Abs(fromTablespoons.Quantity!.Value - 5m) < 0.0001m);
        var fromWeight = CulinaryMath.ScaleStepIngredient(
            Flour(false),
            new RecipeMeasurement(5, MeasurementUnit.Oz),
            1,
            UnitSystem.Original
        );
        Assert.AreEqual(1m, fromWeight.Quantity);
        Assert.AreEqual(MeasurementUnit.Cup, fromWeight.Unit);
        var authoredCup = Flour() with
        {
            Quantity = 1,
            AlternativeMeasurement = new(250, MeasurementUnit.Ml),
        };
        Assert.AreEqual(
            250m,
            CulinaryMath
                .ScaleStepIngredient(
                    authoredCup,
                    new RecipeMeasurement(1, MeasurementUnit.Cup),
                    1,
                    UnitSystem.Metric
                )
                .Quantity
        );
        var authoredLiters = CulinaryMath.ScaleStepIngredient(
            authoredCup,
            new RecipeMeasurement(1, MeasurementUnit.L),
            1,
            UnitSystem.Original
        );
        Assert.AreEqual(1m, authoredLiters.Quantity);
        Assert.AreEqual(MeasurementUnit.L, authoredLiters.Unit);
    }

    [TestMethod]
    public void StepAmounts_UnassignedAreNameOnlyAndIncompatibleUnitsAreNotInvented()
    {
        var ingredient = Flour();
        var unassigned = CulinaryMath.ScaleStepIngredient(ingredient, null, 2, UnitSystem.Metric);
        Assert.IsNull(unassigned.Quantity);
        Assert.AreEqual("", unassigned.UnitLabel);
        var authored = CulinaryMath.ScaleStepIngredient(
            ingredient,
            new RecipeMeasurement(1, MeasurementUnit.Pinch),
            2,
            UnitSystem.Metric
        );
        Assert.AreEqual(2m, authored.Quantity);
        Assert.AreEqual(MeasurementUnit.Pinch, authored.Unit);
        var withoutEquivalent = CulinaryMath.ScaleStepIngredient(
            ingredient with
            {
                AlternativeMeasurement = null,
            },
            new RecipeMeasurement(1, MeasurementUnit.Cup),
            1,
            UnitSystem.Metric
        );
        Assert.AreEqual(MeasurementUnit.Ml, withoutEquivalent.Unit);
    }

    [TestMethod]
    public void MeasurementValidation_RejectsInvalidValuesAndMissingIngredientIndex()
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
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<RecipeStepIngredientAmountInput>(
                """{"Measurement":{"Quantity":1,"Unit":1}}"""
            )
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
}

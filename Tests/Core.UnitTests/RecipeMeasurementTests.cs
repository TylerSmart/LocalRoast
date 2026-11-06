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

    private static IngestionExtractedDraftDto Normalize(
        LlmExtractedRecipePayload raw,
        string? source = null
    ) =>
        RecipeDraftNormalizer.Normalize(
            raw,
            [],
            [],
            null,
            null,
            null,
            null,
            null,
            rawSourceText: source
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
    public void Normalize_Starter_PreservesPairsAndIndividualFlatStepAmounts()
    {
        var raw = new LlmExtractedRecipePayload(
            Title: "Synthetic Starter",
            ConfidenceScore: 0.98m,
            Ingredients:
            [
                new(null, 25, null, "oz", "flour", OriginalRawText: "5 cups (25 ounces) flour"),
            ],
            Steps:
            [
                new(
                    null,
                    "4A. Combine the listed flour with water.",
                    [0],
                    IngredientAmounts: [new(0, new(1, "cup"))],
                    OriginalRawText: "4A. Combine 1 cup flour with water."
                ),
                new(
                    null,
                    "4B: Feed with the listed flour.",
                    [0],
                    IngredientAmounts: [new(0, new(4, "cups"))],
                    OriginalRawText: "4B: Feed with 4 cups flour."
                ),
            ]
        );
        var draft = Normalize(raw);
        Assert.AreEqual(5m, draft.Ingredients![0].Quantity);
        Assert.AreEqual(25m, draft.Ingredients[0].AlternativeMeasurement!.Quantity);
        Assert.AreEqual(MeasurementUnit.Cup, draft.Ingredients[0].Unit);
        Assert.AreEqual(1m, draft.Steps![0].IngredientAmounts![0].Measurement.Quantity);
        Assert.AreEqual(4m, draft.Steps[1].IngredientAmounts![0].Measurement.Quantity);
        Assert.AreEqual("Combine the listed flour with water.", draft.Steps[0].Instruction);
        Assert.AreEqual("Feed with the listed flour.", draft.Steps[1].Instruction);
        Assert.IsEmpty(draft.MeasurementWarnings!);
        Assert.AreEqual(0.98m, draft.ConfidenceScore);
    }

    [TestMethod]
    public void Normalize_UnsupportedOrInventedAmounts_AreFlaggedAndCannotRemainHighConfidence()
    {
        var raw = new LlmExtractedRecipePayload(
            Title: "Synthetic Starter",
            ConfidenceScore: 0.99m,
            Ingredients:
            [
                new(null, 5, null, "cup", "flour", AlternativeMeasurement: new(25, "oz")),
            ],
            Steps:
            [
                new(
                    null,
                    "Add the listed flour.",
                    [0],
                    IngredientAmounts:
                    [
                        new(0, new(5, "cup")),
                        new(null, new(1, "cup")),
                        new(9, new(1, "cup")),
                    ],
                    OriginalRawText: "Add 1 cup flour."
                ),
            ]
        );
        var draft = Normalize(raw);
        Assert.IsEmpty(draft.Steps![0].IngredientAmounts!);
        Assert.HasCount(4, draft.MeasurementWarnings!);
        Assert.AreEqual(0.70m, draft.ConfidenceScore);
    }

    [TestMethod]
    public void Normalize_UnconfirmedSourceExcerpt_FlagsMeasurementsForReview()
    {
        var raw = new LlmExtractedRecipePayload(
            Title: "Starter",
            Ingredients:
            [
                new(null, 5, null, "cup", "flour", OriginalRawText: "5 cups (25 oz) flour"),
            ],
            Steps:
            [
                new(
                    null,
                    "Use the listed flour.",
                    [0],
                    IngredientAmounts: [new(0, new(1, "cup"))],
                    OriginalRawText: "Add 1 cup flour."
                ),
            ]
        );
        var draft = Normalize(raw, "Actual source says something else.");
        Assert.HasCount(2, draft.MeasurementWarnings!);
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
}

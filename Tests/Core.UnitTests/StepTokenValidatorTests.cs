using System.Text.Json.Nodes;
using App.Core.Ingestion;
using App.Core.Recipes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class StepTokenValidatorTests
{
    [TestMethod]
    public void BuildAndValidateStep_HandlesAllSixTokenTypesAndRanges()
    {
        const string original =
            "Heat 2 tablespoons oil mixture in a 12-inch skillet in a 350 to 375 degrees oven. Add 1 to 2 tablespoons water and 12-cup Bundt pan, cook 10 to 15 minutes.";
        var rawTokens = new JsonArray
        {
            "Heat ",
            new JsonObject
            {
                ["type"] = "mixture",
                ["name"] = "oil mixture",
                ["quantity"] = 2,
                ["unit"] = "tablespoon",
            },
            " in a ",
            new JsonObject
            {
                ["type"] = "dimension",
                ["amount"] = 12,
                ["unit"] = "inch",
                ["hyphenated"] = true,
            },
            " skillet in a ",
            new JsonObject
            {
                ["type"] = "temperature",
                ["amount"] = 350,
                ["maxAmount"] = 375,
                ["unit"] = "fahrenheit",
            },
            " oven. Add ",
            new JsonObject
            {
                ["type"] = "ingredient",
                ["ingredientIndex"] = 0,
                ["name"] = "water",
                ["quantity"] = 1,
                ["maxQuantity"] = 2,
                ["unit"] = "tablespoon",
            },
            " and ",
            new JsonObject
            {
                ["type"] = "fixed",
                ["name"] = "Bundt pan",
                ["amount"] = 12,
                ["unit"] = "cup",
                ["hyphenated"] = true,
            },
            ", cook ",
            new JsonObject
            {
                ["type"] = "timer",
                ["amount"] = 10,
                ["maxAmount"] = 15,
                ["unit"] = "minute",
                ["label"] = "Cook mixture",
            },
            ".",
        };

        var result = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: original,
            rawTokensJson: rawTokens,
            ingredientCount: 1
        );

        Assert.HasCount(
            0,
            result.ValidationWarnings,
            string.Join(" | ", result.ValidationWarnings)
        );
        Assert.HasCount(1, result.IngredientIndices);
        Assert.AreEqual(0, result.IngredientIndices[0]);
        Assert.HasCount(1, result.IngredientAmounts);
        Assert.AreEqual(1m, result.IngredientAmounts[0].Measurement.Quantity);
        Assert.AreEqual(2m, result.IngredientAmounts[0].Measurement.MaxQuantity);
        Assert.HasCount(1, result.Timers);
        Assert.AreEqual(600, result.Timers[0].DurationSeconds);

        var parsed = StepInstructionParser.ExtractTokens(result.CompiledInstruction);
        Assert.IsTrue(parsed.OfType<StepMixtureToken>().Any());
        Assert.IsTrue(parsed.OfType<StepDimensionToken>().Any(d => d.Hyphenated && d.Value == 12m));
        Assert.IsTrue(
            parsed
                .OfType<StepTemperatureToken>()
                .Any(t => t.Fahrenheit == 350m && t.MaxFahrenheit == 375m)
        );
        Assert.IsTrue(
            parsed.OfType<StepIngredientToken>().Any(i => i.Amount == 1m && i.MaxAmount == 2m)
        );
        Assert.IsTrue(
            parsed
                .OfType<StepFixedToken>()
                .Any(f => f.Hyphenated && f.Amount == 12m && f.Name == "Bundt pan")
        );
        Assert.IsTrue(
            parsed.OfType<StepTimerToken>().Any(t => t.Seconds == 600 && t.MaxSeconds == 900)
        );
    }

    [TestMethod]
    public void BuildAndValidateStep_AllowsRotationAngleAndFlagsProseDrift()
    {
        const string rotationOriginal = "Rotate baking sheet 180 degrees.";
        var cleanTokens = new JsonArray { rotationOriginal };
        var cleanRes = StepTokenValidator.BuildAndValidateStep(
            1,
            null,
            rotationOriginal,
            cleanTokens,
            0
        );
        Assert.HasCount(0, cleanRes.ValidationWarnings);

        var driftedTokens = new JsonArray { "Bake in oven." };
        var driftedRes = StepTokenValidator.BuildAndValidateStep(
            1,
            null,
            "Bake in oven for 25 minutes until golden brown.",
            driftedTokens,
            0
        );
        Assert.IsNotEmpty(driftedRes.ValidationWarnings);
    }

    [TestMethod]
    public void TryFindIncompatibleAlternateMeasurement_FlagsVolumePlusSaltOrMismatchedWeight()
    {
        var splitPlus = new List<Stage1AlternateMeasurementDto> { new(1m, "teaspoon") };
        var incompatible = StepTokenValidator.TryFindIncompatibleAlternateMeasurement(
            6m,
            null,
            "tablespoon",
            splitPlus,
            out var reason
        );
        Assert.IsTrue(incompatible);
        Assert.IsFalse(string.IsNullOrWhiteSpace(reason));

        var validWeight = new List<Stage1AlternateMeasurementDto> { new(5m, "oz") };
        var compatible = StepTokenValidator.TryFindIncompatibleAlternateMeasurement(
            1m,
            null,
            "cup",
            validWeight,
            out _
        );
        Assert.IsFalse(compatible);
    }

    [TestMethod]
    public void ValidateStepTokens_DimensionWithUnitAbsentFromSource_EmitsWarning()
    {
        var rawTokens = new JsonArray
        {
            "Strain juice mixture into ",
            new JsonObject
            {
                ["type"] = "dimension",
                ["amount"] = 1,
                ["maxAmount"] = null,
                ["unit"] = "yard",
                ["hyphenated"] = true,
            },
            " pitcher.",
        };

        var result = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Strain juice mixture into 1-quart pitcher.",
            rawTokensJson: rawTokens,
            ingredientCount: 0
        );

        Assert.IsTrue(
            result.ValidationWarnings.Any(w =>
                w.Contains("yard", StringComparison.OrdinalIgnoreCase)
                && w.Contains("does not appear", StringComparison.OrdinalIgnoreCase)
            ),
            string.Join(" | ", result.ValidationWarnings)
        );
    }

    [TestMethod]
    public void TryFindIncompatibleAlternateMeasurement_RateUnitPerPound_ReturnsTrue()
    {
        var invalidWithPound = StepTokenValidator.TryFindIncompatibleAlternateMeasurement(
            primaryQty: 1.25m,
            primaryMaxQty: null,
            primaryUnit: "pound",
            alternates: [new Stage1AlternateMeasurementDto(26m, "per pound")],
            out var reason1
        );
        Assert.IsTrue(invalidWithPound);
        StringAssert.Contains(reason1, "per-unit rate");

        var invalidCountOnly = StepTokenValidator.TryFindIncompatibleAlternateMeasurement(
            primaryQty: 18m,
            primaryMaxQty: null,
            primaryUnit: null,
            alternates: [new Stage1AlternateMeasurementDto(31m, "per pound")],
            out var reason2
        );
        Assert.IsTrue(invalidCountOnly);
        StringAssert.Contains(reason2, "per-unit rate");
    }
}

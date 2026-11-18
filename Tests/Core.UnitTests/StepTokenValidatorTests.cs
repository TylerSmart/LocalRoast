using System.Text.Json.Nodes;
using App.Core.FoodCatalog;
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

    [TestMethod]
    public void SanitizeRawTokens_AdjacentStringsAfterTimerRange_StripsToMinutesOnlyWhenRangePresent()
    {
        var withRange = new JsonArray
        {
            "Simmer ",
            new JsonObject
            {
                ["type"] = "timer",
                ["amount"] = 2,
                ["maxAmount"] = 3,
                ["unit"] = "minute",
                ["label"] = "simmer",
            },
            " to ",
            " minutes.",
        };

        var cleanResult = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Simmer 2 to 3 minutes.",
            rawTokensJson: withRange,
            ingredientCount: 0
        );
        Assert.AreEqual(
            0,
            cleanResult.ValidationWarnings.Count,
            string.Join(" | ", cleanResult.ValidationWarnings)
        );
        var rendered = StepTokenValidator.RenderForValidation(cleanResult.ParsedTokens);
        Assert.AreEqual("Simmer 2 to 3 minutes.", rendered);

        var withoutRange = new JsonArray
        {
            "Simmer ",
            new JsonObject
            {
                ["type"] = "timer",
                ["amount"] = 2,
                ["maxAmount"] = null,
                ["unit"] = "minute",
                ["label"] = "simmer",
            },
            " to ",
            " minutes.",
        };

        var warnResult = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Simmer 2 to 3 minutes.",
            rawTokensJson: withoutRange,
            ingredientCount: 0
        );
        Assert.IsTrue(
            warnResult.ValidationWarnings.Any(w =>
                w.Contains("Duplicate or leftover time unit", StringComparison.OrdinalIgnoreCase)
            ),
            string.Join(" | ", warnResult.ValidationWarnings)
        );
    }

    [TestMethod]
    public void ValidateStepTokens_TimerUnitAbsentFromSource_EmitsWarning_ValidHourPasses()
    {
        var badMonth = new JsonArray
        {
            "Refrigerate for up to ",
            new JsonObject
            {
                ["type"] = "timer",
                ["amount"] = 1,
                ["maxAmount"] = null,
                ["unit"] = "day",
                ["label"] = "Refrigerate",
            },
            ".",
        };
        var monthRes = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Refrigerate for up to 1 month.",
            rawTokensJson: badMonth,
            ingredientCount: 0
        );
        Assert.IsTrue(
            monthRes.ValidationWarnings.Any(w =>
                w.Contains(
                    "does not appear in original instruction",
                    StringComparison.OrdinalIgnoreCase
                )
            ),
            string.Join(" | ", monthRes.ValidationWarnings)
        );

        var validHours = new JsonArray
        {
            "Refrigerate for up to ",
            new JsonObject
            {
                ["type"] = "timer",
                ["amount"] = 24,
                ["maxAmount"] = null,
                ["unit"] = "hour",
                ["label"] = "Refrigerate",
            },
            ".",
        };
        var hourRes = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Refrigerate for up to 24 hours.",
            rawTokensJson: validHours,
            ingredientCount: 0
        );
        Assert.AreEqual(
            0,
            hourRes.ValidationWarnings.Count,
            string.Join(" | ", hourRes.ValidationWarnings)
        );
    }

    [TestMethod]
    public void ValidateStepTokens_TurnPan90DegreesAndMeasuringCupFixed_PassCleanly()
    {
        var turnTokens = new JsonArray
        {
            "Turn pan 90 degrees and whisk in ",
            new JsonObject
            {
                ["type"] = "fixed",
                ["name"] = "liquid measuring cup",
                ["amount"] = 4,
                ["maxAmount"] = null,
                ["unit"] = "cup",
                ["hyphenated"] = true,
            },
            ".",
        };

        var result = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 3,
            sectionName: null,
            originalInstruction: "Turn pan 90 degrees and whisk in 4-cup liquid measuring cup.",
            rawTokensJson: turnTokens,
            ingredientCount: 0
        );

        Assert.AreEqual(
            0,
            result.ValidationWarnings.Count,
            string.Join(" | ", result.ValidationWarnings)
        );
    }

    [TestMethod]
    public void ValidateStepTokens_DroppedPrepositionalPhraseOrClause_EmitsWarning()
    {
        var droppedWaterTokens = new JsonArray
        {
            "Place the ",
            new JsonObject
            {
                ["type"] = "fixed",
                ["name"] = "saucepan",
                ["amount"] = 2,
                ["maxAmount"] = null,
                ["unit"] = "quart",
                ["hyphenated"] = true,
            },
            ".",
        };
        var res1 = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Place the water in a 2-quart saucepan.",
            rawTokensJson: droppedWaterTokens,
            ingredientCount: 0
        );
        Assert.IsTrue(
            res1.ValidationWarnings.Any(w =>
                w.Contains("water in a", StringComparison.OrdinalIgnoreCase)
            ),
            string.Join(" | ", res1.ValidationWarnings)
        );

        var droppedClauseTokens = new JsonArray
        {
            "Fold ",
            new JsonObject
            {
                ["type"] = "dimension",
                ["amount"] = 8,
                ["maxAmount"] = null,
                ["unit"] = "inch",
                ["hyphenated"] = true,
            },
            " sides of egg toward middle to create ",
            new JsonObject
            {
                ["type"] = "dimension",
                ["amount"] = 8,
                ["maxAmount"] = null,
                ["unit"] = "inch",
                ["hyphenated"] = true,
            },
            " by ",
            new JsonObject
            {
                ["type"] = "dimension",
                ["amount"] = 4,
                ["maxAmount"] = null,
                ["unit"] = "inch",
                ["hyphenated"] = true,
            },
            " rectangle.",
        };
        var res2 = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 5,
            sectionName: null,
            originalInstruction: "Fold 4 sides of egg toward middle to create 8-inch square. Fold square in half to create 8 by 4-inch rectangle.",
            rawTokensJson: droppedClauseTokens,
            ingredientCount: 0
        );
        Assert.IsTrue(
            res2.ValidationWarnings.Any(w =>
                w.Contains("Dropped prose clause", StringComparison.OrdinalIgnoreCase)
            ),
            string.Join(" | ", res2.ValidationWarnings)
        );
    }

    [TestMethod]
    public void Helpers_SelfCustomUnitAndEggPartNormalization_WorkAsExpected()
    {
        Assert.IsTrue(RecipeUnitAndSectionHelpers.IsRedundantSelfCustomUnit("egg", "eggs"));
        Assert.IsTrue(
            RecipeUnitAndSectionHelpers.IsRedundantSelfCustomUnit("tortilla", "corn tortillas")
        );
        Assert.IsTrue(RecipeUnitAndSectionHelpers.IsRedundantSelfCustomUnit("leaf", "bay leaves"));
        Assert.IsTrue(
            RecipeUnitAndSectionHelpers.IsRedundantSelfCustomUnit("wrapper", "rice paper wrappers")
        );
        Assert.IsFalse(RecipeUnitAndSectionHelpers.IsRedundantSelfCustomUnit("clove", "garlic"));
        Assert.IsFalse(
            RecipeUnitAndSectionHelpers.IsRedundantSelfCustomUnit("sprig", "fresh thyme")
        );

        Assert.AreEqual(
            "egg white",
            FoodNameSanitizer.NormalizeCanonicalFoodName("large egg whites")
        );
        Assert.AreEqual(
            "egg yolk",
            FoodNameSanitizer.NormalizeCanonicalFoodName("large egg yolks")
        );
    }
}

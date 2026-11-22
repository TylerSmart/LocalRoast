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

    [TestMethod]
    public void SanitizeRawTokens_DuplicateRangeUpperBoundAndSplitTimer_HandlesCleanly()
    {
        var dupTrailingBound = new JsonArray
        {
            "Bake until set, ",
            new JsonObject
            {
                ["type"] = "timer",
                ["amount"] = 6,
                ["maxAmount"] = 8,
                ["unit"] = "minute",
                ["label"] = "bake",
            },
            " to 8 minutes.",
        };
        var res1 = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 4,
            sectionName: null,
            originalInstruction: "Bake until set, 6 to 8 minutes.",
            rawTokensJson: dupTrailingBound,
            ingredientCount: 0
        );
        Assert.IsEmpty(res1.ValidationWarnings, string.Join(" | ", res1.ValidationWarnings));
        Assert.AreEqual(
            "Bake until set, 6 to 8 minutes.",
            StepTokenValidator.RenderForValidation(res1.ParsedTokens)
        );

        var splitRangeWithRedundantMax = new JsonArray
        {
            "Cook ",
            new JsonObject
            {
                ["type"] = "timer",
                ["amount"] = 8,
                ["maxAmount"] = 10,
                ["unit"] = "minute",
                ["label"] = "cook",
            },
            " to ",
            new JsonObject
            {
                ["type"] = "timer",
                ["amount"] = 10,
                ["maxAmount"] = null,
                ["unit"] = "minute",
                ["label"] = "cook",
            },
            ".",
        };
        var res2 = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 2,
            sectionName: null,
            originalInstruction: "Cook 8 to 10 minutes.",
            rawTokensJson: splitRangeWithRedundantMax,
            ingredientCount: 0
        );
        Assert.IsEmpty(res2.ValidationWarnings, string.Join(" | ", res2.ValidationWarnings));
        Assert.AreEqual(
            "Cook 8 to 10 minutes.",
            StepTokenValidator.RenderForValidation(res2.ParsedTokens)
        );

        var trueSplitTimers = new JsonArray
        {
            "Cook ",
            new JsonObject
            {
                ["type"] = "timer",
                ["amount"] = 8,
                ["maxAmount"] = null,
                ["unit"] = "minute",
                ["label"] = "cook",
            },
            " to ",
            new JsonObject
            {
                ["type"] = "timer",
                ["amount"] = 10,
                ["maxAmount"] = null,
                ["unit"] = "minute",
                ["label"] = "cook",
            },
            ".",
        };
        var res3 = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 2,
            sectionName: null,
            originalInstruction: "Cook 8 to 10 minutes.",
            rawTokensJson: trueSplitTimers,
            ingredientCount: 0
        );
        Assert.IsTrue(
            res3.ValidationWarnings.Any(w =>
                w.Contains("Split single-unit time range", StringComparison.OrdinalIgnoreCase)
            ),
            string.Join(" | ", res3.ValidationWarnings)
        );
    }

    [TestMethod]
    public void BuildAndValidateStep_SwappedClosingParenDotAndNullNameFixedYield_WorkCleanly()
    {
        var swappedPunct = new JsonArray
        {
            "Serve warm. (Stew can be refrigerated for up to ",
            new JsonObject
            {
                ["type"] = "timer",
                ["amount"] = 3,
                ["maxAmount"] = null,
                ["unit"] = "day",
                ["label"] = "Refrigerate",
            },
            " days).",
        };
        var res1 = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 5,
            sectionName: null,
            originalInstruction: "Serve warm. (Stew can be refrigerated for up to 3 days.)",
            rawTokensJson: swappedPunct,
            ingredientCount: 0
        );
        Assert.IsEmpty(res1.ValidationWarnings, string.Join(" | ", res1.ValidationWarnings));
        Assert.AreEqual(
            "Serve warm. (Stew can be refrigerated for up to 3 days.)",
            StepTokenValidator.RenderForValidation(res1.ParsedTokens)
        );

        var nounlessFixed = new JsonArray
        {
            "Set aside (you should have ",
            new JsonObject
            {
                ["type"] = "fixed",
                ["name"] = null,
                ["amount"] = 5,
                ["maxAmount"] = 6,
                ["unit"] = "cup",
                ["hyphenated"] = false,
            },
            ").",
        };
        var res2 = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Set aside (you should have 5 to 6 cups).",
            rawTokensJson: nounlessFixed,
            ingredientCount: 0
        );
        Assert.IsEmpty(res2.ValidationWarnings, string.Join(" | ", res2.ValidationWarnings));
        Assert.AreEqual(
            "Set aside (you should have 5 to 6 cups).",
            StepTokenValidator.RenderForValidation(res2.ParsedTokens)
        );
    }

    [TestMethod]
    public void BarePinchSaltAndRemainingPinchSaltDoNotFlagInventedQuantities()
    {
        var tokens1 = new JsonArray { "Whisk eggs, milk, and pinch salt." };
        var res1 = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Whisk eggs, milk, and pinch salt.",
            rawTokensJson: tokens1,
            ingredientCount: 3
        );
        Assert.IsEmpty(res1.ValidationWarnings, string.Join(" | ", res1.ValidationWarnings));

        var tokens2 = new JsonArray { "Season with remaining pinch salt and stir." };
        var res2 = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 2,
            sectionName: null,
            originalInstruction: "Season with remaining pinch salt and stir.",
            rawTokensJson: tokens2,
            ingredientCount: 1
        );
        Assert.IsEmpty(res2.ValidationWarnings, string.Join(" | ", res2.ValidationWarnings));
    }

    [TestMethod]
    public void DanglingOfTheStrippedWhenTokenHasNameAndPreservedWhenFixedNameIsNull()
    {
        var ingOfThe = new JsonArray
        {
            "Pour ",
            new JsonObject
            {
                ["type"] = "ingredient",
                ["ingredientIndex"] = 0,
                ["name"] = "tomatoes",
                ["quantity"] = 2,
                ["unit"] = "cup",
            },
            " of the , crush, and bring to simmer.",
        };
        var resIng = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Pour 2 cups of the tomatoes, crush, and bring to simmer.",
            rawTokensJson: ingOfThe,
            ingredientCount: 1
        );
        Assert.IsEmpty(resIng.ValidationWarnings, string.Join(" | ", resIng.ValidationWarnings));
        Assert.AreEqual(
            "Pour 2 cups tomatoes, crush, and bring to simmer.",
            StepTokenValidator.RenderForValidation(resIng.ParsedTokens)
        );

        var fixedOfThe = new JsonArray
        {
            "Transfer ",
            new JsonObject
            {
                ["type"] = "fixed",
                ["name"] = "pulp",
                ["amount"] = 2,
                ["unit"] = "cup",
                ["hyphenated"] = false,
            },
            " of the to bowl.",
        };
        var resFixed = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Transfer 2 cups of the pulp to bowl.",
            rawTokensJson: fixedOfThe,
            ingredientCount: 0
        );
        Assert.IsEmpty(
            resFixed.ValidationWarnings,
            string.Join(" | ", resFixed.ValidationWarnings)
        );
        Assert.AreEqual(
            "Transfer 2 cups pulp to bowl.",
            StepTokenValidator.RenderForValidation(resFixed.ParsedTokens)
        );

        var nounlessFixed = new JsonArray
        {
            "Strain (about ",
            new JsonObject
            {
                ["type"] = "fixed",
                ["name"] = null,
                ["amount"] = 0.5,
                ["unit"] = "cup",
                ["hyphenated"] = false,
            },
            " of the liquid).",
        };
        var resNullName = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Strain (about 1/2 cup of the liquid).",
            rawTokensJson: nounlessFixed,
            ingredientCount: 0
        );
        Assert.IsEmpty(
            resNullName.ValidationWarnings,
            string.Join(" | ", resNullName.ValidationWarnings)
        );
        Assert.AreEqual(
            "Strain (about 1/2 cup of the liquid).",
            StepTokenValidator.RenderForValidation(resNullName.ParsedTokens)
        );
    }

    [TestMethod]
    public void LeadingDuplicatedRangeAndSplitTempUpperBoundAreSanitized()
    {
        var leadingDupTimer = new JsonArray
        {
            "Bake 10 to ",
            new JsonObject
            {
                ["type"] = "timer",
                ["amount"] = 10,
                ["maxAmount"] = 11,
                ["unit"] = "minute",
                ["label"] = "Bake cookies",
            },
            ".",
        };
        var resTimer = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Bake 10 to 11 minutes.",
            rawTokensJson: leadingDupTimer,
            ingredientCount: 0
        );
        Assert.IsEmpty(
            resTimer.ValidationWarnings,
            string.Join(" | ", resTimer.ValidationWarnings)
        );
        Assert.AreEqual(
            "Bake 10 to 11 minutes.",
            StepTokenValidator.RenderForValidation(resTimer.ParsedTokens)
        );

        var trailingDupTemp = new JsonArray
        {
            "Heat oil to ",
            new JsonObject
            {
                ["type"] = "temperature",
                ["amount"] = 340,
                ["maxAmount"] = 360,
                ["unit"] = "fahrenheit",
            },
            " to ",
            new JsonObject
            {
                ["type"] = "temperature",
                ["amount"] = 360,
                ["unit"] = "fahrenheit",
            },
            ".",
        };
        var resTemp = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Heat oil to 340 to 360 degrees.",
            rawTokensJson: trailingDupTemp,
            ingredientCount: 0
        );
        Assert.IsEmpty(resTemp.ValidationWarnings, string.Join(" | ", resTemp.ValidationWarnings));
        Assert.AreEqual(
            "Heat oil to 340–360°F.",
            StepTokenValidator.RenderForValidation(resTemp.ParsedTokens)
        );

        var splitTemp = new JsonArray
        {
            "Heat oil to ",
            new JsonObject
            {
                ["type"] = "temperature",
                ["amount"] = 340,
                ["unit"] = "fahrenheit",
            },
            " to ",
            new JsonObject
            {
                ["type"] = "temperature",
                ["amount"] = 360,
                ["unit"] = "fahrenheit",
            },
            ".",
        };
        var resSplitTemp = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Heat oil to 340 to 360 degrees.",
            rawTokensJson: splitTemp,
            ingredientCount: 0
        );
        Assert.IsTrue(
            resSplitTemp.ValidationWarnings.Any(w =>
                w.Contains("Split single-unit temperature range", StringComparison.Ordinal)
            ),
            string.Join(" | ", resSplitTemp.ValidationWarnings)
        );

        var mismatchRange = new JsonArray
        {
            "Bake at ",
            new JsonObject
            {
                ["type"] = "temperature",
                ["amount"] = 325,
                ["unit"] = "fahrenheit",
            },
            " to ",
            new JsonObject
            {
                ["type"] = "timer",
                ["amount"] = 350,
                ["unit"] = "minute",
                ["label"] = "Bake",
            },
            ".",
        };
        var resMismatch = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Bake at 325 to 350 degrees.",
            rawTokensJson: mismatchRange,
            ingredientCount: 0
        );
        Assert.IsTrue(
            resMismatch.ValidationWarnings.Any(w =>
                w.Contains("Unexpected range connector", StringComparison.Ordinal)
            ),
            string.Join(" | ", resMismatch.ValidationWarnings)
        );
    }

    [TestMethod]
    public void EmptyYieldParenAndDroppedMeasurementCommaAreFlagged()
    {
        var emptyYield = new JsonArray { "Set aside (you should have )." };
        var resEmptyYield = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Set aside (you should have 5 to 6 cups).",
            rawTokensJson: emptyYield,
            ingredientCount: 0
        );
        Assert.IsTrue(
            resEmptyYield.ValidationWarnings.Any(w =>
                w.Contains("parenthetical yield", StringComparison.OrdinalIgnoreCase)
            ),
            $"Expected empty yield warning, got: {string.Join(" | ", resEmptyYield.ValidationWarnings)}"
        );

        var droppedMeasureComma = new JsonArray
        {
            "Whisk ",
            new JsonObject
            {
                ["type"] = "ingredient",
                ["ingredientIndex"] = 0,
                ["name"] = "oil",
                ["quantity"] = 1,
                ["unit"] = "tablespoon",
            },
            " salt, and ",
            new JsonObject
            {
                ["type"] = "ingredient",
                ["ingredientIndex"] = 1,
                ["name"] = "salt",
                ["quantity"] = 0.125,
                ["unit"] = "teaspoon",
            },
            " pepper.",
        };
        var resDropped = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Whisk 1 tablespoon oil, 1/8 teaspoon salt, and 1/8 teaspoon pepper.",
            rawTokensJson: droppedMeasureComma,
            ingredientCount: 2
        );
        Assert.IsTrue(
            resDropped.ValidationWarnings.Any(w =>
                w.Contains("Dropped 'teaspoon' measurement", StringComparison.Ordinal)
            ),
            $"Expected dropped measurement warning, got: {string.Join(" | ", resDropped.ValidationWarnings)}"
        );
        Assert.IsTrue(
            resDropped.ValidationWarnings.Any(w =>
                w.Contains("Dropped comma after token", StringComparison.Ordinal)
            ),
            $"Expected dropped comma warning, got: {string.Join(" | ", resDropped.ValidationWarnings)}"
        );
    }

    [TestMethod]
    public void FoodNameSanitizerNormalizesDriedAndJarredPrepPrefixes()
    {
        Assert.AreEqual(
            "dried onion",
            App.Core.FoodCatalog.FoodNameSanitizer.NormalizeCanonicalFoodName("dried minced onions")
        );
        Assert.AreEqual(
            "jarred banana peppers",
            App.Core.FoodCatalog.FoodNameSanitizer.NormalizeCanonicalFoodName(
                "jarred sliced banana peppers"
            )
        );
    }

    [TestMethod]
    public void BrokenYieldParenRegex_FlagsPeriodInsideEmptyYield()
    {
        var hollowYield = new JsonArray
        {
            "Set aside ",
            new JsonObject
            {
                ["type"] = "fixed",
                ["name"] = "corn kernels",
                ["amount"] = 5,
                ["maxAmount"] = 6,
                ["unit"] = "cup",
                ["hyphenated"] = false,
            },
            "(you should have .)",
        };
        var res = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Set aside (you should have 5 to 6 cups).",
            rawTokensJson: hollowYield,
            ingredientCount: 0
        );
        Assert.IsTrue(
            res.ValidationWarnings.Any(w =>
                w.Contains("parenthetical yield", StringComparison.OrdinalIgnoreCase)
            ),
            string.Join(" | ", res.ValidationWarnings)
        );
    }

    [TestMethod]
    public void RetrospectiveAfterTimerPhrase_IsExemptFromUntokenizedWarning()
    {
        var raw = new JsonArray
        {
            "Cover and cook for ",
            new JsonObject
            {
                ["type"] = "timer",
                ["amount"] = 9,
                ["unit"] = "minute",
                ["label"] = "Cook potatoes",
            },
            ". After 9 minutes, potatoes should be bubbling.",
        };
        var res = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 2,
            sectionName: null,
            originalInstruction: "Cover and cook for 9 minutes. After 9 minutes, potatoes should be bubbling.",
            rawTokensJson: raw,
            ingredientCount: 0
        );
        Assert.IsEmpty(res.ValidationWarnings, string.Join(" | ", res.ValidationWarnings));
    }

    [TestMethod]
    public void UnitlessHallucinations_BareUnitWord_ProseFraction_AndTheArticleAreFlagged()
    {
        var bareUnit = new JsonArray
        {
            "Leave fat in skillet (you should have about ",
            new JsonObject
            {
                ["type"] = "ingredient",
                ["ingredientIndex"] = null,
                ["name"] = "teaspoon",
                ["quantity"] = 1,
                ["unit"] = null,
            },
            ").",
        };
        var resBareUnit = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Leave fat in skillet (you should have about 1 teaspoon).",
            rawTokensJson: bareUnit,
            ingredientCount: 0
        );
        Assert.IsTrue(
            resBareUnit.ValidationWarnings.Any(w =>
                w.Contains("unit word", StringComparison.OrdinalIgnoreCase)
            ),
            string.Join(" | ", resBareUnit.ValidationWarnings)
        );

        var bareOne = new JsonArray
        {
            "Stir in ",
            new JsonObject
            {
                ["type"] = "ingredient",
                ["ingredientIndex"] = 0,
                ["name"] = "cheddar cheese",
                ["quantity"] = 1,
                ["unit"] = null,
            },
            ".",
        };
        var resBareOne = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 3,
            sectionName: null,
            originalInstruction: "Stir in cheddar.",
            rawTokensJson: bareOne,
            ingredientCount: 1
        );
        Assert.IsTrue(
            resBareOne.ValidationWarnings.Any(w =>
                w.Contains("quantity=1", StringComparison.OrdinalIgnoreCase)
            ),
            string.Join(" | ", resBareOne.ValidationWarnings)
        );

        var articleOne = new JsonArray
        {
            "Flip each potato and heat the ",
            new JsonObject
            {
                ["type"] = "ingredient",
                ["ingredientIndex"] = 0,
                ["name"] = "butter",
                ["quantity"] = 1,
                ["unit"] = null,
            },
            ".",
        };
        var resArticleOne = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Flip each potato and heat the butter.",
            rawTokensJson: articleOne,
            ingredientCount: 1
        );
        Assert.IsTrue(
            resArticleOne.ValidationWarnings.Any(w =>
                w.Contains("quantity=1", StringComparison.OrdinalIgnoreCase)
            ),
            string.Join(" | ", resArticleOne.ValidationWarnings)
        );

        var validEgg = new JsonArray
        {
            "Crack ",
            new JsonObject
            {
                ["type"] = "ingredient",
                ["ingredientIndex"] = 0,
                ["name"] = "egg",
                ["quantity"] = 1,
                ["unit"] = null,
            },
            " into bowl.",
        };
        var resValidEgg = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Crack 1 egg into bowl.",
            rawTokensJson: validEgg,
            ingredientCount: 1
        );
        Assert.IsEmpty(
            resValidEgg.ValidationWarnings,
            string.Join(" | ", resValidEgg.ValidationWarnings)
        );

        var proseFraction = new JsonArray
        {
            "Process ",
            new JsonObject
            {
                ["type"] = "ingredient",
                ["ingredientIndex"] = 0,
                ["name"] = "jarred roasted red peppers",
                ["quantity"] = 0.5,
                ["unit"] = null,
            },
            " until smooth.",
        };
        var resProseFraction = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Process half of red peppers until smooth.",
            rawTokensJson: proseFraction,
            ingredientCount: 1
        );
        Assert.IsTrue(
            resProseFraction.ValidationWarnings.Any(w =>
                w.Contains("prose fraction", StringComparison.OrdinalIgnoreCase)
            ),
            string.Join(" | ", resProseFraction.ValidationWarnings)
        );
    }

    [TestMethod]
    public void SanitizeRawTokens_CleansDegreeHyphen_PeriodSpace_ScoopFixed_AndDoubleDotParen()
    {
        var raw = new JsonArray
        {
            "Heat for ",
            new JsonObject
            {
                ["type"] = "timer",
                ["amount"] = 90,
                ["unit"] = "second",
                ["label"] = "Heat oil",
            },
            ".Reduce heat. Place in ",
            new JsonObject
            {
                ["type"] = "temperature",
                ["amount"] = 150,
                ["unit"] = "fahrenheit",
            },
            "-degree water and scoop with ",
            new JsonObject
            {
                ["type"] = "ingredient",
                ["ingredientIndex"] = null,
                ["name"] = "portion scoop",
                ["quantity"] = 2,
                ["unit"] = "ounce",
            },
            " (check every ",
            new JsonObject
            {
                ["type"] = "timer",
                ["amount"] = 30,
                ["unit"] = "second",
                ["label"] = "Check eggs",
            },
            " for doneness.).",
        };
        var res = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Heat for 90 seconds. Reduce heat. Place in 150-degree water and scoop with 2-ounce portion scoop (check every 30 seconds for doneness.).",
            rawTokensJson: raw,
            ingredientCount: 0
        );
        var rendered = StepTokenValidator.RenderForValidation(res.ParsedTokens);
        Assert.Contains("90 seconds. Reduce heat.", rendered);
        Assert.Contains("150°F water", rendered);
        Assert.IsTrue(rendered.TrimEnd().EndsWith("doneness.)", StringComparison.Ordinal));
        var scoopFixed = res.ParsedTokens.OfType<Stage4FixedToken>().Single();
        Assert.AreEqual(2m, scoopFixed.Amount);
        Assert.AreEqual("portion scoop", scoopFixed.Name);
    }

    [TestMethod]
    public void HasPrintedNumericRangeInSource_RejectsFabricatedAndAcceptsRealRanges()
    {
        Assert.IsFalse(
            StepTokenValidator.HasPrintedNumericRangeInSource(
                "1 pound extra-large shrimp (21 to 25 per pound)",
                1m,
                25m
            )
        );
        Assert.IsFalse(
            StepTokenValidator.HasPrintedNumericRangeInSource(
                "2 (12- to 14-ounce) duck leg quarters",
                2m,
                28m
            )
        );
        Assert.IsFalse(
            StepTokenValidator.HasPrintedNumericRangeInSource(
                "1 (5- to 7-ounce) jar/can olive oil-packed tuna",
                1m,
                7m
            )
        );
        Assert.IsFalse(
            StepTokenValidator.HasPrintedNumericRangeInSource(
                "1/2 cup extra-virgin olive oil, plus extra for serving",
                0.5m,
                2m
            )
        );
        Assert.IsTrue(
            StepTokenValidator.HasPrintedNumericRangeInSource("1–16 large eggs, chilled", 1m, 16m)
        );
        Assert.IsTrue(
            StepTokenValidator.HasPrintedNumericRangeInSource(
                "1 1/2–2 tablespoons lime juice",
                1.5m,
                2m
            )
        );
        Assert.IsTrue(
            StepTokenValidator.HasPrintedNumericRangeInSource(
                "1/4 to 1/2 teaspoon red pepper flakes",
                0.25m,
                0.5m
            )
        );
    }

    [TestMethod]
    public void SanitizeAndValidate_StripsSelfUnit_ConvertsSpuriousCelsius_RestoresComma_AndFlagsWhitespaceTokens()
    {
        var selfUnitRaw = new JsonArray
        {
            "Mince ",
            new JsonObject
            {
                ["type"] = "ingredient",
                ["ingredientIndex"] = 0,
                ["name"] = "lemon zest strips",
                ["quantity"] = 1,
                ["unit"] = "strip",
            },
            ".",
        };
        var resSelf = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Mince 1 lemon zest strip.",
            rawTokensJson: selfUnitRaw,
            ingredientCount: 1
        );
        Assert.IsEmpty(resSelf.ValidationWarnings, string.Join(" | ", resSelf.ValidationWarnings));
        var ingTok = resSelf.ParsedTokens.OfType<Stage4IngredientToken>().Single();
        Assert.IsNull(ingTok.Unit);
        Assert.AreEqual("lemon zest strip", ingTok.Name);

        var celsiusRaw = new JsonArray
        {
            "Simmer until breast pieces register ",
            new JsonObject
            {
                ["type"] = "temperature",
                ["amount"] = 160,
                ["unit"] = "celsius",
            },
            ".",
        };
        var resTemp = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 4,
            sectionName: null,
            originalInstruction: "Simmer until breast pieces register 160 degrees.",
            rawTokensJson: celsiusRaw,
            ingredientCount: 0
        );
        Assert.Contains("160°F", StepTokenValidator.RenderForValidation(resTemp.ParsedTokens));

        var missingCommaRaw = new JsonArray
        {
            "Toss shrimp, ",
            new JsonObject
            {
                ["type"] = "ingredient",
                ["ingredientIndex"] = 0,
                ["name"] = "oil",
                ["quantity"] = 1,
                ["unit"] = "tablespoon",
            },
            " ",
            new JsonObject
            {
                ["type"] = "ingredient",
                ["ingredientIndex"] = 1,
                ["name"] = "garlic",
                ["quantity"] = 2,
                ["unit"] = "teaspoon",
            },
            ", and salt.",
        };
        var resComma = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Toss shrimp, 1 tablespoon oil, 2 teaspoons garlic, and salt.",
            rawTokensJson: missingCommaRaw,
            ingredientCount: 2
        );
        Assert.IsEmpty(
            resComma.ValidationWarnings,
            string.Join(" | ", resComma.ValidationWarnings)
        );
        Assert.Contains(
            "1 tablespoon oil, 2 teaspoons garlic",
            StepTokenValidator.RenderForValidation(resComma.ParsedTokens)
        );

        var adjacentDimRaw = new JsonArray
        {
            "Place in ",
            new JsonObject
            {
                ["type"] = "dimension",
                ["amount"] = 13,
                ["unit"] = "inch",
                ["hyphenated"] = true,
            },
            " ",
            new JsonObject
            {
                ["type"] = "dimension",
                ["amount"] = 9,
                ["unit"] = "inch",
                ["hyphenated"] = true,
            },
            " baking dish.",
        };
        var resAdjacentDim = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 3,
            sectionName: null,
            originalInstruction: "Place in 13 by 9-inch baking dish.",
            rawTokensJson: adjacentDimRaw,
            ingredientCount: 0
        );
        Assert.IsTrue(
            resAdjacentDim.ValidationWarnings.Any(w =>
                w.Contains("separated only by whitespace", StringComparison.OrdinalIgnoreCase)
            ),
            string.Join(" | ", resAdjacentDim.ValidationWarnings)
        );
    }

    [TestMethod]
    public void BuildAndValidateStep_ConvertsIncrementalAtATimeTokensToFixedAndPreservesTotal()
    {
        const string original =
            "(If too thick, loosen with water, adding 1 teaspoon at a time.) Add remaining 1/4 cup flour, 1 tablespoon at a time.";

        var rawTokens = new JsonArray
        {
            "(If too thick, loosen with water, adding ",
            new JsonObject
            {
                ["type"] = "ingredient",
                ["ingredientIndex"] = null,
                ["name"] = "water at a time",
                ["quantity"] = 1,
                ["unit"] = "teaspoon",
            },
            ".) Add remaining ",
            new JsonObject
            {
                ["type"] = "ingredient",
                ["ingredientIndex"] = 0,
                ["name"] = "flour",
                ["quantity"] = 0.25m,
                ["unit"] = "cup",
            },
            ", ",
            new JsonObject
            {
                ["type"] = "ingredient",
                ["ingredientIndex"] = 1,
                ["name"] = "ice water",
                ["quantity"] = 1,
                ["unit"] = "tablespoon",
            },
            " at a time.",
        };

        var result = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: original,
            rawTokensJson: rawTokens,
            ingredientCount: 2
        );

        var parsed = StepInstructionParser.ExtractTokens(result.CompiledInstruction);
        var fixedTokens = parsed.OfType<StepFixedToken>().ToList();
        var ingTokens = parsed.OfType<StepIngredientToken>().ToList();

        Assert.HasCount(2, fixedTokens);
        Assert.AreEqual(1m, fixedTokens[0].Amount);
        Assert.AreEqual(MeasurementUnit.Tsp, fixedTokens[0].Unit);
        Assert.AreEqual(1m, fixedTokens[1].Amount);
        Assert.AreEqual(MeasurementUnit.Tbsp, fixedTokens[1].Unit);

        Assert.HasCount(1, ingTokens);
        Assert.AreEqual("flour", ingTokens[0].Ingredient);
        Assert.AreEqual(0.25m, ingTokens[0].Amount);
        Assert.Contains(
            "1 teaspoon water at a time.)",
            StepTokenValidator.RenderForValidation(result.ParsedTokens)
        );
    }

    [TestMethod]
    public void BuildAndValidateStep_HealsTransposedPreNounModifiersAcrossCommas()
    {
        const string original =
            "Add reserved bacon fat, 1 tablespoon minced fresh chives, 2 teaspoons distilled white vinegar, and 1/8 teaspoon salt.";

        var rawTokens = new JsonArray
        {
            "Add reserved bacon fat, ",
            new JsonObject
            {
                ["type"] = "ingredient",
                ["ingredientIndex"] = 0,
                ["name"] = "chives",
                ["quantity"] = 1,
                ["unit"] = "tablespoon",
            },
            ", minced fresh ",
            new JsonObject
            {
                ["type"] = "ingredient",
                ["ingredientIndex"] = 1,
                ["name"] = "vinegar",
                ["quantity"] = 2,
                ["unit"] = "teaspoon",
            },
            ", distilled white, and ",
            new JsonObject
            {
                ["type"] = "ingredient",
                ["ingredientIndex"] = 2,
                ["name"] = "salt",
                ["quantity"] = 0.125m,
                ["unit"] = "teaspoon",
            },
            ".",
        };

        var result = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: original,
            rawTokensJson: rawTokens,
            ingredientCount: 3
        );

        var rendered = StepTokenValidator.RenderForValidation(result.ParsedTokens);
        Assert.AreEqual(
            "Add reserved bacon fat, 1 tablespoon minced fresh chives, 2 teaspoons distilled white vinegar, and 1/8 teaspoon salt.",
            rendered
        );
    }

    [TestMethod]
    public void BuildAndValidateStep_EmitsIngredientIndexOnCompiledToken()
    {
        const string original = "Fold in 2 teaspoons chives.";
        var rawTokens = new JsonArray
        {
            "Fold in ",
            new JsonObject
            {
                ["type"] = "ingredient",
                ["ingredientIndex"] = 1,
                ["name"] = "chives",
                ["quantity"] = 2,
                ["unit"] = "teaspoon",
            },
            ".",
        };

        var result = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: original,
            rawTokensJson: rawTokens,
            ingredientCount: 2
        );

        var compiledToken = StepInstructionParser
            .ExtractTokens(result.CompiledInstruction)
            .OfType<StepIngredientToken>()
            .Single();
        Assert.AreEqual(1, compiledToken.IngredientIndex);
        Assert.IsNull(compiledToken.IngredientId);
        Assert.AreEqual("chives", compiledToken.Ingredient);
        Assert.AreEqual(2m, compiledToken.Amount);
    }

    [TestMethod]
    public void BuildAndValidateStep_ConvertsMixtureSuffixAndHealsHallucinatedNounSwaps()
    {
        // 1. "... mixture" with ingredientIndex converts cleanly to StepMixtureToken
        var mixtureRes = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Rub 4 teaspoons salt-sugar mixture under skin.",
            rawTokensJson: new JsonArray
            {
                "Rub ",
                new JsonObject
                {
                    ["type"] = "ingredient",
                    ["ingredientIndex"] = 0,
                    ["name"] = "salt-sugar mixture",
                    ["quantity"] = 4,
                    ["unit"] = "teaspoon",
                },
                " under skin.",
            },
            ingredientCount: 2
        );
        Assert.IsEmpty(
            mixtureRes.ValidationWarnings,
            string.Join(" | ", mixtureRes.ValidationWarnings)
        );
        var mixTok = StepInstructionParser
            .ExtractTokens(mixtureRes.CompiledInstruction)
            .OfType<StepMixtureToken>()
            .Single();
        Assert.AreEqual("salt-sugar mixture", mixTok.Name);
        Assert.AreEqual(4m, mixTok.Amount);

        // 2. Hallucinated master ingredient name ("cider vinegar" + " water", "kosher salt" + " defatted juices", "ketchup" + " sauce") heals cleanly
        var swapRes = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 2,
            sectionName: null,
            originalInstruction: "Fill pans with 3 cups water. Stir 1/3 cup defatted juices and 1/2 cup sauce into pork.",
            rawTokensJson: new JsonArray
            {
                "Fill pans with ",
                new JsonObject
                {
                    ["type"] = "ingredient",
                    ["ingredientIndex"] = 8,
                    ["name"] = "cider vinegar",
                    ["quantity"] = 3,
                    ["unit"] = "cup",
                },
                " water. Stir ",
                new JsonObject
                {
                    ["type"] = "ingredient",
                    ["ingredientIndex"] = 12,
                    ["name"] = "kosher salt",
                    ["quantity"] = 0.3333m,
                    ["unit"] = "cup",
                },
                " defatted juices and ",
                new JsonObject
                {
                    ["type"] = "ingredient",
                    ["ingredientIndex"] = 9,
                    ["name"] = "ketchup",
                    ["quantity"] = 0.5m,
                    ["unit"] = "cup",
                },
                " sauce into pork.",
            },
            ingredientCount: 13
        );
        Assert.IsEmpty(swapRes.ValidationWarnings, string.Join(" | ", swapRes.ValidationWarnings));

        var compiledTokens = StepInstructionParser.ExtractTokens(swapRes.CompiledInstruction);
        var waterTok = compiledTokens.OfType<StepIngredientToken>().Single();
        Assert.AreEqual("water", waterTok.Ingredient);
        Assert.IsNull(waterTok.IngredientIndex);
        Assert.AreEqual(3m, waterTok.Amount);

        var mixtures = compiledTokens.OfType<StepMixtureToken>().ToList();
        Assert.HasCount(2, mixtures);
        Assert.AreEqual("defatted juices", mixtures[0].Name);
        Assert.AreEqual("sauce", mixtures[1].Name);
    }

    [TestMethod]
    public void BuildAndValidateStep_HealsFalsePositiveFixedWaterAndFat_WhilePreservingVesselsRatesAndCheckYields()
    {
        // False-positive fixed on brine water ("2 qt cold water") and pan drippings ("3 tbsp fat")
        var stepRes = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Dissolve 1/4 cup salt in 2 quarts cold water. Reserve 3 tablespoons fat. Add 1 tablespoon at a time in 12-cup Bundt pan (you should have 5 to 6 cups).",
            rawTokensJson: new JsonArray
            {
                "Dissolve ",
                new JsonObject
                {
                    ["type"] = "ingredient",
                    ["ingredientIndex"] = 0,
                    ["name"] = "salt",
                    ["quantity"] = 0.25m,
                    ["unit"] = "cup",
                },
                " in ",
                new JsonObject
                {
                    ["type"] = "fixed",
                    ["amount"] = 2m,
                    ["unit"] = "quart",
                    ["hyphenated"] = false,
                },
                " cold water. Reserve ",
                new JsonObject
                {
                    ["type"] = "fixed",
                    ["amount"] = 3m,
                    ["unit"] = "tablespoon",
                    ["hyphenated"] = false,
                },
                " fat. Add ",
                new JsonObject
                {
                    ["type"] = "fixed",
                    ["amount"] = 1m,
                    ["unit"] = "tablespoon",
                    ["hyphenated"] = false,
                },
                " at a time in ",
                new JsonObject
                {
                    ["type"] = "fixed",
                    ["name"] = "Bundt pan",
                    ["amount"] = 12m,
                    ["unit"] = "cup",
                    ["hyphenated"] = true,
                },
                " (you should have ",
                new JsonObject
                {
                    ["type"] = "fixed",
                    ["amount"] = 5m,
                    ["maxAmount"] = 6m,
                    ["unit"] = "cup",
                    ["hyphenated"] = false,
                },
                ").",
            },
            ingredientCount: 1
        );

        Assert.IsEmpty(stepRes.ValidationWarnings, string.Join(" | ", stepRes.ValidationWarnings));

        var tokens = StepInstructionParser.ExtractTokens(stepRes.CompiledInstruction);
        var ingredients = tokens.OfType<StepIngredientToken>().ToList();
        Assert.HasCount(2, ingredients);
        Assert.AreEqual("salt", ingredients[0].Ingredient);
        Assert.AreEqual("cold water", ingredients[1].Ingredient);
        Assert.AreEqual(2m, ingredients[1].Amount);
        Assert.AreEqual(MeasurementUnit.Qt, ingredients[1].Unit);
        Assert.IsNull(ingredients[1].IngredientIndex);

        var fatMix = tokens.OfType<StepMixtureToken>().Single();
        Assert.AreEqual("fat", fatMix.Name);
        Assert.AreEqual(3m, fatMix.Amount);
        Assert.AreEqual(MeasurementUnit.Tbsp, fatMix.Unit);

        var keptFixed = tokens.OfType<StepFixedToken>().ToList();
        Assert.HasCount(3, keptFixed);
        Assert.AreEqual(1m, keptFixed[0].Amount);
        Assert.AreEqual("Bundt pan", keptFixed[1].Name);
        Assert.AreEqual(5m, keptFixed[2].Amount);
        Assert.AreEqual(6m, keptFixed[2].MaxAmount);
    }

    [TestMethod]
    public void BuildAndValidateStep_PreservesFixedFlagOnStepIngredientToken()
    {
        var res = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Spread 1 tablespoon butter on each roll.",
            rawTokensJson: new JsonArray
            {
                "Spread ",
                new JsonObject
                {
                    ["type"] = "ingredient",
                    ["ingredientIndex"] = 0,
                    ["name"] = "butter",
                    ["quantity"] = 1m,
                    ["unit"] = "tablespoon",
                    ["fixed"] = true,
                },
                " on each roll.",
            },
            ingredientCount: 1
        );

        Assert.IsEmpty(res.ValidationWarnings, string.Join(" | ", res.ValidationWarnings));
        var tok = StepInstructionParser
            .ExtractTokens(res.CompiledInstruction)
            .OfType<StepIngredientToken>()
            .Single();
        Assert.IsTrue(tok.Fixed);
        StringAssert.Contains(res.CompiledInstruction, "\"fixed\":true");
    }

    [TestMethod]
    public void BuildAndValidateStep_HealsFixedDimensionFixedTimeUnitlessCountPanFatMixtureAndTransposedOfThe()
    {
        // 1. Fixed with unit='inch' + customUnitName='inch' heals to Dimension and restores noun into text
        var dimRes = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Heat oil in a 10-inch nonstick skillet, stirring every 20 seconds.",
            rawTokensJson: new JsonArray
            {
                "Heat oil in a ",
                new JsonObject
                {
                    ["type"] = "fixed",
                    ["amount"] = 10m,
                    ["unit"] = "custom",
                    ["customUnitName"] = "inch",
                    ["name"] = "nonstick skillet",
                    ["hyphenated"] = true,
                },
                ", stirring every ",
                new JsonObject
                {
                    ["type"] = "fixed",
                    ["amount"] = 20m,
                    ["unit"] = "second",
                    ["name"] = null,
                    ["hyphenated"] = false,
                },
                " .",
            },
            ingredientCount: 1
        );
        Assert.IsEmpty(dimRes.ValidationWarnings, string.Join(" | ", dimRes.ValidationWarnings));
        var dimTok = StepInstructionParser
            .ExtractTokens(dimRes.CompiledInstruction)
            .OfType<StepDimensionToken>()
            .Single();
        Assert.AreEqual(10m, dimTok.Value);
        Assert.AreEqual(DimensionUnit.Inch, dimTok.Unit);
        Assert.IsTrue(dimTok.Hyphenated);
        var timerTok = StepInstructionParser
            .ExtractTokens(dimRes.CompiledInstruction)
            .OfType<StepTimerToken>()
            .Single();
        Assert.AreEqual(20, timerTok.Seconds);
        Assert.AreEqual(
            "Heat oil in a 10-inch nonstick skillet, stirring every 20 seconds.",
            StepTokenValidator.RenderForValidation(dimRes.ParsedTokens)
        );

        // 2. Unitless/'count' batch counts unwrap to plain text while check-yield stays Fixed
        var yieldRes = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 2,
            sectionName: null,
            originalInstruction: "Drop dumplings (you should have about 18 dumplings and about 5 to 6 cups).",
            rawTokensJson: new JsonArray
            {
                "Drop dumplings (you should have about ",
                new JsonObject
                {
                    ["type"] = "fixed",
                    ["amount"] = 18m,
                    ["unit"] = "count",
                    ["name"] = "dumplings",
                    ["hyphenated"] = false,
                },
                " and about ",
                new JsonObject
                {
                    ["type"] = "fixed",
                    ["amount"] = 5m,
                    ["maxAmount"] = 6m,
                    ["unit"] = "cup",
                    ["name"] = null,
                    ["hyphenated"] = false,
                },
                ").",
            },
            ingredientCount: 0
        );
        Assert.IsEmpty(
            yieldRes.ValidationWarnings,
            string.Join(" | ", yieldRes.ValidationWarnings)
        );
        var keptFixed = StepInstructionParser
            .ExtractTokens(yieldRes.CompiledInstruction)
            .OfType<StepFixedToken>()
            .Single();
        Assert.AreEqual(5m, keptFixed.Amount);
        Assert.AreEqual(6m, keptFixed.MaxAmount);

        // 3. Unlinked pan fat heals to Mixture; unitless prose fraction Mixture heals back to prose;
        //    and transposed "Heat of the ||2 tsp oil||" cleans "of the" while keeping "Add half of the ||2 lb chicken||"
        var healRes = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 3,
            sectionName: null,
            originalInstruction: "Heat 2 teaspoons of the oil, add half of the 2 pounds chicken, spread one-quarter of the cheese mixture, and discard all but 1 teaspoon fat.",
            rawTokensJson: new JsonArray
            {
                "Heat of the ",
                new JsonObject
                {
                    ["type"] = "ingredient",
                    ["ingredientIndex"] = 0,
                    ["name"] = "oil",
                    ["quantity"] = 2m,
                    ["unit"] = "teaspoon",
                },
                ", add half of the ",
                new JsonObject
                {
                    ["type"] = "ingredient",
                    ["ingredientIndex"] = 1,
                    ["name"] = "chicken",
                    ["quantity"] = 2m,
                    ["unit"] = "pound",
                },
                ", spread ",
                new JsonObject
                {
                    ["type"] = "mixture",
                    ["name"] = "cheese mixture",
                    ["quantity"] = 0.25m,
                    ["unit"] = null,
                },
                ", and discard all but ",
                new JsonObject
                {
                    ["type"] = "ingredient",
                    ["ingredientIndex"] = null,
                    ["name"] = "fat",
                    ["quantity"] = 1m,
                    ["unit"] = "teaspoon",
                },
                ".",
            },
            ingredientCount: 2
        );
        Assert.IsEmpty(healRes.ValidationWarnings, string.Join(" | ", healRes.ValidationWarnings));
        var fatMix = StepInstructionParser
            .ExtractTokens(healRes.CompiledInstruction)
            .OfType<StepMixtureToken>()
            .Single();
        Assert.AreEqual("fat", fatMix.Name);
        Assert.AreEqual(1m, fatMix.Amount);
        Assert.AreEqual(MeasurementUnit.Tsp, fatMix.Unit);
        var renderedHeal = StepTokenValidator.RenderForValidation(healRes.ParsedTokens);
        Assert.AreEqual(
            "Heat 2 teaspoons oil, add half of the 2 pounds chicken, spread one-quarter of the cheese mixture, and discard all but 1 teaspoon fat.",
            renderedHeal
        );
    }

    [TestMethod]
    public void BuildAndValidateStep_FixedMixtureDoesNotScaleAt2x()
    {
        var res = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Form 1 tablespoon pork mixture into meatball; repeat.",
            rawTokensJson: new JsonArray
            {
                "Form ",
                new JsonObject
                {
                    ["type"] = "mixture",
                    ["name"] = "pork mixture",
                    ["quantity"] = 1m,
                    ["unit"] = "tablespoon",
                    ["fixed"] = true,
                },
                " into meatball; repeat.",
            },
            ingredientCount: 0
        );

        Assert.IsEmpty(res.ValidationWarnings, string.Join(" | ", res.ValidationWarnings));
        var mixTok = StepInstructionParser
            .ExtractTokens(res.CompiledInstruction)
            .OfType<StepMixtureToken>()
            .Single();
        Assert.IsTrue(mixTok.Fixed);
        Assert.AreEqual(
            "Form 1 tablespoon pork mixture into meatball; repeat.",
            StepTokenValidator.RenderForValidation(res.ParsedTokens, scaleFactor: 2m)
        );
    }

    [TestMethod]
    public void BuildAndValidateStep_CleansDanglingBareOfAndAndPromotesSoupToMixture()
    {
        var res = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Repeat with remaining 2 tsp oil and remaining chicken, then puree 3 cups soup.",
            rawTokensJson: new JsonArray
            {
                "Repeat with the remaining ",
                new JsonObject
                {
                    ["type"] = "ingredient",
                    ["ingredientIndex"] = 0,
                    ["name"] = "oil",
                    ["quantity"] = 2m,
                    ["unit"] = "teaspoon",
                },
                " of and the remaining chicken, then puree ",
                new JsonObject
                {
                    ["type"] = "ingredient",
                    ["ingredientIndex"] = null,
                    ["name"] = "soup",
                    ["quantity"] = 3m,
                    ["unit"] = "cup",
                },
                ".",
            },
            ingredientCount: 1
        );

        Assert.IsEmpty(res.ValidationWarnings, string.Join(" | ", res.ValidationWarnings));
        var plain = StepInstructionParser.ToPlainText(res.CompiledInstruction);
        Assert.DoesNotContain("of and", plain);
        StringAssert.Contains(plain, "oil and the remaining chicken");
        var mix = StepInstructionParser
            .ExtractTokens(res.CompiledInstruction)
            .OfType<StepMixtureToken>()
            .Single();
        Assert.AreEqual("soup", mix.Name);
        Assert.AreEqual(3m, mix.Amount);
        Assert.AreEqual(MeasurementUnit.Cup, mix.Unit);
    }

    [TestMethod]
    public void BuildAndValidateStep_ExemptsPhysicalUtensilQuantitiesAndHealsSpaceBeforeParenAndDroppedPeriod()
    {
        var utensilRes = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 1,
            sectionName: null,
            originalInstruction: "Use back of 1 teaspoon dipped in bowl of cold water. Using a 1 tablespoon measure, scoop batter.",
            rawTokensJson: new JsonArray
            {
                "Use back of 1 teaspoon dipped in bowl of cold water. Using a 1 tablespoon measure, scoop batter.",
            },
            ingredientCount: 0
        );
        Assert.IsEmpty(
            utensilRes.ValidationWarnings,
            string.Join(" | ", utensilRes.ValidationWarnings)
        );

        var punctRes = StepTokenValidator.BuildAndValidateStep(
            stepNumber: 2,
            sectionName: null,
            originalInstruction: "Scoop 1 tbsp dough. Using second small spoon, bake for 15 minutes (do not open).",
            rawTokensJson: new JsonArray
            {
                "Scoop ",
                new JsonObject
                {
                    ["type"] = "fixed",
                    ["amount"] = 1m,
                    ["unit"] = "tablespoon",
                    ["name"] = "dough",
                    ["hyphenated"] = false,
                },
                " Using second small spoon, bake for ",
                new JsonObject
                {
                    ["type"] = "timer",
                    ["amount"] = 15m,
                    ["unit"] = "minute",
                    ["label"] = "bake",
                },
                "(do not open).",
            },
            ingredientCount: 0
        );
        Assert.IsEmpty(
            punctRes.ValidationWarnings,
            string.Join(" | ", punctRes.ValidationWarnings)
        );
        var plainPunct = StepInstructionParser.ToPlainText(punctRes.CompiledInstruction);
        StringAssert.Contains(plainPunct, ". Using second small spoon");
        StringAssert.Contains(plainPunct, " (do not open).");
    }
}

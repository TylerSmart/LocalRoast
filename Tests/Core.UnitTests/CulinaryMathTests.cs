using App.Core.Recipes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class CulinaryMathTests
{
    [TestMethod]
    public void FormatNumber_SnapsToKitchenFractions()
    {
        Assert.AreEqual("1/8", CulinaryMath.FormatNumber(0.125m, false));
        Assert.AreEqual("1/4", CulinaryMath.FormatNumber(0.25m, false));
        Assert.AreEqual("1/3", CulinaryMath.FormatNumber(0.333m, false));
        Assert.AreEqual("1/2", CulinaryMath.FormatNumber(0.5m, false));
        Assert.AreEqual("2/3", CulinaryMath.FormatNumber(0.667m, false));
        Assert.AreEqual("3/4", CulinaryMath.FormatNumber(0.75m, false));
        Assert.AreEqual("1 1/2", CulinaryMath.FormatNumber(1.5m, false));
        Assert.AreEqual("2", CulinaryMath.FormatNumber(2.0m, false));
        Assert.AreEqual("2 3/4", CulinaryMath.FormatNumber(2.75m, false));
    }

    [TestMethod]
    public void FormatNumber_Metric_RoundsLargeValuesAndTrimsSmall()
    {
        Assert.AreEqual("250", CulinaryMath.FormatNumber(250m, true));
        Assert.AreEqual("15", CulinaryMath.FormatNumber(15.02m, true));
        Assert.AreEqual("1.5", CulinaryMath.FormatNumber(1.5m, true));
        Assert.AreEqual("0.75", CulinaryMath.FormatNumber(0.75m, true));
    }

    [TestMethod]
    public void ScaleAndConvert_Original_ScalesNumerically()
    {
        var result = CulinaryMath.ScaleAndConvert(
            quantity: 1.5m,
            maxQuantity: 2.0m,
            unit: MeasurementUnit.Cup,
            customUnitName: null,
            scaleFactor: 2.0m,
            targetSystem: UnitSystem.Original
        );

        Assert.AreEqual(3.0m, result.Quantity);
        Assert.AreEqual(4.0m, result.MaxQuantity);
        Assert.AreEqual(MeasurementUnit.Cup, result.Unit);
        Assert.AreEqual("cups", result.UnitLabel);
        Assert.AreEqual("3–4", result.FormattedAmount);
    }

    [TestMethod]
    public void ScaleAndConvert_UsToMetric_VolumeConvertsAccurately()
    {
        // 1 cup = ~236.588 ml -> scaled 1x to metric should be ~237 ml
        var result = CulinaryMath.ScaleAndConvert(
            quantity: 1.0m,
            maxQuantity: null,
            unit: MeasurementUnit.Cup,
            customUnitName: null,
            scaleFactor: 1.0m,
            targetSystem: UnitSystem.Metric
        );

        Assert.AreEqual(MeasurementUnit.Ml, result.Unit);
        Assert.AreEqual("ml", result.UnitLabel);
        Assert.AreEqual("237", result.FormattedAmount);

        // 5 cups = ~1183 ml -> should convert to L
        var largeResult = CulinaryMath.ScaleAndConvert(
            quantity: 5.0m,
            maxQuantity: null,
            unit: MeasurementUnit.Cup,
            customUnitName: null,
            scaleFactor: 1.0m,
            targetSystem: UnitSystem.Metric
        );

        Assert.AreEqual(MeasurementUnit.L, largeResult.Unit);
        Assert.AreEqual("l", largeResult.UnitLabel);
        Assert.AreEqual("1.18", largeResult.FormattedAmount);
    }

    [TestMethod]
    public void ScaleAndConvert_UsToMetric_WeightConvertsAccurately()
    {
        // 8 oz = ~226.8 g
        var result = CulinaryMath.ScaleAndConvert(
            quantity: 8.0m,
            maxQuantity: null,
            unit: MeasurementUnit.Oz,
            customUnitName: null,
            scaleFactor: 1.0m,
            targetSystem: UnitSystem.Metric
        );

        Assert.AreEqual(MeasurementUnit.G, result.Unit);
        Assert.AreEqual("g", result.UnitLabel);
        Assert.AreEqual("227", result.FormattedAmount);

        // 3 lbs = ~1360.78 g -> converts to kg
        var largeResult = CulinaryMath.ScaleAndConvert(
            quantity: 3.0m,
            maxQuantity: null,
            unit: MeasurementUnit.Lb,
            customUnitName: null,
            scaleFactor: 1.0m,
            targetSystem: UnitSystem.Metric
        );

        Assert.AreEqual(MeasurementUnit.Kg, largeResult.Unit);
        Assert.AreEqual("kg", largeResult.UnitLabel);
        Assert.AreEqual("1.36", largeResult.FormattedAmount);
    }

    [TestMethod]
    public void ScaleAndConvert_MetricToUs_VolumeConvertsAccurately()
    {
        // 500 ml -> converts to cup (~2.11 cups -> 2 or 2 1/8)
        var result = CulinaryMath.ScaleAndConvert(
            quantity: 500m,
            maxQuantity: null,
            unit: MeasurementUnit.Ml,
            customUnitName: null,
            scaleFactor: 1.0m,
            targetSystem: UnitSystem.UsCustomary
        );

        Assert.AreEqual(MeasurementUnit.Cup, result.Unit);
        Assert.AreEqual("cups", result.UnitLabel);
        StringAssert.StartsWith(result.FormattedAmount, "2");

        // 5 ml -> converts to tsp (~1 tsp)
        var smallResult = CulinaryMath.ScaleAndConvert(
            quantity: 5m,
            maxQuantity: null,
            unit: MeasurementUnit.Ml,
            customUnitName: null,
            scaleFactor: 1.0m,
            targetSystem: UnitSystem.UsCustomary
        );

        Assert.AreEqual(MeasurementUnit.Tsp, smallResult.Unit);
        Assert.AreEqual("1", smallResult.FormattedAmount);
    }

    [TestMethod]
    public void ScaleAndConvert_CountUnits_PreservesUnitWithoutConversion()
    {
        var result = CulinaryMath.ScaleAndConvert(
            quantity: 3.0m,
            maxQuantity: 4.0m,
            unit: MeasurementUnit.Clove,
            customUnitName: null,
            scaleFactor: 1.5m,
            targetSystem: UnitSystem.Metric
        );

        Assert.AreEqual(MeasurementUnit.Clove, result.Unit);
        Assert.AreEqual("cloves", result.UnitLabel);
        Assert.AreEqual("4 1/2–6", result.FormattedAmount);
    }

    [TestMethod]
    public void FormatDuration_FormatsExpectedStrings()
    {
        Assert.AreEqual("0 sec", CulinaryMath.FormatDuration(0));
        Assert.AreEqual("45 sec", CulinaryMath.FormatDuration(45));
        Assert.AreEqual("15 min", CulinaryMath.FormatDuration(900));
        Assert.AreEqual("15 min 30 sec", CulinaryMath.FormatDuration(930));
        Assert.AreEqual("1 hr", CulinaryMath.FormatDuration(3600));
        Assert.AreEqual("1 hr 30 min", CulinaryMath.FormatDuration(5400));
    }

    [TestMethod]
    public void FormatMinutes_FormatsExpectedStrings()
    {
        Assert.AreEqual(string.Empty, CulinaryMath.FormatMinutes(null));
        Assert.AreEqual(string.Empty, CulinaryMath.FormatMinutes(-5));
        Assert.AreEqual("0 min", CulinaryMath.FormatMinutes(0));
        Assert.AreEqual("15 min", CulinaryMath.FormatMinutes(15));
        Assert.AreEqual("59 min", CulinaryMath.FormatMinutes(59));
        Assert.AreEqual("1 hr", CulinaryMath.FormatMinutes(60));
        Assert.AreEqual("1 hr 15 min", CulinaryMath.FormatMinutes(75));
        Assert.AreEqual("2 hr", CulinaryMath.FormatMinutes(120));
        Assert.AreEqual("7 hr 45 min", CulinaryMath.FormatMinutes(465));
    }

    [TestMethod]
    public void ScaleAndConvert_SimplifiesUnits_ScalingQuadrupleTbspToQuarterCup()
    {
        // 1 tbsp * 4 -> 1/4 cup in Original
        var originalResult = CulinaryMath.ScaleAndConvert(
            quantity: 1.0m,
            maxQuantity: null,
            unit: MeasurementUnit.Tbsp,
            customUnitName: null,
            scaleFactor: 4.0m,
            targetSystem: UnitSystem.Original
        );
        Assert.AreEqual(0.25m, originalResult.Quantity);
        Assert.AreEqual(MeasurementUnit.Cup, originalResult.Unit);
        Assert.AreEqual("cup", originalResult.UnitLabel);
        Assert.AreEqual("1/4", originalResult.FormattedAmount);

        // 1 tbsp * 4 -> 1/4 cup in UsCustomary
        var usResult = CulinaryMath.ScaleAndConvert(
            quantity: 1.0m,
            maxQuantity: null,
            unit: MeasurementUnit.Tbsp,
            customUnitName: null,
            scaleFactor: 4.0m,
            targetSystem: UnitSystem.UsCustomary
        );
        Assert.AreEqual(0.25m, usResult.Quantity);
        Assert.AreEqual(MeasurementUnit.Cup, usResult.Unit);
        Assert.AreEqual("cup", usResult.UnitLabel);
        Assert.AreEqual("1/4", usResult.FormattedAmount);
    }

    [TestMethod]
    public void ScaleAndConvert_SimplifiesUnits_HalvingQuarterCupToTwoTbsp()
    {
        // 1/4 cup * 0.5 -> 2 tbsp in Original
        var originalResult = CulinaryMath.ScaleAndConvert(
            quantity: 0.25m,
            maxQuantity: null,
            unit: MeasurementUnit.Cup,
            customUnitName: null,
            scaleFactor: 0.5m,
            targetSystem: UnitSystem.Original
        );
        Assert.AreEqual(2.0m, originalResult.Quantity);
        Assert.AreEqual(MeasurementUnit.Tbsp, originalResult.Unit);
        Assert.AreEqual("tbsp", originalResult.UnitLabel);
        Assert.AreEqual("2", originalResult.FormattedAmount);

        // 1/4 cup * 0.5 -> 2 tbsp in UsCustomary
        var usResult = CulinaryMath.ScaleAndConvert(
            quantity: 0.25m,
            maxQuantity: null,
            unit: MeasurementUnit.Cup,
            customUnitName: null,
            scaleFactor: 0.5m,
            targetSystem: UnitSystem.UsCustomary
        );
        Assert.AreEqual(2.0m, usResult.Quantity);
        Assert.AreEqual(MeasurementUnit.Tbsp, usResult.Unit);
        Assert.AreEqual("tbsp", usResult.UnitLabel);
        Assert.AreEqual("2", usResult.FormattedAmount);
    }

    [TestMethod]
    public void ScaleAndConvert_SimplifiesUnits_HalvingTbspToOneAndHalfTsp()
    {
        var result = CulinaryMath.ScaleAndConvert(
            quantity: 1.0m,
            maxQuantity: null,
            unit: MeasurementUnit.Tbsp,
            customUnitName: null,
            scaleFactor: 0.5m,
            targetSystem: UnitSystem.Original
        );
        Assert.AreEqual(1.5m, result.Quantity);
        Assert.AreEqual(MeasurementUnit.Tsp, result.Unit);
        Assert.AreEqual("tsp", result.UnitLabel);
        Assert.AreEqual("1 1/2", result.FormattedAmount);
    }

    [TestMethod]
    public void ScaleAndConvert_SimplifiesUnits_TriplingTspToTbsp()
    {
        var result = CulinaryMath.ScaleAndConvert(
            quantity: 2.0m,
            maxQuantity: null,
            unit: MeasurementUnit.Tsp,
            customUnitName: null,
            scaleFactor: 3.0m,
            targetSystem: UnitSystem.Original
        );
        Assert.AreEqual(2.0m, result.Quantity);
        Assert.AreEqual(MeasurementUnit.Tbsp, result.Unit);
        Assert.AreEqual("tbsp", result.UnitLabel);
        Assert.AreEqual("2", result.FormattedAmount);
    }

    [TestMethod]
    public void ScaleAndConvert_SimplifiesUnits_WeightOzToLbAndLbToOz()
    {
        // 8 oz * 2 -> 1 lb
        var ozResult = CulinaryMath.ScaleAndConvert(
            quantity: 8.0m,
            maxQuantity: null,
            unit: MeasurementUnit.Oz,
            customUnitName: null,
            scaleFactor: 2.0m,
            targetSystem: UnitSystem.Original
        );
        Assert.AreEqual(1.0m, ozResult.Quantity);
        Assert.AreEqual(MeasurementUnit.Lb, ozResult.Unit);
        Assert.AreEqual("lb", ozResult.UnitLabel);
        Assert.AreEqual("1", ozResult.FormattedAmount);

        // 1/4 lb * 0.5 -> 2 oz
        var lbResult = CulinaryMath.ScaleAndConvert(
            quantity: 0.25m,
            maxQuantity: null,
            unit: MeasurementUnit.Lb,
            customUnitName: null,
            scaleFactor: 0.5m,
            targetSystem: UnitSystem.Original
        );
        Assert.AreEqual(2.0m, lbResult.Quantity);
        Assert.AreEqual(MeasurementUnit.Oz, lbResult.Unit);
        Assert.AreEqual("oz", lbResult.UnitLabel);
        Assert.AreEqual("2", lbResult.FormattedAmount);
    }

    [TestMethod]
    public void ScaleAndConvert_PreservesOriginalUnitAtOneX()
    {
        var result = CulinaryMath.ScaleAndConvert(
            quantity: 1.0m,
            maxQuantity: null,
            unit: MeasurementUnit.Tbsp,
            customUnitName: null,
            scaleFactor: 1.0m,
            targetSystem: UnitSystem.Original
        );
        Assert.AreEqual(1.0m, result.Quantity);
        Assert.AreEqual(MeasurementUnit.Tbsp, result.Unit);
        Assert.AreEqual("tbsp", result.UnitLabel);
        Assert.AreEqual("1", result.FormattedAmount);
    }

    [TestMethod]
    public void ScaleAndConvert_SimplifiesRangesWhenBothBoundsMatch()
    {
        // 1-2 tbsp * 4 -> 4-8 tbsp -> 1/4-1/2 cup
        var result = CulinaryMath.ScaleAndConvert(
            quantity: 1.0m,
            maxQuantity: 2.0m,
            unit: MeasurementUnit.Tbsp,
            customUnitName: null,
            scaleFactor: 4.0m,
            targetSystem: UnitSystem.Original
        );
        Assert.AreEqual(0.25m, result.Quantity);
        Assert.AreEqual(0.5m, result.MaxQuantity);
        Assert.AreEqual(MeasurementUnit.Cup, result.Unit);
        Assert.AreEqual("1/4–1/2", result.FormattedAmount);
    }

    [TestMethod]
    public void ScaleAndConvert_PreservesRangeUnitsWhenBoundsMismatch()
    {
        // 2-3 tsp * 1.5 -> 3-4.5 tsp (3 tsp -> 1 tbsp, 4.5 tsp -> 1.5 tbsp, both match -> 1-1 1/2 tbsp)
        var resultMatching = CulinaryMath.ScaleAndConvert(
            quantity: 2.0m,
            maxQuantity: 3.0m,
            unit: MeasurementUnit.Tsp,
            customUnitName: null,
            scaleFactor: 1.5m,
            targetSystem: UnitSystem.Original
        );
        Assert.AreEqual(1.0m, resultMatching.Quantity);
        Assert.AreEqual(1.5m, resultMatching.MaxQuantity);
        Assert.AreEqual(MeasurementUnit.Tbsp, resultMatching.Unit);
        Assert.AreEqual("1–1 1/2", resultMatching.FormattedAmount);

        // 2-3 tsp * 2 -> 4-6 tsp (4 tsp does not simplify to tbsp cleanly, 6 tsp simplifies to 2 tbsp -> bounds mismatch)
        var resultMismatch = CulinaryMath.ScaleAndConvert(
            quantity: 2.0m,
            maxQuantity: 3.0m,
            unit: MeasurementUnit.Tsp,
            customUnitName: null,
            scaleFactor: 2.0m,
            targetSystem: UnitSystem.Original
        );
        Assert.AreEqual(4.0m, resultMismatch.Quantity);
        Assert.AreEqual(6.0m, resultMismatch.MaxQuantity);
        Assert.AreEqual(MeasurementUnit.Tsp, resultMismatch.Unit);
        Assert.AreEqual("4–6", resultMismatch.FormattedAmount);
    }
}

using App.Core.Ingestion;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class StepTimerExtractorTests
{
    [TestMethod]
    public void DetectTimers_EmptyOrWhitespace_ReturnsEmpty()
    {
        var result1 = StepTimerExtractor.DetectTimers(null);
        var result2 = StepTimerExtractor.DetectTimers("   ");

        Assert.AreEqual(0, result1.Count);
        Assert.AreEqual(0, result2.Count);
    }

    [TestMethod]
    public void DetectTimers_SingleMinutes_ExtractsCorrectDurationAndVerb()
    {
        var instruction = "Simmer the sauce gently for 15 minutes until thickened.";
        var result = StepTimerExtractor.DetectTimers(instruction, stepNumber: 3);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(900, result[0].DurationSeconds);
        Assert.AreEqual("Simmer", result[0].Label);
    }

    [TestMethod]
    public void DetectTimers_RangeMinutes_TakesLowerBound()
    {
        var instruction = "Bake in the preheated oven for 25-30 minutes until golden brown.";
        var result = StepTimerExtractor.DetectTimers(instruction, stepNumber: 2);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(1500, result[0].DurationSeconds); // 25 * 60
        Assert.AreEqual("Bake", result[0].Label);
    }

    [TestMethod]
    public void DetectTimers_CompositeHoursAndMinutes_SumsSeconds()
    {
        var instruction = "Roast the chicken for 1 hour and 15 minutes.";
        var result = StepTimerExtractor.DetectTimers(instruction, stepNumber: 1);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(4500, result[0].DurationSeconds); // (1 * 3600) + (15 * 60)
        Assert.AreEqual("Roast", result[0].Label);
    }

    [TestMethod]
    public void DetectTimers_Seconds_ExtractsAccurately()
    {
        var instruction = "Sear scallops for 90 seconds per side without moving them.";
        var result = StepTimerExtractor.DetectTimers(instruction, stepNumber: 4);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(90, result[0].DurationSeconds);
        Assert.AreEqual("Step 4 timer", result[0].Label);
    }

    [TestMethod]
    public void DetectTimers_MultipleDistinctTimers_ExtractsBoth()
    {
        var instruction =
            "Boil pasta for 9 minutes, drain, then rest for 2 minutes before serving.";
        var result = StepTimerExtractor.DetectTimers(instruction, stepNumber: 2);

        Assert.AreEqual(2, result.Count);
        Assert.IsTrue(result.Any(t => t.DurationSeconds == 540));
        Assert.IsTrue(result.Any(t => t.DurationSeconds == 120));
    }
}

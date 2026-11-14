using App.Core.Ingestion;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class StepTimerExtractorTests
{
    [TestMethod]
    public void DetectTimers_EqualDurationsInDifferentPhasesAreBothPreserved()
    {
        var timers = StepTimerExtractor.DetectTimers(
            "Cook first side for 2 to 3 minutes. Cook second side for 2 to 3 minutes."
        );
        Assert.HasCount(2, timers);
        Assert.IsTrue(timers.All(timer => timer.DurationSeconds == 120));
        Assert.AreNotEqual(timers[0].Label, timers[1].Label);
    }

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
    [DataRow("Simmer for 2-3 minutes.", 180, 120)]
    [DataRow("Simmer for 2\u20133 minutes.", 180, 120)]
    [DataRow("Simmer for 2\u20143 minutes.", 180, 120)]
    [DataRow("Simmer for 2 to 3 minutes.", 120, 120)]
    [DataRow("Simmer for 2 to 3 minutes, then rest for 3 to 4 minutes.", 180, 180)]
    [DataRow("Simmer for 3 minutes.", 180, 180)]
    public void UseRangeLowerBound_CorrectsOnlyUnambiguousUpperBounds(
        string instruction,
        int supplied,
        int expected
    )
    {
        Assert.AreEqual(expected, StepTimerExtractor.UseRangeLowerBound(supplied, instruction));
    }

    [TestMethod]
    public void DetectTimers_EnDashRange_UsesLowerBound()
    {
        var timers = StepTimerExtractor.DetectTimers("Bake for 25\u201330 minutes.");
        Assert.HasCount(1, timers);
        Assert.AreEqual(1500, timers[0].DurationSeconds);
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
        Assert.AreEqual("Sear", result[0].Label);
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

    [TestMethod]
    public void IsDurationLabel_DetectsPureDurations()
    {
        Assert.IsTrue(StepTimerExtractor.IsDurationLabel("5 minutes"));
        Assert.IsTrue(StepTimerExtractor.IsDurationLabel("3 to 4 minutes"));
        Assert.IsTrue(StepTimerExtractor.IsDurationLabel("3-4 minutes"));
        Assert.IsTrue(StepTimerExtractor.IsDurationLabel("30 seconds"));
        Assert.IsTrue(StepTimerExtractor.IsDurationLabel("1 to 2 minutes"));
        Assert.IsTrue(StepTimerExtractor.IsDurationLabel("about 5 minutes"));
        Assert.IsTrue(StepTimerExtractor.IsDurationLabel("approx. 10 mins"));
        Assert.IsTrue(StepTimerExtractor.IsDurationLabel("1 hour"));
        Assert.IsTrue(StepTimerExtractor.IsDurationLabel("5m"));
        Assert.IsTrue(StepTimerExtractor.IsDurationLabel("30s"));

        Assert.IsFalse(StepTimerExtractor.IsDurationLabel("Simmer sauce"));
        Assert.IsFalse(StepTimerExtractor.IsDurationLabel("Cook onion"));
        Assert.IsFalse(StepTimerExtractor.IsDurationLabel("Rest"));
        Assert.IsFalse(StepTimerExtractor.IsDurationLabel("Bake"));
        Assert.IsFalse(StepTimerExtractor.IsDurationLabel(null));
        Assert.IsFalse(StepTimerExtractor.IsDurationLabel(string.Empty));
    }

    [TestMethod]
    public void CleanTimerLabel_ResolvesActionVerbsAndStripsDurations()
    {
        // 1. Redundant duration label replaced with contextual verb ("sit" -> "Rest")
        var label1 = StepTimerExtractor.CleanTimerLabel(
            "5 minutes",
            "Add ground beef and toss to combine. Let sit for 5 minutes.",
            stepNumber: 1,
            timerSeconds: 300
        );
        Assert.AreEqual("Rest", label1);

        // 2. Redundant duration label replaced with contextual verb ("cook" -> "Cook")
        var label2 = StepTimerExtractor.CleanTimerLabel(
            "3 to 4 minutes",
            "Add onion and garlic and cook, stirring occasionally, until onion is softened, 3 to 4 minutes.",
            stepNumber: 2,
            timerSeconds: 240
        );
        Assert.AreEqual("Cook", label2);

        // 3. Verb with trailing duration cleaned to just verb
        var label3 = StepTimerExtractor.CleanTimerLabel("Simmer for 10 minutes", null, 1);
        Assert.AreEqual("Simmer", label3);

        // 4. Legitimate action label preserved
        var label4 = StepTimerExtractor.CleanTimerLabel("Simmer sauce", null, 1);
        Assert.AreEqual("Simmer sauce", label4);

        // 5. Pure duration with no contextual verb falls back to step timer
        var label5 = StepTimerExtractor.CleanTimerLabel(
            "10 minutes",
            "Leave mixture at room temperature for 10 minutes.",
            stepNumber: 3
        );
        Assert.AreEqual("Step 3 timer", label5);
    }

    [TestMethod]
    public void DetectTimers_LongClauseWithMixAndKnead_ExtractsActionLabelsInsteadOfGenericStepTimer()
    {
        const string instruction =
            "Turn mixer to medium and mix on medium speed until dough forms satiny, sticky ball that clears sides of bowl, 6 to 8 minutes. Turn dough out onto lightly floured counter and knead briefly until smooth, about 1 minute. Place dough in bowl, cover, and chill for at least 24 hours.";

        var timers = StepTimerExtractor.DetectTimers(instruction, stepNumber: 2);

        Assert.AreEqual(3, timers.Count);
        Assert.AreEqual(360, timers[0].DurationSeconds);
        Assert.AreEqual("Mix", timers[0].Label);
        Assert.AreEqual(60, timers[1].DurationSeconds);
        Assert.AreEqual("Knead", timers[1].Label);
        Assert.AreEqual(86400, timers[2].DurationSeconds);
        Assert.AreEqual("Chill", timers[2].Label);
    }
}

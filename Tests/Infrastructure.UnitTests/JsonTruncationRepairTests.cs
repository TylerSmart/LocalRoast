using System.Text.Json;
using App.Infrastructure.Ingestion;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Infrastructure.UnitTests;

[TestClass]
public sealed class JsonTruncationRepairTests
{
    [TestMethod]
    public void TryRepair_AlreadyValidJson_ReturnsTrueAndUnchanged()
    {
        var json = "{\"title\": \"Pasta\", \"steps\": [{\"instruction\": \"Boil water\"}]}";
        var success = JsonTruncationRepair.TryRepair(json, out var repaired);

        Assert.IsTrue(success);
        Assert.AreEqual(json, repaired);
    }

    [TestMethod]
    public void TryRepair_TruncatedInsideString_ClosesStringAndDelimiters()
    {
        // Simulates truncation inside "label": "Sim
        var truncated = "{\"title\": \"Pasta\", \"steps\": [{\"timers\": [{\"label\": \"Sim";
        var success = JsonTruncationRepair.TryRepair(truncated, out var repaired);

        Assert.IsTrue(success);
        using var doc = JsonDocument.Parse(repaired);
        var label = doc
            .RootElement.GetProperty("steps")[0]
            .GetProperty("timers")[0]
            .GetProperty("label")
            .GetString();
        Assert.AreEqual("Sim", label);
    }

    [TestMethod]
    public void TryRepair_TruncatedTrailingColon_StripsDanglingKeyAndCloses()
    {
        // Simulates truncation at "label":
        var truncated =
            "{\"title\": \"Pasta\", \"steps\": [{\"timers\": [{\"durationSeconds\": 300, \"label\":";
        var success = JsonTruncationRepair.TryRepair(truncated, out var repaired);

        Assert.IsTrue(success);
        using var doc = JsonDocument.Parse(repaired);
        var duration = doc
            .RootElement.GetProperty("steps")[0]
            .GetProperty("timers")[0]
            .GetProperty("durationSeconds")
            .GetInt32();
        Assert.AreEqual(300, duration);
    }

    [TestMethod]
    public void TryRepair_TruncatedTrailingComma_StripsCommaAndCloses()
    {
        var truncated = "{\"title\": \"Soup\", \"ingredients\": [{\"displayName\": \"Carrot\"},";
        var success = JsonTruncationRepair.TryRepair(truncated, out var repaired);

        Assert.IsTrue(success);
        using var doc = JsonDocument.Parse(repaired);
        var ingredients = doc.RootElement.GetProperty("ingredients");
        Assert.AreEqual(1, ingredients.GetArrayLength());
    }

    [TestMethod]
    public void TryRepair_CutOffMidStep27_RecoversPreviousSteps()
    {
        var truncated =
            "{\"title\": \"ATK Roast\", \"steps\": [{\"instruction\": \"Step 1\"}, {\"instruction\": \"Step 2\"}, {\"sectionName\": \"Sauce\", \"instruction\": \"Whisk flour and butter.\", \"timers\": [{\"durationSeconds\": 120, \"label\": \"Whisk";
        var success = JsonTruncationRepair.TryRepair(truncated, out var repaired);

        Assert.IsTrue(success);
        using var doc = JsonDocument.Parse(repaired);
        var steps = doc.RootElement.GetProperty("steps");
        Assert.AreEqual(3, steps.GetArrayLength());
        Assert.AreEqual(
            "Whisk",
            steps[2].GetProperty("timers")[0].GetProperty("label").GetString()
        );
    }
}

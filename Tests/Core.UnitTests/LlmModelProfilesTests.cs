using App.Core.Ingestion;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class LlmModelProfilesTests
{
    [TestMethod]
    [DataRow("gemma4:e4b", "gemma4-e4b")]
    [DataRow("  GEMMA4:E4B-it-q8_0 ", "gemma4-e4b")]
    [DataRow("gemma4:e2b", "gemma4-e2b")]
    [DataRow("gemma4:12b", "gemma4-12b")]
    [DataRow("gemma4:26b-a4b", "gemma4-26b-a4b")]
    [DataRow("gemma4-26b-gpu24", null)]
    [DataRow("qwen2.5:14b", null)]
    [DataRow("", null)]
    [DataRow(null, null)]
    public void Match_UsesLongestTagPrefix(string? model, string? expectedId) =>
        Assert.AreEqual(expectedId, LlmModelProfiles.Match(model)?.Id);

    [TestMethod]
    [DataRow(LlmExtractionStrategy.Auto, "gemma4:e4b", true)]
    [DataRow(LlmExtractionStrategy.Auto, "gemma4:e2b", true)]
    [DataRow(LlmExtractionStrategy.Auto, "gemma4:12b", false)]
    [DataRow(LlmExtractionStrategy.Auto, "my-derived-e4b", false)]
    [DataRow(LlmExtractionStrategy.Staged, "my-derived-e4b", true)]
    [DataRow(LlmExtractionStrategy.SinglePass, "gemma4:e4b", false)]
    public void UsesStagedExtraction_FollowsStrategyThenProfile(
        LlmExtractionStrategy strategy,
        string model,
        bool expected
    ) => Assert.AreEqual(expected, LlmModelProfiles.UsesStagedExtraction(strategy, model));

    [TestMethod]
    public void Profiles_HaveUniqueIdsAndValidSettings()
    {
        Assert.HasCount(
            LlmModelProfiles.All.Count,
            LlmModelProfiles.All.Select(profile => profile.Id).Distinct()
        );
        foreach (var profile in LlmModelProfiles.All)
        {
            Assert.IsTrue(profile.Temperature is null or >= 0 and <= 2, profile.Id);
            Assert.IsTrue(profile.TopP is null or >= 0 and <= 1, profile.Id);
            Assert.IsTrue(profile.TopK is null or >= 1 and <= 1000, profile.Id);
            Assert.IsTrue(profile.RequestTimeoutSeconds is >= 5 and <= 1800, profile.Id);
            Assert.IsTrue(profile.ExtractionRepairRounds is >= 0 and <= 3, profile.Id);
        }
        Assert.IsTrue(LlmModelProfiles.All.Any(profile => profile.RecommendedAsRefineModel));
    }

    [TestMethod]
    public void ForRefinement_SwapsInRefineModelSettingsAndUsesSinglePass()
    {
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "gemma4:e4b",
            VisionModel: "gemma4:e4b",
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 600,
            AutoPublishHighConfidenceText: false,
            EnableThinking: false,
            ContextWindowTokens: 32768,
            Temperature: 0,
            ExtractionStrategy: LlmExtractionStrategy.Staged,
            RefineModel: " gemma4:26b-a4b ",
            RefineEnableThinking: true,
            RefineTemperature: 1,
            RefineTopP: 0.95,
            RefineTopK: 64,
            RefineRequestTimeoutSeconds: 1200
        );

        var refine = config.ForRefinement();

        Assert.AreEqual("gemma4:26b-a4b", refine.TextModel);
        Assert.AreEqual("gemma4:26b-a4b", refine.VisionModel);
        Assert.IsTrue(refine.EnableThinking);
        Assert.AreEqual(1.0, refine.Temperature);
        Assert.AreEqual(0.95, refine.TopP);
        Assert.AreEqual(64, refine.TopK);
        Assert.AreEqual(1200, refine.RequestTimeoutSeconds);
        Assert.AreEqual(LlmExtractionStrategy.SinglePass, refine.ExtractionStrategy);
        Assert.AreEqual(32768, refine.ContextWindowTokens);
        Assert.AreEqual(config.BaseUrl, refine.BaseUrl);
    }
}

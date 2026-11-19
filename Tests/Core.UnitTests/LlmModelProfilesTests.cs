using App.Core.Ingestion;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class LlmModelProfilesTests
{
    [TestMethod]
    public void LlmWorkflow_IncludesEmbedding()
    {
        Assert.IsTrue(Enum.IsDefined(LlmWorkflow.Embedding));
    }

    [TestMethod]
    public void ForRefinement_SwapsInRefineModelSettings()
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
        Assert.AreEqual(32768, refine.ContextWindowTokens);
        Assert.AreEqual(config.BaseUrl, refine.BaseUrl);
    }

    [TestMethod]
    public void ForEmbedding_ReturnsNullWhenUnsetAndUsesEmbeddingOverrideWhenConfigured()
    {
        var baseConfig = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "qwen2.5:14b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 120,
            AutoPublishHighConfidenceText: false
        );

        Assert.IsNull(baseConfig.ForEmbedding());

        var withModelName = baseConfig with { EmbeddingModel = "embeddinggemma" };
        var fallbackResolved = withModelName.ForEmbedding();
        Assert.IsNotNull(fallbackResolved);
        Assert.AreEqual("embeddinggemma", fallbackResolved.TextModel);

        var dedicatedConfig = baseConfig with
        {
            BaseUrl = "http://embed-host:11434",
            TextModel = "nomic-embed-text",
        };
        var withEmbeddingConfig = withModelName with { EmbeddingConfig = dedicatedConfig };
        var overrideResolved = withEmbeddingConfig.ForEmbedding();
        Assert.IsNotNull(overrideResolved);
        Assert.AreEqual("nomic-embed-text", overrideResolved.TextModel);
        Assert.AreEqual("http://embed-host:11434", overrideResolved.BaseUrl);
    }

    [TestMethod]
    public void EffectiveConcurrencyAndResetSchedule_PreferModelOverrideAndFallBackToConnection()
    {
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.OpenAiCompatible,
            BaseUrl: "https://api.example.com/v1",
            ApiKey: "secret",
            TextModel: "gemini-flash",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 120,
            AutoPublishHighConfidenceText: false,
            MaxConcurrentRequests: 3,
            DailyQuotaResetTimezone: "UTC",
            DailyQuotaResetTime: "00:00"
        );

        Assert.AreEqual(3, config.EffectiveMaxConcurrentRequests);
        Assert.AreEqual("UTC", config.EffectiveQuotaResetTimezone);
        Assert.AreEqual("00:00", config.EffectiveQuotaResetTime);

        var withOverrides = config with
        {
            ModelMaxConcurrentRequests = 1,
            ModelDailyQuotaResetTimezone = "America/Los_Angeles",
            ModelDailyQuotaResetTime = "03:00",
        };

        Assert.AreEqual(1, withOverrides.EffectiveMaxConcurrentRequests);
        Assert.AreEqual("America/Los_Angeles", withOverrides.EffectiveQuotaResetTimezone);
        Assert.AreEqual("03:00", withOverrides.EffectiveQuotaResetTime);

        // Model cannot exceed connection max concurrency
        var higherModelConcurrency = config with
        {
            ModelMaxConcurrentRequests = 5,
        };
        Assert.AreEqual(3, higherModelConcurrency.EffectiveMaxConcurrentRequests);
    }

    [TestMethod]
    public void HasDistinctRefinement_ReturnsTrueWhenSameModelUsesDifferentProfileTuning()
    {
        var textConfig = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "gemma4:12b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 120,
            AutoPublishHighConfidenceText: false,
            EnableThinking: false,
            ContextWindowTokens: 8192,
            ConnectionKey: "conn-1",
            ModelKey: "conn-1:gemma4:12b"
        );

        var sameModelSameProfile = textConfig with
        {
            RefineModel = "gemma4:12b",
            RefineConfig = textConfig,
        };
        Assert.IsFalse(sameModelSameProfile.HasDistinctRefinement());

        var sameModelThinkingProfile = textConfig with
        {
            RefineModel = "gemma4:12b",
            RefineConfig = textConfig with
            {
                EnableThinking = true,
                Temperature = 1.0,
                TopP = 0.95,
                TopK = 64,
                ContextWindowTokens = 32768,
                RequestTimeoutSeconds = 900,
            },
        };
        Assert.IsTrue(sameModelThinkingProfile.HasDistinctRefinement());
    }
}

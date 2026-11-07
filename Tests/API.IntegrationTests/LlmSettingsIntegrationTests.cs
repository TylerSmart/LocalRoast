using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using App.Core.Authentication;
using App.Core.Ingestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.API.IntegrationTests;

[TestClass]
public sealed class LlmSettingsIntegrationTests
{
    private AppWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    [TestInitialize]
    public void Setup()
    {
        _factory = new AppWebApplicationFactory();
        _client = _factory.CreateClient();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static async Task<AuthResponse> AuthenticateAsync(HttpClient client)
    {
        var email = $"chef_{Guid.NewGuid():N}@example.com";
        var username = $"chef_{Guid.NewGuid():N}"[..12];
        var register = new RegisterRequest(email, username, "KitchenSecret123!");
        var response = await client.PostAsJsonAsync("/api/auth/register", register);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.IsNotNull(auth);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            auth.Token
        );
        return auth;
    }

    [TestMethod]
    public async Task GetSettings_Unauthenticated_ReturnsUnauthorized()
    {
        var response = await _client.GetAsync("/api/llm-settings");
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task GetSettings_Authenticated_ReturnsDefaultSettingsWithoutPlaintextKey()
    {
        await AuthenticateAsync(_client);

        var response = await _client.GetAsync("/api/llm-settings");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var settings = await response.Content.ReadFromJsonAsync<LlmSettingsDto>();
        Assert.IsNotNull(settings);
        Assert.AreEqual(LlmProvider.Ollama, settings.Provider);
        Assert.IsFalse(settings.HasApiKey);
        Assert.IsNull(settings.ApiKeyMasked);
        Assert.AreEqual(8192, settings.ContextWindowTokens);
        Assert.IsFalse(settings.OllamaCpuOnly);
        Assert.IsNull(settings.Temperature);
        Assert.IsNull(settings.TopP);
        Assert.IsNull(settings.TopK);
        Assert.AreEqual(1, settings.ExtractionRepairRounds);
    }

    [TestMethod]
    public async Task RepairRoundsAndLongTimeout_PersistAndReachEffectiveConfiguration()
    {
        await AuthenticateAsync(_client);

        var response = await _client.PutAsJsonAsync(
            "/api/llm-settings",
            new UpdateLlmSettingsRequest(
                TextModel: "gemma4:26b-a4b",
                RequestTimeoutSeconds: 1800,
                ExtractionRepairRounds: 3
            )
        );
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var settings = await _client.GetFromJsonAsync<LlmSettingsDto>("/api/llm-settings");
        Assert.IsNotNull(settings);
        Assert.AreEqual(1800, settings.RequestTimeoutSeconds);
        Assert.AreEqual(3, settings.ExtractionRepairRounds);
        using (var scope = _factory.Services.CreateScope())
        {
            var effective = await scope
                .ServiceProvider.GetRequiredService<ILlmSettingsService>()
                .GetEffectiveConfigAsync();
            Assert.AreEqual(1800, effective.RequestTimeoutSeconds);
            Assert.AreEqual(3, effective.ExtractionRepairRounds);
        }

        response = await _client.PutAsJsonAsync(
            "/api/llm-settings",
            new UpdateLlmSettingsRequest(TextModel: "gemma4:e4b", ExtractionRepairRounds: 0)
        );
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var disabled = await response.Content.ReadFromJsonAsync<LlmSettingsDto>();
        Assert.IsNotNull(disabled);
        Assert.AreEqual(0, disabled.ExtractionRepairRounds);
    }

    [TestMethod]
    [DataRow(1801, 1)]
    [DataRow(4, 1)]
    [DataRow(120, 6)]
    [DataRow(120, -1)]
    public async Task UpdateSettings_OutOfRangeTimeoutOrRepairRounds_ReturnsBadRequest(
        int timeoutSeconds,
        int repairRounds
    )
    {
        await AuthenticateAsync(_client);

        var response = await _client.PutAsJsonAsync(
            "/api/llm-settings",
            new UpdateLlmSettingsRequest(
                RequestTimeoutSeconds: timeoutSeconds,
                ExtractionRepairRounds: repairRounds
            )
        );

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var settings = await _client.GetFromJsonAsync<LlmSettingsDto>("/api/llm-settings");
        Assert.IsNotNull(settings);
        Assert.AreEqual(1, settings.ExtractionRepairRounds);
        Assert.AreEqual(120, settings.RequestTimeoutSeconds);
    }

    [TestMethod]
    public async Task UpdateSettings_ValidRequest_UpdatesAndMasksApiKey()
    {
        await AuthenticateAsync(_client);

        var request = new UpdateLlmSettingsRequest(
            Provider: LlmProvider.OpenAiCompatible,
            BaseUrl: "http://localhost:1234/v1",
            ApiKey: "sk-test-secret-12345678",
            ClearApiKey: false,
            TextModel: "gemma-2-9b-it",
            VisionModel: "qwen2.5vl:7b",
            OllamaKeepAlive: "10m",
            RequestTimeoutSeconds: 60,
            AutoPublishHighConfidenceText: true,
            EnableThinking: true,
            MaxIngestionRetries: 5,
            ContextWindowTokens: 16384
        );

        var putResponse = await _client.PutAsJsonAsync("/api/llm-settings", request);
        Assert.AreEqual(HttpStatusCode.OK, putResponse.StatusCode);

        var updated = await putResponse.Content.ReadFromJsonAsync<LlmSettingsDto>();
        Assert.IsNotNull(updated);
        Assert.AreEqual(LlmProvider.OpenAiCompatible, updated.Provider);
        Assert.AreEqual("http://localhost:1234/v1", updated.BaseUrl);
        Assert.IsTrue(updated.HasApiKey);
        StringAssert.StartsWith(updated.ApiKeyMasked, "••••");
        StringAssert.EndsWith(updated.ApiKeyMasked, "5678");
        Assert.AreEqual("gemma-2-9b-it", updated.TextModel);
        Assert.IsTrue(updated.AutoPublishHighConfidenceText);
        Assert.IsTrue(updated.EnableThinking);
        Assert.AreEqual(5, updated.MaxIngestionRetries);
        Assert.AreEqual(16384, updated.ContextWindowTokens);

        // Update without providing ApiKey again -> preserves existing key
        var secondRequest = new UpdateLlmSettingsRequest(
            Provider: LlmProvider.OpenAiCompatible,
            BaseUrl: "http://localhost:1234/v1",
            ApiKey: null,
            ClearApiKey: false,
            TextModel: "llama-3-8b",
            VisionModel: null
        );

        var secondPut = await _client.PutAsJsonAsync("/api/llm-settings", secondRequest);
        Assert.AreEqual(HttpStatusCode.OK, secondPut.StatusCode);

        var secondUpdated = await secondPut.Content.ReadFromJsonAsync<LlmSettingsDto>();
        Assert.IsNotNull(secondUpdated);
        Assert.IsTrue(secondUpdated.HasApiKey);
        Assert.AreEqual("llama-3-8b", secondUpdated.TextModel);

        // Update with ClearApiKey = true -> removes key
        var clearRequest = new UpdateLlmSettingsRequest(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            ClearApiKey: true
        );

        var clearPut = await _client.PutAsJsonAsync("/api/llm-settings", clearRequest);
        Assert.AreEqual(HttpStatusCode.OK, clearPut.StatusCode);

        var cleared = await clearPut.Content.ReadFromJsonAsync<LlmSettingsDto>();
        Assert.IsNotNull(cleared);
        Assert.IsFalse(cleared.HasApiKey);
        Assert.IsNull(cleared.ApiKeyMasked);
    }

    [TestMethod]
    public async Task OllamaCpuOnly_PersistsAndReachesEffectiveConfiguration()
    {
        await AuthenticateAsync(_client);
        foreach (var enabled in new[] { true, false })
        {
            var response = await _client.PutAsJsonAsync(
                "/api/llm-settings",
                new UpdateLlmSettingsRequest(TextModel: "gemma4:26b-a4b", OllamaCpuOnly: enabled)
            );
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var settings = await _client.GetFromJsonAsync<LlmSettingsDto>("/api/llm-settings");
            Assert.IsNotNull(settings);
            Assert.AreEqual(enabled, settings.OllamaCpuOnly);
            using var scope = _factory.Services.CreateScope();
            var effective = await scope
                .ServiceProvider.GetRequiredService<ILlmSettingsService>()
                .GetEffectiveConfigAsync();
            Assert.AreEqual(enabled, effective.OllamaCpuOnly);
        }
    }

    [TestMethod]
    public async Task SamplingOverrides_PersistReachEffectiveConfigurationAndClear()
    {
        await AuthenticateAsync(_client);

        var response = await _client.PutAsJsonAsync(
            "/api/llm-settings",
            new UpdateLlmSettingsRequest(
                TextModel: "gemma4:12b",
                EnableThinking: true,
                Temperature: 1.0,
                TopP: 0.95,
                TopK: 64
            )
        );
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var settings = await _client.GetFromJsonAsync<LlmSettingsDto>("/api/llm-settings");
        Assert.IsNotNull(settings);
        Assert.AreEqual(1.0, settings.Temperature);
        Assert.AreEqual(0.95, settings.TopP);
        Assert.AreEqual(64, settings.TopK);
        using (var scope = _factory.Services.CreateScope())
        {
            var effective = await scope
                .ServiceProvider.GetRequiredService<ILlmSettingsService>()
                .GetEffectiveConfigAsync();
            Assert.AreEqual(1.0, effective.Temperature);
            Assert.AreEqual(0.95, effective.TopP);
            Assert.AreEqual(64, effective.TopK);
        }

        response = await _client.PutAsJsonAsync(
            "/api/llm-settings",
            new UpdateLlmSettingsRequest(TextModel: "gemma4:e4b", EnableThinking: true)
        );
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var cleared = await response.Content.ReadFromJsonAsync<LlmSettingsDto>();
        Assert.IsNotNull(cleared);
        Assert.IsNull(cleared.Temperature);
        Assert.IsNull(cleared.TopP);
        Assert.IsNull(cleared.TopK);
        using (var scope = _factory.Services.CreateScope())
        {
            var effective = await scope
                .ServiceProvider.GetRequiredService<ILlmSettingsService>()
                .GetEffectiveConfigAsync();
            Assert.IsNull(effective.Temperature);
            Assert.IsNull(effective.TopP);
            Assert.IsNull(effective.TopK);
        }
    }

    [TestMethod]
    [DataRow(2.01, null, null)]
    [DataRow(-0.1, null, null)]
    [DataRow(null, 1.01, null)]
    [DataRow(null, -0.01, null)]
    [DataRow(null, null, 0)]
    [DataRow(null, null, 1001)]
    public async Task UpdateSettings_OutOfRangeSampling_ReturnsBadRequest(
        double? temperature,
        double? topP,
        int? topK
    )
    {
        await AuthenticateAsync(_client);

        var response = await _client.PutAsJsonAsync(
            "/api/llm-settings",
            new UpdateLlmSettingsRequest(Temperature: temperature, TopP: topP, TopK: topK)
        );

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var settings = await _client.GetFromJsonAsync<LlmSettingsDto>("/api/llm-settings");
        Assert.IsNotNull(settings);
        Assert.IsNull(settings.Temperature);
        Assert.IsNull(settings.TopP);
        Assert.IsNull(settings.TopK);
    }

    [TestMethod]
    public async Task ModelProfiles_ListRecommendedSettingsForEachModel()
    {
        await AuthenticateAsync(_client);

        var profiles = await _client.GetFromJsonAsync<List<LlmModelProfileDto>>(
            "/api/llm-settings/model-profiles"
        );

        Assert.IsNotNull(profiles);
        var e4b = profiles.Single(profile => profile.Id == "gemma4-e4b");
        Assert.IsTrue(e4b.UseStagedExtraction);
        Assert.IsTrue(e4b.EnableThinking);
        var thinking = profiles.Single(profile => profile.Id == "gemma4-12b-thinking");
        Assert.IsTrue(thinking.RecommendedAsRefineModel);
        Assert.AreEqual(1.0, thinking.Temperature);
        Assert.AreEqual(64, thinking.TopK);
    }

    [TestMethod]
    public async Task ExtractionStrategyAndRefineSettings_PersistAndReachRefineConfiguration()
    {
        await AuthenticateAsync(_client);

        var response = await _client.PutAsJsonAsync(
            "/api/llm-settings",
            new UpdateLlmSettingsRequest(
                TextModel: "my-tuned-e4b",
                ExtractionStrategy: LlmExtractionStrategy.Staged,
                RefineModel: "  gemma4:26b-a4b  ",
                RefineEnableThinking: false,
                RefineTemperature: 1.0,
                RefineTopP: 0.95,
                RefineTopK: 64,
                RefineRequestTimeoutSeconds: 900
            )
        );
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var settings = await response.Content.ReadFromJsonAsync<LlmSettingsDto>();
        Assert.IsNotNull(settings);
        Assert.AreEqual(LlmExtractionStrategy.Staged, settings.ExtractionStrategy);
        Assert.AreEqual("gemma4:26b-a4b", settings.RefineModel);
        Assert.IsFalse(settings.RefineEnableThinking);
        Assert.AreEqual(900, settings.RefineRequestTimeoutSeconds);

        using (var scope = _factory.Services.CreateScope())
        {
            var effective = await scope
                .ServiceProvider.GetRequiredService<ILlmSettingsService>()
                .GetEffectiveConfigAsync();
            Assert.IsTrue(
                LlmModelProfiles.UsesStagedExtraction(
                    effective.ExtractionStrategy,
                    effective.TextModel
                )
            );
            var refine = effective.ForRefinement();
            Assert.AreEqual("gemma4:26b-a4b", refine.TextModel);
            Assert.AreEqual("gemma4:26b-a4b", refine.VisionModel);
            Assert.IsFalse(refine.EnableThinking);
            Assert.AreEqual(1.0, refine.Temperature);
            Assert.AreEqual(0.95, refine.TopP);
            Assert.AreEqual(64, refine.TopK);
            Assert.AreEqual(900, refine.RequestTimeoutSeconds);
            Assert.AreEqual(LlmExtractionStrategy.SinglePass, refine.ExtractionStrategy);
        }

        response = await _client.PutAsJsonAsync(
            "/api/llm-settings",
            new UpdateLlmSettingsRequest(TextModel: "gemma4:e4b", RefineModel: "   ")
        );
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var cleared = await response.Content.ReadFromJsonAsync<LlmSettingsDto>();
        Assert.IsNull(cleared!.RefineModel);
        Assert.AreEqual(LlmExtractionStrategy.Auto, cleared.ExtractionStrategy);
    }

    [TestMethod]
    [DataRow(2.01, null, null, 1200)]
    [DataRow(null, 1.01, null, 1200)]
    [DataRow(null, null, 0, 1200)]
    [DataRow(null, null, null, 4)]
    public async Task UpdateSettings_OutOfRangeRefineSettings_ReturnsBadRequest(
        double? temperature,
        double? topP,
        int? topK,
        int timeout
    )
    {
        await AuthenticateAsync(_client);

        var response = await _client.PutAsJsonAsync(
            "/api/llm-settings",
            new UpdateLlmSettingsRequest(
                RefineModel: "gemma4:26b-a4b",
                RefineTemperature: temperature,
                RefineTopP: topP,
                RefineTopK: topK,
                RefineRequestTimeoutSeconds: timeout
            )
        );

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task UpdateSettings_InvalidBaseUrl_ReturnsBadRequest()
    {
        await AuthenticateAsync(_client);

        var request = new UpdateLlmSettingsRequest(
            Provider: LlmProvider.Ollama,
            BaseUrl: "not-a-valid-url"
        );

        var response = await _client.PutAsJsonAsync("/api/llm-settings", request);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task TestConnection_InvalidUrl_ReturnsBadRequest()
    {
        await AuthenticateAsync(_client);

        var request = new TestLlmConnectionRequest(BaseUrl: "ftp://invalid-scheme");

        var response = await _client.PostAsJsonAsync("/api/llm-settings/test", request);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task TestConnection_UnreachableHost_ReturnsOkWithIsReachableFalse()
    {
        await AuthenticateAsync(_client);

        var request = new TestLlmConnectionRequest(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://127.0.0.1:59999",
            TimeoutSeconds: 5
        );

        var response = await _client.PostAsJsonAsync("/api/llm-settings/test", request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<LlmConnectionTestResultDto>();
        Assert.IsNotNull(result);
        Assert.IsFalse(result.IsReachable);
        Assert.IsNotNull(result.ErrorMessage);
    }

    [TestMethod]
    public async Task SetIngestionEnabled_TogglesState_AndRevertsWorkingJobsToQueued()
    {
        await AuthenticateAsync(_client);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<App.Data.AppDbContext>();
            var job = new App.Data.Entities.IngestionJob
            {
                Channel = IngestionChannel.RawText,
                Status = IngestionJobStatus.Extracting,
                DisplayTitle = "In-Flight Recipe",
                RawText = "1 cup flour\nMix and bake.",
                CreatedBy = "test-user",
                ModifiedBy = "test-user",
            };
            db.IngestionJobs.Add(job);
            await db.SaveChangesAsync();
        }

        // Toggle off
        var disableResponse = await _client.PostAsync(
            "/api/llm-settings/set-ingestion-enabled?enabled=false",
            null
        );
        Assert.AreEqual(HttpStatusCode.OK, disableResponse.StatusCode);

        var disabled = await disableResponse.Content.ReadFromJsonAsync<LlmSettingsDto>();
        Assert.IsNotNull(disabled);
        Assert.IsFalse(disabled.EnableIngestion);

        // Verify the job was reverted to Queued
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<App.Data.AppDbContext>();
            var job =
                await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync(
                    db.IngestionJobs,
                    j => j.DisplayTitle == "In-Flight Recipe"
                );
            Assert.AreEqual(IngestionJobStatus.Queued, job.Status);
            StringAssert.Contains(job.StatusDetail, "Paused");
        }

        // Toggle back on
        var enableResponse = await _client.PostAsync(
            "/api/llm-settings/set-ingestion-enabled?enabled=true",
            null
        );
        Assert.AreEqual(HttpStatusCode.OK, enableResponse.StatusCode);

        var enabled = await enableResponse.Content.ReadFromJsonAsync<LlmSettingsDto>();
        Assert.IsNotNull(enabled);
        Assert.IsTrue(enabled.EnableIngestion);
    }
}

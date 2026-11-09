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
    }

    [TestMethod]
    public async Task LongTimeout_PersistsAndReachesEffectiveConfiguration()
    {
        await AuthenticateAsync(_client);

        var response = await _client.PutAsJsonAsync(
            "/api/llm-settings",
            new UpdateLlmSettingsRequest(TextModel: "gemma4:26b-a4b", RequestTimeoutSeconds: 1800)
        );
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var settings = await _client.GetFromJsonAsync<LlmSettingsDto>("/api/llm-settings");
        Assert.IsNotNull(settings);
        Assert.AreEqual(1800, settings.RequestTimeoutSeconds);
        using (var scope = _factory.Services.CreateScope())
        {
            var effective = await scope
                .ServiceProvider.GetRequiredService<ILlmSettingsService>()
                .GetEffectiveConfigAsync();
            Assert.AreEqual(1800, effective.RequestTimeoutSeconds);
        }
    }

    [TestMethod]
    [DataRow(1801)]
    [DataRow(4)]
    public async Task UpdateSettings_OutOfRangeTimeout_ReturnsBadRequest(int timeoutSeconds)
    {
        await AuthenticateAsync(_client);

        var response = await _client.PutAsJsonAsync(
            "/api/llm-settings",
            new UpdateLlmSettingsRequest(RequestTimeoutSeconds: timeoutSeconds)
        );

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var settings = await _client.GetFromJsonAsync<LlmSettingsDto>("/api/llm-settings");
        Assert.IsNotNull(settings);
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
        Assert.IsTrue(e4b.EnableThinking);
        var thinking = profiles.Single(profile => profile.Id == "gemma4-12b-thinking");
        Assert.IsTrue(thinking.RecommendedAsRefineModel);
        Assert.AreEqual(1.0, thinking.Temperature);
        Assert.AreEqual(64, thinking.TopK);
    }

    [TestMethod]
    public async Task RefineSettings_PersistAndReachRefineConfiguration()
    {
        await AuthenticateAsync(_client);

        var response = await _client.PutAsJsonAsync(
            "/api/llm-settings",
            new UpdateLlmSettingsRequest(
                TextModel: "my-tuned-e4b",
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
        Assert.AreEqual("gemma4:26b-a4b", settings.RefineModel);
        Assert.IsFalse(settings.RefineEnableThinking);
        Assert.AreEqual(900, settings.RefineRequestTimeoutSeconds);

        using (var scope = _factory.Services.CreateScope())
        {
            var effective = await scope
                .ServiceProvider.GetRequiredService<ILlmSettingsService>()
                .GetEffectiveConfigAsync();
            var refine = effective.ForRefinement();
            Assert.AreEqual("gemma4:26b-a4b", refine.TextModel);
            Assert.AreEqual("gemma4:26b-a4b", refine.VisionModel);
            Assert.IsFalse(refine.EnableThinking);
            Assert.AreEqual(1.0, refine.Temperature);
            Assert.AreEqual(0.95, refine.TopP);
            Assert.AreEqual(64, refine.TopK);
            Assert.AreEqual(900, refine.RequestTimeoutSeconds);
        }

        response = await _client.PutAsJsonAsync(
            "/api/llm-settings",
            new UpdateLlmSettingsRequest(TextModel: "gemma4:e4b", RefineModel: "   ")
        );
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var cleared = await response.Content.ReadFromJsonAsync<LlmSettingsDto>();
        Assert.IsNull(cleared!.RefineModel);
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

    [TestMethod]
    public async Task GetModelProfiles_Authenticated_ReturnsSeededDefaults()
    {
        await AuthenticateAsync(_client);

        var response = await _client.GetAsync("/api/llm-settings/model-profiles");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var profiles = await response.Content.ReadFromJsonAsync<List<LlmModelProfileDto>>();
        Assert.IsNotNull(profiles);
        Assert.AreEqual(5, profiles.Count);
        Assert.IsTrue(profiles.Any(p => p.Id == "gemma4-e4b" && p.IsBuiltInDefault));
        Assert.IsTrue(profiles.Any(p => p.Id == "gemma4-e2b" && p.IsBuiltInDefault));
        Assert.IsTrue(profiles.Any(p => p.Id == "gemma4-12b" && p.IsBuiltInDefault));
        Assert.IsTrue(
            profiles.Any(p => p.Id == "gemma4-12b-thinking" && p.RecommendedAsRefineModel)
        );
        Assert.IsTrue(profiles.Any(p => p.Id == "gemma4-26b-a4b" && p.RecommendedAsRefineModel));
    }

    [TestMethod]
    public async Task CreateModelProfile_ValidRequest_CreatesAndReturns201()
    {
        await AuthenticateAsync(_client);

        var req = new UpsertLlmModelProfileRequest(
            DisplayName: "Qwen 2.5 14B Custom",
            ModelTag: "qwen2.5:14b",
            MatchPrefixes: ["qwen2.5:14b"],
            Summary: "Solid mid-size model for high-fidelity extraction.",
            EnableThinking: true,
            Temperature: 0.7,
            TopP: 0.9,
            TopK: 40,
            ContextWindowTokens: 16384,
            RequestTimeoutSeconds: 450,
            RecommendedAsRefineModel: true,
            Notes: ["Custom Ollama build"]
        );

        var postResponse = await _client.PostAsJsonAsync("/api/llm-settings/model-profiles", req);
        Assert.AreEqual(HttpStatusCode.Created, postResponse.StatusCode);

        var created = await postResponse.Content.ReadFromJsonAsync<LlmModelProfileDto>();
        Assert.IsNotNull(created);
        Assert.AreEqual("qwen-2-5-14b-custom", created.Id);
        Assert.AreEqual("Qwen 2.5 14B Custom", created.DisplayName);
        Assert.AreEqual("qwen2.5:14b", created.ModelTag);
        Assert.IsFalse(created.IsBuiltInDefault);
        Assert.AreEqual(16384, created.ContextWindowTokens);
        Assert.AreEqual(450, created.RequestTimeoutSeconds);
        Assert.IsTrue(created.RecommendedAsRefineModel);

        var getResponse = await _client.GetAsync("/api/llm-settings/model-profiles");
        var list = await getResponse.Content.ReadFromJsonAsync<List<LlmModelProfileDto>>();
        Assert.IsNotNull(list);
        Assert.AreEqual(6, list.Count);
        Assert.IsTrue(list.Any(p => p.Id == created.Id));
    }

    [TestMethod]
    public async Task UpdateModelProfile_ValidRequest_UpdatesAndReturns200()
    {
        await AuthenticateAsync(_client);

        var req = new UpsertLlmModelProfileRequest(
            DisplayName: "Test Custom Model",
            ModelTag: "test:latest",
            Summary: "Initial summary."
        );
        var postResponse = await _client.PostAsJsonAsync("/api/llm-settings/model-profiles", req);
        var created = await postResponse.Content.ReadFromJsonAsync<LlmModelProfileDto>();
        Assert.IsNotNull(created);

        var updateReq = req with
        {
            Summary = "Updated summary.",
            Temperature = 0.5,
            RequestTimeoutSeconds = 800,
        };
        var putResponse = await _client.PutAsJsonAsync(
            $"/api/llm-settings/model-profiles/{created.Id}",
            updateReq
        );
        Assert.AreEqual(HttpStatusCode.OK, putResponse.StatusCode);

        var updated = await putResponse.Content.ReadFromJsonAsync<LlmModelProfileDto>();
        Assert.IsNotNull(updated);
        Assert.AreEqual("Updated summary.", updated.Summary);
        Assert.AreEqual(0.5, updated.Temperature);
        Assert.AreEqual(800, updated.RequestTimeoutSeconds);
    }

    [TestMethod]
    public async Task UpdateModelProfile_NotFound_Returns404()
    {
        await AuthenticateAsync(_client);

        var updateReq = new UpsertLlmModelProfileRequest(
            DisplayName: "Missing Model",
            ModelTag: "missing:tag"
        );
        var putResponse = await _client.PutAsJsonAsync(
            "/api/llm-settings/model-profiles/does-not-exist",
            updateReq
        );
        Assert.AreEqual(HttpStatusCode.NotFound, putResponse.StatusCode);
    }

    [TestMethod]
    public async Task DeleteModelProfile_ValidId_DeletesAndReturns204()
    {
        await AuthenticateAsync(_client);

        var req = new UpsertLlmModelProfileRequest(
            DisplayName: "To Be Deleted",
            ModelTag: "delete-me:latest"
        );
        var postResponse = await _client.PostAsJsonAsync("/api/llm-settings/model-profiles", req);
        var created = await postResponse.Content.ReadFromJsonAsync<LlmModelProfileDto>();
        Assert.IsNotNull(created);

        var deleteResponse = await _client.DeleteAsync(
            $"/api/llm-settings/model-profiles/{created.Id}"
        );
        Assert.AreEqual(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var getResponse = await _client.GetAsync("/api/llm-settings/model-profiles");
        var list = await getResponse.Content.ReadFromJsonAsync<List<LlmModelProfileDto>>();
        Assert.IsNotNull(list);
        Assert.IsFalse(list.Any(p => p.Id == created.Id));
    }

    [TestMethod]
    public async Task DeleteModelProfile_NotFound_Returns404()
    {
        await AuthenticateAsync(_client);

        var deleteResponse = await _client.DeleteAsync(
            "/api/llm-settings/model-profiles/does-not-exist"
        );
        Assert.AreEqual(HttpStatusCode.NotFound, deleteResponse.StatusCode);
    }

    [TestMethod]
    public async Task RestoreDefaultModelProfiles_RestoresDefaultsPreservingCustomProfiles()
    {
        await AuthenticateAsync(_client);

        // Create a custom profile
        var customReq = new UpsertLlmModelProfileRequest(
            DisplayName: "Custom Profile To Keep",
            ModelTag: "keep-me:latest"
        );
        var postResponse = await _client.PostAsJsonAsync(
            "/api/llm-settings/model-profiles",
            customReq
        );
        var customCreated = await postResponse.Content.ReadFromJsonAsync<LlmModelProfileDto>();
        Assert.IsNotNull(customCreated);

        // Delete one default profile
        var deleteResponse = await _client.DeleteAsync(
            "/api/llm-settings/model-profiles/gemma4-e2b"
        );
        Assert.AreEqual(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // Modify another default profile
        var updateReq = new UpsertLlmModelProfileRequest(
            DisplayName: "Modified Gemma 4 E4B",
            ModelTag: "gemma4:e4b",
            Summary: "Overridden summary."
        );
        var putResponse = await _client.PutAsJsonAsync(
            "/api/llm-settings/model-profiles/gemma4-e4b",
            updateReq
        );
        Assert.AreEqual(HttpStatusCode.OK, putResponse.StatusCode);

        // Restore defaults
        var restoreResponse = await _client.PostAsync(
            "/api/llm-settings/model-profiles/restore-defaults",
            null
        );
        Assert.AreEqual(HttpStatusCode.OK, restoreResponse.StatusCode);

        var list = await restoreResponse.Content.ReadFromJsonAsync<List<LlmModelProfileDto>>();
        Assert.IsNotNull(list);

        // Both gemma4-e2b restored and gemma4-e4b reset to default
        var e2b = list.FirstOrDefault(p => p.Id == "gemma4-e2b");
        Assert.IsNotNull(e2b);

        var e4b = list.FirstOrDefault(p => p.Id == "gemma4-e4b");
        Assert.IsNotNull(e4b);
        Assert.AreEqual("Gemma 4 E4B", e4b.DisplayName);
        StringAssert.Contains(e4b.Summary, "Default choice");

        // Custom profile is still intact
        var custom = list.FirstOrDefault(p => p.Id == customCreated.Id);
        Assert.IsNotNull(custom);
    }

    [TestMethod]
    public async Task CreateModelProfile_ValidationErrors_ReturnsBadRequest()
    {
        await AuthenticateAsync(_client);

        // Missing DisplayName
        var invalidReq = new UpsertLlmModelProfileRequest(DisplayName: "", ModelTag: "valid:tag");
        var resp1 = await _client.PostAsJsonAsync("/api/llm-settings/model-profiles", invalidReq);
        Assert.AreEqual(HttpStatusCode.BadRequest, resp1.StatusCode);

        // Invalid temperature
        var invalidTemp = new UpsertLlmModelProfileRequest(
            DisplayName: "Bad Temp",
            ModelTag: "valid:tag",
            Temperature: 3.5
        );
        var resp2 = await _client.PostAsJsonAsync("/api/llm-settings/model-profiles", invalidTemp);
        Assert.AreEqual(HttpStatusCode.BadRequest, resp2.StatusCode);
    }
}

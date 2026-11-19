using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using App.Core.Authentication;
using App.Core.Ingestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.API.IntegrationTests;

[TestClass]
public sealed class LlmConnectionsIntegrationTests
{
    private AppWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _factory = new AppWebApplicationFactory();
        _client = _factory.CreateClient();

        var register = new RegisterRequest(
            $"chef_{Guid.NewGuid():N}@example.com",
            $"chef_{Guid.NewGuid():N}"[..12],
            "KitchenSecret123!"
        );
        var response = await _client.PostAsJsonAsync("/api/auth/register", register);
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            auth!.Token
        );
    }

    [TestCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<LlmConnectionDto> CreateConnectionAsync(
        string name,
        string baseUrl,
        LlmProvider provider = LlmProvider.Ollama,
        string? apiKey = null,
        int? dailyQuota = null,
        int maxConcurrent = 1
    )
    {
        var response = await _client.PostAsJsonAsync(
            "/api/llm-settings/connections",
            new UpsertLlmConnectionRequest(
                name,
                provider,
                baseUrl,
                ApiKey: apiKey,
                MaxConcurrentRequests: maxConcurrent,
                DailyRequestQuota: dailyQuota
            )
        );
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LlmConnectionDto>())!;
    }

    private async Task<LlmModelConfigurationDto> CreateConfigurationAsync(
        string name,
        Guid connectionId,
        string model,
        int? gpuLayers = null,
        int? maxConcurrent = null,
        int? rpm = null,
        int? rpd = null,
        string? resetTimezone = null,
        string? resetTime = null
    )
    {
        var response = await _client.PostAsJsonAsync(
            "/api/llm-settings/model-configurations",
            new UpsertLlmModelConfigurationRequest(
                name,
                connectionId,
                model,
                OllamaGpuLayers: gpuLayers,
                MaxConcurrentRequests: maxConcurrent,
                RequestsPerMinute: rpm,
                DailyRequestQuota: rpd,
                DailyQuotaResetTimezone: resetTimezone,
                DailyQuotaResetTime: resetTime
            )
        );
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LlmModelConfigurationDto>())!;
    }

    private async Task<LlmModelProfileDto> CreateProfileAsync(
        Guid modelConfigurationId,
        string displayName,
        bool enableThinking = false,
        double? temperature = null,
        double? topP = null,
        int? topK = null,
        int contextWindowTokens = 8192,
        int requestTimeoutSeconds = 120
    )
    {
        var response = await _client.PostAsJsonAsync(
            "/api/llm-settings/model-profiles",
            new UpsertLlmModelProfileRequest(
                ModelConfigurationId: modelConfigurationId,
                DisplayName: displayName,
                Summary: $"{displayName} tuning",
                EnableThinking: enableThinking,
                Temperature: temperature,
                TopP: topP,
                TopK: topK,
                ContextWindowTokens: contextWindowTokens,
                RequestTimeoutSeconds: requestTimeoutSeconds
            )
        );
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LlmModelProfileDto>())!;
    }

    private async Task<EffectiveLlmConnectionConfig> GetEffectiveConfigAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope
            .ServiceProvider.GetRequiredService<ILlmSettingsService>()
            .GetEffectiveConfigAsync();
    }

    [TestMethod]
    public async Task Connections_CreateMasksKey_PreservesItOnUpdate_AndRejectsDuplicateNames()
    {
        var created = await CreateConnectionAsync(
            "Gemini",
            "https://generativelanguage.googleapis.com/v1beta/openai",
            LlmProvider.OpenAiCompatible,
            apiKey: "sk-secret-12345"
        );
        Assert.IsTrue(created.HasApiKey);
        Assert.DoesNotContain("secret", created.ApiKeyMasked!);

        var update = await _client.PutAsJsonAsync(
            $"/api/llm-settings/connections/{created.Id}",
            new UpsertLlmConnectionRequest(
                "Gemini cloud",
                LlmProvider.OpenAiCompatible,
                created.BaseUrl,
                RequestsPerMinute: 10
            )
        );
        Assert.AreEqual(HttpStatusCode.OK, update.StatusCode);
        var updated = await update.Content.ReadFromJsonAsync<LlmConnectionDto>();
        Assert.AreEqual("Gemini cloud", updated!.Name);
        Assert.IsTrue(updated.HasApiKey);
        Assert.AreEqual(10, updated.RequestsPerMinute);

        var duplicate = await _client.PostAsJsonAsync(
            "/api/llm-settings/connections",
            new UpsertLlmConnectionRequest("gemini CLOUD", BaseUrl: "http://localhost:11434")
        );
        Assert.AreEqual(HttpStatusCode.Conflict, duplicate.StatusCode);

        var invalid = await _client.PostAsJsonAsync(
            "/api/llm-settings/connections",
            new UpsertLlmConnectionRequest("Bad", BaseUrl: "not a url")
        );
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [TestMethod]
    public async Task Workflows_UseEachAssignedProfilesModelMachineLimitsAndTuning()
    {
        var desktop = await CreateConnectionAsync(
            "Desktop",
            "http://desktop:11434",
            maxConcurrent: 2
        );
        var laptop = await CreateConnectionAsync("Laptop", "http://laptop:11434", dailyQuota: 50);
        var text = await CreateConfigurationAsync(
            "Gemma text",
            desktop.Id,
            "gemma4:12b",
            gpuLayers: 30,
            maxConcurrent: 1,
            rpm: 10,
            rpd: 250,
            resetTimezone: "America/Los_Angeles",
            resetTime: "00:00"
        );
        var vision = await CreateConfigurationAsync(
            "Qwen vision",
            laptop.Id,
            "qwen3-vl:8b",
            gpuLayers: 0
        );
        var refine = await CreateConfigurationAsync(
            "Big refine",
            laptop.Id,
            "gemma4:26b",
            gpuLayers: 24
        );

        Assert.AreNotEqual(Guid.Empty, text.DefaultProfileId);
        Assert.AreEqual(1, text.ProfileCount);

        var visionThinking = await CreateProfileAsync(
            vision.Id,
            "Thinking",
            enableThinking: true,
            contextWindowTokens: 32768,
            requestTimeoutSeconds: 600
        );
        var refineDeep = await CreateProfileAsync(
            refine.Id,
            "Deep Polish",
            enableThinking: true,
            temperature: 1.0,
            topP: 0.95,
            topK: 64,
            contextWindowTokens: 32768,
            requestTimeoutSeconds: 900
        );

        var response = await _client.PutAsJsonAsync(
            "/api/llm-settings/workflows",
            new UpdateLlmWorkflowsRequest(
                TextModelProfileId: text.DefaultProfileId,
                VisionModelProfileId: visionThinking.Id,
                RefineModelProfileId: refineDeep.Id,
                RefineDuringIngestion: true,
                RefineDuringIdle: true
            )
        );
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var settings = await response.Content.ReadFromJsonAsync<LlmSettingsDto>();
        Assert.AreEqual(text.DefaultProfileId, settings!.TextModelProfileId);
        Assert.AreEqual(visionThinking.Id, settings.VisionModelProfileId);
        Assert.AreEqual(refineDeep.Id, settings.RefineModelProfileId);
        Assert.IsTrue(settings.RefineDuringIngestion);
        Assert.IsTrue(settings.RefineDuringIdle);
        Assert.AreEqual("gemma4:12b", settings.TextModel);
        Assert.AreEqual("qwen3-vl:8b", settings.VisionModel);
        Assert.AreEqual("gemma4:26b", settings.RefineModel);

        var effective = await GetEffectiveConfigAsync();
        Assert.IsTrue(effective.RefineDuringIngestion);
        Assert.IsTrue(effective.RefineDuringIdle);
        Assert.IsTrue(effective.HasRefinementModelConfigured);
        Assert.AreEqual("http://desktop:11434", effective.BaseUrl);
        Assert.AreEqual(8192, effective.ContextWindowTokens);
        Assert.IsNull(effective.Temperature);
        Assert.AreEqual(desktop.Id.ToString(), effective.ConnectionKey);
        Assert.AreEqual($"{desktop.Id}:gemma4:12b", effective.ModelKey);
        Assert.AreEqual(1, effective.EffectiveMaxConcurrentRequests);
        Assert.AreEqual(10, effective.ModelRequestsPerMinute);
        Assert.AreEqual(250, effective.ModelDailyRequestQuota);
        Assert.AreEqual("America/Los_Angeles", effective.EffectiveQuotaResetTimezone);
        Assert.AreEqual("00:00", effective.EffectiveQuotaResetTime);
        Assert.AreEqual(30, effective.OllamaGpuLayers);

        var visionConfig = effective.ForVision();
        Assert.AreEqual("http://laptop:11434", visionConfig.BaseUrl);
        Assert.AreEqual("qwen3-vl:8b", visionConfig.VisionModel);
        Assert.AreEqual(50, visionConfig.DailyRequestQuota);
        Assert.AreEqual(32768, visionConfig.ContextWindowTokens);
        Assert.IsTrue(visionConfig.EnableThinking);
        Assert.AreEqual(laptop.Id.ToString(), visionConfig.ConnectionKey);
        Assert.AreEqual($"{laptop.Id}:qwen3-vl:8b", visionConfig.ModelKey);
        Assert.AreEqual(0, visionConfig.OllamaGpuLayers);

        var refineConfig = effective.ForRefinement();
        Assert.AreEqual("http://laptop:11434", refineConfig.BaseUrl);
        Assert.AreEqual("gemma4:26b", refineConfig.TextModel);
        Assert.AreEqual(32768, refineConfig.ContextWindowTokens);
        Assert.AreEqual(1.0, refineConfig.Temperature);
        Assert.AreEqual(24, refineConfig.OllamaGpuLayers);
        Assert.IsTrue(effective.HasDistinctRefinement());

        var configurations = await _client.GetFromJsonAsync<List<LlmModelConfigurationDto>>(
            "/api/llm-settings/model-configurations"
        );
        var refineModelDto = configurations!.Single(c => c.Id == refine.Id);
        Assert.AreEqual(2, refineModelDto.ProfileCount);
        CollectionAssert.AreEqual(
            new[] { LlmWorkflow.Refinement },
            refineModelDto.AssignedWorkflows.ToArray()
        );

        var profiles = await _client.GetFromJsonAsync<List<LlmModelProfileDto>>(
            "/api/llm-settings/model-profiles"
        );
        CollectionAssert.AreEqual(
            new[] { LlmWorkflow.Refinement },
            profiles!.Single(p => p.Id == refineDeep.Id).AssignedWorkflows.ToArray()
        );
    }

    [TestMethod]
    public async Task ModelConfigurations_EnforceUniqueConnectionModel_AndAllowSameModelWithMultipleProfiles()
    {
        var connA = await CreateConnectionAsync("Host A", "http://host-a:11434");
        var connB = await CreateConnectionAsync("Host B", "http://host-b:11434");

        var created = await CreateConfigurationAsync("Gemma on A", connA.Id, "gemma4:12b");
        Assert.AreEqual("gemma4:12b", created.Model);

        // Duplicate model on same connection (case-insensitive) -> 409 Conflict
        var dupResponse = await _client.PostAsJsonAsync(
            "/api/llm-settings/model-configurations",
            new UpsertLlmModelConfigurationRequest("Gemma on A (thinking)", connA.Id, "GEMMA4:12B")
        );
        Assert.AreEqual(HttpStatusCode.Conflict, dupResponse.StatusCode);

        // Same model on a different connection -> 201 Created, gets its own Defaults profile
        var onB = await CreateConfigurationAsync("Gemma on B", connB.Id, "gemma4:12b");
        Assert.AreEqual(connB.Id, onB.ConnectionId);
        Assert.AreNotEqual(created.DefaultProfileId, onB.DefaultProfileId);

        // Create a Thinking profile under created and another Thinking profile under onB (same name allowed across models)
        var thinkingA = await CreateProfileAsync(
            created.Id,
            "Thinking",
            enableThinking: true,
            temperature: 1.0,
            topP: 0.95,
            topK: 64,
            contextWindowTokens: 32768,
            requestTimeoutSeconds: 900
        );
        var thinkingB = await CreateProfileAsync(onB.Id, "Thinking", enableThinking: true);
        Assert.AreNotEqual(thinkingA.Id, thinkingB.Id);

        // Duplicate profile name on the SAME model -> 409 Conflict
        var dupProfile = await _client.PostAsJsonAsync(
            "/api/llm-settings/model-profiles",
            new UpsertLlmModelProfileRequest(created.Id, "  thinking  ")
        );
        Assert.AreEqual(HttpStatusCode.Conflict, dupProfile.StatusCode);

        // Same model can be assigned to both Text and Refinement using different child profiles while sharing ModelKey
        var workflowRes = await _client.PutAsJsonAsync(
            "/api/llm-settings/workflows",
            new UpdateLlmWorkflowsRequest(
                TextModelProfileId: created.DefaultProfileId,
                RefineModelProfileId: thinkingA.Id
            )
        );
        Assert.AreEqual(HttpStatusCode.OK, workflowRes.StatusCode);

        var effective = await GetEffectiveConfigAsync();
        Assert.IsFalse(effective.EnableThinking);
        Assert.IsTrue(effective.ForRefinement().EnableThinking);
        Assert.AreEqual(effective.ModelKey, effective.ForRefinement().ModelKey);
        Assert.IsTrue(effective.HasDistinctRefinement());
    }

    [TestMethod]
    public async Task ModelConfigurations_RateLimitsAndGpuLayersPersistUpdateAndClear_AndRejectInvalidInput()
    {
        var connection = await CreateConnectionAsync("Desktop", "http://desktop:11434");
        var model = await CreateConfigurationAsync(
            "Gemma",
            connection.Id,
            "gemma4:26b-a4b",
            gpuLayers: 24,
            maxConcurrent: 2,
            rpm: 15,
            rpd: 500,
            resetTimezone: "America/Los_Angeles",
            resetTime: "00:00"
        );
        Assert.AreEqual(24, model.OllamaGpuLayers);
        Assert.AreEqual(2, model.MaxConcurrentRequests);
        Assert.AreEqual(15, model.RequestsPerMinute);
        Assert.AreEqual(500, model.DailyRequestQuota);
        Assert.AreEqual("America/Los_Angeles", model.DailyQuotaResetTimezone);
        Assert.AreEqual("00:00", model.DailyQuotaResetTime);

        foreach (int? layers in new int?[] { 20, 0, null })
        {
            var response = await _client.PutAsJsonAsync(
                $"/api/llm-settings/model-configurations/{model.Id}",
                new UpsertLlmModelConfigurationRequest(
                    model.Name,
                    connection.Id,
                    model.Model,
                    OllamaGpuLayers: layers,
                    MaxConcurrentRequests: null,
                    RequestsPerMinute: null,
                    DailyRequestQuota: null,
                    DailyQuotaResetTimezone: null,
                    DailyQuotaResetTime: null
                )
            );
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var updated = await response.Content.ReadFromJsonAsync<LlmModelConfigurationDto>();
            Assert.AreEqual(layers, updated!.OllamaGpuLayers);
            Assert.IsNull(updated.MaxConcurrentRequests);
            Assert.IsNull(updated.RequestsPerMinute);
            Assert.IsNull(updated.DailyRequestQuota);
            Assert.IsNull(updated.DailyQuotaResetTimezone);
            Assert.IsNull(updated.DailyQuotaResetTime);
        }

        foreach (var invalid in new[] { "-1", "1.5" })
        {
            using var payload = new StringContent(
                $$"""{"name":"Bad","connectionId":"{{connection.Id}}","model":"gemma4:e2b","ollamaGpuLayers":{{invalid}}}""",
                System.Text.Encoding.UTF8,
                "application/json"
            );
            var response = await _client.PostAsync(
                "/api/llm-settings/model-configurations",
                payload
            );
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var invalidReset = await _client.PostAsJsonAsync(
            "/api/llm-settings/model-configurations",
            new UpsertLlmModelConfigurationRequest(
                "Bad Reset",
                connection.Id,
                "gemma4:e2b",
                DailyQuotaResetTime: "99:99"
            )
        );
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidReset.StatusCode);
    }

    [TestMethod]
    public async Task Workflows_UnassignedWorkflowStopsUsingLegacyModel()
    {
        var legacy = await _client.PutAsJsonAsync(
            "/api/llm-settings",
            new UpdateLlmSettingsRequest(TextModel: "legacy-text", VisionModel: "legacy-vision")
        );
        Assert.AreEqual(HttpStatusCode.OK, legacy.StatusCode);
        var connection = await CreateConnectionAsync("Desktop", "http://desktop:11434");
        var text = await CreateConfigurationAsync("Text", connection.Id, "gemma4:12b");

        var response = await _client.PutAsJsonAsync(
            "/api/llm-settings/workflows",
            new UpdateLlmWorkflowsRequest(TextModelProfileId: text.DefaultProfileId)
        );
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var effective = await GetEffectiveConfigAsync();
        Assert.AreEqual("gemma4:12b", effective.TextModel);
        Assert.IsNull(effective.VisionModel);
        Assert.IsNull(effective.ForVision().VisionModel);

        var unknownProfile = await _client.PutAsJsonAsync(
            "/api/llm-settings/workflows",
            new UpdateLlmWorkflowsRequest(TextModelProfileId: Guid.NewGuid())
        );
        Assert.AreEqual(HttpStatusCode.BadRequest, unknownProfile.StatusCode);
    }

    [TestMethod]
    public async Task Delete_DefaultsProfileRemainsUndeletable_AndDeletingAssignedProfileUnassignsOnlyItsWorkflows()
    {
        var connection = await CreateConnectionAsync("Desktop", "http://desktop:11434");
        var text = await CreateConfigurationAsync("Text", connection.Id, "gemma4:12b");
        var custom = await CreateProfileAsync(text.Id, "Custom", enableThinking: true);
        await _client.PutAsJsonAsync(
            "/api/llm-settings/workflows",
            new UpdateLlmWorkflowsRequest(
                TextModelProfileId: text.DefaultProfileId,
                RefineModelProfileId: custom.Id,
                RefineDuringIngestion: true,
                RefineDuringIdle: true
            )
        );

        // Undeletable Defaults profile always returns 409 Conflict
        var deleteDefaults = await _client.DeleteAsync(
            $"/api/llm-settings/model-profiles/{text.DefaultProfileId}"
        );
        Assert.AreEqual(HttpStatusCode.Conflict, deleteDefaults.StatusCode);

        // Renaming or moving Defaults profile returns 400 BadRequest
        var renameDefaults = await _client.PutAsJsonAsync(
            $"/api/llm-settings/model-profiles/{text.DefaultProfileId}",
            new UpsertLlmModelProfileRequest(text.Id, "Renamed")
        );
        Assert.AreEqual(HttpStatusCode.BadRequest, renameDefaults.StatusCode);

        // Editing Defaults profile tuning succeeds
        var editDefaults = await _client.PutAsJsonAsync(
            $"/api/llm-settings/model-profiles/{text.DefaultProfileId}",
            new UpsertLlmModelProfileRequest(
                text.Id,
                LlmProfileDefaults.DefaultDisplayName,
                EnableThinking: true,
                RequestTimeoutSeconds: 300
            )
        );
        Assert.AreEqual(HttpStatusCode.OK, editDefaults.StatusCode);

        // Deleting an assigned custom profile succeeds and deselects only its workflow(s)
        var deleteAssignedProfile = await _client.DeleteAsync(
            $"/api/llm-settings/model-profiles/{custom.Id}"
        );
        Assert.AreEqual(HttpStatusCode.NoContent, deleteAssignedProfile.StatusCode);

        var settings = await _client.GetFromJsonAsync<LlmSettingsDto>("/api/llm-settings");
        Assert.IsNotNull(settings);
        Assert.AreEqual(text.DefaultProfileId, settings.TextModelProfileId);
        Assert.AreEqual("gemma4:12b", settings.TextModel);
        Assert.IsNull(settings.RefineModelProfileId);
        Assert.IsNull(settings.RefineModel);

        var effective = await GetEffectiveConfigAsync();
        Assert.IsFalse(effective.HasRefinementModelConfigured);
        Assert.AreEqual("gemma4:12b", effective.TextModel);
    }

    [TestMethod]
    public async Task DeleteModelConfiguration_CascadesProfilesAndDeselectsOnlyItsWorkflows()
    {
        // Seed legacy model strings so we verify unassigning never falls back to legacy values.
        await _client.PutAsJsonAsync(
            "/api/llm-settings",
            new UpdateLlmSettingsRequest(
                TextModel: "legacy-text",
                VisionModel: "legacy-vision",
                RefineModel: "legacy-refine",
                EmbeddingModel: "legacy-embed"
            )
        );

        var connection = await CreateConnectionAsync("Desktop", "http://desktop:11434");
        var modelA = await CreateConfigurationAsync("Model A", connection.Id, "gemma4:12b");
        var modelACustom = await CreateProfileAsync(modelA.Id, "Thinking", enableThinking: true);
        var modelB = await CreateConfigurationAsync("Model B", connection.Id, "qwen2.5vl:7b");

        await _client.PutAsJsonAsync(
            "/api/llm-settings/workflows",
            new UpdateLlmWorkflowsRequest(
                TextModelProfileId: modelA.DefaultProfileId,
                VisionModelProfileId: modelB.DefaultProfileId,
                RefineModelProfileId: modelACustom.Id
            )
        );

        var deleteModel = await _client.DeleteAsync(
            $"/api/llm-settings/model-configurations/{modelA.Id}"
        );
        Assert.AreEqual(HttpStatusCode.NoContent, deleteModel.StatusCode);

        // Model A and both of its profiles are gone; Model B and its Defaults profile remain.
        var remainingConfigs = await _client.GetFromJsonAsync<List<LlmModelConfigurationDto>>(
            "/api/llm-settings/model-configurations"
        );
        Assert.IsNotNull(remainingConfigs);
        Assert.AreEqual(1, remainingConfigs.Count);
        Assert.AreEqual(modelB.Id, remainingConfigs[0].Id);

        var remainingProfiles = await _client.GetFromJsonAsync<List<LlmModelProfileDto>>(
            "/api/llm-settings/model-profiles"
        );
        Assert.IsNotNull(remainingProfiles);
        Assert.AreEqual(1, remainingProfiles.Count);
        Assert.AreEqual(modelB.DefaultProfileId, remainingProfiles[0].Id);

        // Text and Refinement are deselected (and never fall back to legacy strings); Vision stays.
        var settings = await _client.GetFromJsonAsync<LlmSettingsDto>("/api/llm-settings");
        Assert.IsNotNull(settings);
        Assert.IsNull(settings.TextModelProfileId);
        Assert.IsNull(settings.TextModel);
        Assert.IsNull(settings.RefineModelProfileId);
        Assert.IsNull(settings.RefineModel);
        Assert.AreEqual(modelB.DefaultProfileId, settings.VisionModelProfileId);
        Assert.AreEqual("qwen2.5vl:7b", settings.VisionModel);

        var effective = await GetEffectiveConfigAsync();
        Assert.IsNull(effective.TextModel);
        Assert.IsFalse(effective.HasRefinementModelConfigured);
        Assert.AreEqual("qwen2.5vl:7b", effective.ForVision().VisionModel);
    }

    [TestMethod]
    public async Task DeleteConnection_CascadesModelsAndProfilesAndDeselectsOnlyAffectedWorkflows()
    {
        // Seed legacy model strings to verify unassigning clears them completely.
        await _client.PutAsJsonAsync(
            "/api/llm-settings",
            new UpdateLlmSettingsRequest(
                TextModel: "legacy-text",
                VisionModel: "legacy-vision",
                RefineModel: "legacy-refine",
                EmbeddingModel: "legacy-embed"
            )
        );

        var google = await CreateConnectionAsync(
            "Google AI Studio",
            "https://generativelanguage.googleapis.com/v1beta/openai",
            LlmProvider.OpenAiCompatible,
            apiKey: "sk-test-google"
        );
        var ollama = await CreateConnectionAsync("Local Ollama", "http://ollama:11434");

        var geminiFlash = await CreateConfigurationAsync(
            "models/gemini-3.8-flash @ Google AI Studio",
            google.Id,
            "models/gemini-3.8-flash"
        );
        var geminiArgon = await CreateConfigurationAsync(
            "models/gemini-4-argon @ Google AI Studio",
            google.Id,
            "models/gemini-4-argon"
        );
        var geminiArgonDeep = await CreateProfileAsync(
            geminiArgon.Id,
            "Deep Reasoning",
            enableThinking: true
        );
        var geminiEmbed = await CreateConfigurationAsync(
            "models/gemini-4-hydrogen @ Google AI Studio",
            google.Id,
            "models/gemini-4-hydrogen"
        );
        var localVision = await CreateConfigurationAsync(
            "qwen2.5vl:7b @ Local Ollama",
            ollama.Id,
            "qwen2.5vl:7b"
        );

        await _client.PutAsJsonAsync(
            "/api/llm-settings/workflows",
            new UpdateLlmWorkflowsRequest(
                TextModelProfileId: geminiFlash.DefaultProfileId,
                VisionModelProfileId: localVision.DefaultProfileId,
                RefineModelProfileId: geminiArgonDeep.Id,
                EmbeddingModelProfileId: geminiEmbed.DefaultProfileId,
                RefineDuringIngestion: true,
                RefineDuringIdle: true
            )
        );

        // Deleting Google AI Studio succeeds in one step and cascades all models & profiles.
        var deleteConnection = await _client.DeleteAsync(
            $"/api/llm-settings/connections/{google.Id}"
        );
        Assert.AreEqual(HttpStatusCode.NoContent, deleteConnection.StatusCode);

        // Second delete returns 404 NotFound.
        var deleteAgain = await _client.DeleteAsync($"/api/llm-settings/connections/{google.Id}");
        Assert.AreEqual(HttpStatusCode.NotFound, deleteAgain.StatusCode);

        var connections = await _client.GetFromJsonAsync<List<LlmConnectionDto>>(
            "/api/llm-settings/connections"
        );
        Assert.IsNotNull(connections);
        Assert.AreEqual(1, connections.Count);
        Assert.AreEqual(ollama.Id, connections[0].Id);

        var configurations = await _client.GetFromJsonAsync<List<LlmModelConfigurationDto>>(
            "/api/llm-settings/model-configurations"
        );
        Assert.IsNotNull(configurations);
        Assert.AreEqual(1, configurations.Count);
        Assert.AreEqual(localVision.Id, configurations[0].Id);

        var profiles = await _client.GetFromJsonAsync<List<LlmModelProfileDto>>(
            "/api/llm-settings/model-profiles"
        );
        Assert.IsNotNull(profiles);
        Assert.AreEqual(1, profiles.Count);
        Assert.AreEqual(localVision.DefaultProfileId, profiles[0].Id);

        // Text, Refinement, and Embedding workflows are deselected; Local Ollama Vision remains.
        var settings = await _client.GetFromJsonAsync<LlmSettingsDto>("/api/llm-settings");
        Assert.IsNotNull(settings);
        Assert.IsNull(settings.TextModelProfileId);
        Assert.IsNull(settings.TextModel);
        Assert.IsNull(settings.RefineModelProfileId);
        Assert.IsNull(settings.RefineModel);
        Assert.IsNull(settings.EmbeddingModelProfileId);
        Assert.IsNull(settings.EmbeddingModel);
        Assert.AreEqual(localVision.DefaultProfileId, settings.VisionModelProfileId);
        Assert.AreEqual("qwen2.5vl:7b", settings.VisionModel);

        var effective = await GetEffectiveConfigAsync();
        Assert.IsNull(effective.TextModel);
        Assert.IsFalse(effective.HasRefinementModelConfigured);
        Assert.IsNull(effective.ForEmbedding());
        Assert.AreEqual("qwen2.5vl:7b", effective.ForVision().VisionModel);
        Assert.AreEqual("http://ollama:11434", effective.ForVision().BaseUrl);
    }

    [TestMethod]
    public async Task TestConnection_UnknownOrUnreachable()
    {
        Assert.AreEqual(
            HttpStatusCode.NotFound,
            (
                await _client.PostAsync(
                    $"/api/llm-settings/connections/{Guid.NewGuid()}/test",
                    null
                )
            ).StatusCode
        );

        var connection = await CreateConnectionAsync("Nowhere", "http://127.0.0.1:1");
        var response = await _client.PostAsync(
            $"/api/llm-settings/connections/{connection.Id}/test",
            null
        );
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<LlmConnectionTestResultDto>();
        Assert.IsFalse(result!.IsReachable);
    }
}

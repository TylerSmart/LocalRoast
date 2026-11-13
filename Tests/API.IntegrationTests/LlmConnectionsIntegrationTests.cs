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
        int? dailyQuota = null
    )
    {
        var response = await _client.PostAsJsonAsync(
            "/api/llm-settings/connections",
            new UpsertLlmConnectionRequest(
                name,
                provider,
                baseUrl,
                ApiKey: apiKey,
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
        int contextWindow = 8192,
        double? temperature = null,
        int? gpuLayers = null
    )
    {
        var response = await _client.PostAsJsonAsync(
            "/api/llm-settings/model-configurations",
            new UpsertLlmModelConfigurationRequest(
                name,
                connectionId,
                model,
                Temperature: temperature,
                ContextWindowTokens: contextWindow,
                OllamaGpuLayers: gpuLayers
            )
        );
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LlmModelConfigurationDto>())!;
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
    public async Task Workflows_UseEachAssignedConfigurationsMachineAndTuning()
    {
        var desktop = await CreateConnectionAsync("Desktop", "http://desktop:11434");
        var laptop = await CreateConnectionAsync("Laptop", "http://laptop:11434", dailyQuota: 50);
        var text = await CreateConfigurationAsync(
            "Gemma text",
            desktop.Id,
            "gemma4:12b",
            contextWindow: 16384,
            temperature: 1.0,
            gpuLayers: 30
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
            contextWindow: 32768,
            gpuLayers: 24
        );

        var response = await _client.PutAsJsonAsync(
            "/api/llm-settings/workflows",
            new UpdateLlmWorkflowsRequest(text.Id, vision.Id, refine.Id)
        );
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var settings = await response.Content.ReadFromJsonAsync<LlmSettingsDto>();
        Assert.AreEqual(text.Id, settings!.TextModelConfigurationId);
        Assert.AreEqual("gemma4:12b", settings.TextModel);
        Assert.AreEqual("qwen3-vl:8b", settings.VisionModel);
        Assert.AreEqual("gemma4:26b", settings.RefineModel);

        var effective = await GetEffectiveConfigAsync();
        Assert.AreEqual("http://desktop:11434", effective.BaseUrl);
        Assert.AreEqual(16384, effective.ContextWindowTokens);
        Assert.AreEqual(1.0, effective.Temperature);
        Assert.AreEqual(desktop.Id.ToString(), effective.ConnectionKey);
        Assert.AreEqual(30, effective.OllamaGpuLayers);

        var visionConfig = effective.ForVision();
        Assert.AreEqual("http://laptop:11434", visionConfig.BaseUrl);
        Assert.AreEqual("qwen3-vl:8b", visionConfig.VisionModel);
        Assert.AreEqual(50, visionConfig.DailyRequestQuota);
        Assert.AreEqual(laptop.Id.ToString(), visionConfig.ConnectionKey);
        Assert.AreEqual(0, visionConfig.OllamaGpuLayers);

        var refineConfig = effective.ForRefinement();
        Assert.AreEqual("http://laptop:11434", refineConfig.BaseUrl);
        Assert.AreEqual("gemma4:26b", refineConfig.TextModel);
        Assert.AreEqual(32768, refineConfig.ContextWindowTokens);
        Assert.AreEqual(24, refineConfig.OllamaGpuLayers);
        Assert.IsTrue(effective.HasDistinctRefinement());

        var configurations = await _client.GetFromJsonAsync<List<LlmModelConfigurationDto>>(
            "/api/llm-settings/model-configurations"
        );
        CollectionAssert.AreEqual(
            new[] { LlmWorkflow.Refinement },
            configurations!.Single(c => c.Id == refine.Id).AssignedWorkflows.ToArray()
        );
    }

    [TestMethod]
    public async Task ModelConfigurations_GpuLayersPersistUpdateAndClear_AndRejectInvalidInput()
    {
        var connection = await CreateConnectionAsync("Desktop", "http://desktop:11434");
        var model = await CreateConfigurationAsync(
            "Gemma",
            connection.Id,
            "gemma4:26b-a4b",
            gpuLayers: 24
        );
        Assert.AreEqual(24, model.OllamaGpuLayers);

        foreach (int? layers in new int?[] { 20, 0, null })
        {
            var response = await _client.PutAsJsonAsync(
                $"/api/llm-settings/model-configurations/{model.Id}",
                new UpsertLlmModelConfigurationRequest(
                    model.Name,
                    connection.Id,
                    model.Model,
                    OllamaGpuLayers: layers
                )
            );
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(
                layers,
                (
                    await response.Content.ReadFromJsonAsync<LlmModelConfigurationDto>()
                )!.OllamaGpuLayers
            );
            var saved = await _client.GetFromJsonAsync<List<LlmModelConfigurationDto>>(
                "/api/llm-settings/model-configurations"
            );
            Assert.AreEqual(layers, saved!.Single(c => c.Id == model.Id).OllamaGpuLayers);
        }

        foreach (var invalid in new[] { "-1", "1.5" })
        {
            using var payload = new StringContent(
                $$"""{"name":"Bad","connectionId":"{{connection.Id}}","model":"gemma4:26b-a4b","ollamaGpuLayers":{{invalid}}}""",
                System.Text.Encoding.UTF8,
                "application/json"
            );
            var response = await _client.PostAsync(
                "/api/llm-settings/model-configurations",
                payload
            );
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }
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
            new UpdateLlmWorkflowsRequest(TextModelConfigurationId: text.Id)
        );
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var effective = await GetEffectiveConfigAsync();
        Assert.AreEqual("gemma4:12b", effective.TextModel);
        Assert.IsNull(effective.VisionModel);
        Assert.IsNull(effective.ForVision().VisionModel);

        var unknown = await _client.PutAsJsonAsync(
            "/api/llm-settings/workflows",
            new UpdateLlmWorkflowsRequest(TextModelConfigurationId: Guid.NewGuid())
        );
        Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [TestMethod]
    public async Task Delete_InUseItemsReturnConflict_UntilReleased()
    {
        var connection = await CreateConnectionAsync("Desktop", "http://desktop:11434");
        var text = await CreateConfigurationAsync("Text", connection.Id, "gemma4:12b");
        await _client.PutAsJsonAsync(
            "/api/llm-settings/workflows",
            new UpdateLlmWorkflowsRequest(TextModelConfigurationId: text.Id)
        );

        var deleteConnection = await _client.DeleteAsync(
            $"/api/llm-settings/connections/{connection.Id}"
        );
        Assert.AreEqual(HttpStatusCode.Conflict, deleteConnection.StatusCode);
        var deleteAssigned = await _client.DeleteAsync(
            $"/api/llm-settings/model-configurations/{text.Id}"
        );
        Assert.AreEqual(HttpStatusCode.Conflict, deleteAssigned.StatusCode);

        await _client.PutAsJsonAsync(
            "/api/llm-settings/workflows",
            new UpdateLlmWorkflowsRequest()
        );
        Assert.AreEqual(
            HttpStatusCode.NoContent,
            (
                await _client.DeleteAsync($"/api/llm-settings/model-configurations/{text.Id}")
            ).StatusCode
        );
        Assert.AreEqual(
            HttpStatusCode.NoContent,
            (await _client.DeleteAsync($"/api/llm-settings/connections/{connection.Id}")).StatusCode
        );
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

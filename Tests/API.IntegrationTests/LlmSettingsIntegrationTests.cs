using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using App.Core.Authentication;
using App.Core.Ingestion;
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
            AutoPublishHighConfidenceText: true
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
}

using System.Net;
using System.Text;
using App.Core.Ingestion;
using App.Infrastructure.Ingestion;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Infrastructure.UnitTests;

[TestClass]
public sealed class LlmExtractionClientTests
{
    private sealed class MockHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handlerFunc
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            return Task.FromResult(handlerFunc(request));
        }
    }

    [TestMethod]
    public async Task ProbeAndListModelsAsync_Ollama_ReturnsSortedModelNames()
    {
        var jsonResponse = """
            {
                "models": [
                    { "name": "qwen2.5vl:7b" },
                    { "name": "llama3.2:3b" },
                    { "name": "qwen2.5:14b" }
                ]
            }
            """;

        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.AreEqual("/api/tags", req.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json"),
            };
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: null,
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 15,
            AutoPublishHighConfidenceText: false
        );

        var result = await client.ProbeAndListModelsAsync(config);

        Assert.IsTrue(result.IsReachable);
        Assert.AreEqual(3, result.Models.Count);
        Assert.AreEqual("llama3.2:3b", result.Models[0]); // sorted
        Assert.AreEqual("qwen2.5:14b", result.Models[1]);
        Assert.AreEqual("qwen2.5vl:7b", result.Models[2]);
    }

    [TestMethod]
    public async Task ProbeAndListModelsAsync_OpenAiCompatible_AppendsV1AndBearerToken()
    {
        var jsonResponse = """
            {
                "data": [
                    { "id": "meta-llama-3-8b-instruct" },
                    { "id": "gemma-2-9b-it" }
                ]
            }
            """;

        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.AreEqual("/v1/models", req.RequestUri!.AbsolutePath);
            Assert.AreEqual("Bearer test-key-123", req.Headers.Authorization?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json"),
            };
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.OpenAiCompatible,
            BaseUrl: "http://192.168.1.50:1234",
            ApiKey: "test-key-123",
            TextModel: null,
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 15,
            AutoPublishHighConfidenceText: false
        );

        var result = await client.ProbeAndListModelsAsync(config);

        Assert.IsTrue(result.IsReachable);
        Assert.AreEqual(2, result.Models.Count);
        Assert.AreEqual("gemma-2-9b-it", result.Models[0]);
    }

    [TestMethod]
    public async Task ProbeAndListModelsAsync_UnreachableHost_ReturnsIsReachableFalse()
    {
        var handler = new MockHttpMessageHandler(_ =>
            throw new HttpRequestException("Connection refused.")
        );

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://192.168.1.99:11434",
            ApiKey: null,
            TextModel: null,
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 5,
            AutoPublishHighConfidenceText: false
        );

        var result = await client.ProbeAndListModelsAsync(config);

        Assert.IsFalse(result.IsReachable);
        Assert.AreEqual(0, result.Models.Count);
        StringAssert.Contains(result.ErrorMessage, "Connection refused");
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_Ollama_ExtractsStructuredPayload()
    {
        var ollamaResponse = """
            {
                "message": {
                    "role": "assistant",
                    "content": "{\"title\":\"Skillet Rosemary Chicken\",\"description\":\"Crisp chicken with fresh rosemary.\",\"baseServings\":4,\"yieldDisplay\":\"4 portions\",\"prepTimeMinutes\":10,\"cookTimeMinutes\":25,\"totalTimeMinutes\":35,\"difficulty\":\"Medium\",\"sourceName\":\"Chef Daily\",\"sourceAuthor\":\"Elena\",\"confidenceScore\":0.92,\"suggestedTagNames\":[\"Main\",\"One-Pot\"],\"ingredients\":[{\"sectionName\":null,\"quantity\":4,\"maxQuantity\":null,\"unit\":\"whole\",\"canonicalFoodName\":\"chicken thighs\",\"displayName\":\"bone-in chicken thighs\",\"preparationNote\":null,\"isOptional\":false,\"originalRawText\":\"4 bone-in chicken thighs\",\"suggestedAisleName\":\"Meat & Seafood\",\"suggestedIsPantryStaple\":false}],\"steps\":[{\"sectionName\":null,\"instruction\":\"Sear chicken in olive oil for 8 minutes per side.\",\"ingredientIndices\":[0],\"timers\":[{\"durationSeconds\":480,\"label\":\"Sear\"}]}]}"
                }
            }
            """;

        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.AreEqual("/api/chat", req.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ollamaResponse, Encoding.UTF8, "application/json"),
            };
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "qwen2.5:14b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: false
        );

        var context = new LlmExtractionContext(
            SourceContent: "Title: Skillet Rosemary Chicken\nIngredients: 4 bone-in chicken thighs",
            AvailableAisleNames: ["Meat & Seafood", "Produce"],
            AvailableTagNames: ["Main", "One-Pot"]
        );

        var result = await client.ExtractRecipeFromTextAsync(config, context);

        Assert.AreEqual("Skillet Rosemary Chicken", result.Title);
        Assert.AreEqual(4, result.BaseServings);
        Assert.AreEqual(1, result.Ingredients?.Count);
        Assert.AreEqual("chicken thighs", result.Ingredients![0].CanonicalFoodName);
        Assert.AreEqual(1, result.Steps?.Count);
        Assert.AreEqual(480, result.Steps![0].Timers![0].DurationSeconds);
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_Transient503_ThrowsLlmHostUnavailableException()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(
            HttpStatusCode.ServiceUnavailable
        )
        {
            ReasonPhrase = "Service Unavailable",
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "qwen2.5:14b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: false
        );

        var context = new LlmExtractionContext("Some recipe", [], []);

        await Assert.ThrowsAsync<LlmHostUnavailableException>(async () =>
        {
            await client.ExtractRecipeFromTextAsync(config, context);
        });
    }
}

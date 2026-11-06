using System.Net;
using System.Text;
using System.Text.Json;
using App.Core.Exceptions;
using App.Core.Ingestion;
using App.Infrastructure.Ingestion;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Infrastructure.UnitTests;

[TestClass]
public sealed class LlmExtractionClientTests
{
    [TestMethod]
    public async Task LlmRequestGate_SerializesConcurrentRequests()
    {
        var gate = new LlmRequestGate();
        var first = await gate.AcquireAsync();
        var pendingSecond = gate.AcquireAsync();

        Assert.IsFalse(pendingSecond.IsCompleted);

        first.Dispose();
        using var second = await pendingSecond.WaitAsync(TimeSpan.FromSeconds(1));
    }

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

    private sealed class CancellationAwareHttpMessageHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
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
                    "content": "{\"title\":\"Skillet Rosemary Chicken\",\"description\":\"Crisp chicken with fresh rosemary.\",\"baseServings\":4,\"yieldDisplay\":\"4 portions\",\"prepTimeMinutes\":10,\"cookTimeMinutes\":25,\"totalTimeMinutes\":35,\"difficulty\":\"Medium\",\"sourceName\":\"Chef Daily\",\"sourceAuthor\":\"Elena\",\"confidenceScore\":0.92,\"suggestedTagNames\":[\"Main\",\"One-Pot\"],\"ingredients\":[{\"sectionName\":null,\"quantity\":4,\"maxQuantity\":null,\"unit\":\"whole\",\"canonicalFoodName\":\"chicken thighs\",\"displayName\":\"bone-in chicken thighs\",\"preparationNote\":null,\"isOptional\":false,\"originalRawText\":\"4 bone-in chicken thighs\",\"suggestedIsPantryStaple\":false}],\"steps\":[{\"sectionName\":null,\"instruction\":\"Sear chicken in olive oil for 8 minutes per side.\",\"ingredientIndices\":[0],\"timers\":[{\"durationSeconds\":480,\"label\":\"Sear\"}]}]}"
                }
            }
            """;

        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.AreEqual("/api/chat", req.RequestUri!.AbsolutePath);
            var body = req.Content!.ReadAsStringAsync().Result;
            StringAssert.Contains(body, "\"think\":false");
            StringAssert.Contains(body, "\"num_ctx\":16384");
            StringAssert.Contains(body, "\"num_predict\":16384");
            StringAssert.Contains(body, "\"repeat_penalty\":1.1");
            StringAssert.Contains(body, "Never combine repeated ingredients");
            StringAssert.Contains(body, "Do not repeat any part of the ingredient name");
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
            AutoPublishHighConfidenceText: false,
            ContextWindowTokens: 16384
        );

        var context = new LlmExtractionContext(
            SourceContent: "Title: Skillet Rosemary Chicken\nIngredients: 4 bone-in chicken thighs",
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

        var context = new LlmExtractionContext("Some recipe", []);

        await Assert.ThrowsAsync<LlmHostUnavailableException>(async () =>
        {
            await client.ExtractRecipeFromTextAsync(config, context);
        });
    }

    [TestMethod]
    public async Task ExtractRecipeFromImagesAsync_Ollama_SendsImagesArrayAndExtractsBookMetadata()
    {
        var rawRecipeJson = """
            {
                "title": "Grandma's Blueberry Scones",
                "sourceBookTitle": "Family Heirlooms",
                "sourceBookPage": 42,
                "confidenceScore": 0.95,
                "ingredients": [
                    { "displayName": "2 cups flour", "canonicalFoodName": "all-purpose flour", "quantity": 2, "unit": "cup" }
                ],
                "steps": [
                    { "instruction": "Whisk flour and sugar." }
                ]
            }
            """;

        var ollamaResponse = System.Text.Json.JsonSerializer.Serialize(
            new { message = new { role = "assistant", content = rawRecipeJson } }
        );

        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.AreEqual("/api/chat", req.RequestUri!.AbsolutePath);
            var body = req.Content!.ReadAsStringAsync().Result;
            StringAssert.Contains(body, "\"think\":false");
            StringAssert.Contains(body, "\"num_predict\":16384");
            StringAssert.Contains(body, "\"images\":");
            StringAssert.Contains(body, "bW9ja2Jhc2U2NA==");
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
            VisionModel: "qwen2.5vl:7b",
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: false
        );

        var context = new LlmVisionExtractionContext(
            Images: [new LlmVisionImageInput(1, "image/jpeg", "bW9ja2Jhc2U2NA==")],
            SupplementalHintText: "Book: Family Heirlooms",
            AvailableTagNames: ["Breakfast"]
        );

        var result = await client.ExtractRecipeFromImagesAsync(config, context);

        Assert.AreEqual("Grandma's Blueberry Scones", result.Title);
        Assert.AreEqual("Family Heirlooms", result.SourceBookTitle);
        Assert.AreEqual(42, result.SourceBookPage);
        Assert.AreEqual(1, result.Ingredients?.Count);
    }

    [TestMethod]
    public async Task ExtractRecipeFromImagesAsync_OpenAiCompatible_SendsImageUrlBlock()
    {
        var rawRecipeJson = """
            {
                "title": "Rustic Sourdough",
                "sourceBookTitle": "Bread Craft",
                "sourceBookPage": 108,
                "confidenceScore": 0.92,
                "ingredients": [
                    { "displayName": "500g bread flour", "canonicalFoodName": "bread flour", "quantity": 500, "unit": "g" }
                ],
                "steps": [
                    { "instruction": "Mix starter and water." }
                ]
            }
            """;

        var openAiResponse = System.Text.Json.JsonSerializer.Serialize(
            new
            {
                choices = new[]
                {
                    new { message = new { role = "assistant", content = rawRecipeJson } },
                },
            }
        );

        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.AreEqual("/v1/chat/completions", req.RequestUri!.AbsolutePath);
            var body = req.Content!.ReadAsStringAsync().Result;
            StringAssert.Contains(body, "image_url");
            StringAssert.Contains(body, "data:image/jpeg;base64,bW9ja2Jhc2U2NA==");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(openAiResponse, Encoding.UTF8, "application/json"),
            };
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.OpenAiCompatible,
            BaseUrl: "http://192.168.1.50:1234",
            ApiKey: "key-xyz",
            TextModel: "meta-llama-3-8b",
            VisionModel: "meta-llama-3.2-11b-vision",
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: false
        );

        var context = new LlmVisionExtractionContext(
            Images: [new LlmVisionImageInput(1, "image/jpeg", "bW9ja2Jhc2U2NA==")],
            SupplementalHintText: null,
            AvailableTagNames: ["Breads"]
        );

        var result = await client.ExtractRecipeFromImagesAsync(config, context);

        Assert.AreEqual("Rustic Sourdough", result.Title);
        Assert.AreEqual("Bread Craft", result.SourceBookTitle);
        Assert.AreEqual(108, result.SourceBookPage);
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_OpenAiCompatible_SendsJsonSchemaFormatAndExtractsRecipe()
    {
        var rawRecipeJson = """
            {
                "title": "Mango Tango Tacos",
                "description": "Flavorful beef tacos with fresh mango.",
                "baseServings": 4,
                "ingredients": [
                    { "displayName": "1 lb ground beef", "canonicalFoodName": "ground beef", "quantity": 1, "unit": "lb" }
                ],
                "steps": [
                    { "instruction": "Brown beef in a skillet." }
                ]
            }
            """;

        var openAiResponse = System.Text.Json.JsonSerializer.Serialize(
            new
            {
                choices = new[]
                {
                    new { message = new { role = "assistant", content = rawRecipeJson } },
                },
            }
        );

        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.AreEqual("/v1/chat/completions", req.RequestUri!.AbsolutePath);
            var body = req.Content!.ReadAsStringAsync().Result;
            StringAssert.Contains(body, "\"json_schema\"");
            StringAssert.Contains(body, "\"recipe_extraction\"");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(openAiResponse, Encoding.UTF8, "application/json"),
            };
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.OpenAiCompatible,
            BaseUrl: "http://localhost:1234",
            ApiKey: null,
            TextModel: "google/gemma-4-12b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: false
        );

        var context = new LlmExtractionContext(
            SourceContent: "Title: Mango Tango Tacos\nIngredients: 1 lb ground beef",
            AvailableTagNames: ["Mexican"]
        );

        var result = await client.ExtractRecipeFromTextAsync(config, context);

        Assert.AreEqual("Mango Tango Tacos", result.Title);
        Assert.AreEqual(4, result.BaseServings);
        Assert.AreEqual(1, result.Ingredients?.Count);
    }

    [TestMethod]
    public async Task SuggestIngredientSubstitutionsAsync_Ollama_ReturnsParsedSubstitutions()
    {
        var jsonResponse = """
            {
                "model": "qwen2.5:14b",
                "message": {
                    "role": "assistant",
                    "content": "{\"substitutions\":[{\"label\":\"Greek Yogurt\",\"ratioAndAmount\":\"1 cup Greek yogurt + 1/4 cup milk\",\"culinaryNotes\":\"Stir until smooth.\",\"flavorOrTextureImpact\":\"Provides pleasant tang and tender crumb.\"}]}"
                }
            }
            """;

        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.AreEqual("/api/chat", req.RequestUri!.AbsolutePath);
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
            TextModel: "qwen2.5:14b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: false
        );

        var context = new LlmSubstitutionContext(
            RecipeTitle: "Sour Cream Coffee Cake",
            RecipeDescription: "Rich breakfast cake",
            TargetServings: 8,
            TargetIngredientName: "Sour Cream",
            ScaledAmountDisplay: "1 cup",
            PreparationNote: null,
            OtherRecipeIngredients: ["Flour", "Sugar", "Butter"],
            RelevantStepInstructions: ["Beat butter, sugar, and sour cream."],
            DietaryOrPantryNote: null
        );

        var result = await client.SuggestIngredientSubstitutionsAsync(config, context);

        Assert.IsNotNull(result.Substitutions);
        Assert.AreEqual(1, result.Substitutions.Count);
        Assert.AreEqual("Greek Yogurt", result.Substitutions[0].Label);
    }

    [TestMethod]
    [DataRow(LlmProvider.Ollama)]
    [DataRow(LlmProvider.OpenAiCompatible)]
    public async Task AnswerKitchenQuestionAsync_UsesConfiguredProviderAndRecipeContext(
        LlmProvider provider
    )
    {
        var content = "```json\n{\"answer\":\"Cut into thin matchsticks.\"}\n```";
        var response =
            provider == LlmProvider.Ollama
                ? JsonSerializer.Serialize(new { message = new { role = "assistant", content } })
                : JsonSerializer.Serialize(
                    new
                    {
                        choices = new[] { new { message = new { role = "assistant", content } } },
                    }
                );

        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.AreEqual(
                provider == LlmProvider.Ollama ? "/api/chat" : "/v1/chat/completions",
                req.RequestUri!.AbsolutePath
            );
            using var body = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
            var root = body.RootElement;
            var messages = root.GetProperty("messages");
            var system = messages[0].GetProperty("content").GetString();
            var user = messages[1].GetProperty("content").GetString();
            Assert.IsNotNull(system);
            Assert.IsNotNull(user);
            StringAssert.Contains(system, "ingredient list may be incomplete");
            StringAssert.Contains(user, "What does julienne mean?");
            StringAssert.Contains(user, "Peeled carrots");
            StringAssert.Contains(user, "Julienne the carrots.");
            StringAssert.EndsWith(user, "What does julienne mean?");

            if (provider == LlmProvider.Ollama)
            {
                Assert.AreEqual(
                    4096,
                    root.GetProperty("options").GetProperty("num_predict").GetInt32()
                );
                Assert.IsTrue(
                    root.GetProperty("format")
                        .GetProperty("properties")
                        .TryGetProperty("answer", out _)
                );
            }
            else
            {
                Assert.AreEqual("Bearer", req.Headers.Authorization?.Scheme);
                Assert.AreEqual(4096, root.GetProperty("max_tokens").GetInt32());
                Assert.AreEqual(
                    "kitchen_answer",
                    root.GetProperty("response_format")
                        .GetProperty("json_schema")
                        .GetProperty("name")
                        .GetString()
                );
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = CreateKitchenQuestionConfig(provider);
        var context = new LlmKitchenQuestionContext(
            Question: "What does julienne mean?",
            RecipeTitle: "Carrot Salad",
            RecipeDescription: "A crisp salad",
            RecipeIngredients: ["Carrots", "Lemon"],
            IngredientName: "Carrots",
            IngredientPreparationNote: "Peeled carrots",
            StepNumber: 1,
            StepInstruction: "Julienne the carrots."
        );

        var answer = await client.AnswerKitchenQuestionAsync(config, context);

        Assert.AreEqual("Cut into thin matchsticks.", answer);
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("{}")]
    [DataRow("{\"answer\":\"   \"}")]
    [DataRow("{\"answer\":42}")]
    [DataRow("not JSON")]
    public async Task AnswerKitchenQuestionAsync_RejectsEmptyOrMalformedModelOutput(string content)
    {
        var response = JsonSerializer.Serialize(
            new { message = new { role = "assistant", content } }
        );
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response, Encoding.UTF8, "application/json"),
        });
        var client = new LlmExtractionClient(new HttpClient(handler));

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            client.AnswerKitchenQuestionAsync(
                CreateKitchenQuestionConfig(LlmProvider.Ollama),
                new LlmKitchenQuestionContext(
                    "What is aquafaba?",
                    null,
                    null,
                    [],
                    null,
                    null,
                    null,
                    null
                )
            )
        );
    }

    [TestMethod]
    public async Task AnswerKitchenQuestionAsync_ReportsMalformedProviderResponseAndOfflineHost()
    {
        var malformedHandler = new MockHttpMessageHandler(_ => new HttpResponseMessage(
            HttpStatusCode.OK
        )
        {
            Content = new StringContent("not JSON", Encoding.UTF8, "application/json"),
        });
        var offlineHandler = new MockHttpMessageHandler(_ => new HttpResponseMessage(
            HttpStatusCode.ServiceUnavailable
        ));
        var context = new LlmKitchenQuestionContext(
            "What is aquafaba?",
            null,
            null,
            [],
            null,
            null,
            null,
            null
        );
        var config = CreateKitchenQuestionConfig(LlmProvider.Ollama);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            new LlmExtractionClient(new HttpClient(malformedHandler)).AnswerKitchenQuestionAsync(
                config,
                context
            )
        );
        await Assert.ThrowsAsync<LlmHostUnavailableException>(() =>
            new LlmExtractionClient(new HttpClient(offlineHandler)).AnswerKitchenQuestionAsync(
                config,
                context
            )
        );
        await Assert.ThrowsAsync<LlmHostUnavailableException>(() =>
            new LlmExtractionClient(new HttpClient(offlineHandler)).AnswerKitchenQuestionAsync(
                config with
                {
                    TextModel = null,
                },
                context
            )
        );
    }

    [TestMethod]
    [DataRow(LlmProvider.Ollama, "[]")]
    [DataRow(LlmProvider.OpenAiCompatible, "{}")]
    [DataRow(LlmProvider.OpenAiCompatible, "{\"choices\":[]}")]
    [DataRow(LlmProvider.OpenAiCompatible, "{\"choices\":[null]}")]
    [DataRow(LlmProvider.OpenAiCompatible, "{\"choices\":[{\"message\":\"invalid\"}]}")]
    public async Task AnswerKitchenQuestionAsync_RejectsInvalidProviderEnvelopes(
        LlmProvider provider,
        string response
    )
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response, Encoding.UTF8, "application/json"),
        });
        var client = new LlmExtractionClient(new HttpClient(handler));
        var context = new LlmKitchenQuestionContext(
            "What is aquafaba?",
            null,
            null,
            [],
            null,
            null,
            null,
            null
        );

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            client.AnswerKitchenQuestionAsync(CreateKitchenQuestionConfig(provider), context)
        );
        Assert.IsTrue(
            error.Message.Contains("invalid", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("empty answer", StringComparison.OrdinalIgnoreCase)
        );
    }

    [TestMethod]
    public async Task AnswerKitchenQuestionAsync_CancellationAbortsTheProviderRequest()
    {
        using var handler = new CancellationAwareHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var client = new LlmExtractionClient(httpClient);
        var context = new LlmKitchenQuestionContext(
            "What is aquafaba?",
            null,
            null,
            [],
            null,
            null,
            null,
            null
        );

        var pending = client.AnswerKitchenQuestionAsync(
            CreateKitchenQuestionConfig(LlmProvider.Ollama),
            context,
            cancellation.Token
        );
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(() => pending);
    }

    private static EffectiveLlmConnectionConfig CreateKitchenQuestionConfig(LlmProvider provider) =>
        new(
            Provider: provider,
            BaseUrl: "http://localhost:1234",
            ApiKey: provider == LlmProvider.OpenAiCompatible ? "test-key" : null,
            TextModel: "test-model",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: false
        );

    [TestMethod]
    public async Task DraftWeeklyMealPlanAsync_OpenAiCompatible_ReturnsParsedProposals()
    {
        var rawContent =
            "{\"proposals\":[{\"catalogIndex\":1,\"recipeTitle\":\"Lemon Herb Chicken\",\"dayOffset\":0,\"slot\":\"Dinner\",\"isOnDeck\":false,\"targetServings\":4,\"rationale\":\"Fast weeknight meal\"}]}";
        var openAiResponse = System.Text.Json.JsonSerializer.Serialize(
            new
            {
                choices = new[]
                {
                    new { message = new { role = "assistant", content = rawContent } },
                },
            }
        );

        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.AreEqual("/v1/chat/completions", req.RequestUri!.AbsolutePath);
            using var body = System.Text.Json.JsonDocument.Parse(
                req.Content!.ReadAsStringAsync().GetAwaiter().GetResult()
            );
            var messages = body.RootElement.GetProperty("messages");
            var systemPrompt = messages[0].GetProperty("content").GetString()!;
            var userPrompt = messages[1].GetProperty("content").GetString()!;
            StringAssert.Contains(systemPrompt, "0 is the start date");
            StringAssert.Contains(systemPrompt, "do not add extra proposals for leftovers");
            StringAssert.Contains(userPrompt, "Seven-Day Period Starts: 2026-11-02");
            StringAssert.Contains(
                userPrompt,
                "Requested Number of Cooking Occasions (not counting leftovers): 1"
            );
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(openAiResponse, Encoding.UTF8, "application/json"),
            };
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.OpenAiCompatible,
            BaseUrl: "http://localhost:1234",
            ApiKey: "test-key",
            TextModel: "meta-llama/llama-3.1-8b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: false
        );

        var context = new LlmDraftMealPlanContext(
            WeekStartDate: new DateOnly(2026, 11, 2),
            UserPrompt: "Quick dinners",
            RequestedMealCount: 1,
            DefaultSlotName: "Dinner",
            PlaceOnDeck: false,
            ExistingWeekScheduleLines: [],
            CookbookCatalog:
            [
                new LlmCookbookRecipeCandidate(
                    CatalogIndex: 1,
                    RecipeId: Guid.NewGuid(),
                    Title: "Lemon Herb Chicken",
                    BaseServings: 4,
                    TotalTimeMinutes: 30,
                    Difficulty: "Easy",
                    Tags: ["Quick"],
                    KeyIngredients: ["Chicken"],
                    AverageRating: 4.8m,
                    TimesCooked: 3,
                    DaysSinceLastCooked: 10
                ),
            ]
        );

        var result = await client.DraftWeeklyMealPlanAsync(config, context);

        Assert.IsNotNull(result.Proposals);
        Assert.AreEqual(1, result.Proposals.Count);
        Assert.AreEqual(1, result.Proposals[0].CatalogIndex);
        Assert.AreEqual("Lemon Herb Chicken", result.Proposals[0].RecipeTitle);
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_Ollama_EmptyContentWithLengthDoneReason_ThrowsDomainValidationException()
    {
        var ollamaResponse = """
            {
                "done": true,
                "done_reason": "length",
                "message": {
                    "role": "assistant",
                    "content": "",
                    "thinking": "Let me analyze every line of HTML..."
                }
            }
            """;

        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ollamaResponse, Encoding.UTF8, "application/json"),
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "gemma4:12b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: false
        );

        var ex = await Assert.ThrowsAsync<DomainValidationException>(async () =>
        {
            await client.ExtractRecipeFromTextAsync(
                config,
                new LlmExtractionContext("Title: Coconut Chicken Curry", [])
            );
        });

        StringAssert.Contains(ex.Message, "done_reason: length");
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_Ollama_ThinkingOnlyWithoutContent_ThrowsDomainValidationException()
    {
        var ollamaResponse = """
            {
                "done": true,
                "done_reason": "stop",
                "message": {
                    "role": "assistant",
                    "content": "",
                    "thinking": "Only internal reasoning tokens emitted."
                }
            }
            """;

        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ollamaResponse, Encoding.UTF8, "application/json"),
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "gemma4:12b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: false
        );

        var ex = await Assert.ThrowsAsync<DomainValidationException>(async () =>
        {
            await client.ExtractRecipeFromTextAsync(
                config,
                new LlmExtractionContext("Title: Coconut Chicken Curry", [])
            );
        });

        StringAssert.Contains(ex.Message, "internal reasoning tokens");
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_OpenAiCompatible_EmptyContentWithLengthFinishReason_ThrowsDomainValidationException()
    {
        var openAiResponse = """
            {
                "choices": [
                    {
                        "finish_reason": "length",
                        "message": {
                            "role": "assistant",
                            "content": ""
                        }
                    }
                ]
            }
            """;

        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(openAiResponse, Encoding.UTF8, "application/json"),
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.OpenAiCompatible,
            BaseUrl: "http://localhost:1234",
            ApiKey: "test-key",
            TextModel: "gemma-4-12b-it",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: false
        );

        var ex = await Assert.ThrowsAsync<DomainValidationException>(async () =>
        {
            await client.ExtractRecipeFromTextAsync(
                config,
                new LlmExtractionContext("Title: Coconut Chicken Curry", [])
            );
        });

        StringAssert.Contains(ex.Message, "finish_reason: length");
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_IncludesExistingFoodCatalogAndTagReuseCreationRules()
    {
        var ollamaResponse = """
            {
                "message": {
                    "role": "assistant",
                    "content": "{\"title\":\"Garlic Pasta\",\"ingredients\":[{\"displayName\":\"garlic\",\"canonicalFoodName\":\"Garlic\"}],\"steps\":[{\"instruction\":\"Boil pasta.\"}]}"
                }
            }
            """;

        string capturedBody = "";
        var handler = new MockHttpMessageHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().Result;
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
            AutoPublishHighConfidenceText: true
        );

        var context = new LlmExtractionContext(
            SourceContent: "Title: Garlic Pasta",
            AvailableTagNames: ["Dinner", "Italian"],
            AvailableFoodNames: ["Garlic", "Unsalted Butter", "Kosher Salt"]
        );

        await client.ExtractRecipeFromTextAsync(config, context);

        StringAssert.Contains(capturedBody, "Existing Food Catalog");
        StringAssert.Contains(capturedBody, "Unsalted Butter");
        StringAssert.Contains(capturedBody, "Existing Tags: [Dinner, Italian]");
        StringAssert.Contains(capturedBody, "propose new concise Title-Case tags");
        StringAssert.Contains(capturedBody, "suggestedIsPantryStaple");
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_TruncatedStepsJson_RepairsAndExtractsAllCompletedSteps()
    {
        // Simulates an LLM cut off mid-timer in step 2
        var truncatedRawJson =
            "{\"title\":\"ATK Crispy Skin Salmon\",\"description\":\"Perfect pan-seared salmon with crispy skin.\",\"baseServings\":4,\"sourceBookPage\":\"184-185\",\"ingredients\":[{\"displayName\":\"4 salmon fillets\",\"canonicalFoodName\":\"salmon fillet\",\"quantity\":4,\"unit\":\"whole\"}],\"steps\":[{\"instruction\":\"Score salmon skin and pat thoroughly dry.\"},{\"instruction\":\"Place salmon skin-side down in cold skillet and turn heat to medium-high.\",\"timers\":[{\"durationSeconds\":360,\"label\":\"Crisp skin";

        var ollamaResponse = System.Text.Json.JsonSerializer.Serialize(
            new { message = new { role = "assistant", content = truncatedRawJson } }
        );

        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ollamaResponse, Encoding.UTF8, "application/json"),
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

        var context = new LlmExtractionContext("Crispy Skin Salmon recipe", []);
        var result = await client.ExtractRecipeFromTextAsync(config, context);

        Assert.AreEqual("ATK Crispy Skin Salmon", result.Title);
        Assert.AreEqual(184, result.SourceBookPage); // Flexible converter parsed string range "184-185" into 184
        Assert.AreEqual(1, result.Ingredients?.Count);
        Assert.AreEqual(2, result.Steps?.Count); // Repaired step 2 cleanly
        Assert.AreEqual(
            "Place salmon skin-side down in cold skillet and turn heat to medium-high.",
            result.Steps![1].Instruction
        );
        Assert.AreEqual("Crisp skin", result.Steps![1].Timers![0].Label);
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_StringSourceBookPageWithPrefix_ParsesPageNumber()
    {
        var rawRecipeJson = """
            {
                "title": "Cast Iron Steak",
                "sourceBookTitle": "The Cook's Illustrated Meat Book",
                "sourceBookPage": "p. 210",
                "confidenceScore": "0.95",
                "ingredients": [
                    { "displayName": "1 strip steak", "quantity": "1", "unit": "whole" }
                ],
                "steps": [
                    { "instruction": "Sear steak in hot cast iron skillet." }
                ]
            }
            """;

        var ollamaResponse = System.Text.Json.JsonSerializer.Serialize(
            new { message = new { role = "assistant", content = rawRecipeJson } }
        );

        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ollamaResponse, Encoding.UTF8, "application/json"),
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

        var result = await client.ExtractRecipeFromTextAsync(
            config,
            new LlmExtractionContext("Recipe", [])
        );

        Assert.AreEqual("Cast Iron Steak", result.Title);
        Assert.AreEqual(210, result.SourceBookPage);
        Assert.AreEqual(0.95m, result.ConfidenceScore);
        Assert.AreEqual(1m, result.Ingredients![0].Quantity);
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_WithThinkingTags_StripsThinkingAndParsesRecipe()
    {
        var rawRecipeJson = """
            <think>
            Analyzing recipe ingredients and steps...
            Checking for cookware instructions.
            </think>
            ```json
            {
                "title": "Herb Roasted Chicken",
                "ingredients": [
                    { "displayName": "1 whole chicken", "quantity": "1", "unit": "whole" }
                ],
                "steps": [
                    { "instruction": "Roast chicken at 425F until internal temp reaches 165F." }
                ]
            }
            ```
            """;

        var ollamaResponse = System.Text.Json.JsonSerializer.Serialize(
            new { message = new { role = "assistant", content = rawRecipeJson } }
        );

        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ollamaResponse, Encoding.UTF8, "application/json"),
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
            AutoPublishHighConfidenceText: false,
            EnableThinking: true
        );

        var result = await client.ExtractRecipeFromTextAsync(
            config,
            new LlmExtractionContext("Recipe", [])
        );

        Assert.AreEqual("Herb Roasted Chicken", result.Title);
        Assert.AreEqual(1, result.Ingredients!.Count);
        Assert.AreEqual(1, result.Steps!.Count);
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_EnableThinkingTrue_SendsThinkTrueInOllamaPayload()
    {
        string? capturedRequestBody = null;

        var rawRecipeJson = """
            {
                "title": "Simple Pasta",
                "ingredients": [{ "displayName": "Pasta", "quantity": 1, "unit": "package" }],
                "steps": [{ "instruction": "Boil pasta." }]
            }
            """;

        var ollamaResponse = System.Text.Json.JsonSerializer.Serialize(
            new { message = new { role = "assistant", content = rawRecipeJson } }
        );

        var handler = new MockHttpMessageHandler(req =>
        {
            capturedRequestBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
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
            TextModel: "deepseek-r1:14b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: false,
            EnableThinking: true
        );

        await client.ExtractRecipeFromTextAsync(
            config,
            new LlmExtractionContext("Simple Pasta", [])
        );

        Assert.IsNotNull(capturedRequestBody);
        Assert.IsTrue(capturedRequestBody.Contains("\"think\":true"));
    }
}

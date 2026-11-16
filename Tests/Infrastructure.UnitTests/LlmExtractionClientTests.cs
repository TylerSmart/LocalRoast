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

    [TestMethod]
    [DataRow(false, null)]
    [DataRow(true, null)]
    [DataRow(false, 24)]
    [DataRow(false, 0)]
    [DataRow(true, 24)]
    public async Task OllamaGpuLayers_TextAndVision_RespectOverridesAndCpuOnly(
        bool cpuOnly,
        int? gpuLayers
    )
    {
        var calls = 0;
        var handler = new MockHttpMessageHandler(request =>
        {
            using var body = JsonDocument.Parse(
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()
            );
            var options = body.RootElement.GetProperty("options");
            Assert.AreEqual(
                cpuOnly || gpuLayers.HasValue,
                options.TryGetProperty("num_gpu", out var gpu)
            );
            if (cpuOnly || gpuLayers.HasValue)
                Assert.AreEqual(cpuOnly ? 0 : gpuLayers!.Value, gpu.GetInt32());
            Assert.AreEqual(16384, options.GetProperty("num_predict").GetInt32());
            var schema = body.RootElement.GetProperty("format").GetProperty("properties");
            if (calls == 0)
            {
                var ingProps = schema
                    .GetProperty("ingredients")
                    .GetProperty("items")
                    .GetProperty("properties");
                Assert.IsTrue(ingProps.TryGetProperty("altQuantity", out _));
                Assert.IsTrue(ingProps.TryGetProperty("altUnit", out _));
                Assert.IsTrue(schema.TryGetProperty("steps", out _));
                Assert.IsTrue(schema.TryGetProperty("qualityAudit", out _));
            }
            calls++;
            return OllamaReply(ButterIngredientsJson);
        });
        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            LlmProvider.Ollama,
            "http://localhost:11434",
            null,
            "test-text",
            "test-vision",
            "0",
            30,
            false,
            OllamaCpuOnly: cpuOnly,
            OllamaGpuLayers: gpuLayers
        );
        await client.ExtractRecipeFromTextAsync(config, new LlmExtractionContext(TaggedSource));
        await client.TranscribeRecipeImagesAsync(
            config,
            new LlmVisionExtractionContext(
                [new LlmVisionImageInput(1, "image/jpeg", "dGVzdA==")],
                null,
                []
            )
        );
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    [DataRow(LlmProvider.Ollama, false)]
    [DataRow(LlmProvider.Ollama, true)]
    [DataRow(LlmProvider.OpenAiCompatible, false)]
    [DataRow(LlmProvider.OpenAiCompatible, true)]
    public async Task SamplingOverrides_ApplyToEveryRequestOnlyWhenSet(
        LlmProvider provider,
        bool overridden
    )
    {
        var sampling = new List<(double Temperature, double? TopP, int? TopK)>();
        var handler = new MockHttpMessageHandler(request =>
        {
            using var body = JsonDocument.Parse(
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()
            );
            var root = body.RootElement;
            var parameters = provider == LlmProvider.Ollama ? root.GetProperty("options") : root;
            sampling.Add(
                (
                    parameters.GetProperty("temperature").GetDouble(),
                    parameters.TryGetProperty("top_p", out var topP) ? topP.GetDouble() : null,
                    parameters.TryGetProperty("top_k", out var topK) ? topK.GetInt32() : null
                )
            );
            var content = sampling.Count switch
            {
                2 => TranscriptionJson,
                3 =>
                    """{"substitutions":[{"label":"Greek Yogurt","ratioAndAmount":"1 cup","culinaryNotes":"Stir.","flavorOrTextureImpact":"Tangy."}]}""",
                4 => """{"answer":"Cut into thin matchsticks."}""",
                _ => ButterIngredientsJson,
            };
            var message = new { role = "assistant", content };
            var response =
                provider == LlmProvider.Ollama
                    ? JsonSerializer.Serialize(new { message })
                    : JsonSerializer.Serialize(new { choices = new[] { new { message } } });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        });
        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            provider,
            provider == LlmProvider.Ollama ? "http://localhost:11434" : "http://localhost:1234",
            null,
            "test-text",
            "test-vision",
            "5m",
            30,
            false,
            Temperature: overridden ? 1.0 : null,
            TopP: overridden ? 0.95 : null,
            TopK: overridden ? 64 : null
        );

        await client.ExtractRecipeFromTextAsync(config, new LlmExtractionContext(TaggedSource));
        await client.TranscribeRecipeImagesAsync(
            config,
            new LlmVisionExtractionContext(
                [new LlmVisionImageInput(1, "image/jpeg", "dGVzdA==")],
                null,
                []
            )
        );
        await client.SuggestIngredientSubstitutionsAsync(
            config,
            new LlmSubstitutionContext(
                "Sour Cream Coffee Cake",
                null,
                8,
                "Sour Cream",
                "1 cup",
                null,
                [],
                [],
                null
            )
        );
        await client.AnswerKitchenQuestionAsync(
            config,
            new LlmKitchenQuestionContext(
                "What does julienne mean?",
                null,
                null,
                [],
                null,
                null,
                null,
                null
            )
        );

        double[] expectedTemperatures = overridden ? [1, 1, 1, 1] : [0, 0, 0.2, 0.2];
        CollectionAssert.AreEqual(
            expectedTemperatures,
            sampling.Select(s => s.Temperature).ToArray()
        );
        foreach (var (_, topP, topK) in sampling)
        {
            Assert.AreEqual(overridden ? 0.95 : null, topP);
            Assert.AreEqual(overridden ? 64 : null, topK);
        }
    }

    [TestMethod]
    [DataRow(HttpStatusCode.InternalServerError)]
    [DataRow(HttpStatusCode.ServiceUnavailable)]
    public async Task GemmaMemoryFailure_IsActionableInsteadOfAnOfflineHost(HttpStatusCode status)
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(
                """{"error":"Gemma4Assistant requires ctx_other to be set (normal during memory fitting); failed to allocate Vulkan0 buffer"}"""
            ),
        });
        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            LlmProvider.Ollama,
            "http://localhost:11434",
            null,
            "gemma4:26b-a4b",
            null,
            "0",
            30,
            false
        );
        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            client.ExtractRecipeFromTextAsync(config, new LlmExtractionContext(TaggedSource))
        );
        StringAssert.Contains(error.Message, "CPU-only");
        StringAssert.Contains(error.Message, "not a missing application setting");
        StringAssert.Contains(error.Message, "Vulkan0");
    }

    private const string TaggedSource =
        "Butter Toast\n\nIngredients:\n- 2 tablespoons unsalted butter\n\nInstructions:\n1. Melt 2 tablespoons butter in skillet for 2 minutes.";

    private const string ButterIngredientsJson = """
        {"title":"Butter Toast","proposedTags":["Breakfast"],"ingredients":[{"refId":"ing_1","canonicalFoodItem":"unsalted butter","displayName":"unsalted butter","quantity":2,"unit":"tbsp","rawText":"2 tablespoons unsalted butter"}],"steps":[{"stepNumber":1,"templateText":"Melt {{ing_1_a}} in skillet for {{timer_1}}.","ingredientTokens":[{"tokenKey":"ing_1_a","ingredientRefId":"ing_1","stepQuantity":2,"stepUnit":"tbsp"}],"timerTokens":[{"tokenKey":"timer_1","durationSeconds":120,"label":"Melt butter"}],"temperatureTokens":[]}],"qualityAudit":{"isPlausibleRecipe":true,"warnings":[]}}
        """;

    private const string TranscriptionJson = """
        {"title":"Butter Toast","introduction":"","ingredientLines":["2 tablespoons unsalted butter"],"instructionParagraphs":["Melt butter."],"notes":""}
        """;

    private static HttpResponseMessage OllamaReply(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { message = new { role = "assistant", content } }),
                Encoding.UTF8,
                "application/json"
            ),
        };

    private static HttpResponseMessage OpenAiReply(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(
                    new
                    {
                        choices = new[] { new { message = new { role = "assistant", content } } },
                    }
                ),
                Encoding.UTF8,
                "application/json"
            ),
        };

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
                    "content": "{\"title\":\"Skillet Rosemary Chicken\",\"description\":\"Crisp chicken with fresh rosemary.\",\"servings\":4,\"yieldDisplay\":\"4 portions\",\"prepTimeMinutes\":10,\"cookTimeMinutes\":25,\"totalTimeMinutes\":35,\"difficulty\":\"Medium\",\"sourceName\":\"Chef Daily\",\"sourceAuthor\":\"Elena\",\"proposedTags\":[\"Main\",\"One-Pot\"],\"ingredients\":[{\"refId\":\"ing_1\",\"sectionName\":null,\"canonicalFoodItem\":\"chicken thighs\",\"displayName\":\"bone-in chicken thighs\",\"category\":\"MeatAndSeafood\",\"quantity\":4,\"maxQuantity\":null,\"unit\":\"whole\",\"altQuantity\":null,\"altUnit\":null,\"preparationNotes\":null,\"isOptional\":false,\"isRecipeReference\":false,\"referencedRecipeTitle\":null,\"rawText\":\"4 bone-in chicken thighs\"}],\"steps\":[{\"stepNumber\":1,\"sectionName\":null,\"templateText\":\"Sear {{ing_1_a}} in olive oil for {{timer_1}}.\",\"ingredientTokens\":[{\"tokenKey\":\"ing_1_a\",\"ingredientRefId\":\"ing_1\",\"stepQuantity\":4,\"stepUnit\":\"whole\",\"fallbackDisplay\":null}],\"timerTokens\":[{\"tokenKey\":\"timer_1\",\"durationSeconds\":480,\"label\":\"Sear chicken\"}],\"temperatureTokens\":[]}],\"qualityAudit\":{\"isPlausibleRecipe\":true,\"warnings\":[]}}"
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
            StringAssert.Contains(body, "master shopping ingredient list");
            using var request = JsonDocument.Parse(body);
            Assert.AreEqual(
                1,
                request.RootElement.GetProperty("options").GetProperty("repeat_penalty").GetDouble()
            );
            Assert.AreEqual(
                0,
                request.RootElement.GetProperty("options").GetProperty("temperature").GetDouble()
            );
            Assert.AreEqual(
                expected: 16384,
                actual: request.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32()
            );
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
            SourceContent: "Skillet Rosemary Chicken\n\nIngredients:\n- 4 bone-in chicken thighs\n\nInstructions:\n1. Sear chicken in olive oil for 8 minutes per side."
        );

        var result = await client.ExtractRecipeFromTextAsync(config, context);

        Assert.AreEqual("Skillet Rosemary Chicken", result.Title);
        Assert.AreEqual(4, result.Servings);
        Assert.HasCount(1, result.Ingredients!);
        Assert.AreEqual("chicken thighs", result.Ingredients![0].CanonicalFoodItem);
        Assert.AreEqual("MeatAndSeafood", result.Ingredients[0].Category);
        Assert.HasCount(1, result.Steps!);
        Assert.AreEqual(
            "Sear {{ing_1_a}} in olive oil for {{timer_1}}.",
            result.Steps![0].TemplateText
        );
        Assert.AreEqual("ing_1", result.Steps[0].IngredientTokens![0].IngredientRefId);
        Assert.AreEqual(480, result.Steps[0].TimerTokens![0].DurationSeconds);
        Assert.AreEqual("Sear chicken", result.Steps[0].TimerTokens![0].Label);
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

        var context = new LlmExtractionContext(TaggedSource);

        await Assert.ThrowsAsync<LlmHostUnavailableException>(async () =>
        {
            await client.ExtractRecipeFromTextAsync(config, context);
        });
    }

    [TestMethod]
    public async Task TranscribeRecipeImagesAsync_Ollama_SendsImagesArrayAndBuildsCanonicalSource()
    {
        var transcriptionJson = """
            {
                "title": "Grandma's Blueberry Scones",
                "introduction": "A family favorite.",
                "ingredientLines": ["2 cups flour", " ", "1 cup blueberries"],
                "instructionParagraphs": ["Whisk flour and sugar.", "Fold in 1 cup blueberries."],
                "notes": "Freeze up to 1 month."
            }
            """;

        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.AreEqual("/api/chat", req.RequestUri!.AbsolutePath);
            var body = req.Content!.ReadAsStringAsync().Result;
            StringAssert.Contains(body, "\"think\":false");
            StringAssert.Contains(body, "\"num_predict\":16384");
            StringAssert.Contains(body, "\"images\":");
            StringAssert.Contains(body, "bW9ja2Jhc2U2NA==");
            StringAssert.Contains(body, "instructionParagraphs");
            StringAssert.Contains(body, "Book: Family Heirlooms");
            return OllamaReply(transcriptionJson);
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

        var result = await client.TranscribeRecipeImagesAsync(config, context);

        Assert.AreEqual(
            "Grandma's Blueberry Scones\n\nA family favorite.\n\nFreeze up to 1 month.\n\nIngredients:\n- 2 cups flour\n- 1 cup blueberries\n\nInstructions:\n1. Whisk flour and sugar.\n2. Fold in 1 cup blueberries.",
            result
        );
        Assert.IsTrue(
            result.Contains(
                "Instructions:\n1. Whisk flour and sugar.\n2. Fold in 1 cup blueberries."
            )
        );
    }

    [TestMethod]
    public async Task TranscribeRecipeImagesAsync_NoInstructionParagraphs_ReturnsNull()
    {
        var handler = new MockHttpMessageHandler(_ =>
            OllamaReply(
                """{"title":"Scones","introduction":"","ingredientLines":["2 cups flour"],"instructionParagraphs":[],"notes":""}"""
            )
        );
        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            LlmProvider.Ollama,
            "http://localhost:11434",
            null,
            "qwen2.5:14b",
            "qwen2.5vl:7b",
            "5m",
            30,
            false
        );

        var result = await client.TranscribeRecipeImagesAsync(
            config,
            new LlmVisionExtractionContext(
                [new LlmVisionImageInput(1, "image/jpeg", "dGVzdA==")],
                null,
                []
            )
        );

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task TranscribeRecipeImagesAsync_OpenAiCompatible_SendsImageUrlBlock()
    {
        var rawRecipeJson = """
            {
                "title": "Rustic Sourdough",
                "introduction": "",
                "ingredientLines": ["500g bread flour"],
                "instructionParagraphs": ["Mix flour and water."],
                "notes": ""
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

        var result = await client.TranscribeRecipeImagesAsync(config, context);

        Assert.AreEqual(
            "Rustic Sourdough\n\nIngredients:\n- 500g bread flour\n\nInstructions:\n1. Mix flour and water.",
            result
        );
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_OpenAiCompatible_SendsJsonSchemaFormatAndExtractsRecipe()
    {
        var rawRecipeJson = """
            {
                "title": "Mango Tango Tacos",
                "description": "Flavorful beef tacos with fresh mango.",
                "servings": 4,
                "ingredients": [
                    { "refId": "ing_1", "displayName": "ground beef", "canonicalFoodItem": "ground beef", "quantity": 1, "unit": "lb", "rawText": "1 lb ground beef" },
                    {
                        "refId": "ing_2",
                        "canonicalFoodItem": "Foolproof All-Butter Dough",
                        "displayName": "Foolproof All-Butter Dough",
                        "isRecipeReference": true,
                        "referencedRecipeTitle": "Foolproof All-Butter Dough",
                        "quantity": 1,
                        "unit": "recipe",
                        "rawText": "1 recipe Foolproof All-Butter Dough"
                    }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Brown {{ing_1_a}} in a skillet and serve on {{ing_2_a}}.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 1, "stepUnit": "lb" },
                            { "tokenKey": "ing_2_a", "ingredientRefId": "ing_2", "stepQuantity": 1, "stepUnit": "recipe" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
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
            Assert.IsFalse(body.Contains("\"num_gpu\"", StringComparison.Ordinal));
            StringAssert.Contains(body, "\"extracted_recipe\"");
            StringAssert.Contains(body, "\"isRecipeReference\"");
            StringAssert.Contains(body, "\"referencedRecipeTitle\"");
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
            AutoPublishHighConfidenceText: false,
            OllamaCpuOnly: true,
            OllamaGpuLayers: 24
        );

        var context = new LlmExtractionContext(
            SourceContent: "Mango Tango Tacos\n\nIngredients:\n- 1 lb ground beef\n\nInstructions:\n1. Brown beef in a skillet."
        );

        var result = await client.ExtractRecipeFromTextAsync(config, context);

        Assert.AreEqual("Mango Tango Tacos", result.Title);
        Assert.AreEqual(4, result.Servings);
        Assert.HasCount(2, result.Ingredients!);
        Assert.IsTrue(result.Ingredients![1].IsRecipeReference);
        Assert.AreEqual("Foolproof All-Butter Dough", result.Ingredients[1].ReferencedRecipeTitle);
        Assert.AreEqual(
            "Brown {{ing_1_a}} in a skillet and serve on {{ing_2_a}}.",
            result.Steps![0].TemplateText
        );
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
            await client.ExtractRecipeFromTextAsync(config, new LlmExtractionContext(TaggedSource));
        });

        StringAssert.Contains(ex.Message, "done_reason: length");
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_Ollama_ThinkingRunsOutOfTokens_RetriesWithoutThinking()
    {
        var ollamaResponse = """
            {
                "done": true,
                "done_reason": "length",
                "message": { "role": "assistant", "content": "", "thinking": "..." }
            }
            """;
        var thinkFlags = new List<bool>();
        var handler = new MockHttpMessageHandler(request =>
        {
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result);
            thinkFlags.Add(body.RootElement.GetProperty("think").GetBoolean());
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
            TextModel: "gemma4:12b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: false,
            EnableThinking: true
        );

        await Assert.ThrowsAsync<DomainValidationException>(async () =>
        {
            await client.ExtractRecipeFromTextAsync(config, new LlmExtractionContext(TaggedSource));
        });

        CollectionAssert.AreEqual(new[] { true, false }, thinkFlags);
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
            await client.ExtractRecipeFromTextAsync(config, new LlmExtractionContext(TaggedSource));
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
            await client.ExtractRecipeFromTextAsync(config, new LlmExtractionContext(TaggedSource));
        });

        StringAssert.Contains(ex.Message, "finish_reason: length");
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_IncludesSourceHintsWithoutCatalogLists()
    {
        string capturedBody = "";
        var handler = new MockHttpMessageHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().Result;
            return OllamaReply(ButterIngredientsJson);
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
            SourceContent: TaggedSource,
            SourceUrl: "https://example.com/butter-toast",
            TitleHint: "Butter Toast"
        );

        await client.ExtractRecipeFromTextAsync(config, context);

        StringAssert.Contains(capturedBody, "https://example.com/butter-toast");
        StringAssert.Contains(capturedBody, "Title hint: Butter Toast");
        StringAssert.Contains(capturedBody, "qualityAudit");
        StringAssert.Contains(capturedBody, "templateText");
        Assert.IsFalse(capturedBody.Contains("AvailableFoods", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_StringNumericFieldsWithUnits_ParseFlexibly()
    {
        var rawRecipeJson = """
            {
                "title": "Cast Iron Steak",
                "servings": "2",
                "prepTimeMinutes": "5 mins",
                "cookTimeMinutes": "10",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "strip steak", "displayName": "strip steak", "quantity": "1", "unit": "whole", "rawText": "1 strip steak" }
                ],
                "steps": [
                    { "stepNumber": 1, "templateText": "Sear {{ing_1_a}}.", "ingredientTokens": [{ "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 1, "stepUnit": "whole" }], "timerTokens": [], "temperatureTokens": [] }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        var handler = new MockHttpMessageHandler(_ => OllamaReply(rawRecipeJson));

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
            new LlmExtractionContext(TaggedSource)
        );

        Assert.AreEqual("Cast Iron Steak", result.Title);
        Assert.AreEqual(2m, result.Servings);
        Assert.AreEqual(5, result.PrepTimeMinutes);
        Assert.AreEqual(10, result.CookTimeMinutes);
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
                    { "refId": "ing_1", "canonicalFoodItem": "whole chicken", "displayName": "1 whole chicken", "quantity": "1", "unit": "whole", "rawText": "1 whole chicken" }
                ],
                "steps": [
                    { "stepNumber": 1, "templateText": "Roast {{ing_1_a}} for {{timer_1}}.", "ingredientTokens": [{ "tokenKey": "ing_1_a", "ingredientRefId": "ing_1" }], "timerTokens": [{ "tokenKey": "timer_1", "durationSeconds": 120, "label": "Roast chicken" }], "temperatureTokens": [] }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            ```
            """;

        var handler = new MockHttpMessageHandler(_ => OllamaReply(rawRecipeJson));

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
            new LlmExtractionContext(TaggedSource)
        );

        Assert.AreEqual("Herb Roasted Chicken", result.Title);
        Assert.HasCount(1, result.Ingredients!);
        Assert.AreEqual("Roast {{ing_1_a}} for {{timer_1}}.", result.Steps![0].TemplateText);
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_EnableThinkingTrue_SendsThinkTrueInOllamaPayload()
    {
        string? capturedRequestBody = null;

        var rawRecipeJson = """
            {
                "title": "Simple Pasta",
                "ingredients": [
                    { "refId": "ing_1", "displayName": "Pasta", "quantity": 1, "unit": "package" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Boil {{ing_1}} until tender.",
                        "ingredientTokens": [{ "tokenKey": "ing_1", "ingredientRefId": "ing_1" }],
                        "timerTokens": [],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
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

        await client.ExtractRecipeFromTextAsync(config, new LlmExtractionContext(TaggedSource));

        Assert.IsNotNull(capturedRequestBody);
        Assert.IsTrue(capturedRequestBody.Contains("\"think\":true"));
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_ValidationFailure_RetriesMultiTurnAndSucceeds()
    {
        var attempt = 0;
        string? secondTurnPrompt = null;

        var brokenJson = """
            {
                "title": "Simple Soup",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "carrot", "displayName": "carrots", "quantity": 2, "unit": "whole", "rawText": "2 carrots" }
                ],
                "steps": [
                    { "stepNumber": 1, "templateText": "Chop {{missing_token}}.", "ingredientTokens": [], "timerTokens": [], "temperatureTokens": [] }
                ]
            }
            """;

        var repairedJson = """
            {
                "title": "Simple Soup",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "carrot", "displayName": "carrots", "quantity": 2, "unit": "whole", "rawText": "2 carrots" }
                ],
                "steps": [
                    { "stepNumber": 1, "templateText": "Chop {{ing_1_a}}.", "ingredientTokens": [{ "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 2, "stepUnit": "whole" }], "timerTokens": [], "temperatureTokens": [] }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        var handler = new MockHttpMessageHandler(req =>
        {
            attempt++;
            if (attempt == 1)
            {
                return OllamaReply(brokenJson);
            }

            secondTurnPrompt = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return OllamaReply(repairedJson);
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "gemma4:e4b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: false,
            EnableThinking: false
        );

        var result = await client.ExtractRecipeFromTextAsync(
            config,
            new LlmExtractionContext("Simple Soup\n2 carrots\nChop carrots.")
        );

        Assert.AreEqual(2, attempt);
        Assert.IsNotNull(secondTurnPrompt);
        StringAssert.Contains(secondTurnPrompt, "Your JSON failed validation");
        StringAssert.Contains(secondTurnPrompt, "{{missing_token}}");
        Assert.AreEqual("Chop {{ing_1_a}}.", result.Steps![0].TemplateText);
    }

    [TestMethod]
    public async Task DisambiguateEntitiesAsync_SendsCandidatesAndParsesDecisions()
    {
        var unsaltedId = Guid.NewGuid();
        var dinnerTagId = Guid.NewGuid();
        string? capturedBody = null;

        var handler = new MockHttpMessageHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return OllamaReply(
                $$"""{"ingredientDecisions":[{"refId":"ing_1","matchedFoodItemId":"{{unsaltedId}}"}],"tagDecisions":[{"proposedTag":"Supper","matchedTagId":"{{dinnerTagId}}"}]}"""
            );
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

        var disambiguation = await client.DisambiguateEntitiesAsync(
            config,
            new LlmEntityDisambiguationContext(
                RecipeTitle: "Butter Toast",
                AmbiguousIngredients:
                [
                    new AmbiguousIngredientCandidate(
                        "ing_1",
                        "butter",
                        "2 tbsp butter",
                        [new EntityCandidateOption(unsaltedId, "Unsalted Butter", 0.65)]
                    ),
                ],
                AmbiguousTags:
                [
                    new AmbiguousTagCandidate(
                        "Supper",
                        [new EntityCandidateOption(dinnerTagId, "Dinner", 0.60)]
                    ),
                ]
            )
        );

        Assert.IsNotNull(capturedBody);
        StringAssert.Contains(capturedBody, "Unsalted Butter");
        StringAssert.Contains(capturedBody, "Supper");
        Assert.AreEqual(unsaltedId, disambiguation.IngredientDecisions![0].MatchedFoodItemId);
        Assert.AreEqual(dinnerTagId, disambiguation.TagDecisions![0].MatchedTagId);
    }

    [TestMethod]
    public async Task ExtractRecipe_TransientRateLimit_RetriesAndSucceeds()
    {
        var attempt = 0;
        var handler = new MockHttpMessageHandler(req =>
        {
            attempt++;
            if (attempt == 1)
            {
                var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent(
                        """{"error":{"message":"Rate limit exceeded. Try again in 1 second."}}"""
                    ),
                };
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                    TimeSpan.FromSeconds(1)
                );
                return response;
            }

            return OpenAiReply(
                """{"title":"Carrot Soup","ingredients":[{"refId":"ing_1","canonicalFoodItem":"carrot","displayName":"carrots","quantity":2,"unit":"whole","rawText":"2 carrots"}],"steps":[{"stepNumber":1,"templateText":"Chop {{ing_1_a}}.","ingredientTokens":[{"tokenKey":"ing_1_a","ingredientRefId":"ing_1","stepQuantity":2,"stepUnit":"whole"}],"timerTokens":[],"temperatureTokens":[]}],"qualityAudit":{"isPlausibleRecipe":true,"warnings":[]}}"""
            );
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.OpenAiCompatible,
            BaseUrl: "https://api.example.com/v1",
            ApiKey: "test-key",
            TextModel: "gemini-2.5-flash",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: false,
            EnableThinking: false,
            EnableRateLimitSafeguards: true
        );

        var result = await client.ExtractRecipeFromTextAsync(
            config,
            new LlmExtractionContext("Ingredients:\n- 2 carrots\nInstructions:\nChop 2 carrots.")
        );

        Assert.IsNotNull(result);
        Assert.AreEqual("Carrot Soup", result.Title);
        Assert.IsTrue(attempt >= 2, $"Expected at least 2 attempts, but got {attempt}");
    }

    [TestMethod]
    public async Task ExtractRecipe_DailyQuotaExhaustion_ThrowsWithIsQuotaExhaustedTrue_WithoutRetrying()
    {
        var attempt = 0;
        var handler = new MockHttpMessageHandler(req =>
        {
            attempt++;
            return new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent(
                    """{"error":{"message":"Resource has been exhausted (e.g. check quota): GenerateContent requests per day quota exceeded."}}"""
                ),
            };
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.OpenAiCompatible,
            BaseUrl: "https://api.example.com/v1",
            ApiKey: "test-key",
            TextModel: "gemini-2.5-flash",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: false,
            EnableThinking: false,
            EnableRateLimitSafeguards: true
        );

        var ex = await Assert.ThrowsAsync<LlmHostUnavailableException>(() =>
            client.ExtractRecipeFromTextAsync(
                config,
                new LlmExtractionContext(
                    "Ingredients:\n- 2 carrots\nInstructions:\nChop 2 carrots."
                )
            )
        );

        Assert.IsTrue(ex.IsQuotaExhausted);
        Assert.AreEqual(1, attempt, "Hard quota exhaustion should not retry repeatedly.");
    }

    [TestMethod]
    public async Task ExtractRecipe_RateLimitSafeguardsDisabled_ThrowsImmediatelyOn429()
    {
        var attempt = 0;
        var handler = new MockHttpMessageHandler(req =>
        {
            attempt++;
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"error":{"message":"Rate limit exceeded."}}"""),
            };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                TimeSpan.FromSeconds(1)
            );
            return response;
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.OpenAiCompatible,
            BaseUrl: "https://api.example.com/v1",
            ApiKey: "test-key",
            TextModel: "gemini-2.5-flash",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: false,
            EnableThinking: false,
            EnableRateLimitSafeguards: false
        );

        await Assert.ThrowsAsync<LlmHostUnavailableException>(() =>
            client.ExtractRecipeFromTextAsync(
                config,
                new LlmExtractionContext(
                    "Ingredients:\n- 2 carrots\nInstructions:\nChop 2 carrots."
                )
            )
        );

        Assert.AreEqual(
            1,
            attempt,
            "When safeguards are disabled, 429 should not trigger in-place retries."
        );
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_PromptIncludesCuratedTaxonomyAndVariationRules()
    {
        string capturedBody = "";
        var handler = new MockHttpMessageHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().Result;
            return OllamaReply(ButterIngredientsJson);
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "gemma4:e4b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: true
        );

        await client.ExtractRecipeFromTextAsync(
            config,
            new LlmExtractionContext("Ingredients:\n- 1 tbsp butter\nInstructions:\nMelt butter.")
        );

        using var bodyDoc = JsonDocument.Parse(capturedBody);
        var systemPrompt = bodyDoc
            .RootElement.GetProperty("messages")[0]
            .GetProperty("content")
            .GetString();
        Assert.IsNotNull(systemPrompt);
        StringAssert.Contains(systemPrompt, "Boil & Simmer");
        StringAssert.Contains(systemPrompt, "Food Processor");
        StringAssert.Contains(systemPrompt, "Variation Instructions");
        StringAssert.Contains(systemPrompt, "altQuantity: 30");
        StringAssert.Contains(systemPrompt, "microwave power percentages");
        StringAssert.Contains(systemPrompt, "chronological cooking order");
        StringAssert.Contains(systemPrompt, "NEVER multiply seconds by 6 or 10");
        StringAssert.Contains(systemPrompt, "parenthetical hints");
        Assert.DoesNotContain("AvailableFoods", systemPrompt, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_RetriesOnceOnUnreferencedIngredientWarning_AndFallsBackIfRetryFails()
    {
        var unreferencedFirstReply = """
            {
                "title": "Hummus",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "chickpea", "displayName": "chickpeas", "quantity": 2, "unit": "can", "rawText": "2 cans chickpeas" },
                    { "refId": "ing_2", "canonicalFoodItem": "parsley", "displayName": "fresh parsley", "quantity": 1, "unit": "tbsp", "rawText": "1 tablespoon minced fresh parsley" }
                ],
                "steps": [
                    { "stepNumber": 1, "templateText": "Blend {{ing_1_a}} until smooth.", "ingredientTokens": [{ "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 2, "stepUnit": "can" }], "timerTokens": [], "temperatureTokens": [] }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;
        var fixedSecondReply = """
            {
                "title": "Hummus",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "chickpea", "displayName": "chickpeas", "quantity": 2, "unit": "can", "rawText": "2 cans chickpeas" },
                    { "refId": "ing_2", "canonicalFoodItem": "parsley", "displayName": "fresh parsley", "quantity": 1, "unit": "tbsp", "rawText": "1 tablespoon minced fresh parsley" }
                ],
                "steps": [
                    { "stepNumber": 1, "templateText": "Blend {{ing_1_a}} until smooth and top with {{ing_2_a}}.", "ingredientTokens": [{ "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 2, "stepUnit": "can" }, { "tokenKey": "ing_2_a", "ingredientRefId": "ing_2", "stepQuantity": 1, "stepUnit": "tbsp" }], "timerTokens": [], "temperatureTokens": [] }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        var attempt = 0;
        var handler = new MockHttpMessageHandler(_ =>
        {
            attempt++;
            return attempt == 1
                ? OllamaReply(unreferencedFirstReply)
                : OllamaReply(fixedSecondReply);
        });

        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "gemma4:e4b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: true
        );

        var fixedResult = await new LlmExtractionClient(
            new HttpClient(handler)
        ).ExtractRecipeFromTextAsync(config, new LlmExtractionContext("Hummus recipe"));

        Assert.AreEqual(2, attempt);
        StringAssert.Contains(fixedResult.Steps![0].TemplateText, "{{ing_2_a}}");

        var fallbackAttempt = 0;
        var fallbackHandler = new MockHttpMessageHandler(_ =>
        {
            fallbackAttempt++;
            return fallbackAttempt == 1
                ? OllamaReply(unreferencedFirstReply)
                : OllamaReply("{ not valid json");
        });

        var fallbackResult = await new LlmExtractionClient(
            new HttpClient(fallbackHandler)
        ).ExtractRecipeFromTextAsync(config, new LlmExtractionContext("Hummus recipe"));

        Assert.AreEqual("Hummus", fallbackResult.Title);
        Assert.HasCount(2, fallbackResult.Ingredients!);
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_NormalizesCompoundPlusAndRetriesOnTimerMinutesOrDuplicatePlaceholder()
    {
        var attempt = 0;
        string? retryFeedback = null;

        var firstReply = """
            {
                "title": "Gougères",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "olive oil", "displayName": "olive oil", "quantity": 6, "unit": "tbsp", "altQuantity": 1, "altUnit": "tsp", "rawText": "6 tablespoons plus 1 teaspoon olive oil" }
                ],
                "steps": [
                    { "stepNumber": 1, "templateText": "Warm {{ing_1_a}}, pulse {{timer_1}} pulses, then bake {{timer_2}} and drizzle {{ing_1_a}}.", "ingredientTokens": [{ "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 6.3333, "stepUnit": "tbsp" }], "timerTokens": [{ "tokenKey": "timer_1", "durationSeconds": 10, "label": "Pulse" }, { "tokenKey": "timer_2", "durationSeconds": 14, "label": "Bake" }], "temperatureTokens": [] }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        var fixedReply = """
            {
                "title": "Gougères",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "olive oil", "displayName": "olive oil", "quantity": 6.3333, "unit": "tbsp", "rawText": "6 tablespoons plus 1 teaspoon olive oil" }
                ],
                "steps": [
                    { "stepNumber": 1, "templateText": "Warm {{ing_1_a}}, pulse 5 pulses, then bake {{timer_1}} and drizzle {{ing_1_b}}.", "ingredientTokens": [{ "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 6.3333, "stepUnit": "tbsp" }, { "tokenKey": "ing_1_b", "ingredientRefId": "ing_1", "stepQuantity": null, "stepUnit": null }], "timerTokens": [{ "tokenKey": "timer_1", "durationSeconds": 840, "label": "Bake gougères" }], "temperatureTokens": [] }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        var handler = new MockHttpMessageHandler(req =>
        {
            attempt++;
            if (attempt == 1)
            {
                return OllamaReply(firstReply);
            }

            retryFeedback = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return OllamaReply(fixedReply);
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "gemma4:e4b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: true
        );

        var result = await client.ExtractRecipeFromTextAsync(
            config,
            new LlmExtractionContext(
                "Gougères\n6 tablespoons plus 1 teaspoon olive oil\nPulse 5 pulses, bake 14 to 20 minutes."
            )
        );

        Assert.AreEqual(2, attempt);
        Assert.IsNotNull(retryFeedback);
        StringAssert.Contains(retryFeedback, "appears more than once in TemplateText");
        StringAssert.Contains(retryFeedback, "durationSeconds=14");
        StringAssert.Contains(retryFeedback, "food-processor pulses");
        Assert.AreEqual(840, result.Steps![0].TimerTokens![0].DurationSeconds);
        Assert.AreEqual(6.3333m, result.Ingredients![0].Quantity);
        Assert.IsNull(result.Ingredients![0].AltQuantity);
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_RetriesWhenTimerDurationDoesNotMatchSourceText()
    {
        var attempt = 0;
        string? retryFeedback = null;

        var firstReply = """
            {
                "title": "Pizza Dough",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "flour", "displayName": "flour", "quantity": 3, "unit": "cup", "rawText": "3 cups flour" }
                ],
                "steps": [
                    { "stepNumber": 1, "templateText": "Knead {{ing_1_a}} for {{timer_1}}.", "ingredientTokens": [{ "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 3, "stepUnit": "cup" }], "timerTokens": [{ "tokenKey": "timer_1", "durationSeconds": 180, "label": "Knead dough" }], "temperatureTokens": [] }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        var fixedReply = """
            {
                "title": "Pizza Dough",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "flour", "displayName": "flour", "quantity": 3, "unit": "cup", "rawText": "3 cups flour" }
                ],
                "steps": [
                    { "stepNumber": 1, "templateText": "Knead {{ing_1_a}} for {{timer_1}}.", "ingredientTokens": [{ "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 3, "stepUnit": "cup" }], "timerTokens": [{ "tokenKey": "timer_1", "durationSeconds": 30, "label": "Knead dough" }], "temperatureTokens": [] }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        var handler = new MockHttpMessageHandler(req =>
        {
            attempt++;
            if (attempt == 1)
            {
                return OllamaReply(firstReply);
            }

            retryFeedback = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return OllamaReply(fixedReply);
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "gemma4:e4b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: true
        );

        var result = await client.ExtractRecipeFromTextAsync(
            config,
            new LlmExtractionContext("Pizza Dough\n3 cups flour\nProcess for 30 to 40 seconds.")
        );

        Assert.AreEqual(2, attempt);
        Assert.IsNotNull(retryFeedback);
        StringAssert.Contains(retryFeedback, "does not match any timed duration in the source");
        Assert.AreEqual(30, result.Steps![0].TimerTokens![0].DurationSeconds);
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_WhenRetriesExhaust_AppliesSafeStepTokenGlitchRepair()
    {
        var attempt = 0;
        const string glitchyReply = """
            {
                "title": "Buffalo Cauliflower",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "cauliflower", "displayName": "cauliflower", "quantity": 1, "unit": "whole", "rawText": "1 head cauliflower" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Heat oven to at {{temp_1}}, roast {{ing_1_a}}.",
                        "ingredientTokens": [{ "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 1, "stepUnit": "whole" }],
                        "timerTokens": [],
                        "temperatureTokens": [{ "tokenKey": "temp_1", "value": 400, "baseUnit": "F" }]
                    },
                    {
                        "stepNumber": 2,
                        "templateText": "Toss in the sauce.",
                        "ingredientTokens": [{ "tokenKey": "ing_1_b", "ingredientRefId": "ing_1", "stepQuantity": null, "stepUnit": null, "fallbackDisplay": "cauliflower" }],
                        "timerTokens": [],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        var handler = new MockHttpMessageHandler(_ =>
        {
            attempt++;
            return OllamaReply(glitchyReply);
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "gemma4:e4b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: true
        );

        var result = await client.ExtractRecipeFromTextAsync(
            config,
            new LlmExtractionContext(
                "Buffalo Cauliflower\n1 head cauliflower\nHeat oven to 400°F, roast cauliflower. Toss in the sauce."
            )
        );

        Assert.AreEqual(3, attempt);
        Assert.AreEqual(
            "Heat oven to {{temp_1}}, roast {{ing_1_a}}.",
            result.Steps![0].TemplateText
        );
        Assert.IsEmpty(result.Steps![1].IngredientTokens!);
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_QuantityMismatchRetryIncludesPerStepOccurrences_AndHealsUnlistedAndHalfBatch()
    {
        var attempts = new List<string>();
        const string mismatchReply = """
            {
                "title": "Buffalo Cauliflower",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "cauliflower", "displayName": "cauliflower florets", "quantity": 1, "unit": "lb", "rawText": "1 pound cauliflower florets" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Toss {{ing_1_a}}, spray pan with {{unlisted_spray}}.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 1, "stepUnit": "lb" },
                            { "tokenKey": "unlisted_spray", "ingredientRefId": "null", "stepQuantity": null, "stepUnit": null, "fallbackDisplay": "cooking spray" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    },
                    {
                        "stepNumber": 2,
                        "templateText": "Fry half of {{ing_1_b}}.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_b", "ingredientRefId": "ing_1", "stepQuantity": 0.5, "stepUnit": "lb" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        var handler = new MockHttpMessageHandler(req =>
        {
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            attempts.Add(body);
            return OllamaReply(mismatchReply);
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "gemma4:e4b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: true
        );

        var result = await client.ExtractRecipeFromTextAsync(
            config,
            new LlmExtractionContext(
                "Buffalo Cauliflower\n1 pound cauliflower florets\n1. Toss cauliflower, spray pan. 2. Fry half of cauliflower."
            )
        );

        Assert.HasCount(3, attempts);
        StringAssert.Contains(
            attempts[1],
            "Your current step quantities for ing_1: Step 1 ({{ing_1_a}}=1 lb), Step 2 ({{ing_1_b}}=0.5 lb)"
        );
        Assert.IsTrue(RecipeValidator.Validate(result).IsValid);
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_RetriesWithMixtureAndMissingTimerHints()
    {
        var attempts = new List<string>();
        var attemptNum = 0;

        const string invalidReply = """
            {
                "title": "New Englander",
                "ingredients": [
                    { "refId": "ing_4", "canonicalFoodItem": "white wine vinegar", "displayName": "white wine vinegar", "quantity": 1, "unit": "oz", "rawText": "1 oz white wine vinegar" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Whisk in {{ing_4_a}}.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_4_a", "ingredientRefId": "ing_4", "stepQuantity": 1, "stepUnit": "oz", "fallbackDisplay": "vinegar" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    },
                    {
                        "stepNumber": 2,
                        "templateText": "Process until smooth, {{timer_3}}. Add {{ing_4_b}}.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_4_b", "ingredientRefId": "ing_4", "stepQuantity": 2, "stepUnit": "oz", "fallbackDisplay": "shrub syrup" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        const string fixedReply = """
            {
                "title": "New Englander",
                "ingredients": [
                    { "refId": "ing_4", "canonicalFoodItem": "white wine vinegar", "displayName": "white wine vinegar", "quantity": 1, "unit": "oz", "rawText": "1 oz white wine vinegar" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Whisk in {{ing_4_a}}.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_4_a", "ingredientRefId": "ing_4", "stepQuantity": 1, "stepUnit": "oz", "fallbackDisplay": "vinegar" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    },
                    {
                        "stepNumber": 2,
                        "templateText": "Process until smooth, {{timer_3}}. Add shrub syrup.",
                        "ingredientTokens": [],
                        "timerTokens": [
                            { "tokenKey": "timer_3", "durationSeconds": 10, "label": "Process mixture" }
                        ],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        var handler = new MockHttpMessageHandler(req =>
        {
            attemptNum++;
            attempts.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return OllamaReply(attemptNum == 1 ? invalidReply : fixedReply);
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "gemma4:e4b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: true
        );

        var result = await client.ExtractRecipeFromTextAsync(
            config,
            new LlmExtractionContext(
                "New Englander\n- 1 oz white wine vinegar\n1. Whisk in vinegar. 2. Process until smooth, 10 seconds. Add 2 oz shrub syrup."
            )
        );

        Assert.HasCount(2, attempts);
        StringAssert.Contains(attempts[1], "[\\u0027shrub syrup\\u0027]");
        StringAssert.Contains(attempts[1], "combined prepared mixture/syrup/paste/sauce");
        StringAssert.Contains(attempts[1], "Process until smooth, {{timer_3}}.");
        StringAssert.Contains(
            attempts[1],
            "timerTokens (matching the source step durationSeconds)"
        );
        Assert.IsTrue(RecipeValidator.Validate(result).IsValid);
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_RetriesWithEarlyFullStepAndTimerUnitHints()
    {
        var attempts = new List<string>();
        var attemptNum = 0;

        const string invalidReply = """
            {
                "title": "Butter Pie Crust",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "unsalted butter", "displayName": "unsalted butter", "quantity": 20, "unit": "tbsp", "rawText": "20 tablespoons unsalted butter" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Grate {{ing_1_a}} and cut {{ing_1_b}}, wait {{timer_1}}.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 4, "stepUnit": "tbsp" },
                            { "tokenKey": "ing_1_b", "ingredientRefId": "ing_1", "stepQuantity": 16, "stepUnit": "tbsp" }
                        ],
                        "timerTokens": [
                            { "tokenKey": "timer_1", "durationSeconds": 3600, "label": "chill butter" }
                        ],
                        "temperatureTokens": []
                    },
                    {
                        "stepNumber": 2,
                        "templateText": "Add {{ing_1_c}} to processor.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_c", "ingredientRefId": "ing_1", "stepQuantity": 10, "stepUnit": "tbsp" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        const string fixedReply = """
            {
                "title": "Butter Pie Crust",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "unsalted butter", "displayName": "unsalted butter", "quantity": 20, "unit": "tbsp", "rawText": "20 tablespoons unsalted butter" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Grate {{ing_1_a}} and cut {{ing_1_b}}, wait {{timer_1}}.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 4, "stepUnit": "tbsp" },
                            { "tokenKey": "ing_1_b", "ingredientRefId": "ing_1", "stepQuantity": 16, "stepUnit": "tbsp" }
                        ],
                        "timerTokens": [
                            { "tokenKey": "timer_1", "durationSeconds": 60, "label": "chill butter" }
                        ],
                        "temperatureTokens": []
                    },
                    {
                        "stepNumber": 2,
                        "templateText": "Add {{ing_1_c}} to processor.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_c", "ingredientRefId": "ing_1", "stepQuantity": null, "stepUnit": null }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        var handler = new MockHttpMessageHandler(req =>
        {
            attemptNum++;
            attempts.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return OllamaReply(attemptNum == 1 ? invalidReply : fixedReply);
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "gemma4:e4b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: true
        );

        var result = await client.ExtractRecipeFromTextAsync(
            config,
            new LlmExtractionContext(
                "Butter Pie Crust\n- 20 tablespoons unsalted butter\n1. Grate butter and cut remaining butter, wait 1 minute.\n2. Add butter pieces to processor."
            )
        );

        Assert.HasCount(2, attempts);
        StringAssert.Contains(attempts[1], "Source Step 1 durations:");
        StringAssert.Contains(attempts[1], "1 minute");
        StringAssert.Contains(attempts[1], "(= 60s)");
        StringAssert.Contains(attempts[1], "around:");
        StringAssert.Contains(attempts[1], "Steps through Step 1");
        StringAssert.Contains(attempts[1], "ALREADY consume 100% of ing_1");
        StringAssert.Contains(attempts[1], "Step 2 ({{ing_1_c}}=10 tbsp)");
        Assert.IsTrue(RecipeValidator.Validate(result).IsValid);
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_RetriesCollectiveUnreferencedAndSecondMultiplierTimer()
    {
        var attempts = new List<string>();
        var attemptNum = 0;

        const string timerErrorReply = """
            {
                "title": "Herb-Poached Shrimp",
                "ingredients": [
                    { "refId": "ing_1", "sectionName": "SHRIMP", "canonicalFoodItem": "jumbo shrimp", "displayName": "jumbo shrimp", "quantity": 1, "unit": "lb", "rawText": "1 pound jumbo shrimp" },
                    { "refId": "ing_2", "sectionName": "SHRIMP", "canonicalFoodItem": "dry white wine", "displayName": "dry white wine", "quantity": 1, "unit": "cup", "rawText": "1 cup dry white wine" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Drain {{ing_1_a}}, {{timer_1}}, then boil remaining ingredients except the shrimp.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 1, "stepUnit": "lb" }
                        ],
                        "timerTokens": [
                            { "tokenKey": "timer_1", "durationSeconds": 300, "label": "drain" }
                        ],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        const string unreferencedReply = """
            {
                "title": "Herb-Poached Shrimp",
                "ingredients": [
                    { "refId": "ing_1", "sectionName": "SHRIMP", "canonicalFoodItem": "jumbo shrimp", "displayName": "jumbo shrimp", "quantity": 1, "unit": "lb", "rawText": "1 pound jumbo shrimp" },
                    { "refId": "ing_2", "sectionName": "SHRIMP", "canonicalFoodItem": "dry white wine", "displayName": "dry white wine", "quantity": 1, "unit": "cup", "rawText": "1 cup dry white wine" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Drain {{ing_1_a}}, {{timer_1}}, then boil remaining ingredients except the shrimp.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 1, "stepUnit": "lb" }
                        ],
                        "timerTokens": [
                            { "tokenKey": "timer_1", "durationSeconds": 20, "label": "drain" }
                        ],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        const string fixedReply = """
            {
                "title": "Herb-Poached Shrimp",
                "ingredients": [
                    { "refId": "ing_1", "sectionName": "SHRIMP", "canonicalFoodItem": "jumbo shrimp", "displayName": "jumbo shrimp", "quantity": 1, "unit": "lb", "rawText": "1 pound jumbo shrimp" },
                    { "refId": "ing_2", "sectionName": "SHRIMP", "canonicalFoodItem": "dry white wine", "displayName": "dry white wine", "quantity": 1, "unit": "cup", "rawText": "1 cup dry white wine" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Drain {{ing_1_a}}, {{timer_1}}, then boil {{ing_2_a}}.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 1, "stepUnit": "lb" },
                            { "tokenKey": "ing_2_a", "ingredientRefId": "ing_2", "stepQuantity": 1, "stepUnit": "cup" }
                        ],
                        "timerTokens": [
                            { "tokenKey": "timer_1", "durationSeconds": 20, "label": "drain" }
                        ],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        var handler = new MockHttpMessageHandler(req =>
        {
            attemptNum++;
            attempts.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return OllamaReply(
                attemptNum == 1 ? timerErrorReply
                : attemptNum == 2 ? unreferencedReply
                : fixedReply
            );
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "gemma4:e4b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: true
        );

        var result = await client.ExtractRecipeFromTextAsync(
            config,
            new LlmExtractionContext(
                "Herb-Poached Shrimp\n- 1 pound jumbo shrimp\n- 1 cup dry white wine\n1. Drain shrimp, 20 to 30 seconds, then boil remaining ingredients except the shrimp."
            )
        );

        Assert.HasCount(3, attempts);
        StringAssert.Contains(attempts[1], "(= 20..30s)");
        StringAssert.Contains(attempts[1], "accidentally multiplied by 10");
        StringAssert.Contains(attempts[2], "section: \\u0027SHRIMP\\u0027");
        StringAssert.Contains(
            attempts[2],
            "Step 1 uses \\u0027remaining ingredients except the shrimp\\u0027"
        );
        var validation = RecipeValidator.Validate(result);
        Assert.IsTrue(validation.IsValid);
        Assert.IsEmpty(validation.Warnings);
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_RetriesSynthesizedStepWaterLeadingMeasurementMismatch()
    {
        var attempts = new List<string>();
        var attemptNum = 0;

        const string invalidReply = """
            {
                "title": "Customizing Shrimp Cocktail",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "cold water", "displayName": "cold water", "quantity": 2, "unit": "qt", "rawText": "1 quart cold water in Dutch oven. ... combine ice and 1 quart water in bowl" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Pour {{ing_1_a}} and {{ing_1_b}}.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 1, "stepUnit": "qt" },
                            { "tokenKey": "ing_1_b", "ingredientRefId": "ing_1", "stepQuantity": 1, "stepUnit": "qt" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        const string fixedReply = """
            {
                "title": "Customizing Shrimp Cocktail",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "table salt", "displayName": "table salt", "quantity": 3, "unit": "tbsp", "rawText": "3 tablespoons table salt" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Dissolve {{ing_1_a}} in 1 quart cold water and combine with 1 quart water.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 3, "stepUnit": "tbsp" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        var handler = new MockHttpMessageHandler(req =>
        {
            attemptNum++;
            attempts.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return OllamaReply(attemptNum == 1 ? invalidReply : fixedReply);
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "gemma4:e4b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: true
        );

        var result = await client.ExtractRecipeFromTextAsync(
            config,
            new LlmExtractionContext(
                "Customizing Shrimp Cocktail\n- 3 tablespoons table salt\n1. Dissolve salt in 1 quart cold water and combine ice and 1 quart water."
            )
        );

        Assert.HasCount(2, attempts);
        StringAssert.Contains(attempts[1], "REMOVE \\u0027ing_1\\u0027");
        Assert.IsTrue(RecipeValidator.Validate(result).IsValid);
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_AddsCutCountFromHintOnRetry()
    {
        var attempts = new List<string>();
        var attemptNum = 0;

        const string invalidReply = """
            {
                "title": "Bouyourdi",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "tomato", "displayName": "tomato", "quantity": 1, "unit": "whole", "rawText": "1 large tomato, cored" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Slice two 1/4-inch rounds from {{ing_1_a}}. Chop remaining {{ing_1_b}}.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 2, "stepUnit": "whole" },
                            { "tokenKey": "ing_1_b", "ingredientRefId": "ing_1", "stepQuantity": null, "stepUnit": null }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        const string fixedReply = """
            {
                "title": "Bouyourdi",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "tomato", "displayName": "tomato", "quantity": 1, "unit": "whole", "rawText": "1 large tomato, cored" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Slice two 1/4-inch rounds from {{ing_1_a}}. Chop remaining {{ing_1_b}}.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 1, "stepUnit": "whole" },
                            { "tokenKey": "ing_1_b", "ingredientRefId": "ing_1", "stepQuantity": null, "stepUnit": null }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        var handler = new MockHttpMessageHandler(req =>
        {
            attemptNum++;
            attempts.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return OllamaReply(attemptNum == 1 ? invalidReply : fixedReply);
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "gemma4:e4b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: true
        );

        var result = await client.ExtractRecipeFromTextAsync(
            config,
            new LlmExtractionContext(
                "Bouyourdi\n- 1 large tomato, cored\n1. Slice two 1/4-inch rounds from tomato. Chop remaining tomato."
            )
        );

        Assert.HasCount(2, attempts);
        StringAssert.Contains(attempts[1], "cut-piece phrase");
        Assert.IsTrue(RecipeValidator.Validate(result).IsValid);
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_SuggestsPlaceholdersForExceptClauseInCollectiveAttachmentRetry()
    {
        var attempts = new List<string>();
        var attemptNum = 0;

        const string unreferencedReply = """
            {
                "title": "Quiche",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "whole milk", "displayName": "whole milk", "quantity": 1, "unit": "cup", "rawText": "1 cup whole milk" },
                    { "refId": "ing_2", "canonicalFoodItem": "Gruyère cheese", "displayName": "Gruyère cheese", "quantity": 4, "unit": "oz", "rawText": "4 ounces Gruyère cheese" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Whisk the remaining ingredients except the {{ing_2_a}} together. Bake with {{ing_2_b}}.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_2_a", "ingredientRefId": "ing_2", "stepQuantity": null, "stepUnit": null },
                            { "tokenKey": "ing_2_b", "ingredientRefId": "ing_2", "stepQuantity": 4, "stepUnit": "oz" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        const string fixedReply = """
            {
                "title": "Quiche",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "whole milk", "displayName": "whole milk", "quantity": 1, "unit": "cup", "rawText": "1 cup whole milk" },
                    { "refId": "ing_2", "canonicalFoodItem": "Gruyère cheese", "displayName": "Gruyère cheese", "quantity": 4, "unit": "oz", "rawText": "4 ounces Gruyère cheese" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Whisk {{ing_1_a}} together. Bake with {{ing_2_b}}.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 1, "stepUnit": "cup" },
                            { "tokenKey": "ing_2_b", "ingredientRefId": "ing_2", "stepQuantity": 4, "stepUnit": "oz" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        var handler = new MockHttpMessageHandler(req =>
        {
            attemptNum++;
            attempts.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return OllamaReply(attemptNum == 1 ? unreferencedReply : fixedReply);
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "gemma4:e4b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: true
        );

        var result = await client.ExtractRecipeFromTextAsync(
            config,
            new LlmExtractionContext(
                "Quiche\n- 1 cup whole milk\n- 4 ounces Gruyère cheese\n1. Whisk the remaining ingredients except the Gruyère together. Bake with Gruyère."
            )
        );

        Assert.HasCount(2, attempts);
        StringAssert.Contains(attempts[1], "except the {{ing_2_a}}");
        StringAssert.Contains(attempts[1], "{{ing_1_a}} (1 cup whole milk)");
        var validation = RecipeValidator.Validate(result);
        Assert.IsTrue(validation.IsValid);
        Assert.IsEmpty(validation.Warnings);
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_AddsDividedSplitHintInsteadOfNullingLaterAdditions()
    {
        var attempts = new List<string>();
        var attemptNum = 0;

        const string invalidReply = """
            {
                "title": "Scrambled Eggs",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "cilantro", "displayName": "fresh cilantro", "quantity": 0.25, "unit": "cup", "rawText": "1/4 cup chopped fresh cilantro, divided" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Add {{ing_1_a}}.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 0.25, "stepUnit": "cup" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    },
                    {
                        "stepNumber": 2,
                        "templateText": "Top with remaining {{ing_1_b}}.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_b", "ingredientRefId": "ing_1", "stepQuantity": 1, "stepUnit": "tbsp" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        const string fixedReply = """
            {
                "title": "Scrambled Eggs",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "cilantro", "displayName": "fresh cilantro", "quantity": 0.25, "unit": "cup", "rawText": "1/4 cup chopped fresh cilantro, divided" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Add {{ing_1_a}}.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 3, "stepUnit": "tbsp" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    },
                    {
                        "stepNumber": 2,
                        "templateText": "Top with remaining {{ing_1_b}}.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_b", "ingredientRefId": "ing_1", "stepQuantity": 1, "stepUnit": "tbsp" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        var handler = new MockHttpMessageHandler(req =>
        {
            attemptNum++;
            attempts.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return OllamaReply(attemptNum == 1 ? invalidReply : fixedReply);
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "gemma4:e4b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: true
        );

        var result = await client.ExtractRecipeFromTextAsync(
            config,
            new LlmExtractionContext(
                "Scrambled Eggs\n- 1/4 cup chopped fresh cilantro, divided\n1. Add 3 tbsp cilantro.\n2. Top with remaining 1 tbsp cilantro."
            )
        );

        Assert.HasCount(2, attempts);
        StringAssert.Contains(attempts[1], "Do NOT null later additions");
        Assert.IsTrue(RecipeValidator.Validate(result).IsValid);
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_AddsStepWordMatchHintForUnreferencedRollOnAttachmentRetry()
    {
        var attempts = new List<string>();
        var attemptNum = 0;

        const string unreferencedReply = """
            {
                "title": "Egg, Kimchi, and Avocado Sandwiches",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "mayonnaise", "displayName": "mayonnaise", "quantity": 0.25, "unit": "cup", "rawText": "1/4 cup mayonnaise" },
                    { "refId": "ing_2", "canonicalFoodItem": "kaiser roll", "displayName": "kaiser rolls", "quantity": 4, "unit": "whole", "rawText": "4 kaiser rolls, halved and lightly toasted" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Spread {{ing_1_a}} on roll bottoms. Set roll tops over fillings.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 0.25, "stepUnit": "cup" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        const string fixedReply = """
            {
                "title": "Egg, Kimchi, and Avocado Sandwiches",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "mayonnaise", "displayName": "mayonnaise", "quantity": 0.25, "unit": "cup", "rawText": "1/4 cup mayonnaise" },
                    { "refId": "ing_2", "canonicalFoodItem": "kaiser roll", "displayName": "kaiser rolls", "quantity": 4, "unit": "whole", "rawText": "4 kaiser rolls, halved and lightly toasted" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Spread {{ing_1_a}} on {{ing_2_a}} bottoms. Set {{ing_2_b}} tops over fillings.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 0.25, "stepUnit": "cup" },
                            { "tokenKey": "ing_2_a", "ingredientRefId": "ing_2", "stepQuantity": null, "stepUnit": null },
                            { "tokenKey": "ing_2_b", "ingredientRefId": "ing_2", "stepQuantity": null, "stepUnit": null }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        var handler = new MockHttpMessageHandler(req =>
        {
            attemptNum++;
            attempts.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return OllamaReply(attemptNum == 1 ? unreferencedReply : fixedReply);
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "gemma4:e4b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: true
        );

        var result = await client.ExtractRecipeFromTextAsync(
            config,
            new LlmExtractionContext(
                "Egg, Kimchi, and Avocado Sandwiches\n- 1/4 cup mayonnaise\n- 4 kaiser rolls, halved and lightly toasted\n1. Spread mayonnaise on roll bottoms. Set roll tops over fillings."
            )
        );

        Assert.HasCount(2, attempts);
        StringAssert.Contains(attempts[1], "already mentions");
        StringAssert.Contains(attempts[1], "{{ing_2_a}}");
        StringAssert.Contains(attempts[1], "{{ing_2_b}}");
        var validation = RecipeValidator.Validate(result);
        Assert.IsTrue(validation.IsValid);
        Assert.IsEmpty(validation.Warnings);
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_AddsCountedPieceRewriteHintAfterFullConsumed()
    {
        var attempts = new List<string>();
        var attemptNum = 0;

        const string invalidReply = """
            {
                "title": "Gỏi Cuốn",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "shrimp", "displayName": "shrimp", "quantity": 18, "unit": "whole", "rawText": "18 medium-large shrimp" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Boil {{ing_1_a}} and halve lengthwise.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 18, "stepUnit": "whole" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    },
                    {
                        "stepNumber": 2,
                        "templateText": "Arrange {{ing_1_b}} halves on wrapper.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_b", "ingredientRefId": "ing_1", "stepQuantity": 3, "stepUnit": "whole" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        const string fixedReply = """
            {
                "title": "Gỏi Cuốn",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "shrimp", "displayName": "shrimp", "quantity": 18, "unit": "whole", "rawText": "18 medium-large shrimp" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Boil {{ing_1_a}} and halve lengthwise.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 18, "stepUnit": "whole" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    },
                    {
                        "stepNumber": 2,
                        "templateText": "Arrange 3 {{ing_1_b}} halves on wrapper.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_b", "ingredientRefId": "ing_1", "stepQuantity": null, "stepUnit": null }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        var handler = new MockHttpMessageHandler(req =>
        {
            attemptNum++;
            attempts.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return OllamaReply(attemptNum == 1 ? invalidReply : fixedReply);
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "gemma4:e4b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: true
        );

        var result = await client.ExtractRecipeFromTextAsync(
            config,
            new LlmExtractionContext(
                "Gỏi Cuốn\n- 18 medium-large shrimp\n1. Boil shrimp and halve lengthwise.\n2. Arrange 3 shrimp halves on wrapper."
            )
        );

        Assert.HasCount(2, attempts);
        StringAssert.Contains(attempts[1], "rewrite");
        StringAssert.Contains(attempts[1], "3 {{ing_1_b}} halves");
        Assert.IsTrue(RecipeValidator.Validate(result).IsValid);
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_AddsSameClauseUsingHintInsteadOfNullingLaterDividedStep()
    {
        var attempts = new List<string>();
        var attemptNum = 0;

        const string invalidReply = """
            {
                "title": "Breakfast Strata",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "unsalted butter", "displayName": "unsalted butter", "quantity": 5, "unit": "tbsp", "rawText": "5 tablespoons unsalted butter, softened, divided" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Spread {{ing_1_a}} on bread, using {{ing_1_b}}.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 2, "stepUnit": "tbsp" },
                            { "tokenKey": "ing_1_b", "ingredientRefId": "ing_1", "stepQuantity": 2, "stepUnit": "tbsp" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    },
                    {
                        "stepNumber": 2,
                        "templateText": "Melt {{ing_1_c}} in skillet.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_c", "ingredientRefId": "ing_1", "stepQuantity": 2, "stepUnit": "tbsp" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    },
                    {
                        "stepNumber": 3,
                        "templateText": "Grease dish with remaining {{ing_1_d}}.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_d", "ingredientRefId": "ing_1", "stepQuantity": 1, "stepUnit": "tbsp" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        const string fixedReply = """
            {
                "title": "Breakfast Strata",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "unsalted butter", "displayName": "unsalted butter", "quantity": 5, "unit": "tbsp", "rawText": "5 tablespoons unsalted butter, softened, divided" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Spread {{ing_1_a}} on bread, using {{ing_1_b}}.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": null, "stepUnit": null },
                            { "tokenKey": "ing_1_b", "ingredientRefId": "ing_1", "stepQuantity": 2, "stepUnit": "tbsp" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    },
                    {
                        "stepNumber": 2,
                        "templateText": "Melt {{ing_1_c}} in skillet.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_c", "ingredientRefId": "ing_1", "stepQuantity": 2, "stepUnit": "tbsp" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    },
                    {
                        "stepNumber": 3,
                        "templateText": "Grease dish with remaining {{ing_1_d}}.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_d", "ingredientRefId": "ing_1", "stepQuantity": 1, "stepUnit": "tbsp" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        var handler = new MockHttpMessageHandler(req =>
        {
            attemptNum++;
            attempts.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return OllamaReply(attemptNum == 1 ? invalidReply : fixedReply);
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "gemma4:e4b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: true
        );

        var result = await client.ExtractRecipeFromTextAsync(
            config,
            new LlmExtractionContext(
                "Breakfast Strata\n- 5 tablespoons unsalted butter, softened, divided\n1. Spread butter on bread, using 2 tablespoons butter.\n2. Melt 2 tablespoons butter.\n3. Grease dish with remaining 1 tablespoon butter."
            )
        );

        Assert.HasCount(2, attempts);
        StringAssert.Contains(attempts[1], "instead of dropping later divided additions");
        Assert.IsTrue(RecipeValidator.Validate(result).IsValid);
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_AddsSeparatedEggsHintOnRetry()
    {
        var attempts = new List<string>();
        var attemptNum = 0;

        const string invalidReply = """
            {
                "title": "Fluffy Omelets",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "egg", "displayName": "large eggs", "quantity": 4, "unit": "whole", "rawText": "4 large eggs, separated" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Whisk {{ing_1_a}} yolks. Whip {{ing_1_b}} whites.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 4, "stepUnit": "whole" },
                            { "tokenKey": "ing_1_b", "ingredientRefId": "ing_1", "stepQuantity": 4, "stepUnit": "whole" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        const string fixedReply = """
            {
                "title": "Fluffy Omelets",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "egg", "displayName": "large eggs", "quantity": 4, "unit": "whole", "rawText": "4 large eggs, separated" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Whisk {{ing_1_a}} yolks. Whip {{ing_1_b}} whites.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 4, "stepUnit": "whole" },
                            { "tokenKey": "ing_1_b", "ingredientRefId": "ing_1", "stepQuantity": null, "stepUnit": null }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        var handler = new MockHttpMessageHandler(req =>
        {
            attemptNum++;
            attempts.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return OllamaReply(attemptNum == 1 ? invalidReply : fixedReply);
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "gemma4:e4b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: true
        );

        var result = await client.ExtractRecipeFromTextAsync(
            config,
            new LlmExtractionContext(
                "Fluffy Omelets\n- 4 large eggs, separated\n1. Whisk egg yolks. Whip egg whites."
            )
        );

        Assert.HasCount(2, attempts);
        StringAssert.Contains(attempts[1], "separates ONE batch into paired parts");
        Assert.IsTrue(RecipeValidator.Validate(result).IsValid);
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_RunsAttachmentRetryEvenWhenThirdValidationAttemptSucceedsWithWarnings()
    {
        var attempts = new List<string>();
        var attemptNum = 0;

        const string invalidReply = """
            {
                "title": "Quiche",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "egg", "displayName": "eggs", "quantity": 2, "unit": "whole", "rawText": "2 eggs" },
                    { "refId": "ing_2", "canonicalFoodItem": "milk", "displayName": "milk", "quantity": 1, "unit": "cup", "rawText": "1 cup milk" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Whisk {{ing_1_a}} with {{ing_2_a}}.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 5, "stepUnit": "whole" },
                            { "tokenKey": "ing_2_a", "ingredientRefId": "ing_2", "stepQuantity": 1, "stepUnit": "cup" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        const string validButUnreferencedReply = """
            {
                "title": "Quiche",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "egg", "displayName": "eggs", "quantity": 2, "unit": "whole", "rawText": "2 eggs" },
                    { "refId": "ing_2", "canonicalFoodItem": "milk", "displayName": "milk", "quantity": 1, "unit": "cup", "rawText": "1 cup milk" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Whisk {{ing_1_a}} with the remaining ingredients.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 2, "stepUnit": "whole" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        const string attachedReply = """
            {
                "title": "Quiche",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "egg", "displayName": "eggs", "quantity": 2, "unit": "whole", "rawText": "2 eggs" },
                    { "refId": "ing_2", "canonicalFoodItem": "milk", "displayName": "milk", "quantity": 1, "unit": "cup", "rawText": "1 cup milk" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Whisk {{ing_1_a}} with {{ing_2_a}}.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 2, "stepUnit": "whole" },
                            { "tokenKey": "ing_2_a", "ingredientRefId": "ing_2", "stepQuantity": 1, "stepUnit": "cup" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        var handler = new MockHttpMessageHandler(req =>
        {
            attemptNum++;
            attempts.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return OllamaReply(
                attemptNum switch
                {
                    1 or 2 => invalidReply,
                    3 => validButUnreferencedReply,
                    _ => attachedReply,
                }
            );
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "gemma4:e4b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: true
        );

        var result = await client.ExtractRecipeFromTextAsync(
            config,
            new LlmExtractionContext("Quiche\n- 2 eggs\n- 1 cup milk\n1. Whisk eggs with milk.")
        );

        Assert.HasCount(4, attempts);
        var validation = RecipeValidator.Validate(result);
        Assert.IsTrue(validation.IsValid);
        Assert.IsEmpty(validation.Warnings);
    }

    [TestMethod]
    public async Task ExtractRecipeFromTextAsync_AddsSourceMixtureHintOnExcessTokenRetry()
    {
        var attempts = new List<string>();
        var attemptNum = 0;

        const string invalidReply = """
            {
                "title": "French Toast Casserole",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "brown sugar", "displayName": "brown sugar", "quantity": 6, "unit": "tbsp", "rawText": "6 tablespoons packed brown sugar" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Mix {{ing_1_a}} with spices.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 6, "stepUnit": "tbsp" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    },
                    {
                        "stepNumber": 2,
                        "templateText": "Sprinkle {{ing_1_b}} over skillet.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_b", "ingredientRefId": "ing_1", "stepQuantity": 2, "stepUnit": "tbsp" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        const string fixedReply = """
            {
                "title": "French Toast Casserole",
                "ingredients": [
                    { "refId": "ing_1", "canonicalFoodItem": "brown sugar", "displayName": "brown sugar", "quantity": 6, "unit": "tbsp", "rawText": "6 tablespoons packed brown sugar" }
                ],
                "steps": [
                    {
                        "stepNumber": 1,
                        "templateText": "Mix {{ing_1_a}} with spices.",
                        "ingredientTokens": [
                            { "tokenKey": "ing_1_a", "ingredientRefId": "ing_1", "stepQuantity": 6, "stepUnit": "tbsp" }
                        ],
                        "timerTokens": [],
                        "temperatureTokens": []
                    },
                    {
                        "stepNumber": 2,
                        "templateText": "Sprinkle 2 tablespoons brown sugar mixture over skillet.",
                        "ingredientTokens": [],
                        "timerTokens": [],
                        "temperatureTokens": []
                    }
                ],
                "qualityAudit": { "isPlausibleRecipe": true, "warnings": [] }
            }
            """;

        var handler = new MockHttpMessageHandler(req =>
        {
            attemptNum++;
            attempts.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return OllamaReply(attemptNum == 1 ? invalidReply : fixedReply);
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "gemma4:e4b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: true
        );

        var result = await client.ExtractRecipeFromTextAsync(
            config,
            new LlmExtractionContext(
                "French Toast Casserole\n- 6 tablespoons packed brown sugar\n1. Mix brown sugar with spices.\n2. Sprinkle 2 tablespoons brown sugar mixture over skillet."
            )
        );

        Assert.HasCount(2, attempts);
        StringAssert.Contains(attempts[1], "2 tablespoons brown sugar mixture");
        Assert.IsTrue(RecipeValidator.Validate(result).IsValid);
    }
}

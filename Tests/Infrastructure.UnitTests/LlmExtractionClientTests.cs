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
                var secProps = schema
                    .GetProperty("sections")
                    .GetProperty("items")
                    .GetProperty("properties");
                var ingProps = secProps
                    .GetProperty("ingredients")
                    .GetProperty("items")
                    .GetProperty("properties");
                Assert.IsTrue(ingProps.TryGetProperty("alternateMeasurements", out _));
                Assert.IsTrue(ingProps.TryGetProperty("replacements", out _));
                Assert.IsTrue(secProps.TryGetProperty("instructions", out _));
                Assert.IsTrue(schema.TryGetProperty("timing", out _));
                Assert.IsTrue(schema.TryGetProperty("tags", out _));
            }
            calls++;
            return OllamaReply(Stage1ButterJson);
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
        await client.ExecuteStructuredStageAsync<Stage1RecipeDto>(
            config,
            "stage1_extract_recipe",
            [new("system", "Extract recipe."), new("user", TaggedSource)],
            MultiStageSchemas.Stage1ExtractRecipeSchema
        );
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
                _ => Stage1ButterJson,
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

        await client.ExecuteStructuredStageAsync<Stage1RecipeDto>(
            config,
            "stage1_extract_recipe",
            [new("system", "Extract recipe."), new("user", TaggedSource)],
            MultiStageSchemas.Stage1ExtractRecipeSchema
        );
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
            client.ExecuteStructuredStageAsync<Stage1RecipeDto>(
                config,
                "stage1_extract_recipe",
                [new("system", "Extract recipe."), new("user", TaggedSource)],
                MultiStageSchemas.Stage1ExtractRecipeSchema
            )
        );
        StringAssert.Contains(error.Message, "CPU-only");
        StringAssert.Contains(error.Message, "not a missing application setting");
        StringAssert.Contains(error.Message, "Vulkan0");
    }

    private const string TaggedSource =
        "Butter Toast\n\nIngredients:\n- 2 tablespoons unsalted butter\n\nInstructions:\n1. Melt 2 tablespoons butter in skillet for 2 minutes.";

    private const string Stage1ButterJson = """
        {"title":"Butter Toast","description":null,"servings":2,"yield":null,"sections":[{"name":null,"ingredients":[{"name":"unsalted butter","quantity":2,"maxQuantity":null,"unit":"tbsp","prepNote":null,"isOptional":false,"replacements":[],"alternateMeasurements":[]}],"instructions":["Melt 2 tablespoons butter in skillet for 2 minutes."]}],"timing":{"prep":5,"cook":2,"total":7},"tags":["Breakfast"]}
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
    public async Task ExecuteStructuredStageAsync_Ollama_ExtractsStructuredPayload()
    {
        var stage1Json = """
            {
                "title": "Skillet Rosemary Chicken",
                "description": "Crisp chicken with fresh rosemary.",
                "servings": 4,
                "yield": "4 portions",
                "sections": [
                    {
                        "name": null,
                        "ingredients": [
                            {
                                "name": "bone-in chicken thighs",
                                "quantity": 4,
                                "maxQuantity": null,
                                "unit": "whole",
                                "prepNote": null,
                                "isOptional": false,
                                "replacements": [],
                                "alternateMeasurements": []
                            }
                        ],
                        "instructions": [
                            "Sear 4 chicken thighs in olive oil for 8 minutes per side."
                        ]
                    }
                ],
                "timing": { "prep": 10, "cook": 25, "total": 35 },
                "tags": ["Main", "One-Pot"]
            }
            """;

        var ollamaResponse = JsonSerializer.Serialize(
            new { message = new { role = "assistant", content = stage1Json } }
        );

        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.AreEqual("/api/chat", req.RequestUri!.AbsolutePath);
            var body = req.Content!.ReadAsStringAsync().Result;
            StringAssert.Contains(body, "\"think\":false");
            StringAssert.Contains(body, "\"num_ctx\":16384");
            StringAssert.Contains(body, "\"num_predict\":16384");
            StringAssert.Contains(body, "Extract recipe JSON");
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

        var result = await client.ExecuteStructuredStageAsync<Stage1RecipeDto>(
            config,
            "stage1_extract_recipe",
            [
                new("system", "Extract recipe JSON."),
                new(
                    "user",
                    "Skillet Rosemary Chicken\n\nIngredients:\n- 4 bone-in chicken thighs\n\nInstructions:\n1. Sear chicken in olive oil for 8 minutes per side."
                ),
            ],
            MultiStageSchemas.Stage1ExtractRecipeSchema
        );

        Assert.IsNull(result.Error);
        Assert.AreEqual("qwen2.5:14b", result.Model);
        Assert.IsNotNull(result.Parsed);
        Assert.AreEqual("Skillet Rosemary Chicken", result.Parsed.Title);
        Assert.AreEqual(4, result.Parsed.Servings);
        Assert.HasCount(1, result.Parsed.Sections);
        Assert.HasCount(1, result.Parsed.Sections[0].Ingredients);
        Assert.AreEqual("bone-in chicken thighs", result.Parsed.Sections[0].Ingredients[0].Name);
        Assert.HasCount(1, result.Parsed.Sections[0].Instructions);
        Assert.AreEqual(35, result.Parsed.Timing?.Total);
    }

    [TestMethod]
    public async Task ExecuteStructuredStageAsync_Transient503_ThrowsLlmHostUnavailableException()
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

        await Assert.ThrowsAsync<LlmHostUnavailableException>(async () =>
        {
            await client.ExecuteStructuredStageAsync<Stage1RecipeDto>(
                config,
                "stage1_extract_recipe",
                [new("system", "Extract recipe."), new("user", TaggedSource)],
                MultiStageSchemas.Stage1ExtractRecipeSchema
            );
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

        Assert.IsNotNull(result);
        Assert.AreEqual(
            "Grandma's Blueberry Scones\n\nA family favorite.\n\nFreeze up to 1 month.\n\nIngredients:\n- 2 cups flour\n- 1 cup blueberries\n\nInstructions:\n1. Whisk flour and sugar.\n2. Fold in 1 cup blueberries.",
            result
        );
        Assert.Contains(
            "Instructions:\n1. Whisk flour and sugar.\n2. Fold in 1 cup blueberries.",
            result
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
    public async Task ExecuteStructuredStageAsync_OpenAiCompatible_SendsJsonSchemaFormatAndExtractsResult()
    {
        var rawRecipeJson = """
            {
                "title": "Mango Tango Tacos",
                "description": "Flavorful beef tacos with fresh mango.",
                "servings": 4,
                "yield": null,
                "sections": [
                    {
                        "name": null,
                        "ingredients": [
                            {
                                "name": "ground beef",
                                "quantity": 1,
                                "maxQuantity": null,
                                "unit": "lb",
                                "prepNote": null,
                                "isOptional": false,
                                "replacements": [
                                    { "name": "ground turkey", "quantity": 1, "unit": "lb", "prepNote": null }
                                ],
                                "alternateMeasurements": []
                            }
                        ],
                        "instructions": ["Brown 1 lb ground beef in a skillet."]
                    }
                ],
                "timing": { "prep": 10, "cook": 15, "total": 25 },
                "tags": ["Dinner", "Mexican"]
            }
            """;

        var openAiResponse = JsonSerializer.Serialize(
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
            StringAssert.Contains(body, "\"stage1_extract_recipe\"");
            StringAssert.Contains(body, "\"replacements\"");
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

        var result = await client.ExecuteStructuredStageAsync<Stage1RecipeDto>(
            config,
            "stage1_extract_recipe",
            [
                new("system", "Extract recipe JSON."),
                new(
                    "user",
                    "Mango Tango Tacos\n\nIngredients:\n- 1 lb ground beef (or 1 lb ground turkey)\n\nInstructions:\n1. Brown 1 lb ground beef in a skillet."
                ),
            ],
            MultiStageSchemas.Stage1ExtractRecipeSchema
        );

        Assert.IsNull(result.Error);
        Assert.IsNotNull(result.Parsed);
        Assert.AreEqual("Mango Tango Tacos", result.Parsed.Title);
        Assert.AreEqual(4, result.Parsed.Servings);
        Assert.HasCount(1, result.Parsed.Sections[0].Ingredients);
        Assert.IsNotNull(result.Parsed.Sections[0].Ingredients[0].Replacements);
        Assert.HasCount(1, result.Parsed.Sections[0].Ingredients[0].Replacements!);
        Assert.AreEqual(
            "ground turkey",
            result.Parsed.Sections[0].Ingredients[0].Replacements![0].Name
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
    public async Task ExecuteStructuredStageAsync_Ollama_EmptyContentWithLengthDoneReason_ReturnsLengthDoneReasonResult()
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

        var result = await client.ExecuteStructuredStageAsync<Stage1RecipeDto>(
            config,
            "stage1_extract_recipe",
            [new("system", "Extract recipe."), new("user", TaggedSource)],
            MultiStageSchemas.Stage1ExtractRecipeSchema
        );

        Assert.IsNull(result.Parsed);
        Assert.AreEqual("length", result.DoneReason);
        Assert.IsNotNull(result.Error);
        StringAssert.Contains(result.Error, "done_reason: length");
    }

    [TestMethod]
    public async Task ExecuteStructuredStageAsync_Ollama_ThinkingRunsOutOfTokens_RetriesWithoutThinkingAndSucceeds()
    {
        var truncatedResponse = """
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
            var think = body.RootElement.GetProperty("think").GetBoolean();
            thinkFlags.Add(think);
            return think
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        truncatedResponse,
                        Encoding.UTF8,
                        "application/json"
                    ),
                }
                : OllamaReply(Stage1ButterJson);
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

        var result = await client.ExecuteStructuredStageAsync<Stage1RecipeDto>(
            config,
            "stage1_extract_recipe",
            [new("system", "Extract recipe."), new("user", TaggedSource)],
            MultiStageSchemas.Stage1ExtractRecipeSchema
        );

        CollectionAssert.AreEqual(new[] { true, false }, thinkFlags);
        Assert.IsNull(result.Error);
        Assert.IsNotNull(result.Parsed);
        Assert.AreEqual("Butter Toast", result.Parsed.Title);
    }

    [TestMethod]
    public async Task ExecuteStructuredStageAsync_Ollama_ThinkingOnlyWithoutContent_ThrowsDomainValidationException()
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
            await client.ExecuteStructuredStageAsync<Stage1RecipeDto>(
                config,
                "stage1_extract_recipe",
                [new("system", "Extract recipe."), new("user", TaggedSource)],
                MultiStageSchemas.Stage1ExtractRecipeSchema
            );
        });

        StringAssert.Contains(ex.Message, "internal reasoning tokens");
    }

    [TestMethod]
    public async Task ExecuteStructuredStageAsync_OpenAiCompatible_EmptyContentWithLengthFinishReason_ReturnsLengthDoneReasonResult()
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

        var result = await client.ExecuteStructuredStageAsync<Stage1RecipeDto>(
            config,
            "stage1_extract_recipe",
            [new("system", "Extract recipe."), new("user", TaggedSource)],
            MultiStageSchemas.Stage1ExtractRecipeSchema
        );

        Assert.IsNull(result.Parsed);
        Assert.AreEqual("length", result.DoneReason);
        Assert.IsNotNull(result.Error);
        StringAssert.Contains(result.Error, "finish_reason: length");
    }

    [TestMethod]
    public async Task ExecuteStructuredStageAsync_InvalidJson_ReturnsErrorResultWithoutThrowing()
    {
        var handler = new MockHttpMessageHandler(_ => OllamaReply("{ not valid json"));

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

        var result = await client.ExecuteStructuredStageAsync<Stage1RecipeDto>(
            config,
            "stage1_extract_recipe",
            [new("system", "Extract recipe."), new("user", TaggedSource)],
            MultiStageSchemas.Stage1ExtractRecipeSchema
        );

        Assert.IsNull(result.Parsed);
        Assert.IsNull(result.DoneReason);
        Assert.IsNotNull(result.Error);
    }

    [TestMethod]
    public async Task ExecuteStructuredStageAsync_WithThinkingTagsAndMarkdownFences_StripsAndParses()
    {
        var rawRecipeJson = """
            <think>
            Analyzing recipe ingredients and steps...
            Checking for cookware instructions.
            </think>
            ```json
            {
                "title": "Herb Roasted Chicken",
                "description": null,
                "servings": 4,
                "yield": null,
                "sections": [
                    {
                        "name": null,
                        "ingredients": [
                            { "name": "whole chicken", "quantity": 1, "maxQuantity": null, "unit": "whole", "prepNote": null, "isOptional": false, "replacements": [], "alternateMeasurements": [] }
                        ],
                        "instructions": ["Roast 1 whole chicken for 2 minutes."]
                    }
                ],
                "timing": { "prep": 5, "cook": 2, "total": 7 },
                "tags": ["Dinner"]
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

        var result = await client.ExecuteStructuredStageAsync<Stage1RecipeDto>(
            config,
            "stage1_extract_recipe",
            [new("system", "Extract recipe."), new("user", TaggedSource)],
            MultiStageSchemas.Stage1ExtractRecipeSchema
        );

        Assert.IsNull(result.Error);
        Assert.IsNotNull(result.Parsed);
        Assert.AreEqual("Herb Roasted Chicken", result.Parsed.Title);
        Assert.HasCount(1, result.Parsed.Sections[0].Ingredients);
    }

    [TestMethod]
    [DataRow(true, null, true)]
    [DataRow(true, false, false)]
    [DataRow(false, true, true)]
    public async Task ExecuteStructuredStageAsync_ThinkingFlag_RespectsOverrideAndConfig(
        bool configEnableThinking,
        bool? overrideThinking,
        bool expectedThinkPayload
    )
    {
        string? capturedRequestBody = null;
        var handler = new MockHttpMessageHandler(req =>
        {
            capturedRequestBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return OllamaReply(Stage1ButterJson);
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
            EnableThinking: configEnableThinking
        );

        await client.ExecuteStructuredStageAsync<Stage1RecipeDto>(
            config,
            "stage2_shorten_description",
            [new("system", "Shorten."), new("user", TaggedSource)],
            MultiStageSchemas.Stage1ExtractRecipeSchema,
            overrideThinking: overrideThinking
        );

        Assert.IsNotNull(capturedRequestBody);
        using var bodyDoc = JsonDocument.Parse(capturedRequestBody);
        Assert.AreEqual(
            expectedThinkPayload,
            bodyDoc.RootElement.GetProperty("think").GetBoolean()
        );
    }

    [TestMethod]
    public async Task ExecuteStructuredStageAsync_MultiTurnMessages_SerializesConversationInOrder()
    {
        string? capturedRequestBody = null;
        var handler = new MockHttpMessageHandler(req =>
        {
            capturedRequestBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return OllamaReply(Stage1ButterJson);
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

        var result = await client.ExecuteStructuredStageAsync<Stage1RecipeDto>(
            config,
            "stage1_extract_recipe",
            [
                new("system", "Extract recipe."),
                new("user", "First turn source."),
                new("assistant", "{\"title\":\"Broken\"}"),
                new("user", "Repair the JSON."),
            ],
            MultiStageSchemas.Stage1ExtractRecipeSchema
        );

        Assert.IsNull(result.Error);
        Assert.IsNotNull(result.Parsed);
        Assert.IsNotNull(capturedRequestBody);
        using var doc = JsonDocument.Parse(capturedRequestBody);
        var messages = doc.RootElement.GetProperty("messages");
        Assert.AreEqual(4, messages.GetArrayLength());
        Assert.AreEqual("system", messages[0].GetProperty("role").GetString());
        Assert.AreEqual("user", messages[1].GetProperty("role").GetString());
        Assert.AreEqual("assistant", messages[2].GetProperty("role").GetString());
        Assert.AreEqual("user", messages[3].GetProperty("role").GetString());
        Assert.AreEqual("Repair the JSON.", messages[3].GetProperty("content").GetString());
    }

    [TestMethod]
    public async Task ExecuteStructuredStageAsync_Stage3cDto_DeserializesCorrectly()
    {
        var handler = new MockHttpMessageHandler(_ =>
            OllamaReply(
                """{"matches":[{"ingredientIndex":0,"matchType":"existing_ingredient","id":"F1","name":"Unsalted Butter","category":"DairyAndEggs"}]}"""
            )
        );

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

        var matchResult = await client.ExecuteStructuredStageAsync<Stage3BatchMatchResponseDto>(
            config,
            "stage3c_match_batch",
            [new("system", "Match ingredients."), new("user", "butter")],
            MultiStageSchemas.Stage3MatchBatchSchema
        );

        Assert.IsNull(matchResult.Error);
        Assert.IsNotNull(matchResult.Parsed);
        Assert.HasCount(1, matchResult.Parsed.Matches);
        Assert.AreEqual(0, matchResult.Parsed.Matches[0].IngredientIndex);
        Assert.AreEqual("existing_ingredient", matchResult.Parsed.Matches[0].MatchType);
        Assert.AreEqual("F1", matchResult.Parsed.Matches[0].Id);
        Assert.AreEqual("Unsalted Butter", matchResult.Parsed.Matches[0].Name);
    }

    [TestMethod]
    public async Task ExecuteStructuredStage_TransientRateLimit_RetriesAndSucceeds()
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

            return OpenAiReply(Stage1ButterJson);
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

        var result = await client.ExecuteStructuredStageAsync<Stage1RecipeDto>(
            config,
            "stage1_extract_recipe",
            [new("system", "Extract recipe."), new("user", TaggedSource)],
            MultiStageSchemas.Stage1ExtractRecipeSchema
        );

        Assert.IsNull(result.Error);
        Assert.IsNotNull(result.Parsed);
        Assert.AreEqual("Butter Toast", result.Parsed.Title);
        Assert.IsTrue(attempt >= 2, $"Expected at least 2 attempts, but got {attempt}");
    }

    [TestMethod]
    public async Task ExecuteStructuredStage_DailyQuotaExhaustion_ThrowsWithIsQuotaExhaustedTrue_WithoutRetrying()
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
            client.ExecuteStructuredStageAsync<Stage1RecipeDto>(
                config,
                "stage1_extract_recipe",
                [new("system", "Extract recipe."), new("user", TaggedSource)],
                MultiStageSchemas.Stage1ExtractRecipeSchema
            )
        );

        Assert.IsTrue(ex.IsQuotaExhausted);
        Assert.AreEqual(1, attempt, "Hard quota exhaustion should not retry repeatedly.");
    }

    [TestMethod]
    public async Task ExecuteStructuredStage_RateLimitSafeguardsDisabled_ThrowsImmediatelyOn429()
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
            client.ExecuteStructuredStageAsync<Stage1RecipeDto>(
                config,
                "stage1_extract_recipe",
                [new("system", "Extract recipe."), new("user", TaggedSource)],
                MultiStageSchemas.Stage1ExtractRecipeSchema
            )
        );

        Assert.AreEqual(
            1,
            attempt,
            "When safeguards are disabled, 429 should not trigger in-place retries."
        );
    }

    [TestMethod]
    public async Task GenerateEmbeddingsAsync_EmptyInput_ReturnsEmptyWithoutHttpCalls()
    {
        var calls = 0;
        var handler = new MockHttpMessageHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "embeddinggemma",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: false
        );

        var result = await client.GenerateEmbeddingsAsync(config, []);

        Assert.AreEqual(0, calls);
        Assert.IsEmpty(result);
    }

    [TestMethod]
    public async Task GenerateEmbeddingsAsync_MissingModel_ThrowsLlmHostUnavailableException()
    {
        var client = new LlmExtractionClient(
            new HttpClient(
                new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))
            )
        );
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: " ",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: false
        );

        await Assert.ThrowsAsync<LlmHostUnavailableException>(() =>
            client.GenerateEmbeddingsAsync(config, ["unsalted butter"])
        );
    }

    [TestMethod]
    public async Task GenerateEmbeddingsAsync_BatchesChunksOver64SortsByIndexAndL2Normalizes()
    {
        var batchSizes = new List<int>();
        var requestPaths = new List<string>();
        var handler = new MockHttpMessageHandler(req =>
        {
            requestPaths.Add(req.RequestUri!.AbsolutePath);
            Assert.AreEqual("Bearer", req.Headers.Authorization?.Scheme);
            Assert.AreEqual("secret-key", req.Headers.Authorization?.Parameter);

            using var body = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
            Assert.AreEqual("embeddinggemma", body.RootElement.GetProperty("model").GetString());
            var inputArray = body.RootElement.GetProperty("input").EnumerateArray().ToArray();
            batchSizes.Add(inputArray.Length);

            // Return items in reverse order to verify index sorting, and unnormalized lengths to verify L2 normalization.
            var data = Enumerable
                .Range(0, inputArray.Length)
                .Reverse()
                .Select(i => new { index = i, embedding = new[] { 3.0f, 4.0f * (i + 1) } })
                .ToArray();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { data }),
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.OpenAiCompatible,
            BaseUrl: "http://localhost:11434",
            ApiKey: "secret-key",
            TextModel: "embeddinggemma",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 30,
            AutoPublishHighConfidenceText: false
        );

        var inputs = Enumerable.Range(0, 70).Select(i => $"food_{i}").ToArray();
        var vectors = await client.GenerateEmbeddingsAsync(config, inputs);

        CollectionAssert.AreEqual(new[] { 64, 6 }, batchSizes);
        Assert.IsTrue(requestPaths.All(p => p == "/v1/embeddings"));
        Assert.HasCount(70, vectors);

        // Verify item 0 (raw [3, 4] -> [0.6, 0.8]) and batch 2 item 0 (raw [3, 4] -> [0.6, 0.8]), plus L2 norm == 1.
        Assert.AreEqual(0.6f, vectors[0][0], 1e-5f);
        Assert.AreEqual(0.8f, vectors[0][1], 1e-5f);
        Assert.AreEqual(0.6f, vectors[64][0], 1e-5f);
        Assert.AreEqual(0.8f, vectors[64][1], 1e-5f);

        foreach (var vec in vectors)
        {
            var norm = Math.Sqrt(vec.Sum(x => (double)x * x));
            Assert.AreEqual(1.0, norm, 1e-5);
        }
    }

    [TestMethod]
    public async Task DecomposeMealPlanPromptAsync_ParsesStructuredBuckets_AndFallsBackOnInvalidJson()
    {
        var responses = new Queue<string>([
            """
            {
              "excludedTerms": ["seafood", "shrimp"],
              "prioritizeIngredients": ["spinach"],
              "maxTotalTimeMinutes": 35,
              "searchBuckets": [
                {
                  "bucketName": "Italian pasta",
                  "semanticQuery": "creamy italian pasta dinner",
                  "suggestedTags": ["Italian", "Quick"],
                  "maxTotalTimeMinutes": 30,
                  "desiredCount": 2
                }
              ]
            }
            """,
            "{not valid json",
        ]);

        var handler = new MockHttpMessageHandler(req =>
        {
            using var body = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
            var props = body.RootElement.GetProperty("format").GetProperty("properties");
            Assert.IsTrue(props.TryGetProperty("searchBuckets", out _));
            Assert.IsTrue(props.TryGetProperty("excludedTerms", out _));
            Assert.IsTrue(props.TryGetProperty("prioritizeIngredients", out _));
            return OllamaReply(responses.Dequeue());
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            LlmProvider.Ollama,
            "http://localhost:11434",
            null,
            "qwen2.5:14b",
            null,
            "5m",
            30,
            false
        );

        var parsed = await client.DecomposeMealPlanPromptAsync(
            config,
            new LlmMealPlanQueryAnalysisContext(
                "Italian pasta, no seafood, use spinach",
                2,
                "Dinner"
            )
        );

        Assert.AreEqual(2, parsed.ExcludedTerms!.Count);
        Assert.AreEqual("spinach", parsed.PrioritizeIngredients![0]);
        Assert.AreEqual(35, parsed.MaxTotalTimeMinutes);
        Assert.AreEqual(1, parsed.SearchBuckets!.Count);
        Assert.AreEqual("Italian pasta", parsed.SearchBuckets[0].BucketName);

        var fallback = await client.DecomposeMealPlanPromptAsync(
            config,
            new LlmMealPlanQueryAnalysisContext("Cozy fall soups", 3, "Dinner")
        );
        Assert.AreEqual(1, fallback.SearchBuckets!.Count);
        Assert.AreEqual("Cozy fall soups", fallback.SearchBuckets[0].SemanticQuery);
        Assert.AreEqual(3, fallback.SearchBuckets[0].DesiredCount);
    }

    [TestMethod]
    public async Task DraftWeeklyMealPlanAsync_IncludesBucketContextAndParsesUnmetCravingNotes()
    {
        string? capturedUserPrompt = null;
        var handler = new MockHttpMessageHandler(req =>
        {
            using var body = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
            capturedUserPrompt = body
                .RootElement.GetProperty("messages")
                .EnumerateArray()
                .Last(m => m.GetProperty("role").GetString() == "user")
                .GetProperty("content")
                .GetString();

            var props = body.RootElement.GetProperty("format").GetProperty("properties");
            Assert.IsTrue(props.TryGetProperty("unmetCravingNotes", out _));

            return OllamaReply(
                """
                {
                  "proposals": [
                    {
                      "catalogIndex": 1,
                      "recipeTitle": "Tuscan Chicken",
                      "dayOffset": 0,
                      "slot": "Dinner",
                      "isOnDeck": false,
                      "targetServings": 4,
                      "rationale": "Uses up spinach."
                    }
                  ],
                  "unmetCravingNotes": ["No authentic Moroccan recipes matched in your cookbook."]
                }
                """
            );
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            LlmProvider.Ollama,
            "http://localhost:11434",
            null,
            "qwen2.5:14b",
            null,
            "5m",
            30,
            false
        );

        var payload = await client.DraftWeeklyMealPlanAsync(
            config,
            new LlmDraftMealPlanContext(
                WeekStartDate: new DateOnly(2026, 11, 5),
                UserPrompt: "Use up spinach and one Moroccan night",
                RequestedMealCount: 1,
                DefaultSlotName: "Dinner",
                PlaceOnDeck: false,
                ExistingWeekScheduleLines: [],
                CookbookCatalog:
                [
                    new LlmCookbookRecipeCandidate(
                        1,
                        Guid.NewGuid(),
                        "Tuscan Chicken",
                        4m,
                        30,
                        "Easy",
                        ["Italian"],
                        ["Spinach", "Chicken Breast"],
                        4.8m,
                        3,
                        18
                    ),
                ],
                BucketSummaries: ["Moroccan dinner (target ~1)"],
                PrioritizeIngredients: ["spinach"],
                UnmatchedBuckets: ["Moroccan dinner"]
            )
        );

        Assert.IsNotNull(capturedUserPrompt);
        StringAssert.Contains(capturedUserPrompt, "On-Hand Ingredients to Prioritize: spinach");
        StringAssert.Contains(capturedUserPrompt, "Requested Meal Buckets:");
        StringAssert.Contains(
            capturedUserPrompt,
            "Weakly Matched Buckets in Cookbook: Moroccan dinner"
        );
        Assert.AreEqual(1, payload.Proposals!.Count);
        Assert.AreEqual(1, payload.UnmetCravingNotes!.Count);
        StringAssert.Contains(payload.UnmetCravingNotes[0], "Moroccan");
    }

    [TestMethod]
    public async Task DraftWeeklyMealPlanAsync_BlankPrompt_UsesSurpriseGuidanceAndHigherDefaultTemperature()
    {
        string? capturedSystemPrompt = null;
        string? capturedUserPrompt = null;
        double capturedTemperature = 0;

        var handler = new MockHttpMessageHandler(req =>
        {
            using var body = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
            capturedTemperature = body
                .RootElement.GetProperty("options")
                .GetProperty("temperature")
                .GetDouble();
            var messages = body.RootElement.GetProperty("messages");
            capturedSystemPrompt = messages[0].GetProperty("content").GetString();
            capturedUserPrompt = messages[1].GetProperty("content").GetString();

            return OllamaReply(
                """
                {
                  "proposals": [
                    {
                      "catalogIndex": 1,
                      "recipeTitle": "Tuscan Chicken",
                      "dayOffset": 0,
                      "slot": "Dinner",
                      "isOnDeck": false,
                      "targetServings": 4,
                      "rationale": "Balanced main dish for weeknight dinner."
                    }
                  ],
                  "unmetCravingNotes": []
                }
                """
            );
        });

        var client = new LlmExtractionClient(new HttpClient(handler));
        var config = new EffectiveLlmConnectionConfig(
            LlmProvider.Ollama,
            "http://localhost:11434",
            null,
            "qwen2.5:14b",
            null,
            "5m",
            30,
            false
        );

        var payload = await client.DraftWeeklyMealPlanAsync(
            config,
            new LlmDraftMealPlanContext(
                WeekStartDate: new DateOnly(2026, 11, 5),
                UserPrompt: string.Empty,
                RequestedMealCount: 1,
                DefaultSlotName: "Dinner",
                PlaceOnDeck: false,
                ExistingWeekScheduleLines: [],
                CookbookCatalog:
                [
                    new LlmCookbookRecipeCandidate(
                        1,
                        Guid.NewGuid(),
                        "Tuscan Chicken",
                        4m,
                        30,
                        "Easy",
                        ["Dinner", "Main Course"],
                        ["Spinach", "Chicken Breast"],
                        4.8m,
                        3,
                        18
                    ),
                ]
            )
        );

        Assert.AreEqual(0.6, capturedTemperature, 0.001);
        Assert.IsNotNull(capturedSystemPrompt);
        StringAssert.Contains(capturedSystemPrompt, "NEVER suggest standalone dips");
        Assert.IsNotNull(capturedUserPrompt);
        StringAssert.Contains(
            capturedUserPrompt,
            "No specific preferences — surprise the household"
        );
        Assert.AreEqual(1, payload.Proposals!.Count);
    }
}

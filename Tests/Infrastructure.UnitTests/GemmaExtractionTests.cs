using System.Net;
using System.Text;
using System.Text.Json;
using App.Core.Exceptions;
using App.Core.Ingestion;
using App.Infrastructure.Ingestion;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Infrastructure.UnitTests;

[TestClass]
public sealed class GemmaExtractionTests
{
    private const string Source =
        "Title: Butter sauce\nIngredients:\n- 2 tbsp butter\nInstructions:\n1. Melt the butter.";
    private const string IngredientsJson = """
        {"title":"Butter sauce","confidenceScore":0.95,"ingredients":[{"quantity":2,"unit":"tbsp","canonicalFoodName":"butter","displayName":"butter","originalRawText":"2 tbsp butter"}]}
        """;
    private const string StepsJson = """
        {"steps":[{"instruction":"Melt the butter.","ingredientIndices":[0],"timers":[]}]}
        """;

    [TestMethod]
    [DataRow(LlmProvider.Ollama, "gemma4:e2b")]
    [DataRow(LlmProvider.Ollama, "gemma4:e4b")]
    [DataRow(LlmProvider.OpenAiCompatible, "gemma4:e2b")]
    [DataRow(LlmProvider.OpenAiCompatible, "gemma4:e4b")]
    public async Task Extract_UsesFinalIngredientsInSerialStepPass(
        LlmProvider provider,
        string model
    )
    {
        var calls = 0;
        using var handler = new Handler(request =>
        {
            calls++;
            using var document = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result);
            var body = document.RootElement;
            if (provider == LlmProvider.Ollama)
            {
                Assert.AreEqual(
                    16384,
                    body.GetProperty("options").GetProperty("num_ctx").GetInt32()
                );
            }
            var schema = GetSchema(body, provider);
            AssertStrictSchema(schema);
            var properties = schema.GetProperty("properties");
            if (calls == 1)
            {
                Assert.IsTrue(properties.TryGetProperty("ingredients", out var ingredients));
                Assert.IsFalse(properties.TryGetProperty("steps", out _));
                var fields = ingredients.GetProperty("items").GetProperty("properties");
                CollectionAssert.Contains(
                    fields
                        .GetProperty("unit")
                        .GetProperty("enum")
                        .EnumerateArray()
                        .Select(value => value.GetString())
                        .ToArray(),
                    "custom"
                );
                foreach (var name in new[] { "quantity", "maxQuantity", "unit", "preparationNote" })
                {
                    Assert.IsTrue(
                        fields
                            .GetProperty(name)
                            .GetProperty("type")
                            .EnumerateArray()
                            .Any(t => t.GetString() == "null")
                    );
                }
            }
            else
            {
                Assert.AreEqual(2, calls);
                Assert.AreEqual(
                    0,
                    properties
                        .GetProperty("steps")
                        .GetProperty("items")
                        .GetProperty("properties")
                        .GetProperty("ingredientIndices")
                        .GetProperty("items")
                        .GetProperty("maximum")
                        .GetInt32()
                );
                var prompt = body.GetProperty("messages")[1].GetProperty("content").GetString()!;
                StringAssert.Contains(prompt, "\"index\":0");
                StringAssert.Contains(prompt, "\"quantity\":2");
                StringAssert.Contains(prompt, "\"displayName\":\"butter\"");
            }
            return Response(provider, calls == 1 ? IngredientsJson : StepsJson);
        });
        using var http = new HttpClient(handler);
        var result = await new LlmExtractionClient(http).ExtractRecipeFromTextAsync(
            Config(provider, model),
            new LlmExtractionContext(Source, [])
        );
        Assert.AreEqual(2, calls);
        Assert.AreEqual("Butter sauce", result.Title);
        Assert.AreEqual(2, result.Ingredients![0].Quantity);
        CollectionAssert.AreEqual(new[] { 0 }, result.Steps![0].IngredientIndices!.ToArray());
    }

    [TestMethod]
    public async Task Extract_EmptyIngredientPhaseStopsBeforeStepRequest()
    {
        var calls = 0;
        using var http = new HttpClient(
            new Handler(_ =>
            {
                calls++;
                return Response(
                    LlmProvider.Ollama,
                    """{"title":"Missing ingredients","ingredients":[]}"""
                );
            })
        );
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            new LlmExtractionClient(http).ExtractRecipeFromTextAsync(
                Config(LlmProvider.Ollama, "gemma4:e2b"),
                new LlmExtractionContext(Source, [])
            )
        );
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task Extract_RepairsDetectedErrorOnceAndRetainsUnresolvedIssuesForReview()
    {
        var calls = 0;
        var badSteps = """{"steps":[{"instruction":"Melt butter.","ingredientIndices":[8]}]}""";
        using var http = new HttpClient(
            new Handler(request =>
            {
                calls++;
                if (calls == 3)
                {
                    var body = request.Content!.ReadAsStringAsync().Result;
                    StringAssert.Contains(body, "out of range");
                    return Response(
                        LlmProvider.Ollama,
                        IngredientsJson[..^1] + ",\"steps\":" + badSteps[9..]
                    );
                }
                return Response(LlmProvider.Ollama, calls == 1 ? IngredientsJson : badSteps);
            })
        );
        var result = await new LlmExtractionClient(http).ExtractRecipeFromTextAsync(
            Config(LlmProvider.Ollama, "gemma4:e4b"),
            new LlmExtractionContext(Source, [])
        );
        Assert.AreEqual(3, calls);
        Assert.IsNotEmpty(RecipeExtractionDiagnostics.GetIssues(result, Source));
    }

    [TestMethod]
    public async Task Extract_StepPhaseFailureIsNotReturnedAsSuccessfulPartialRecipe()
    {
        var calls = 0;
        using var http = new HttpClient(
            new Handler(_ =>
            {
                calls++;
                return Response(
                    LlmProvider.Ollama,
                    calls == 1 ? IngredientsJson : """{"steps":[]}"""
                );
            })
        );
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            new LlmExtractionClient(http).ExtractRecipeFromTextAsync(
                Config(LlmProvider.Ollama, "gemma4:e4b"),
                new LlmExtractionContext(Source, [])
            )
        );
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task Extract_EmptyCorrectionCannotReplaceACompleteDraft()
    {
        var calls = 0;
        using var http = new HttpClient(
            new Handler(_ =>
            {
                calls++;
                return Response(
                    LlmProvider.Ollama,
                    calls switch
                    {
                        1 => IngredientsJson,
                        2 =>
                            """{"steps":[{"instruction":"Melt 2 tbsp butter.","ingredientIndices":[0]}]}""",
                        _ => """{"title":"Butter sauce","ingredients":[],"steps":[]}""",
                    }
                );
            })
        );
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            new LlmExtractionClient(http).ExtractRecipeFromTextAsync(
                Config(LlmProvider.Ollama, "gemma4:e4b"),
                new LlmExtractionContext(Source, [])
            )
        );
        Assert.AreEqual(3, calls);
    }

    private static EffectiveLlmConnectionConfig Config(LlmProvider provider, string model) =>
        new(provider, "http://localhost:11434", null, model, null, "5m", 120, false);

    [TestMethod]
    [DataRow(LlmProvider.Ollama)]
    [DataRow(LlmProvider.OpenAiCompatible)]
    public async Task Extract_TruncatedButParseableContentIsRejected(LlmProvider provider)
    {
        var content =
            provider == LlmProvider.Ollama
                ? JsonSerializer.Serialize(
                    new { done_reason = "length", message = new { content = IngredientsJson } }
                )
                : JsonSerializer.Serialize(
                    new
                    {
                        choices = new[]
                        {
                            new
                            {
                                finish_reason = "length",
                                message = new { content = IngredientsJson },
                            },
                        },
                    }
                );
        using var http = new HttpClient(
            new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            })
        );
        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            new LlmExtractionClient(http).ExtractRecipeFromTextAsync(
                Config(provider, "gemma4:e4b"),
                new LlmExtractionContext(Source, [])
            )
        );
        StringAssert.Contains(error.Message, "maximum token limit");
    }

    private static JsonElement GetSchema(JsonElement body, LlmProvider provider) =>
        provider == LlmProvider.Ollama
            ? body.GetProperty("format")
            : body.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema");

    private static void AssertStrictSchema(JsonElement schema)
    {
        if (schema.TryGetProperty("properties", out var properties))
        {
            Assert.IsFalse(schema.GetProperty("additionalProperties").GetBoolean());
            var required = schema
                .GetProperty("required")
                .EnumerateArray()
                .Select(x => x.GetString())
                .ToArray();
            CollectionAssert.AreEquivalent(
                properties.EnumerateObject().Select(x => x.Name).ToArray(),
                required
            );
            foreach (var property in properties.EnumerateObject())
                AssertStrictSchema(property.Value);
        }
        if (schema.TryGetProperty("items", out var items))
            AssertStrictSchema(items);
    }

    private static HttpResponseMessage Response(LlmProvider provider, string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                provider == LlmProvider.Ollama
                    ? JsonSerializer.Serialize(new { message = new { content } })
                    : JsonSerializer.Serialize(
                        new { choices = new[] { new { message = new { content } } } }
                    ),
                Encoding.UTF8,
                "application/json"
            ),
        };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(handle(request));
    }
}

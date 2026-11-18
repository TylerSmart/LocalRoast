using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    private const string CleanStage1Json = """
        {"title":"Butter sauce","description":null,"servings":2,"yield":null,"sections":[{"name":null,"ingredients":[{"name":"unsalted butter","quantity":2,"maxQuantity":null,"unit":"tbsp","prepNote":null,"isOptional":false,"replacements":[],"alternateMeasurements":[]}],"instructions":["Melt 2 tbsp butter in a skillet for 2 minutes."]}],"timing":{"prep":5,"cook":5,"total":10},"tags":["Sauce"]}
        """;

    [TestMethod]
    [DataRow(LlmProvider.Ollama, "gemma4:e2b")]
    [DataRow(LlmProvider.Ollama, "gemma4:e4b")]
    [DataRow(LlmProvider.OpenAiCompatible, "gemma4:e2b")]
    [DataRow(LlmProvider.OpenAiCompatible, "gemma4:e4b")]
    public async Task ExecuteStructuredStage_Stage1SendsStrictSchemaAndParsesSections(
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
                Assert.AreEqual(
                    1.0,
                    body.GetProperty("options").GetProperty("repeat_penalty").GetDouble()
                );
            }
            var schema = GetSchema(body, provider);
            AssertStrictSchema(schema);
            var properties = schema.GetProperty("properties");
            Assert.IsTrue(properties.TryGetProperty("sections", out var sections));
            Assert.IsTrue(properties.TryGetProperty("timing", out _));
            Assert.IsTrue(properties.TryGetProperty("tags", out _));
            var secProps = sections.GetProperty("items").GetProperty("properties");
            Assert.IsTrue(secProps.TryGetProperty("ingredients", out _));
            Assert.IsTrue(secProps.TryGetProperty("instructions", out _));
            return Response(provider, CleanStage1Json);
        });

        using var http = new HttpClient(handler);
        var result = await new LlmExtractionClient(
            http
        ).ExecuteStructuredStageAsync<Stage1RecipeDto>(
            Config(provider, model),
            "stage1_extract_recipe",
            [new("system", "Extract recipe"), new("user", Source)],
            MultiStageSchemas.Stage1ExtractRecipeSchema
        );
        Assert.AreEqual(1, calls);
        Assert.IsNull(result.Error);
        Assert.IsNotNull(result.Parsed);
        Assert.AreEqual("Butter sauce", result.Parsed.Title);
        Assert.AreEqual(2m, result.Parsed.Sections[0].Ingredients[0].Quantity);
    }

    [TestMethod]
    [DataRow(LlmProvider.Ollama, 8192, 16384)]
    [DataRow(LlmProvider.Ollama, 32768, 32768)]
    [DataRow(LlmProvider.OpenAiCompatible, 32768, 16384)]
    public async Task ExecuteStructuredStage_OutputCapFollowsTheContextWindowOrMaxTokens(
        LlmProvider provider,
        int contextWindow,
        int expectedMaxCap
    )
    {
        using var http = new HttpClient(
            new Handler(request =>
            {
                using var document = JsonDocument.Parse(
                    request.Content!.ReadAsStringAsync().Result
                );
                var body = document.RootElement;
                Assert.AreEqual(
                    expectedMaxCap,
                    provider == LlmProvider.Ollama
                        ? body.GetProperty("options").GetProperty("num_predict").GetInt32()
                        : body.GetProperty("max_tokens").GetInt32()
                );
                return Response(provider, CleanStage1Json);
            })
        );

        await new LlmExtractionClient(http).ExecuteStructuredStageAsync<Stage1RecipeDto>(
            Config(provider, "gemma4:12b") with
            {
                ContextWindowTokens = contextWindow,
            },
            "stage1_extract_recipe",
            [new("system", "Extract"), new("user", Source)],
            MultiStageSchemas.Stage1ExtractRecipeSchema,
            maxTokens: expectedMaxCap
        );
    }

    [TestMethod]
    public async Task ExecuteStructuredStage_MissingTextModelIsRejectedBeforeAnyRequest()
    {
        var calls = 0;
        using var http = new HttpClient(
            new Handler(_ =>
            {
                calls++;
                return Response(LlmProvider.Ollama, CleanStage1Json);
            })
        );

        await Assert.ThrowsAsync<LlmHostUnavailableException>(() =>
            new LlmExtractionClient(http).ExecuteStructuredStageAsync<Stage1RecipeDto>(
                Config(LlmProvider.Ollama, " "),
                "stage1_extract_recipe",
                [new("system", "Extract"), new("user", Source)],
                MultiStageSchemas.Stage1ExtractRecipeSchema
            )
        );
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    [DataRow(LlmProvider.Ollama)]
    [DataRow(LlmProvider.OpenAiCompatible)]
    public async Task ExecuteStructuredStage_TruncatedResponseReturnsLengthErrorResult(
        LlmProvider provider
    )
    {
        var content =
            provider == LlmProvider.Ollama
                ? JsonSerializer.Serialize(
                    new { done_reason = "length", message = new { content = CleanStage1Json } }
                )
                : JsonSerializer.Serialize(
                    new
                    {
                        choices = new[]
                        {
                            new
                            {
                                finish_reason = "length",
                                message = new { content = CleanStage1Json },
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
        var res = await new LlmExtractionClient(http).ExecuteStructuredStageAsync<Stage1RecipeDto>(
            Config(provider, "gemma4:e4b"),
            "stage1_extract_recipe",
            [new("system", "Extract"), new("user", Source)],
            MultiStageSchemas.Stage1ExtractRecipeSchema
        );
        Assert.IsNull(res.Parsed);
        Assert.IsNotNull(res.Error);
        StringAssert.Contains(res.Error, "maximum token limit");
    }

    private static EffectiveLlmConnectionConfig Config(LlmProvider provider, string model) =>
        new(
            provider,
            "http://localhost:11434",
            null,
            model,
            null,
            "5m",
            120,
            false,
            ContextWindowTokens: 16384
        );

    private static JsonElement GetSchema(JsonElement body, LlmProvider provider) =>
        provider == LlmProvider.Ollama
            ? body.GetProperty("format")
            : body.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema");

    private static void AssertStrictSchema(JsonElement schema)
    {
        if (schema.TryGetProperty("properties", out var properties))
        {
            Assert.IsFalse(schema.GetProperty("additionalProperties").GetBoolean());
            if (schema.TryGetProperty("required", out var reqProp))
            {
                var required = reqProp.EnumerateArray().Select(x => x.GetString()).ToArray();
                foreach (var r in required)
                {
                    Assert.IsTrue(
                        properties.TryGetProperty(r!, out _),
                        $"Required key '{r}' missing from properties."
                    );
                }
            }
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

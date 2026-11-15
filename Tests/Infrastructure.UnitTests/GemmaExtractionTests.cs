using System.Net;
using System.Text;
using System.Text.Json;
using App.Core.Exceptions;
using App.Core.Ingestion;
using App.Core.Recipes;
using App.Infrastructure.Ingestion;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Infrastructure.UnitTests;

[TestClass]
public sealed class GemmaExtractionTests
{
    private const string Source =
        "Title: Butter sauce\nIngredients:\n- 2 tbsp butter\nInstructions:\n1. Melt the butter.";

    private const string CleanRecipe = """
        {"title":"Butter sauce","proposedTags":["Sauce"],"ingredients":[{"refId":"ing_1","canonicalFoodItem":"butter","displayName":"butter","category":"DairyAndEggs","quantity":2,"unit":"tbsp","rawText":"2 tbsp butter"}],"steps":[{"stepNumber":1,"templateText":"Melt {{ing_1_a}}.","ingredientTokens":[{"tokenKey":"ing_1_a","ingredientRefId":"ing_1","stepQuantity":2,"stepUnit":"tbsp"}],"timerTokens":[],"temperatureTokens":[]}],"qualityAudit":{"isPlausibleRecipe":true,"warnings":[]}}
        """;

    [TestMethod]
    [DataRow(LlmProvider.Ollama, "gemma4:e2b")]
    [DataRow(LlmProvider.Ollama, "gemma4:e4b")]
    [DataRow(LlmProvider.OpenAiCompatible, "gemma4:e2b")]
    [DataRow(LlmProvider.OpenAiCompatible, "gemma4:e4b")]
    public async Task Extract_SendsStrictExtractedRecipeSchemaWithPlaceholderTokens(
        LlmProvider provider,
        string model
    )
    {
        var calls = 0;
        const string extractedWithTokens = """
            {"title":"Butter sauce","proposedTags":["Sauce"],"ingredients":[{"refId":"ing_1","canonicalFoodItem":"butter","displayName":"butter","category":"DairyAndEggs","quantity":2,"unit":"tbsp","rawText":"2 tbsp butter"}],"steps":[{"stepNumber":1,"templateText":"Melt {{ing_1_a}} in a pan for {{timer_1}}.","ingredientTokens":[{"tokenKey":"ing_1_a","ingredientRefId":"ing_1","stepQuantity":2,"stepUnit":"tbsp"}],"timerTokens":[{"tokenKey":"timer_1","durationSeconds":120,"label":"Melt butter"}],"temperatureTokens":[]}],"qualityAudit":{"isPlausibleRecipe":true,"warnings":[]}}
            """;

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
            Assert.IsTrue(properties.TryGetProperty("ingredients", out var ingredients));
            Assert.IsTrue(properties.TryGetProperty("steps", out var stepsProp));
            Assert.IsTrue(properties.TryGetProperty("qualityAudit", out _));
            var fields = ingredients.GetProperty("items").GetProperty("properties");
            foreach (
                var name in new[] { "quantity", "maxQuantity", "unit", "altQuantity", "altUnit" }
            )
            {
                Assert.IsTrue(
                    fields
                        .GetProperty(name)
                        .GetProperty("type")
                        .EnumerateArray()
                        .Any(t => t.GetString() == "null")
                );
            }
            var stepProps = stepsProp.GetProperty("items").GetProperty("properties");
            Assert.IsTrue(stepProps.TryGetProperty("templateText", out _));
            Assert.IsTrue(stepProps.TryGetProperty("ingredientTokens", out _));
            Assert.IsTrue(stepProps.TryGetProperty("timerTokens", out _));
            Assert.IsTrue(stepProps.TryGetProperty("temperatureTokens", out _));
            return Response(provider, extractedWithTokens);
        });

        const string timedSource =
            "Title: Butter sauce\nIngredients:\n- 2 tbsp butter\nInstructions:\n1. Melt the butter in a pan for 2 minutes.";
        using var http = new HttpClient(handler);
        var result = await new LlmExtractionClient(http).ExtractRecipeFromTextAsync(
            Config(provider, model),
            new LlmExtractionContext(timedSource)
        );
        Assert.AreEqual(1, calls);
        Assert.AreEqual("Butter sauce", result.Title);
        Assert.AreEqual(2m, result.Ingredients![0].Quantity);
        Assert.AreEqual(
            "Melt {{ing_1_a}} in a pan for {{timer_1}}.",
            result.Steps![0].TemplateText
        );

        var validation = RecipeValidator.Validate(result);
        Assert.IsTrue(validation.IsValid);
        var matches = EntityCandidateMatcher.Match(result, [], []);
        var draft = RecipeDraftCompiler.Compile(result, validation, matches);
        var tokens = StepInstructionParser.ExtractTokens(draft.Steps![0].Instruction);
        Assert.HasCount(2, tokens);
        var inlineIng = (StepIngredientToken)tokens[0];
        Assert.AreEqual("butter", inlineIng.Ingredient);
        Assert.AreEqual(2m, inlineIng.Amount);
        Assert.AreEqual(MeasurementUnit.Tbsp, inlineIng.Unit);
    }

    [TestMethod]
    [DataRow(LlmProvider.Ollama)]
    [DataRow(LlmProvider.OpenAiCompatible)]
    public async Task Extract_PreservesAltMeasurementsAndRecipeReferencesThroughCompiler(
        LlmProvider provider
    )
    {
        const string source = """
Ingredients:
- 4 tablespoons oil (2 ounces)
- 1 recipe Dough
Instructions:
Heat 2 tablespoons oil.
Add 2 tablespoons remaining oil and the dough.
""";
        var recipe = new ExtractedRecipeDto(
            Title: "Dough",
            Ingredients:
            [
                new(
                    RefId: "ing_1",
                    CanonicalFoodItem: "oil",
                    DisplayName: "oil",
                    Category: "CondimentsAndSauces",
                    Quantity: 4m,
                    Unit: "tbsp",
                    AltQuantity: 2m,
                    AltUnit: "oz",
                    RawText: "4 tablespoons oil (2 ounces)"
                ),
                new(
                    RefId: "ing_2",
                    CanonicalFoodItem: "Dough",
                    DisplayName: "Dough",
                    Quantity: 1m,
                    Unit: "whole",
                    IsRecipeReference: true,
                    ReferencedRecipeTitle: "Dough",
                    RawText: "1 recipe Dough"
                ),
            ],
            Steps:
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Heat {{ing_1_a}}.",
                    IngredientTokens: [new("ing_1_a", "ing_1", 2m, "tbsp")]
                ),
                new(
                    StepNumber: 2,
                    TemplateText: "Add {{ing_1_b}} remaining and {{ing_2_a}}.",
                    IngredientTokens:
                    [
                        new("ing_1_b", "ing_1", 2m, "tbsp"),
                        new("ing_2_a", "ing_2", 1m, "whole"),
                    ]
                ),
            ],
            QualityAudit: new RecipeQualityAuditDto(true, [])
        );

        var calls = 0;
        using var http = new HttpClient(
            new Handler(request =>
            {
                calls++;
                using var document = JsonDocument.Parse(
                    request.Content!.ReadAsStringAsync().Result
                );
                var body = document.RootElement;
                AssertStrictSchema(GetSchema(body, provider));
                var systemPrompt = body.GetProperty("messages")[0]
                    .GetProperty("content")
                    .GetString()!;
                if (provider == LlmProvider.Ollama)
                {
                    var options = body.GetProperty("options");
                    Assert.AreEqual(8192, options.GetProperty("num_ctx").GetInt32());
                    Assert.AreEqual(0, options.GetProperty("num_gpu").GetInt32());
                    Assert.AreEqual(0, options.GetProperty("temperature").GetDouble());
                    Assert.AreEqual(1, options.GetProperty("repeat_penalty").GetDouble());
                }
                StringAssert.Contains(systemPrompt, "altQuantity / altUnit");
                StringAssert.Contains(systemPrompt, "referencedRecipeTitle");
                return Response(
                    provider,
                    JsonSerializer.Serialize(
                        recipe,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    )
                );
            })
        );

        var extracted = await new LlmExtractionClient(http).ExtractRecipeFromTextAsync(
            Config(provider, "gemma4:e4b") with
            {
                ContextWindowTokens = 8192,
                OllamaCpuOnly = true,
            },
            new LlmExtractionContext(source)
        );

        Assert.AreEqual(1, calls);
        var validation = RecipeValidator.Validate(extracted);
        Assert.IsTrue(validation.IsValid);
        var matches = EntityCandidateMatcher.Match(extracted, [], []);
        var draft = RecipeDraftCompiler.Compile(extracted, validation, matches);

        Assert.AreEqual(2m, draft.Ingredients![0].AlternativeMeasurement!.Quantity);
        Assert.AreEqual("Dough", draft.Ingredients[1].ReferencedRecipeTitle);
        CollectionAssert.AreEqual(
            new[] { 0, 1 },
            draft.Steps![1].IngredientIndices!.Order().ToArray()
        );
    }

    [TestMethod]
    [DataRow(LlmProvider.Ollama, 8192, 16384)]
    [DataRow(LlmProvider.Ollama, 32768, 32768)]
    [DataRow(LlmProvider.OpenAiCompatible, 32768, 16384)]
    public async Task Extract_OutputCapFollowsTheContextWindow(
        LlmProvider provider,
        int contextWindow,
        int expectedCap
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
                    expectedCap,
                    provider == LlmProvider.Ollama
                        ? body.GetProperty("options").GetProperty("num_predict").GetInt32()
                        : body.GetProperty("max_tokens").GetInt32()
                );
                return Response(provider, CleanRecipe);
            })
        );
        await new LlmExtractionClient(http).ExtractRecipeFromTextAsync(
            Config(provider, "gemma4:12b") with
            {
                ContextWindowTokens = contextWindow,
            },
            new LlmExtractionContext(Source)
        );
    }

    [TestMethod]
    public async Task Extract_EmptySourceIsRejectedBeforeAnyRequest()
    {
        var calls = 0;
        using var http = new HttpClient(
            new Handler(_ =>
            {
                calls++;
                return Response(LlmProvider.Ollama, CleanRecipe);
            })
        );

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            new LlmExtractionClient(http).ExtractRecipeFromTextAsync(
                Config(LlmProvider.Ollama, "gemma4:e4b"),
                new LlmExtractionContext("   ")
            )
        );
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    [DataRow(LlmProvider.Ollama)]
    [DataRow(LlmProvider.OpenAiCompatible)]
    public async Task Extract_TruncatedButParseableContentIsRejected(LlmProvider provider)
    {
        var content =
            provider == LlmProvider.Ollama
                ? JsonSerializer.Serialize(
                    new { done_reason = "length", message = new { content = CleanRecipe } }
                )
                : JsonSerializer.Serialize(
                    new
                    {
                        choices = new[]
                        {
                            new
                            {
                                finish_reason = "length",
                                message = new { content = CleanRecipe },
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
                new LlmExtractionContext(Source)
            )
        );
        StringAssert.Contains(error.Message, "maximum token limit");
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

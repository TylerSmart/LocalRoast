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
    private const string IngredientsJson = """
        {"title":"Butter sauce","confidenceScore":0.95,"ingredients":[{"quantity":2,"unit":"tbsp","canonicalFoodName":"butter","displayName":"butter","originalRawText":"2 tbsp butter"}]}
        """;
    private const string ButterTags = """{"steps":{"1":["0 butter"]}}""";
    private const string CleanRecipe = """
        {"title":"Butter sauce","suggestedTagNames":["Sauce"],"ingredients":[{"quantity":2,"unit":"tbsp","canonicalFoodName":"butter","displayName":"butter","originalRawText":"2 tbsp butter"}],"steps":[{"instruction":"Melt the butter.","ingredientIndices":[0]}]}
        """;
    private const string MeasuredSource =
        "Title: Butter sauce\nIngredients:\n- 2 tbsp butter\nInstructions:\n1. Melt 2 tbsp butter.";

    private static bool IsMethodTagRequest(string body) =>
        body.Contains("You annotate a recipe method", StringComparison.Ordinal);

    private static string UserText(JsonElement body)
    {
        var content = body.GetProperty("messages")[1].GetProperty("content");
        return content.ValueKind == JsonValueKind.Array
            ? content[0].GetProperty("text").GetString()!
            : content.GetString()!;
    }

    [TestMethod]
    [DataRow(LlmProvider.Ollama, "gemma4:e2b")]
    [DataRow(LlmProvider.Ollama, "gemma4:e4b")]
    [DataRow(LlmProvider.OpenAiCompatible, "gemma4:e2b")]
    [DataRow(LlmProvider.OpenAiCompatible, "gemma4:e4b")]
    public async Task Extract_SendsSingleExtractionWithStepsAndInlineTokens(
        LlmProvider provider,
        string model
    )
    {
        var calls = 0;
        const string extractedWithTokens = """
            {"title":"Butter sauce","confidenceScore":0.95,"ingredients":[{"quantity":2,"unit":"tbsp","canonicalFoodName":"butter","displayName":"butter","originalRawText":"2 tbsp butter"}],"steps":[{"stepNumber":1,"instruction":"Melt ||{\"ingredient\": \"butter\", \"amount\": 2, \"unit\": \"tbsp\"}|| in a pan."}]}
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
            return Response(provider, extractedWithTokens);
        });
        using var http = new HttpClient(handler);
        var result = await new LlmExtractionClient(http).ExtractRecipeFromTextAsync(
            Config(provider, model),
            new LlmExtractionContext(Source, [])
        );
        Assert.AreEqual(1, calls);
        Assert.AreEqual("Butter sauce", result.Title);
        Assert.AreEqual(2, result.Ingredients![0].Quantity);
        Assert.AreEqual(
            "Melt ||{\"ingredient\": \"butter\", \"amount\": 2, \"unit\": \"tbsp\"}|| in a pan.",
            result.Steps![0].Instruction
        );
        var tokens = StepInstructionParser.ExtractTokens(result.Steps[0].Instruction);
        var token = tokens.Single() as StepIngredientToken;
        Assert.IsNotNull(token);
        Assert.AreEqual("butter", token.Ingredient);
        Assert.AreEqual(2m, token.Amount);
        Assert.AreEqual(MeasurementUnit.Tbsp, token.Unit);
        Assert.IsEmpty(result.TaggerIssues ?? []);
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
    public async Task Extract_SourceWithoutInstructionsIsRejectedBeforeAnyRequest()
    {
        var calls = 0;
        using var http = new HttpClient(
            new Handler(_ =>
            {
                calls++;
                return Response(LlmProvider.Ollama, IngredientsJson);
            })
        );
        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            new LlmExtractionClient(http).ExtractRecipeFromTextAsync(
                Config(LlmProvider.Ollama, "gemma4:e4b"),
                new LlmExtractionContext("Title: Butter sauce\nIngredients:\n- 2 tbsp butter", [])
            )
        );
        StringAssert.Contains(error.Message, "no Instructions section");
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    [DataRow(LlmProvider.Ollama)]
    [DataRow(LlmProvider.OpenAiCompatible)]
    public async Task Extract_TagsVerbatimAmountsAndPreservesMeasurementsAndReferences(
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
        var recipe = new LlmExtractedRecipePayload(
            Title: "Dough",
            Ingredients:
            [
                new(
                    null,
                    4,
                    null,
                    "tbsp",
                    "oil",
                    "oil",
                    OriginalRawText: "4 tablespoons oil (2 ounces)",
                    AlternativeMeasurement: new(2, "oz"),
                    SuggestedCategory: "CondimentsAndSauces"
                ),
                new(
                    null,
                    1,
                    null,
                    "whole",
                    "",
                    "Dough",
                    OriginalRawText: "1 recipe Dough",
                    IsRecipeReference: true,
                    ReferencedRecipeTitle: "Dough"
                ),
            ],
            Steps:
            [
                new(
                    null,
                    "Heat ||{\"ingredient\": \"oil\", \"amount\": 2, \"unit\": \"tbsp\"}|| oil."
                ),
                new(
                    null,
                    "Add ||{\"ingredient\": \"oil\", \"amount\": 2, \"unit\": \"tbsp\"}|| remaining oil and the ||{\"ingredient\": \"Dough\", \"amount\": 1, \"unit\": \"whole\"}||."
                ),
            ]
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
                StringAssert.Contains(systemPrompt, "alternativeMeasurement");
                StringAssert.Contains(systemPrompt, "referencedRecipeTitle");
                StringAssert.Contains(systemPrompt, "suggestedCategory");
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
            new LlmExtractionContext(source, [])
        );
        Assert.AreEqual(1, calls);
        Assert.IsEmpty(extracted.TaggerIssues ?? []);
        Assert.AreEqual(
            "Heat ||{\"ingredient\": \"oil\", \"amount\": 2, \"unit\": \"tbsp\"}|| oil.",
            extracted.Steps![0].Instruction
        );
        var tokens = StepInstructionParser.ExtractTokens(extracted.Steps[0].Instruction);
        var inline = (StepIngredientToken)tokens.Single();
        Assert.AreEqual("oil", inline.Ingredient);
        Assert.AreEqual(2m, inline.Amount);
        Assert.AreEqual(MeasurementUnit.Tbsp, inline.Unit);

        var draft = RecipeDraftNormalizer.Normalize(
            extracted,
            [],
            [],
            null,
            null,
            null,
            null,
            null,
            rawSourceText: source
        );
        Assert.AreEqual(2m, draft.Ingredients![0].AlternativeMeasurement!.Quantity);
        Assert.AreEqual("Dough", draft.Ingredients[1].ReferencedRecipeTitle);
        Assert.AreEqual(
            "Add ||{\"ingredient\": \"oil\", \"amount\": 2, \"unit\": \"tbsp\"}|| remaining oil and the ||{\"ingredient\": \"Dough\", \"amount\": 1, \"unit\": \"whole\"}||.",
            draft.Steps![1].Instruction
        );
        CollectionAssert.AreEqual(
            new[] { 0, 1 },
            draft.Steps[1].IngredientIndices!.Order().ToArray()
        );
    }

    [TestMethod]
    public async Task Extract_UnclassifiedAmountIsReturnedAsTaggerIssue()
    {
        var malformedRecipe = """
            {"title":"Butter sauce","confidenceScore":0.95,"ingredients":[{"quantity":2,"unit":"tbsp","canonicalFoodName":"butter","displayName":"butter","originalRawText":"2 tbsp butter"}],"steps":[{"stepNumber":1,"instruction":"Melt ||{bad json}|| butter."}]}
            """;
        using var http = new HttpClient(
            new Handler(_ => Response(LlmProvider.Ollama, malformedRecipe))
        );
        var result = await new LlmExtractionClient(http).ExtractRecipeFromTextAsync(
            Config(LlmProvider.Ollama, "gemma4:e4b"),
            new LlmExtractionContext(MeasuredSource, [])
        );
        Assert.AreEqual("Melt ||{bad json}|| butter.", result.Steps![0].Instruction);
        Assert.IsTrue(
            result.TaggerIssues!.Any(issue => issue.Contains("unparseable inline token"))
        );
    }

    [TestMethod]
    [DataRow(LlmProvider.Ollama)]
    [DataRow(LlmProvider.OpenAiCompatible)]
    public async Task ManualRepair_ReExtractsFromSourceWithHintAndPreviousIngredients(
        LlmProvider provider
    )
    {
        var calls = 0;
        using var http = new HttpClient(
            new Handler(request =>
            {
                calls++;
                using var document = JsonDocument.Parse(
                    request.Content!.ReadAsStringAsync().Result
                );
                var body = document.RootElement;
                var text = UserText(body);
                StringAssert.Contains(
                    text,
                    "Reviewer feedback (apply it): Keep the user's unsalted butter."
                );
                StringAssert.Contains(text, "Butter sauce");
                StringAssert.Contains(text, "Previous ingredient rows");
                StringAssert.Contains(text, "2 tbsp butter");
                StringAssert.Contains(
                    body.GetProperty("messages")[0].GetProperty("content").GetString()!,
                    "Tags (suggestedTagNames)"
                );
                return Response(provider, CleanRecipe);
            })
        );
        var original = JsonSerializer.Deserialize<LlmExtractedRecipePayload>(
            CleanRecipe,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        )!;
        var result = await new LlmExtractionClient(http).RepairRecipeDraftAsync(
            Config(provider, "text-model"),
            new LlmRecipeRepairContext(Source, original, "Keep the user's unsalted butter.")
        );

        Assert.AreEqual(1, calls);
        Assert.AreEqual("Melt the butter.", result.Draft.Steps![0].Instruction);
        Assert.IsEmpty(result.Notices);
    }

    [TestMethod]
    public async Task ManualRepair_ReportsUnavailableHostInsteadOfReturningTheOriginalDraft()
    {
        using var http = new HttpClient(
            new Handler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
        );
        var original = JsonSerializer.Deserialize<LlmExtractedRecipePayload>(
            CleanRecipe,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        )!;

        await Assert.ThrowsAsync<LlmHostUnavailableException>(() =>
            new LlmExtractionClient(http).RepairRecipeDraftAsync(
                Config(LlmProvider.Ollama, "text-model"),
                new LlmRecipeRepairContext(Source, original, "Make the timer clearer.")
            )
        );
    }

    [TestMethod]
    public async Task ManualRepair_ReportsRemainingTaggerIssuesAsNotices()
    {
        var recipeWithIssue = """
            {"title":"Butter sauce","suggestedTagNames":["Sauce"],"ingredients":[{"quantity":2,"unit":"tbsp","canonicalFoodName":"butter","displayName":"butter","originalRawText":"2 tbsp butter"}],"steps":[{"instruction":"Melt ||{broken}||."}]}
            """;
        using var http = new HttpClient(
            new Handler(_ => Response(LlmProvider.Ollama, recipeWithIssue))
        );
        var original = JsonSerializer.Deserialize<LlmExtractedRecipePayload>(
            CleanRecipe,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        )!;

        var result = await new LlmExtractionClient(http).RepairRecipeDraftAsync(
            Config(LlmProvider.Ollama, "text-model"),
            new LlmRecipeRepairContext(MeasuredSource, original, null)
        );

        Assert.IsNotEmpty(result.Notices);
        Assert.IsTrue(result.Notices.All(notice => notice.StartsWith("Still needs review: ")));
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
            new LlmExtractionContext(Source, [])
        );
    }

    [TestMethod]
    [DataRow(LlmProvider.Ollama, false)]
    [DataRow(LlmProvider.Ollama, true)]
    [DataRow(LlmProvider.OpenAiCompatible, false)]
    public async Task StructureSource_LabelsEveryLineAndBuildsCanonicalText(
        LlmProvider provider,
        bool thinking
    )
    {
        const string plain = """
            Skillet Cornbread
            Serves 8
            Jump to recipe
            1 cup cornmeal
            1 cup buttermilk, at room
            temperature
            Whisk the cornmeal and buttermilk.
            Bake for 20 minutes.
            """;
        using var http = new HttpClient(
            new Handler(request =>
            {
                using var document = JsonDocument.Parse(
                    request.Content!.ReadAsStringAsync().Result
                );
                var body = document.RootElement;
                var schema = GetSchema(body, provider);
                AssertStrictSchema(schema);
                var properties = schema.GetProperty("properties").EnumerateObject().ToList();
                CollectionAssert.AreEqual(
                    new[] { "1", "2", "3", "4", "5", "6", "7", "8" },
                    properties.Select(p => p.Name).ToArray()
                );
                CollectionAssert.AreEqual(
                    RecipeSourceSectioner.LabelNames.ToArray(),
                    properties[0]
                        .Value.GetProperty("enum")
                        .EnumerateArray()
                        .Select(x => x.GetString())
                        .ToArray()
                );
                var expectedTokens = 1024 + (thinking ? 8192 : 0);
                if (provider == LlmProvider.Ollama)
                {
                    Assert.AreEqual(thinking, body.GetProperty("think").GetBoolean());
                    Assert.AreEqual(
                        expectedTokens,
                        body.GetProperty("options").GetProperty("num_predict").GetInt32()
                    );
                }
                else
                {
                    Assert.AreEqual(expectedTokens, body.GetProperty("max_tokens").GetInt32());
                }
                var system = body.GetProperty("messages")[0].GetProperty("content").GetString()!;
                Assert.DoesNotContain("JSON schema:", system);
                Assert.DoesNotContain("Strict Output Rule", system);
                var prompt = body.GetProperty("messages")[1].GetProperty("content").GetString()!;
                StringAssert.Contains(prompt, "1| Skillet Cornbread");
                StringAssert.Contains(prompt, "6| temperature");
                return Response(
                    provider,
                    """
                    {"1":"title","2":"detail","3":"skip","4":"ingredient","5":"ingredient","6":"ingredient_wrap","7":"instruction","8":"instruction"}
                    """
                );
            })
        );
        var structured = await new LlmExtractionClient(http).StructureRecipeSourceAsync(
            Config(provider, "gemma4:e4b") with
            {
                EnableThinking = thinking,
            },
            plain
        );
        Assert.AreEqual(
            "Title: Skillet Cornbread\nServes 8\n\nIngredients:\n- 1 cup cornmeal\n- 1 cup buttermilk, at room temperature\n\nInstructions:\nWhisk the cornmeal and buttermilk.\nBake for 20 minutes.",
            structured
        );
    }

    [TestMethod]
    public async Task StructureSource_UnparseableLabelsFallBackToNull()
    {
        using var http = new HttpClient(new Handler(_ => Response(LlmProvider.Ollama, "not json")));
        Assert.IsNull(
            await new LlmExtractionClient(http).StructureRecipeSourceAsync(
                Config(LlmProvider.Ollama, "gemma4:e4b"),
                "Cornbread\n1 cup cornmeal\nBake it."
            )
        );
    }

    [TestMethod]
    public async Task StructureSource_SkipsSourcesOverTheLineCap()
    {
        var requests = 0;
        using var http = new HttpClient(
            new Handler(_ =>
            {
                requests++;
                return Response(LlmProvider.Ollama, "{}");
            })
        );
        var source = string.Join(
            '\n',
            Enumerable.Range(1, RecipeSourceSectioner.MaxLines + 1).Select(i => $"Line {i}")
        );
        Assert.IsNull(
            await new LlmExtractionClient(http).StructureRecipeSourceAsync(
                Config(LlmProvider.Ollama, "gemma4:e4b"),
                source
            )
        );
        Assert.AreEqual(0, requests);
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

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using App.Core.Authentication;
using App.Core.Recipes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.API.IntegrationTests;

[TestClass]
public sealed class RecipeInlineAmountsIntegrationTests
{
    private const string RouxStep =
        "Melt 1/2 cup butter, whisk in 2 cups flour, then reserve 1 cup of the roux.";

    private AppWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _factory = new AppWebApplicationFactory();
        _client = _factory.CreateClient();
        var response = await _client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest("inline@example.com", "inlineamt", "Password123!")
        );
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.IsNotNull(auth);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            auth.Token
        );
    }

    [TestCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static RecipeStepInlineAmountInput Span(
        string instruction,
        string text,
        InlineStepAmountKind kind,
        int? ingredientIndex,
        RecipeMeasurement measurement,
        int occurrence = 0
    )
    {
        var start = -1;
        for (var i = 0; i <= occurrence; i++)
        {
            start = instruction.IndexOf(text, start + 1, StringComparison.Ordinal);
        }
        Assert.IsGreaterThanOrEqualTo(0, start, $"'{text}' not found in '{instruction}'.");
        return new(start, text.Length, kind, ingredientIndex, measurement);
    }

    private static UpsertRecipeRequest RouxRecipe() =>
        new(
            Title: "Synthetic Roux",
            Ingredients:
            [
                new RecipeIngredientInput(
                    Quantity: 0.5m,
                    Unit: MeasurementUnit.Cup,
                    DisplayName: "butter"
                ),
                new RecipeIngredientInput(
                    Quantity: 2,
                    Unit: MeasurementUnit.Cup,
                    DisplayName: "flour"
                ),
            ],
            Steps:
            [
                new RecipeStepInput(
                    Instruction: RouxStep,
                    InlineAmounts:
                    [
                        Span(
                            RouxStep,
                            "1/2 cup",
                            InlineStepAmountKind.Ingredient,
                            0,
                            new(0.5m, MeasurementUnit.Cup)
                        ),
                        Span(
                            RouxStep,
                            "2 cups",
                            InlineStepAmountKind.Ingredient,
                            1,
                            new(2, MeasurementUnit.Cup)
                        ),
                        Span(
                            RouxStep,
                            "1 cup",
                            InlineStepAmountKind.Mixture,
                            null,
                            new(1, MeasurementUnit.Cup)
                        ),
                    ]
                ),
            ]
        );

    private async Task<RecipeDetailDto> CreateAsync(UpsertRecipeRequest request)
    {
        var response = await _client.PostAsJsonAsync("/api/recipes", request);
        Assert.AreEqual(
            HttpStatusCode.Created,
            response.StatusCode,
            await response.Content.ReadAsStringAsync()
        );
        var created = await response.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(created);
        return created;
    }

    [TestMethod]
    public async Task CreateAndGet_RoundTripsInlineAmountsWithIngredientIds()
    {
        var created = await CreateAsync(RouxRecipe());

        var json = await _client.GetStringAsync($"/api/recipes/{created.Id}");
        using (var document = JsonDocument.Parse(json))
        {
            var first = document.RootElement.GetProperty("steps")[0].GetProperty("inlineAmounts")[
                0
            ];
            Assert.AreEqual("Ingredient", first.GetProperty("kind").GetString());
            Assert.AreEqual(5, first.GetProperty("start").GetInt32());
        }

        var recipe = JsonSerializer.Deserialize<RecipeDetailDto>(json, JsonSerializerOptions.Web);
        Assert.IsNotNull(recipe);
        var butter = recipe.Ingredients.Single(i => i.DisplayName == "butter");
        var flour = recipe.Ingredients.Single(i => i.DisplayName == "flour");
        var step = recipe.Steps.Single();
        Assert.AreEqual(RouxStep, step.Instruction);
        var spans = step.InlineAmounts!;
        Assert.HasCount(3, spans);
        CollectionAssert.AreEqual(
            new[] { "1/2 cup", "2 cups", "1 cup" },
            spans.Select(s => RouxStep.Substring(s.Start, s.Length)).ToArray()
        );
        Assert.AreEqual(butter.Id, spans[0].IngredientId);
        Assert.AreEqual(flour.Id, spans[1].IngredientId);
        Assert.AreEqual(InlineStepAmountKind.Mixture, spans[2].Kind);
        Assert.IsNull(spans[2].IngredientId);
        Assert.AreEqual(0.5m, spans[0].Measurement.Quantity);
        Assert.AreEqual(MeasurementUnit.Cup, spans[1].Measurement.Unit);
        CollectionAssert.AreEquivalent(new[] { butter.Id, flour.Id }, step.IngredientIds.ToArray());
        Assert.AreEqual(
            2m,
            step.IngredientAmounts!.Single(a => a.IngredientId == flour.Id).Measurement.Quantity
        );
    }

    [TestMethod]
    public async Task Update_RecreatesStepsWithTrimmedOffsets()
    {
        var created = await CreateAsync(RouxRecipe());
        const string instruction = "  Cook 1 cup butter until nutty.";
        var update = RouxRecipe() with
        {
            Steps =
            [
                new RecipeStepInput(
                    Instruction: instruction,
                    InlineAmounts:
                    [
                        Span(
                            instruction,
                            "1 cup",
                            InlineStepAmountKind.Ingredient,
                            0,
                            new(1, MeasurementUnit.Cup),
                            0
                        ) with
                        {
                            AlternativeMeasurement = new(8, MeasurementUnit.Oz),
                        },
                    ]
                ),
                new RecipeStepInput(Instruction: "Whisk in the flour.", IngredientIndices: [1]),
            ],
        };

        var response = await _client.PutAsJsonAsync($"/api/recipes/{created.Id}", update);
        Assert.AreEqual(
            HttpStatusCode.OK,
            response.StatusCode,
            await response.Content.ReadAsStringAsync()
        );

        var recipe = await _client.GetFromJsonAsync<RecipeDetailDto>($"/api/recipes/{created.Id}");
        Assert.IsNotNull(recipe);
        Assert.HasCount(2, recipe.Steps);
        var step = recipe.Steps[0];
        Assert.AreEqual(instruction.Trim(), step.Instruction);
        var span = step.InlineAmounts!.Single();
        Assert.AreEqual("1 cup", step.Instruction.Substring(span.Start, span.Length));
        Assert.AreEqual(8m, span.AlternativeMeasurement!.Quantity);
        Assert.AreEqual(MeasurementUnit.Oz, span.AlternativeMeasurement.Unit);
        Assert.IsEmpty(recipe.Steps[1].InlineAmounts!);
    }

    [TestMethod]
    public async Task InvalidInlineAmounts_ReturnBadRequest()
    {
        var valid = RouxRecipe();
        var spans = valid.Steps![0].InlineAmounts!;
        RecipeStepInput WithSpans(params RecipeStepInlineAmountInput[] inline) =>
            valid.Steps[0] with
            {
                InlineAmounts = inline,
            };
        var invalidSteps = new[]
        {
            // Overlapping spans.
            WithSpans(spans[0], spans[0] with { Start = spans[0].Start + 2 }),
            // Out of order.
            WithSpans(spans[1], spans[0]),
            // Out of range.
            WithSpans(spans[0] with { Start = RouxStep.Length - 2, Length = 5 }),
            // Ingredient kind without an index.
            WithSpans(spans[0] with { IngredientIndex = null }),
            // Ingredient kind with an unknown index.
            WithSpans(spans[0] with { IngredientIndex = 7 }),
            // Fixed kind with an index.
            WithSpans(spans[0] with { Kind = InlineStepAmountKind.Fixed }),
        };

        foreach (var step in invalidSteps)
        {
            var response = await _client.PostAsJsonAsync(
                "/api/recipes",
                valid with
                {
                    Steps = [step],
                }
            );
            Assert.AreEqual(
                HttpStatusCode.BadRequest,
                response.StatusCode,
                JsonSerializer.Serialize(step.InlineAmounts)
            );
        }
    }

    [TestMethod]
    public async Task IngredientSpans_DeriveTheStepAmount()
    {
        const string instruction = "Stir in 1/2 cup milk, rest, then add another 1/2 cup milk.";
        var request = new UpsertRecipeRequest(
            Title: "Synthetic Custard",
            Ingredients:
            [
                new RecipeIngredientInput(
                    Quantity: 1,
                    Unit: MeasurementUnit.Cup,
                    DisplayName: "milk"
                ),
            ],
            Steps:
            [
                new RecipeStepInput(
                    Instruction: instruction,
                    IngredientAmounts: [new(0, new(3, MeasurementUnit.Cup))],
                    InlineAmounts:
                    [
                        Span(
                            instruction,
                            "1/2 cup",
                            InlineStepAmountKind.Ingredient,
                            0,
                            new(0.5m, MeasurementUnit.Cup)
                        ),
                        Span(
                            instruction,
                            "1/2 cup",
                            InlineStepAmountKind.Ingredient,
                            0,
                            new(0.5m, MeasurementUnit.Cup),
                            1
                        ),
                    ]
                ),
            ]
        );

        var created = await CreateAsync(request);

        var amount = created.Steps[0].IngredientAmounts!.Single();
        Assert.AreEqual(created.Ingredients[0].Id, amount.IngredientId);
        Assert.AreEqual(1m, amount.Measurement.Quantity);
        Assert.AreEqual(MeasurementUnit.Cup, amount.Measurement.Unit);
        Assert.HasCount(2, created.Steps[0].InlineAmounts!);
    }

    [TestMethod]
    public async Task Update_RemovingAReferencedIngredientRowSucceeds()
    {
        var created = await CreateAsync(RouxRecipe());
        const string instruction = "Melt 1/2 cup butter.";
        var update = RouxRecipe() with
        {
            Ingredients = [RouxRecipe().Ingredients![0]],
            Steps =
            [
                new RecipeStepInput(
                    Instruction: instruction,
                    InlineAmounts:
                    [
                        Span(
                            instruction,
                            "1/2 cup",
                            InlineStepAmountKind.Ingredient,
                            0,
                            new(0.5m, MeasurementUnit.Cup)
                        ),
                    ]
                ),
            ],
        };

        var response = await _client.PutAsJsonAsync($"/api/recipes/{created.Id}", update);
        Assert.AreEqual(
            HttpStatusCode.OK,
            response.StatusCode,
            await response.Content.ReadAsStringAsync()
        );
        var recipe = await response.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(recipe);
        Assert.HasCount(1, recipe.Ingredients);
        Assert.AreEqual(
            recipe.Ingredients[0].Id,
            recipe.Steps.Single().InlineAmounts!.Single().IngredientId
        );

        var deleted = await _client.DeleteAsync($"/api/recipes/{created.Id}");
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [TestMethod]
    public async Task DetectStepAmounts_ReturnsMeasuredSpans()
    {
        const string instruction = "Whisk in 1 1/2 cups milk and simmer for 5 minutes.";
        var response = await _client.PostAsJsonAsync(
            "/api/recipes/step-amounts/detect",
            new DetectStepAmountsRequest(instruction)
        );
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var detected = await response.Content.ReadFromJsonAsync<
            IReadOnlyList<DetectedStepAmountDto>
        >();
        Assert.IsNotNull(detected);
        var amount = detected.Single();
        Assert.AreEqual(instruction.IndexOf("1 1/2", StringComparison.Ordinal), amount.Start);
        Assert.AreEqual("1 1/2 cups".Length, amount.Length);
        Assert.AreEqual("1 1/2 cups", amount.Text);
        Assert.AreEqual(1.5m, amount.Measurement.Quantity);
        Assert.AreEqual(MeasurementUnit.Cup, amount.Measurement.Unit);
        Assert.IsNull(amount.AlternativeMeasurement);

        var invalid = await _client.PostAsJsonAsync(
            "/api/recipes/step-amounts/detect",
            new { instruction = (string?)null }
        );
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var anonymous = _factory.CreateClient();
        var unauthorized = await anonymous.PostAsJsonAsync(
            "/api/recipes/step-amounts/detect",
            new DetectStepAmountsRequest(instruction)
        );
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }

    [TestMethod]
    public async Task PublicShare_IncludesInlineAmounts()
    {
        var created = await CreateAsync(RouxRecipe());
        var shareResponse = await _client.PostAsJsonAsync(
            $"/api/recipes/{created.Id}/shares",
            new CreateShareLinkRequest()
        );
        shareResponse.EnsureSuccessStatusCode();
        var share = await shareResponse.Content.ReadFromJsonAsync<ShareLinkDto>();
        Assert.IsNotNull(share);

        using var anonymous = _factory.CreateClient();
        var shared = await anonymous.GetFromJsonAsync<PublicSharedRecipeDto>(
            $"/api/public/shares/{share.Token}"
        );
        Assert.IsNotNull(shared);
        var spans = shared.Steps.Single().InlineAmounts!;
        Assert.HasCount(3, spans);
        Assert.AreEqual(
            shared.Ingredients.Single(i => i.DisplayName == "flour").Id,
            spans[1].IngredientId
        );
        Assert.AreEqual(InlineStepAmountKind.Mixture, spans[2].Kind);
    }
}

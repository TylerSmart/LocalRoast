using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using App.Core.Authentication;
using App.Core.Mcp;
using App.Core.MealPlans;
using App.Core.Recipes;
using App.Core.Shopping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.API.IntegrationTests;

[TestClass]
public sealed class RecipeMeasurementsIntegrationTests
{
    private AppWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _factory = new AppWebApplicationFactory();
        _client = _factory.CreateClient();
        var response = await _client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest("measurements@example.com", "measurements", "Password123!")
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

    private static UpsertRecipeRequest Starter() =>
        new(
            Title: "Synthetic Starter",
            BaseServings: 4,
            Status: RecipeStatus.Published,
            Ingredients:
            [
                new RecipeIngredientInput(
                    DisplayName: "Bread flour",
                    Quantity: 5,
                    Unit: MeasurementUnit.Cup,
                    AlternativeMeasurement: new(25, MeasurementUnit.Oz),
                    PreferAlternativeMeasurement: true
                ),
            ],
            Steps:
            [
                new(
                    Instruction: "Mix the listed flour.",
                    IngredientAmounts: [new(0, new(1, MeasurementUnit.Cup))]
                ),
                new(
                    Instruction: "Feed with the listed flour.",
                    IngredientAmounts: [new(0, new(4, MeasurementUnit.Cup))]
                ),
                new(
                    Instruction: "Rest the prepared portion.",
                    IngredientAmounts: [new(0, new(1, MeasurementUnit.Cup))]
                ),
                new(Instruction: "Inspect the flour.", IngredientIndices: [0]),
            ]
        );

    [TestMethod]
    public async Task Measurements_RoundTripThroughUpdateSharingMcpAndShoppingWithoutDoubleCounting()
    {
        var request = Starter();
        var createdResponse = await _client.PostAsJsonAsync("/api/recipes", request);
        Assert.AreEqual(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = await createdResponse.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(created);
        var updatedResponse = await _client.PutAsJsonAsync($"/api/recipes/{created.Id}", request);
        Assert.AreEqual(HttpStatusCode.OK, updatedResponse.StatusCode);
        var recipe = await _client.GetFromJsonAsync<RecipeDetailDto>($"/api/recipes/{created.Id}");
        Assert.IsNotNull(recipe);
        var flour = recipe.Ingredients.Single();
        Assert.AreEqual(25m, flour.AlternativeMeasurement!.Quantity);
        Assert.IsTrue(flour.PreferAlternativeMeasurement);
        Assert.AreEqual(1m, recipe.Steps[0].IngredientAmounts![0].Measurement.Quantity);
        Assert.AreEqual(4m, recipe.Steps[1].IngredientAmounts![0].Measurement.Quantity);
        Assert.AreEqual(flour.Id, recipe.Steps[0].IngredientAmounts![0].IngredientId);
        CollectionAssert.AreEqual(new[] { flour.Id }, recipe.Steps[0].IngredientIds.ToArray());
        Assert.IsEmpty(recipe.Steps[3].IngredientAmounts!);

        var shareResponse = await _client.PostAsJsonAsync(
            $"/api/recipes/{recipe.Id}/shares",
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
        Assert.AreEqual(25m, shared.Ingredients[0].AlternativeMeasurement!.Quantity);
        Assert.IsTrue(shared.Ingredients[0].PreferAlternativeMeasurement);
        Assert.AreEqual(1m, shared.Steps[0].IngredientAmounts![0].Measurement.Quantity);

        using (var scope = _factory.Services.CreateScope())
        {
            var mcp = await scope
                .ServiceProvider.GetRequiredService<IMcpCookbookService>()
                .GetRecipeAsync(recipe.Id);
            Assert.IsNotNull(mcp);
            Assert.AreEqual(5m, mcp.Steps[0].StepIngredients[0].Quantity);
            Assert.AreEqual(MeasurementUnit.Oz, mcp.Steps[0].StepIngredients[0].Unit);
            Assert.IsNull(mcp.Steps[3].StepIngredients[0].Quantity);
        }

        var date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
        var mealResponse = await _client.PostAsJsonAsync(
            "/api/meal-plans/entries",
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.Recipe,
                ScheduledDate: date,
                Slot: MealSlot.Dinner,
                RecipeId: recipe.Id,
                TargetServings: 4
            )
        );
        mealResponse.EnsureSuccessStatusCode();
        var listResponse = await _client.PostAsJsonAsync(
            "/api/shopping-lists",
            new CreateShoppingListRequest("Starter supplies")
        );
        listResponse.EnsureSuccessStatusCode();
        var list = await listResponse.Content.ReadFromJsonAsync<ShoppingListDetailDto>();
        Assert.IsNotNull(list);
        var generatedResponse = await _client.PostAsJsonAsync(
            $"/api/shopping-lists/{list.Id}/generate/meal-plan",
            new GenerateFromMealPlanRequest(date, date, IncludeOnDeck: false)
        );
        generatedResponse.EnsureSuccessStatusCode();
        var generated =
            await generatedResponse.Content.ReadFromJsonAsync<ShoppingListGenerationResultDto>();
        Assert.IsNotNull(generated);
        var shoppingFlour = generated.ShoppingList.ActiveItems.Single();
        var metric = CulinaryMath.ScaleAndConvert(
            shoppingFlour.Quantity,
            shoppingFlour.MaxQuantity,
            shoppingFlour.Unit,
            shoppingFlour.CustomUnitName,
            1,
            UnitSystem.Metric
        );
        Assert.AreEqual(708.7375m, metric.Quantity);
    }

    [TestMethod]
    public async Task InvalidAlternativesAndStepAmounts_ReturnBadRequest()
    {
        var valid = Starter();
        var ingredient = valid.Ingredients![0];
        var invalidRequests = new[]
        {
            valid with
            {
                Ingredients =
                [
                    ingredient with
                    {
                        AlternativeMeasurement = new(0, MeasurementUnit.G),
                    },
                ],
            },
            valid with
            {
                Ingredients = [ingredient with { Quantity = null }],
            },
            valid with
            {
                Ingredients = [ingredient with { AlternativeMeasurement = null }],
            },
            valid with
            {
                Steps =
                [
                    new RecipeStepInput(
                        Instruction: "Mix.",
                        IngredientAmounts: [new(9, new(1, MeasurementUnit.Cup))]
                    ),
                ],
            },
            valid with
            {
                Steps =
                [
                    new RecipeStepInput(
                        Instruction: "Mix.",
                        IngredientAmounts: [new(0, new(2, MeasurementUnit.Cup, 1))]
                    ),
                ],
            },
            valid with
            {
                Steps =
                [
                    new RecipeStepInput(
                        Instruction: "Mix.",
                        IngredientAmounts:
                        [
                            new(0, new(1, MeasurementUnit.Cup)),
                            new(0, new(1, MeasurementUnit.Cup)),
                        ]
                    ),
                ],
            },
        };
        foreach (var request in invalidRequests)
        {
            var response = await _client.PostAsJsonAsync("/api/recipes", request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }
}

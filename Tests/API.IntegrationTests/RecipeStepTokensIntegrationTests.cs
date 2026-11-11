using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using App.Core.Authentication;
using App.Core.Recipes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.API.IntegrationTests;

[TestClass]
public sealed class RecipeStepTokensIntegrationTests
{
    private const string RouxStepWithTokens =
        "Melt ||{\"ingredient\": \"butter\", \"amount\": 0.5, \"unit\": \"cup\"}||, whisk in ||{\"ingredient\": \"flour\", \"amount\": 2, \"unit\": \"cup\"}||, then simmer for ||{\"type\": \"timer\", \"seconds\": 300, \"label\": \"Simmer roux\"}||.";

    private AppWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _factory = new AppWebApplicationFactory();
        _client = _factory.CreateClient();
        var response = await _client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest("tokens@example.com", "stepuser", "Password123!")
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
            Steps: [new RecipeStepInput(Instruction: RouxStepWithTokens, IngredientIndices: [0, 1])]
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
    public async Task CreateAndGet_RoundTripsStepTokensAndDerivesIngredients()
    {
        var created = await CreateAsync(RouxRecipe());

        var json = await _client.GetStringAsync($"/api/recipes/{created.Id}");
        var recipe = JsonSerializer.Deserialize<RecipeDetailDto>(json, JsonSerializerOptions.Web);
        Assert.IsNotNull(recipe);

        var butter = recipe.Ingredients.Single(i => i.DisplayName == "butter");
        var flour = recipe.Ingredients.Single(i => i.DisplayName == "flour");
        var step = recipe.Steps.Single();
        Assert.AreEqual(RouxStepWithTokens, step.Instruction);

        var tokens = StepInstructionParser.ExtractTokens(step.Instruction);
        Assert.AreEqual(3, tokens.Count);

        var butterToken = tokens[0] as StepIngredientToken;
        Assert.IsNotNull(butterToken);
        Assert.AreEqual("butter", butterToken.Ingredient);
        Assert.AreEqual(0.5m, butterToken.Amount);
        Assert.AreEqual(MeasurementUnit.Cup, butterToken.Unit);

        var flourToken = tokens[1] as StepIngredientToken;
        Assert.IsNotNull(flourToken);
        Assert.AreEqual("flour", flourToken.Ingredient);
        Assert.AreEqual(2m, flourToken.Amount);

        var timerToken = tokens[2] as StepTimerToken;
        Assert.IsNotNull(timerToken);
        Assert.AreEqual(300, timerToken.Seconds);
        Assert.AreEqual("Simmer roux", timerToken.Label);

        CollectionAssert.AreEquivalent(new[] { butter.Id, flour.Id }, step.IngredientIds.ToArray());
    }

    [TestMethod]
    public async Task Update_PreservesTokensAndPlaintextConversion()
    {
        var created = await CreateAsync(RouxRecipe());
        const string updatedInstruction =
            "Cook ||{\"ingredient\": \"butter\", \"amount\": 1, \"unit\": \"cup\"}|| until nutty.";
        var update = RouxRecipe() with
        {
            Steps =
            [
                new RecipeStepInput(Instruction: updatedInstruction, IngredientIndices: [0]),
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
        Assert.AreEqual(2, recipe.Steps.Count);
        Assert.AreEqual(updatedInstruction, recipe.Steps[0].Instruction);

        var plainText = StepInstructionParser.ToPlainText(recipe.Steps[0].Instruction);
        Assert.AreEqual("Cook 1 cup butter until nutty.", plainText);
    }

    [TestMethod]
    public async Task StepTokens_PancakePerItemDisplayAmount_RoundTripsProperly()
    {
        const string pancakeStep =
            "Top each pancake with ||{\"ingredient\": \"butter\", \"unit\": \"teaspoon\", \"amount\": 4, \"displayAmount\": 1}||.";
        var pancakeRecipe = new UpsertRecipeRequest(
            Title: "Fluffy Pancakes",
            Ingredients:
            [
                new RecipeIngredientInput(
                    Quantity: 4,
                    Unit: MeasurementUnit.Tsp,
                    DisplayName: "butter"
                ),
            ],
            Steps: [new RecipeStepInput(Instruction: pancakeStep, IngredientIndices: [0])]
        );

        var created = await CreateAsync(pancakeRecipe);
        var step = created.Steps.Single();
        var token =
            StepInstructionParser.ExtractTokens(step.Instruction).Single() as StepIngredientToken;

        Assert.IsNotNull(token);
        Assert.AreEqual("butter", token.Ingredient);
        Assert.AreEqual(4m, token.Amount);
        Assert.AreEqual(1m, token.DisplayAmount);
        Assert.AreEqual(MeasurementUnit.Tsp, token.Unit);
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
    public async Task PublicShare_IncludesStepTokens()
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
        var step = shared.Steps.Single();
        Assert.AreEqual(RouxStepWithTokens, step.Instruction);
        var tokens = StepInstructionParser.ExtractTokens(step.Instruction);
        Assert.AreEqual(3, tokens.Count);
    }
}

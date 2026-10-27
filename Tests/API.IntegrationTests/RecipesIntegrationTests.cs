using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using App.Core.Authentication;
using App.Core.FoodCatalog;
using App.Core.Recipes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.API.IntegrationTests;

[TestClass]
public sealed class RecipesIntegrationTests
{
    private AppWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    [TestInitialize]
    public void Setup()
    {
        _factory = new AppWebApplicationFactory();
        _client = _factory.CreateClient();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static async Task<AuthResponse> RegisterAndAuthenticateAsync(
        HttpClient client,
        string email,
        string username
    )
    {
        var register = new RegisterRequest(email, username, "Password123!");
        var response = await client.PostAsJsonAsync("/api/auth/register", register);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.IsNotNull(auth);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            auth.Token
        );
        return auth;
    }

    [TestMethod]
    public async Task CreateGetAndListRecipe_WithIngredientsAndSteps_Succeeds()
    {
        await RegisterAndAuthenticateAsync(_client, "chef1@example.com", "chef1");

        var createReq = new UpsertRecipeRequest(
            Title: "Crispy Garlic Roast Chicken",
            Description: "A family favorite roast chicken with crispy skin and garlic herb butter.",
            Status: RecipeStatus.Published,
            BaseServings: 4m,
            YieldDisplay: "4 servings",
            PrepTimeMinutes: 20,
            CookTimeMinutes: 50,
            TotalTimeMinutes: null, // should auto-calculate to 70
            Difficulty: RecipeDifficulty.Medium,
            SourceUrl: "https://example.com/roast-chicken",
            SourceName: "Mom's Kitchen",
            SourceAuthor: "Julia Child",
            SourceBookTitle: "The Art of French Cooking",
            SourceBookPage: 124,
            Ingredients:
            [
                new RecipeIngredientInput(
                    SectionName: "Main",
                    Quantity: 1m,
                    MaxQuantity: null,
                    Unit: MeasurementUnit.Whole,
                    CustomUnitName: null,
                    FoodItemId: null,
                    LinkedRecipeId: null,
                    DisplayName: "Whole Chicken",
                    PreparationNote: "patted dry",
                    IsOptional: false
                ),
                new RecipeIngredientInput(
                    SectionName: "Herb Butter",
                    Quantity: 4m,
                    MaxQuantity: null,
                    Unit: MeasurementUnit.Tbsp,
                    CustomUnitName: null,
                    FoodItemId: null,
                    LinkedRecipeId: null,
                    DisplayName: "Unsalted Butter",
                    PreparationNote: "softened",
                    IsOptional: false
                ),
                new RecipeIngredientInput(
                    SectionName: "Herb Butter",
                    Quantity: 4m,
                    MaxQuantity: 6m,
                    Unit: MeasurementUnit.Clove,
                    CustomUnitName: null,
                    FoodItemId: null,
                    LinkedRecipeId: null,
                    DisplayName: "Garlic",
                    PreparationNote: "minced",
                    IsOptional: false
                ),
            ],
            Steps:
            [
                new RecipeStepInput(
                    SectionName: "Prep",
                    Instruction: "Mix softened butter with minced garlic.",
                    IngredientIndices: [1, 2],
                    Timers: []
                ),
                new RecipeStepInput(
                    SectionName: "Roast",
                    Instruction: "Rub butter under skin and roast chicken at 425F.",
                    IngredientIndices: [0],
                    Timers:
                    [
                        new RecipeStepTimerInput(DurationSeconds: 3000, Label: "Roast in oven"),
                    ]
                ),
            ]
        );

        var createRes = await _client.PostAsJsonAsync("/api/recipes", createReq);
        Assert.AreEqual(HttpStatusCode.Created, createRes.StatusCode);

        var created = await createRes.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(created);
        Assert.AreEqual("Crispy Garlic Roast Chicken", created.Title);
        Assert.AreEqual("crispy-garlic-roast-chicken", created.Slug);
        Assert.AreEqual(70, created.TotalTimeMinutes);
        Assert.AreEqual(3, created.Ingredients.Count);
        Assert.AreEqual(2, created.Steps.Count);
        Assert.AreEqual(1, created.Steps[1].Timers.Count);
        Assert.AreEqual("Roast in oven", created.Steps[1].Timers[0].Label);

        // Verify ingredient reference indices in step 0 mapped to ingredient IDs
        Assert.AreEqual(2, created.Steps[0].IngredientIds.Count);
        Assert.AreEqual(created.Ingredients[1].Id, created.Steps[0].IngredientIds[0]);
        Assert.AreEqual(created.Ingredients[2].Id, created.Steps[0].IngredientIds[1]);

        // List endpoint returns summary
        var listRes = await _client.GetAsync("/api/recipes?search=garlic");
        Assert.AreEqual(HttpStatusCode.OK, listRes.StatusCode);

        var list = await listRes.Content.ReadFromJsonAsync<List<RecipeSummaryDto>>();
        Assert.IsNotNull(list);
        Assert.IsTrue(list.Any(r => r.Id == created.Id));
    }

    [TestMethod]
    public async Task UpdateAndDeleteRecipe_Succeeds()
    {
        await RegisterAndAuthenticateAsync(_client, "chef2@example.com", "chef2");

        var createReq = new UpsertRecipeRequest(
            Title: "Simple Toast",
            Description: "Quick breakfast",
            Ingredients:
            [
                new RecipeIngredientInput(
                    SectionName: null,
                    Quantity: 2m,
                    MaxQuantity: null,
                    Unit: MeasurementUnit.Slice,
                    CustomUnitName: null,
                    FoodItemId: null,
                    LinkedRecipeId: null,
                    DisplayName: "Bread"
                ),
            ],
            Steps:
            [
                new RecipeStepInput(
                    SectionName: null,
                    Instruction: "Toast bread in toaster.",
                    IngredientIndices: [0]
                ),
            ]
        );

        var created = await (
            await _client.PostAsJsonAsync("/api/recipes", createReq)
        ).Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(created);

        // Update with additional butter ingredient
        var updateReq = new UpsertRecipeRequest(
            Title: "Buttered Toast",
            Description: "Delicious breakfast with butter",
            Ingredients:
            [
                new RecipeIngredientInput(
                    SectionName: null,
                    Quantity: 2m,
                    MaxQuantity: null,
                    Unit: MeasurementUnit.Slice,
                    CustomUnitName: null,
                    FoodItemId: null,
                    LinkedRecipeId: null,
                    DisplayName: "Bread"
                ),
                new RecipeIngredientInput(
                    SectionName: null,
                    Quantity: 1m,
                    MaxQuantity: null,
                    Unit: MeasurementUnit.Tbsp,
                    CustomUnitName: null,
                    FoodItemId: null,
                    LinkedRecipeId: null,
                    DisplayName: "Butter"
                ),
            ],
            Steps:
            [
                new RecipeStepInput(
                    SectionName: null,
                    Instruction: "Toast bread, then spread butter.",
                    IngredientIndices: [0, 1]
                ),
            ]
        );

        var updateRes = await _client.PutAsJsonAsync($"/api/recipes/{created.Id}", updateReq);
        Assert.AreEqual(HttpStatusCode.OK, updateRes.StatusCode);

        var updated = await updateRes.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(updated);
        Assert.AreEqual("Buttered Toast", updated.Title);
        Assert.AreEqual("buttered-toast", updated.Slug);
        Assert.AreEqual(2, updated.Ingredients.Count);

        // Delete
        var delRes = await _client.DeleteAsync($"/api/recipes/{created.Id}");
        Assert.AreEqual(HttpStatusCode.NoContent, delRes.StatusCode);

        var getRes = await _client.GetAsync($"/api/recipes/{created.Id}");
        Assert.AreEqual(HttpStatusCode.NotFound, getRes.StatusCode);
    }

    [TestMethod]
    public async Task SubRecipe_InlinePreview_AndCyclePrevention_WorksCorrectly()
    {
        await RegisterAndAuthenticateAsync(_client, "dag_chef@example.com", "dagchef");

        // 1. Create sub-recipe A: "Pickled Onions"
        var subRecipeAReq = new UpsertRecipeRequest(
            Title: "Quick Pickled Onions",
            Description: "Tangy pickled red onions",
            BaseServings: 8m,
            Ingredients:
            [
                new RecipeIngredientInput(
                    SectionName: null,
                    Quantity: 1m,
                    MaxQuantity: null,
                    Unit: MeasurementUnit.Whole,
                    CustomUnitName: null,
                    FoodItemId: null,
                    LinkedRecipeId: null,
                    DisplayName: "Red Onion"
                ),
            ],
            Steps:
            [
                new RecipeStepInput(
                    SectionName: null,
                    Instruction: "Slice onion and pickle in vinegar.",
                    IngredientIndices: [0]
                ),
            ]
        );
        var subRecipeA = await (
            await _client.PostAsJsonAsync("/api/recipes", subRecipeAReq)
        ).Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(subRecipeA);

        // 2. Create parent recipe B: "Braised Pork Tacos" linking to sub-recipe A
        var parentBReq = new UpsertRecipeRequest(
            Title: "Braised Pork Tacos",
            Description: "Slow-cooked pork tacos with pickled onions",
            BaseServings: 4m,
            Ingredients:
            [
                new RecipeIngredientInput(
                    SectionName: null,
                    Quantity: 2m,
                    MaxQuantity: null,
                    Unit: MeasurementUnit.Lb,
                    CustomUnitName: null,
                    FoodItemId: null,
                    LinkedRecipeId: null,
                    DisplayName: "Pork Shoulder"
                ),
                new RecipeIngredientInput(
                    SectionName: "Toppings",
                    Quantity: 0.5m,
                    MaxQuantity: null,
                    Unit: MeasurementUnit.Whole,
                    CustomUnitName: null,
                    FoodItemId: null,
                    LinkedRecipeId: subRecipeA.Id,
                    DisplayName: "Pickled Onions"
                ),
            ],
            Steps:
            [
                new RecipeStepInput(
                    SectionName: null,
                    Instruction: "Serve pork with pickled onions.",
                    IngredientIndices: [0, 1]
                ),
            ]
        );
        var parentB = await (
            await _client.PostAsJsonAsync("/api/recipes", parentBReq)
        ).Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(parentB);

        // Verify parent recipe B includes sub-recipe preview
        Assert.AreEqual(1, parentB.LinkedSubRecipes.Count);
        Assert.AreEqual(subRecipeA.Id, parentB.LinkedSubRecipes[0].Id);
        Assert.AreEqual("Quick Pickled Onions", parentB.LinkedSubRecipes[0].Title);

        // 3. Test self-linking rejection (400 Bad Request)
        var selfLinkReq = new UpsertRecipeRequest(
            Title: "Self Linked Recipe",
            Ingredients:
            [
                new RecipeIngredientInput(
                    SectionName: null,
                    Quantity: 1m,
                    MaxQuantity: null,
                    Unit: null,
                    CustomUnitName: null,
                    FoodItemId: null,
                    LinkedRecipeId: parentB.Id,
                    DisplayName: "Self"
                ),
            ],
            Steps: [new RecipeStepInput(SectionName: null, Instruction: "Step 1")]
        );
        var selfLinkRes = await _client.PutAsJsonAsync($"/api/recipes/{parentB.Id}", selfLinkReq);
        Assert.AreEqual(HttpStatusCode.BadRequest, selfLinkRes.StatusCode);

        // 4. Test cycle rejection (A -> B -> A) -> 409 Conflict
        var cycleReq = new UpsertRecipeRequest(
            Title: "Quick Pickled Onions",
            Ingredients:
            [
                new RecipeIngredientInput(
                    SectionName: null,
                    Quantity: 1m,
                    MaxQuantity: null,
                    Unit: null,
                    CustomUnitName: null,
                    FoodItemId: null,
                    LinkedRecipeId: parentB.Id, // Linking A back to B creates a cycle!
                    DisplayName: "Tacos inside onions"
                ),
            ],
            Steps: [new RecipeStepInput(SectionName: null, Instruction: "Step 1")]
        );
        var cycleRes = await _client.PutAsJsonAsync($"/api/recipes/{subRecipeA.Id}", cycleReq);
        Assert.AreEqual(HttpStatusCode.Conflict, cycleRes.StatusCode);

        // 5. Test delete protection while linked (409 Conflict)
        var deleteSubRes = await _client.DeleteAsync($"/api/recipes/{subRecipeA.Id}");
        Assert.AreEqual(HttpStatusCode.Conflict, deleteSubRes.StatusCode);
    }

    [TestMethod]
    public async Task GlobalHouseholdTenancy_RecipesCollaborativeAcrossUsers()
    {
        await RegisterAndAuthenticateAsync(_client, "member1@example.com", "member1");

        var createReq = new UpsertRecipeRequest(
            Title: "Household Lasagna",
            Description: "Shared family recipe",
            Ingredients:
            [
                new RecipeIngredientInput(
                    SectionName: null,
                    Quantity: 1m,
                    MaxQuantity: null,
                    Unit: MeasurementUnit.Lb,
                    CustomUnitName: null,
                    FoodItemId: null,
                    LinkedRecipeId: null,
                    DisplayName: "Lasagna Noodles"
                ),
            ],
            Steps: [new RecipeStepInput(SectionName: null, Instruction: "Assemble layers")]
        );
        var created = await (
            await _client.PostAsJsonAsync("/api/recipes", createReq)
        ).Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(created);

        // Second family member logs in
        using var client2 = _factory.CreateClient();
        await RegisterAndAuthenticateAsync(client2, "member2@example.com", "member2");

        // Member 2 can view and update the recipe
        var getRes = await client2.GetAsync($"/api/recipes/{created.Id}");
        Assert.AreEqual(HttpStatusCode.OK, getRes.StatusCode);

        var updateReq = new UpsertRecipeRequest(
            Title: "Household Lasagna (Extra Cheese)",
            Description: "Updated by member 2",
            Ingredients: created
                .Ingredients.Select(i => new RecipeIngredientInput(
                    i.SectionName,
                    i.Quantity,
                    i.MaxQuantity,
                    i.Unit,
                    i.CustomUnitName,
                    i.FoodItemId,
                    i.LinkedRecipeId,
                    i.DisplayName,
                    i.PreparationNote,
                    i.IsOptional,
                    i.OriginalRawText
                ))
                .ToList(),
            Steps: created
                .Steps.Select(s => new RecipeStepInput(s.SectionName, s.Instruction, [0], []))
                .ToList()
        );

        var updateRes = await client2.PutAsJsonAsync($"/api/recipes/{created.Id}", updateReq);
        Assert.AreEqual(HttpStatusCode.OK, updateRes.StatusCode);

        var updated = await updateRes.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(updated);
        Assert.AreEqual("Household Lasagna (Extra Cheese)", updated.Title);
    }

    [TestMethod]
    public async Task UpdateRecipe_WithTagsAndCollections_SucceedsWithoutCollectionModifiedError()
    {
        await RegisterAndAuthenticateAsync(_client, "edit_test_chef@example.com", "edittestchef");

        // Fetch tags and collections
        var tags = await (await _client.GetAsync("/api/tags")).Content.ReadFromJsonAsync<
            IReadOnlyList<TagDto>
        >();
        Assert.IsNotNull(tags);
        Assert.IsTrue(tags.Count >= 3);

        var collections = await (
            await _client.GetAsync("/api/collections")
        ).Content.ReadFromJsonAsync<IReadOnlyList<CollectionDto>>();
        Assert.IsNotNull(collections);
        Assert.IsTrue(collections.Count >= 2);

        var initialTagIds = tags.Take(3).Select(t => t.Id).ToList();
        var initialCollectionIds = collections.Take(2).Select(c => c.Id).ToList();

        // 1. Create recipe with multiple tags, multiple collections, 2 ingredients, 1 step with timer
        var createReq = new UpsertRecipeRequest(
            Title: "Crispy Cast-Iron Gluten-Free Cornbread",
            Description: "Initial description",
            BaseServings: 8m,
            YieldDisplay: "8 wedges",
            PrepTimeMinutes: 10,
            CookTimeMinutes: 25,
            TotalTimeMinutes: 35,
            Difficulty: RecipeDifficulty.Easy,
            TagIds: initialTagIds,
            CollectionIds: initialCollectionIds,
            Ingredients:
            [
                new RecipeIngredientInput(
                    SectionName: "Dry Ingredients",
                    Quantity: 1.5m,
                    Unit: MeasurementUnit.Cup,
                    DisplayName: "yellow cornmeal"
                ),
                new RecipeIngredientInput(
                    SectionName: "Dry Ingredients",
                    Quantity: 1m,
                    Unit: MeasurementUnit.Cup,
                    DisplayName: "gluten-free flour"
                ),
            ],
            Steps:
            [
                new RecipeStepInput(
                    SectionName: "Bake",
                    Instruction: "Bake in hot skillet.",
                    IngredientIndices: [0, 1],
                    Timers: [new RecipeStepTimerInput(1500, "Bake timer")]
                ),
            ]
        );

        var createRes = await _client.PostAsJsonAsync("/api/recipes", createReq);
        Assert.AreEqual(HttpStatusCode.Created, createRes.StatusCode);
        var created = await createRes.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(created);
        Assert.AreEqual(3, created.Tags.Count);
        Assert.AreEqual(2, created.Collections.Count);
        Assert.AreEqual(2, created.Ingredients.Count);

        // 2. Update the recipe: change title, keep 2 tags and replace 1 tag, keep 1 collection and remove 1 collection, add an ingredient
        var updatedTagIds = new List<Guid> { initialTagIds[0], initialTagIds[1] };
        if (tags.Count > 3)
        {
            updatedTagIds.Add(tags[3].Id);
        }
        var updatedCollectionIds = new List<Guid> { initialCollectionIds[0] };

        var updateReq = new UpsertRecipeRequest(
            Title: "Crispy Cast-Iron Gluten-Free Cornbreadd",
            Description: "Updated description with tender buttermilk crumb.",
            BaseServings: 8m,
            YieldDisplay: "8 wedges",
            PrepTimeMinutes: 10,
            CookTimeMinutes: 25,
            TotalTimeMinutes: 35,
            Difficulty: RecipeDifficulty.Easy,
            TagIds: updatedTagIds,
            CollectionIds: updatedCollectionIds,
            Ingredients:
            [
                new RecipeIngredientInput(
                    SectionName: "Dry Ingredients",
                    Quantity: 1.5m,
                    Unit: MeasurementUnit.Cup,
                    DisplayName: "yellow cornmeal"
                ),
                new RecipeIngredientInput(
                    SectionName: "Dry Ingredients",
                    Quantity: 1m,
                    Unit: MeasurementUnit.Cup,
                    DisplayName: "gluten-free flour"
                ),
                new RecipeIngredientInput(
                    SectionName: "Wet Ingredients",
                    Quantity: 1.25m,
                    Unit: MeasurementUnit.Cup,
                    DisplayName: "buttermilk"
                ),
            ],
            Steps:
            [
                new RecipeStepInput(
                    SectionName: "Bake",
                    Instruction: "Bake in hot skillet until crust is deep golden.",
                    IngredientIndices: [0, 1, 2],
                    Timers: [new RecipeStepTimerInput(1500, "Bake timer")]
                ),
            ]
        );

        var updateRes = await _client.PutAsJsonAsync($"/api/recipes/{created.Id}", updateReq);
        Assert.AreEqual(HttpStatusCode.OK, updateRes.StatusCode);

        var updated = await updateRes.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(updated);
        Assert.AreEqual("Crispy Cast-Iron Gluten-Free Cornbreadd", updated.Title);
        Assert.AreEqual("crispy-cast-iron-gluten-free-cornbreadd", updated.Slug);
        Assert.AreEqual(3, updated.Ingredients.Count);
        Assert.AreEqual(updatedTagIds.Count, updated.Tags.Count);
        Assert.AreEqual(1, updated.Collections.Count);
    }
}

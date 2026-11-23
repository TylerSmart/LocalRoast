using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using App.Core.Authentication;
using App.Core.Common;
using App.Core.FoodCatalog;
using App.Core.Recipes;
using App.Core.Shopping;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.API.IntegrationTests;

[TestClass]
public sealed class FoodCatalogIntegrationTests
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
    public async Task GetFoodItems_StartsEmpty_AndSupportsPantryStaplesAndAliasSearch()
    {
        await RegisterAndAuthenticateAsync(_client, "staples_user@example.com", "staplesuser");

        var initialResponse = await _client.GetAsync("/api/food-items");
        Assert.AreEqual(HttpStatusCode.OK, initialResponse.StatusCode);
        var initialItems = await initialResponse.Content.ReadFromJsonAsync<List<FoodItemDto>>();
        Assert.IsNotNull(initialItems);
        Assert.AreEqual(0, initialItems.Count);

        await _client.PostAsJsonAsync(
            "/api/food-items",
            new CreateFoodItemRequest("Kosher Salt", IsPantryStaple: true, Aliases: ["Salt"])
        );
        await _client.PostAsJsonAsync(
            "/api/food-items",
            new CreateFoodItemRequest(
                "Green Onion",
                IsPantryStaple: false,
                Aliases: ["Scallion", "Spring Onion"]
            )
        );

        var allItemsResponse = await _client.GetAsync("/api/food-items");
        Assert.AreEqual(HttpStatusCode.OK, allItemsResponse.StatusCode);

        var allItems = await allItemsResponse.Content.ReadFromJsonAsync<List<FoodItemDto>>();
        Assert.IsNotNull(allItems);
        Assert.AreEqual(2, allItems.Count);
        Assert.IsTrue(allItems.Any(f => f.Name == "Kosher Salt" && f.IsPantryStaple));

        // Search by alias "scallion" -> should find "Green Onion"
        var aliasSearchResponse = await _client.GetAsync("/api/food-items?search=scallion");
        Assert.AreEqual(HttpStatusCode.OK, aliasSearchResponse.StatusCode);

        var matchedItems = await aliasSearchResponse.Content.ReadFromJsonAsync<List<FoodItemDto>>();
        Assert.IsNotNull(matchedItems);
        Assert.IsTrue(matchedItems.Any(f => f.Name == "Green Onion"));
    }

    [TestMethod]
    public async Task CreateUpdateAndToggleFoodItem_Succeeds()
    {
        await RegisterAndAuthenticateAsync(_client, "catalog_user@example.com", "cataloguser");

        // Create
        var createReq = new CreateFoodItemRequest(
            Name: "Avocado",
            IsPantryStaple: false,
            Aliases: ["Hass Avocado", "Alligator Pear"]
        );
        var createRes = await _client.PostAsJsonAsync("/api/food-items", createReq);
        Assert.AreEqual(HttpStatusCode.Created, createRes.StatusCode);

        var created = await createRes.Content.ReadFromJsonAsync<FoodItemDto>();
        Assert.IsNotNull(created);
        Assert.AreEqual("Avocado", created.Name);
        Assert.IsFalse(created.IsPantryStaple);
        Assert.AreEqual(2, created.Aliases.Count);

        // Update & toggle pantry staple
        var updateReq = new UpdateFoodItemRequest(
            Name: "Avocado",
            IsPantryStaple: true,
            Aliases: ["Hass Avocado"]
        );
        var updateRes = await _client.PutAsJsonAsync($"/api/food-items/{created.Id}", updateReq);
        Assert.AreEqual(HttpStatusCode.OK, updateRes.StatusCode);

        var updated = await updateRes.Content.ReadFromJsonAsync<FoodItemDto>();
        Assert.IsNotNull(updated);
        Assert.IsTrue(updated.IsPantryStaple);
        Assert.AreEqual(1, updated.Aliases.Count);
    }

    [TestMethod]
    public async Task FoodItemCategories_AutoDetectOverrideFilter_AndFlowIntoShoppingItems()
    {
        await RegisterAndAuthenticateAsync(_client, "category_user@example.com", "categoryuser");

        var autoRes = await _client.PostAsJsonAsync(
            "/api/food-items",
            new CreateFoodItemRequest("Whole Milk")
        );
        Assert.AreEqual(HttpStatusCode.Created, autoRes.StatusCode);
        var milk = await autoRes.Content.ReadFromJsonAsync<FoodItemDto>();
        Assert.IsNotNull(milk);
        Assert.AreEqual(FoodCategory.DairyAndEggs, milk.Category);

        var explicitRes = await _client.PostAsJsonAsync(
            "/api/food-items",
            new CreateFoodItemRequest("Mystery Crunch", Category: FoodCategory.Snacks)
        );
        var crunch = await explicitRes.Content.ReadFromJsonAsync<FoodItemDto>();
        Assert.IsNotNull(crunch);
        Assert.AreEqual(FoodCategory.Snacks, crunch.Category);

        // Moving milk to "Refrigerated" persists; omitting the category on update keeps it.
        var movedRes = await _client.PutAsJsonAsync(
            $"/api/food-items/{milk.Id}",
            new UpdateFoodItemRequest("Whole Milk", Category: FoodCategory.Refrigerated)
        );
        Assert.AreEqual(HttpStatusCode.OK, movedRes.StatusCode);
        var keptRes = await _client.PutAsJsonAsync(
            $"/api/food-items/{milk.Id}",
            new UpdateFoodItemRequest("Whole Milk", IsPantryStaple: true)
        );
        var kept = await keptRes.Content.ReadFromJsonAsync<FoodItemDto>();
        Assert.IsNotNull(kept);
        Assert.AreEqual(FoodCategory.Refrigerated, kept.Category);

        var filtered = await _client.GetFromJsonAsync<List<FoodItemDto>>(
            "/api/food-items?category=Refrigerated"
        );
        Assert.IsNotNull(filtered);
        Assert.AreEqual(1, filtered.Count);
        Assert.AreEqual("Whole Milk", filtered[0].Name);

        var listRes = await _client.PostAsJsonAsync(
            "/api/shopping-lists",
            new CreateShoppingListRequest("Category Trip")
        );
        var list = await listRes.Content.ReadFromJsonAsync<ShoppingListDetailDto>();
        Assert.IsNotNull(list);

        var linkedRes = await _client.PostAsJsonAsync(
            $"/api/shopping-lists/{list.Id}/items",
            new CreateShoppingListItemRequest("Milk", FoodItemId: milk.Id)
        );
        var linked = await linkedRes.Content.ReadFromJsonAsync<ShoppingListItemDto>();
        Assert.IsNotNull(linked);
        Assert.AreEqual(FoodCategory.Refrigerated, linked.Category);

        var freeTextRes = await _client.PostAsJsonAsync(
            $"/api/shopping-lists/{list.Id}/items",
            new CreateShoppingListItemRequest("Bananas")
        );
        var freeText = await freeTextRes.Content.ReadFromJsonAsync<ShoppingListItemDto>();
        Assert.IsNotNull(freeText);
        Assert.AreEqual(FoodCategory.Produce, freeText.Category);
    }

    [TestMethod]
    public async Task CreateFoodItem_WithDuplicateNameOrAlias_ReturnsConflict()
    {
        await RegisterAndAuthenticateAsync(_client, "conflict_user@example.com", "conflictuser");

        var seedRes = await _client.PostAsJsonAsync(
            "/api/food-items",
            new CreateFoodItemRequest(
                "Green Onion",
                IsPantryStaple: false,
                Aliases: ["Scallion", "Spring Onion"]
            )
        );
        Assert.AreEqual(HttpStatusCode.Created, seedRes.StatusCode);

        // Duplicate of existing item
        var dupReq = new CreateFoodItemRequest("Green Onion");
        var dupRes = await _client.PostAsJsonAsync("/api/food-items", dupReq);
        Assert.AreEqual(HttpStatusCode.Conflict, dupRes.StatusCode);

        // Collision with existing alias "scallion"
        var aliasCollisionReq = new CreateFoodItemRequest("Scallion");
        var aliasCollisionRes = await _client.PostAsJsonAsync("/api/food-items", aliasCollisionReq);
        Assert.AreEqual(HttpStatusCode.Conflict, aliasCollisionRes.StatusCode);
    }

    [TestMethod]
    public async Task DeleteFoodItem_ReferencedByRecipe_ReturnsConflict()
    {
        await RegisterAndAuthenticateAsync(_client, "del_user@example.com", "deluser");

        var createFood = await (
            await _client.PostAsJsonAsync(
                "/api/food-items",
                new CreateFoodItemRequest("Special Honey")
            )
        ).Content.ReadFromJsonAsync<FoodItemDto>();
        Assert.IsNotNull(createFood);

        // Create recipe referencing this food item
        var recipeReq = new UpsertRecipeRequest(
            Title: "Honey Glazed Carrots",
            Description: "Delicious carrots",
            Ingredients:
            [
                new RecipeIngredientInput(
                    SectionName: null,
                    Quantity: 2m,
                    MaxQuantity: null,
                    Unit: MeasurementUnit.Tbsp,
                    CustomUnitName: null,
                    FoodItemId: createFood.Id,
                    LinkedRecipeId: null,
                    DisplayName: "Special Honey",
                    PreparationNote: null
                ),
            ],
            Steps:
            [
                new RecipeStepInput(SectionName: null, Instruction: "Drizzle honey over carrots."),
            ]
        );
        var recipeRes = await _client.PostAsJsonAsync("/api/recipes", recipeReq);
        Assert.AreEqual(HttpStatusCode.Created, recipeRes.StatusCode);

        // Try deleting food item -> should return 409 Conflict
        var deleteRes = await _client.DeleteAsync($"/api/food-items/{createFood.Id}");
        Assert.AreEqual(HttpStatusCode.Conflict, deleteRes.StatusCode);
    }

    [TestMethod]
    public async Task GlobalHouseholdTenancy_ItemsVisibleAcrossUsers()
    {
        await RegisterAndAuthenticateAsync(_client, "user_a@example.com", "usera");

        var createFood = await (
            await _client.PostAsJsonAsync(
                "/api/food-items",
                new CreateFoodItemRequest("Smoked Paprika")
            )
        ).Content.ReadFromJsonAsync<FoodItemDto>();
        Assert.IsNotNull(createFood);

        // Other household user registers
        using var clientB = _factory.CreateClient();
        await RegisterAndAuthenticateAsync(clientB, "user_b@example.com", "userb");

        var getRes = await clientB.GetAsync($"/api/food-items/{createFood.Id}");
        Assert.AreEqual(HttpStatusCode.OK, getRes.StatusCode);

        var fetched = await getRes.Content.ReadFromJsonAsync<FoodItemDto>();
        Assert.IsNotNull(fetched);
        Assert.AreEqual("Smoked Paprika", fetched.Name);
    }

    [TestMethod]
    public async Task MergeFoodItems_ReturnsOk_AndRepointsReferences()
    {
        await RegisterAndAuthenticateAsync(_client, "merge_user@example.com", "mergeuser");

        // 1. Create target and source items
        var targetName = $"Unique Target {Guid.NewGuid():N}";
        var sourceName = $"Unique Source {Guid.NewGuid():N}";

        var targetRes = await _client.PostAsJsonAsync(
            "/api/food-items",
            new CreateFoodItemRequest(targetName)
        );
        Assert.AreEqual(HttpStatusCode.Created, targetRes.StatusCode);
        var target = await targetRes.Content.ReadFromJsonAsync<FoodItemDto>();
        Assert.IsNotNull(target);

        var sourceRes = await _client.PostAsJsonAsync(
            "/api/food-items",
            new CreateFoodItemRequest(sourceName)
        );
        Assert.AreEqual(HttpStatusCode.Created, sourceRes.StatusCode);
        var source = await sourceRes.Content.ReadFromJsonAsync<FoodItemDto>();
        Assert.IsNotNull(source);

        // 2. Perform merge
        var mergeReq = new MergeFoodItemsRequest(
            TargetFoodItemId: target.Id,
            SourceFoodItemIds: [source.Id],
            KeepSourceNamesAsAliases: true
        );
        var mergeRes = await _client.PostAsJsonAsync("/api/food-items/merge", mergeReq);
        Assert.AreEqual(HttpStatusCode.OK, mergeRes.StatusCode);

        var mergeResult = await mergeRes.Content.ReadFromJsonAsync<MergeFoodItemsResultDto>();
        Assert.IsNotNull(mergeResult);
        Assert.AreEqual(target.Id, mergeResult.TargetFoodItem.Id);
        Assert.AreEqual(1, mergeResult.MergedSourceCount);
        Assert.IsTrue(mergeResult.AddedAliases.Contains(sourceName));

        // 3. Verify source deleted (404)
        var getSourceRes = await _client.GetAsync($"/api/food-items/{source.Id}");
        Assert.AreEqual(HttpStatusCode.NotFound, getSourceRes.StatusCode);

        // 4. Verify target updated with alias
        var getTargetRes = await _client.GetAsync($"/api/food-items/{target.Id}");
        Assert.AreEqual(HttpStatusCode.OK, getTargetRes.StatusCode);
        var updatedTarget = await getTargetRes.Content.ReadFromJsonAsync<FoodItemDto>();
        Assert.IsNotNull(updatedTarget);
        Assert.IsTrue(updatedTarget.Aliases.Contains(sourceName));
    }

    [TestMethod]
    public async Task MergeFoodItems_Unauthorized_Returns401()
    {
        using var unauthClient = _factory.CreateClient();
        var mergeRes = await unauthClient.PostAsJsonAsync(
            "/api/food-items/merge",
            new MergeFoodItemsRequest(Guid.NewGuid(), [Guid.NewGuid()])
        );
        Assert.AreEqual(HttpStatusCode.Unauthorized, mergeRes.StatusCode);
    }

    [TestMethod]
    public async Task CreateFoodItem_WithDisallowedOrPlaceholderName_ReturnsBadRequest()
    {
        await RegisterAndAuthenticateAsync(
            _client,
            "invalid_food_user@example.com",
            "invalidfooduser"
        );

        var disallowedNames = new[] { "Null", "null", "none", "n/a", "undefined", "item", "--" };

        foreach (var disallowed in disallowedNames)
        {
            var res = await _client.PostAsJsonAsync(
                "/api/food-items",
                new CreateFoodItemRequest(disallowed)
            );
            Assert.AreEqual(
                HttpStatusCode.BadRequest,
                res.StatusCode,
                $"Food item name '{disallowed}' should be rejected as bad request."
            );
        }
    }

    [TestMethod]
    public async Task GetFoodItemRecipes_ReturnsReferencingRecipes_AndIngredientDisplayNames()
    {
        await RegisterAndAuthenticateAsync(_client, "usage_user@example.com", "usageuser");

        var createFood = await (
            await _client.PostAsJsonAsync(
                "/api/food-items",
                new CreateFoodItemRequest("Fresh Basil")
            )
        ).Content.ReadFromJsonAsync<FoodItemDto>();
        Assert.IsNotNull(createFood);

        var recipeReq = new UpsertRecipeRequest(
            Title: "Caprese Salad",
            Description: "Fresh salad",
            Ingredients:
            [
                new RecipeIngredientInput(
                    SectionName: null,
                    Quantity: 6m,
                    MaxQuantity: null,
                    Unit: MeasurementUnit.Custom,
                    CustomUnitName: "leaves",
                    FoodItemId: createFood.Id,
                    LinkedRecipeId: null,
                    DisplayName: "fresh basil leaves, torn",
                    PreparationNote: "torn"
                ),
            ],
            Steps:
            [
                new RecipeStepInput(SectionName: null, Instruction: "Scatter basil over tomatoes."),
            ]
        );
        var recipeRes = await _client.PostAsJsonAsync("/api/recipes", recipeReq);
        Assert.AreEqual(HttpStatusCode.Created, recipeRes.StatusCode);

        var usageRes = await _client.GetAsync($"/api/food-items/{createFood.Id}/recipes");
        Assert.AreEqual(HttpStatusCode.OK, usageRes.StatusCode);

        var usages = await usageRes.Content.ReadFromJsonAsync<List<FoodItemRecipeUsageDto>>();
        Assert.IsNotNull(usages);
        Assert.AreEqual(1, usages.Count);
        Assert.AreEqual("Caprese Salad", usages[0].RecipeTitle);
        Assert.IsTrue(usages[0].IngredientDisplayNames.Contains("fresh basil leaves, torn"));
    }

    [TestMethod]
    public async Task UnlinkFoodItemRecipes_ClearsIngredientReferences_AndAllowsNormalDeletion()
    {
        await RegisterAndAuthenticateAsync(_client, "unlink_user@example.com", "unlinkuser");

        var createFood = await (
            await _client.PostAsJsonAsync(
                "/api/food-items",
                new CreateFoodItemRequest("To Be Unlinked")
            )
        ).Content.ReadFromJsonAsync<FoodItemDto>();
        Assert.IsNotNull(createFood);

        var recipeReq = new UpsertRecipeRequest(
            Title: "Unlink Test Recipe",
            Description: "Unlink test",
            Ingredients:
            [
                new RecipeIngredientInput(
                    SectionName: null,
                    Quantity: 1m,
                    MaxQuantity: null,
                    Unit: MeasurementUnit.Pinch,
                    CustomUnitName: null,
                    FoodItemId: createFood.Id,
                    LinkedRecipeId: null,
                    DisplayName: "a pinch of something",
                    PreparationNote: null
                ),
            ],
            Steps: [new RecipeStepInput(null, "Mix.")]
        );
        var recipeRes = await _client.PostAsJsonAsync("/api/recipes", recipeReq);
        Assert.AreEqual(HttpStatusCode.Created, recipeRes.StatusCode);

        // Delete without unlinking fails with 409
        var deleteBlockedRes = await _client.DeleteAsync($"/api/food-items/{createFood.Id}");
        Assert.AreEqual(HttpStatusCode.Conflict, deleteBlockedRes.StatusCode);

        // Call unlink endpoint
        var unlinkRes = await _client.PostAsync(
            $"/api/food-items/{createFood.Id}/unlink-recipes",
            null
        );
        Assert.AreEqual(HttpStatusCode.OK, unlinkRes.StatusCode);

        var unlinkResult =
            await unlinkRes.Content.ReadFromJsonAsync<UnlinkFoodItemRecipesResultDto>();
        Assert.IsNotNull(unlinkResult);
        Assert.AreEqual(1, unlinkResult.UnlinkedRecipeCount);
        Assert.AreEqual(1, unlinkResult.UnlinkedIngredientCount);

        // Recipes usage is now empty
        var usageRes = await _client.GetAsync($"/api/food-items/{createFood.Id}/recipes");
        Assert.AreEqual(HttpStatusCode.OK, usageRes.StatusCode);
        var usages = await usageRes.Content.ReadFromJsonAsync<List<FoodItemRecipeUsageDto>>();
        Assert.IsNotNull(usages);
        Assert.AreEqual(0, usages.Count);

        // Now deletion succeeds with 204
        var deleteRes = await _client.DeleteAsync($"/api/food-items/{createFood.Id}");
        Assert.AreEqual(HttpStatusCode.NoContent, deleteRes.StatusCode);
    }

    [TestMethod]
    public async Task DeleteFoodItem_WithUnlinkFromRecipesTrue_SucceedsDirectly()
    {
        await RegisterAndAuthenticateAsync(_client, "forcedel_user@example.com", "forcedeluser");

        var createFood = await (
            await _client.PostAsJsonAsync(
                "/api/food-items",
                new CreateFoodItemRequest("Direct Force Delete")
            )
        ).Content.ReadFromJsonAsync<FoodItemDto>();
        Assert.IsNotNull(createFood);

        var recipeReq = new UpsertRecipeRequest(
            Title: "Force Delete Test Recipe",
            Description: "Force delete",
            Ingredients:
            [
                new RecipeIngredientInput(
                    SectionName: null,
                    Quantity: 1m,
                    MaxQuantity: null,
                    Unit: MeasurementUnit.Pinch,
                    CustomUnitName: null,
                    FoodItemId: createFood.Id,
                    LinkedRecipeId: null,
                    DisplayName: "mystery ingredient",
                    PreparationNote: null
                ),
            ],
            Steps: [new RecipeStepInput(null, "Cook.")]
        );
        var recipeRes = await _client.PostAsJsonAsync("/api/recipes", recipeReq);
        Assert.AreEqual(HttpStatusCode.Created, recipeRes.StatusCode);

        // Delete with unlinkFromRecipes=true
        var deleteRes = await _client.DeleteAsync(
            $"/api/food-items/{createFood.Id}?unlinkFromRecipes=true"
        );
        Assert.AreEqual(HttpStatusCode.NoContent, deleteRes.StatusCode);

        // Verify item is gone
        var getRes = await _client.GetAsync($"/api/food-items/{createFood.Id}");
        Assert.AreEqual(HttpStatusCode.NotFound, getRes.StatusCode);
    }

    [TestMethod]
    public async Task GetFoodItems_SupportsSortingByNameRecipeCountCreatedAtAndModifiedAt()
    {
        await RegisterAndAuthenticateAsync(_client, "sort_user@example.com", "sortuser");

        var betaRes = await _client.PostAsJsonAsync(
            "/api/food-items",
            new CreateFoodItemRequest("Beta Herb")
        );
        Assert.AreEqual(HttpStatusCode.Created, betaRes.StatusCode);
        var beta = await betaRes.Content.ReadFromJsonAsync<FoodItemDto>();
        Assert.IsNotNull(beta);

        await Task.Delay(25);

        var gammaRes = await _client.PostAsJsonAsync(
            "/api/food-items",
            new CreateFoodItemRequest("Gamma Spice")
        );
        Assert.AreEqual(HttpStatusCode.Created, gammaRes.StatusCode);
        var gamma = await gammaRes.Content.ReadFromJsonAsync<FoodItemDto>();
        Assert.IsNotNull(gamma);

        await Task.Delay(25);

        var alphaRes = await _client.PostAsJsonAsync(
            "/api/food-items",
            new CreateFoodItemRequest("Alpha Flour")
        );
        Assert.AreEqual(HttpStatusCode.Created, alphaRes.StatusCode);
        var alpha = await alphaRes.Content.ReadFromJsonAsync<FoodItemDto>();
        Assert.IsNotNull(alpha);

        // Update Beta Herb so its ModifiedAt is newest
        await Task.Delay(25);
        var updateBetaRes = await _client.PutAsJsonAsync(
            $"/api/food-items/{beta.Id}",
            new UpdateFoodItemRequest("Beta Herb", IsPantryStaple: true)
        );
        Assert.AreEqual(HttpStatusCode.OK, updateBetaRes.StatusCode);

        // Recipe usage: Gamma Spice = 2 recipes, Beta Herb = 1 recipe (2 ingredient rows in 1 recipe), Alpha Flour = 0
        for (var i = 1; i <= 2; i++)
        {
            var r = await _client.PostAsJsonAsync(
                "/api/recipes",
                new UpsertRecipeRequest(
                    Title: $"Gamma Recipe {i}",
                    Ingredients:
                    [
                        new RecipeIngredientInput(
                            null,
                            1m,
                            null,
                            MeasurementUnit.Tsp,
                            null,
                            gamma.Id,
                            null,
                            "Gamma Spice",
                            null
                        ),
                    ],
                    Steps: [new RecipeStepInput(null, "Season.")]
                )
            );
            Assert.AreEqual(HttpStatusCode.Created, r.StatusCode);
        }

        var betaRecipeRes = await _client.PostAsJsonAsync(
            "/api/recipes",
            new UpsertRecipeRequest(
                Title: "Beta Recipe",
                Ingredients:
                [
                    new RecipeIngredientInput(
                        null,
                        1m,
                        null,
                        MeasurementUnit.Tbsp,
                        null,
                        beta.Id,
                        null,
                        "Beta Herb, divided",
                        null
                    ),
                    new RecipeIngredientInput(
                        null,
                        1m,
                        null,
                        MeasurementUnit.Tsp,
                        null,
                        beta.Id,
                        null,
                        "Beta Herb for garnish",
                        null
                    ),
                ],
                Steps: [new RecipeStepInput(null, "Cook.")]
            )
        );
        Assert.AreEqual(HttpStatusCode.Created, betaRecipeRes.StatusCode);

        // 1. Default -> Name Asc
        var defaultPaged = await _client.GetFromJsonAsync<PagedResultDto<FoodItemDto>>(
            "/api/food-items?page=1&pageSize=10"
        );
        Assert.IsNotNull(defaultPaged);
        CollectionAssert.AreEqual(
            new[] { "Alpha Flour", "Beta Herb", "Gamma Spice" },
            defaultPaged.Items.Select(f => f.Name).ToArray()
        );

        // 2. Name Desc
        var nameDesc = await _client.GetFromJsonAsync<PagedResultDto<FoodItemDto>>(
            "/api/food-items?page=1&pageSize=10&sortBy=Name&sortDirection=Desc"
        );
        Assert.IsNotNull(nameDesc);
        CollectionAssert.AreEqual(
            new[] { "Gamma Spice", "Beta Herb", "Alpha Flour" },
            nameDesc.Items.Select(f => f.Name).ToArray()
        );

        // 3. RecipeCount Desc (paged & unpaged)
        var usageDescPaged = await _client.GetFromJsonAsync<PagedResultDto<FoodItemDto>>(
            "/api/food-items?page=1&pageSize=10&sortBy=RecipeCount&sortDirection=Desc"
        );
        Assert.IsNotNull(usageDescPaged);
        CollectionAssert.AreEqual(
            new[] { "Gamma Spice", "Beta Herb", "Alpha Flour" },
            usageDescPaged.Items.Select(f => f.Name).ToArray()
        );
        CollectionAssert.AreEqual(
            new[] { 2, 1, 0 },
            usageDescPaged.Items.Select(f => f.ReferencedRecipeCount).ToArray()
        );

        var usageDescUnpaged = await _client.GetFromJsonAsync<List<FoodItemDto>>(
            "/api/food-items?sortBy=RecipeCount&sortDirection=Desc"
        );
        Assert.IsNotNull(usageDescUnpaged);
        CollectionAssert.AreEqual(
            new[] { "Gamma Spice", "Beta Herb", "Alpha Flour" },
            usageDescUnpaged.Select(f => f.Name).ToArray()
        );

        // 4. RecipeCount Asc
        var usageAsc = await _client.GetFromJsonAsync<PagedResultDto<FoodItemDto>>(
            "/api/food-items?page=1&pageSize=10&sortBy=RecipeCount&sortDirection=Asc"
        );
        Assert.IsNotNull(usageAsc);
        CollectionAssert.AreEqual(
            new[] { "Alpha Flour", "Beta Herb", "Gamma Spice" },
            usageAsc.Items.Select(f => f.Name).ToArray()
        );

        // 5. CreatedAt Asc & Desc
        var createdAsc = await _client.GetFromJsonAsync<PagedResultDto<FoodItemDto>>(
            "/api/food-items?page=1&pageSize=10&sortBy=CreatedAt&sortDirection=Asc"
        );
        Assert.IsNotNull(createdAsc);
        CollectionAssert.AreEqual(
            new[] { beta.Id, gamma.Id, alpha.Id },
            createdAsc.Items.Select(f => f.Id).ToArray()
        );

        var createdDesc = await _client.GetFromJsonAsync<PagedResultDto<FoodItemDto>>(
            "/api/food-items?page=1&pageSize=10&sortBy=CreatedAt&sortDirection=Desc"
        );
        Assert.IsNotNull(createdDesc);
        CollectionAssert.AreEqual(
            new[] { alpha.Id, gamma.Id, beta.Id },
            createdDesc.Items.Select(f => f.Id).ToArray()
        );

        // 6. ModifiedAt Desc & Asc
        var modifiedDesc = await _client.GetFromJsonAsync<PagedResultDto<FoodItemDto>>(
            "/api/food-items?page=1&pageSize=10&sortBy=ModifiedAt&sortDirection=Desc"
        );
        Assert.IsNotNull(modifiedDesc);
        CollectionAssert.AreEqual(
            new[] { beta.Id, alpha.Id, gamma.Id },
            modifiedDesc.Items.Select(f => f.Id).ToArray()
        );

        var modifiedAsc = await _client.GetFromJsonAsync<PagedResultDto<FoodItemDto>>(
            "/api/food-items?page=1&pageSize=10&sortBy=ModifiedAt&sortDirection=Asc"
        );
        Assert.IsNotNull(modifiedAsc);
        CollectionAssert.AreEqual(
            new[] { gamma.Id, alpha.Id, beta.Id },
            modifiedAsc.Items.Select(f => f.Id).ToArray()
        );
    }

    [TestMethod]
    public async Task BulkDeleteFoodItems_DeletesUnused_BlocksReferencedUnlessUnlinkTrue_AndValidatesInput()
    {
        using var unauthClient = _factory.CreateClient();
        var unauthRes = await unauthClient.PostAsJsonAsync(
            "/api/food-items/bulk-delete",
            new BulkDeleteFoodItemsRequest([Guid.NewGuid()])
        );
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthRes.StatusCode);

        await RegisterAndAuthenticateAsync(_client, "bulkdel_user@example.com", "bulkdeluser");

        var emptyRes = await _client.PostAsJsonAsync(
            "/api/food-items/bulk-delete",
            new BulkDeleteFoodItemsRequest([])
        );
        Assert.AreEqual(HttpStatusCode.BadRequest, emptyRes.StatusCode);

        var emptyGuidRes = await _client.PostAsJsonAsync(
            "/api/food-items/bulk-delete",
            new BulkDeleteFoodItemsRequest([Guid.Empty])
        );
        Assert.AreEqual(HttpStatusCode.BadRequest, emptyGuidRes.StatusCode);

        var unused1 = await (
            await _client.PostAsJsonAsync(
                "/api/food-items",
                new CreateFoodItemRequest("Bulk Unused 1")
            )
        ).Content.ReadFromJsonAsync<FoodItemDto>();
        var unused2 = await (
            await _client.PostAsJsonAsync(
                "/api/food-items",
                new CreateFoodItemRequest("Bulk Unused 2")
            )
        ).Content.ReadFromJsonAsync<FoodItemDto>();
        var unused3 = await (
            await _client.PostAsJsonAsync(
                "/api/food-items",
                new CreateFoodItemRequest("Bulk Unused 3")
            )
        ).Content.ReadFromJsonAsync<FoodItemDto>();
        var used = await (
            await _client.PostAsJsonAsync("/api/food-items", new CreateFoodItemRequest("Bulk Used"))
        ).Content.ReadFromJsonAsync<FoodItemDto>();
        Assert.IsNotNull(unused1);
        Assert.IsNotNull(unused2);
        Assert.IsNotNull(unused3);
        Assert.IsNotNull(used);

        var recipeRes = await _client.PostAsJsonAsync(
            "/api/recipes",
            new UpsertRecipeRequest(
                Title: "Bulk Delete Recipe",
                Ingredients:
                [
                    new RecipeIngredientInput(
                        null,
                        1m,
                        null,
                        MeasurementUnit.Tsp,
                        null,
                        used.Id,
                        null,
                        "Bulk Used",
                        null
                    ),
                ],
                Steps: [new RecipeStepInput(null, "Mix.")]
            )
        );
        Assert.AreEqual(HttpStatusCode.Created, recipeRes.StatusCode);
        var recipe = await recipeRes.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(recipe);

        // 1. Delete 2 unused items
        var deleteUnusedRes = await _client.PostAsJsonAsync(
            "/api/food-items/bulk-delete",
            new BulkDeleteFoodItemsRequest([unused1.Id, unused2.Id])
        );
        Assert.AreEqual(HttpStatusCode.OK, deleteUnusedRes.StatusCode);
        var deleteUnusedResult =
            await deleteUnusedRes.Content.ReadFromJsonAsync<BulkDeleteFoodItemsResultDto>();
        Assert.IsNotNull(deleteUnusedResult);
        Assert.AreEqual(2, deleteUnusedResult.DeletedCount);
        Assert.AreEqual(
            HttpStatusCode.NotFound,
            (await _client.GetAsync($"/api/food-items/{unused1.Id}")).StatusCode
        );
        Assert.AreEqual(
            HttpStatusCode.NotFound,
            (await _client.GetAsync($"/api/food-items/{unused2.Id}")).StatusCode
        );

        // 2. Mixed unused + used with UnlinkFromRecipes = false -> 409 Conflict, neither deleted
        var conflictRes = await _client.PostAsJsonAsync(
            "/api/food-items/bulk-delete",
            new BulkDeleteFoodItemsRequest([unused3.Id, used.Id], UnlinkFromRecipes: false)
        );
        Assert.AreEqual(HttpStatusCode.Conflict, conflictRes.StatusCode);
        Assert.AreEqual(
            HttpStatusCode.OK,
            (await _client.GetAsync($"/api/food-items/{unused3.Id}")).StatusCode
        );
        Assert.AreEqual(
            HttpStatusCode.OK,
            (await _client.GetAsync($"/api/food-items/{used.Id}")).StatusCode
        );

        // 3. Same mixed request with UnlinkFromRecipes = true -> 200 OK, both deleted, recipe preserved
        var unlinkRes = await _client.PostAsJsonAsync(
            "/api/food-items/bulk-delete",
            new BulkDeleteFoodItemsRequest([unused3.Id, used.Id], UnlinkFromRecipes: true)
        );
        Assert.AreEqual(HttpStatusCode.OK, unlinkRes.StatusCode);
        var unlinkResult =
            await unlinkRes.Content.ReadFromJsonAsync<BulkDeleteFoodItemsResultDto>();
        Assert.IsNotNull(unlinkResult);
        Assert.AreEqual(2, unlinkResult.DeletedCount);
        Assert.AreEqual(1, unlinkResult.UnlinkedRecipeCount);
        Assert.AreEqual(1, unlinkResult.UnlinkedIngredientCount);

        Assert.AreEqual(
            HttpStatusCode.NotFound,
            (await _client.GetAsync($"/api/food-items/{unused3.Id}")).StatusCode
        );
        Assert.AreEqual(
            HttpStatusCode.NotFound,
            (await _client.GetAsync($"/api/food-items/{used.Id}")).StatusCode
        );

        var recipeAfter = await _client.GetFromJsonAsync<RecipeDetailDto>(
            $"/api/recipes/{recipe.Id}"
        );
        Assert.IsNotNull(recipeAfter);
        Assert.IsNull(recipeAfter.Ingredients[0].FoodItemId);
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using App.Core.Authentication;
using App.Core.FoodCatalog;
using App.Core.MealPlans;
using App.Core.Recipes;
using App.Core.Shopping;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.API.IntegrationTests;

[TestClass]
public sealed class ShoppingListsIntegrationTests
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
    public async Task Unauthenticated_Endpoints_ReturnUnauthorized()
    {
        var response = await _client.GetAsync("/api/shopping-lists");
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);

        var postResponse = await _client.PostAsJsonAsync(
            "/api/shopping-lists",
            new CreateShoppingListRequest("Test")
        );
        Assert.AreEqual(HttpStatusCode.Unauthorized, postResponse.StatusCode);
    }

    [TestMethod]
    public async Task GetShoppingLists_StartsEmpty_AndSupportsCrud()
    {
        await RegisterAndAuthenticateAsync(_client, "cook1@example.com", "Cook One");

        // 1. Initial GET returns empty list on fresh deployment
        var initialResponse = await _client.GetAsync("/api/shopping-lists");
        Assert.AreEqual(HttpStatusCode.OK, initialResponse.StatusCode);
        var initialLists = await initialResponse.Content.ReadFromJsonAsync<
            List<ShoppingListSummaryDto>
        >();
        Assert.IsNotNull(initialLists);
        Assert.AreEqual(0, initialLists.Count);

        // 2. Create new list "Costco Run"
        var createResponse = await _client.PostAsJsonAsync(
            "/api/shopping-lists",
            new CreateShoppingListRequest("Costco Run", IsPinned: false)
        );
        Assert.AreEqual(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<ShoppingListDetailDto>();
        Assert.IsNotNull(created);
        Assert.AreEqual("Costco Run", created.Name);

        // 3. Duplicate name returns 409 Conflict
        var dupResponse = await _client.PostAsJsonAsync(
            "/api/shopping-lists",
            new CreateShoppingListRequest("costco run")
        );
        Assert.AreEqual(HttpStatusCode.Conflict, dupResponse.StatusCode);

        // 4. Update list
        var updateResponse = await _client.PutAsJsonAsync(
            $"/api/shopping-lists/{created.Id}",
            new UpdateShoppingListRequest("Costco Wholesale", IsPinned: true)
        );
        Assert.AreEqual(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<ShoppingListDetailDto>();
        Assert.IsNotNull(updated);
        Assert.AreEqual("Costco Wholesale", updated.Name);
        Assert.IsTrue(updated.IsPinned);

        // 5. Delete list
        var deleteResponse = await _client.DeleteAsync($"/api/shopping-lists/{created.Id}");
        Assert.AreEqual(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var getDeleted = await _client.GetAsync($"/api/shopping-lists/{created.Id}");
        Assert.AreEqual(HttpStatusCode.NotFound, getDeleted.StatusCode);
    }

    [TestMethod]
    public async Task ShoppingListItems_Add_Update_Check_ClearChecked()
    {
        await RegisterAndAuthenticateAsync(_client, "cook2@example.com", "Cook Two");

        var createListRes = await _client.PostAsJsonAsync(
            "/api/shopping-lists",
            new CreateShoppingListRequest("Quick Trip")
        );
        Assert.AreEqual(HttpStatusCode.Created, createListRes.StatusCode);
        var list = await createListRes.Content.ReadFromJsonAsync<ShoppingListDetailDto>();
        Assert.IsNotNull(list);

        // 1. Add item "Paper Towels"
        var addItemRes = await _client.PostAsJsonAsync(
            $"/api/shopping-lists/{list.Id}/items",
            new CreateShoppingListItemRequest(
                "Paper Towels",
                2m,
                null,
                MeasurementUnit.Whole,
                null,
                null,
                "2-pack"
            )
        );
        Assert.AreEqual(HttpStatusCode.Created, addItemRes.StatusCode);
        var item = await addItemRes.Content.ReadFromJsonAsync<ShoppingListItemDto>();
        Assert.IsNotNull(item);
        Assert.AreEqual("Paper Towels", item.DisplayName);
        Assert.AreEqual(2m, item.Quantity);
        Assert.IsFalse(item.IsChecked);

        // 2. Update item
        var updateItemRes = await _client.PutAsJsonAsync(
            $"/api/shopping-lists/{list.Id}/items/{item.Id}",
            new UpdateShoppingListItemRequest(
                "Paper Towels",
                4m,
                null,
                MeasurementUnit.Whole,
                null,
                "Bounty only"
            )
        );
        Assert.AreEqual(HttpStatusCode.OK, updateItemRes.StatusCode);
        var updatedItem = await updateItemRes.Content.ReadFromJsonAsync<ShoppingListItemDto>();
        Assert.IsNotNull(updatedItem);
        Assert.AreEqual(4m, updatedItem.Quantity);
        Assert.AreEqual("Bounty only", updatedItem.Notes);

        // 3. Mark checked
        var checkRes = await _client.PatchAsJsonAsync(
            $"/api/shopping-lists/{list.Id}/items/{item.Id}/checked",
            new SetShoppingListItemCheckedRequest(true)
        );
        Assert.AreEqual(HttpStatusCode.OK, checkRes.StatusCode);
        var checkedItem = await checkRes.Content.ReadFromJsonAsync<ShoppingListItemDto>();
        Assert.IsNotNull(checkedItem);
        Assert.IsTrue(checkedItem.IsChecked);
        Assert.AreEqual("Cook Two", checkedItem.CheckedByDisplayName);

        // 4. Verify detail has 1 checked and 0 active
        var detailRes = await _client.GetAsync($"/api/shopping-lists/{list.Id}");
        var detail = await detailRes.Content.ReadFromJsonAsync<ShoppingListDetailDto>();
        Assert.IsNotNull(detail);
        Assert.AreEqual(0, detail.ActiveItems.Count);
        Assert.AreEqual(1, detail.CheckedItems.Count);

        // 5. Clear checked items
        var clearRes = await _client.DeleteAsync($"/api/shopping-lists/{list.Id}/items/checked");
        Assert.AreEqual(HttpStatusCode.OK, clearRes.StatusCode);
        var clearResult = await clearRes.Content.ReadFromJsonAsync<ClearCheckedItemsResultDto>();
        Assert.IsNotNull(clearResult);
        Assert.AreEqual(1, clearResult.RemovedCount);

        // 6. Verify detail now empty
        var finalDetailRes = await _client.GetAsync($"/api/shopping-lists/{list.Id}");
        var finalDetail = await finalDetailRes.Content.ReadFromJsonAsync<ShoppingListDetailDto>();
        Assert.IsNotNull(finalDetail);
        Assert.AreEqual(0, finalDetail.CheckedItems.Count);
    }

    [TestMethod]
    public async Task MealPlan_GenerateShoppingList_Consolidates_ExcludesPantry_AndPromotesStaple()
    {
        await RegisterAndAuthenticateAsync(_client, "planner@example.com", "Chef Planner");

        // 1. Create Garlic and Olive Oil in Food Catalog
        var garlicRes = await _client.PostAsJsonAsync(
            "/api/food-items",
            new CreateFoodItemRequest("Garlic", IsPantryStaple: false)
        );
        Assert.AreEqual(HttpStatusCode.Created, garlicRes.StatusCode);
        var garlic = await garlicRes.Content.ReadFromJsonAsync<FoodItemDto>();
        Assert.IsNotNull(garlic);

        var oilRes = await _client.PostAsJsonAsync(
            "/api/food-items",
            new CreateFoodItemRequest("Extra-Virgin Olive Oil", IsPantryStaple: true)
        );
        Assert.AreEqual(HttpStatusCode.Created, oilRes.StatusCode);
        var oliveOil = await oilRes.Content.ReadFromJsonAsync<FoodItemDto>();
        Assert.IsNotNull(oliveOil);

        // 2. Create Recipe A (Tacos, 4 servings: 2 cloves garlic, 2 tbsp olive oil)
        var recipeARes = await _client.PostAsJsonAsync(
            "/api/recipes",
            new UpsertRecipeRequest(
                Title: "Tacos Integration",
                Description: "Tasty",
                BaseServings: 4m,
                Ingredients:
                [
                    new RecipeIngredientInput(
                        DisplayName: "Garlic",
                        Quantity: 2m,
                        Unit: MeasurementUnit.Clove,
                        FoodItemId: garlic.Id
                    ),
                    new RecipeIngredientInput(
                        DisplayName: "Olive Oil",
                        Quantity: 2m,
                        Unit: MeasurementUnit.Tbsp,
                        FoodItemId: oliveOil.Id
                    ),
                ]
            )
        );
        Assert.AreEqual(HttpStatusCode.Created, recipeARes.StatusCode);
        var recipeA = await recipeARes.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(recipeA);

        // 3. Create Recipe B (Fajitas, 4 servings: 3 cloves garlic, 0.25 cup olive oil)
        var recipeBRes = await _client.PostAsJsonAsync(
            "/api/recipes",
            new UpsertRecipeRequest(
                Title: "Fajitas Integration",
                Description: "Sizzling",
                BaseServings: 4m,
                Ingredients:
                [
                    new RecipeIngredientInput(
                        DisplayName: "Garlic",
                        Quantity: 3m,
                        Unit: MeasurementUnit.Clove,
                        FoodItemId: garlic.Id
                    ),
                    new RecipeIngredientInput(
                        DisplayName: "Olive Oil",
                        Quantity: 0.25m,
                        Unit: MeasurementUnit.Cup,
                        FoodItemId: oliveOil.Id
                    ),
                ]
            )
        );
        Assert.AreEqual(HttpStatusCode.Created, recipeBRes.StatusCode);
        var recipeB = await recipeBRes.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(recipeB);

        // 4. Schedule on Meal Plan: Monday Dinner (Recipe A scaled to 8 = 2x), On-Deck (Recipe B 1x), Leftovers (Wed)
        var weekStart = new DateOnly(2026, 11, 2);
        var entry1Res = await _client.PostAsJsonAsync(
            "/api/meal-plans/entries",
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.Recipe,
                IsOnDeck: false,
                ScheduledDate: weekStart,
                Slot: MealSlot.Dinner,
                RecipeId: recipeA.Id,
                TargetServings: 8m
            )
        );
        Assert.AreEqual(HttpStatusCode.Created, entry1Res.StatusCode);

        var entry2Res = await _client.PostAsJsonAsync(
            "/api/meal-plans/entries",
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.Recipe,
                IsOnDeck: true,
                WeekStartDate: weekStart,
                RecipeId: recipeB.Id
            )
        );
        Assert.AreEqual(HttpStatusCode.Created, entry2Res.StatusCode);

        var entryLeftoversRes = await _client.PostAsJsonAsync(
            "/api/meal-plans/entries",
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.Leftovers,
                IsOnDeck: false,
                ScheduledDate: weekStart.AddDays(2),
                Slot: MealSlot.Dinner,
                RecipeId: recipeA.Id
            )
        );
        Assert.AreEqual(HttpStatusCode.Created, entryLeftoversRes.StatusCode);

        // 5. Create a target shopping list
        var listRes = await _client.PostAsJsonAsync(
            "/api/shopping-lists",
            new CreateShoppingListRequest("Plan Groceries")
        );
        var list = await listRes.Content.ReadFromJsonAsync<ShoppingListDetailDto>();
        Assert.IsNotNull(list);

        var genRequest = new GenerateFromMealPlanRequest(
            StartDate: weekStart,
            EndDate: weekStart.AddDays(6),
            IncludeOnDeck: true
        );

        // 6. Preview
        var previewRes = await _client.PostAsJsonAsync(
            $"/api/shopping-lists/{list.Id}/generate/meal-plan/preview",
            genRequest
        );
        Assert.AreEqual(HttpStatusCode.OK, previewRes.StatusCode);
        var preview =
            await previewRes.Content.ReadFromJsonAsync<ShoppingListGenerationPreviewDto>();
        Assert.IsNotNull(preview);
        Assert.AreEqual(2, preview.EligibleMealsCount);
        Assert.AreEqual(1, preview.IgnoredLeftoversCount);
        Assert.AreEqual(1, preview.ActiveCandidateItems.Count); // Garlic
        Assert.AreEqual(1, preview.ExcludedPantryCandidateItems.Count); // Olive Oil

        // 7. Execute Generation
        var execRes = await _client.PostAsJsonAsync(
            $"/api/shopping-lists/{list.Id}/generate/meal-plan",
            genRequest
        );
        Assert.AreEqual(HttpStatusCode.OK, execRes.StatusCode);
        var execResult = await execRes.Content.ReadFromJsonAsync<ShoppingListGenerationResultDto>();
        Assert.IsNotNull(execResult);

        var detail = execResult.ShoppingList;
        Assert.AreEqual(1, detail.ActiveItems.Count);
        Assert.AreEqual(1, detail.ExcludedPantryItems.Count);

        // Garlic: 2x 2 cloves (Recipe A) + 1x 3 cloves (Recipe B) = 7 cloves
        var garlicItem = detail.ActiveItems[0];
        Assert.AreEqual(7m, garlicItem.Quantity);
        Assert.AreEqual(2, garlicItem.Sources.Count);

        // Olive Oil: 2x 2 tbsp (4 tbsp) + 1x 0.25 cup (4 tbsp) = 8 tbsp = 0.5 cup
        var oilItem = detail.ExcludedPantryItems[0];
        Assert.AreEqual(0.5m, oilItem.Quantity);
        Assert.IsTrue(oilItem.IsPantryStapleExcluded);

        // 8. Promote Olive Oil to active checklist
        var promoteRes = await _client.PostAsJsonAsync(
            $"/api/shopping-lists/{list.Id}/items/{oilItem.Id}/promote-staple",
            new { }
        );
        Assert.AreEqual(HttpStatusCode.OK, promoteRes.StatusCode);
        var promotedItem = await promoteRes.Content.ReadFromJsonAsync<ShoppingListItemDto>();
        Assert.IsNotNull(promotedItem);
        Assert.IsFalse(promotedItem.IsPantryStapleExcluded);

        var updatedDetailRes = await _client.GetAsync($"/api/shopping-lists/{list.Id}");
        var updatedDetail =
            await updatedDetailRes.Content.ReadFromJsonAsync<ShoppingListDetailDto>();
        Assert.IsNotNull(updatedDetail);
        Assert.AreEqual(2, updatedDetail.ActiveItems.Count);
        Assert.AreEqual(0, updatedDetail.ExcludedPantryItems.Count);
    }
}

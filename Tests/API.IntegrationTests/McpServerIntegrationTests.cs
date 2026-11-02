using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using App.Core.Authentication;
using App.Core.Mcp;
using App.Core.Recipes;
using App.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace App.API.IntegrationTests;

[TestClass]
public sealed class McpServerIntegrationTests
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

    [TestMethod]
    public async Task McpEndpoint_WhenUnauthenticated_ReturnsUnauthorized()
    {
        var response = await _client.PostAsync(
            "/api/mcp",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
        );
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task McpEndpoint_WhenUsingJwtToken_ReturnsUnauthorized()
    {
        var auth = await RegisterAndAuthorizeAsync(
            "jwttest@example.com",
            "jwttest",
            "Password123!"
        );
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/mcp")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);

        var response = await _client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task McpEndpoint_WhenUsingRevokedPat_ReturnsUnauthorized()
    {
        await RegisterAndAuthorizeAsync("revokedchef@example.com", "revokedchef", "Password123!");
        var pat = await CreatePatAsync("Revokable");

        // Revoke token
        var revokeResponse = await _client.PostAsync(
            $"/api/account/tokens/{pat.Token.Id}/revoke",
            null
        );
        Assert.AreEqual(HttpStatusCode.OK, revokeResponse.StatusCode);

        // Try calling /api/mcp with revoked token
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/mcp")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pat.PlaintextToken);

        var response = await _client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task McpClient_ListTools_AdvertisesAll13Milestone2And3Tools()
    {
        await RegisterAndAuthorizeAsync("tooltester@example.com", "tooltester", "Password123!");
        var pat = await CreatePatAsync("Tool Tester");

        var testClient = _factory.CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(testClient.BaseAddress!, "/api/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {pat.PlaintextToken}",
                },
            },
            httpClient: testClient,
            ownsHttpClient: false
        );

        await using var mcpClient = await McpClient.CreateAsync(transport);

        var tools = await mcpClient.ListToolsAsync();
        Assert.IsNotNull(tools);
        var toolNames = tools.Select(t => t.Name).ToHashSet();

        Assert.AreEqual(13, toolNames.Count, "Expected exactly 13 MCP tools");

        // Milestone 2 tools
        Assert.IsTrue(toolNames.Contains("search_recipes"), "Missing search_recipes");
        Assert.IsTrue(toolNames.Contains("get_recipe"), "Missing get_recipe");
        Assert.IsTrue(
            toolNames.Contains("match_recipes_by_ingredients"),
            "Missing match_recipes_by_ingredients"
        );
        Assert.IsTrue(
            toolNames.Contains("list_collections_and_tags"),
            "Missing list_collections_and_tags"
        );
        Assert.IsTrue(
            toolNames.Contains("enqueue_recipe_ingestion"),
            "Missing enqueue_recipe_ingestion"
        );
        Assert.IsTrue(
            toolNames.Contains("save_structured_recipe"),
            "Missing save_structured_recipe"
        );

        // Milestone 3 tools
        Assert.IsTrue(toolNames.Contains("get_meal_plan"), "Missing get_meal_plan");
        Assert.IsTrue(toolNames.Contains("plan_meals"), "Missing plan_meals");
        Assert.IsTrue(
            toolNames.Contains("remove_meal_plan_entry"),
            "Missing remove_meal_plan_entry"
        );
        Assert.IsTrue(toolNames.Contains("get_shopping_lists"), "Missing get_shopping_lists");
        Assert.IsTrue(
            toolNames.Contains("generate_shopping_list_from_meal_plan"),
            "Missing generate_shopping_list_from_meal_plan"
        );
        Assert.IsTrue(
            toolNames.Contains("upsert_shopping_list_items"),
            "Missing upsert_shopping_list_items"
        );
        Assert.IsTrue(
            toolNames.Contains("set_shopping_list_item_checked"),
            "Missing set_shopping_list_item_checked"
        );
    }

    [TestMethod]
    public async Task McpClient_CallTools_EndToEnd_SaveSearchMatchAndEnqueue()
    {
        var auth = await RegisterAndAuthorizeAsync(
            "mcpchef@example.com",
            "mcpchef",
            "Password123!"
        );
        var pat = await CreatePatAsync("Agent Chef");

        var testClient = _factory.CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(testClient.BaseAddress!, "/api/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {pat.PlaintextToken}",
                },
            },
            httpClient: testClient,
            ownsHttpClient: false
        );

        await using var mcpClient = await McpClient.CreateAsync(transport);

        // 1. Call list_collections_and_tags
        var taxonomyResult = await mcpClient.CallToolAsync("list_collections_and_tags");
        Assert.IsNotNull(taxonomyResult);
        Assert.IsFalse(taxonomyResult.IsError == true);

        // 2. Call save_structured_recipe
        var saveResult = await mcpClient.CallToolAsync(
            "save_structured_recipe",
            new Dictionary<string, object?>
            {
                ["title"] = "MCP Roasted Pepper Penne",
                ["description"] = "Delicious pasta authored via MCP",
                ["publishImmediately"] = true,
                ["baseServings"] = 4,
                ["ingredients"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["displayName"] = "2 Bell Peppers, sliced",
                        ["canonicalFoodName"] = "Bell Pepper",
                        ["quantity"] = 2,
                        ["unit"] = "whole",
                    },
                    new Dictionary<string, object?>
                    {
                        ["displayName"] = "3 cloves Garlic",
                        ["canonicalFoodName"] = "Garlic",
                        ["quantity"] = 3,
                        ["unit"] = "clove",
                    },
                },
                ["steps"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["instruction"] = "Roast bell peppers for 15 minutes.",
                        ["ingredientIndices"] = new[] { 0 },
                    },
                    new Dictionary<string, object?>
                    {
                        ["instruction"] = "Toss with penne and garlic.",
                        ["ingredientIndices"] = new[] { 1 },
                    },
                },
            }
        );

        Assert.IsNotNull(saveResult);
        Assert.IsFalse(saveResult.IsError == true);
        var saveJson = ((TextContentBlock)saveResult.Content[0]).Text;
        StringAssert.Contains(saveJson, "MCP Roasted Pepper Penne");

        // Verify CreatedBy is attributed to the PAT owner's user ID
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var savedRecipe = await db.Recipes.FirstOrDefaultAsync(r =>
                r.Title == "MCP Roasted Pepper Penne"
            );
            Assert.IsNotNull(savedRecipe);
            Assert.AreEqual(auth.Id.ToString(), savedRecipe.CreatedBy);
        }

        // 3. Call search_recipes
        var searchResult = await mcpClient.CallToolAsync(
            "search_recipes",
            new Dictionary<string, object?> { ["query"] = "Roasted Pepper" }
        );

        Assert.IsNotNull(searchResult);
        Assert.IsFalse(searchResult.IsError == true);
        var searchJson = ((TextContentBlock)searchResult.Content[0]).Text;
        StringAssert.Contains(searchJson, "MCP Roasted Pepper Penne");

        // 4. Call match_recipes_by_ingredients
        var matchResult = await mcpClient.CallToolAsync(
            "match_recipes_by_ingredients",
            new Dictionary<string, object?> { ["ingredients"] = new[] { "bell pepper" } }
        );

        Assert.IsNotNull(matchResult);
        Assert.IsFalse(matchResult.IsError == true);
        var matchJson = ((TextContentBlock)matchResult.Content[0]).Text;
        StringAssert.Contains(matchJson, "MCP Roasted Pepper Penne");

        // 5. Call enqueue_recipe_ingestion
        var enqueueResult = await mcpClient.CallToolAsync(
            "enqueue_recipe_ingestion",
            new Dictionary<string, object?>
            {
                ["rawText"] =
                    "Pasted Quick Snack\nIngredients:\n- 1 Apple\nSteps:\n1. Slice and eat.",
                ["titleHint"] = "Apple Snack",
            }
        );

        Assert.IsNotNull(enqueueResult);
        Assert.IsFalse(enqueueResult.IsError == true);
        var enqueueJson = ((TextContentBlock)enqueueResult.Content[0]).Text;
        StringAssert.Contains(enqueueJson, "Apple Snack");

        // 6. Verify PAT LastUsedAt was updated
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tokenInDb = await db.PersonalAccessTokens.FirstOrDefaultAsync(t =>
                t.Id == pat.Token.Id
            );
            Assert.IsNotNull(tokenInDb?.LastUsedAt);
        }
    }

    [TestMethod]
    public async Task McpClient_Milestone3Tools_PlanMeals_GenerateShoppingList_UpsertAndCheckItems_EndToEnd()
    {
        var auth = await RegisterAndAuthorizeAsync("m3chef@example.com", "m3chef", "Password123!");
        var pat = await CreatePatAsync("M3 Assistant");

        var testClient = _factory.CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(testClient.BaseAddress!, "/api/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {pat.PlaintextToken}",
                },
            },
            httpClient: testClient,
            ownsHttpClient: false
        );

        await using var mcpClient = await McpClient.CreateAsync(transport);

        // 1. Save a structured recipe
        var saveRes = await mcpClient.CallToolAsync(
            "save_structured_recipe",
            new Dictionary<string, object?>
            {
                ["title"] = "MCP Lemon Herb Chicken",
                ["publishImmediately"] = true,
                ["baseServings"] = 4,
                ["ingredients"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["displayName"] = "4 Chicken breasts",
                        ["canonicalFoodName"] = "Chicken Breast",
                        ["quantity"] = 4,
                        ["unit"] = "whole",
                    },
                    new Dictionary<string, object?>
                    {
                        ["displayName"] = "1 tbsp Olive Oil",
                        ["canonicalFoodName"] = "Extra-Virgin Olive Oil",
                        ["quantity"] = 1,
                        ["unit"] = "tbsp",
                    },
                },
                ["steps"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["instruction"] = "Sear chicken in olive oil.",
                    },
                },
            }
        );
        Assert.IsNotNull(saveRes);
        Assert.IsFalse(saveRes.IsError == true);

        // 2. Plan meals: recipe on Monday, leftovers on Tuesday
        var planRes = await mcpClient.CallToolAsync(
            "plan_meals",
            new Dictionary<string, object?>
            {
                ["entries"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["entryType"] = "Recipe",
                        ["scheduledDate"] = "2026-11-02",
                        ["slot"] = "Dinner",
                        ["recipeTitle"] = "MCP Lemon Herb Chicken",
                        ["targetServings"] = 8,
                    },
                    new Dictionary<string, object?>
                    {
                        ["entryType"] = "Leftovers",
                        ["scheduledDate"] = "2026-11-03",
                        ["slot"] = "Lunch",
                        ["customTitle"] = "Leftover Chicken",
                    },
                },
            }
        );
        Assert.IsNotNull(planRes);
        Assert.IsFalse(planRes.IsError == true);

        // 3. Get meal plan
        var getPlanRes = await mcpClient.CallToolAsync(
            "get_meal_plan",
            new Dictionary<string, object?>
            {
                ["startDate"] = "2026-11-02",
                ["endDate"] = "2026-11-08",
            }
        );
        Assert.IsNotNull(getPlanRes);
        Assert.IsFalse(getPlanRes.IsError == true);
        var planJson = ((TextContentBlock)getPlanRes.Content[0]).Text;
        StringAssert.Contains(planJson, "MCP Lemon Herb Chicken");
        StringAssert.Contains(planJson, "Leftover Chicken");

        // 4. Generate shopping list from meal plan into "Weekly Groceries"
        var genRes = await mcpClient.CallToolAsync(
            "generate_shopping_list_from_meal_plan",
            new Dictionary<string, object?>
            {
                ["startDate"] = "2026-11-02",
                ["endDate"] = "2026-11-08",
                ["listName"] = "Weekly Groceries",
                ["createListIfMissing"] = true,
            }
        );
        Assert.IsNotNull(genRes);
        Assert.IsFalse(genRes.IsError == true);
        var genJson = ((TextContentBlock)genRes.Content[0]).Text;
        StringAssert.Contains(genJson, "Weekly Groceries");

        // 5. Upsert items: add paper towels
        var upsertRes = await mcpClient.CallToolAsync(
            "upsert_shopping_list_items",
            new Dictionary<string, object?>
            {
                ["listName"] = "Weekly Groceries",
                ["items"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["displayName"] = "Paper Towels",
                        ["quantity"] = 2,
                        ["unit"] = "whole",
                    },
                },
            }
        );
        Assert.IsNotNull(upsertRes);
        Assert.IsFalse(upsertRes.IsError == true);
        var upsertJson = ((TextContentBlock)upsertRes.Content[0]).Text;
        StringAssert.Contains(upsertJson, "Paper Towels");

        // 6. Check item
        var checkRes = await mcpClient.CallToolAsync(
            "set_shopping_list_item_checked",
            new Dictionary<string, object?>
            {
                ["listName"] = "Weekly Groceries",
                ["itemName"] = "Paper Towels",
                ["isChecked"] = true,
            }
        );
        Assert.IsNotNull(checkRes);
        Assert.IsFalse(checkRes.IsError == true);

        // 7. Get shopping lists
        var getListsRes = await mcpClient.CallToolAsync(
            "get_shopping_lists",
            new Dictionary<string, object?>
            {
                ["listName"] = "Weekly Groceries",
                ["includeItems"] = true,
            }
        );
        Assert.IsNotNull(getListsRes);
        Assert.IsFalse(getListsRes.IsError == true);
        var listsJson = ((TextContentBlock)getListsRes.Content[0]).Text;
        StringAssert.Contains(listsJson, "Paper Towels");
    }

    private async Task<AuthResponse> RegisterAndAuthorizeAsync(
        string email,
        string username,
        string password
    )
    {
        var response = await _client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(email, username, password)
        );
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.IsNotNull(auth);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            auth.Token
        );
        return auth;
    }

    private async Task<CreatedPersonalAccessTokenDto> CreatePatAsync(string name)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/account/tokens",
            new CreatePersonalAccessTokenRequest(name)
        );
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<CreatedPersonalAccessTokenDto>();
        Assert.IsNotNull(created);
        return created;
    }
}

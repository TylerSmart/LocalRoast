using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace App.Client.E2ETests;

[TestClass]
public sealed class McpAndPatE2ETests : E2ETestBase
{
    [TestMethod]
    public async Task PersonalAccessToken_MintInUI_ExecuteMcpTools_VerifyCookbookAttributionAndLastUsed()
    {
        var (username, _) = await RegisterAndLoginAsync();

        // 1. Open user menu and click "MCP & API Tokens"
        await Page.Locator(".user-profile-btn").ClickAsync();
        var tokensMenuItem = Page.Locator(
            "button[mat-menu-item]",
            new() { HasText = "MCP & API Tokens" }
        );
        await Expect(tokensMenuItem).ToBeVisibleAsync();
        await tokensMenuItem.ClickAsync();

        var dialog = Page.Locator("app-account-settings-dialog");
        await Expect(dialog).ToBeVisibleAsync();

        // Verify active tab is MCP Tokens and snippet is rendered
        var activeTab = dialog.Locator(".mat-mdc-tab[aria-selected='true']");
        await Expect(activeTab).ToContainTextAsync("MCP Tokens");
        await Expect(dialog.Locator(".mcp-config-card")).ToBeVisibleAsync();

        // 2. Generate a new token with 30-day expiration
        var nameInput = dialog.Locator("input[formcontrolname='name']");
        await nameInput.FillAsync("Antigravity Kitchen Assistant");

        await dialog.Locator("mat-select[formcontrolname='expirationDays']").ClickAsync();
        await Page.Locator("mat-option", new() { HasText = "30 days" }).ClickAsync();

        await dialog.Locator("button.generate-btn").ClickAsync();

        // 3. Extract the one-time plaintext token
        var secretCode = dialog.Locator(".pat-secret-value");
        await Expect(secretCode).ToBeVisibleAsync();
        var plaintextToken = (await secretCode.InnerTextAsync()).Trim();
        Assert.StartsWith("lr_pat_", plaintextToken);

        // 4. Connect real McpClient via Streamable HTTP with Authorization: Bearer <lr_pat_...>
        using var httpClient = new HttpClient { BaseAddress = new Uri(ServerUrl) };
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(new Uri(ServerUrl), "/api/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {plaintextToken}",
                },
            },
            httpClient: httpClient,
            ownsHttpClient: false
        );

        await using var mcpClient = await McpClient.CreateAsync(transport);

        // Assert all 6 Milestone 2 tools are advertised
        var tools = await mcpClient.ListToolsAsync();
        Assert.IsNotNull(tools);
        var toolNames = tools.Select(t => t.Name).ToHashSet();
        Assert.Contains("search_recipes", toolNames);
        Assert.Contains("get_recipe", toolNames);
        Assert.Contains("match_recipes_by_ingredients", toolNames);
        Assert.Contains("list_collections_and_tags", toolNames);
        Assert.Contains("enqueue_recipe_ingestion", toolNames);
        Assert.Contains("save_structured_recipe", toolNames);

        // Call save_structured_recipe
        var recipeTitle = $"MCP Charred Scallion Ramen {Guid.NewGuid():N}"[..30];
        var saveResult = await mcpClient.CallToolAsync(
            "save_structured_recipe",
            new Dictionary<string, object?>
            {
                ["title"] = recipeTitle,
                ["description"] = "Rich scallion ramen made via MCP client.",
                ["publishImmediately"] = true,
                ["baseServings"] = 2,
                ["tags"] = new[] { "Weeknight", "Noodles" },
                ["ingredients"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["displayName"] = "4 bunches Scallions, charred",
                        ["canonicalFoodName"] = "Scallion",
                        ["quantity"] = 4,
                        ["unit"] = "bunch",
                    },
                    new Dictionary<string, object?>
                    {
                        ["displayName"] = "200 g Ramen noodles",
                        ["canonicalFoodName"] = "Ramen Noodles",
                        ["quantity"] = 200,
                        ["unit"] = "g",
                    },
                },
                ["steps"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["instruction"] = "Simmer broth with charred scallions for 20 minutes.",
                        ["ingredientIndices"] = new[] { 0 },
                    },
                    new Dictionary<string, object?>
                    {
                        ["instruction"] = "Cook noodles and serve hot.",
                        ["ingredientIndices"] = new[] { 1 },
                    },
                },
            }
        );
        Assert.IsNotNull(saveResult);
        Assert.AreNotEqual(true, saveResult.IsError);

        // Call search_recipes
        var searchResult = await mcpClient.CallToolAsync(
            "search_recipes",
            new Dictionary<string, object?> { ["query"] = "Scallion Ramen" }
        );
        Assert.IsNotNull(searchResult);
        Assert.AreNotEqual(true, searchResult.IsError);
        var searchJson = ((TextContentBlock)searchResult.Content[0]).Text;
        StringAssert.Contains(searchJson, recipeTitle);

        // Call match_recipes_by_ingredients
        var matchResult = await mcpClient.CallToolAsync(
            "match_recipes_by_ingredients",
            new Dictionary<string, object?> { ["ingredients"] = new[] { "scallion" } }
        );
        Assert.IsNotNull(matchResult);
        Assert.AreNotEqual(true, matchResult.IsError);
        var matchJson = ((TextContentBlock)matchResult.Content[0]).Text;
        StringAssert.Contains(matchJson, recipeTitle);

        // Call enqueue_recipe_ingestion
        var enqueueResult = await mcpClient.CallToolAsync(
            "enqueue_recipe_ingestion",
            new Dictionary<string, object?>
            {
                ["rawText"] =
                    "Quick Garlic Butter Pasta\nIngredients:\n- 200g spaghetti\n- 4 cloves garlic\nSteps:\n1. Boil pasta for 9 minutes.",
                ["titleHint"] = "MCP Queued Pasta",
            }
        );
        Assert.IsNotNull(enqueueResult);
        Assert.AreNotEqual(true, enqueueResult.IsError);

        // 5. Close and re-open dialog to verify Last used is populated
        await dialog.Locator("button[aria-label='Close account settings']").ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0);

        await Page.Locator(".user-profile-btn").ClickAsync();
        await Page.Locator("button[mat-menu-item]", new() { HasText = "MCP & API Tokens" })
            .ClickAsync();
        await Expect(dialog).ToBeVisibleAsync();

        var lastUsedValue = dialog.Locator(".last-used-value");
        await Expect(lastUsedValue).ToBeVisibleAsync();

        await dialog.Locator("button[aria-label='Close account settings']").ClickAsync();

        // 6. Navigate to /recipes, verify recipe card is present in Cookbook
        await Page.GotoAsync($"{ServerUrl}/recipes");
        var recipeCard = Page.Locator(".recipe-card", new() { HasText = recipeTitle });
        await Expect(recipeCard).ToBeVisibleAsync();

        // Click recipe card to navigate to detail page
        await recipeCard.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+$"));

        await Expect(Page.Locator("h1.recipe-title")).ToContainTextAsync(recipeTitle);
        await Expect(Page.Locator(".ingredients-column")).ToContainTextAsync("Scallions");
    }

    [TestMethod]
    public async Task PersonalAccessToken_RevokeInUI_ImmediatelyBlocksMcpAccessAndRestEndpointsRejectPat()
    {
        await RegisterAndLoginAsync();

        // 1. Open MCP Tokens tab and mint "Temporary Voice Agent"
        await Page.Locator(".user-profile-btn").ClickAsync();
        await Page.Locator("button[mat-menu-item]", new() { HasText = "MCP & API Tokens" })
            .ClickAsync();

        var dialog = Page.Locator("app-account-settings-dialog");
        await Expect(dialog).ToBeVisibleAsync();

        await dialog.Locator("input[formcontrolname='name']").FillAsync("Temporary Voice Agent");
        await dialog.Locator("button.generate-btn").ClickAsync();

        var secretCode = dialog.Locator(".pat-secret-value");
        await Expect(secretCode).ToBeVisibleAsync();
        var plaintextToken = (await secretCode.InnerTextAsync()).Trim();

        // 2. Connect McpClient initially to verify access works
        using var httpClient = new HttpClient { BaseAddress = new Uri(ServerUrl) };
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(new Uri(ServerUrl), "/api/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {plaintextToken}",
                },
            },
            httpClient: httpClient,
            ownsHttpClient: false
        );

        await using (var mcpClient = await McpClient.CreateAsync(transport))
        {
            var tools = await mcpClient.ListToolsAsync();
            Assert.IsNotNull(tools);
        }

        // 3. Verify regular REST endpoint rejects PAT with 401 Unauthorized
        var restReq = new HttpRequestMessage(HttpMethod.Get, $"{ServerUrl}/api/account");
        restReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plaintextToken);
        var restRes = await httpClient.SendAsync(restReq);
        Assert.AreEqual(HttpStatusCode.Unauthorized, restRes.StatusCode);

        // 4. Revoke token in the UI
        var tokenRow = dialog.Locator(".token-item", new() { HasText = "Temporary Voice Agent" });
        var revokeBtn = tokenRow.Locator("button.revoke-btn");
        await revokeBtn.ClickAsync();

        var revokedBadge = tokenRow.Locator(".badge-revoked");
        await Expect(revokedBadge).ToBeVisibleAsync();

        // 5. Subsequent call to /api/mcp fails immediately with 401 Unauthorized
        var mcpReq = new HttpRequestMessage(HttpMethod.Post, $"{ServerUrl}/api/mcp")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        mcpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plaintextToken);
        var mcpRes = await httpClient.SendAsync(mcpReq);
        Assert.AreEqual(HttpStatusCode.Unauthorized, mcpRes.StatusCode);

        // 6. Delete token in UI and verify it is removed from inventory
        var deleteBtn = tokenRow.Locator("button.delete-btn");
        await deleteBtn.ClickAsync();
        await Expect(tokenRow).ToHaveCountAsync(0);
    }

    [TestMethod]
    public async Task PersonalAccessToken_MobileViewport_360px_DialogTabsAndSnippetRenderCleanly()
    {
        // Set mobile viewport 360x640
        await Page.SetViewportSizeAsync(360, 640);
        await RegisterAndLoginAsync();

        // Open user menu and click "MCP & API Tokens"
        await Page.Locator(".user-profile-btn").ClickAsync();
        var tokensMenuItem = Page.Locator(
            "button[mat-menu-item]",
            new() { HasText = "MCP & API Tokens" }
        );
        await Expect(tokensMenuItem).ToBeVisibleAsync();
        await tokensMenuItem.ClickAsync();

        var dialog = Page.Locator("app-account-settings-dialog");
        await Expect(dialog).ToBeVisibleAsync();

        // Verify page does not horizontally scroll beyond 360px
        var scrollWidth = await Page.EvaluateAsync<int>("document.documentElement.scrollWidth");
        Assert.IsLessThanOrEqualTo(scrollWidth, 360);

        // Generate token
        await dialog.Locator("input[formcontrolname='name']").FillAsync("Mobile Test PAT");
        await dialog.Locator("button.generate-btn").ClickAsync();

        var secretCode = dialog.Locator(".pat-secret-value");
        await Expect(secretCode).ToBeVisibleAsync();

        var copyConfigBtn = dialog.Locator("button.copy-config-btn");
        await Expect(copyConfigBtn).ToBeVisibleAsync();
        await copyConfigBtn.ClickAsync();

        var tokenRow = dialog.Locator(".token-item", new() { HasText = "Mobile Test PAT" });
        var revokeBtn = tokenRow.Locator("button.revoke-btn");
        await Expect(revokeBtn).ToBeVisibleAsync();
        await revokeBtn.ClickAsync();

        await Expect(tokenRow.Locator(".badge-revoked")).ToBeVisibleAsync();
    }
}

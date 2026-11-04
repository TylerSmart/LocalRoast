using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace App.Client.E2ETests;

[TestClass]
public sealed class SignalRCoShoppingAndMcpE2ETests : E2ETestBase
{
    private async Task<(string Username, string Email)> RegisterAndLoginOnPageAsync(
        IPage targetPage,
        string? username = null,
        string? email = null,
        string password = "KitchenSecret123!"
    )
    {
        username ??= $"chef_{Guid.NewGuid():N}"[..12];
        email ??= $"chef_{Guid.NewGuid():N}@example.com";

        await targetPage.GotoAsync($"{ServerUrl}/login");
        await targetPage.Locator(".mat-mdc-tab", new() { HasText = "Create Account" }).ClickAsync();

        var registerForm = targetPage.Locator("form[aria-label='Create account form']");
        await registerForm.Locator("input[formcontrolname='username']").FillAsync(username);
        await registerForm.Locator("input[formcontrolname='email']").FillAsync(email);
        await registerForm.Locator("input[formcontrolname='password']").FillAsync(password);
        await registerForm.Locator("input[formcontrolname='confirmPassword']").FillAsync(password);

        await registerForm.Locator("button[type='submit']").ClickAsync();
        await Expect(targetPage)
            .ToHaveURLAsync(new Regex(".*/recipes.*"), new() { Timeout = 15000 });

        return (username, email);
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task SignalR_TwoHouseholdPhones_LiveCoShopping_SyncsAddCheckPromoteClearAndListCreation()
    {
        // User A (Phone 1)
        var (userA, _) = await RegisterAndLoginOnPageAsync(Page);

        // User B (Phone 2) in isolated browser context
        var (contextB, pageB) = await CreateIsolatedPageAsync("phone2");
        try
        {
            var (userB, _) = await RegisterAndLoginOnPageAsync(pageB);

            // Both navigate to /shopping
            await Page.GotoAsync($"{ServerUrl}/shopping");
            await pageB.GotoAsync($"{ServerUrl}/shopping");

            await Expect(Page.Locator(".sync-status-pill.live")).ToBeVisibleAsync();
            await Expect(pageB.Locator(".sync-status-pill.live")).ToBeVisibleAsync();

            // Create a dedicated list "Live Co-Shopping" to isolate from prior tests
            await Page.Locator(".desktop-list-pills button.new-list-btn").ClickAsync();
            var initDialog = Page.Locator("mat-dialog-container");
            await Expect(initDialog.Locator("h2")).ToContainTextAsync("New Shopping List");
            var initInput = initDialog.Locator("input[formcontrolname='name']");
            await initInput.ClickAsync();
            await initInput.FillAsync("Live Co-Shopping");
            await Expect(initDialog.Locator("button[type='submit']")).ToBeEnabledAsync();
            await initDialog.Locator("button[type='submit']").ClickAsync();
            await Expect(Page.Locator("mat-dialog-container")).ToHaveCountAsync(0);

            // Asserts User B sees the new list and selects it
            var livePillB = pageB.Locator(
                ".desktop-list-pills .list-pill",
                new() { HasText = "Live Co-Shopping" }
            );
            await Expect(livePillB).ToBeVisibleAsync();
            await livePillB.ClickAsync();

            // 1. Live Add: User A quick-adds Organic Blueberries and Sourdough Loaf
            var quickAddInputA = Page.Locator(".quick-add-bar input");
            await quickAddInputA.FillAsync("Organic Blueberries");
            await Page.Locator("button.quick-add-submit-btn").ClickAsync();
            await Expect(
                    Page.Locator(
                        ".active-items-card .shopping-item-row",
                        new() { HasText = "Organic Blueberries" }
                    )
                )
                .ToBeVisibleAsync();

            await quickAddInputA.FillAsync("Sourdough Loaf");
            await Page.Locator("button.quick-add-submit-btn").ClickAsync();
            await Expect(
                    Page.Locator(
                        ".active-items-card .shopping-item-row",
                        new() { HasText = "Sourdough Loaf" }
                    )
                )
                .ToBeVisibleAsync();

            // Asserts User B's checklist renders both items automatically without page reload
            await Expect(
                    pageB.Locator(
                        ".active-items-card .shopping-item-row",
                        new() { HasText = "Organic Blueberries" }
                    )
                )
                .ToBeVisibleAsync();
            await Expect(
                    pageB.Locator(
                        ".active-items-card .shopping-item-row",
                        new() { HasText = "Sourdough Loaf" }
                    )
                )
                .ToBeVisibleAsync();
            await Expect(pageB.Locator(".desktop-list-pills .list-pill.active .pill-count"))
                .ToContainTextAsync("(2)");

            // 2. Live Check with Attribution: User B checks off Organic Blueberries
            var blueberriesRowB = pageB.Locator(
                ".active-items-card .shopping-item-row",
                new() { HasText = "Organic Blueberries" }
            );
            await blueberriesRowB.Locator("mat-checkbox").ClickAsync();

            // User A's screen moves Organic Blueberries into .checked-items-section ("Checked Off (1)")
            // and displays "Checked off by <userB>" in real time
            var checkedSectionA = Page.Locator(".checked-items-section");
            await Expect(checkedSectionA.Locator(".checked-toggle-btn"))
                .ToContainTextAsync("Checked Off (1)");
            var checkedBlueberriesA = checkedSectionA.Locator(
                ".shopping-item-row",
                new() { HasText = "Organic Blueberries" }
            );
            await Expect(checkedBlueberriesA).ToBeVisibleAsync();
            await Expect(checkedBlueberriesA.Locator(".checked-by-meta"))
                .ToContainTextAsync($"Checked off by {userB}");

            // 3. Live Clear Checked Items: User A clicks "Clear Checked Items"
            await checkedSectionA.Locator("button.clear-checked-btn").ClickAsync();
            await Expect(Page.Locator(".checked-items-section")).Not.ToBeVisibleAsync();
            await Expect(pageB.Locator(".checked-items-section")).Not.ToBeVisibleAsync();

            // 4. List Creation: User A creates new list "Costco Run"
            await Page.Locator(".desktop-list-pills button.new-list-btn").ClickAsync();
            var listDialog = Page.Locator("mat-dialog-container");
            await Expect(listDialog.Locator("h2")).ToContainTextAsync("New Shopping List");
            var nameInput = listDialog.Locator("input[formcontrolname='name']");
            await nameInput.ClickAsync();
            await nameInput.FillAsync("Costco Run");
            await Expect(listDialog.Locator("button[type='submit']")).ToBeEnabledAsync();
            await listDialog.Locator("button[type='submit']").ClickAsync();
            await Expect(Page.Locator("mat-dialog-container")).ToHaveCountAsync(0);

            await Expect(
                    Page.Locator(".desktop-list-pills .list-pill", new() { HasText = "Costco Run" })
                )
                .ToBeVisibleAsync();

            // Asserts "Costco Run" pill appears automatically in User B's desktop-list-pills
            await Expect(
                    pageB.Locator(
                        ".desktop-list-pills .list-pill",
                        new() { HasText = "Costco Run" }
                    )
                )
                .ToBeVisibleAsync();
        }
        finally
        {
            // Managed by E2ETestBase cleanup
        }
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task McpMilestone3Tools_PlanMeals_GenerateShoppingList_UpsertAndCheckItems_UpdatesBrowserLive()
    {
        await RegisterAndLoginAsync();
        await EnsureFoodItemExistsViaApiAsync("Garlic", isPantryStaple: false);
        await EnsureFoodItemExistsViaApiAsync("Kosher Salt", isPantryStaple: true);

        // 1. Open user menu and mint a Personal Access Token via UI
        await Page.Locator(".user-profile-btn").ClickAsync();
        var tokensMenuItem = Page.Locator(
            "button[mat-menu-item]",
            new() { HasText = "MCP & API Tokens" }
        );
        await Expect(tokensMenuItem).ToBeVisibleAsync();
        await tokensMenuItem.ClickAsync();

        var dialog = Page.Locator("app-account-settings-dialog");
        await Expect(dialog).ToBeVisibleAsync();

        var nameInput = dialog.Locator("input[formcontrolname='name']");
        await nameInput.FillAsync("Milestone 3 Test Bot");
        await dialog.Locator("button.generate-btn").ClickAsync();

        var secretCode = dialog.Locator(".pat-secret-value");
        await Expect(secretCode).ToBeVisibleAsync();
        var plaintextToken = (await secretCode.InnerTextAsync()).Trim();
        Assert.StartsWith("lr_pat_", plaintextToken);

        await dialog.Locator("button[aria-label='Close account settings']").ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0);

        // 2. Navigate browser to /shopping and confirm Live Sync
        await Page.Locator("nav.desktop-nav a", new() { HasText = "Shop" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/shopping.*"));
        await Expect(Page.Locator(".sync-status-pill.live")).ToBeVisibleAsync();

        var listName = $"MCP Live {Guid.NewGuid():N}"[..18];

        // 3. Connect real McpClient via Streamable HTTP with PAT
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

        // Assert all 13 tools are advertised (6 Milestone 2 + 7 Milestone 3 tools)
        var tools = await mcpClient.ListToolsAsync();
        Assert.IsNotNull(tools);
        var toolNames = tools.Select(t => t.Name).ToHashSet();
        Assert.HasCount(13, toolNames);
        Assert.Contains("search_recipes", toolNames);
        Assert.Contains("get_recipe", toolNames);
        Assert.Contains("match_recipes_by_ingredients", toolNames);
        Assert.Contains("list_collections_and_tags", toolNames);
        Assert.Contains("enqueue_recipe_ingestion", toolNames);
        Assert.Contains("save_structured_recipe", toolNames);
        Assert.Contains("get_meal_plan", toolNames);
        Assert.Contains("plan_meals", toolNames);
        Assert.Contains("remove_meal_plan_entry", toolNames);
        Assert.Contains("get_shopping_lists", toolNames);
        Assert.Contains("generate_shopping_list_from_meal_plan", toolNames);
        Assert.Contains("upsert_shopping_list_items", toolNames);
        Assert.Contains("set_shopping_list_item_checked", toolNames);

        // 4. Save structured recipe (base 4 servings, Garlic 3 cloves, Kosher Salt 1 tsp)
        var recipeTitle = $"MCP Feast {Guid.NewGuid():N}"[..20];
        var saveResult = await mcpClient.CallToolAsync(
            "save_structured_recipe",
            new Dictionary<string, object?>
            {
                ["title"] = recipeTitle,
                ["description"] = "Delicious roasted feast for co-shopping E2E.",
                ["publishImmediately"] = true,
                ["baseServings"] = 4,
                ["ingredients"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["displayName"] = "3 cloves Garlic, minced",
                        ["canonicalFoodName"] = "Garlic",
                        ["quantity"] = 3,
                        ["unit"] = "Clove",
                    },
                    new Dictionary<string, object?>
                    {
                        ["displayName"] = "1 tsp Kosher Salt",
                        ["canonicalFoodName"] = "Kosher Salt",
                        ["quantity"] = 1,
                        ["unit"] = "Teaspoon",
                    },
                },
                ["steps"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["instruction"] = "Mince garlic, season with salt, and roast.",
                    },
                },
            }
        );
        Assert.IsNotNull(saveResult);
        Assert.AreNotEqual(true, saveResult.IsError);

        // 5. Plan meals via MCP on a clean upcoming week (isolated from other E2E suites)
        var today = DateTime.Today;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)).AddDays(28);
        var tuesday = monday.AddDays(1);
        var friday = monday.AddDays(4);
        var saturday = monday.AddDays(5);
        var sunday = monday.AddDays(6);

        var mondayStr = monday.ToString("yyyy-MM-dd");
        var tuesdayStr = tuesday.ToString("yyyy-MM-dd");
        var fridayStr = friday.ToString("yyyy-MM-dd");
        var saturdayStr = saturday.ToString("yyyy-MM-dd");
        var sundayStr = sunday.ToString("yyyy-MM-dd");

        var planMealsResult = await mcpClient.CallToolAsync(
            "plan_meals",
            new Dictionary<string, object?>
            {
                ["entries"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["entryType"] = "Recipe",
                        ["scheduledDate"] = mondayStr,
                        ["slot"] = "Dinner",
                        ["recipeTitle"] = recipeTitle,
                        ["targetServings"] = 8,
                    },
                    new Dictionary<string, object?>
                    {
                        ["entryType"] = "Leftovers",
                        ["scheduledDate"] = tuesdayStr,
                        ["slot"] = "Lunch",
                        ["customTitle"] = "Leftovers",
                    },
                    new Dictionary<string, object?>
                    {
                        ["entryType"] = "FreeformNote",
                        ["scheduledDate"] = fridayStr,
                        ["slot"] = "Dinner",
                        ["customTitle"] = "Friday Takeout",
                    },
                },
            }
        );
        Assert.IsNotNull(planMealsResult);
        Assert.AreNotEqual(true, planMealsResult.IsError);

        // Verify via get_meal_plan and retrieve IDs
        var getMealPlanResult = await mcpClient.CallToolAsync(
            "get_meal_plan",
            new Dictionary<string, object?>
            {
                ["startDate"] = mondayStr,
                ["endDate"] = sundayStr,
                ["includeOnDeck"] = true,
            }
        );
        Assert.IsNotNull(getMealPlanResult);
        Assert.AreNotEqual(true, getMealPlanResult.IsError);

        using var mealDoc = JsonDocument.Parse(
            ((TextContentBlock)getMealPlanResult.Content[0]).Text
        );
        var entries = mealDoc.RootElement.GetProperty("scheduledEntries");
        Guid? fridayEntryId = null;
        Guid? tuesdayEntryId = null;

        foreach (var entry in entries.EnumerateArray())
        {
            var title = entry.TryGetProperty("displayTitle", out var dt) ? dt.GetString() : null;
            var id = entry.GetProperty("id").GetGuid();
            if (title == "Friday Takeout")
            {
                fridayEntryId = id;
            }
            if (title == "Leftovers")
            {
                tuesdayEntryId = id;
            }
        }

        Assert.IsNotNull(fridayEntryId);
        Assert.IsNotNull(tuesdayEntryId);

        // Reschedule Friday's note to Saturday
        var rescheduleResult = await mcpClient.CallToolAsync(
            "remove_meal_plan_entry",
            new Dictionary<string, object?>
            {
                ["entryId"] = fridayEntryId.Value.ToString(),
                ["rescheduleToDate"] = saturdayStr,
                ["rescheduleToSlot"] = "Dinner",
            }
        );
        Assert.IsNotNull(rescheduleResult);
        Assert.AreNotEqual(true, rescheduleResult.IsError);

        // Remove Tuesday's leftovers
        var removeResult = await mcpClient.CallToolAsync(
            "remove_meal_plan_entry",
            new Dictionary<string, object?> { ["entryId"] = tuesdayEntryId.Value.ToString() }
        );
        Assert.IsNotNull(removeResult);
        Assert.AreNotEqual(true, removeResult.IsError);

        // 6. Generate shopping list from meal plan into isolated list (auto-created via MCP)
        var generateResult = await mcpClient.CallToolAsync(
            "generate_shopping_list_from_meal_plan",
            new Dictionary<string, object?>
            {
                ["startDate"] = mondayStr,
                ["endDate"] = sundayStr,
                ["listName"] = listName,
                ["createListIfMissing"] = true,
                ["includeOnDeck"] = true,
                ["includeSubRecipes"] = true,
                ["includeOptionalIngredients"] = true,
                ["duplicatePolicy"] = "SkipAlreadyImported",
            }
        );
        Assert.IsNotNull(generateResult);
        var generateError =
            generateResult.Content.Count > 0
                ? ((TextContentBlock)generateResult.Content[0]).Text
                : "No content";
        Assert.AreNotEqual(true, generateResult.IsError, generateError);

        // Asserts the new list pill appears live in the browser via SignalR and select it
        var listPill = Page.Locator(".desktop-list-pills .list-pill", new() { HasText = listName });
        await Expect(listPill).ToBeVisibleAsync();
        await listPill.ClickAsync();
        await Expect(Page.Locator(".desktop-list-pills .list-pill.active"))
            .ToContainTextAsync(listName);

        // Asserts the open browser /shopping page shows generated items:
        // - Garlic scaled to 6 cloves (base 4 scaled to 8)
        // - Friday Takeout is excluded
        // - Kosher Salt in .pantry-staples-drawer
        var garlicRow = Page.Locator(
            ".active-items-card .shopping-item-row",
            new() { HasText = "Garlic" }
        );
        await Expect(garlicRow).ToBeVisibleAsync();
        await Expect(garlicRow.Locator(".item-qty-badge")).ToContainTextAsync("6 cloves");
        await Expect(Page.Locator(".active-items-card")).Not.ToContainTextAsync("Friday Takeout");

        var pantryDrawer = Page.Locator(".pantry-staples-drawer");
        await Expect(pantryDrawer).ToBeVisibleAsync();
        await Expect(pantryDrawer.Locator(".drawer-header-title"))
            .ToContainTextAsync("Verify Pantry Staples (Excluded) (1)");

        // 7. Upsert Paper Towels (qty 2) via MCP
        var upsertResult = await mcpClient.CallToolAsync(
            "upsert_shopping_list_items",
            new Dictionary<string, object?>
            {
                ["listName"] = listName,
                ["items"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["displayName"] = "Paper Towels",
                        ["quantity"] = 2,
                    },
                },
            }
        );
        Assert.IsNotNull(upsertResult);
        Assert.AreNotEqual(true, upsertResult.IsError);

        // Asserts Paper Towels appears live in open browser
        var paperTowelsRow = Page.Locator(
            ".active-items-card .shopping-item-row",
            new() { HasText = "Paper Towels" }
        );
        await Expect(paperTowelsRow).ToBeVisibleAsync();

        // 8. Set Paper Towels checked via MCP
        var checkResult = await mcpClient.CallToolAsync(
            "set_shopping_list_item_checked",
            new Dictionary<string, object?>
            {
                ["listName"] = listName,
                ["itemName"] = "Paper Towels",
                ["isChecked"] = true,
            }
        );
        Assert.IsNotNull(checkResult);
        Assert.AreNotEqual(true, checkResult.IsError);

        // Asserts Paper Towels moves into Checked Off (1) live in the open browser
        var checkedSection = Page.Locator(".checked-items-section");
        await Expect(checkedSection).ToBeVisibleAsync();
        await Expect(checkedSection.Locator(".checked-toggle-btn"))
            .ToContainTextAsync("Checked Off (1)");
        await Expect(
                checkedSection.Locator(".shopping-item-row", new() { HasText = "Paper Towels" })
            )
            .ToBeVisibleAsync();
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task SignalRCoShopping_MobileViewport_360px_RendersLiveSyncBadgeAndRecoversFromOffline()
    {
        await Page.SetViewportSizeAsync(360, 640);
        await RegisterAndLoginAsync();
        await EnsureShoppingListExistsViaApiAsync("Weekly Groceries", isPinned: true);

        // Navigate via mobile bottom nav Shop tab
        var shopTab = Page.Locator("a.mobile-tab[aria-label='Shop']");
        await Expect(shopTab).ToBeVisibleAsync();
        await shopTab.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/shopping.*"));

        // Asserts .sync-status-pill.live is visible
        var liveBadge = Page.Locator(".sync-status-pill.live");
        await Expect(liveBadge).ToBeVisibleAsync();
        await Expect(liveBadge).ToContainTextAsync("Live Sync");

        // Asserts zero horizontal overflow at 360px
        var isOverflowing = await Page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > window.innerWidth"
        );
        Assert.IsFalse(isOverflowing, "Page has horizontal scroll overflow at 360px");

        // Toggle offline and back online
        await Context.SetOfflineAsync(true);
        await Task.Delay(500);

        await Context.SetOfflineAsync(false);
        var offlineBtn = Page.Locator("button.sync-status-pill.offline");
        if (await offlineBtn.IsVisibleAsync())
        {
            await offlineBtn.ClickAsync();
        }

        // Verify client recovers to Live Sync
        await Expect(liveBadge).ToBeVisibleAsync(new() { Timeout = 10000 });

        // Quick add an item on mobile
        var quickAddInput = Page.Locator(".quick-add-bar input");
        await quickAddInput.FillAsync("Cold Brew");
        await Page.Locator("button.quick-add-submit-btn").ClickAsync();

        var coldBrewRow = Page.Locator(
            ".active-items-card .shopping-item-row",
            new() { HasText = "Cold Brew" }
        );
        await Expect(coldBrewRow).ToBeVisibleAsync();

        // Check off item
        await coldBrewRow.Locator("mat-checkbox").ClickAsync();
        await Expect(
                Page.Locator(
                    ".checked-items-section .shopping-item-row",
                    new() { HasText = "Cold Brew" }
                )
            )
            .ToBeVisibleAsync();

        // Re-verify zero horizontal overflow
        isOverflowing = await Page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > window.innerWidth"
        );
        Assert.IsFalse(
            isOverflowing,
            "Page has horizontal scroll overflow after operations at 360px"
        );
    }
}

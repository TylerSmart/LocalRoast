using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace App.Client.E2ETests;

[TestClass]
public sealed class FoodCatalogE2ETests : E2ETestBase
{
    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task FoodCatalog_DisplaysCreatedItems_AndFiltersByPantryStaplesAndSearch()
    {
        await RegisterAndLoginAsync();
        await EnsureFoodItemExistsViaApiAsync(
            "Extra-Virgin Olive Oil",
            isPantryStaple: true,
            aliases: ["EVOO", "Olive Oil"]
        );
        await EnsureFoodItemExistsViaApiAsync("Garlic", isPantryStaple: false);

        // Navigate to Catalog
        await Page.Locator("nav.desktop-nav a", new() { HasText = "Food Catalog" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/catalog.*"));
        await Expect(Page.Locator(".page-title")).ToContainTextAsync("Canonical Food Catalog");

        // Verify created item cards are displayed
        await Expect(Page.Locator(".food-item-card", new() { HasText = "Extra-Virgin Olive Oil" }))
            .ToBeVisibleAsync();
        await Expect(Page.Locator(".food-item-card", new() { HasText = "Garlic" }))
            .ToBeVisibleAsync();

        // Filter by Pantry Staples Only
        await Page.Locator("mat-slide-toggle button").ClickAsync();
        await Expect(Page.Locator(".food-item-card", new() { HasText = "Extra-Virgin Olive Oil" }))
            .ToBeVisibleAsync();
        await Expect(Page.Locator(".food-item-card", new() { HasText = "Garlic" }))
            .Not.ToBeVisibleAsync();

        // Reset Pantry Staples filter
        await Page.Locator("mat-slide-toggle button").ClickAsync();
        await Expect(Page.Locator(".food-item-card", new() { HasText = "Garlic" }))
            .ToBeVisibleAsync();
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task FoodCatalog_CreateItem_TogglePantryStaple_AndSearchByAlias()
    {
        await RegisterAndLoginAsync();

        await Page.Locator("nav.desktop-nav a", new() { HasText = "Food Catalog" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/catalog.*"));

        // Click Add Food Item
        await Page.Locator("button", new() { HasText = "Add Food Item" }).ClickAsync();
        await Expect(Page.Locator("mat-dialog-container h2")).ToContainTextAsync("New Food Item");

        var uniqueName = $"Fresh Herb {Guid.NewGuid():N}"[..20];
        var uniqueAlias = $"Green Magic {Guid.NewGuid():N}"[..18];

        var nameInput = Page.Locator("input[formcontrolname='name']");
        var aliasesInput = Page.Locator("input[formcontrolname='aliasesText']");
        await Expect(nameInput).ToBeFocusedAsync();
        await nameInput.FillAsync(uniqueName);
        await Expect(nameInput).ToHaveValueAsync(uniqueName);
        await aliasesInput.FillAsync(uniqueAlias);
        await Expect(aliasesInput).ToHaveValueAsync(uniqueAlias);

        await Page.Locator("mat-dialog-actions button", new() { HasText = "Create Item" })
            .ClickAsync();

        // Verify item card is in the catalog
        var card = Page.Locator(".food-item-card", new() { HasText = uniqueName });
        await Expect(card).ToBeVisibleAsync();
        await Expect(card.Locator(".alias-pill", new() { HasText = uniqueAlias }))
            .ToBeVisibleAsync();

        // Toggle Pantry Staple star button
        var stapleBtn = card.Locator("button[aria-label*='pantry staple']");
        var initialLabel = await stapleBtn.GetAttributeAsync("aria-label");
        await stapleBtn.ClickAsync();
        var expectedNextLabel =
            initialLabel?.Contains("Mark as") == true
                ? "Unmark pantry staple"
                : "Mark as pantry staple";
        await Expect(stapleBtn).ToHaveAttributeAsync("aria-label", expectedNextLabel);
        var starIcon = stapleBtn.Locator("mat-icon");
        await Expect(starIcon).ToHaveClassAsync(new Regex(".*staple-active.*"));
        var fontVariation = await starIcon.EvaluateAsync<string>(
            "el => getComputedStyle(el).fontVariationSettings"
        );
        Assert.Contains("\"FILL\" 1", fontVariation);

        // Search by Alias (wait for debounced /api/food-items?search=... request to complete before opening card menu)
        var searchInput = Page.Locator(".search-field input");
        var searchResponse = await Page.RunAndWaitForResponseAsync(
            () => searchInput.FillAsync(uniqueAlias),
            r =>
                r.Url.Contains("/api/food-items", StringComparison.Ordinal)
                && r.Url.Contains("search=", StringComparison.Ordinal)
                && r.Ok
        );
        Assert.IsTrue(searchResponse.Ok);
        await Expect(card).ToBeVisibleAsync();

        // Verify Merge, Edit, and Delete are grouped inside the card overflow menu
        await Expect(card.Locator("button[aria-label='Edit food item']")).ToHaveCountAsync(0);
        await Expect(card.Locator("button[aria-label='Delete food item']")).ToHaveCountAsync(0);
        await card.Locator(".food-item-menu-btn").ClickAsync();
        await Expect(Page.Locator("button.merge-item-btn")).ToBeVisibleAsync();
        await Expect(Page.Locator("button.edit-food-item-btn")).ToBeVisibleAsync();
        await Expect(Page.Locator("button.delete-food-item-btn")).ToBeVisibleAsync();
        await Page.Locator("button.edit-food-item-btn").ClickAsync();
        var editDialog = Page.Locator("app-food-item-dialog");
        await Expect(editDialog).ToBeVisibleAsync();
        await editDialog
            .Locator("button[aria-label='Close dialog'], button:has-text('Cancel')")
            .First.ClickAsync();
        await Expect(editDialog).Not.ToBeVisibleAsync();

        // Search for nonexistent term
        await searchInput.FillAsync("ZzzNonExistentFoodItem999");
        await Expect(Page.Locator(".empty-state h3")).ToContainTextAsync("No food items found");
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task FoodCatalog_RecipesUsingFoodItem_CanOpenDialog_AndUnlinkIngredient()
    {
        await RegisterAndLoginAsync();

        // 1. Create a canonical food item via API
        var foodItemId = await CreateFoodItemViaApiAsync("Organic Sweet Basil");

        // 2. Create a recipe using this food item
        var recipeTitle = $"Basil Pesto {Guid.NewGuid():N}"[..20];
        await CreateRecipeWithIngredientViaApiAsync(recipeTitle, "Organic Sweet Basil", foodItemId);

        // 3. Navigate to Food Catalog
        await Page.Locator("nav.desktop-nav a", new() { HasText = "Food Catalog" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/catalog.*"));

        var card = Page.Locator(".food-item-card", new() { HasText = "Organic Sweet Basil" });
        await Expect(card).ToBeVisibleAsync();

        // 4. Verify "1 recipe" button is present
        var recipesBtn = card.Locator(".usage-btn");
        await Expect(recipesBtn).ToBeVisibleAsync();
        await Expect(recipesBtn).ToContainTextAsync("1 recipe");

        // 5. Click button to open FoodItemRecipesDialog
        await recipesBtn.ClickAsync();

        var recipesDialog = Page.Locator("app-food-item-recipes-dialog");
        await Expect(recipesDialog).ToBeVisibleAsync();
        await Expect(recipesDialog.Locator(".dialog-title"))
            .ToContainTextAsync("Organic Sweet Basil");

        var recipeRow = recipesDialog.Locator(".recipe-usage-row", new() { HasText = recipeTitle });
        await Expect(recipeRow).ToBeVisibleAsync();

        // 6. Click "Unlink All"
        var unlinkBtn = recipesDialog.Locator(".unlink-all-btn");
        await unlinkBtn.ClickAsync();

        // Verify success banner appears
        await Expect(recipesDialog.Locator(".success-banner")).ToBeVisibleAsync();

        // Close dialog
        await recipesDialog
            .Locator("mat-dialog-actions button", new() { HasText = "Close" })
            .ClickAsync();
        await Expect(recipesDialog).Not.ToBeVisibleAsync();

        // Card should now display "0 recipes"
        await Expect(card.Locator(".usage-count.muted")).ToContainTextAsync("0 recipes");
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task FoodCatalog_SupportsSortingByNameRecipeUsageCreatedAndUpdated_AndPersistsInUrl()
    {
        await RegisterAndLoginAsync();

        var suffix = Guid.NewGuid().ToString("N")[..6];
        var betaName = $"Beta Herb {suffix}";
        var gammaName = $"Gamma Spice {suffix}";
        var alphaName = $"Alpha Flour {suffix}";

        var createdIds = await Page.EvaluateAsync<string[]>(
            @"async ({ betaName, gammaName, alphaName }) => {
                const tokenKey = Object.keys(localStorage).find(k => k.endsWith('-token'));
                const token = tokenKey ? localStorage.getItem(tokenKey) : null;
                const headers = {
                    'Content-Type': 'application/json',
                    'Authorization': `Bearer ${token}`
                };
                const items = [];
                for (const name of [betaName, gammaName, alphaName]) {
                    const res = await fetch('/api/food-items', {
                        method: 'POST',
                        headers,
                        body: JSON.stringify({ name, isPantryStaple: false, aliases: [] })
                    });
                    if (!res.ok) throw new Error('Failed to create food item: ' + res.status);
                    const data = await res.json();
                    items.push(data.id);
                    await new Promise(r => setTimeout(r, 35));
                }

                // Update Beta Herb so its ModifiedAt is newest
                const updateRes = await fetch(`/api/food-items/${items[0]}`, {
                    method: 'PUT',
                    headers,
                    body: JSON.stringify({ name: betaName, isPantryStaple: true, aliases: [] })
                });
                if (!updateRes.ok) throw new Error('Failed to update food item: ' + updateRes.status);

                return items;
            }",
            new
            {
                betaName,
                gammaName,
                alphaName,
            }
        );
        Assert.HasCount(3, createdIds);

        var betaId = Guid.Parse(createdIds[0]);
        var gammaId = Guid.Parse(createdIds[1]);

        // Gamma Spice -> 2 recipes, Beta Herb -> 1 recipe, Alpha Flour -> 0 recipes
        await CreateRecipeWithIngredientViaApiAsync($"Gamma Dish 1 {suffix}", gammaName, gammaId);
        await CreateRecipeWithIngredientViaApiAsync($"Gamma Dish 2 {suffix}", gammaName, gammaId);
        await CreateRecipeWithIngredientViaApiAsync($"Beta Dish 1 {suffix}", betaName, betaId);

        await Page.GotoAsync($"{ServerUrl}/catalog");
        await Expect(Page.Locator(".food-item-card", new() { HasText = betaName }))
            .ToBeVisibleAsync();

        var sortSelect = Page.GetByTestId("food-catalog-sort-select");
        var sortDirectionBtn = Page.GetByTestId("food-catalog-sort-direction");
        await Expect(sortSelect).ToBeVisibleAsync();
        await Expect(sortDirectionBtn).ToBeVisibleAsync();
        var sortBtnBox = await sortDirectionBtn.BoundingBoxAsync();
        Assert.IsNotNull(sortBtnBox);
        Assert.IsLessThanOrEqualTo(
            1.0,
            Math.Abs(sortBtnBox.Width - sortBtnBox.Height),
            $"Expected circular sort direction button, got {sortBtnBox.Width}x{sortBtnBox.Height}."
        );
        Assert.IsGreaterThanOrEqualTo(43.0, sortBtnBox.Width);
        Assert.IsLessThanOrEqualTo(45.0, sortBtnBox.Width);
        Assert.IsGreaterThanOrEqualTo(43.0, sortBtnBox.Height);
        Assert.IsLessThanOrEqualTo(45.0, sortBtnBox.Height);

        var targetNames = Page.Locator(".food-item-card .item-name")
            .Filter(new() { HasText = suffix });

        async Task AssertOrderAsync(params string[] expectedNames)
        {
            await Expect(targetNames).ToHaveTextAsync(expectedNames);
        }

        async Task SelectSortByAsync(string optionText)
        {
            await sortSelect.ClickAsync();
            var option = Page.Locator("mat-option", new() { HasText = optionText });
            var response = await Page.RunAndWaitForResponseAsync(
                () => option.ClickAsync(),
                r => r.Url.Contains("/api/food-items", StringComparison.Ordinal) && r.Ok
            );
            Assert.IsTrue(response.Ok);
        }

        async Task ToggleSortDirectionAsync()
        {
            var response = await Page.RunAndWaitForResponseAsync(
                () => sortDirectionBtn.ClickAsync(),
                r => r.Url.Contains("/api/food-items", StringComparison.Ordinal) && r.Ok
            );
            Assert.IsTrue(response.Ok);
        }

        // Default: Name (Asc) -> Alpha Flour, Beta Herb, Gamma Spice
        await AssertOrderAsync(alphaName, betaName, gammaName);

        // Select Recipe Usage (still Asc: least used -> most used)
        await SelectSortByAsync("Recipe Usage");
        await Expect(Page).ToHaveURLAsync(new Regex("[?&]sort=RecipeCount"));
        await AssertOrderAsync(alphaName, betaName, gammaName);

        // Toggle to Desc (most used -> least used): Gamma (2), Beta (1), Alpha (0)
        await ToggleSortDirectionAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("[?&]dir=Desc"));
        await AssertOrderAsync(gammaName, betaName, alphaName);

        // Reload page and confirm Recipe Usage Desc persists from URL query params
        await Page.ReloadAsync();
        await AssertOrderAsync(gammaName, betaName, alphaName);
        await Expect(sortSelect).ToContainTextAsync("Recipe Usage");
        await Expect(sortDirectionBtn.Locator("mat-icon")).ToHaveTextAsync("arrow_downward");

        // Switch to Created (still Desc) -> Alpha (3rd), Gamma (2nd), Beta (1st)
        await SelectSortByAsync("Created");
        await Expect(Page).ToHaveURLAsync(new Regex("[?&]sort=CreatedAt"));
        await AssertOrderAsync(alphaName, gammaName, betaName);

        // Toggle to Created Asc -> Beta, Gamma, Alpha
        await ToggleSortDirectionAsync();
        await AssertOrderAsync(betaName, gammaName, alphaName);

        // Switch to Updated (still Asc) -> Gamma, Alpha, Beta
        await SelectSortByAsync("Updated");
        await Expect(Page).ToHaveURLAsync(new Regex("[?&]sort=ModifiedAt"));
        await AssertOrderAsync(gammaName, alphaName, betaName);

        // Toggle to Updated Desc -> Beta, Alpha, Gamma
        await ToggleSortDirectionAsync();
        await AssertOrderAsync(betaName, alphaName, gammaName);

        // Verify 360px mobile viewport renders search & sort controls cleanly without horizontal overflow
        await Page.SetViewportSizeAsync(360, 800);
        await Expect(sortSelect).ToBeVisibleAsync();
        await Expect(sortDirectionBtn).ToBeVisibleAsync();
        Assert.IsTrue(
            await Page.EvaluateAsync<bool>(
                "document.documentElement.scrollWidth <= window.innerWidth + 1"
            ),
            "Viewport horizontal overflow detected at 360px on food catalog with sort controls."
        );
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task FoodCatalog_MultiSelectUnusedAndBulkDelete_RemovesSelectedItems_AndFitsMobile360px()
    {
        await RegisterAndLoginAsync();

        var suffix = Guid.NewGuid().ToString("N")[..6];
        var unusedA = $"Unused Herb A {suffix}";
        var unusedB = $"Unused Spice B {suffix}";
        var unusedC = $"Unused Flour C {suffix}";
        var usedGarlic = $"Used Garlic {suffix}";

        await CreateFoodItemViaApiAsync(unusedA);
        await CreateFoodItemViaApiAsync(unusedB);
        await CreateFoodItemViaApiAsync(unusedC);
        var usedId = await CreateFoodItemViaApiAsync(usedGarlic);
        await CreateRecipeWithIngredientViaApiAsync($"Garlic Soup {suffix}", usedGarlic, usedId);

        await Page.GotoAsync($"{ServerUrl}/catalog");
        var searchInput = Page.Locator(".search-field input");
        var searchResponse = await Page.RunAndWaitForResponseAsync(
            () => searchInput.FillAsync(suffix),
            r =>
                r.Url.Contains("/api/food-items", StringComparison.Ordinal)
                && r.Url.Contains($"search={suffix}", StringComparison.Ordinal)
                && r.Ok
        );
        Assert.IsTrue(searchResponse.Ok);

        await Expect(Page.Locator(".food-item-card")).ToHaveCountAsync(4);

        // 0. Verify Shift+Click range selection on checkboxes and card bodies (forward & reverse)
        var cards = Page.Locator(".food-item-card");
        await cards.Nth(0).GetByTestId("food-item-select-checkbox").ClickAsync();
        await Expect(Page.GetByTestId("food-catalog-select-all")).ToContainTextAsync("1 selected");

        await cards
            .Nth(2)
            .GetByTestId("food-item-select-checkbox")
            .ClickAsync(new() { Modifiers = [KeyboardModifier.Shift] });
        await Expect(Page.GetByTestId("food-catalog-select-all")).ToContainTextAsync("3 selected");
        await Expect(Page.Locator(".food-item-card.selected")).ToHaveCountAsync(3);
        await Expect(Page.Locator(".food-item-card", new() { HasText = usedGarlic }))
            .Not.ToHaveClassAsync(new Regex(".*\\bselected\\b.*"));

        var browserSelectionText = await Page.EvaluateAsync<string>(
            "() => window.getSelection()?.toString() ?? ''"
        );
        Assert.AreEqual(string.Empty, browserSelectionText);

        await Page.GetByTestId("food-catalog-clear-selection").ClickAsync();
        await Expect(Page.Locator(".food-item-card.selected")).ToHaveCountAsync(0);

        // Reverse Shift+Click directly on card body (.item-name)
        await cards.Nth(2).GetByTestId("food-item-select-checkbox").ClickAsync();
        await cards
            .Nth(0)
            .Locator(".item-name")
            .ClickAsync(new() { Modifiers = [KeyboardModifier.Shift] });
        await Expect(Page.GetByTestId("food-catalog-select-all")).ToContainTextAsync("3 selected");
        await Expect(Page.Locator(".food-item-card.selected")).ToHaveCountAsync(3);
        await Page.GetByTestId("food-catalog-clear-selection").ClickAsync();

        // 1. Click "Select unused (3)" and verify only unused cards are selected
        var selectUnusedBtn = Page.GetByTestId("food-catalog-select-unused");
        await Expect(selectUnusedBtn).ToBeVisibleAsync();
        await Expect(selectUnusedBtn).ToContainTextAsync("Select unused (3)");
        await selectUnusedBtn.ClickAsync();

        await Expect(Page.GetByTestId("food-catalog-select-all")).ToContainTextAsync("3 selected");
        await Expect(Page.Locator(".food-item-card.selected")).ToHaveCountAsync(3);
        await Expect(Page.Locator(".food-item-card", new() { HasText = usedGarlic }))
            .Not.ToHaveClassAsync(new Regex(".*\\bselected\\b.*"));

        // 2. Click "Delete Selected (3)" and confirm
        var deleteSelectedBtn = Page.GetByTestId("food-catalog-delete-selected");
        await Expect(deleteSelectedBtn).ToContainTextAsync("Delete Selected (3)");
        await deleteSelectedBtn.ClickAsync();

        var confirmDialog = Page.Locator("app-confirm-dialog");
        await Expect(confirmDialog).ToBeVisibleAsync();
        var deleteConfirmBtn = confirmDialog.Locator(
            "button",
            new() { HasText = "Delete 3 Items" }
        );
        await Expect(deleteConfirmBtn).ToBeVisibleAsync();

        var bulkDeleteResp = await Page.RunAndWaitForResponseAsync(
            () => deleteConfirmBtn.ClickAsync(),
            r =>
                r.Url.Contains("/api/food-items/bulk-delete", StringComparison.Ordinal)
                && r.Request.Method == "POST"
                && r.Ok
        );
        Assert.IsTrue(bulkDeleteResp.Ok);

        // Unused items removed, used item remains
        await Expect(Page.Locator(".food-item-card")).ToHaveCountAsync(1);
        await Expect(Page.Locator(".food-item-card", new() { HasText = usedGarlic }))
            .ToBeVisibleAsync();

        // 3. Select remaining used item via card checkbox and confirm Unlink & Delete (1)
        var usedCard = Page.Locator(".food-item-card", new() { HasText = usedGarlic });
        await usedCard.GetByTestId("food-item-select-checkbox").ClickAsync();
        await Expect(deleteSelectedBtn).ToContainTextAsync("Delete Selected (1)");
        await deleteSelectedBtn.ClickAsync();

        await Expect(confirmDialog).ToBeVisibleAsync();
        var unlinkDeleteBtn = confirmDialog.Locator(
            "button",
            new() { HasText = "Unlink & Delete (1)" }
        );
        await Expect(unlinkDeleteBtn).ToBeVisibleAsync();

        var unlinkBulkDeleteResp = await Page.RunAndWaitForResponseAsync(
            () => unlinkDeleteBtn.ClickAsync(),
            r =>
                r.Url.Contains("/api/food-items/bulk-delete", StringComparison.Ordinal)
                && r.Request.Method == "POST"
                && r.Ok
        );
        Assert.IsTrue(unlinkBulkDeleteResp.Ok);
        await Expect(Page.Locator(".empty-state h3")).ToContainTextAsync("No food items found");

        // 4. Verify 360px mobile viewport with selection bar active fits with zero horizontal overflow
        await CreateFoodItemViaApiAsync($"Mobile Item 1 {suffix}");
        await CreateFoodItemViaApiAsync($"Mobile Item 2 {suffix}");
        await Page.SetViewportSizeAsync(360, 800);
        await Page.ReloadAsync();
        await Expect(Page.Locator(".food-item-card")).ToHaveCountAsync(2);

        await Page.GetByTestId("food-catalog-select-unused").ClickAsync();
        await Expect(Page.GetByTestId("food-catalog-selection-bar")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("food-catalog-delete-selected")).ToBeVisibleAsync();
        Assert.IsTrue(
            await Page.EvaluateAsync<bool>(
                "document.documentElement.scrollWidth <= window.innerWidth + 1"
            ),
            "Viewport horizontal overflow detected at 360px on food catalog with selection bar active."
        );
    }
}

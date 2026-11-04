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

        // Search by Alias
        var searchInput = Page.Locator(".search-field input");
        await searchInput.FillAsync(uniqueAlias);
        await Expect(card).ToBeVisibleAsync();

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
        await recipesDialog.Locator("button", new() { HasText = "Close" }).ClickAsync();
        await Expect(recipesDialog).Not.ToBeVisibleAsync();

        // Card should now display "0 recipes"
        await Expect(card.Locator(".usage-count.muted")).ToContainTextAsync("0 recipes");
    }
}

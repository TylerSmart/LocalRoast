using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace App.Client.E2ETests;

[TestClass]
public sealed class FoodCatalogE2ETests : E2ETestBase
{
    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task FoodCatalog_DisplaysSeededAislesAndStaples_AndFiltersByAisle()
    {
        await RegisterAndLoginAsync();

        // Navigate to Catalog
        await Page.Locator("nav.desktop-nav a", new() { HasText = "Food Catalog" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/catalog.*"));
        await Expect(Page.Locator(".page-title")).ToContainTextAsync("Canonical Food Catalog");

        // Verify seeded staple item cards are displayed
        await Expect(Page.Locator(".food-item-card", new() { HasText = "Olive Oil" }))
            .ToBeVisibleAsync();
        await Expect(Page.Locator(".food-item-card", new() { HasText = "Garlic" }))
            .ToBeVisibleAsync();

        // Filter by Produce aisle
        await Page.Locator("mat-select", new() { HasText = "All Aisles" }).ClickAsync();
        await Page.Locator("mat-option", new() { HasText = "Produce" }).ClickAsync();
        await Expect(Page.Locator("mat-option")).ToHaveCountAsync(0);

        // Garlic should still be visible; Olive Oil should disappear
        await Expect(Page.Locator(".food-item-card", new() { HasText = "Garlic" }))
            .ToBeVisibleAsync();
        await Expect(Page.Locator(".food-item-card", new() { HasText = "Olive Oil" }))
            .Not.ToBeVisibleAsync();

        // Reset filter to All Aisles
        await Page.Locator("mat-select", new() { HasText = "Produce" }).ClickAsync();
        await Page.Locator("mat-option", new() { HasText = "All Aisles" }).ClickAsync();
        await Expect(Page.Locator(".food-item-card", new() { HasText = "Olive Oil" }))
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

        // Search by Alias
        var searchInput = Page.Locator(".search-field input");
        await searchInput.FillAsync(uniqueAlias);
        await Expect(card).ToBeVisibleAsync();

        // Search for nonexistent term
        await searchInput.FillAsync("ZzzNonExistentFoodItem999");
        await Expect(Page.Locator(".empty-state h3")).ToContainTextAsync("No food items found");
    }
}

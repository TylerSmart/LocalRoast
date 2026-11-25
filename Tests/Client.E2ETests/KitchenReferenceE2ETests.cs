using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace App.Client.E2ETests;

[TestClass]
public sealed class KitchenReferenceE2ETests : E2ETestBase
{
    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task KitchenReferenceDialog_TopBarAndKeyboardShortcut_ConvertsUnitsSearchesTermsAndStartsStandaloneTimer()
    {
        await RegisterAndLoginAsync();

        // 1. Open from top bar button
        await Page.GetByTestId("kitchen-reference-btn").ClickAsync();
        var dialog = Page.Locator("app-kitchen-reference-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(dialog.Locator("h2.dialog-title")).ToContainTextAsync("Kitchen Reference");

        // 2. Verify default Volume <-> Weight density conversion (1 cup All-Purpose Flour = 120 g)
        await Expect(dialog.GetByTestId("conversion-result-value")).ToContainTextAsync("120 g");

        await dialog.GetByTestId("converter-amount-input").FillAsync("2");
        await Expect(dialog.GetByTestId("conversion-result-value")).ToContainTextAsync("240 g");

        // 3. Switch to Baking Pans calculator
        await dialog.GetByTestId("submode-baking-pans").ClickAsync();
        await Expect(dialog.GetByTestId("pan-scaling-multiplier")).ToContainTextAsync("1.3x");

        // 4. Switch to Temps tab and filter Poultry
        await dialog.GetByTestId("ref-tab-temps").ClickAsync();
        await dialog
            .GetByRole(AriaRole.Button, new() { Name = "Poultry", Exact = true })
            .ClickAsync();
        await Expect(dialog.GetByTestId("temp-reference-card").First).ToContainTextAsync("165°F");

        // 5. Search culinary terms ("julienne", "blanch", "dash")
        var searchInput = dialog.GetByTestId("kitchen-reference-search-input");
        await searchInput.FillAsync("julienne");
        await Expect(dialog.GetByTestId("kitchen-reference-search-results"))
            .ToContainTextAsync("1/8\" × 1/8\" × 2\"");

        await searchInput.FillAsync("blanch");
        await Expect(dialog.GetByTestId("kitchen-reference-search-results"))
            .ToContainTextAsync("ice water");

        await searchInput.FillAsync("dash");
        await Expect(dialog.GetByTestId("kitchen-reference-search-results"))
            .ToContainTextAsync("1/8 tsp");

        await dialog
            .GetByRole(AriaRole.Button, new() { Name = "Clear reference search" })
            .ClickAsync();

        // 6. Check Swaps tab
        await dialog.GetByTestId("ref-tab-swaps").ClickAsync();
        await Expect(dialog).ToContainTextAsync("Buttermilk");

        // 7. Start a standalone timer from Ratios & Timers tab
        await dialog.GetByTestId("ref-tab-ratios").ClickAsync();
        await dialog.GetByTestId("quick-timer-preset-5").ClickAsync();
        await Expect(dialog.GetByTestId("reference-active-timers")).ToBeVisibleAsync();

        await dialog.GetByTestId("close-kitchen-reference-btn").ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();

        // Verify standalone timer pill appears in AppShell and reopens Ratios tab when clicked
        var timerPill = Page.Locator("button.active-timer-nav-pill");
        await Expect(timerPill).ToBeVisibleAsync();
        await timerPill.ClickAsync();
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(dialog.GetByTestId("reference-active-timers")).ToBeVisibleAsync();

        // 8. Verify Control+/ shortcut closes and reopens dialog
        await Page.Keyboard.PressAsync("Control+Slash");
        await Expect(dialog).Not.ToBeVisibleAsync();

        await Page.Keyboard.PressAsync("Control+Slash");
        await Expect(dialog).ToBeVisibleAsync();
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task KitchenReferenceDialog_CookModeAndPublicSharedRecipe_OpensCleanly()
    {
        await RegisterAndLoginAsync();
        var recipeId = await CreateRecipeWithIngredientViaApiAsync(
            $"Reference Test Roast {Guid.NewGuid():N}"[..24],
            "Kosher Salt"
        );

        // 1. Cook Mode access
        await Page.GotoAsync($"{ServerUrl}/recipes/{recipeId}/cook");
        await Expect(Page.Locator(".cook-step-card")).ToBeVisibleAsync();
        await Page.GetByTestId("cook-kitchen-reference-btn").ClickAsync();
        var dialog = Page.Locator("app-kitchen-reference-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        await dialog.GetByTestId("close-kitchen-reference-btn").ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();

        // 2. Public Shared Recipe access (unauthenticated guest context)
        await Page.GotoAsync($"{ServerUrl}/recipes/{recipeId}");
        await Page.GetByRole(AriaRole.Button, new() { Name = "More recipe actions" }).ClickAsync();
        await Page.GetByRole(AriaRole.Menuitem, new() { Name = "Share recipe", Exact = true })
            .ClickAsync();

        var shareDialog = Page.Locator("app-recipe-share-dialog");
        await Expect(shareDialog).ToBeVisibleAsync();
        await shareDialog.Locator("input[formcontrolname='label']").FillAsync("Guest Ref Share");
        await shareDialog
            .GetByRole(AriaRole.Button, new() { Name = "Create share link" })
            .ClickAsync();

        var linkCard = shareDialog.Locator(".link-card").First;
        await Expect(linkCard).ToBeVisibleAsync();
        await linkCard.Locator(".link-actions-menu-btn").ClickAsync();
        var shareUrl = await Page.Locator("a[aria-label='Open public view in new tab']")
            .First.GetAttributeAsync("href");
        Assert.IsNotNull(shareUrl);
        await Page.Keyboard.PressAsync("Escape");

        var (_, guestPage) = await CreateIsolatedPageAsync("guest-ref");
        await guestPage.GotoAsync(shareUrl);
        await Expect(guestPage.Locator(".recipe-title")).ToBeVisibleAsync();
        await guestPage.GetByTestId("public-kitchen-reference-btn").ClickAsync();
        await Expect(guestPage.Locator("app-kitchen-reference-dialog")).ToBeVisibleAsync();
    }

    [TestMethod]
    [DataRow(360, 640)]
    [DataRow(740, 360)]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task KitchenReferenceDialog_ResponsiveViewports_FitsWithoutHorizontalOverflow(
        int width,
        int height
    )
    {
        await RegisterAndLoginAsync();
        await Page.SetViewportSizeAsync(width, height);

        await Page.GetByTestId("kitchen-reference-btn").ClickAsync();
        var dialogContainer = Page.Locator("mat-dialog-container");
        await Expect(dialogContainer).ToBeVisibleAsync();

        foreach (var tab in new[] { "convert", "temps", "glossary", "swaps", "ratios" })
        {
            await dialogContainer.GetByTestId($"ref-tab-{tab}").ClickAsync();
            await Expect(dialogContainer.Locator("mat-dialog-actions")).ToBeInViewportAsync();

            var docOverflow = await Page.EvaluateAsync<bool>(
                "() => document.documentElement.scrollWidth > window.innerWidth + 1"
            );
            Assert.IsFalse(
                docOverflow,
                $"Document overflowed horizontally on tab '{tab}' at {width}x{height}."
            );

            var contentOverflow = await dialogContainer
                .Locator("mat-dialog-content")
                .EvaluateAsync<bool>("el => el.scrollWidth > el.clientWidth + 1");
            Assert.IsFalse(
                contentOverflow,
                $"Dialog content overflowed horizontally on tab '{tab}' at {width}x{height}."
            );
        }
    }
}

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

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task KitchenTimers_UnlockAudioOnStart_AndCountUpOvertimeAcrossAllViews()
    {
        await Page.AddInitScriptAsync(
            """
            (() => {
                window.__audioUnlockLog = {
                    bufferStarts: 0,
                    oscillatorStarts: 0,
                    resumes: 0
                };
                class SpyAudioContext {
                    constructor() {
                        this.state = 'suspended';
                        this.currentTime = 0;
                        this.destination = {};
                    }
                    resume() {
                        window.__audioUnlockLog.resumes++;
                        this.state = 'running';
                        return Promise.resolve();
                    }
                    createBuffer() { return {}; }
                    createBufferSource() {
                        return {
                            buffer: null,
                            connect() {},
                            start() { window.__audioUnlockLog.bufferStarts++; }
                        };
                    }
                    createOscillator() {
                        return {
                            type: 'sine',
                            frequency: { setValueAtTime() {} },
                            connect() {},
                            start() { window.__audioUnlockLog.oscillatorStarts++; },
                            stop() {}
                        };
                    }
                    createGain() {
                        return {
                            gain: { setValueAtTime() {}, exponentialRampToValueAtTime() {} },
                            connect() {}
                        };
                    }
                }
                window.AudioContext = SpyAudioContext;
                window.webkitAudioContext = SpyAudioContext;
            })();
            """
        );

        await RegisterAndLoginAsync();

        var recipeId = await Page.EvaluateAsync<string>(
            """
            async () => {
                const tokenKey = Object.keys(localStorage).find(k => k.endsWith('-token'));
                const token = tokenKey ? localStorage.getItem(tokenKey) : null;
                const res = await fetch('/api/recipes', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        'Authorization': `Bearer ${token}`
                    },
                    body: JSON.stringify({
                        title: 'Quick Boil Egg',
                        baseServings: 1,
                        ingredients: [{ displayName: 'Egg', quantity: 1 }],
                        steps: [{
                            stepNumber: 1,
                            instruction: 'Simmer briefly.',
                            timers: [{ sortOrder: 1, durationSeconds: 2, label: 'Quick Test' }]
                        }]
                    })
                });
                const data = await res.json();
                return data.id;
            }
            """
        );

        await Page.GotoAsync($"{ServerUrl}/recipes/{recipeId}/cook");
        await Expect(Page.Locator(".start-step-timer-btn")).ToBeVisibleAsync();

        // Start timer and verify synchronous WebKit AudioContext unlock on click
        await Page.Locator(".start-step-timer-btn").ClickAsync();
        var bufferStartsAfterClick = await Page.EvaluateAsync<int>(
            "() => window.__audioUnlockLog.bufferStarts"
        );
        Assert.IsGreaterThan(
            0,
            bufferStartsAfterClick,
            "Expected silent audio unlock buffer to play on Start click."
        );

        // Wait for timer completion and verify overtime count-up in Cook Mode + chime played
        var cookCountdown = Page.Locator(".timer-card .timer-countdown").First;
        await Expect(Page.Locator(".timer-card").First).ToHaveClassAsync(new Regex("is-completed"));
        await Expect(cookCountdown).ToHaveTextAsync(new Regex(@"^\+00:0[0-9]$"));
        await Expect(cookCountdown).ToHaveTextAsync(new Regex(@"^\+00:0[1-9]$"));

        var oscStartsAfterComplete = await Page.EvaluateAsync<int>(
            "() => window.__audioUnlockLog.oscillatorStarts"
        );
        Assert.IsGreaterThanOrEqualTo(
            4,
            oscStartsAfterComplete,
            "Expected completion chime oscillators to fire."
        );

        // Exit Cook Mode and verify AppShell top-bar pill shows completed state + overtime count-up
        await Page.GetByRole(AriaRole.Button, new() { Name = "Exit Cook Mode" }).ClickAsync();
        var navPill = Page.Locator(".active-timer-nav-pill.is-completed");
        await Expect(navPill).ToBeVisibleAsync();
        await Expect(navPill.Locator(".timer-nav-time"))
            .ToHaveTextAsync(new Regex(@"^\+00:0[1-9]$"));

        // Open Kitchen Reference Ratios & Timers tab and verify overtime count-up there as well
        await Page.GetByTestId("kitchen-reference-btn").ClickAsync();
        var refDialog = Page.Locator("app-kitchen-reference-dialog");
        await refDialog.GetByTestId("ref-tab-ratios").ClickAsync();
        await Expect(refDialog.Locator(".active-timer-row.is-completed .timer-row-countdown"))
            .ToHaveTextAsync(new Regex(@"^\+00:0[1-9]$"));
    }
}

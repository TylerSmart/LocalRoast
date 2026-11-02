using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace App.Client.E2ETests;

[TestClass]
public sealed class CookModeAndCookLogsE2ETests : E2ETestBase
{
    private static readonly byte[] ValidPngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="
    );

    private async Task CreateRecipeQuickAsync(
        string title,
        string ingredientName,
        string instruction,
        int baseServings = 4,
        int? timerMinutes = null,
        string? timerLabel = null
    )
    {
        await Page.GotoAsync($"{ServerUrl}/recipes/new");
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/new.*"));

        await Page.Locator("input[formcontrolname='title']").FillAsync(title);
        await Page.Locator("input[formcontrolname='baseServings']")
            .FillAsync(baseServings.ToString());
        await Page.Locator("input[formcontrolname='displayName']").First.FillAsync(ingredientName);
        await Page.Locator("input[formcontrolname='quantity']").First.FillAsync("2");
        await Page.Locator("textarea[formcontrolname='instruction']").First.FillAsync(instruction);

        if (timerMinutes.HasValue && !string.IsNullOrEmpty(timerLabel))
        {
            await Page.Locator(".add-timer-btn").First.ClickAsync();
            await Page.Locator("input[formcontrolname='minutes']")
                .First.FillAsync(timerMinutes.Value.ToString());
            await Page.Locator("input[formcontrolname='label']").First.FillAsync(timerLabel);
        }

        await Page.Locator("button[type='submit']", new() { HasText = "Save Recipe" })
            .First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+$"));
    }

    [TestMethod]
    public async Task CookMode_WakeLock_LiveScaling_StepNavigation_AndMultiTimers_CompletesAndLogsCook()
    {
        // 1. Install deterministic wake lock spy before loading page
        await Page.AddInitScriptAsync(
            @"
            Object.defineProperty(navigator, 'wakeLock', {
                value: {
                    request: async () => ({
                        released: false,
                        addEventListener: () => {},
                        removeEventListener: () => {},
                        release: async function() { this.released = true; }
                    })
                },
                configurable: true,
                writable: true
            });
        "
        );

        var (username, _) = await RegisterAndLoginAsync();

        // 2. Create recipe with 2 ingredients and a step timer
        await Page.Locator("a[routerlink='/recipes/new']").First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/new.*"));

        var recipeTitle = $"Honey Garlic Chicken {Guid.NewGuid():N}"[..26];
        await Page.Locator("input[formcontrolname='title']").FillAsync(recipeTitle);
        await Page.Locator("input[formcontrolname='baseServings']").FillAsync("4");
        await Page.Locator("textarea[formcontrolname='description']")
            .FillAsync("Crisp pan-seared chicken in sweet and savory garlic glaze.");

        // Ingredient 1: Soy Sauce (2 tbsp)
        await Page.Locator("input[formcontrolname='displayName']").First.FillAsync("Soy Sauce");
        await Page.Locator("input[formcontrolname='quantity']").First.FillAsync("2");
        await Page.Locator("mat-select[formcontrolname='unit']").First.ClickAsync();
        await Page.Locator("mat-option", new() { HasText = "Tablespoon (tbsp)" })
            .First.ClickAsync();

        // Ingredient 2: Garlic (4 clove)
        await Page.Locator("button", new() { HasText = "Add Ingredient" }).ClickAsync();
        var secondIng = Page.Locator(".builder-item-card").Nth(1);
        await secondIng.Locator("input[formcontrolname='displayName']").FillAsync("Garlic");
        await secondIng.Locator("input[formcontrolname='quantity']").FillAsync("4");
        await secondIng.Locator("mat-select[formcontrolname='unit']").ClickAsync();
        await Page.Locator("mat-option", new() { HasText = "Clove" }).First.ClickAsync();

        // Step 1: Simmer sauce with timer
        await Page.Locator("textarea[formcontrolname='instruction']")
            .First.FillAsync("Simmer sauce with garlic until glaze forms.");

        // Connect ingredients to Step 1 callouts
        var calloutChecks = Page.Locator(".callout-check");
        if (await calloutChecks.CountAsync() >= 2)
        {
            await calloutChecks.Nth(0).ClickAsync();
            await calloutChecks.Nth(1).ClickAsync();
        }

        // Add 15 min timer to Step 1
        await Page.Locator(".add-timer-btn").First.ClickAsync();
        await Page.Locator("input[formcontrolname='minutes']").First.FillAsync("15");
        await Page.Locator("input[formcontrolname='label']").First.FillAsync("Simmer glaze");

        // Step 2: Finish and serve
        await Page.GetByRole(AriaRole.Button, new() { Name = "Add Step", Exact = true })
            .ClickAsync();
        await Page.Locator("textarea[formcontrolname='instruction']")
            .Nth(1)
            .FillAsync("Garnish with scallions and serve hot.");

        await Page.Locator("button[type='submit']", new() { HasText = "Save Recipe" })
            .First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+$"));

        // 3. Launch Cook Mode
        var startCookingBtn = Page.Locator("button.start-cooking-btn");
        await Expect(startCookingBtn).ToBeVisibleAsync();
        await startCookingBtn.ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+/cook.*"));

        // Verify Wake Lock Pill is active
        var wakeLockPill = Page.Locator(".wake-lock-pill");
        await Expect(wakeLockPill).ToBeVisibleAsync();
        await Expect(wakeLockPill).ToContainTextAsync("Screen Awake");

        // Verify Step 1 is active
        await Expect(Page.Locator(".step-counter")).ToContainTextAsync("Step 1 of 2");

        // Verify pinned ingredients at 4 servings
        var ingredientsPane = Page.Locator(".cook-ingredients-pane");
        await Expect(ingredientsPane).ToContainTextAsync("Soy Sauce");
        await Expect(ingredientsPane).ToContainTextAsync("2 tbsp");
        await Expect(ingredientsPane).ToContainTextAsync("Garlic");
        await Expect(ingredientsPane).ToContainTextAsync("4 clove");

        // 4. Live scaling: scale to 2x (8 servings)
        await Page.Locator("button.mult-chip", new() { HasText = "2x" }).ClickAsync();
        await Expect(ingredientsPane).ToContainTextAsync("4 tbsp");
        await Expect(ingredientsPane).ToContainTextAsync("8 clove");

        // Toggle to Metric
        await Page.Locator("mat-button-toggle[value='Metric'] button").ClickAsync();
        await Expect(ingredientsPane).ToContainTextAsync("ml");

        // 5. Multi-timer Engine
        // Start Step 1's timer ("Simmer glaze")
        var stepTimerBtn = Page.Locator("button.start-step-timer-btn").First;
        await Expect(stepTimerBtn).ToBeVisibleAsync();
        await stepTimerBtn.ClickAsync();

        // Verify floating timer tray is visible
        var timerTray = Page.Locator(".floating-timer-tray");
        await Expect(timerTray).ToBeVisibleAsync();
        await Expect(timerTray).ToContainTextAsync("Simmer glaze");

        // Add a custom timer (+ Custom Timer)
        await Page.Locator("button.custom-timer-toggle-btn").ClickAsync();
        var customForm = Page.Locator(".custom-timer-form");
        await Expect(customForm).ToBeVisibleAsync();
        await customForm.Locator("input.custom-timer-input").FillAsync("Toast sesame");
        await customForm.Locator("input.custom-timer-mins").FillAsync("1");
        await customForm.Locator("button.start-custom-btn").ClickAsync();

        // Verify 2 active timer cards in tray
        var timerCards = timerTray.Locator(".timer-card");
        await Expect(timerCards).ToHaveCountAsync(2);

        // Bump "Simmer glaze" timer by +1m
        var simmerCard = timerTray.Locator(".timer-card", new() { HasText = "Simmer glaze" });
        await simmerCard.Locator("button.bump-btn").ClickAsync();

        // Pause and Resume "Simmer glaze"
        await simmerCard.Locator("button[aria-label='Pause timer']").ClickAsync();
        await Expect(simmerCard).ToHaveClassAsync(new Regex(".*is-paused.*"));
        await simmerCard.Locator("button[aria-label='Resume timer']").ClickAsync();
        await Expect(simmerCard).Not.ToHaveClassAsync(new Regex(".*is-paused.*"));

        // Dismiss "Toast sesame" timer
        var sesameCard = timerTray.Locator(".timer-card", new() { HasText = "Toast sesame" });
        await sesameCard.Locator("button.dismiss-btn").ClickAsync();
        await Expect(timerTray.Locator(".timer-card")).ToHaveCountAsync(1);

        // 6. Navigate to Step 2 using keyboard ArrowRight
        await Page.Keyboard.PressAsync("ArrowRight");
        await Expect(Page.Locator(".step-counter")).ToContainTextAsync("Step 2 of 2");

        // Final step button reads "Finish & Log Cook"
        var finishBtn = Page.Locator("button.next-btn");
        await Expect(finishBtn).ToContainTextAsync("Finish & Log Cook");
        await finishBtn.ClickAsync();

        // 7. Complete Cook Log Dialog
        var cookDialog = Page.Locator("app-cook-log-dialog");
        await Expect(cookDialog).ToBeVisibleAsync();

        // 5 Star rating
        await cookDialog.Locator("button[aria-label='5 stars']").ClickAsync();

        // Notes
        await cookDialog
            .Locator("textarea[formcontrolname='notes']")
            .FillAsync("Doubled the lime zest and seared 2 mins longer");

        // Upload dish photo
        await cookDialog
            .Locator("input[type='file']")
            .SetInputFilesAsync(
                new FilePayload
                {
                    Name = "dish.png",
                    MimeType = "image/png",
                    Buffer = ValidPngBytes,
                }
            );
        await Expect(cookDialog.Locator(".photo-preview-card")).ToBeVisibleAsync();

        // Submit
        await cookDialog.Locator("button.submit-btn").ClickAsync();
        await Expect(cookDialog).Not.ToBeVisibleAsync();

        // 8. Assertions on Recipe Detail Page
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+$"));

        // Stats strip shows Cooked 1x
        await Expect(Page.Locator(".stat-pill:has-text('Cooked')")).ToContainTextAsync("1x");

        // Latest tweak banner at top of recipe
        var tweakBanner = Page.Locator(".latest-tweak-banner");
        await Expect(tweakBanner).ToBeVisibleAsync();
        await Expect(tweakBanner)
            .ToContainTextAsync("Doubled the lime zest and seared 2 mins longer");

        // Cook history section
        var historySection = Page.Locator(".cook-history-section");
        await Expect(historySection).ToBeVisibleAsync();
        await Expect(historySection).ToContainTextAsync(username);
        await Expect(historySection.Locator("img.log-thumb-img")).ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task CookLogs_SearchByTweakNote_ForgottenFavoritesFilter_MealPlanStartCooking_AndPublicShareOptIn()
    {
        var (username, email) = await RegisterAndLoginAsync();

        // 1. Create Recipe A: "Sunday Beef Bourguignon"
        var bourguignonTitle = $"Beef Bourguignon {Guid.NewGuid():N}"[..24];
        await CreateRecipeQuickAsync(
            bourguignonTitle,
            "Beef Chuck",
            "Brown beef thoroughly and simmer with red wine.",
            4
        );

        var urlA = Page.Url;
        var matchA = Regex.Match(urlA, @"/recipes/([a-f0-9-]+)$");
        Assert.IsTrue(matchA.Success, "Expected recipe A URL to contain GUID.");
        var recipeAId = matchA.Groups[1].Value;

        // Seed two older cook logs on Recipe A via authenticated fetch
        await Page.EvaluateAsync(
            @"async (args) => {
                const tokenKey = Object.keys(localStorage).find(k => k.endsWith('-token'));
                const token = tokenKey ? localStorage.getItem(tokenKey) : null;
                const headers = { 'Content-Type': 'application/json' };
                if (token) headers['Authorization'] = `Bearer ${token}`;

                const res1 = await fetch(`/api/recipes/${args.id}/cook-logs`, {
                    method: 'POST',
                    headers,
                    body: JSON.stringify({
                        cookedAt: args.cookedAt1,
                        rating: 5,
                        notes: 'Secret splash of aged balsamic',
                        servingsMade: 4
                    })
                });
                if (!res1.ok) throw new Error(`Failed to create log 1: ${res1.status} ${await res1.text()}`);

                const res2 = await fetch(`/api/recipes/${args.id}/cook-logs`, {
                    method: 'POST',
                    headers,
                    body: JSON.stringify({
                        cookedAt: args.cookedAt2,
                        rating: 4,
                        notes: 'Great with mushrooms',
                        servingsMade: 4
                    })
                });
                if (!res2.ok) throw new Error(`Failed to create log 2: ${res2.status} ${await res2.text()}`);
            }",
            new
            {
                id = recipeAId,
                cookedAt1 = DateTimeOffset.UtcNow.AddDays(-80).ToString("o"),
                cookedAt2 = DateTimeOffset.UtcNow.AddDays(-70).ToString("o"),
            }
        );

        // 2. Create Recipe B: "Quick Weeknight Quesadillas" (Cooked today)
        var quesadillasTitle = $"Quesadillas {Guid.NewGuid():N}"[..20];
        await CreateRecipeQuickAsync(
            quesadillasTitle,
            "Tortillas",
            "Warm tortillas with cheese and fold.",
            4
        );

        var urlB = Page.Url;
        var matchB = Regex.Match(urlB, @"/recipes/([a-f0-9-]+)$");
        Assert.IsTrue(matchB.Success, "Expected recipe B URL to contain GUID.");
        var recipeBId = matchB.Groups[1].Value;

        // Seed cook log for today on Recipe B
        await Page.EvaluateAsync(
            @"async (args) => {
                const tokenKey = Object.keys(localStorage).find(k => k.endsWith('-token'));
                const token = tokenKey ? localStorage.getItem(tokenKey) : null;
                const headers = { 'Content-Type': 'application/json' };
                if (token) headers['Authorization'] = `Bearer ${token}`;

                const res = await fetch(`/api/recipes/${args.id}/cook-logs`, {
                    method: 'POST',
                    headers,
                    body: JSON.stringify({
                        cookedAt: args.cookedAt,
                        rating: 5,
                        notes: 'Quick dinner',
                        servingsMade: 4
                    })
                });
                if (!res.ok) throw new Error(`Failed to create log for B: ${res.status} ${await res.text()}`);
            }",
            new { id = recipeBId, cookedAt = DateTimeOffset.UtcNow.ToString("o") }
        );

        // 3. Test Global Search matches cook notes
        await Page.Keyboard.PressAsync("Control+k");
        var searchDialog = Page.Locator("app-global-search-dialog");
        await Expect(searchDialog).ToBeVisibleAsync();

        var searchInput = searchDialog.Locator("input.dialog-search-input");
        await searchInput.FillAsync("aged balsamic");

        // Expect Beef Bourguignon to appear in search results
        var resultItem = searchDialog.Locator(".result-item", new() { HasText = bourguignonTitle });
        await Expect(resultItem).ToBeVisibleAsync();

        // Close search dialog
        await Page.Keyboard.PressAsync("Escape");
        await Expect(searchDialog).Not.ToBeVisibleAsync();

        // 4. Test Forgotten Favorites Filter in Cookbook
        await Page.GotoAsync($"{ServerUrl}/recipes");
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes.*"));

        // Toggle Forgotten Favorites pill
        var forgottenPill = Page.Locator("button.forgotten-favorites-pill");
        await Expect(forgottenPill).ToBeVisibleAsync();
        await forgottenPill.ClickAsync();

        // Beef Bourguignon (cooked 70+ days ago, rated 4.5) is shown
        var recipeCardA = Page.Locator(
            "mat-card.recipe-card",
            new() { HasText = bourguignonTitle }
        );
        await Expect(recipeCardA).ToBeVisibleAsync();

        // Quesadillas (cooked today) is excluded
        var recipeCardB = Page.Locator(
            "mat-card.recipe-card",
            new() { HasText = quesadillasTitle }
        );
        await Expect(recipeCardB).Not.ToBeVisibleAsync();

        // Toggle off
        await forgottenPill.ClickAsync();

        // 5. Schedule Quesadillas onto Meal Plan and Launch Cook Mode with pre-scaled servings
        await Page.GotoAsync($"{ServerUrl}/recipes/{recipeBId}");
        await Page.Locator("button[aria-label='Plan meal']").ClickAsync();

        var planDialog = Page.Locator("mat-dialog-container");
        await Expect(planDialog).ToBeVisibleAsync();

        // Increase servings from 4 to 8
        var incBtn = planDialog.Locator("button[aria-label='Increase servings']");
        await incBtn.ClickAsync();
        await incBtn.ClickAsync();
        await incBtn.ClickAsync();
        await incBtn.ClickAsync();

        // Plan meal
        await planDialog
            .Locator("button[color='primary']", new() { HasText = "Plan Meal" })
            .ClickAsync();
        await Expect(planDialog).Not.ToBeVisibleAsync();

        // Navigate to Meal Plan
        await Page.GotoAsync($"{ServerUrl}/meal-plan");
        await Expect(Page).ToHaveURLAsync(new Regex(".*/meal-plan.*"));

        // Click 1-tap Start Cooking button on Today's scheduled card
        var startCookingMealBtn = Page.Locator("button.start-cooking-meal-btn").First;
        await Expect(startCookingMealBtn).ToBeVisibleAsync();
        await startCookingMealBtn.ClickAsync();

        // Verify Cook Mode loaded with 8 servings
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+/cook.*"));
        await Expect(Page.Locator(".servings-val")).ToContainTextAsync("8");

        // 6. Test Public Share Link Privacy Boundary
        await Page.GotoAsync($"{ServerUrl}/recipes/{recipeAId}");

        // Create share link WITHOUT cook logs
        await Page.Locator("button[aria-label='Share recipe']").ClickAsync();
        var shareDialog = Page.Locator("app-recipe-share-dialog");
        await Expect(shareDialog).ToBeVisibleAsync();

        await shareDialog.Locator("input[formcontrolname='label']").FillAsync("Public No Logs");
        await shareDialog.Locator("button[aria-label='Create share link']").ClickAsync();

        var linkNoLogs = shareDialog.Locator(".link-card", new() { HasText = "Public No Logs" });
        await Expect(linkNoLogs).ToBeVisibleAsync();
        var urlNoLogs = await linkNoLogs
            .Locator("a[aria-label='Open public view in new tab']")
            .GetAttributeAsync("href");
        Assert.IsNotNull(urlNoLogs);

        // Create share link WITH cook logs
        await shareDialog.Locator("input[formcontrolname='label']").FillAsync("Public With Logs");
        await shareDialog.Locator("mat-checkbox[formcontrolname='includeCookLogs']").ClickAsync();
        await shareDialog.Locator("button[aria-label='Create share link']").ClickAsync();

        var linkWithLogs = shareDialog.Locator(
            ".link-card",
            new() { HasText = "Public With Logs" }
        );
        await Expect(linkWithLogs).ToBeVisibleAsync();
        var urlWithLogs = await linkWithLogs
            .Locator("a[aria-label='Open public view in new tab']")
            .GetAttributeAsync("href");
        Assert.IsNotNull(urlWithLogs);

        // 7. Verify in isolated unauthenticated context
        var anonContext = await Browser.NewContextAsync();
        try
        {
            var anonPage = await anonContext.NewPageAsync();

            // Link without logs: notes section is hidden and tweak note does not leak
            await anonPage.GotoAsync(urlNoLogs);
            await Expect(anonPage.Locator(".recipe-title")).ToContainTextAsync(bourguignonTitle);
            await Expect(anonPage.Locator(".public-cook-notes-section")).Not.ToBeVisibleAsync();

            var contentNoLogs = await anonPage.ContentAsync();
            Assert.DoesNotContain(
                "Secret splash of aged balsamic",
                contentNoLogs,
                "Private notes must not leak when includeCookLogs is false."
            );

            // Link with logs: notes section is visible and contains tweak note, but ZERO user attribution
            await anonPage.GotoAsync(urlWithLogs);
            await Expect(anonPage.Locator(".recipe-title")).ToContainTextAsync(bourguignonTitle);

            var notesSection = anonPage.Locator(".public-cook-notes-section");
            await Expect(notesSection).ToBeVisibleAsync();
            await Expect(notesSection).ToContainTextAsync("Secret splash of aged balsamic");

            var contentWithLogs = await anonPage.ContentAsync();
            Assert.DoesNotContain(
                username,
                contentWithLogs,
                "Chef username must not leak in public view."
            );
            Assert.DoesNotContain(
                email,
                contentWithLogs,
                "Chef email must not leak in public view."
            );
        }
        finally
        {
            await anonContext.CloseAsync();
        }
    }

    [TestMethod]
    public async Task CookMode_MobileViewport_360px_FullScreenSteps_IngredientsDrawer_AndTimerTray_ZeroOverflow()
    {
        // Set mobile viewport to 360x640 (standard Android phone)
        await Page.SetViewportSizeAsync(360, 640);

        await RegisterAndLoginAsync();

        var title = $"Campfire Skillet {Guid.NewGuid():N}"[..22];
        await CreateRecipeQuickAsync(
            title,
            "Baby Potatoes",
            "Dice potatoes and pan fry in seasoned butter.",
            2,
            timerMinutes: 10,
            timerLabel: "Fry potatoes"
        );

        // Launch Cook Mode
        var startCookingBtn = Page.Locator("button.start-cooking-btn");
        await Expect(startCookingBtn).ToBeVisibleAsync();
        await startCookingBtn.ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+/cook.*"));

        // Check zero horizontal overflow on initial load
        var initialOverflow = await Page.EvaluateAsync<bool>(
            @"() => document.documentElement.scrollWidth > document.documentElement.clientWidth"
        );
        Assert.IsFalse(
            initialOverflow,
            "Cook Mode initial view at 360px must have zero horizontal overflow."
        );

        // Open mobile ingredients drawer
        var openDrawerBtn = Page.Locator("button.mobile-ingredients-btn");
        await Expect(openDrawerBtn).ToBeVisibleAsync();
        await openDrawerBtn.ClickAsync();

        var mobileDrawer = Page.Locator(".mobile-ingredients-drawer");
        await Expect(mobileDrawer).ToBeVisibleAsync();

        // Check off ingredient
        var firstItem = mobileDrawer.Locator(".ingredient-item").First;
        await firstItem.ClickAsync();
        await Expect(firstItem).ToHaveClassAsync(new Regex(".*is-checked.*"));

        // Close drawer via backdrop button
        var backdropBtn = Page.Locator("button.mobile-drawer-backdrop");
        await backdropBtn.ClickAsync();
        await Expect(mobileDrawer).Not.ToBeVisibleAsync();

        // Start step timer
        var timerBtn = Page.Locator("button.start-step-timer-btn").First;
        await Expect(timerBtn).ToBeVisibleAsync();
        await timerBtn.ClickAsync();

        // Verify timer tray visible
        var timerTray = Page.Locator(".floating-timer-tray");
        await Expect(timerTray).ToBeVisibleAsync();

        // Check zero horizontal overflow with active timer tray at 360px
        var timerOverflow = await Page.EvaluateAsync<bool>(
            @"() => document.documentElement.scrollWidth > document.documentElement.clientWidth"
        );
        Assert.IsFalse(
            timerOverflow,
            "Cook Mode with active floating timer tray at 360px must have zero horizontal overflow."
        );
    }
}

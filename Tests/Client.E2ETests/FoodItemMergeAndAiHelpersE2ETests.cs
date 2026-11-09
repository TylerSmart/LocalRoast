using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace App.Client.E2ETests;

public sealed class MockKitchenAiOllamaServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    public string BaseUrl { get; }
    public bool SimulateOffline { get; set; }
    public string? LastKitchenPrompt { get; private set; }

    private MockKitchenAiOllamaServer(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public static async Task<MockKitchenAiOllamaServer> StartAsync()
    {
        var port = GetFreePort();
        var baseUrl = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));

        var app = builder.Build();
        var server = new MockKitchenAiOllamaServer(app, baseUrl);

        app.MapGet(
            "/api/tags",
            () =>
            {
                if (server.SimulateOffline)
                {
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }

                return Results.Ok(
                    new
                    {
                        models = new[] { new { name = "llama3:8b" }, new { name = "qwen2.5:14b" } },
                    }
                );
            }
        );

        app.MapPost(
            "/api/chat",
            async (HttpRequest request) =>
            {
                if (server.SimulateOffline)
                {
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }

                using var reader = new StreamReader(request.Body);
                var bodyText = await reader.ReadToEndAsync();
                using var body = JsonDocument.Parse(bodyText);
                var formatProperties = body
                    .RootElement.GetProperty("format")
                    .GetProperty("properties");

                string payloadJson;
                if (formatProperties.TryGetProperty("answer", out _))
                {
                    server.LastKitchenPrompt = body
                        .RootElement.GetProperty("messages")[1]
                        .GetProperty("content")
                        .GetString();
                    payloadJson = """
                        { "answer": "Julienne means slicing food into thin matchsticks." }
                        """;
                }
                else if (
                    bodyText.Contains("draft_meal_plan")
                    || bodyText.Contains("proposals")
                    || bodyText.Contains("meal planning")
                )
                {
                    var prompt = body
                        .RootElement.GetProperty("messages")
                        .EnumerateArray()
                        .Last(m => m.GetProperty("role").GetString() == "user")
                        .GetProperty("content")
                        .GetString()!;
                    var requested = int.Parse(
                        Regex
                            .Match(
                                prompt,
                                @"Requested Number of Cooking Occasions \(not counting leftovers\): (\d+)"
                            )
                            .Groups[1]
                            .Value
                    );
                    var catalog = Regex
                        .Matches(
                            prompt,
                            @"^\[(\d+)\] (.+?) \| BaseServings:",
                            RegexOptions.Multiline
                        )
                        .OrderBy(match =>
                            match.Groups[2].Value switch
                            {
                                "Tuscan Garlic Chicken" => 0,
                                "Veggie Stir Fry" => 1,
                                "Lentil Soup" => 2,
                                _ => 3,
                            }
                        )
                        .ToArray();
                    payloadJson = JsonSerializer.Serialize(
                        new
                        {
                            proposals = Enumerable
                                .Range(0, requested)
                                .Select(i => new
                                {
                                    catalogIndex = int.Parse(
                                        catalog[i % catalog.Length].Groups[1].Value
                                    ),
                                    recipeTitle = catalog[i % catalog.Length].Groups[2].Value,
                                    dayOffset = i,
                                    slot = "Dinner",
                                    isOnDeck = false,
                                    targetServings = 4,
                                    rationale = "High-protein family favorite from cookbook.",
                                }),
                        }
                    );
                }
                else
                {
                    // Ingredient substitution
                    payloadJson = """
                        {
                          "substitutions": [
                            {
                              "label": "Greek Yogurt + Lemon Juice",
                              "ratioAndAmount": "1 cup per 1 cup buttermilk",
                              "culinaryNotes": "Whisk 3/4 cup plain Greek yogurt with 1/4 cup milk and 1 tsp lemon juice.",
                              "flavorOrTextureImpact": "Provides identical acidity and creamy body without curdling."
                            },
                            {
                              "label": "Milk + Vinegar",
                              "ratioAndAmount": "1 cup whole milk + 1 tbsp white vinegar",
                              "culinaryNotes": "Let stand for 5 minutes until curdled.",
                              "flavorOrTextureImpact": "Classic pantry substitute with light acidity."
                            }
                          ]
                        }
                        """;
                }

                return Results.Ok(
                    new
                    {
                        model = "llama3:8b",
                        message = new { role = "assistant", content = payloadJson },
                    }
                );
            }
        );

        await app.StartAsync();
        return server;
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

[TestClass]
public sealed class FoodItemMergeAndAiHelpersE2ETests : E2ETestBase
{
    private async Task ConfigureLlmProviderAsync(string baseUrl, string modelName = "llama3:8b")
    {
        await Page.Locator(".user-profile-btn").ClickAsync();
        await Page.Locator("button", new() { HasText = "LLM Provider Settings" }).ClickAsync();

        var dialog = Page.Locator("app-llm-settings-dialog");
        await Expect(dialog).ToBeVisibleAsync();

        var baseUrlInput = dialog.Locator("input[formcontrolname='baseUrl']");
        await baseUrlInput.FillAsync(baseUrl);

        await dialog
            .Locator("button[aria-label='Test LLM connection and refresh models']")
            .ClickAsync();
        var statusBanner = dialog.Locator(".connection-status-banner.status-success");
        await Expect(statusBanner).ToBeVisibleAsync();

        var textModelInput = dialog.Locator("input[formcontrolname='textModel']");
        await textModelInput.FillAsync(modelName);

        // Close autocomplete overlay
        await Page.Keyboard.PressAsync("Escape");

        await dialog
            .Locator("button", new() { HasText = "Save Settings" })
            .ClickAsync(new() { Force = true });
        await Expect(dialog).Not.ToBeVisibleAsync();
    }

    private async Task<string> CreateRecipeWithIngredientAsync(
        string title,
        string ingredientName,
        string instruction = "Heat skillet and cook ingredients thoroughly for 5 minutes.",
        int baseServings = 4,
        string? preparationNote = null
    )
    {
        await Page.Locator("nav.desktop-nav a", new() { HasText = "Cookbook" }).ClickAsync();
        await Page.Locator("a[routerlink='/recipes/new']").First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/new.*"));

        await Page.Locator("input[formcontrolname='title']").FillAsync(title);
        await Page.Locator("input[formcontrolname='baseServings']")
            .FillAsync(baseServings.ToString());
        await Page.Locator("input[formcontrolname='displayName']").First.FillAsync(ingredientName);
        await Page.Locator("input[formcontrolname='quantity']").First.FillAsync("1");
        if (preparationNote is not null)
        {
            await Page.Locator("input[formcontrolname='preparationNote']")
                .First.FillAsync(preparationNote);
        }
        await Page.Locator("textarea[formcontrolname='instruction']").First.FillAsync(instruction);

        // Add a step timer so cook mode has rich step content
        await Page.Locator(".add-timer-btn").First.ClickAsync();
        await Page.Locator("input[formcontrolname='minutes']").First.FillAsync("5");
        await Page.Locator("input[formcontrolname='label']").First.FillAsync("Simmer");

        await Page.Locator("button[type='submit']", new() { HasText = "Save Recipe" })
            .First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+$"));

        // Extract recipe URL
        return Page.Url;
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task FoodItemMerge_ConsolidatesDuplicates_UpdatesShoppingReferences_AndAliasesSurvivingItem()
    {
        await RegisterAndLoginAsync();

        // 1. Navigate to Food Catalog
        await Page.Locator("nav.desktop-nav a", new() { HasText = "Food Catalog" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/catalog.*"));
        await Expect(Page.Locator(".page-title")).ToContainTextAsync("Canonical Food Catalog");

        // 2. Create duplicate food items with unique names
        var targetName = $"Heirloom Tomato {Guid.NewGuid():N}"[..22];
        var sourceName = $"Heirloom Tomatoes Bulk {Guid.NewGuid():N}"[..24];

        // Create target item
        await Page.Locator("button", new() { HasText = "Add Food Item" }).ClickAsync();
        var addDialog = Page.Locator("mat-dialog-container");
        await Expect(addDialog.Locator("h2")).ToContainTextAsync("New Food Item");
        await addDialog.Locator("input[formcontrolname='name']").FillAsync(targetName);
        await addDialog.Locator("button", new() { HasText = "Create Item" }).ClickAsync();
        await Expect(addDialog).Not.ToBeVisibleAsync();

        // Create source item
        await Page.Locator("button", new() { HasText = "Add Food Item" }).ClickAsync();
        await Expect(addDialog.Locator("h2")).ToContainTextAsync("New Food Item");
        await addDialog.Locator("input[formcontrolname='name']").FillAsync(sourceName);
        await addDialog.Locator("button", new() { HasText = "Create Item" }).ClickAsync();
        await Expect(addDialog).Not.ToBeVisibleAsync();

        // 3. Open Merge Duplicates Dialog
        await Page.Locator(".merge-duplicates-btn").ClickAsync();
        var mergeDialog = Page.Locator("app-merge-food-items-dialog");
        await Expect(mergeDialog).ToBeVisibleAsync();

        // Select Target Item
        await mergeDialog.Locator(".target-select").ClickAsync();
        await Page.Locator("mat-option", new() { HasText = targetName }).ClickAsync();

        // Select Source Item
        await mergeDialog.Locator(".source-select").ClickAsync();
        await Page.Locator("mat-option", new() { HasText = sourceName }).ClickAsync();
        await Page.Keyboard.PressAsync("Escape");

        // 4. Test Swap Target & Source
        var swapBtn = mergeDialog.Locator(".swap-btn");
        await Expect(swapBtn).ToBeVisibleAsync();
        await swapBtn.ClickAsync();

        // Swap back so targetName is target
        await swapBtn.ClickAsync();

        // 5. Confirm Merge
        var confirmBtn = mergeDialog.Locator(".confirm-merge-btn");
        await Expect(confirmBtn).ToBeEnabledAsync();
        await confirmBtn.ClickAsync();

        await Expect(mergeDialog).Not.ToBeVisibleAsync();

        // 6. Verify sourceName is now deleted from catalog and target item remains
        await Page.Locator(".search-field input").FillAsync(targetName);
        await Expect(Page.Locator(".food-item-card", new() { HasText = targetName }))
            .ToBeVisibleAsync();

        // Verify source name is retained as an alias badge on surviving card
        await Expect(
                Page.Locator(".food-item-card", new() { HasText = targetName })
                    .Locator(".alias-chip")
            )
            .ToContainTextAsync(sourceName);
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task AiIngredientSubstitution_RecipeDetailAndCookMode_WithOfflineResilience()
    {
        await using var mockServer = await MockKitchenAiOllamaServer.StartAsync();

        await RegisterAndLoginAsync();
        await ConfigureLlmProviderAsync(mockServer.BaseUrl);

        // Create recipe with ingredient Buttermilk
        var recipeUrl = await CreateRecipeWithIngredientAsync(
            "Southern Fluffy Biscuits",
            "Buttermilk"
        );

        // 1. Verify substitute button is visible when AI host is online
        var subBtn = Page.Locator(".substitute-ing-btn").First;
        await Expect(subBtn).ToBeVisibleAsync();

        // Click substitute button
        await subBtn.ClickAsync();
        var subDialog = Page.Locator("app-ingredient-substitution-dialog");
        await Expect(subDialog).ToBeVisibleAsync();

        // Verify loaded substitutions
        await Expect(subDialog.Locator(".substitution-card")).ToHaveCountAsync(2);
        await Expect(subDialog.Locator(".option-title").First)
            .ToContainTextAsync("Greek Yogurt + Lemon Juice");

        // Click copy ratio button and verify icon changes to check
        await subDialog.Locator(".copy-ratio-btn").First.ClickAsync();
        await Expect(subDialog.Locator(".copy-sub-btn mat-icon").First).ToHaveTextAsync("check");

        // Close dialog
        await subDialog.Locator("button[aria-label='Close dialog']").ClickAsync();
        await Expect(subDialog).Not.ToBeVisibleAsync();

        // 2. Open Cook Mode
        await Page.Locator("button", new() { HasText = "Start Cooking" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/.*/cook.*"));

        // Verify .cook-substitute-btn in desktop ingredient list
        var cookSubBtn = Page.Locator(".cook-substitute-btn").First;
        await Expect(cookSubBtn).ToBeVisibleAsync();

        // Click substitution button in cook mode
        await cookSubBtn.ClickAsync();
        await Expect(subDialog).ToBeVisibleAsync();
        await subDialog.Locator("button[aria-label='Close dialog']").ClickAsync();
        await Expect(subDialog).Not.ToBeVisibleAsync();

        // Exit cook mode
        await Page.Locator(".exit-btn").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+$"));

        // 3. Simulate offline host and verify graceful degradation
        mockServer.SimulateOffline = true;

        // Invalidate status by reloading recipe detail page
        await Page.ReloadAsync();

        // Verify substitute button cleanly hides when offline
        await Expect(Page.Locator(".substitute-ing-btn")).Not.ToBeVisibleAsync();
        await Expect(Page.Locator(".explain-ing-btn")).Not.ToBeVisibleAsync();
        await Expect(Page.Locator(".explain-step-btn")).Not.ToBeVisibleAsync();
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task IngredientActions_StayBesideLongNamesAndNotes_OnDesktopAndMobile()
    {
        await using var mockServer = await MockKitchenAiOllamaServer.StartAsync();
        await RegisterAndLoginAsync();
        await ConfigureLlmProviderAsync(mockServer.BaseUrl);
        await CreateRecipeWithIngredientAsync(
            "Long Ingredient Layout",
            "Extra-virgin cold-pressed olive oil from a very long ingredient name",
            preparationNote: "finely chopped, drained thoroughly, and divided into several portions for serving"
        );

        foreach (var width in new[] { 1280, 360 })
        {
            await Page.SetViewportSizeAsync(width, 740);
            var row = Page.Locator(".ingredient-row").First;
            var ask = row.Locator(".explain-ing-btn");
            var substitute = row.Locator(".substitute-ing-btn");
            await Expect(ask).ToBeVisibleAsync();
            await Expect(substitute).ToBeVisibleAsync();
            var checkboxBox = await row.Locator("mat-checkbox").BoundingBoxAsync();
            var askBox = await ask.BoundingBoxAsync();
            var substituteBox = await substitute.BoundingBoxAsync();
            Assert.IsNotNull(checkboxBox);
            Assert.IsNotNull(askBox);
            Assert.IsNotNull(substituteBox);
            Assert.IsLessThanOrEqualTo(substituteBox.X + 1, checkboxBox.X + checkboxBox.Width);
            Assert.IsLessThanOrEqualTo(askBox.X + 1, substituteBox.X + substituteBox.Width);
            Assert.IsLessThanOrEqualTo(
                checkboxBox.Y + checkboxBox.Height + 1,
                askBox.Y + askBox.Height / 2,
                $"Ingredient actions must not wrap below the checkbox at {width}px."
            );
            Assert.IsLessThanOrEqualTo(
                width,
                await Page.EvaluateAsync<int>("() => document.documentElement.scrollWidth"),
                $"Recipe must fit at {width}px."
            );
        }
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task KitchenAi_AnswersIngredientStepAndGeneralQuestions_OnDesktopAndMobile()
    {
        await using var mockServer = await MockKitchenAiOllamaServer.StartAsync();
        await RegisterAndLoginAsync();
        await ConfigureLlmProviderAsync(mockServer.BaseUrl);
        await CreateRecipeWithIngredientAsync(
            "Kiwi Salad",
            "Kiwi",
            "Julienne the peeled kiwi.",
            preparationNote: "peeled and sliced"
        );

        var dialog = Page.Locator("app-kitchen-question-dialog");
        await Page.Locator(".explain-ing-btn").First.ClickAsync();
        await Expect(dialog.Locator(".answer-text")).ToContainTextAsync("thin matchsticks");
        Assert.IsNotNull(mockServer.LastKitchenPrompt);
        StringAssert.Contains(mockServer.LastKitchenPrompt, "Kiwi");
        StringAssert.Contains(mockServer.LastKitchenPrompt, "peeled and sliced");

        await dialog.Locator("textarea").FillAsync("How do I peel a kiwi?");
        mockServer.SimulateOffline = true;
        await dialog.Locator("button[type='submit']").ClickAsync();
        await Expect(dialog.Locator("[role='alert']")).ToBeVisibleAsync();
        mockServer.SimulateOffline = false;
        await dialog.Locator("button[type='submit']").ClickAsync();
        await Expect(dialog.Locator(".answer-text")).ToContainTextAsync("thin matchsticks");
        Assert.IsNotNull(mockServer.LastKitchenPrompt);
        StringAssert.Contains(mockServer.LastKitchenPrompt, "How do I peel a kiwi?");
        await dialog.Locator("button[aria-label='Close Kitchen AI']").ClickAsync();

        await Page.Locator(".explain-step-btn").First.ClickAsync();
        await Expect(dialog.Locator(".answer-text")).ToBeVisibleAsync();
        Assert.IsNotNull(mockServer.LastKitchenPrompt);
        StringAssert.Contains(mockServer.LastKitchenPrompt, "Julienne the peeled kiwi.");
        await dialog.Locator("button[aria-label='Close Kitchen AI']").ClickAsync();

        await Page.Locator("button[aria-label='More recipe actions']").ClickAsync();
        await Page.GetByRole(AriaRole.Menuitem, new() { Name = "Ask Kitchen AI" }).ClickAsync();
        await dialog.Locator("textarea").FillAsync("What is aquafaba?");
        await dialog.Locator("button[type='submit']").ClickAsync();
        await Expect(dialog.Locator(".answer-text")).ToBeVisibleAsync();
        Assert.IsNotNull(mockServer.LastKitchenPrompt);
        StringAssert.Contains(mockServer.LastKitchenPrompt, "What is aquafaba?");
        await dialog.Locator("button[aria-label='Close Kitchen AI']").ClickAsync();

        await Page.SetViewportSizeAsync(360, 740);
        await Page.Locator(".explain-ing-btn").First.ClickAsync();
        await Expect(dialog.Locator(".answer-text")).ToBeVisibleAsync();
        Assert.IsLessThanOrEqualTo(
            await dialog.EvaluateAsync<int>("el => el.clientWidth"),
            await dialog.EvaluateAsync<int>("el => el.scrollWidth"),
            "Kitchen AI dialog must fit on a 360px recipe page."
        );
        Assert.IsLessThanOrEqualTo(
            360,
            await Page.EvaluateAsync<int>("() => document.documentElement.scrollWidth"),
            "Recipe detail must not overflow horizontally at 360px."
        );
        await dialog.Locator("button[aria-label='Close Kitchen AI']").ClickAsync();
        await Page.SetViewportSizeAsync(1280, 720);

        await Page.Locator(".start-cooking-btn").ClickAsync();
        await Page.Locator(".desktop-explain-btn").First.ClickAsync();
        await Expect(dialog.Locator(".answer-text")).ToBeVisibleAsync();
        await dialog.Locator("button[aria-label='Close Kitchen AI']").ClickAsync();
        await Expect(Page.Locator(".cook-ingredients-pane .ingredient-item").First)
            .Not.ToHaveClassAsync(new Regex("is-checked"));

        await Page.Locator(".step-ai-explain-btn").ClickAsync();
        await Expect(dialog.Locator(".answer-text")).ToBeVisibleAsync();
        Assert.IsNotNull(mockServer.LastKitchenPrompt);
        StringAssert.Contains(mockServer.LastKitchenPrompt, "Julienne the peeled kiwi.");
        await dialog.Locator("button[aria-label='Close Kitchen AI']").ClickAsync();

        await Page.SetViewportSizeAsync(360, 740);
        await Page.Locator(".mobile-ingredients-btn").ClickAsync();
        await Page.Locator(".mobile-explain-btn").First.ClickAsync();
        await Expect(dialog.Locator(".answer-text")).ToBeVisibleAsync();
        Assert.IsNotNull(mockServer.LastKitchenPrompt);
        StringAssert.Contains(mockServer.LastKitchenPrompt, "peeled and sliced");
        await Expect(Page.Locator(".mobile-ingredients-drawer .ingredient-item").First)
            .Not.ToHaveClassAsync(new Regex("is-checked"));

        var dialogScrollWidth = await dialog.EvaluateAsync<int>("el => el.scrollWidth");
        var dialogClientWidth = await dialog.EvaluateAsync<int>("el => el.clientWidth");
        Assert.IsLessThanOrEqualTo(
            dialogClientWidth,
            dialogScrollWidth,
            "Kitchen AI dialog must not overflow horizontally at 360px."
        );
        var pageScrollWidth = await Page.EvaluateAsync<int>(
            "() => document.documentElement.scrollWidth"
        );
        Assert.IsLessThanOrEqualTo(360, pageScrollWidth);
        await dialog.Locator("button[aria-label='Close Kitchen AI']").ClickAsync();
        await Page.Locator(
                ".mobile-ingredients-drawer button[aria-label='Close ingredients drawer']"
            )
            .ClickAsync();

        await Page.Locator(".kitchen-ai-general-btn").ClickAsync();
        await dialog.Locator("textarea").FillAsync("What is aquafaba?");
        await dialog.Locator("button[type='submit']").ClickAsync();
        await Expect(dialog.Locator(".answer-text")).ToBeVisibleAsync();
        await dialog.Locator("button[aria-label='Close Kitchen AI']").ClickAsync();

        await Page.Locator(".step-ai-explain-btn").ClickAsync();
        await Expect(dialog.Locator(".answer-text")).ToBeVisibleAsync();
        await dialog.Locator("button[aria-label='Close Kitchen AI']").ClickAsync();
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task DraftWeeklyMealPlan_AppliesGroundedMeals_AndVerifiesMobileResponsiveLayout()
    {
        await using var mockServer = await MockKitchenAiOllamaServer.StartAsync();

        await RegisterAndLoginAsync();
        await ConfigureLlmProviderAsync(mockServer.BaseUrl);

        // Three recipes allow a two-suggestion draft and a distinct replacement.
        await CreateRecipeWithIngredientAsync(
            "Tuscan Garlic Chicken",
            "Chicken Breast",
            "Cook chicken until golden brown."
        );
        await CreateRecipeWithIngredientAsync(
            "Veggie Stir Fry",
            "Bell Pepper",
            "Stir fry vegetables over high heat."
        );
        await CreateRecipeWithIngredientAsync("Lentil Soup", "Lentils", "Simmer until tender.");

        // 1. Navigate to Meal Plan
        await Page.Locator("nav.desktop-nav a", new() { HasText = "Meal Plan" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/meal-plan.*"));
        await Expect(Page.Locator("mat-button-toggle[value='next7'] button"))
            .ToHaveAttributeAsync("aria-checked", "true");
        await Expect(Page.Locator(".week-days-grid .day-column")).ToHaveCountAsync(7);
        await Expect(Page.Locator(".week-days-grid .day-column").First)
            .ToHaveClassAsync(new Regex("is-today"));

        // The E2E server shares household plans between tests. Clear both weeks in this horizon.
        await Page.Locator("mat-button-toggle[value='week'] button").ClickAsync();
        for (var week = 0; week < 2; week++)
        {
            await Expect(Page.Locator(".week-days-grid")).ToBeVisibleAsync();
            if (await Page.Locator("button.clear-week-btn").IsVisibleAsync())
            {
                await Page.Locator("button.clear-week-btn").ClickAsync();
                await Page.Locator("mat-dialog-container button", new() { HasText = "Clear Week" })
                    .ClickAsync();
                await Expect(Page.Locator("mat-dialog-container")).Not.ToBeVisibleAsync();
                await Expect(Page.Locator(".week-days-grid")).ToBeVisibleAsync();
            }
            if (week == 0)
            {
                await Page.Locator("button[aria-label='Next period']").ClickAsync();
            }
        }
        await Page.Locator("button.today-btn").ClickAsync();
        await Page.Locator("mat-button-toggle[value='next7'] button").ClickAsync();
        await Expect(Page.Locator(".meal-entry-card")).ToHaveCountAsync(0);

        // Verify Draft Week AI button is visible
        var draftWeekBtn = Page.Locator(".draft-week-ai-btn");
        await Expect(draftWeekBtn).ToBeVisibleAsync();

        // Click Draft Week
        await draftWeekBtn.ClickAsync();
        var draftDialog = Page.Locator("app-draft-meal-plan-dialog");
        await Expect(draftDialog).ToBeVisibleAsync();

        // 2. Select Preset Idea Chip
        var presetChip = draftDialog.Locator(".preset-chip").First;
        await presetChip.ClickAsync();
        await draftDialog.Locator(".meal-count-field mat-select").ClickAsync();
        await Page.GetByRole(AriaRole.Option, new() { Name = "2", Exact = true }).ClickAsync();
        await draftDialog.Locator(".household-servings-input").FillAsync("2");

        // Click Draft Plan
        var draftSubmitBtn = draftDialog.Locator(".draft-submit-btn");
        await Expect(draftSubmitBtn).ToBeEnabledAsync();
        await draftSubmitBtn.ClickAsync();

        // Verify Candidate Cards are rendered
        var candidateCards = draftDialog.Locator(".candidate-card");
        await Expect(candidateCards).ToHaveCountAsync(2, new() { Timeout = 10000 });
        await Expect(draftDialog.Locator(".candidate-title").First)
            .ToContainTextAsync("Tuscan Garlic Chicken");
        await Expect(draftDialog.Locator(".candidate-rationale").First)
            .ToContainTextAsync("High-protein family favorite");
        await Expect(candidateCards.First.Locator(".servings-badge"))
            .ToContainTextAsync("Cook 8 servings");
        await Expect(candidateCards.Nth(1).Locator(".servings-badge"))
            .ToContainTextAsync("Cook 6 servings");
        await Expect(candidateCards.First).ToContainTextAsync("Leftovers:");
        var originalDates = await draftDialog
            .Locator(".candidate-date-input")
            .EvaluateAllAsync<string[]>("inputs => inputs.map(input => input.value)");

        mockServer.SimulateOffline = true;
        await candidateCards.First.Locator(".retry-candidate-btn").ClickAsync();
        await Expect(draftDialog.Locator(".error-banner")).ToBeVisibleAsync();
        await Expect(draftDialog.Locator(".candidate-title").First)
            .ToContainTextAsync("Tuscan Garlic Chicken");
        mockServer.SimulateOffline = false;
        await candidateCards.First.Locator(".retry-candidate-btn").ClickAsync();
        await Expect(draftDialog.Locator(".candidate-title").First)
            .ToContainTextAsync("Lentil Soup");
        await Expect(draftDialog.Locator(".candidate-title").Nth(1))
            .ToContainTextAsync("Veggie Stir Fry");
        var retriedDates = await draftDialog
            .Locator(".candidate-date-input")
            .EvaluateAllAsync<string[]>("inputs => inputs.map(input => input.value)");
        CollectionAssert.AreEqual(originalDates, retriedDates);
        await Expect(candidateCards.First.Locator(".servings-badge"))
            .ToContainTextAsync("Cook 8 servings");

        // Deselect second candidate
        var secondCheckbox = candidateCards.Nth(1).Locator(".candidate-checkbox");
        await secondCheckbox.ClickAsync();

        // 3. Apply Selected Meals
        var applyBtn = draftDialog.Locator(".apply-draft-btn");
        await Expect(applyBtn).ToContainTextAsync("Add Selected Meals (4)");
        await applyBtn.ClickAsync();

        await Expect(draftDialog).Not.ToBeVisibleAsync();

        // 4. Verify scheduled meal appears on calendar
        await Expect(Page.Locator(".meal-entry-card", new() { HasText = "Lentil Soup" }))
            .ToHaveCountAsync(4);
        await Expect(Page.Locator(".meal-entry-card .leftovers-badge")).ToHaveCountAsync(3);
        await Page.ReloadAsync();
        await Expect(Page.Locator(".meal-entry-card", new() { HasText = "Lentil Soup" }))
            .ToHaveCountAsync(4);

        // 5. Test 360px Mobile Viewport Responsiveness (zero horizontal scroll overflow)
        await Page.SetViewportSizeAsync(360, 740);

        // Check horizontal overflow on meal plan page
        var pageScrollWidth = await Page.EvaluateAsync<int>(
            "() => document.documentElement.scrollWidth"
        );
        var pageClientWidth = await Page.EvaluateAsync<int>(
            "() => document.documentElement.clientWidth"
        );
        Assert.IsLessThanOrEqualTo(
            pageScrollWidth,
            pageClientWidth,
            $"Expected no horizontal overflow on mobile calendar view, but scrollWidth ({pageScrollWidth}) > clientWidth ({pageClientWidth})"
        );

        // Re-open Draft dialog in 360px viewport and verify zero overflow
        await Page.Locator(".draft-week-ai-btn").ClickAsync();
        await Expect(draftDialog).ToBeVisibleAsync();
        await draftDialog.Locator(".preset-chip").First.ClickAsync();
        await draftDialog.Locator(".meal-count-field mat-select").ClickAsync();
        await Page.GetByRole(AriaRole.Option, new() { Name = "1", Exact = true }).ClickAsync();
        await draftDialog.Locator(".draft-submit-btn").ClickAsync();
        await Expect(draftDialog.Locator(".candidate-card")).ToHaveCountAsync(1);
        await draftDialog.Locator(".retry-candidate-btn").ClickAsync();
        await Expect(draftDialog.Locator(".candidate-title")).ToContainTextAsync("Veggie Stir Fry");
        await Expect(draftDialog.Locator(".error-banner")).Not.ToBeVisibleAsync();

        var dialogScrollWidth = await Page.EvaluateAsync<int>(
            "() => document.querySelector('.draft-dialog-container').scrollWidth"
        );
        var dialogClientWidth = await Page.EvaluateAsync<int>(
            "() => document.querySelector('.draft-dialog-container').clientWidth"
        );
        Assert.IsLessThanOrEqualTo(
            dialogScrollWidth,
            dialogClientWidth,
            $"Expected no horizontal overflow on mobile draft dialog, but scrollWidth ({dialogScrollWidth}) > clientWidth ({dialogClientWidth})"
        );

        // Close dialog
        await draftDialog.Locator("button[aria-label='Close dialog']").ClickAsync();
        await Expect(draftDialog).Not.ToBeVisibleAsync();
    }
}

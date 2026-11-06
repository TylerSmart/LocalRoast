using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace App.Client.E2ETests;

[TestClass]
public sealed class ResponsiveUiE2ETests : E2ETestBase
{
    private const string SampleImagePath = "/api/uploads/responsive/sample.png";
    private const string RecipeTitle =
        "Slow-roasted vegetables with lemon, herbs and a very generously described family supper";
    private const string IngredientName =
        "ExtraVirginOliveOilWithAnIntentionallyLongUnbrokenCatalogName";
    private string? responsiveShoppingListId;

    [TestMethod]
    [DataRow(360, 740, "light")]
    [DataRow(768, 1024, "dark")]
    [DataRow(1024, 768, "light")]
    [DataRow(1440, 900, "dark")]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task RoutedPages_LongContentFitsAtEveryViewport(
        int width,
        int height,
        string theme
    )
    {
        await RegisterAndLoginAsync();
        var recipeId = await SeedRecipeAsync();
        var listId = await CreateResponsiveShoppingListAsync();
        await SetViewportAsync(width, height, theme);

        (string Route, string Ready)[] routes =
        [
            ("recipes", ".recipe-card"),
            ($"recipes/{recipeId}", ".recipe-body-grid"),
            ($"recipes/{recipeId}/edit", ".editor-form"),
            ("meal-plan", ".week-days-grid"),
            ($"shopping?listId={listId}", ".workspace-content"),
            ("catalog", ".food-item-card"),
            ("matcher", ".picker-card"),
            ("ingest", ".capture-card"),
        ];

        foreach (var (route, ready) in routes)
        {
            await Page.GotoAsync($"{ServerUrl}/{route}");
            await Expect(Page.Locator(ready).First).ToBeVisibleAsync();
            await AssertLayoutAsync(
                Page.Locator("app-shell"),
                route.Split('?')[0].Replace('/', '-')
            );
        }

        foreach (var channel in new[] { "Paste Raw Text", "Photos / Camera Scan", "PDF Document" })
        {
            await Page.GetByRole(AriaRole.Tab, new() { Name = channel }).ClickAsync();
            await AssertLayoutAsync(Page.Locator("app-shell"), $"ingest-{channel.Split(' ')[0]}");
        }

        await Page.GotoAsync($"{ServerUrl}/meal-plan");
        await Page.Locator("mat-button-toggle[value='month']").ClickAsync();
        await Expect(Page.Locator(".month-days-grid")).ToBeVisibleAsync();
        await AssertLayoutAsync(Page.Locator("app-shell"), "month");
        if (width <= 600)
        {
            await Expect(Page.Locator(".month-agenda-date:visible").First).ToBeVisibleAsync();
            await Expect(Page.Locator(".month-weekdays-row")).Not.ToBeVisibleAsync();
        }
    }

    [TestMethod]
    [DataRow(360, 640)]
    [DataRow(768, 1024)]
    [DataRow(1440, 900)]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task Authentication_FormsAndValidationFit(int width, int height)
    {
        await SetViewportAsync(width, height, "light");
        await Page.GotoAsync($"{ServerUrl}/login");
        await Expect(Page.Locator(".auth-card")).ToBeVisibleAsync();
        await AssertLayoutAsync(Page.Locator(".auth-page"), "login");
        await Page.GetByRole(AriaRole.Tab, new() { Name = "Create Account" }).ClickAsync();
        var form = Page.Locator("form[aria-label='Create account form']");
        await form.Locator("input[formcontrolname='email']").FillAsync("invalid-email");
        await form.Locator("input[formcontrolname='password']").ClickAsync();
        await Expect(form.Locator("mat-error")).ToBeVisibleAsync();
        await AssertLayoutAsync(Page.Locator(".auth-page"), "registration-validation");
    }

    [TestMethod]
    [DataRow(360, 480)]
    [DataRow(740, 360)]
    [DataRow(768, 700)]
    [DataRow(1440, 900)]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task SettingsDialogs_TabsAndActionsStayReachable(int width, int height)
    {
        await RegisterAndLoginAsync();
        await SetViewportAsync(width, height, "dark");
        await OpenAccountMenuAsync("Account Settings");
        var account = Page.Locator("app-account-settings-dialog");
        await Expect(account.Locator("input[formcontrolname='username']")).ToBeVisibleAsync();
        await AssertDialogAsync("account-profile");
        foreach (var tab in new[] { "Password", "MCP Tokens", "Data Management" })
        {
            await account.GetByRole(AriaRole.Tab, new() { Name = tab, Exact = true }).ClickAsync();
            await AssertDialogAsync($"account-{tab.Replace(' ', '-')}");
        }
        await account.Locator(".reset-data-button").ClickAsync();
        await AssertDialogAsync("reset-data");
        await Page.Locator("app-reset-app-data-dialog")
            .GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true })
            .ClickAsync();
        await account
            .GetByRole(AriaRole.Button, new() { Name = "Close account settings" })
            .ClickAsync();

        await Page.Locator("button[aria-label^='Theme:']").ClickAsync();
        await Page.GetByRole(AriaRole.Menuitem, new() { Name = "Customize Theme..." }).ClickAsync();
        await AssertDialogAsync("theme");
        await Page.Locator("app-theme-customizer-dialog")
            .GetByRole(AriaRole.Button, new() { Name = "Done", Exact = true })
            .ClickAsync();

        await OpenAccountMenuAsync("LLM Provider Settings");
        await Expect(Page.Locator("app-llm-settings-dialog input").First).ToBeVisibleAsync();
        await AssertDialogAsync("llm-settings");
        await Page.Keyboard.PressAsync("Escape");

        await Page.Locator("button[aria-label='Open quick search']:visible").ClickAsync();
        await Expect(Page.Locator(".result-item").First).ToBeVisibleAsync();
        await AssertDialogAsync("global-search");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Close search", Exact = true })
            .ClickAsync();
        await Expect(Page.Locator("mat-dialog-container")).ToHaveCountAsync(0);
    }

    [TestMethod]
    [DataRow(360, 640)]
    [DataRow(768, 700)]
    [DataRow(1440, 900)]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task RecipeDialogs_FormsFitAndNestedDialogsRemainUsable(int width, int height)
    {
        await RegisterAndLoginAsync();
        var recipeId = await SeedRecipeAsync();
        await CreateResponsiveShoppingListAsync();
        await SetViewportAsync(width, height, "light");
        await Page.GotoAsync($"{ServerUrl}/recipes/{recipeId}");
        await Expect(Page.Locator(".recipe-body-grid")).ToBeVisibleAsync();

        await Page.GetByRole(AriaRole.Button, new() { Name = "I Made This!", Exact = true })
            .ClickAsync();
        await AssertDialogAsync("cook-log");
        await Page.Keyboard.PressAsync("Escape");

        foreach (
            var action in new[] { "Plan meal", "Add ingredients to shopping list", "Share recipe" }
        )
        {
            await OpenRecipeActionAsync(action);
            await AssertDialogAsync(action.Replace(' ', '-'));
            await Page.Keyboard.PressAsync("Escape");
        }

        await OpenRecipeActionAsync("Organize collections and tags");
        await Expect(Page.Locator("app-recipe-organization-dialog .loading-state"))
            .ToHaveCountAsync(0);
        await AssertDialogAsync("organize");
        foreach (var label in new[] { "New Collection", "New Tag" })
        {
            await Page.Locator("app-recipe-organization-dialog")
                .GetByRole(AriaRole.Button, new() { Name = label, Exact = true })
                .ClickAsync();
            await AssertDialogAsync(label.Replace(' ', '-'));
            var nested = Page.Locator("mat-dialog-container").Last;
            await nested
                .Locator("input[formcontrolname='name']")
                .FillAsync($"{IngredientName}-{Guid.NewGuid().ToString("N")[..6]}");
            await nested.Locator("button[mat-flat-button]").ClickAsync();
            await Expect(Page.Locator("mat-dialog-container")).ToHaveCountAsync(1);
            await AssertDialogAsync($"organize-with-{label.Replace(' ', '-')}");
        }
        await Page.GetByRole(
                AriaRole.Button,
                new() { Name = "Close organize recipe dialog", Exact = true }
            )
            .ClickAsync();
        await Expect(Page.Locator("mat-dialog-container")).ToHaveCountAsync(0);

        await OpenRecipeActionAsync("Delete recipe");
        await AssertDialogAsync("delete-confirmation");
        await Page.Keyboard.PressAsync("Escape");

        await Page.GotoAsync($"{ServerUrl}/catalog");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Add new food item" }).ClickAsync();
        await AssertDialogAsync("food-item");
        await Page.Keyboard.PressAsync("Escape");
        await Page.GetByRole(AriaRole.Button, new() { Name = "View recipes using this item" })
            .First.ClickAsync();
        await Expect(Page.Locator(".recipe-usage-row")).ToBeVisibleAsync();
        await AssertDialogAsync("food-item-recipes");
        await Page.Keyboard.PressAsync("Escape");
        await Page.GetByRole(
                AriaRole.Button,
                new() { Name = "Merge duplicate food items", Exact = true }
            )
            .ClickAsync();
        await Expect(Page.Locator("app-merge-food-items-dialog .loading-state"))
            .ToHaveCountAsync(0);
        await AssertDialogAsync("merge-food-items");
    }

    [TestMethod]
    [DataRow(360, 640)]
    [DataRow(768, 700)]
    [DataRow(1440, 900)]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task PlanningAndShoppingDialogs_AllFormModesFit(int width, int height)
    {
        await RegisterAndLoginAsync();
        await SeedRecipeAsync();
        var listId = await CreateResponsiveShoppingListAsync();
        await SetViewportAsync(width, height, "dark");
        await Page.GotoAsync($"{ServerUrl}/meal-plan");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Add a meal to your plan" })
            .ClickAsync();
        await AssertDialogAsync("meal-entry");
        foreach (var type in new[] { "Leftovers", "FreeformNote" })
        {
            await Page.Locator($"mat-button-toggle[value='{type}']").ClickAsync();
            await AssertDialogAsync($"meal-entry-{type}");
        }
        await Page.Locator("mat-button-toggle[value='ondeck']").ClickAsync();
        await AssertDialogAsync("meal-entry-ondeck");
        await Page.Locator("input[formcontrolname='customTitle']").FillAsync(RecipeTitle);
        await Page.Locator("app-meal-plan-entry-dialog mat-dialog-actions button[mat-flat-button]")
            .ClickAsync();
        await Expect(Page.Locator("app-meal-plan-entry-dialog")).ToHaveCountAsync(0);
        if (width <= 900)
        {
            await Page.GetByRole(AriaRole.Button, new() { Name = "Toggle On-Deck tray" })
                .ClickAsync();
        }
        await Expect(Page.Locator(".week-view-wrapper")).ToContainTextAsync(RecipeTitle);
        await AssertLayoutAsync(Page.Locator("app-shell"), "populated-meal-plan");

        await Page.GetByRole(AriaRole.Button, new() { Name = "Manage reusable week templates" })
            .ClickAsync();
        await AssertDialogAsync("templates");
        await Page.GetByRole(AriaRole.Tab, new() { Name = "Save Current Week" }).ClickAsync();
        await AssertDialogAsync("save-template");
        await Page.Locator("app-meal-plan-templates-dialog input[formcontrolname='name']")
            .FillAsync($"{RecipeTitle} {Guid.NewGuid().ToString("N")[..6]}");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Save as Template", Exact = true })
            .ClickAsync();
        await Page.GetByRole(AriaRole.Tab, new() { Name = "Saved Templates" }).ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Preview template items" })
            .Last.ClickAsync();
        await Expect(Page.Locator(".template-preview-tray")).ToBeVisibleAsync();
        await AssertDialogAsync("template-preview");
        await Page.Keyboard.PressAsync("Escape");
        await Page.GetByRole(
                AriaRole.Button,
                new() { Name = "Generate shopping list for this week" }
            )
            .ClickAsync();
        await AssertDialogAsync("generate-shopping");
        await Page.Keyboard.PressAsync("Escape");

        await Page.GotoAsync($"{ServerUrl}/shopping?listId={listId}");
        await Expect(Page.Locator(".workspace-content")).ToBeVisibleAsync();
        await Page.Locator("button[aria-label='Create new shopping list']:visible").ClickAsync();
        await AssertDialogAsync("shopping-list");
        await Page.Keyboard.PressAsync("Escape");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Add item with details" })
            .ClickAsync();
        await AssertDialogAsync("shopping-item");
        await Page.Keyboard.PressAsync("Escape");
        await Page.Locator(".quick-add-input-field input").FillAsync(IngredientName);
        await Page.Locator(".quick-add-submit-btn").ClickAsync();
        await Expect(Page.Locator(".item-checkbox").First).ToBeVisibleAsync();
        await AssertLayoutAsync(Page.Locator("app-shell"), "populated-shopping");
        await Page.Locator(".item-checkbox").First.ClickAsync();
        await Expect(Page.Locator(".checked-items-section")).ToBeVisibleAsync();
        await AssertLayoutAsync(Page.Locator("app-shell"), "checked-shopping");
    }

    [TestMethod]
    [DataRow(360, 740)]
    [DataRow(768, 1024)]
    [DataRow(1440, 900)]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task CookMode_ControlsTimersAndDrawerFit(int width, int height)
    {
        await RegisterAndLoginAsync();
        var recipeId = await SeedRecipeAsync();
        await SetViewportAsync(width, height, "light");
        await Page.GotoAsync($"{ServerUrl}/recipes/{recipeId}/cook");
        await Expect(Page.Locator(".cook-step-card")).ToBeVisibleAsync();
        await Page.Locator(".custom-timer-toggle-btn").ClickAsync();
        await AssertLayoutAsync(Page.Locator(".cook-mode-container"), "cook-mode-custom-timer");
        await Page.Locator(".custom-timer-input").FillAsync(RecipeTitle);
        await Page.Locator(".custom-timer-mins").FillAsync("10");
        await Page.Locator(".custom-timer-form button").ClickAsync();
        await Expect(Page.Locator(".timer-card")).ToBeVisibleAsync();
        await AssertLayoutAsync(Page.Locator(".cook-mode-container"), "cook-mode-running-timer");

        if (width <= 900)
        {
            var trigger = Page.Locator(".mobile-ingredients-btn");
            await trigger.ClickAsync();
            var drawer = Page.Locator(".mobile-ingredients-drawer");
            await Expect(drawer).ToBeVisibleAsync();
            await Expect(
                    drawer.GetByRole(
                        AriaRole.Button,
                        new() { Name = "Close ingredients drawer", Exact = true }
                    )
                )
                .ToBeFocusedAsync();
            await AssertLayoutAsync(drawer, "cook-ingredients-drawer");
            await Page.Keyboard.PressAsync("Shift+Tab");
            Assert.IsTrue(
                await drawer.EvaluateAsync<bool>("el => el.contains(document.activeElement)")
            );
            await Page.Keyboard.PressAsync("Escape");
            await Expect(drawer).Not.ToBeVisibleAsync();
            await Expect(trigger).ToBeFocusedAsync();
        }

        await Page.GetByRole(AriaRole.Button, new() { Name = "Exit Cook Mode" }).ClickAsync();
        await Expect(Page.Locator(".active-timer-nav-pill")).ToBeVisibleAsync();
        await AssertLayoutAsync(Page.Locator("app-shell"), "shell-running-timer");
    }

    [TestMethod]
    [DataRow(360, 640)]
    [DataRow(768, 1024)]
    [DataRow(1024, 768)]
    [DataRow(1440, 900)]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task IngestionReview_EditorSourceAndPreviewFit(int width, int height)
    {
        await RegisterAndLoginAsync();
        await SetViewportAsync(width, height, "dark");
        var jobId = Guid.NewGuid();
        await MockMediaAsync();
        var draft = new
        {
            title = RecipeTitle,
            description = "A detailed recipe with long source text, ingredient names and timers.",
            baseServings = 4,
            difficulty = "Medium",
            sourceName = IngredientName,
            confidenceScore = 0.95,
            ingredients = new[]
            {
                new
                {
                    sortOrder = 0,
                    displayName = IngredientName,
                    originalRawText = "2 cups " + IngredientName,
                    quantity = 2,
                    unit = "Cup",
                    proposedFoodItemName = IngredientName,
                    isNewFoodItem = true,
                    suggestedIsPantryStaple = true,
                },
            },
            steps = new[]
            {
                new
                {
                    stepNumber = 1,
                    instruction = RecipeTitle + ". Mix thoroughly, then roast until golden.",
                    ingredientIndices = new[] { 0 },
                    timers = new[]
                    {
                        new
                        {
                            sortOrder = 0,
                            durationSeconds = 600,
                            label = "Roast vegetables",
                        },
                    },
                },
            },
        };
        await Page.RouteAsync(
            $"**/api/ingestion/jobs/{jobId}",
            route =>
                route.FulfillAsync(
                    new()
                    {
                        ContentType = "application/json",
                        Body = JsonSerializer.Serialize(
                            new
                            {
                                id = jobId,
                                channel = "Images",
                                status = "ReadyForReview",
                                displayTitle = RecipeTitle,
                                rawText = string.Join("\n", Enumerable.Repeat(RecipeTitle, 12)),
                                extractedModel = "local-recipe-extraction-model-with-a-long-name",
                                extractedDraft = draft,
                                sourceFiles = Enumerable
                                    .Range(1, 2)
                                    .Select(page => new
                                    {
                                        pageNumber = page,
                                        originalFileName = $"{IngredientName}-{page}.png",
                                        urlPath = SampleImagePath,
                                        contentType = "image/png",
                                        fileSizeBytes = 100,
                                    }),
                            }
                        ),
                    }
                )
        );

        await Page.GotoAsync($"{ServerUrl}/ingest/review/{jobId}");
        await Expect(Page.Locator(".draft-form")).ToBeVisibleAsync();
        await AssertLayoutAsync(Page.Locator("app-shell"), "review-editor");
        if (width <= 960)
        {
            await Page.GetByRole(AriaRole.Tab, new() { Name = "Original Source" }).ClickAsync();
            await AssertLayoutAsync(Page.Locator("app-shell"), "review-source");
            await Page.GetByRole(AriaRole.Tab, new() { Name = "Preview", Exact = true })
                .ClickAsync();
        }
        else
        {
            await Page.Locator(".preview-toggle-btn").ClickAsync();
        }
        await Expect(Page.Locator("app-recipe-detail-preview")).ToBeVisibleAsync();
        await AssertLayoutAsync(Page.Locator("app-shell"), "review-inline-preview");
        await Page.Locator(".review-header .preview-btn").ClickAsync();
        await AssertDialogAsync("recipe-preview");
    }

    [TestMethod]
    [DataRow(360, 640)]
    [DataRow(768, 700)]
    [DataRow(1440, 900)]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task AiHelpers_PopulatedResponsesAndEditableProposalsFit(int width, int height)
    {
        await RegisterAndLoginAsync();
        var recipeId = await SeedRecipeAsync();
        await SetViewportAsync(width, height, "dark");
        await MockJsonAsync(
            "**/api/ai-helpers/status",
            new
            {
                isAvailable = true,
                provider = "Ollama",
                latencyMs = 1,
            }
        );
        await MockJsonAsync(
            "**/api/ai-helpers/kitchen-question",
            new
            {
                answer = string.Join(
                    "\n\n",
                    Enumerable.Repeat(
                        "Slice the vegetables into evenly sized pieces so they cook at the same rate.",
                        8
                    )
                ),
                modelUsed = IngredientName,
            }
        );
        await MockJsonAsync(
            "**/api/ai-helpers/ingredient-substitutions",
            new
            {
                recipeId,
                recipeTitle = RecipeTitle,
                ingredientDisplayName = IngredientName,
                scaledAmountDisplay = "2 cups",
                modelUsed = IngredientName,
                substitutions = new[]
                {
                    new
                    {
                        label = RecipeTitle,
                        ratioAndAmount = "1 cup for every 1 cup of the original ingredient",
                        culinaryNotes = RecipeTitle,
                        flavorOrTextureImpact = IngredientName,
                    },
                },
            }
        );
        var week = DateTime.Today.AddDays(-(int)DateTime.Today.DayOfWeek);
        await MockJsonAsync(
            "**/api/ai-helpers/draft-meal-plan",
            new
            {
                weekStartDate = week.ToString("yyyy-MM-dd"),
                weekEndDate = week.AddDays(6).ToString("yyyy-MM-dd"),
                modelUsed = IngredientName,
                consideredCookbookRecipeCount = 1,
                candidates = new[]
                {
                    new
                    {
                        recipeId,
                        recipe = new
                        {
                            id = recipeId,
                            title = RecipeTitle,
                            slug = "vegetables",
                            baseServings = 4,
                            totalTimeMinutes = 35,
                        },
                        isOnDeck = false,
                        dayOffset = 1,
                        scheduledDate = week.AddDays(1).ToString("yyyy-MM-dd"),
                        slot = "Dinner",
                        targetServings = 4,
                        rationale = RecipeTitle,
                    },
                },
            }
        );
        await Page.GotoAsync($"{ServerUrl}/recipes/{recipeId}");
        await OpenRecipeActionAsync("Ask Kitchen AI");
        await Page.Locator("textarea[formcontrolname='question']")
            .FillAsync("How should I chop vegetables?");
        await Page.Locator("app-kitchen-question-dialog button[type='submit']").ClickAsync();
        await Expect(Page.Locator(".answer-card")).ToBeVisibleAsync();
        await AssertDialogAsync("kitchen-answer");
        await Page.Keyboard.PressAsync("Escape");

        await Page.Locator(".substitute-ing-btn").First.ClickAsync();
        await Expect(Page.Locator(".substitution-card")).ToBeVisibleAsync();
        await AssertDialogAsync("substitution-results");
        await Page.Keyboard.PressAsync("Escape");

        await Page.GotoAsync($"{ServerUrl}/meal-plan");
        await Page.Locator(".draft-week-ai-btn").ClickAsync();
        await Page.Locator(".preset-chip").First.ClickAsync();
        await Page.Locator(".draft-submit-btn").ClickAsync();
        await Expect(Page.Locator(".candidate-card")).ToBeVisibleAsync();
        await Expect(Page.Locator(".candidate-slot-select")).ToHaveValueAsync("Dinner");
        await AssertDialogAsync("draft-plan-results");
        await Page.Locator(".candidate-servings-input").FillAsync("6");
        await Page.Locator(".candidate-slot-select").SelectOptionAsync("Lunch");
        await Expect(Page.Locator(".candidate-slot-select")).ToHaveValueAsync("Lunch");
        await Page.Locator(".candidate-card").ScrollIntoViewIfNeededAsync();
        await AssertDialogAsync("draft-plan-editing");
    }

    [TestMethod]
    [DataRow(360, 640)]
    [DataRow(768, 700)]
    [DataRow(1440, 900)]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task MediaSharingAndMatcher_PopulatedContentFits(int width, int height)
    {
        await RegisterAndLoginAsync();
        var recipeId = await SeedRecipeAsync();
        await SetViewportAsync(width, height, "light");
        await MockMediaAsync();
        await Page.RouteAsync(
            $"**/api/recipes/{recipeId}",
            async route =>
            {
                var response = await route.FetchAsync();
                var recipe = JsonNode.Parse(await response.TextAsync())!;
                recipe["attachments"] = JsonSerializer.SerializeToNode(
                    new[]
                    {
                        new
                        {
                            id = Guid.NewGuid(),
                            sortOrder = 0,
                            fileName = IngredientName + ".png",
                            mediaUrlPath = SampleImagePath,
                            contentType = "image/png",
                            fileSizeBytes = 100,
                        },
                        new
                        {
                            id = Guid.NewGuid(),
                            sortOrder = 1,
                            fileName = IngredientName + ".txt",
                            mediaUrlPath = "/api/uploads/responsive/source.txt",
                            contentType = "text/plain",
                            fileSizeBytes = 100,
                        },
                        new
                        {
                            id = Guid.NewGuid(),
                            sortOrder = 2,
                            fileName = IngredientName + ".pdf",
                            mediaUrlPath = "/api/uploads/responsive/source.pdf",
                            contentType = "application/pdf",
                            fileSizeBytes = 100,
                        },
                    }
                );
                await route.FulfillAsync(
                    new() { Response = response, Body = recipe.ToJsonString() }
                );
            }
        );
        await Page.GotoAsync($"{ServerUrl}/recipes/{recipeId}");
        await Page.Locator(".attachment-item-card").First.ClickAsync();
        await Expect(Page.Locator(".attachment-image")).ToBeVisibleAsync();
        await AssertDialogAsync("attachment-image");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Zoom in", Exact = true })
            .ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Rotate clockwise" }).ClickAsync();
        await Expect(Page.Locator(".zoom-badge")).ToHaveTextAsync("125%");
        await AssertDialogAsync("attachment-transformed");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Next attachment" }).ClickAsync();
        await Expect(Page.Locator(".text-content")).ToContainTextAsync(RecipeTitle);
        await AssertDialogAsync("attachment-text");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Next attachment" }).ClickAsync();
        await Expect(Page.Locator(".pdf-embed")).ToBeVisibleAsync();
        await AssertDialogAsync("attachment-pdf");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Close attachment viewer" })
            .ClickAsync();

        await OpenRecipeActionAsync("Share recipe");
        await Page.Locator("input[formcontrolname='label']").FillAsync(RecipeTitle);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Create share link" }).ClickAsync();
        var link = Page.Locator("a[aria-label='Open public view in new tab']").First;
        await Expect(link).ToBeVisibleAsync();
        await AssertDialogAsync("share-with-link");
        var shareUrl = await link.GetAttributeAsync("href");
        Assert.IsNotNull(shareUrl);
        await Page.GotoAsync(shareUrl);
        await Expect(Page.Locator(".recipe-title")).ToContainTextAsync(RecipeTitle);
        await AssertLayoutAsync(Page.Locator("app-public-shared-recipe"), "public-share");

        await Page.GotoAsync($"{ServerUrl}/matcher");
        await Page.Locator("input[aria-label='Search ingredients to match']")
            .FillAsync(IngredientName);
        await Page.Locator("mat-option").First.ClickAsync();
        await Expect(Page.Locator(".matcher-result-card").First).ToBeVisibleAsync();
        await AssertLayoutAsync(Page.Locator("app-shell"), "matcher-results");
    }

    private async Task<string> CreateResponsiveShoppingListAsync()
    {
        responsiveShoppingListId = await Page.EvaluateAsync<string>(
            """
            async name => {
                const tokenKey = Object.keys(localStorage).find(key => key.endsWith('-token'));
                const response = await fetch('/api/shopping-lists', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        Authorization: `Bearer ${localStorage.getItem(tokenKey)}`
                    },
                    body: JSON.stringify({ name, isPinned: false })
                });
                if (!response.ok) throw new Error(`Create layout list: ${response.status} ${await response.text()}`);
                const list = await response.json();
                if (!list.id) throw new Error('Created layout list has no ID');
                return list.id;
            }
            """,
            $"Layout groceries for family and friends {Guid.NewGuid():N}"
        );
        return responsiveShoppingListId;
    }

    [TestCleanup]
    public async Task DeleteResponsiveShoppingListAsync()
    {
        if (responsiveShoppingListId is null)
        {
            return;
        }
        await Page.EvaluateAsync(
            """
            async id => {
                const tokenKey = Object.keys(localStorage).find(key => key.endsWith('-token'));
                const response = await fetch(`/api/shopping-lists/${id}`, {
                    method: 'DELETE',
                    headers: { Authorization: `Bearer ${localStorage.getItem(tokenKey)}` }
                });
                if (!response.ok) throw new Error(`Delete layout list: ${response.status} ${await response.text()}`);
            }
            """,
            responsiveShoppingListId
        );
    }

    private Task MockJsonAsync(string pattern, object body) =>
        Page.RouteAsync(
            pattern,
            route =>
                route.FulfillAsync(
                    new()
                    {
                        ContentType = "application/json",
                        Body = JsonSerializer.Serialize(body),
                    }
                )
        );

    private async Task MockMediaAsync()
    {
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(
            900,
            1200,
            new(220, 195, 165)
        );
        using var imageStream = new MemoryStream();
        await image.SaveAsync(imageStream, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
        var png = imageStream.ToArray();
        await Page.RouteAsync(
            $"**{SampleImagePath}",
            route => route.FulfillAsync(new() { ContentType = "image/png", BodyBytes = png })
        );
        await Page.RouteAsync(
            "**/api/uploads/responsive/source.txt",
            route =>
                route.FulfillAsync(
                    new()
                    {
                        ContentType = "text/plain",
                        Body = string.Join(
                            "\n",
                            Enumerable.Repeat(RecipeTitle + " " + IngredientName, 20)
                        ),
                    }
                )
        );
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        var font = builder.AddStandard14Font(
            UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica
        );
        page.AddText(
            "Recipe source for responsive layout verification",
            12,
            new UglyToad.PdfPig.Core.PdfPoint(50, 700),
            font
        );
        var pdf = builder.Build();
        await Page.RouteAsync(
            "**/api/uploads/responsive/source.pdf",
            route => route.FulfillAsync(new() { ContentType = "application/pdf", BodyBytes = pdf })
        );
    }

    private async Task<Guid> SeedRecipeAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var foodId = await CreateFoodItemViaApiAsync($"{IngredientName}-{suffix}");
        return await CreateRecipeWithIngredientViaApiAsync(
            $"{RecipeTitle} {suffix}",
            IngredientName,
            foodId
        );
    }

    private async Task SetViewportAsync(int width, int height, string theme)
    {
        await Page.SetViewportSizeAsync(width, height);
        await Page.EmulateMediaAsync(
            new()
            {
                ReducedMotion = ReducedMotion.Reduce,
                ColorScheme = theme == "dark" ? ColorScheme.Dark : ColorScheme.Light,
            }
        );
        var themeButton = Page.Locator("button[aria-label^='Theme:']");
        if (await themeButton.CountAsync() > 0)
        {
            await themeButton.ClickAsync();
            await Page.RunAndWaitForResponseAsync(
                () =>
                    Page.GetByRole(
                            AriaRole.Menuitem,
                            new() { Name = theme == "dark" ? "Dark" : "Light", Exact = true }
                        )
                        .ClickAsync(),
                response =>
                    response.Url.EndsWith("/api/account/theme") && response.Request.Method == "PUT"
            );
            await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-theme", theme);
        }
    }

    private async Task OpenAccountMenuAsync(string name)
    {
        await Page.Locator(".user-profile-btn").ClickAsync();
        await Page.GetByRole(AriaRole.Menuitem, new() { Name = name, Exact = true }).ClickAsync();
    }

    private async Task OpenRecipeActionAsync(string name)
    {
        await Page.GetByRole(AriaRole.Button, new() { Name = "More recipe actions" }).ClickAsync();
        await Page.GetByRole(AriaRole.Menuitem, new() { Name = name, Exact = true }).ClickAsync();
    }

    private async Task AssertDialogAsync(string name)
    {
        var dialog = Page.Locator("mat-dialog-container").Last;
        await Expect(dialog).ToBeVisibleAsync();
        await AssertLayoutAsync(dialog, name);
        var bounds = await dialog.BoundingBoxAsync();
        Assert.IsNotNull(bounds);
        Assert.IsGreaterThanOrEqualTo(-1, bounds.Y, $"{name}: dialog extends above the viewport.");
        Assert.IsLessThanOrEqualTo(
            Page.ViewportSize!.Height + 1,
            bounds.Y + bounds.Height,
            $"{name}: dialog actions extend below the viewport."
        );

        var actions = dialog.Locator("mat-dialog-actions");
        if (await actions.CountAsync() > 0)
        {
            await Expect(actions).ToBeInViewportAsync(new() { Ratio = 1 });
        }
    }

    private async Task AssertLayoutAsync(ILocator root, string name)
    {
        await Page.EvaluateAsync("async () => { await document.fonts.ready; }");
        var overlappingNotifications = await Page.EvaluateAsync<string[]>(
            """
            () => {
                const navigation = document.querySelector('.mobile-bottom-nav');
                if (!navigation?.getClientRects().length) return [];
                const navBounds = navigation.getBoundingClientRect();
                return Array.from(document.querySelectorAll('mat-snack-bar-container'))
                    .filter(el => el.getClientRects().length && getComputedStyle(el).visibility !== 'hidden')
                    .map(el => el.getBoundingClientRect())
                    .filter(bounds => bounds.bottom > navBounds.top + 1)
                    .map(bounds => `Notification bottom ${Math.round(bounds.bottom)} exceeds navigation top ${Math.round(navBounds.top)}`);
            }
            """
        );
        Assert.IsEmpty(
            overlappingNotifications,
            $"{name}: feedback obscures the phone navigation:\n{string.Join("\n", overlappingNotifications)}"
        );
        var pageWidth = await Page.EvaluateAsync<int>("() => document.documentElement.scrollWidth");
        Assert.IsLessThanOrEqualTo(
            Page.ViewportSize!.Width + 1,
            pageWidth,
            $"{name}: the document overflows horizontally."
        );
        var snapshotDirectory = Environment.GetEnvironmentVariable(
            "LOCALROAST_RESPONSIVE_SCREENSHOTS"
        );
        if (!string.IsNullOrWhiteSpace(snapshotDirectory))
        {
            Directory.CreateDirectory(snapshotDirectory);
            await Page.ScreenshotAsync(
                new()
                {
                    Path = Path.Combine(
                        snapshotDirectory,
                        $"{Page.ViewportSize!.Width}-{Page.ViewportSize.Height}-{name}.png"
                    ),
                    FullPage = await Page.Locator("mat-dialog-container").CountAsync() == 0,
                    Animations = ScreenshotAnimations.Disabled,
                }
            );
        }

        var violations = await root.EvaluateAsync<string[]>(
            """
            (root, viewport) => {
                const intentionalScroll = '.collections-strip, .filter-strip, .selected-previews-strip, .timer-cards-row, .thumbnails-strip, .mat-mdc-tab-header';
                const elements = [root, ...root.querySelectorAll('header, section, form, h1, h2, h3, mat-card, mat-form-field, button, a, input, textarea, mat-button-toggle-group, mat-dialog-content')];
                return elements.filter(el => {
                    if (!el.getClientRects().length || el.closest('[inert]') || el.closest(intentionalScroll)) return false;
                    if (getComputedStyle(el).visibility === 'hidden' || el.matches('input[type=file], .skip-link')) return false;
                    const bounds = el.getBoundingClientRect();
                    return bounds.width > 0 && (bounds.left < -1 || bounds.right > viewport + 1);
                }).map(el => `${el.tagName}.${el.className}: ${Math.round(el.getBoundingClientRect().left)}..${Math.round(el.getBoundingClientRect().right)} (viewport ${viewport})`);
            }
            """,
            Page.ViewportSize.Width
        );
        Assert.IsEmpty(
            violations,
            $"{name} has off-screen content:\n{string.Join("\n", violations)}"
        );
        var content = root.Locator("mat-dialog-content");
        if (await content.CountAsync() == 1)
        {
            Assert.IsTrue(
                await content.EvaluateAsync<bool>("el => el.scrollWidth <= el.clientWidth + 1"),
                $"{name}: dialog content requires horizontal scrolling."
            );
        }

        var overflowingFields = await root.Locator("mat-form-field")
            .EvaluateAllAsync<string[]>(
                """
                fields => fields.filter(el => el.getClientRects().length && el.getBoundingClientRect().height > 115 && !el.querySelector('textarea, mat-hint, mat-error'))
                    .map(el => `${el.textContent.trim()}: ${Math.round(el.getBoundingClientRect().height)}px tall`)
                """
            );
        Assert.IsEmpty(
            overflowingFields,
            $"{name} stretches form controls:\n{string.Join("\n", overflowingFields)}"
        );
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace App.Client.E2ETests;

public sealed class MockLanLlmAndBlogServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    public string BaseUrl { get; }
    public bool SimulateOffline { get; set; }

    private MockLanLlmAndBlogServer(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public static async Task<MockLanLlmAndBlogServer> StartAsync()
    {
        var port = GetFreePort();
        var baseUrl = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));

        var app = builder.Build();
        var server = new MockLanLlmAndBlogServer(app, baseUrl);

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
                        models = new[]
                        {
                            new { name = "qwen2.5:14b" },
                            new { name = "qwen2.5vl:7b" },
                        },
                    }
                );
            }
        );

        app.MapPost(
            "/api/chat",
            () =>
            {
                if (server.SimulateOffline)
                {
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }

                var payloadJson = """
                    {
                      "title": "Crispy Sesame Tofu",
                      "description": "Golden crisp tofu tossed in fragrant sesame sauce.",
                      "baseServings": 4,
                      "yieldDisplay": "4 bowls",
                      "prepTimeMinutes": 15,
                      "cookTimeMinutes": 10,
                      "totalTimeMinutes": 25,
                      "difficulty": "Easy",
                      "sourceName": "Food Blog",
                      "sourceAuthor": "Chef Alex",
                      "ingredients": [
                        {
                          "sortOrder": 0,
                          "displayName": "Garlic",
                          "quantity": 2,
                          "unit": "Clove",
                          "matchedFoodName": "Garlic",
                          "isPantryStaple": true,
                          "originalRawText": "2 cloves garlic, minced"
                        },
                        {
                          "sortOrder": 1,
                          "displayName": "Toasted Sesame Oil",
                          "quantity": 2,
                          "unit": "Tbsp",
                          "isPantryStaple": false,
                          "originalRawText": "2 tbsp toasted sesame oil"
                        }
                      ],
                      "steps": [
                        {
                          "stepNumber": 1,
                          "instruction": "Heat oil and sauté minced garlic for 2 minutes.",
                          "ingredientIndices": [0, 1],
                          "timers": [
                            {
                              "durationSeconds": 120,
                              "label": "Sauté"
                            }
                          ]
                        }
                      ],
                      "suggestedTagNames": ["Quick & Easy"],
                      "confidenceScore": 0.92
                    }
                    """;

                return Results.Ok(
                    new
                    {
                        model = "qwen2.5:14b",
                        message = new { role = "assistant", content = payloadJson },
                    }
                );
            }
        );

        app.MapGet(
            "/blog/tofu.png",
            () =>
            {
                var pngBytes = Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="
                );
                return Results.File(pngBytes, "image/png");
            }
        );

        app.MapGet(
            "/blog/crispy-sesame-tofu",
            () =>
            {
                var html = $$"""
                    <!DOCTYPE html>
                    <html>
                    <head>
                      <title>Crispy Sesame Tofu Recipe</title>
                      <script type="application/ld+json">
                      {
                        "@context": "https://schema.org",
                        "@type": "Recipe",
                        "name": "Crispy Sesame Tofu",
                        "description": "Golden crisp tofu tossed in fragrant sesame sauce.",
                        "author": { "@type": "Person", "name": "Chef Alex" },
                        "publisher": { "@type": "Organization", "name": "Food Blog" },
                        "image": "{{baseUrl}}/blog/tofu.png",
                        "recipeYield": "4 servings",
                        "prepTime": "PT15M",
                        "cookTime": "PT10M",
                        "totalTime": "PT25M",
                        "recipeIngredient": [
                          "2 cloves garlic, minced",
                          "2 tbsp toasted sesame oil"
                        ],
                        "recipeInstructions": [
                          {
                            "@type": "HowToStep",
                            "text": "Heat oil and sauté minced garlic for 2 minutes."
                          }
                        ]
                      }
                      </script>
                    </head>
                    <body>
                      <h1>Crispy Sesame Tofu</h1>
                      <p>Delicious golden tofu with sesame sauce.</p>
                    </body>
                    </html>
                    """;

                return Results.Content(html, "text/html");
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
public sealed class LlmIngestionE2ETests : E2ETestBase
{
    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task LlmSettingsAndUrlIngestion_ConfigureOllamaScrapeUrlReviewSideBySideAndPublish_CreatesRecipeAndNewFoodItem()
    {
        await using var mockServer = await MockLanLlmAndBlogServer.StartAsync();

        await RegisterAndLoginAsync();
        await EnsureFoodItemExistsViaApiAsync("Garlic", isPantryStaple: false);

        // 1. Open LLM Provider Settings dialog via user menu
        await Page.Locator(".user-profile-btn").ClickAsync();
        await Page.Locator("button", new() { HasText = "LLM Provider Settings" }).ClickAsync();

        var dialog = Page.Locator("app-llm-settings-dialog");
        await Expect(dialog).ToBeVisibleAsync();

        // Enter mock Ollama server Base URL
        var baseUrlInput = dialog.Locator("input[formcontrolname='baseUrl']");
        await baseUrlInput.FillAsync(mockServer.BaseUrl);

        // Test Connection
        await dialog
            .Locator("button[aria-label='Test LLM connection and refresh models']")
            .ClickAsync();
        var statusBanner = dialog.Locator(".connection-status-banner.status-success");
        await Expect(statusBanner).ToBeVisibleAsync();
        await Expect(statusBanner).ToContainTextAsync("Connected in");

        // Fill models & save
        var textModelInput = dialog.Locator("input[formcontrolname='textModel']");
        await textModelInput.FillAsync("qwen2.5:14b");

        var visionModelInput = dialog.Locator("input[formcontrolname='visionModel']");
        await visionModelInput.FillAsync("qwen2.5vl:7b");

        // Close autocomplete dropdown overlay by pressing Escape
        await Page.Keyboard.PressAsync("Escape");

        await dialog
            .Locator("button", new() { HasText = "Save Settings" })
            .ClickAsync(new() { Force = true });
        await Expect(dialog).Not.ToBeVisibleAsync();

        // 2. Navigate to /ingest and submit mock blog URL
        await Page.Locator("a.nav-link", new() { HasText = "Ingest" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/ingest.*"));

        var recipeUrl = $"{mockServer.BaseUrl}/blog/crispy-sesame-tofu";
        await Page.Locator("textarea[formcontrolname='urlsText']").FillAsync(recipeUrl);
        await Page.Locator("button[aria-label='Queue recipe URLs']").ClickAsync();

        // 3. Wait for job to process and reach ReadyForReview
        var reviewButton = Page.Locator("a.review-action-btn").First;
        await WaitForLocatorWithRefreshAsync(reviewButton);

        // Shell Ingest badge should display "1"
        var navBadge = Page.Locator("nav.desktop-nav .inbox-count-badge");
        await Expect(navBadge).ToContainTextAsync("1");

        // 4. Click "Review Side-by-Side"
        await reviewButton.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/ingest/review/.*"));

        // Verify Left Source Inspector displays scraped info
        var sourcePane = Page.Locator(".source-inspector-pane");
        await Expect(sourcePane).ToBeVisibleAsync();
        await Expect(sourcePane.Locator(".meta-link")).ToContainTextAsync("crispy-sesame-tofu");
        await Expect(sourcePane.Locator(".raw-lines-list"))
            .ToContainTextAsync("2 cloves garlic, minced");

        // Verify Right Draft Editor highlights new vs matched food items
        var draftPane = Page.Locator(".draft-editor-pane");
        await Expect(draftPane).ToBeVisibleAsync();
        await Expect(draftPane.Locator(".matched-food-pill")).ToContainTextAsync("Linked: Garlic");
        await Expect(draftPane.Locator(".new-food-item-banner"))
            .ToContainTextAsync("New Food Item");

        var proposedNameInput = draftPane.Locator("input[formcontrolname='proposedFoodItemName']");
        await Expect(proposedNameInput).ToHaveValueAsync("Toasted Sesame Oil");
        await Expect(draftPane.Locator(".proposed-tag-toggle"))
            .ToContainTextAsync("+ Quick & Easy");

        // 5. Approve & Publish
        await Page.Locator("button[aria-label='Approve and publish recipe']").ClickAsync();

        // Should navigate to published recipe view
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+$"));
        await Expect(Page.Locator(".recipe-title")).ToContainTextAsync("Crispy Sesame Tofu");
        await Expect(Page.Locator(".hero-tag-badge")).ToContainTextAsync("Quick & Easy");
        await Expect(Page.Locator(".ingredients-column")).ToContainTextAsync("Toasted Sesame Oil");

        // 6. Navigate back to /ingest and verify published job can be removed from the ingestion list
        await Page.Locator("a.nav-link", new() { HasText = "Ingest" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/ingest.*"));

        var publishedJobCard = Page.Locator(".job-card", new() { HasText = "Crispy Sesame Tofu" });
        await Expect(publishedJobCard).ToBeVisibleAsync();
        await Expect(publishedJobCard.Locator(".status-badge")).ToContainTextAsync("Published");

        await publishedJobCard.Locator("button.discard-btn").ClickAsync();
        await Expect(publishedJobCard).Not.ToBeVisibleAsync();

        // 7. Navigate to /catalog and verify "Toasted Sesame Oil" was created in the food catalog
        await Page.Locator("a.nav-link", new() { HasText = "Food Catalog" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/catalog.*"));
        await Expect(Page.Locator(".food-item-card", new() { HasText = "Toasted Sesame Oil" }))
            .ToBeVisibleAsync();
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task OfflineLlmHostResilience_RawTextQueuesAsWaitingForHost_ThenRetryAllWaitingCompletesWhenHostOnline()
    {
        await using var mockServer = await MockLanLlmAndBlogServer.StartAsync();

        await RegisterAndLoginAsync();

        // 1. Configure settings pointing to mock server
        await Page.GotoAsync($"{ServerUrl}/ingest");
        await Page.Locator(".llm-settings-btn").ClickAsync();

        var dialog = Page.Locator("app-llm-settings-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        await dialog.Locator("input[formcontrolname='baseUrl']").FillAsync(mockServer.BaseUrl);
        await dialog.Locator("input[formcontrolname='textModel']").FillAsync("qwen2.5:14b");
        await Page.Keyboard.PressAsync("Escape");

        await dialog
            .Locator("button", new() { HasText = "Save Settings" })
            .ClickAsync(new() { Force = true });
        await Expect(dialog).Not.ToBeVisibleAsync();

        // 2. Simulate offline host
        mockServer.SimulateOffline = true;

        // 3. Queue a Raw Text recipe
        await Page.Locator("button.tab-pill", new() { HasText = "Paste Raw Text" }).ClickAsync();
        await Page.Locator("input[formcontrolname='titleHint']").FillAsync("Spicy Peanut Noodles");
        await Page.Locator("textarea[formcontrolname='rawText']")
            .FillAsync("1 lb noodles\n3 tbsp peanut butter\nMix together and serve.");
        await Page.Locator("button[aria-label='Queue raw text recipe']").ClickAsync();

        // 4. Verify job enters WaitingForLlmHost and banner appears
        var waitingBanner = Page.Locator(".waiting-host-banner");
        await WaitForLocatorWithRefreshAsync(waitingBanner);
        await Expect(waitingBanner.Locator(".banner-title"))
            .ToContainTextAsync("waiting for local LLM host");

        var jobCard = Page.Locator(".job-card", new() { HasText = "Spicy Peanut Noodles" });
        await Expect(jobCard.Locator(".status-badge")).ToContainTextAsync("Waiting for LLM Host");

        // 5. Bring host back online and retry
        mockServer.SimulateOffline = false;

        await Page.RunAndWaitForResponseAsync(
            async () =>
            {
                await waitingBanner
                    .Locator("button[aria-label='Retry all waiting jobs']")
                    .ClickAsync();
            },
            resp => resp.Url.Contains("/api/ingestion/jobs/retry-waiting") && resp.Ok
        );

        // 6. Job should complete and transition to ReadyForReview
        var reviewBtn = Page.Locator("a.review-action-btn").First;
        await WaitForLocatorWithRefreshAsync(reviewBtn);

        // Open review and discard cleanly
        await reviewBtn.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/ingest/review/.*"));

        await Page.Locator("button[aria-label='Discard ingestion job']").ClickAsync();
        var confirmDialog = Page.Locator("app-confirm-dialog");
        await Expect(confirmDialog).ToBeVisibleAsync();
        await confirmDialog.Locator("button", new() { HasText = "Discard Job" }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex(".*/ingest$"));
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task IngestionAndReviewInbox_MobileViewport_360px_SwitchesSourceAndDraftPanesCleanly()
    {
        await using var mockServer = await MockLanLlmAndBlogServer.StartAsync();

        await RegisterAndLoginAsync();

        // 1. Set mobile viewport size to 360x640
        await Page.SetViewportSizeAsync(360, 640);

        // Verify bottom navigation bar has all 4 tabs and doesn't exceed 360px
        var bottomNav = Page.Locator(".mobile-bottom-nav");
        await Expect(bottomNav).ToBeVisibleAsync();

        var navScrollWidth = await bottomNav.EvaluateAsync<int>("el => el.scrollWidth");
        Assert.IsLessThanOrEqualTo(
            navScrollWidth,
            360,
            $"Mobile bottom nav width {navScrollWidth} must fit in 360px without overflow."
        );

        // Verify 4 tabs exist
        await Expect(bottomNav.Locator("a.mobile-tab", new() { HasText = "Cookbook" }))
            .ToBeVisibleAsync();
        await Expect(bottomNav.Locator("a.mobile-tab", new() { HasText = "Ingest" }))
            .ToBeVisibleAsync();
        await Expect(bottomNav.Locator("a.mobile-tab", new() { HasText = "Matcher" }))
            .ToBeVisibleAsync();
        await Expect(bottomNav.Locator("a.mobile-tab", new() { HasText = "Catalog" }))
            .ToBeVisibleAsync();

        // 2. Configure LLM settings pointing to mock server
        await bottomNav.Locator("a.mobile-tab", new() { HasText = "Ingest" }).ClickAsync();
        await Page.Locator(".llm-settings-btn").ClickAsync();

        var dialog = Page.Locator("app-llm-settings-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        await dialog.Locator("input[formcontrolname='baseUrl']").FillAsync(mockServer.BaseUrl);
        await dialog.Locator("input[formcontrolname='textModel']").FillAsync("qwen2.5:14b");
        await Page.Keyboard.PressAsync("Escape");

        await dialog
            .Locator("button", new() { HasText = "Save Settings" })
            .ClickAsync(new() { Force = true });
        await Expect(dialog).Not.ToBeVisibleAsync();

        // 3. Submit a raw text recipe
        await Page.Locator("button.tab-pill", new() { HasText = "Paste Raw Text" }).ClickAsync();
        await Page.Locator("textarea[formcontrolname='rawText']").FillAsync("Quick mobile toast");
        await Page.Locator("button[aria-label='Queue raw text recipe']").ClickAsync();

        var reviewBtn = Page.Locator("a.review-action-btn").First;
        await WaitForLocatorWithRefreshAsync(reviewBtn);
        await reviewBtn.ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex(".*/ingest/review/.*"));

        // 4. Verify mobile pane switcher
        var switcher = Page.Locator(".mobile-pane-switcher");
        await Expect(switcher).ToBeVisibleAsync();

        var sourcePane = Page.Locator(".source-inspector-pane");
        var draftPane = Page.Locator(".draft-editor-pane");

        // By default on mobile, draft is active
        await Expect(draftPane).ToBeVisibleAsync();
        await Expect(sourcePane).Not.ToBeVisibleAsync();

        // Switch to Recipe Preview on mobile
        await switcher.Locator("button", new() { HasText = "Preview" }).ClickAsync();
        await Expect(draftPane).ToBeVisibleAsync();
        await Expect(Page.Locator("app-recipe-detail-preview")).ToBeVisibleAsync();

        // Switch to Original Source
        await switcher.Locator("button", new() { HasText = "Original Source" }).ClickAsync();
        await Expect(sourcePane).ToBeVisibleAsync();
        await Expect(draftPane).Not.ToBeVisibleAsync();

        // Switch back to Structured Draft
        await switcher.Locator("button", new() { HasText = "Edit Draft" }).ClickAsync();
        await Expect(draftPane).ToBeVisibleAsync();
        await Expect(sourcePane).Not.ToBeVisibleAsync();

        // Switch to Recipe Preview on mobile
        await switcher.Locator("button", new() { HasText = "Preview" }).ClickAsync();
        await Expect(draftPane).ToBeVisibleAsync();
        await Expect(Page.Locator("app-recipe-detail-preview")).ToBeVisibleAsync();

        // Verify document scrollWidth does not exceed 360px (no horizontal overflow)
        var docScrollWidth = await Page.EvaluateAsync<int>(
            "() => document.documentElement.scrollWidth"
        );
        Assert.IsLessThanOrEqualTo(
            docScrollWidth,
            360,
            $"Mobile document scrollWidth {docScrollWidth} must not exceed 360px viewport."
        );
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task IngestionHub_AutoRefreshAndCountdownAndRetryAll_WorksAsExpected()
    {
        await RegisterAndLoginAsync();

        // 1. Navigate to /ingest
        await Page.GotoAsync($"{ServerUrl}/ingest");
        await Expect(Page).ToHaveURLAsync(new Regex(".*/ingest$"));

        // 2. Verify unified refresh controls in queue toolbar
        var refreshGroup = Page.Locator(".queue-toolbar-actions .unified-refresh-group");
        await Expect(refreshGroup).ToBeVisibleAsync();

        var refreshBtn = refreshGroup.Locator(".refresh-main-btn");
        await Expect(refreshBtn).ToBeVisibleAsync();
        await Expect(refreshBtn).ToContainTextAsync("Refresh");

        // Circular countdown spinner should be visible
        var countdownSpinner = refreshGroup.Locator(".countdown-spinner");
        await Expect(countdownSpinner).ToBeVisibleAsync();

        var configBtn = refreshGroup.Locator(".refresh-config-btn");
        await Expect(configBtn).ToBeVisibleAsync();

        // 3. Open menu and switch to Manual (Off)
        await configBtn.ClickAsync();
        var menu = Page.Locator(".refresh-menu-panel");
        await Expect(menu).ToBeVisibleAsync();

        await menu.Locator("button", new() { HasText = "Manual (Off)" }).ClickAsync();
        await Expect(refreshBtn).ToContainTextAsync("Refresh");
        await Expect(countdownSpinner).Not.ToBeVisibleAsync();

        // 4. Click manual refresh button
        await refreshBtn.ClickAsync();

        // 5. Open menu and switch interval to 30 seconds
        await configBtn.ClickAsync();
        await Expect(menu).ToBeVisibleAsync();
        await menu.Locator("button", new() { HasText = "30 seconds" }).ClickAsync();
        await Expect(countdownSpinner).ToBeVisibleAsync();
        await Expect(refreshBtn).ToContainTextAsync(new Regex(@"Refresh \(\d+s\)"));

        // 6. Verify Retry All button exists in queue toolbar
        var retryAllBtn = Page.Locator(".queue-toolbar .retry-all-tab-btn");
        await Expect(retryAllBtn).ToBeVisibleAsync();
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task IngestionHub_PauseAndResumeIngestion_TogglesStateAndShowsBanner()
    {
        await RegisterAndLoginAsync();

        await Page.GotoAsync($"{ServerUrl}/ingest");
        await Expect(Page).ToHaveURLAsync(new Regex(".*/ingest$"));

        // 1. Initial state: Pause Ingestion button is shown, no paused banner
        var pauseBtn = Page.Locator(".toggle-ingestion-btn");
        await Expect(pauseBtn).ToBeVisibleAsync();
        await Expect(pauseBtn).ToContainTextAsync("Pause Ingestion");

        var pausedBanner = Page.Locator(".paused-ingestion-banner");
        await Expect(pausedBanner).Not.ToBeVisibleAsync();

        // 2. Click Pause Ingestion
        await pauseBtn.ClickAsync();

        // Button switches to Resume Ingestion
        await Expect(pauseBtn).ToContainTextAsync("Resume Ingestion");

        // Paused banner appears
        await Expect(pausedBanner).ToBeVisibleAsync();
        await Expect(pausedBanner.Locator(".banner-content"))
            .ToContainTextAsync("LLM Ingestion is Paused");

        // 3. Click Resume button in banner
        var resumeBannerBtn = pausedBanner.Locator(".banner-resume-btn");
        await resumeBannerBtn.ClickAsync();

        // Banner disappears and header button switches back to Pause Ingestion
        await Expect(pausedBanner).Not.ToBeVisibleAsync();
        await Expect(pauseBtn).ToContainTextAsync("Pause Ingestion");
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task LlmSettingsDialog_AutoDiscoversModelsAndTogglesThinkingAndRetries()
    {
        await using var mockServer = await MockLanLlmAndBlogServer.StartAsync();

        await RegisterAndLoginAsync();

        await Page.GotoAsync($"{ServerUrl}/ingest");
        await Page.Locator(".llm-settings-btn").ClickAsync();

        var dialog = Page.Locator("app-llm-settings-dialog");
        await Expect(dialog).ToBeVisibleAsync();

        // Enter mock server baseUrl and test connection to discover models
        await dialog.Locator("input[formcontrolname='baseUrl']").FillAsync(mockServer.BaseUrl);
        await dialog
            .Locator("button[aria-label='Test LLM connection and refresh models']")
            .ClickAsync();

        var statusBanner = dialog.Locator(".connection-status-banner.status-success");
        await Expect(statusBanner).ToBeVisibleAsync();
        await Expect(statusBanner).ToContainTextAsync("model(s) discovered");

        // Check Thinking toggle
        var thinkingToggle = dialog.Locator(
            "mat-slide-toggle[formcontrolname='enableThinking'] button"
        );
        await Expect(thinkingToggle).ToBeVisibleAsync();
        await thinkingToggle.ClickAsync();

        // Check retries input
        var retriesInput = dialog.Locator("input[formcontrolname='maxIngestionRetries']");
        await Expect(retriesInput).ToBeVisibleAsync();
        await retriesInput.FillAsync("4");

        // Check enable ingestion toggle
        var ingestionToggle = dialog.Locator(
            "mat-slide-toggle[formcontrolname='enableIngestion'] button"
        );
        await Expect(ingestionToggle).ToBeVisibleAsync();

        await Page.Keyboard.PressAsync("Escape");

        // Save
        await dialog
            .Locator("button", new() { HasText = "Save Settings" })
            .ClickAsync(new() { Force = true });
        await Expect(dialog).Not.ToBeVisibleAsync();

        // Re-open to verify persisted values
        await Page.Locator(".llm-settings-btn").ClickAsync();
        await Expect(dialog).ToBeVisibleAsync();

        var savedRetries = dialog.Locator("input[formcontrolname='maxIngestionRetries']");
        await Expect(savedRetries).ToHaveValueAsync("4");
        await dialog.Locator("button", new() { HasText = "Cancel" }).ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task IngestionReview_RecipeDetailPreview_InlineToggleAndDialogPreview_WorksAsExpected()
    {
        await using var mockServer = await MockLanLlmAndBlogServer.StartAsync();

        await RegisterAndLoginAsync();

        // 1. Configure LLM settings pointing to mock server
        await Page.GotoAsync($"{ServerUrl}/ingest");
        await Page.Locator(".llm-settings-btn").ClickAsync();

        var dialog = Page.Locator("app-llm-settings-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        await dialog.Locator("input[formcontrolname='baseUrl']").FillAsync(mockServer.BaseUrl);
        await dialog.Locator("input[formcontrolname='textModel']").FillAsync("qwen2.5:14b");
        await Page.Keyboard.PressAsync("Escape");

        await dialog
            .Locator("button", new() { HasText = "Save Settings" })
            .ClickAsync(new() { Force = true });
        await Expect(dialog).Not.ToBeVisibleAsync();

        // 2. Submit a raw text recipe
        await Page.Locator("button.tab-pill", new() { HasText = "Paste Raw Text" }).ClickAsync();
        await Page.Locator("textarea[formcontrolname='rawText']")
            .FillAsync("Crispy Sesame Tofu with garlic and oil");
        await Page.Locator("button[aria-label='Queue raw text recipe']").ClickAsync();

        var reviewBtn = Page.Locator("a.review-action-btn").First;
        await WaitForLocatorWithRefreshAsync(reviewBtn);
        await reviewBtn.ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex(".*/ingest/review/.*"));

        // 3. Verify Preview button exists in header actions
        var headerPreviewBtn = Page.Locator(".header-actions button.preview-btn");
        await Expect(headerPreviewBtn).ToBeVisibleAsync();
        await Expect(headerPreviewBtn).ToContainTextAsync("Preview");

        // 4. Verify segmented view toggle in draft pane toolbar
        var paneToolbar = Page.Locator(".draft-pane-toolbar");
        await Expect(paneToolbar).ToBeVisibleAsync();

        var editToggle = paneToolbar.Locator("button", new() { HasText = "Edit Draft" });
        var previewToggle = paneToolbar.Locator("button", new() { HasText = "Recipe Preview" });
        await Expect(editToggle).ToBeVisibleAsync();
        await Expect(previewToggle).ToBeVisibleAsync();

        // Initially in Edit Draft mode
        await Expect(Page.Locator(".draft-form")).ToBeVisibleAsync();
        await Expect(Page.Locator("app-recipe-detail-preview")).Not.ToBeVisibleAsync();

        // 5. Click "Recipe Preview" in pane toolbar
        await previewToggle.ClickAsync();

        var inlinePreview = Page.Locator(".inline-preview-pane app-recipe-detail-preview");
        await Expect(inlinePreview).ToBeVisibleAsync();
        await Expect(Page.Locator(".draft-form")).Not.ToBeVisibleAsync();

        // Verify recipe details in preview
        await Expect(inlinePreview.Locator(".recipe-title"))
            .ToContainTextAsync("Crispy Sesame Tofu");
        await Expect(inlinePreview.Locator(".recipe-stats-strip")).ToContainTextAsync("15 min");
        await Expect(inlinePreview.Locator(".ingredients-column")).ToContainTextAsync("Garlic");
        await Expect(inlinePreview.Locator(".instructions-column"))
            .ToContainTextAsync("Heat oil and sauté");

        // Verify expand button in pane toolbar
        var expandBtn = paneToolbar.Locator(".open-modal-preview-btn");
        await Expect(expandBtn).ToBeVisibleAsync();

        // 6. Switch back to Edit Draft
        await editToggle.ClickAsync();
        await Expect(Page.Locator(".draft-form")).ToBeVisibleAsync();
        await Expect(inlinePreview).Not.ToBeVisibleAsync();

        // 7. Open full modal preview via header button
        await headerPreviewBtn.ClickAsync();
        var previewModal = Page.Locator("app-recipe-detail-preview-dialog");
        await Expect(previewModal).ToBeVisibleAsync();
        await Expect(previewModal.Locator(".dialog-title"))
            .ToContainTextAsync("Recipe Detail Preview");
        await Expect(previewModal.Locator(".recipe-title"))
            .ToContainTextAsync("Crispy Sesame Tofu");

        // Close modal via "Back to Edit"
        await previewModal.Locator("button.back-btn").ClickAsync();
        await Expect(previewModal).Not.ToBeVisibleAsync();
        await Expect(Page.Locator(".draft-form")).ToBeVisibleAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();
    }
}

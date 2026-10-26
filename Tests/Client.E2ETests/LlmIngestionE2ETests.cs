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
                          "suggestedStoreAisleName": "Produce",
                          "isPantryStaple": true,
                          "originalRawText": "2 cloves garlic, minced"
                        },
                        {
                          "sortOrder": 1,
                          "displayName": "Toasted Sesame Oil",
                          "quantity": 2,
                          "unit": "Tbsp",
                          "suggestedStoreAisleName": "Spices & Baking",
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
    public async Task LlmSettingsAndUrlIngestion_ConfigureOllamaScrapeUrlReviewSideBySideAndPublish_CreatesRecipeAndNewFoodItem()
    {
        await using var mockServer = await MockLanLlmAndBlogServer.StartAsync();

        await RegisterAndLoginAsync();

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
        await Expect(reviewButton).ToBeVisibleAsync(new() { Timeout = 20000 });

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

        // 5. Approve & Publish
        await Page.Locator("button[aria-label='Approve and publish recipe']").ClickAsync();

        // Should navigate to published recipe view
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+$"));
        await Expect(Page.Locator(".recipe-title")).ToContainTextAsync("Crispy Sesame Tofu");
        await Expect(Page.Locator(".ingredients-column")).ToContainTextAsync("Toasted Sesame Oil");

        // 6. Navigate to /catalog and verify "Toasted Sesame Oil" was created in the food catalog
        await Page.Locator("a.nav-link", new() { HasText = "Food Catalog" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/catalog.*"));
        await Expect(Page.Locator(".food-item-card", new() { HasText = "Toasted Sesame Oil" }))
            .ToBeVisibleAsync();
    }

    [TestMethod]
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
        await Expect(waitingBanner).ToBeVisibleAsync(new() { Timeout = 15000 });
        await Expect(waitingBanner.Locator(".banner-title"))
            .ToContainTextAsync("waiting for local LLM host");

        var jobCard = Page.Locator(".job-card", new() { HasText = "Spicy Peanut Noodles" });
        await Expect(jobCard.Locator(".status-badge")).ToContainTextAsync("Waiting for LLM Host");

        // 5. Bring host back online and retry
        mockServer.SimulateOffline = false;

        await waitingBanner.Locator("button[aria-label='Retry all waiting jobs']").ClickAsync();

        // 6. Job should complete and transition to ReadyForReview
        var reviewBtn = Page.Locator("a.review-action-btn").First;
        await Expect(reviewBtn).ToBeVisibleAsync(new() { Timeout = 15000 });

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
        await Expect(reviewBtn).ToBeVisibleAsync(new() { Timeout = 15000 });
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

        // Switch to Original Source
        await switcher.Locator("button", new() { HasText = "Original Source" }).ClickAsync();
        await Expect(sourcePane).ToBeVisibleAsync();
        await Expect(draftPane).Not.ToBeVisibleAsync();

        // Switch back to Structured Draft
        await switcher.Locator("button", new() { HasText = "Structured Draft" }).ClickAsync();
        await Expect(draftPane).ToBeVisibleAsync();
        await Expect(sourcePane).Not.ToBeVisibleAsync();

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
}

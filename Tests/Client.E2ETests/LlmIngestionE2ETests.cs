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

public sealed class MockLanLlmAndBlogServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    public string BaseUrl { get; }
    public bool SimulateOffline { get; set; }
    public string? ResponsePayload { get; set; }
    public Dictionary<string, string> ResponsesByModel { get; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<string> RequestedModels { get; } = [];
    public int? LastNumGpu { get; private set; }
    public double? LastTemperature { get; private set; }
    public double? LastTopP { get; private set; }
    public int? LastTopK { get; private set; }
    public string? LastUserPrompt { get; private set; }

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
            async (HttpRequest request) =>
            {
                using var body = await JsonDocument.ParseAsync(request.Body);
                var userContent = body
                    .RootElement.GetProperty("messages")[1]
                    .GetProperty("content");
                server.LastUserPrompt =
                    userContent.ValueKind == JsonValueKind.String
                        ? userContent.GetString()
                        : userContent[0].GetProperty("text").GetString();
                var hasOptions = body.RootElement.TryGetProperty("options", out var options);
                server.LastNumGpu =
                    hasOptions && options.TryGetProperty("num_gpu", out var numGpu)
                        ? numGpu.GetInt32()
                        : null;
                server.LastTemperature =
                    hasOptions && options.TryGetProperty("temperature", out var temperature)
                        ? temperature.GetDouble()
                        : null;
                server.LastTopP =
                    hasOptions && options.TryGetProperty("top_p", out var topP)
                        ? topP.GetDouble()
                        : null;
                server.LastTopK =
                    hasOptions && options.TryGetProperty("top_k", out var topK)
                        ? topK.GetInt32()
                        : null;
                if (server.SimulateOffline)
                {
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }

                var requestedModel = body.RootElement.TryGetProperty("model", out var modelValue)
                    ? modelValue.GetString() ?? ""
                    : "";
                lock (server.RequestedModels)
                {
                    server.RequestedModels.Add(requestedModel);
                }

                var payloadJson =
                    (
                        server.ResponsesByModel.TryGetValue(requestedModel, out var modelPayload)
                            ? modelPayload
                            : null
                    )
                    ?? server.ResponsePayload
                    ?? """
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
                        message = new
                        {
                            role = "assistant",
                            content = TaggedPipelineMockResponder.Respond(
                                body.RootElement,
                                payloadJson
                            ),
                        },
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
    // Matches the mock host's default tofu payload; the tagged pipeline keeps this method verbatim.
    private const string TofuSourceText =
        "Crispy Sesame Tofu\nIngredients:\n- 2 cloves garlic, minced\n- 2 tbsp toasted sesame oil\nInstructions:\nHeat oil and sauté minced garlic for 2 minutes.";

    // Tab (not Escape) leaves the model field: with no autocomplete panel open, Escape closes
    // the dialog and cancels the in-flight save.
    private async Task SaveLlmSettingsDialogAsync(ILocator dialog)
    {
        await Page.Keyboard.PressAsync("Tab");
        await Page.RunAndWaitForResponseAsync(
            () => dialog.Locator("button", new() { HasText = "Save Settings" }).ClickAsync(),
            resp =>
                resp.Url.EndsWith("/api/llm-settings") && resp.Request.Method == "PUT" && resp.Ok
        );
        await Expect(dialog).Not.ToBeVisibleAsync();
    }

    private async Task DiscardAllJobCardsAsync()
    {
        var discardButtons = Page.Locator(".job-card button.discard-btn");
        for (var remaining = await discardButtons.CountAsync(); remaining > 0; remaining--)
        {
            await discardButtons.First.ClickAsync();
            await Expect(discardButtons).ToHaveCountAsync(remaining - 1);
        }
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task ReviewRepair_PreviewsCancelsAppliesAndSavesWithoutLosingEditsOnMobile()
    {
        await using var mockServer = await MockLanLlmAndBlogServer.StartAsync();
        mockServer.ResponsePayload = """
            {
              "title": "Repair Journey", "baseServings": 4,
              "ingredients": [
                {"displayName":"butter","canonicalFoodName":"butter","quantity":1,"unit":"Tbsp","originalRawText":"1 tbsp butter"},
                {"displayName":"salt","canonicalFoodName":"salt","quantity":null,"unit":null,"originalRawText":"Salt"}
              ],
              "steps": [
                {"instruction":"Melt butter and add salt.","ingredientIndices":[0],
                 "timers":[{"durationSeconds":60,"label":"Cook"}]}
              ]
            }
            """;
        await RegisterAndLoginAsync();
        await Page.GotoAsync($"{ServerUrl}/ingest");
        await Page.Locator(".llm-settings-btn").ClickAsync();
        var settings = Page.Locator("app-llm-settings-dialog");
        await settings.Locator("input[formcontrolname='baseUrl']").FillAsync(mockServer.BaseUrl);
        await settings
            .Locator("button[aria-label='Test LLM connection and refresh models']")
            .ClickAsync();
        await Expect(settings.Locator(".connection-status-banner.status-success"))
            .ToBeVisibleAsync();
        await settings.Locator("input[formcontrolname='textModel']").FillAsync("qwen2.5:14b");
        await settings.Locator("button", new() { HasText = "Save Settings" }).ClickAsync();
        await Expect(settings).Not.ToBeVisibleAsync();
        await Page.Locator("button.tab-pill", new() { HasText = "Paste Raw Text" }).ClickAsync();
        await Page.Locator("textarea[formcontrolname='rawText']")
            .FillAsync(
                "Repair Journey\nIngredients:\n- 1 tbsp butter\n- Salt\nInstructions:\nMelt butter and add salt, about 1 minute."
            );
        var queuedResponse = await Page.RunAndWaitForResponseAsync(
            () => Page.Locator("button[aria-label='Queue raw text recipe']").ClickAsync(),
            response =>
                response.Url.EndsWith("/api/ingestion/jobs/text", StringComparison.Ordinal)
                && response.Request.Method == "POST"
        );
        Assert.IsTrue(queuedResponse.Ok);
        using var queuedJob = JsonDocument.Parse(await queuedResponse.TextAsync());
        var jobId = queuedJob.RootElement.GetProperty("id").GetGuid();
        try
        {
            var reviewButton = Page.Locator($"a.review-action-btn[href$='{jobId}']");
            await WaitForLocatorWithRefreshAsync(reviewButton);
            await reviewButton.ClickAsync();
            await Page.SetViewportSizeAsync(360, 800);

            var reviewCard = Page.Locator(".review-diagnostics-card");
            await Expect(reviewCard).ToContainTextAsync("vague label");
            await Expect(reviewCard).ToContainTextAsync("salt");
            var title = Page.Locator("input[formcontrolname='title']");
            await title.FillAsync("Unsaved user title");
            await Expect(reviewCard).ToContainTextAsync("draft has changed");
            await reviewCard.Locator("button", new() { HasText = "Recheck issues" }).ClickAsync();
            await Expect(reviewCard).Not.ToContainTextAsync("draft has changed");

            mockServer.ResponsePayload = """
                {
                  "title":"AI proposal","baseServings":4,
                  "ingredients":[
                    {"displayName":"butter","canonicalFoodName":"butter","quantity":1,"unit":"Tbsp","originalRawText":"1 tbsp butter"},
                    {"displayName":"salt","canonicalFoodName":"salt","quantity":null,"unit":null,"originalRawText":"Salt"}
                  ],
                  "steps":[
                    {"instruction":"Melt butter, then add salt.","ingredientIndices":[0,1],
                     "timers":[{"durationSeconds":60,"label":"Melt butter"}]}
                  ]
                }
                """;
            await reviewCard.Locator("textarea").FillAsync("Link the salt.");
            await reviewCard
                .Locator("button", new() { HasText = "Re-extract with AI" })
                .ClickAsync();
            var preview = Page.Locator("app-ingestion-repair-dialog");
            await Expect(preview).ToBeVisibleAsync();
            await Expect(preview).ToContainTextAsync("Unsaved user title");
            await Expect(preview).ToContainTextAsync("AI proposal");
            await Expect(preview).ToContainTextAsync("Linked ingredients");
            await Expect(preview).ToContainTextAsync("Melt butter: 60s");
            StringAssert.Contains(mockServer.LastUserPrompt!, "Link the salt.");
            Assert.IsTrue(
                await Page.EvaluateAsync<bool>(
                    "document.documentElement.scrollWidth <= window.innerWidth"
                ),
                "The review page must fit a 360px viewport."
            );
            Assert.IsTrue(
                await preview.EvaluateAsync<bool>("el => el.scrollWidth <= el.clientWidth"),
                "The repair dialog must fit a 360px viewport."
            );
            await preview.Locator("button", new() { HasText = "Cancel" }).ClickAsync();
            await Expect(title).ToHaveValueAsync("Unsaved user title");
            await Page.ReloadAsync();
            await Expect(title).ToHaveValueAsync("Repair Journey");

            await title.FillAsync("Unsaved after reload");
            mockServer.SimulateOffline = true;
            await reviewCard
                .Locator("button", new() { HasText = "Re-extract with AI" })
                .ClickAsync();
            await Expect(reviewCard.Locator("[role='alert']")).ToBeVisibleAsync();
            await Expect(title).ToHaveValueAsync("Unsaved after reload");
            mockServer.SimulateOffline = false;

            await reviewCard
                .Locator("button", new() { HasText = "Re-extract with AI" })
                .ClickAsync();
            await Expect(preview).ToBeVisibleAsync();
            await preview.Locator("button", new() { HasText = "Apply to editor" }).ClickAsync();
            await Expect(title).ToHaveValueAsync("AI proposal");
            await Page.ReloadAsync();
            await Expect(title).ToHaveValueAsync("Repair Journey");

            await reviewCard
                .Locator("button", new() { HasText = "Re-extract with AI" })
                .ClickAsync();
            await Expect(preview).ToBeVisibleAsync();
            await preview.Locator("button", new() { HasText = "Apply to editor" }).ClickAsync();
            await Expect(title).ToHaveValueAsync("AI proposal");
            await Page.Locator("button[aria-label='Save review draft']").ClickAsync();
            await Expect(
                    Page.Locator("mat-snack-bar-container")
                        .Filter(new() { HasText = "Review draft saved." })
                )
                .ToBeVisibleAsync();
            await Page.ReloadAsync();
            await Expect(title).ToHaveValueAsync("AI proposal");
        }
        finally
        {
            var status = await Page.EvaluateAsync<int>(
                """
                async jobId => {
                    const tokenKey = Object.keys(localStorage).find(key => key.endsWith('-token'));
                    const token = tokenKey ? localStorage.getItem(tokenKey) : null;
                    const response = await fetch(`/api/ingestion/jobs/${jobId}`, {
                        method: 'DELETE',
                        headers: { Authorization: `Bearer ${token}` }
                    });
                    return response.status;
                }
                """,
                jobId.ToString()
            );
            Assert.AreEqual(204, status, "The review journey must remove its own ingestion job.");
        }
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task StarterMeasurements_SaveReviewPublishScaleAndSwitchOnMobile_ScalesTaggedPortions()
    {
        await using var mockServer = await MockLanLlmAndBlogServer.StartAsync();
        mockServer.ResponsePayload = """
            {
              "title": "Measurement Starter", "baseServings": 4, "confidenceScore": 0.90,
              "ingredients": [{
                "displayName": "Starter Flour", "quantity": 5, "unit": "Cup",
                "originalRawText": "5 cups (25 ounces) Starter Flour"
              }],
              "steps": [
                { "instruction": "Combine 1 cup Starter Flour with water.", "ingredientIndices": [0] },
                { "instruction": "Feed with 4 cups Starter Flour.", "ingredientIndices": [0] }
              ]
            }
            """;
        await RegisterAndLoginAsync();
        await Page.GotoAsync($"{ServerUrl}/ingest");
        await Page.Locator(".llm-settings-btn").ClickAsync();
        var dialog = Page.Locator("app-llm-settings-dialog");
        await dialog.Locator("input[formcontrolname='baseUrl']").FillAsync(mockServer.BaseUrl);
        await dialog
            .Locator("button[aria-label='Test LLM connection and refresh models']")
            .ClickAsync();
        await Expect(dialog.Locator(".connection-status-banner.status-success")).ToBeVisibleAsync();
        await dialog.Locator("input[formcontrolname='textModel']").FillAsync("qwen2.5:14b");
        await dialog.Locator("mat-checkbox[formcontrolname='ollamaCpuOnly'] label").ClickAsync();
        await dialog.Locator("button", new() { HasText = "Save Settings" }).ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();
        await Page.Locator(".llm-settings-btn").ClickAsync();
        await Expect(dialog.Locator("mat-checkbox[formcontrolname='ollamaCpuOnly'] input"))
            .ToBeCheckedAsync();
        await dialog.Locator("button", new() { HasText = "Save Settings" }).ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();

        await Page.Locator("button.tab-pill", new() { HasText = "Paste Raw Text" }).ClickAsync();
        await Page.Locator("input[formcontrolname='titleHint']").FillAsync("Measurement Starter");
        await Page.Locator("textarea[formcontrolname='rawText']")
            .FillAsync(
                "5 cups (25 ounces) Starter Flour\n4A. Combine 1 cup Starter Flour with water.\n4B. Feed with 4 cups Starter Flour."
            );
        await Page.Locator("button[aria-label='Queue raw text recipe']").ClickAsync();
        var job = Page.Locator(".job-card", new() { HasText = "Measurement Starter" });
        await WaitForLocatorWithRefreshAsync(job.Locator("a.review-action-btn"));
        Assert.AreEqual(0, mockServer.LastNumGpu);
        await job.Locator("a.review-action-btn").ClickAsync();
        await Page.SetViewportSizeAsync(360, 800);
        var alternative = Page.Locator(
            "app-measurement-editor[formcontrolname='alternativeMeasurement']"
        );
        await Expect(alternative.Locator("input[formcontrolname='quantity']"))
            .ToHaveValueAsync("25");
        await Page.Locator("mat-checkbox[formcontrolname='preferAlternativeMeasurement'] input")
            .CheckAsync();
        var instructions = Page.Locator("textarea[formcontrolname='instruction']");
        await Expect(instructions.First)
            .ToHaveValueAsync("Combine 1 cup Starter Flour with water.");
        var taggedRows = Page.Locator("[data-testid=inline-amount-row]");
        await Expect(taggedRows).ToHaveCountAsync(2);
        await Expect(taggedRows.First.Locator("[data-testid=inline-amount-ingredient]"))
            .ToContainTextAsync("Starter Flour");
        await Page.Locator("button[aria-label='Save review draft']").ClickAsync();
        await Expect(Page.Locator("mat-snack-bar-container"))
            .ToContainTextAsync("Review draft saved.");
        await Page.ReloadAsync();
        await Expect(
                Page.Locator("mat-checkbox[formcontrolname='preferAlternativeMeasurement'] input")
            )
            .ToBeCheckedAsync();
        await Expect(taggedRows).ToHaveCountAsync(2);
        Assert.IsTrue(
            await Page.EvaluateAsync<bool>(
                "document.documentElement.scrollWidth <= window.innerWidth"
            ),
            "Review must fit a 360px viewport."
        );
        await Page.Locator("button[aria-label='Approve and publish recipe']").ClickAsync();
        await Expect(Page.Locator(".recipe-title")).ToHaveTextAsync("Measurement Starter");
        await Page.Locator("mat-button-toggle[value='Metric']").ClickAsync();
        // Tagged portions scale in place through the preferred 25-ounce weight.
        var chips = Page.Locator(".step-instruction-text .inline-amount");
        await Expect(chips.First).ToHaveTextAsync("142 g");
        await Expect(chips.Nth(1)).ToHaveTextAsync("567 g");
        await Page.GetByRole(AriaRole.Button, new() { Name = "0.5x", Exact = true }).ClickAsync();
        await Expect(chips.First).ToHaveTextAsync("71 g");
        await Page.GetByRole(AriaRole.Button, new() { Name = "2x", Exact = true }).ClickAsync();
        await Expect(chips.First).ToHaveTextAsync("283 g");
        var switchButton = Page.Locator(
            "button[aria-label='Switch measurement for Starter Flour']"
        );
        await switchButton.ClickAsync();
        await Expect(chips.First).ToHaveTextAsync("473 ml");
        await switchButton.ClickAsync();
        await Expect(chips.First).ToHaveTextAsync("283 g");
        Assert.IsTrue(
            await Page.EvaluateAsync<bool>(
                "document.documentElement.scrollWidth <= window.innerWidth"
            ),
            "Detail must fit a 360px viewport."
        );

        await Page.Locator("button[aria-label='Start cooking in kitchen mode']").ClickAsync();
        var cookChip = Page.Locator(".instruction-text .inline-amount").First;
        await Page.Locator("mat-button-toggle[value='Metric']").ClickAsync();
        await Expect(cookChip).ToHaveTextAsync("283 g");
        await Page.GetByRole(AriaRole.Button, new() { Name = "0.5x", Exact = true }).ClickAsync();
        await Expect(cookChip).ToHaveTextAsync("71 g");
        await Page.GetByRole(AriaRole.Button, new() { Name = "2x", Exact = true }).ClickAsync();
        await Expect(cookChip).ToHaveTextAsync("283 g");
        await Page.Locator("button[aria-label='Open ingredients drawer']").ClickAsync();
        await Page.Locator(
                ".mobile-ingredients-drawer button[aria-label='Switch measurement for Starter Flour']"
            )
            .ClickAsync();
        await Page.Locator("button[aria-label='Close ingredients drawer']").ClickAsync();
        await Expect(cookChip).ToHaveTextAsync("473 ml");
        Assert.IsTrue(
            await Page.EvaluateAsync<bool>(
                "document.documentElement.scrollWidth <= window.innerWidth"
            ),
            "Cook mode must fit a 360px viewport."
        );
        await Page.Locator("button.exit-cook-btn").ClickAsync();
        await Page.Locator("button[aria-label='More recipe actions']").ClickAsync();
        await Page.Locator("[aria-label='Edit recipe']").ClickAsync();
        await Expect(alternative.Locator("input[formcontrolname='quantity']"))
            .ToHaveValueAsync("25");
        await Expect(taggedRows).ToHaveCountAsync(2);
        Assert.IsTrue(
            await Page.EvaluateAsync<bool>(
                "document.documentElement.scrollWidth <= window.innerWidth"
            ),
            "Editor must fit a 360px viewport."
        );
    }

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
        await SaveLlmSettingsDialogAsync(dialog);
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
        await SaveLlmSettingsDialogAsync(dialog);
        await Expect(dialog).Not.ToBeVisibleAsync();

        // 3. Submit a raw text recipe
        await Page.Locator("button.tab-pill", new() { HasText = "Paste Raw Text" }).ClickAsync();
        await Page.Locator("textarea[formcontrolname='rawText']").FillAsync(TofuSourceText);
        await Page.Locator("button[aria-label='Queue raw text recipe']").ClickAsync();

        var reviewBtn = Page.Locator("a.review-action-btn").First;
        await WaitForLocatorWithRefreshAsync(reviewBtn);
        await reviewBtn.ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex(".*/ingest/review/.*"));

        // Review header actions render as a tidy grid without text wrapping
        var headerButtons = Page.Locator(".review-header .header-actions > button");
        await Expect(headerButtons.First).ToBeVisibleAsync();
        var headerButtonHeights = await headerButtons.EvaluateAllAsync<double[]>(
            "els => els.map(el => el.getBoundingClientRect().height)"
        );
        foreach (var height in headerButtonHeights)
        {
            Assert.IsLessThanOrEqualTo(48.0, height, "Review action buttons must not wrap text.");
        }

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
    public async Task IngestionHub_RunNowWhilePaused_ProcessesOnlyTheChosenJob()
    {
        await using var mockServer = await MockLanLlmAndBlogServer.StartAsync();

        await RegisterAndLoginAsync();

        await Page.GotoAsync($"{ServerUrl}/ingest");
        await Page.Locator(".llm-settings-btn").ClickAsync();

        var dialog = Page.Locator("app-llm-settings-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        await dialog.Locator("input[formcontrolname='baseUrl']").FillAsync(mockServer.BaseUrl);
        await dialog.Locator("input[formcontrolname='textModel']").FillAsync("qwen2.5:14b");
        await SaveLlmSettingsDialogAsync(dialog);
        await Expect(dialog).Not.ToBeVisibleAsync();

        var pauseBtn = Page.Locator(".toggle-ingestion-btn");
        await pauseBtn.ClickAsync();
        try
        {
            await Expect(pauseBtn).ToContainTextAsync("Resume Ingestion");
            await Expect(Page.Locator(".paused-ingestion-banner")).ToContainTextAsync("Run now");

            async Task QueueAsync(string title)
            {
                await Page.Locator("input[formcontrolname='titleHint']").FillAsync(title);
                await Page.Locator("textarea[formcontrolname='rawText']")
                    .FillAsync("1 lb noodles\n3 tbsp peanut butter\nMix together and serve.");
                await Page.Locator("button[aria-label='Queue raw text recipe']").ClickAsync();
                await Expect(Page.Locator(".job-card", new() { HasText = title }))
                    .ToBeVisibleAsync();
            }

            await Page.Locator("button.tab-pill", new() { HasText = "Paste Raw Text" })
                .ClickAsync();
            // Jobs are shared across tests, so start from an empty list.
            await DiscardAllJobCardsAsync();
            await QueueAsync("Held Peanut Noodles");
            await QueueAsync("Chosen Peanut Noodles");

            var heldCard = Page.Locator(".job-card", new() { HasText = "Held Peanut Noodles" });
            var chosenCard = Page.Locator(".job-card", new() { HasText = "Chosen Peanut Noodles" });
            var runNowBtn = chosenCard.Locator("button.run-now-btn");
            await Expect(runNowBtn).ToContainTextAsync("Run now");

            await Page.RunAndWaitForResponseAsync(
                () => runNowBtn.ClickAsync(),
                resp => resp.Url.Contains("/run-now") && resp.Status == 202
            );

            // Extraction retitles the chosen card with the mock recipe's title.
            var processedCard = Page.Locator(".job-card", new() { HasText = "Crispy Sesame Tofu" });
            await WaitForLocatorWithRefreshAsync(processedCard.Locator("a.review-action-btn"));
            await Expect(chosenCard).ToHaveCountAsync(0);
            await Expect(Page.Locator(".job-card")).ToHaveCountAsync(2);
            await Expect(heldCard.Locator("a.review-action-btn")).Not.ToBeVisibleAsync();
            await Expect(heldCard.Locator("button.run-now-btn")).ToContainTextAsync("Run now");
            await Expect(pauseBtn).ToContainTextAsync("Resume Ingestion");
        }
        finally
        {
            // Household settings and jobs are shared: drop this test's jobs (the held one targets a
            // mock server that is about to stop) and never leave ingestion paused for later tests.
            await DiscardAllJobCardsAsync();
            if ((await pauseBtn.InnerTextAsync()).Contains("Resume Ingestion"))
            {
                await pauseBtn.ClickAsync();
                await Expect(pauseBtn).ToContainTextAsync("Pause Ingestion");
            }
        }
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task IngestionHub_MobileViewport_360px_ActionsFitAndJobCardsDoNotOverflow()
    {
        const string longTitle =
            "Sweet and Saucy Grilled Salmon with Lime-Jalapeño Glaze and Extra Long Supercalifragilisticexpialidocious Garnish";
        var longDetail =
            "Attempt 3/3 failed: The LLM host returned an invalid response while extracting the structured recipe. "
            + string.Join(" ", Enumerable.Repeat("Additional diagnostic context.", 8))
            + " END-OF-DETAIL";

        await Page.RouteAsync(
            new Regex(@"/api/ingestion/jobs\?"),
            async route =>
            {
                var response = await route.FetchAsync();
                var body = System.Text.Json.Nodes.JsonNode.Parse(await response.TextAsync())!;
                if (body["items"] is System.Text.Json.Nodes.JsonArray items)
                {
                    foreach (var item in items)
                    {
                        item!["statusDetail"] = longDetail;
                    }
                }
                await route.FulfillAsync(new() { Response = response, Body = body.ToJsonString() });
            }
        );

        await RegisterAndLoginAsync();
        await Page.SetViewportSizeAsync(360, 640);
        await Page.GotoAsync($"{ServerUrl}/ingest");

        // 1. Pause ingestion so the queued job stays put
        var toggleBtn = Page.Locator(".toggle-ingestion-btn");
        try
        {
            await toggleBtn.ClickAsync();
            await Expect(toggleBtn).ToContainTextAsync("Resume Ingestion");

            // 2. Header action buttons share the same full width
            var toggleBox = (await toggleBtn.BoundingBoxAsync())!;
            var settingsBox = (await Page.Locator(".llm-settings-btn").BoundingBoxAsync())!;
            Assert.AreEqual(settingsBox.Width, toggleBox.Width, 1.0);

            // 3. Banner Resume button keeps its horizontal padding around the icon
            var resumeBtn = Page.Locator(".paused-ingestion-banner .banner-resume-btn");
            await Expect(resumeBtn).ToBeVisibleAsync();
            var resumeBox = (await resumeBtn.BoundingBoxAsync())!;
            var resumeIconBox = (await resumeBtn.Locator("mat-icon").BoundingBoxAsync())!;
            Assert.IsGreaterThanOrEqualTo(8.0, resumeIconBox.X - resumeBox.X);
            var resumeOverflows = await resumeBtn.EvaluateAsync<bool>(
                "el => el.scrollWidth > el.clientWidth"
            );
            Assert.IsFalse(resumeOverflows, "Resume button content must not overflow.");

            // 4. Queue a job with a very long title
            await Page.Locator("button.tab-pill", new() { HasText = "Paste Raw Text" })
                .ClickAsync();
            await Page.Locator("input[formcontrolname='titleHint']").FillAsync(longTitle);
            await Page.Locator("textarea[formcontrolname='rawText']").FillAsync("1 salmon fillet");
            await Page.Locator("button[aria-label='Queue raw text recipe']").ClickAsync();

            var jobCard = Page.Locator(".job-card", new() { HasText = "Sweet and Saucy" });
            await WaitForLocatorWithRefreshAsync(jobCard);

            // 5. Card content stays within the card and the page does not scroll horizontally
            var cardOverflow = await jobCard.EvaluateAsync<double>(
                @"card => {
                const right = card.getBoundingClientRect().right;
                return Math.max(...[...card.querySelectorAll('.job-title, .job-detail-text, .job-badges, .job-actions')]
                    .map(el => el.getBoundingClientRect().right - right));
            }"
            );
            Assert.IsLessThanOrEqualTo(
                0.5,
                cardOverflow,
                "Job card content must not overflow the card."
            );
            var pageOverflows = await Page.EvaluateAsync<bool>(
                "() => document.documentElement.scrollWidth > document.documentElement.clientWidth"
            );
            Assert.IsFalse(pageOverflows, "Ingestion page must not scroll horizontally at 360px.");

            // 6. Long status detail can be expanded to reveal the full text
            var detail = jobCard.Locator(".job-detail-text");
            var toggleDetail = jobCard.Locator(".detail-toggle-btn");
            await Expect(toggleDetail).ToContainTextAsync("Show full details");
            var collapsedHeight = (await detail.BoundingBoxAsync())!.Height;

            await toggleDetail.ClickAsync();
            await Expect(toggleDetail).ToContainTextAsync("Show less");
            await Expect(detail).ToHaveClassAsync(new Regex("expanded"));
            var expandedHeight = (await detail.BoundingBoxAsync())!.Height;
            Assert.IsGreaterThan(collapsedHeight, expandedHeight);

            await toggleDetail.ClickAsync();
            await Expect(toggleDetail).ToContainTextAsync("Show full details");
        }
        finally
        {
            await Page.UnrouteAllAsync();
            if ((await toggleBtn.InnerTextAsync()).Contains("Resume Ingestion"))
            {
                await toggleBtn.ClickAsync();
                await Expect(toggleBtn).ToContainTextAsync("Pause Ingestion");
            }
        }
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
        if (await thinkingToggle.GetAttributeAsync("aria-checked") != "true")
        {
            await thinkingToggle.ClickAsync();
        }

        // Check retries input
        var retriesInput = dialog.Locator("input[formcontrolname='maxIngestionRetries']");
        await Expect(retriesInput).ToBeVisibleAsync();
        await retriesInput.FillAsync("4");

        // Check enable ingestion toggle
        var ingestionToggle = dialog.Locator(
            "mat-slide-toggle[formcontrolname='enableIngestion'] button"
        );
        await Expect(ingestionToggle).ToBeVisibleAsync();

        // Save
        await dialog.Locator("button", new() { HasText = "Save Settings" }).ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();

        // Re-open to verify persisted values
        await Page.Locator(".llm-settings-btn").ClickAsync();
        await Expect(dialog).ToBeVisibleAsync();

        var savedRetries = dialog.Locator("input[formcontrolname='maxIngestionRetries']");
        await Expect(savedRetries).ToHaveValueAsync("4");
        await Expect(dialog.Locator("mat-slide-toggle[formcontrolname='enableThinking'] button"))
            .ToHaveAttributeAsync("aria-checked", "true");
        await dialog.Locator("button", new() { HasText = "Cancel" }).ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task LlmSettingsDialog_ContextWindowCanBeAdjustedAndPersists()
    {
        await RegisterAndLoginAsync();
        await Page.GotoAsync($"{ServerUrl}/ingest");
        await Page.Locator(".llm-settings-btn").ClickAsync();

        var dialog = Page.Locator("app-llm-settings-dialog");
        await Expect(dialog).ToBeVisibleAsync();

        var contextInput = dialog.Locator("input[formcontrolname='contextWindowTokens']");
        await contextInput.FillAsync("16384");
        await dialog
            .Locator("button", new() { HasText = "Save Settings" })
            .ClickAsync(new() { Force = true });
        await Expect(dialog).Not.ToBeVisibleAsync();

        await Page.Locator(".llm-settings-btn").ClickAsync();
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(contextInput).ToHaveValueAsync("16384");
        await Expect(dialog.Locator("#contextWindowSlider")).ToHaveValueAsync("16384");
        await dialog.Locator("button", new() { HasText = "Cancel" }).ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task LlmSettingsDialog_LongTimeoutPersistsAndFitsMobile()
    {
        await RegisterAndLoginAsync();
        await Page.SetViewportSizeAsync(360, 740);
        await Page.GotoAsync($"{ServerUrl}/ingest");
        await Page.Locator(".llm-settings-btn").ClickAsync();

        var dialog = Page.Locator("app-llm-settings-dialog");
        var timeout = dialog.Locator("input[formcontrolname='requestTimeoutSeconds']");
        var save = dialog.Locator("button", new() { HasText = "Save Settings" });
        await Expect(dialog.Locator("input[formcontrolname='extractionRepairRounds']"))
            .ToHaveCountAsync(0);

        await timeout.FillAsync("1801");
        await Expect(save).ToBeDisabledAsync();
        await timeout.FillAsync("1800");
        await Expect(save).ToBeEnabledAsync();

        await timeout.ScrollIntoViewIfNeededAsync();
        Assert.IsTrue(
            await timeout.EvaluateAsync<bool>(
                """
                input => {
                  const field = input.closest('mat-form-field').getBoundingClientRect();
                  const content = input.closest('mat-dialog-content').getBoundingClientRect();
                  return field.left >= content.left && field.right <= content.right;
                }
                """
            ),
            "Request Timeout must fit inside the dialog at 360px."
        );
        Assert.IsTrue(
            await Page.EvaluateAsync<bool>(
                "document.documentElement.scrollWidth <= window.innerWidth"
            ),
            "LLM settings must fit a 360px viewport."
        );

        await save.ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();
        await Page.Locator(".llm-settings-btn").ClickAsync();
        await Expect(timeout).ToHaveValueAsync("1800");
        await dialog.Locator("button", new() { HasText = "Cancel" }).ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task LlmSettingsDialog_SamplingOverridesPersistReachOllamaAndFitMobile()
    {
        await using var mockServer = await MockLanLlmAndBlogServer.StartAsync();
        await RegisterAndLoginAsync();
        await Page.GotoAsync($"{ServerUrl}/ingest");
        await Page.Locator(".llm-settings-btn").ClickAsync();

        var dialog = Page.Locator("app-llm-settings-dialog");
        var temperature = dialog.Locator("input[formcontrolname='temperature']");
        var topP = dialog.Locator("input[formcontrolname='topP']");
        var topK = dialog.Locator("input[formcontrolname='topK']");
        var save = dialog.Locator("button", new() { HasText = "Save Settings" });
        await dialog.Locator("input[formcontrolname='baseUrl']").FillAsync(mockServer.BaseUrl);
        await Expect(temperature).ToHaveValueAsync("");
        await temperature.FillAsync("1");
        await topP.FillAsync("0.95");
        await topK.FillAsync("12.5");
        await Expect(save).ToBeDisabledAsync();
        await topK.FillAsync("64");
        await dialog.Locator("input[formcontrolname='textModel']").FillAsync("qwen2.5:14b");
        await Page.Keyboard.PressAsync("Tab");

        await Page.SetViewportSizeAsync(360, 740);
        var sampling = dialog.Locator(".sampling-section");
        await sampling.ScrollIntoViewIfNeededAsync();
        Assert.IsTrue(
            await sampling.EvaluateAsync<bool>(
                """
                section => {
                  const bounds = section.getBoundingClientRect();
                  const inputs = [...section.querySelectorAll('input')].map(i => i.getBoundingClientRect());
                  return section.scrollWidth <= section.clientWidth
                    && inputs.every(r => r.left >= bounds.left && r.right <= bounds.right)
                    && inputs.every((r, i) => i === 0 || r.top >= inputs[i - 1].bottom);
                }
                """
            ),
            "Sampling fields must stack inside the dialog at 360px."
        );
        Assert.IsTrue(
            await Page.EvaluateAsync<bool>(
                "document.documentElement.scrollWidth <= window.innerWidth"
            ),
            "LLM settings must fit a 360px viewport."
        );
        await save.ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();
        await Page.SetViewportSizeAsync(1280, 720);

        await Page.Locator(".llm-settings-btn").ClickAsync();
        await Expect(temperature).ToHaveValueAsync("1");
        await Expect(topP).ToHaveValueAsync("0.95");
        await Expect(topK).ToHaveValueAsync("64");
        await dialog.Locator("button", new() { HasText = "Cancel" }).ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();

        await Page.Locator("button.tab-pill", new() { HasText = "Paste Raw Text" }).ClickAsync();
        await Page.Locator("textarea[formcontrolname='rawText']").FillAsync(TofuSourceText);
        await Page.Locator("button[aria-label='Queue raw text recipe']").ClickAsync();
        await WaitForLocatorWithRefreshAsync(Page.Locator("a.review-action-btn").First);
        Assert.AreEqual(1.0, mockServer.LastTemperature);
        Assert.AreEqual(0.95, mockServer.LastTopP);
        Assert.AreEqual(64, mockServer.LastTopK);

        await Page.Locator(".llm-settings-btn").ClickAsync();
        await Expect(temperature).ToHaveValueAsync("1");
        await temperature.FillAsync("");
        await topP.FillAsync("");
        await topK.FillAsync("");
        await save.ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();
        await Page.Locator(".llm-settings-btn").ClickAsync();
        await Expect(dialog.Locator("input[formcontrolname='baseUrl']"))
            .ToHaveValueAsync(mockServer.BaseUrl);
        await Expect(temperature).ToHaveValueAsync("");
        await Expect(topP).ToHaveValueAsync("");
        await Expect(topK).ToHaveValueAsync("");
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
        await SaveLlmSettingsDialogAsync(dialog);
        await Expect(dialog).Not.ToBeVisibleAsync();

        // 2. Submit a raw text recipe
        await Page.Locator("button.tab-pill", new() { HasText = "Paste Raw Text" }).ClickAsync();
        await Page.Locator("textarea[formcontrolname='rawText']").FillAsync(TofuSourceText);
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
            .ToContainTextAsync("oil and sauté");

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

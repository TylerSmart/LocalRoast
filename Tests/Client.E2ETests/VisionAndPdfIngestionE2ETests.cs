using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace App.Client.E2ETests;

public sealed class MockMultimodalServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    public string BaseUrl { get; }
    public bool SimulateOffline { get; set; }

    private MockMultimodalServer(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public static async Task<MockMultimodalServer> StartAsync()
    {
        var port = GetFreePort();
        var baseUrl = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));

        var app = builder.Build();
        var server = new MockMultimodalServer(app, baseUrl);

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
            async (HttpContext context) =>
            {
                if (server.SimulateOffline)
                {
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }

                using var reader = new StreamReader(context.Request.Body);
                var requestBody = await reader.ReadToEndAsync();

                var isVision = requestBody.Contains("\"images\":");

                var recipeJson = isVision
                    ? """
                        {
                          "title": "Rustic Blueberry Scones",
                          "description": "Golden scones studded with fresh blueberries.",
                          "baseServings": 8,
                          "yieldDisplay": "8 scones",
                          "prepTimeMinutes": 20,
                          "cookTimeMinutes": 15,
                          "totalTimeMinutes": 35,
                          "difficulty": "Easy",
                          "sourceName": "Cookbook Scan",
                          "sourceAuthor": "Chef Beatrice",
                          "sourceBookTitle": "Baking with Heritage",
                          "sourceBookPage": 74,
                          "confidenceScore": 0.94,
                          "ingredients": [
                            {
                              "sortOrder": 0,
                              "displayName": "All-Purpose Flour",
                              "quantity": 2,
                              "unit": "Cup",
                              "canonicalFoodName": "All-Purpose Flour",
                              "suggestedAisleName": "Spices & Baking",
                              "isPantryStaple": true,
                              "originalRawText": "2 cups all-purpose flour"
                            },
                            {
                              "sortOrder": 1,
                              "displayName": "Fresh Blueberries",
                              "quantity": 1,
                              "unit": "Cup",
                              "canonicalFoodName": "Blueberries",
                              "suggestedAisleName": "Produce",
                              "isPantryStaple": false,
                              "originalRawText": "1 cup fresh blueberries"
                            }
                          ],
                          "steps": [
                            {
                              "stepNumber": 1,
                              "instruction": "Whisk flour and gently fold in fresh blueberries.",
                              "ingredientIndices": [0, 1]
                            }
                          ]
                        }
                        """
                    : """
                        {
                          "title": "Artisan French Loaf",
                          "description": "Crisp crust and airy crumb.",
                          "baseServings": 6,
                          "yieldDisplay": "1 loaf",
                          "prepTimeMinutes": 30,
                          "cookTimeMinutes": 25,
                          "totalTimeMinutes": 55,
                          "difficulty": "Medium",
                          "sourceName": "PDF Document",
                          "sourceAuthor": "Baker Jacques",
                          "sourceBookTitle": "Bread Arts",
                          "sourceBookPage": 12,
                          "confidenceScore": 0.92,
                          "ingredients": [
                            {
                              "sortOrder": 0,
                              "displayName": "Bread Flour",
                              "quantity": 500,
                              "unit": "G",
                              "canonicalFoodName": "Bread Flour",
                              "suggestedAisleName": "Spices & Baking",
                              "isPantryStaple": true,
                              "originalRawText": "500g bread flour"
                            }
                          ],
                          "steps": [
                            {
                              "stepNumber": 1,
                              "instruction": "Knead dough until smooth and elastic.",
                              "ingredientIndices": [0]
                            }
                          ]
                        }
                        """;

                return Results.Ok(
                    new { message = new { role = "assistant", content = recipeJson } }
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
public sealed class VisionAndPdfIngestionE2ETests : E2ETestBase
{
    private static string CreateSampleImageFile(string fileName, int width = 600, int height = 800)
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "LocalRoast_E2E_" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        using var image = new Image<Rgba32>(width, height);
        image.SaveAsPng(path);
        return path;
    }

    private static string CreateSamplePdfFile(
        string fileName,
        string text =
            "Sample Recipe Book with artisan sourdough loaf ingredients and instructions for baking crisp crust bread."
    )
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "LocalRoast_E2E_" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        var font = builder.AddStandard14Font(
            UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica
        );
        page.AddText(text, 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);
        var pdfBytes = builder.Build();
        File.WriteAllBytes(path, pdfBytes);
        return path;
    }

    private async Task ConfigureLlmSettingsAsync(string baseUrl)
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
        await textModelInput.FillAsync("qwen2.5:14b");

        var visionModelInput = dialog.Locator("input[formcontrolname='visionModel']");
        await visionModelInput.FillAsync("qwen2.5vl:7b");

        await Page.Keyboard.PressAsync("Escape");

        await dialog
            .Locator("button", new() { HasText = "Save Settings" })
            .ClickAsync(new() { Force = true });
        await Expect(dialog).Not.ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task MultiImageIngestion_UploadReviewPromoteCoverAndRetainAttachments_CreatesRecipe()
    {
        await using var mockServer = await MockMultimodalServer.StartAsync();

        await RegisterAndLoginAsync();
        await ConfigureLlmSettingsAsync(mockServer.BaseUrl);

        // 1. Navigate to /ingest and select Photos/Camera tab
        await Page.Locator(".desktop-nav a[routerlink='/ingest']").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/ingest$"));

        await Page.Locator("button.tab-pill", new() { HasText = "Photos / Camera Scan" })
            .ClickAsync();

        // Fill book metadata
        await Page.Locator("input[formcontrolname='sourceBookTitle']")
            .FillAsync("Baking with Heritage");
        await Page.Locator("input[formcontrolname='sourceBookPage']").FillAsync("74");

        // Upload 2 sample images
        var img1 = CreateSampleImageFile("scone-page1.png", 600, 800);
        var img2 = CreateSampleImageFile("scone-page2.png", 600, 800);

        var fileChooser = await Page.RunAndWaitForFileChooserAsync(async () =>
        {
            await Page.Locator("button", new() { HasText = "Select Photos" }).ClickAsync();
        });
        await fileChooser.SetFilesAsync(new[] { img1, img2 });

        // Verify previews strip shows 2 pages
        await Expect(Page.Locator(".selected-previews-strip .preview-card")).ToHaveCountAsync(2);

        // Submit
        await Page.Locator("button", new() { HasText = "Extract Recipe from 2 Photo(s)" })
            .ClickAsync();

        // 2. Wait for job to process and click Review Side-by-Side
        var jobRow = Page.Locator(".job-card", new() { HasText = "Rustic Blueberry Scones" });
        var reviewBtn = jobRow.Locator(
            "a.review-action-btn",
            new() { HasText = "Review Side-by-Side" }
        );
        await Expect(reviewBtn).ToBeVisibleAsync(new() { Timeout = 20000 });
        await reviewBtn.ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex(".*/ingest/review/.*"));

        // 3. Verify Source Viewer in left pane
        var sourceViewer = Page.Locator("app-source-viewer");
        await Expect(sourceViewer).ToBeVisibleAsync(new() { Timeout = 15000 });

        // Verify thumbnails in source viewer
        var thumbStrip = sourceViewer.Locator(".thumbnails-strip .thumbnail-item");
        await Expect(thumbStrip).ToHaveCountAsync(2);

        // Select page 2 in carousel
        await thumbStrip.Nth(1).ClickAsync();

        // Promote page 2 to cover
        var promoteBtn = sourceViewer.Locator("button.promote-cover-btn");
        if (await promoteBtn.IsVisibleAsync())
        {
            await promoteBtn.ClickAsync();
            await Expect(sourceViewer.Locator(".cover-selected-btn")).ToBeVisibleAsync();
        }

        // Verify right pane has Book Title and Page
        var bookTitleInput = Page.Locator("input[formcontrolname='sourceBookTitle']");
        await Expect(bookTitleInput).ToHaveValueAsync("Baking with Heritage");

        // Verify retain attachments checkbox is checked
        var retainCheckbox = Page.Locator(
            "mat-checkbox[formcontrolname='retainSourceAttachments'] input"
        );
        await Expect(retainCheckbox).ToBeCheckedAsync();

        // 4. Approve & Publish
        await Page.Locator("button", new() { HasText = "Approve & Publish" }).ClickAsync();

        // 5. Lands on Recipe Detail page
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+$"));

        // Verify recipe title
        await Expect(Page.Locator(".recipe-title")).ToContainTextAsync("Rustic Blueberry Scones");

        // Verify attachments card exists with 2 attachments
        var attachmentsCard = Page.Locator(".attachments-section .attachment-item-card");
        await Expect(attachmentsCard).ToHaveCountAsync(2);

        // Click first attachment to open viewer dialog
        await attachmentsCard.First.ClickAsync();
        var dialog = Page.Locator("app-recipe-attachment-viewer-dialog");
        await Expect(dialog).ToBeVisibleAsync();

        // Close dialog
        await dialog.Locator("button[mattooltip='Close Viewer']").ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task OfflineVisionHost_ShowsWaitingBannerAndRecoversOnRetry()
    {
        await using var mockServer = await MockMultimodalServer.StartAsync();

        await RegisterAndLoginAsync();
        await ConfigureLlmSettingsAsync(mockServer.BaseUrl);

        // Turn offline before queueing
        mockServer.SimulateOffline = true;

        await Page.Locator(".desktop-nav a[routerlink='/ingest']").ClickAsync();
        await Page.Locator("button.tab-pill", new() { HasText = "Photos / Camera Scan" })
            .ClickAsync();

        var img = CreateSampleImageFile("offline-page.png", 600, 800);
        var fileChooser = await Page.RunAndWaitForFileChooserAsync(async () =>
        {
            await Page.Locator("button", new() { HasText = "Select Photos" }).ClickAsync();
        });
        await fileChooser.SetFilesAsync(img);

        await Page.Locator("button", new() { HasText = "Extract Recipe from 1 Photo(s)" })
            .ClickAsync();

        // Expect waiting host banner to appear
        var waitingBanner = Page.Locator(".waiting-host-banner");
        await Expect(waitingBanner).ToBeVisibleAsync(new() { Timeout = 15000 });
        await Expect(waitingBanner).ToContainTextAsync("waiting for local LLM host");

        // Bring mock server back online
        mockServer.SimulateOffline = false;

        // Click Retry All Waiting Jobs
        await waitingBanner
            .Locator("button", new() { HasText = "Retry All Waiting Jobs" })
            .ClickAsync();

        // Should recover and become Ready for Review
        var jobRow = Page.Locator(".job-card", new() { HasText = "Rustic Blueberry Scones" }).First;
        var reviewBtn = jobRow.Locator(
            "a.review-action-btn",
            new() { HasText = "Review Side-by-Side" }
        );
        await Expect(reviewBtn).ToBeVisibleAsync(new() { Timeout = 20000 });
    }

    [TestMethod]
    public async Task MobileViewport_CameraScanAndSplitReviewPanes_OperateSmoothly()
    {
        await using var mockServer = await MockMultimodalServer.StartAsync();

        await RegisterAndLoginAsync();
        await ConfigureLlmSettingsAsync(mockServer.BaseUrl);

        // Set mobile viewport 360x740
        await Page.SetViewportSizeAsync(360, 740);

        await Page.Locator(".mobile-bottom-nav a[routerlink='/ingest']").ClickAsync();
        await Page.Locator("button.tab-pill", new() { HasText = "Photos / Camera Scan" })
            .ClickAsync();

        // Camera button is visible on mobile
        var cameraBtn = Page.Locator("button.camera-btn");
        await Expect(cameraBtn).ToBeVisibleAsync();

        var img = CreateSampleImageFile("mobile-scan.png", 400, 600);
        var fileChooser = await Page.RunAndWaitForFileChooserAsync(async () =>
        {
            await cameraBtn.ClickAsync();
        });
        await fileChooser.SetFilesAsync(img);

        await Page.Locator("button", new() { HasText = "Extract Recipe from 1 Photo(s)" })
            .ClickAsync();

        var jobRow = Page.Locator(".job-card", new() { HasText = "Rustic Blueberry Scones" }).First;
        var reviewBtn = jobRow.Locator(
            "a.review-action-btn",
            new() { HasText = "Review Side-by-Side" }
        );
        await Expect(reviewBtn).ToBeVisibleAsync(new() { Timeout = 20000 });
        await reviewBtn.ClickAsync();

        // On mobile review page, verify mobile pane switcher
        var switcher = Page.Locator(".mobile-pane-switcher");
        await Expect(switcher).ToBeVisibleAsync();

        // Toggle to Original Source pane
        await switcher
            .Locator("button.switcher-pill", new() { HasText = "Original Source" })
            .ClickAsync();
        await Expect(Page.Locator(".source-inspector-pane")).ToBeVisibleAsync();

        // Toggle to Structured Draft pane
        await switcher
            .Locator("button.switcher-pill", new() { HasText = "Structured Draft" })
            .ClickAsync();
        await Expect(Page.Locator(".draft-editor-pane")).ToBeVisibleAsync();
    }
}

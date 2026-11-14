using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace App.Client.E2ETests;

[TestClass]
public sealed class ClientSmokeTests : E2ETestBase
{
    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task CompleteE2EFlow_Register_CreateCatalogItem_CreateRecipe_ScaleAndConvert_MobileNav()
    {
        // 1. Register and verify authenticated landing on recipes
        var (username, _) = await RegisterAndLoginAsync();
        await Expect(Page.Locator(".brand-title")).ToContainTextAsync("LocalRoast");
        await Expect(Page.Locator(".user-name")).ToContainTextAsync(username);

        // 2. Navigate to Food Catalog and create a food item
        await Page.Locator("nav.desktop-nav a", new() { HasText = "Food Catalog" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/catalog.*"));
        await Expect(Page.Locator(".page-title")).ToContainTextAsync("Canonical Food Catalog");

        // Click "Add Food Item"
        await Page.Locator("button", new() { HasText = "Add Food Item" }).ClickAsync();
        await Expect(Page.Locator("mat-dialog-container h2")).ToContainTextAsync("New Food Item");

        var testFoodItemName = $"San Marzano Tomatoes {Guid.NewGuid():N}"[..25];
        await Page.Locator("input[formcontrolname='name']").FillAsync(testFoodItemName);
        await Page.Locator("input[formcontrolname='aliasesText']")
            .FillAsync("Canned Plum Tomatoes, Pelati");
        await Page.Locator("mat-checkbox[formcontrolname='isPantryStaple'] label").ClickAsync();

        await Page.Locator("mat-dialog-actions button", new() { HasText = "Create Item" })
            .ClickAsync();

        // Verify item card appears in catalog
        await Expect(Page.Locator(".food-item-card", new() { HasText = testFoodItemName }))
            .ToBeVisibleAsync();

        // 3. Navigate to Create Recipe
        await Page.Locator("nav.desktop-nav a", new() { HasText = "Cookbook" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes.*"));

        await Page.Locator("a[routerlink='/recipes/new']").First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/new.*"));
        await Expect(Page.Locator(".page-title")).ToContainTextAsync("New Recipe");

        var recipeTitle = "Classic Marinara Sauce";
        await Page.Locator("input[formcontrolname='title']").FillAsync(recipeTitle);
        await Page.Locator("input[formcontrolname='baseServings']").FillAsync("4");
        await Page.Locator("input[formcontrolname='prepTimeMinutes']").FillAsync("10");
        await Page.Locator("input[formcontrolname='cookTimeMinutes']").FillAsync("30");
        await Page.Locator("textarea[formcontrolname='description']")
            .FillAsync("Simple rich tomato sauce");

        // Fill ingredient
        await Page.Locator("input[formcontrolname='displayName']")
            .First.FillAsync(testFoodItemName);
        await Page.Locator("input[formcontrolname='quantity']").First.FillAsync("2");

        // Select Unit: Cup
        await Page.Locator("mat-select[formcontrolname='unit']").First.ClickAsync();
        await Page.Locator("mat-option", new() { HasText = "Cup (cup)" }).ClickAsync();

        // Fill Step Instruction
        await Page.Locator("[formcontrolname='instruction'] .step-instruction-editable")
            .First.FillAsync("Simmer tomatoes over medium heat until thickened.");

        // Add Step Timer
        await Page.Locator(".add-timer-btn").First.ClickAsync();
        var timerDialog = Page.Locator("app-step-token-dialog");
        await Expect(timerDialog).ToBeVisibleAsync();
        await timerDialog.Locator("input[formcontrolname='minutes']").FillAsync("20");
        await timerDialog.Locator("input[formcontrolname='label']").FillAsync("Simmer gently");
        await timerDialog.Locator("button", new() { HasText = "Insert Token" }).ClickAsync();
        await Expect(timerDialog).ToHaveCountAsync(0);

        // Submit recipe
        await Page.Locator("button[type='submit']", new() { HasText = "Save Recipe" })
            .First.ClickAsync();

        // 4. Verify Recipe Detail Page
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+$"));
        await Expect(Page.Locator(".recipe-title")).ToContainTextAsync(recipeTitle);
        await Expect(Page.Locator(".ingredient-row")).ToContainTextAsync("2 cups");
        await Expect(Page.Locator(".ingredient-row")).ToContainTextAsync(testFoodItemName);
        await Expect(Page.Locator(".timer-badge")).ToContainTextAsync("20 min");

        // 5. Test Live 2x Scaling
        await Page.Locator("button.mult-chip", new() { HasText = "2x" }).ClickAsync();
        await Expect(Page.Locator(".ingredient-row")).ToContainTextAsync("4 cups");

        // 6. Test Metric Unit Switching
        await Page.Locator("mat-button-toggle", new() { HasText = "Metric" }).ClickAsync();
        await Expect(Page.Locator(".ingredient-row")).ToContainTextAsync("946 ml");

        // 7. Verify 360px Mobile Viewport and Bottom Navigation Bar
        await Page.SetViewportSizeAsync(360, 640);
        await Expect(Page.Locator("nav.mobile-bottom-nav")).ToBeVisibleAsync();
        await Expect(Page.Locator("nav.mobile-bottom-nav a", new() { HasText = "Cookbook" }))
            .ToBeVisibleAsync();
        await Expect(Page.Locator("nav.mobile-bottom-nav a", new() { HasText = "Catalog" }))
            .ToBeVisibleAsync();
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task FailedTestArtifactCaptureAndTimeoutConfig_SavesVideoScreenshotDomConsoleNetworkTraceAndApiLogs()
    {
        // 1. Verify every [TestMethod] in the E2E assembly has a maximum non-cooperative [Timeout] configured
        var testMethods = typeof(E2ETestBase)
            .Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<TestClassAttribute>() is not null)
            .SelectMany(t =>
                t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.GetCustomAttribute<TestMethodAttribute>() is not null)
            )
            .ToList();

        Assert.IsNotEmpty(testMethods);

        foreach (var method in testMethods)
        {
            var timeoutAttr = method.GetCustomAttribute<TimeoutAttribute>();
            Assert.IsNotNull(
                timeoutAttr,
                $"E2E test method '{method.DeclaringType?.Name}.{method.Name}' must have a [Timeout] attribute."
            );
            Assert.IsGreaterThan(0, timeoutAttr.Timeout);
            Assert.IsLessThanOrEqualTo(
                DefaultTestTimeoutMs,
                timeoutAttr.Timeout,
                $"E2E test method '{method.DeclaringType?.Name}.{method.Name}' exceeds maximum allowed timeout ({DefaultTestTimeoutMs}ms)."
            );
            Assert.IsFalse(
                timeoutAttr.CooperativeCancellation,
                $"E2E test method '{method.DeclaringType?.Name}.{method.Name}' must use hard timeout (CooperativeCancellation = false) so hung Playwright calls abort immediately."
            );
        }

        // 2. Navigate to login page and emit console/network activity, then verify artifact capture
        await Page.GotoAsync($"{ServerUrl}/login");
        await Expect(Page.Locator(".mat-mdc-tab", new() { HasText = "Create Account" }))
            .ToBeVisibleAsync();
        await Page.EvaluateAsync("() => console.warn('Diagnostic warning from E2E verification')");

        var artifactDir = await SaveDebugArtifactsAsync("Verification test", closeContexts: true);
        try
        {
            Assert.IsTrue(Directory.Exists(artifactDir), "Artifact directory should be created.");

            var expectedFiles = new[]
            {
                "screenshot.png",
                "video.webm",
                "page.html",
                "page-data.json",
                "console.log",
                "network-errors.log",
                "trace.zip",
                "api-server.log",
            };

            foreach (var file in expectedFiles)
            {
                var fullPath = Path.Combine(artifactDir, file);
                Assert.IsTrue(
                    File.Exists(fullPath),
                    $"Expected artifact '{file}' to exist at {fullPath}."
                );
                Assert.IsGreaterThan(
                    0L,
                    new FileInfo(fullPath).Length,
                    $"Expected artifact '{file}' to be non-empty."
                );
            }

            var consoleLog = await File.ReadAllTextAsync(Path.Combine(artifactDir, "console.log"));
            Assert.Contains("Diagnostic warning from E2E verification", consoleLog);

            var pageDataJson = await File.ReadAllTextAsync(
                Path.Combine(artifactDir, "page-data.json")
            );
            Assert.Contains("/login", pageDataJson);
        }
        finally
        {
            if (Directory.Exists(artifactDir))
            {
                Directory.Delete(artifactDir, recursive: true);
            }

            var parentRunDir = Directory.GetParent(artifactDir)?.FullName;
            if (
                !string.IsNullOrEmpty(parentRunDir)
                && Directory.Exists(parentRunDir)
                && Directory.GetFileSystemEntries(parentRunDir).Length == 0
            )
            {
                Directory.Delete(parentRunDir);
            }
        }
    }
}

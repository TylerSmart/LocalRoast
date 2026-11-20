using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace App.Client.E2ETests;

[TestClass]
public sealed class PwaE2ETests : E2ETestBase
{
    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task PwaManifestAndServiceWorkerAssets_ServeWithExpectedMimeTypesAndHeaders()
    {
        await Page.GotoAsync($"{ServerUrl}/login");

        // Verify PWA metadata tags in HTML
        var manifestHref = await Page.Locator("link[rel='manifest']").GetAttributeAsync("href");
        Assert.AreEqual("site.webmanifest", manifestHref);
        Assert.AreEqual(
            "yes",
            await Page.Locator("meta[name='apple-mobile-web-app-capable']")
                .GetAttributeAsync("content")
        );
        Assert.AreEqual(
            "LocalRoast",
            await Page.Locator("meta[name='apple-mobile-web-app-title']")
                .GetAttributeAsync("content")
        );

        // Verify manifest content type, Cache-Control, icons (any/maskable/monochrome), shortcuts, and share_target
        var manifestResponse = await Page.APIRequest.GetAsync($"{ServerUrl}/site.webmanifest");
        Assert.AreEqual(200, manifestResponse.Status);
        StringAssert.Contains(
            manifestResponse.Headers["content-type"],
            "application/manifest+json"
        );
        AssertNoCache(manifestResponse, "/site.webmanifest");

        using var manifestDoc = JsonDocument.Parse(await manifestResponse.TextAsync());
        var root = manifestDoc.RootElement;
        Assert.AreEqual("/", root.GetProperty("id").GetString());
        Assert.AreEqual("LocalRoast", root.GetProperty("name").GetString());
        Assert.AreEqual("standalone", root.GetProperty("display").GetString());

        var icons = root.GetProperty("icons").EnumerateArray().ToList();
        var purposes = icons.Select(i => i.GetProperty("purpose").GetString()).ToHashSet();
        Assert.Contains("any", purposes, "Manifest must declare 'any' icons.");
        Assert.Contains("maskable", purposes, "Manifest must declare dedicated 'maskable' icons.");
        Assert.Contains("monochrome", purposes, "Manifest must declare 'monochrome' icons.");
        Assert.DoesNotContain(
            "any maskable",
            purposes,
            "Manifest should not combine 'any maskable' into a single purpose string."
        );

        foreach (var icon in icons)
        {
            var src = icon.GetProperty("src").GetString()!;
            var iconResponse = await Page.APIRequest.GetAsync($"{ServerUrl}/{src.TrimStart('/')}");
            Assert.AreEqual(200, iconResponse.Status, $"Icon '{src}' failed to load.");
            var bytes = await iconResponse.BodyAsync();
            Assert.IsGreaterThan(0, bytes.Length, $"Icon '{src}' was empty.");
        }

        var shortcuts = root.GetProperty("shortcuts").EnumerateArray().ToList();
        Assert.HasCount(4, shortcuts);
        Assert.AreEqual(
            "/ingest",
            root.GetProperty("share_target").GetProperty("action").GetString()
        );

        // Verify Angular Service Worker control files are served with no-cache
        foreach (var swPath in new[] { "/ngsw.json", "/ngsw-worker.js", "/index.html" })
        {
            var swResp = await Page.APIRequest.GetAsync($"{ServerUrl}{swPath}");
            Assert.AreEqual(200, swResp.Status, $"Expected 200 for {swPath}");
            AssertNoCache(swResp, swPath);
        }
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task ServiceWorker_RegistersAndServesCachedRecipeOfflineWithoutSigningOut()
    {
        var (username, _) = await RegisterAndLoginAsync();

        // Wait for the Angular Service Worker to register and take control of the page
        await WaitForServiceWorkerControlAsync();

        // Create and view a recipe while online so the SW caches the recipe read model
        var recipeTitle = $"Offline Sourdough {Guid.NewGuid():N}"[..24];
        var recipeId = await CreateRecipeWithIngredientViaApiAsync(recipeTitle, "Bread Flour");
        await Page.GotoAsync($"{ServerUrl}/recipes/{recipeId}");
        await Expect(Page.Locator("h1")).ToContainTextAsync(recipeTitle);
        await Expect(Page.Locator("body")).ToContainTextAsync("Bread Flour");

        // Switch Chromium into true offline mode and reload the recipe page
        await Context.SetOfflineAsync(true);
        try
        {
            await Page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });

            // App shell and cached recipe details render cleanly offline without losing auth
            await Expect(Page).ToHaveURLAsync(new Regex($".*/recipes/{recipeId}$"));
            await Expect(Page.Locator("h1"))
                .ToContainTextAsync(recipeTitle, new() { Timeout = 15000 });
            await Expect(Page.Locator("body")).ToContainTextAsync("Bread Flour");
            await Expect(Page.Locator(".user-name")).ToContainTextAsync(username);

            // Offline banner stays visible (cached /api/recipes response must NOT clear it)
            var banner = Page.GetByTestId("connection-banner");
            await Expect(banner).ToBeVisibleAsync(new() { Timeout = 15000 });

            // Verify 360px mobile layout has no horizontal overflow while offline
            await Page.SetViewportSizeAsync(360, 640);
            await Expect(banner).ToBeVisibleAsync();
            var hasOverflow = await Page.EvaluateAsync<bool>(
                "() => document.documentElement.scrollWidth > window.innerWidth + 1"
            );
            Assert.IsFalse(hasOverflow, "Offline recipe page overflowed at 360px mobile viewport.");
            await Page.SetViewportSizeAsync(1280, 720);
        }
        finally
        {
            await Context.SetOfflineAsync(false);
        }

        // Bring browser back online and confirm connection banner clears automatically
        await Expect(Page.GetByTestId("connection-banner"))
            .Not.ToBeVisibleAsync(new() { Timeout = 15000 });
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task WebShareTarget_PrePopulatesIngestionHub_AndStripsQueryParams()
    {
        await RegisterAndLoginAsync();

        // 1. Share URL flow (desktop)
        await Page.GotoAsync(
            $"{ServerUrl}/ingest?url=https%3A%2F%2Fexample.com%2Fcrispy-roast-chicken&title=Crispy%20Roast%20Chicken"
        );
        var urlTextarea = Page.Locator("textarea[formcontrolname='urlsText']");
        await Expect(urlTextarea).ToBeVisibleAsync();
        await Expect(urlTextarea).ToHaveValueAsync("https://example.com/crispy-roast-chicken");
        await Expect(Page.Locator("body")).ToContainTextAsync("Shared recipe link loaded");
        await Expect(Page).ToHaveURLAsync(new Regex(".*/ingest$"));

        // 2. Share multi-line recipe text flow at 360px mobile viewport
        await Page.SetViewportSizeAsync(360, 640);
        await Page.GotoAsync(
            $"{ServerUrl}/ingest?title=Grandma%20Biscuits&text=Ingredients%3A%0A-%202%20cups%20flour%0A-%201%20tbsp%20baking%20powder%0AInstructions%3A%0ABake%20at%20425F."
        );
        var titleHintInput = Page.Locator("input[formcontrolname='titleHint']");
        var rawTextArea = Page.Locator("textarea[formcontrolname='rawText']");
        await Expect(rawTextArea).ToBeVisibleAsync();
        await Expect(titleHintInput).ToHaveValueAsync("Grandma Biscuits");
        await Expect(rawTextArea).ToHaveValueAsync(new Regex("2 cups flour"));
        await Expect(Page).ToHaveURLAsync(new Regex(".*/ingest$"));
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task InstallMenuAction_AppearsOnBeforeInstallPrompt_TriggersPrompt_AndHidesWhenInstalled()
    {
        await RegisterAndLoginAsync();

        // Dispatch synthetic beforeinstallprompt event to verify user menu install entry
        await Page.EvaluateAsync(
            """
            () => {
                window.__pwaPromptCalls = 0;
                const event = new Event('beforeinstallprompt', { cancelable: true });
                event.prompt = () => {
                    window.__pwaPromptCalls += 1;
                    return Promise.resolve();
                };
                event.userChoice = Promise.resolve({ outcome: 'accepted', platform: 'web' });
                window.dispatchEvent(event);
            }
            """
        );

        var userMenuBtn = Page.Locator("button.user-profile-btn");
        await userMenuBtn.ClickAsync();

        var installMenuItem = Page.GetByTestId("pwa-install-menu-item");
        await Expect(installMenuItem).ToBeVisibleAsync();
        await Expect(installMenuItem).ToContainTextAsync("Install LocalRoast");
        await installMenuItem.ClickAsync();

        var promptCalls = await Page.EvaluateAsync<int>("() => window.__pwaPromptCalls");
        Assert.AreEqual(1, promptCalls);

        // Simulate OS appinstalled completion and verify menu item stays hidden
        await Page.EvaluateAsync("() => window.dispatchEvent(new Event('appinstalled'))");
        await userMenuBtn.ClickAsync();
        await Expect(Page.GetByTestId("pwa-install-menu-item")).ToHaveCountAsync(0);
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task ThemeSwitcher_UpdatesMetaThemeColorDynamically()
    {
        await RegisterAndLoginAsync();

        var themeBtn = Page.Locator("button[aria-label*='Theme:']").First;
        await themeBtn.ClickAsync();
        await Page.Locator("button[role='menuitem']", new() { HasText = "Light" }).ClickAsync();
        var lightThemeColor = await Page.Locator("meta[name='theme-color']")
            .GetAttributeAsync("content");
        Assert.AreEqual("#f0edf1", lightThemeColor);

        await themeBtn.ClickAsync();
        await Page.Locator("button[role='menuitem']", new() { HasText = "Dark" }).ClickAsync();
        var darkThemeColor = await Page.Locator("meta[name='theme-color']")
            .GetAttributeAsync("content");
        Assert.AreEqual("#1a1715", darkThemeColor);
    }

    private async Task WaitForServiceWorkerControlAsync()
    {
        var controlled = await Page.EvaluateAsync<bool>(
            """
            async () => {
                if (!('serviceWorker' in navigator)) return false;
                await navigator.serviceWorker.ready;
                if (navigator.serviceWorker.controller) return true;
                return await new Promise((resolve) => {
                    const timeout = setTimeout(() => resolve(Boolean(navigator.serviceWorker.controller)), 10000);
                    navigator.serviceWorker.addEventListener('controllerchange', () => {
                        clearTimeout(timeout);
                        resolve(true);
                    }, { once: true });
                });
            }
            """
        );

        if (!controlled)
        {
            await Page.ReloadAsync();
            controlled = await Page.EvaluateAsync<bool>(
                "async () => { await navigator.serviceWorker.ready; return Boolean(navigator.serviceWorker.controller); }"
            );
        }

        Assert.IsTrue(
            controlled,
            "Expected Angular Service Worker (ngsw-worker.js) to control the page."
        );
    }

    private static void AssertNoCache(IAPIResponse response, string path)
    {
        Assert.IsTrue(
            response.Headers.TryGetValue("cache-control", out var cacheControl),
            $"Expected Cache-Control header for {path}."
        );
        StringAssert.Contains(cacheControl, "no-cache");
        StringAssert.Contains(cacheControl, "must-revalidate");
    }
}

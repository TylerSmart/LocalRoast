using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace App.Client.E2ETests;

[TestClass]
public sealed class AuthSessionResilienceE2ETests : E2ETestBase
{
    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task PersistentSession_HasNoExpClaim_AndSurvivesApiOutageAcrossReloadAndRecovery()
    {
        // 1. Register a fresh user and create a recipe so we can verify real data reloads on recovery
        var (username, _) = await RegisterAndLoginAsync();
        await Expect(Page.Locator(".user-name")).ToContainTextAsync(username);

        // Verify stored token has no JWT exp claim and cached user is stored alongside it
        var storageSnapshot = await Page.EvaluateAsync<string>(
            """
            () => {
                const token = localStorage.getItem('local-roast-token');
                const userRaw = localStorage.getItem('local-roast-user');
                let hasExp = null;
                if (token && token.split('.').length === 3) {
                    const b64 = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
                    const payload = JSON.parse(atob(b64.padEnd(b64.length + ((4 - (b64.length % 4)) % 4), '=')));
                    hasExp = Object.prototype.hasOwnProperty.call(payload, 'exp');
                }
                return JSON.stringify({
                    hasToken: Boolean(token),
                    hasExp,
                    user: userRaw ? JSON.parse(userRaw) : null,
                });
            }
            """
        );
        using (var doc = JsonDocument.Parse(storageSnapshot))
        {
            Assert.IsTrue(doc.RootElement.GetProperty("hasToken").GetBoolean());
            Assert.IsFalse(
                doc.RootElement.GetProperty("hasExp").GetBoolean(),
                "Default session JWT should omit the exp claim so logins remain persistent."
            );
            Assert.AreEqual(
                username,
                doc.RootElement.GetProperty("user").GetProperty("username").GetString()
            );
        }

        // Create a recipe to verify auto-reload once the API comes back online
        await Page.Locator("a[routerlink='/recipes/new']").First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/new.*"));
        var recipeTitle = $"Resilient Roast {Guid.NewGuid():N}"[..24];
        await Page.Locator("input[formcontrolname='title']").FillAsync(recipeTitle);
        await Page.Locator("input[formcontrolname='displayName']").First.FillAsync("Coffee Beans");
        await Page.Locator("input[formcontrolname='quantity']").First.FillAsync("2");
        await Page.Locator("mat-select[formcontrolname='unit']").First.ClickAsync();
        await Page.Locator("mat-option", new() { HasText = "Tablespoon (tbsp)" }).ClickAsync();
        await Page.Locator("[formcontrolname='instruction'] .step-instruction-editable")
            .First.FillAsync("Brew and enjoy while the API restarts.");
        await Page.Locator("button[type='submit']", new() { HasText = "Save Recipe" })
            .First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+$"));

        // Navigate back to cookbook list
        await Page.Locator("nav.desktop-nav a", new() { HasText = "Cookbook" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes$"));
        await Expect(Page.Locator("body")).ToContainTextAsync(recipeTitle);

        // 2. Simulate an API restart/outage (proxy returns 502 without X-Api-Version) during a full reload
        var apiOffline = true;
        await Context.RouteAsync(
            "**/api/**",
            async route =>
            {
                if (apiOffline)
                {
                    await route.FulfillAsync(
                        new RouteFulfillOptions
                        {
                            Status = 502,
                            ContentType = "text/plain",
                            Body = "Bad Gateway",
                        }
                    );
                    return;
                }

                await route.ContinueAsync();
            }
        );

        await Page.ReloadAsync();

        // User must NOT be signed out or redirected to /login
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes$"));
        await Expect(Page.Locator(".user-name")).ToContainTextAsync(username);

        // Reconnection banner should appear cleanly at desktop and 360px mobile viewports
        var banner = Page.GetByTestId("connection-banner");
        await Expect(banner).ToBeVisibleAsync();
        await Expect(banner).ToContainTextAsync("still signed in");

        await Page.SetViewportSizeAsync(360, 640);
        await Expect(banner).ToBeVisibleAsync();
        var hasHorizontalOverflow = await Page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > window.innerWidth + 1"
        );
        Assert.IsFalse(
            hasHorizontalOverflow,
            "Connection banner should not cause horizontal overflow at 360px mobile width."
        );
        await Page.SetViewportSizeAsync(1280, 720);

        // Token must still be preserved in localStorage
        var tokenDuringOutage = await Page.EvaluateAsync<string?>(
            "() => localStorage.getItem('local-roast-token')"
        );
        Assert.IsFalse(string.IsNullOrWhiteSpace(tokenDuringOutage));

        // 3. Bring the API back online and confirm automatic reconnection & view reload without re-login
        apiOffline = false;
        await Expect(banner).Not.ToBeVisibleAsync(new() { Timeout = 10000 });
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes$"));
        await Expect(Page.Locator("body"))
            .ToContainTextAsync(recipeTitle, new() { Timeout = 10000 });
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task InvalidSession_Receiving401OnStartup_ClearsStorageAndRedirectsToLogin()
    {
        await RegisterAndLoginAsync();

        // Replace token with an invalid/tampered token and reload
        await Page.EvaluateAsync(
            "() => localStorage.setItem('local-roast-token', 'tampered.jwt.token')"
        );
        await Page.ReloadAsync();

        await Expect(Page).ToHaveURLAsync(new Regex(".*/login.*"), new() { Timeout = 10000 });

        var remainingToken = await Page.EvaluateAsync<string?>(
            "() => localStorage.getItem('local-roast-token')"
        );
        var remainingUser = await Page.EvaluateAsync<string?>(
            "() => localStorage.getItem('local-roast-user')"
        );
        Assert.IsNull(remainingToken);
        Assert.IsNull(remainingUser);
    }
}

using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace App.Client.E2ETests;

[TestClass]
public sealed class NavigationAndThemeE2ETests : E2ETestBase
{
    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task AppShell_DesktopNavigation_SwitchesRoutes_AndUpdatesActiveLink()
    {
        await RegisterAndLoginAsync();

        // Initially on recipes
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes.*"));
        var cookbookLink = Page.Locator("nav.desktop-nav a", new() { HasText = "Cookbook" });
        await Expect(cookbookLink).ToHaveClassAsync(new Regex(".*active-nav-link.*"));

        // Click Food Catalog
        var catalogLink = Page.Locator("nav.desktop-nav a", new() { HasText = "Food Catalog" });
        await catalogLink.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/catalog.*"));
        await Expect(catalogLink).ToHaveClassAsync(new Regex(".*active-nav-link.*"));

        // Navigate back to Cookbook
        await cookbookLink.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes.*"));
        await Expect(cookbookLink).ToHaveClassAsync(new Regex(".*active-nav-link.*"));
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task AppShell_MobileNavigation_360pxViewport_RendersBottomBar()
    {
        await RegisterAndLoginAsync();

        // Set to 360px mobile viewport
        await Page.SetViewportSizeAsync(360, 640);

        // Verify desktop nav hidden, mobile bottom nav visible
        await Expect(Page.Locator("nav.desktop-nav")).Not.ToBeVisibleAsync();
        var mobileNav = Page.Locator("nav.mobile-bottom-nav");
        await Expect(mobileNav).ToBeVisibleAsync();

        // Tap Catalog in mobile nav
        var catalogTab = mobileNav.Locator("a", new() { HasText = "Catalog" });
        await catalogTab.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/catalog.*"));
        await Expect(Page.Locator(".page-title")).ToContainTextAsync("Canonical Food Catalog");

        // Tap Cookbook in mobile nav
        var cookbookTab = mobileNav.Locator("a", new() { HasText = "Cookbook" });
        await cookbookTab.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes.*"));
        await Expect(Page.Locator(".page-title")).ToContainTextAsync("Cookbook");
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task ThemeAndUserMenu_OpensDialogs_AndLogsOut()
    {
        var (username, _) = await RegisterAndLoginAsync();

        // 1. Open Theme Customizer Dialog
        await Page.Locator("button[aria-label*='Theme:']").First.ClickAsync();
        var customizeBtn = Page.Locator(
            "button[role='menuitem']",
            new() { HasText = "Customize Theme" }
        );
        await customizeBtn.ClickAsync();

        var dialog = Page.Locator("mat-dialog-container");
        await Expect(dialog.Locator("h2")).ToContainTextAsync("Theme & Appearance");
        await dialog.Locator("button", new() { HasText = "Done" }).ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();

        // 2. Open User Menu and Logout
        var userMenuBtn = Page.Locator("button.user-profile-btn");
        await userMenuBtn.ClickAsync();
        var logoutBtn = Page.Locator("button[role='menuitem']", new() { HasText = "Log Out" });
        await logoutBtn.ClickAsync();

        // Verify returned to login page
        await Expect(Page).ToHaveURLAsync(new Regex(".*/login.*"));
        await Expect(Page.Locator("h1")).ToContainTextAsync("Welcome to LocalRoast");
    }
}

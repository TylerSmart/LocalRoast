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
    public async Task AppShell_TabletAndIntermediateViewports_AllNavButtonsFullyVisibleWithoutOverlap()
    {
        await RegisterAndLoginAsync();

        int[] widths = [769, 820, 900, 901, 960, 961, 1024, 1133, 1200, 1280, 1281, 1366];

        foreach (var width in widths)
        {
            await Page.SetViewportSizeAsync(width, 768);

            var desktopNav = Page.Locator("nav.desktop-nav");
            await Expect(desktopNav).ToBeVisibleAsync();

            var metrics = await Page.EvaluateAsync<NavMetrics>(
                @"() => {
                    const brand = document.querySelector('.brand-section').getBoundingClientRect();
                    const nav = document.querySelector('nav.desktop-nav');
                    const navRect = nav.getBoundingClientRect();
                    const actions = document.querySelector('.top-actions').getBoundingClientRect();
                    const links = Array.from(nav.querySelectorAll('a.nav-link'));
                    const firstLink = links[0].getBoundingClientRect();
                    const firstIcon = links[0].querySelector('mat-icon').getBoundingClientRect();
                    const lastLink = links[links.length - 1].getBoundingClientRect();
                    const lastLabel = links[links.length - 1].querySelector('span:not(.mat-mdc-button-persistent-ripple):not(.mat-focus-indicator):not(.mat-mdc-button-touch-target)').getBoundingClientRect();

                    return {
                        navClientWidth: nav.clientWidth,
                        navScrollWidth: nav.scrollWidth,
                        brandRight: brand.right,
                        navLeft: navRect.left,
                        navRight: navRect.right,
                        actionsLeft: actions.left,
                        firstLinkLeft: firstLink.left,
                        firstIconLeft: firstIcon.left,
                        lastLinkRight: lastLink.right,
                        lastLabelRight: lastLabel.right
                    };
                }"
            );

            Assert.IsLessThanOrEqualTo(
                metrics.NavClientWidth + 1,
                metrics.NavScrollWidth,
                $"At {width}px, desktop-nav overflowed (scrollWidth={metrics.NavScrollWidth}, clientWidth={metrics.NavClientWidth})."
            );
            Assert.IsGreaterThanOrEqualTo(
                metrics.BrandRight - 0.5,
                metrics.FirstLinkLeft,
                $"At {width}px, Cookbook button is behind brand title (brandRight={metrics.BrandRight}, firstLinkLeft={metrics.FirstLinkLeft})."
            );
            Assert.IsGreaterThanOrEqualTo(
                metrics.NavLeft - 0.5,
                metrics.FirstIconLeft,
                $"At {width}px, Cookbook icon is clipped on the left (navLeft={metrics.NavLeft}, firstIconLeft={metrics.FirstIconLeft})."
            );
            Assert.IsLessThanOrEqualTo(
                metrics.ActionsLeft + 0.5,
                metrics.LastLinkRight,
                $"At {width}px, Food Catalog button is behind top-actions (lastLinkRight={metrics.LastLinkRight}, actionsLeft={metrics.ActionsLeft})."
            );
            Assert.IsLessThanOrEqualTo(
                metrics.NavRight + 0.5,
                metrics.LastLabelRight,
                $"At {width}px, Food Catalog label is clipped on the right (lastLabelRight={metrics.LastLabelRight}, navRight={metrics.NavRight})."
            );
        }
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

    private sealed class NavMetrics
    {
        public double NavClientWidth { get; set; }
        public double NavScrollWidth { get; set; }
        public double BrandRight { get; set; }
        public double NavLeft { get; set; }
        public double NavRight { get; set; }
        public double ActionsLeft { get; set; }
        public double FirstLinkLeft { get; set; }
        public double FirstIconLeft { get; set; }
        public double LastLinkRight { get; set; }
        public double LastLabelRight { get; set; }
    }
}

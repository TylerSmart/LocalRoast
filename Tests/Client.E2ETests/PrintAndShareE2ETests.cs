using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace App.Client.E2ETests;

[TestClass]
public sealed class PrintAndShareE2ETests : E2ETestBase
{
    [TestMethod]
    public async Task ShareLink_CreateOpenInUnauthenticatedContextScaleAndRevoke_EnforcesStrictBoundaries()
    {
        await RegisterAndLoginAsync();

        // 1. Create a published sub-recipe ("Quick Pickled Onions")
        await Page.Locator("a[routerlink='/recipes/new']").First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/new.*"));

        var subRecipeTitle = $"Pickled Onions {Guid.NewGuid():N}"[..22];
        await Page.Locator("input[formcontrolname='title']").FillAsync(subRecipeTitle);
        await Page.Locator("input[formcontrolname='displayName']").First.FillAsync("Red Onion");
        await Page.Locator("input[formcontrolname='quantity']").First.FillAsync("1");
        await Page.Locator("textarea[formcontrolname='instruction']")
            .First.FillAsync("Slice thin and cover with vinegar.");
        await Page.Locator("button[type='submit']", new() { HasText = "Save Recipe" })
            .First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+$"));

        // 2. Create a parent recipe linking the sub-recipe ("Street Fish Tacos")
        await Page.GotoAsync($"{ServerUrl}/recipes/new");
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/new.*"));

        var parentRecipeTitle = $"Fish Tacos {Guid.NewGuid():N}"[..20];
        await Page.Locator("input[formcontrolname='title']").FillAsync(parentRecipeTitle);
        await Page.Locator("input[formcontrolname='baseServings']").FillAsync("4");
        await Page.Locator("textarea[formcontrolname='description']")
            .FillAsync("Baja style street tacos with crisp fish.");

        // First Ingredient: Mahi Mahi (200 g)
        await Page.Locator("input[formcontrolname='displayName']").First.FillAsync("Mahi Mahi");
        await Page.Locator("input[formcontrolname='quantity']").First.FillAsync("200");
        await Page.Locator("mat-select[formcontrolname='unit']").First.ClickAsync();
        await Page.Locator("mat-option", new() { HasText = "Gram (g)" }).First.ClickAsync();

        // Second Ingredient: Linked Sub-Recipe ("Pickled Onions")
        await Page.Locator("button", new() { HasText = "Add Ingredient" }).ClickAsync();
        var secondIng = Page.Locator(".builder-item-card").Nth(1);
        await secondIng.Locator("mat-checkbox.mode-checkbox").ClickAsync();

        var subSelect = secondIng.Locator("mat-select[formcontrolname='linkedRecipeId']");
        await Expect(subSelect).ToBeVisibleAsync();
        await subSelect.ClickAsync(new() { Force = true });
        await Page.Locator("mat-option", new() { HasText = subRecipeTitle }).First.ClickAsync();

        await secondIng.Locator("input[formcontrolname='quantity']").FillAsync("1");

        // Step
        await Page.Locator("textarea[formcontrolname='instruction']")
            .First.FillAsync("Grill fish and assemble tacos with pickled onions.");

        await Page.Locator("button[type='submit']", new() { HasText = "Save Recipe" })
            .First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+$"));
        await Expect(Page.Locator(".recipe-title")).ToContainTextAsync(parentRecipeTitle);

        // 3. Open Share Dialog and create a share link
        await Page.Locator("button[aria-label='Share recipe']").ClickAsync();
        var shareDialog = Page.Locator("app-recipe-share-dialog");
        await Expect(shareDialog).ToBeVisibleAsync();

        await shareDialog.Locator("input[formcontrolname='label']").FillAsync("Family Taco Night");
        await shareDialog.Locator("button[aria-label='Create share link']").ClickAsync();

        var linkCard = shareDialog.Locator(".link-card").First;
        await Expect(linkCard).ToBeVisibleAsync();
        await Expect(linkCard.Locator(".link-label")).ToContainTextAsync("Family Taco Night");
        await Expect(linkCard.Locator(".badge-active")).ToContainTextAsync("Active");

        var openLink = linkCard.Locator("a[aria-label='Open public view in new tab']");
        var shareUrl = await openLink.GetAttributeAsync("href");
        Assert.IsNotNull(shareUrl, "Expected share URL href to be populated.");

        // 4. Launch brand-new unauthenticated browser context
        await using var guestContext = await Browser.NewContextAsync();
        var guestPage = await guestContext.NewPageAsync();
        await guestPage.GotoAsync(shareUrl);

        // 5. Verify unauthenticated public recipe view
        await Expect(guestPage).ToHaveURLAsync(new Regex(".*/share/.*"));
        await Expect(guestPage.Locator(".recipe-title")).ToContainTextAsync(parentRecipeTitle);
        await Expect(guestPage.Locator(".shared-badge")).ToContainTextAsync("Shared Recipe");

        // Shell navigation must NOT be present
        await Expect(guestPage.Locator(".desktop-nav")).Not.ToBeVisibleAsync();
        await Expect(guestPage.Locator("app-shell .top-bar")).Not.ToBeVisibleAsync();

        // 6. Test scaling to 2x (4 servings -> 8 servings, 200 g -> 400 g)
        await guestPage.Locator("button.mult-chip", new() { HasText = "2x" }).ClickAsync();
        await Expect(guestPage.Locator(".servings-display")).ToContainTextAsync("8");
        await Expect(guestPage.Locator(".scaled-qty").First).ToContainTextAsync("400 g");

        // 7. Test unit conversion to US Customary (400 g -> oz)
        await guestPage.Locator("mat-button-toggle", new() { HasText = "US" }).ClickAsync();
        await Expect(guestPage.Locator(".scaled-qty").First).ToContainTextAsync("oz");

        // 8. Verify sub-recipe inline preview and lack of internal links
        var subPanel = guestPage.Locator("mat-expansion-panel.sub-recipe-inline-panel").First;
        await subPanel.Locator("mat-expansion-panel-header").ClickAsync();
        await Expect(subPanel.Locator("mat-panel-title")).ToContainTextAsync(subRecipeTitle);
        await Expect(subPanel.Locator(".sub-ingredients-list")).ToContainTextAsync("Red Onion");

        var leakedLinks = guestPage.Locator("app-public-shared-recipe a[href*='/recipes/']");
        await Expect(leakedLinks).ToHaveCountAsync(0);

        // 9. Back on authenticated page: Revoke the share link
        await shareDialog.Locator("button[aria-label='Revoke share link']").First.ClickAsync();
        await Expect(shareDialog.Locator(".badge-revoked")).ToBeVisibleAsync();

        // 10. Reload guest page and assert unavailable card
        await guestPage.ReloadAsync();
        await Expect(guestPage.Locator(".public-unavailable-card[role='alert']"))
            .ToBeVisibleAsync();
        await Expect(guestPage.Locator(".recipe-title")).Not.ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task PrintView_RecipeDetailAndPublicShare_AppliesCrispPrintMediaStylesAndTriggersPrint()
    {
        await RegisterAndLoginAsync();

        // 1. Create a recipe
        await Page.Locator("a[routerlink='/recipes/new']").First.ClickAsync();
        var recipeTitle = $"Crisp Carbonara {Guid.NewGuid():N}"[..22];
        await Page.Locator("input[formcontrolname='title']").FillAsync(recipeTitle);
        await Page.Locator("input[formcontrolname='baseServings']").FillAsync("2");
        await Page.Locator("input[formcontrolname='displayName']").First.FillAsync("Guanciale");
        await Page.Locator("input[formcontrolname='quantity']").First.FillAsync("200");
        await Page.Locator("mat-select[formcontrolname='unit']").First.ClickAsync();
        await Page.Locator("mat-option", new() { HasText = "Gram (g)" }).First.ClickAsync();
        await Page.Locator("textarea[formcontrolname='instruction']")
            .First.FillAsync("Crisp guanciale over low heat.");
        await Page.Locator("button[type='submit']", new() { HasText = "Save Recipe" })
            .First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+$"));

        // 2. Set dark mode to verify print media overrides it to light ink & paper
        await Page.EvaluateAsync("document.documentElement.setAttribute('data-theme', 'dark');");

        // Scale to 2x (4 servings)
        await Page.Locator("button.mult-chip", new() { HasText = "2x" }).ClickAsync();
        await Expect(Page.Locator(".servings-display")).ToContainTextAsync("4");

        // Stub window.print and verify trigger
        await Page.EvaluateAsync(
            "window.__printed = false; window.print = () => { window.__printed = true; };"
        );
        await Page.Locator("button[aria-label='Print recipe']").ClickAsync();
        var wasPrinted = await Page.EvaluateAsync<bool>("() => window.__printed === true");
        Assert.IsTrue(wasPrinted, "Expected window.print() to be triggered.");

        // 3. Emulate print media on authenticated detail page
        await Page.EmulateMediaAsync(new() { Media = Media.Print });

        await Expect(Page.Locator(".detail-nav-bar")).ToBeHiddenAsync();
        await Expect(Page.Locator(".scaling-toolbar-card")).ToBeHiddenAsync();
        await Expect(Page.Locator("app-shell .top-bar")).ToBeHiddenAsync();

        await Expect(Page.Locator(".print-only-meta-line")).ToBeVisibleAsync();
        await Expect(Page.Locator(".print-only-meta-line")).ToContainTextAsync("4 servings");

        var computedBg = await Page.EvaluateAsync<string>(
            "() => window.getComputedStyle(document.body).backgroundColor"
        );
        Assert.AreEqual(
            "rgb(255, 255, 255)",
            computedBg,
            "Expected print background to be forced to #ffffff white."
        );

        // Reset to screen media
        await Page.EmulateMediaAsync(new() { Media = Media.Screen });

        // 4. Create a share link
        await Page.Locator("button[aria-label='Share recipe']").ClickAsync();
        var shareDialog = Page.Locator("app-recipe-share-dialog");
        await Expect(shareDialog).ToBeVisibleAsync();
        await shareDialog.Locator("button[aria-label='Create share link']").ClickAsync();
        var shareUrl = await shareDialog
            .Locator("a[aria-label='Open public view in new tab']")
            .First.GetAttributeAsync("href");
        Assert.IsNotNull(shareUrl);

        // 5. Navigate to public share view and emulate print media
        await Page.GotoAsync(shareUrl);
        await Expect(Page.Locator(".recipe-title")).ToContainTextAsync(recipeTitle);

        await Page.EmulateMediaAsync(new() { Media = Media.Print });
        await Expect(Page.Locator(".public-top-bar")).ToBeHiddenAsync();
        await Expect(Page.Locator(".scaling-toolbar-card")).ToBeHiddenAsync();
        await Expect(Page.Locator(".print-only-meta-line")).ToBeVisibleAsync();
        await Expect(Page.Locator(".ingredients-column")).ToBeVisibleAsync();
        await Expect(Page.Locator(".instructions-column")).ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task PrintAndShare_MobileViewport_360px_RendersDialogAndPublicPageWithoutOverflow()
    {
        await Page.SetViewportSizeAsync(360, 640);
        await RegisterAndLoginAsync();

        // 1. Create a recipe
        await Page.Locator("a[routerlink='/recipes/new']").First.ClickAsync();
        var recipeTitle = $"Mobile Soup {Guid.NewGuid():N}"[..20];
        await Page.Locator("input[formcontrolname='title']").FillAsync(recipeTitle);
        await Page.Locator("input[formcontrolname='baseServings']").FillAsync("2");
        await Page.Locator("input[formcontrolname='displayName']").First.FillAsync("Green Lentils");
        await Page.Locator("input[formcontrolname='quantity']").First.FillAsync("1");
        await Page.Locator("textarea[formcontrolname='instruction']")
            .First.FillAsync("Simmer lentils until tender.");
        await Page.Locator("button[type='submit']", new() { HasText = "Save Recipe" })
            .First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+$"));

        // 2. Assert recipe detail action buttons are present and no horizontal overflow at 360px
        await Expect(Page.Locator("button[aria-label='Share recipe']")).ToBeVisibleAsync();
        await Expect(Page.Locator("button[aria-label='Print recipe']")).ToBeVisibleAsync();

        var detailScrollWidth = await Page.EvaluateAsync<int>(
            "() => document.documentElement.scrollWidth"
        );
        Assert.IsLessThanOrEqualTo(
            detailScrollWidth,
            360,
            $"Expected document.documentElement.scrollWidth <= 360, but was {detailScrollWidth}."
        );

        // 3. Open share dialog and verify no overflow at 360px
        await Page.Locator("button[aria-label='Share recipe']").ClickAsync();
        var shareDialog = Page.Locator("app-recipe-share-dialog");
        await Expect(shareDialog).ToBeVisibleAsync();

        var dialogScrollWidth = await Page.EvaluateAsync<int>(
            "() => document.documentElement.scrollWidth"
        );
        Assert.IsLessThanOrEqualTo(
            dialogScrollWidth,
            360,
            $"Expected document.documentElement.scrollWidth <= 360 with share dialog open, but was {dialogScrollWidth}."
        );

        await shareDialog.Locator("input[formcontrolname='label']").FillAsync("Mobile Potluck");
        await shareDialog.Locator("button[aria-label='Create share link']").ClickAsync();
        var shareUrl = await shareDialog
            .Locator("a[aria-label='Open public view in new tab']")
            .First.GetAttributeAsync("href");
        Assert.IsNotNull(shareUrl);

        // 4. Navigate to public share view at 360px
        await Page.GotoAsync(shareUrl);
        await Expect(Page.Locator(".recipe-title")).ToContainTextAsync(recipeTitle);

        await Page.Locator("button.mult-chip", new() { HasText = "2x" }).ClickAsync();
        await Page.Locator("mat-button-toggle", new() { HasText = "Metric" }).ClickAsync();

        var publicScrollWidth = await Page.EvaluateAsync<int>(
            "() => document.documentElement.scrollWidth"
        );
        Assert.IsLessThanOrEqualTo(
            publicScrollWidth,
            360,
            $"Expected public share page scrollWidth <= 360, but was {publicScrollWidth}."
        );
    }
}

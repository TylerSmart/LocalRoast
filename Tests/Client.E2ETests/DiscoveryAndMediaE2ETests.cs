using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace App.Client.E2ETests;

[TestClass]
public sealed class DiscoveryAndMediaE2ETests : E2ETestBase
{
    private static readonly byte[] ValidPngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="
    );

    [TestMethod]
    public async Task RecipeMediaAndTaxonomy_UploadPhotosAndOrganize_FiltersInCookbook()
    {
        await RegisterAndLoginAsync();

        // 1. Create a custom Collection from the Cookbook collections strip
        await Page.Locator(".add-collection-pill").ClickAsync();
        var collectionDialog = Page.Locator("app-collection-dialog");
        await Expect(collectionDialog).ToBeVisibleAsync();

        var collectionName = $"Summer Grilling {Guid.NewGuid():N}"[..20];
        await collectionDialog.Locator("input[formcontrolname='name']").FillAsync(collectionName);
        await collectionDialog
            .Locator("textarea[formcontrolname='description']")
            .FillAsync("BBQ favorites and smoker projects");
        await collectionDialog.Locator("button[mat-flat-button]").ClickAsync();
        await Expect(collectionDialog).Not.ToBeVisibleAsync();

        // Verify collection pill appears in strip
        var createdCollectionPill = Page.Locator(
            ".collection-pill",
            new() { HasText = collectionName }
        );
        await Expect(createdCollectionPill).ToBeVisibleAsync();

        // 2. Navigate to New Recipe and fill details
        await Page.Locator("a[routerlink='/recipes/new']").First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/new.*"));

        var recipeTitle = $"Grilled Veggie Skewers {Guid.NewGuid():N}"[..24];
        await Page.Locator("input[formcontrolname='title']").FillAsync(recipeTitle);
        await Page.Locator("input[formcontrolname='baseServings']").FillAsync("4");
        await Page.Locator("textarea[formcontrolname='description']")
            .FillAsync("Charred vegetables with lemon and oregano.");

        // Upload Cover Photo
        await Page.SetInputFilesAsync(
            "input.cover-file-input",
            new FilePayload
            {
                Name = "cover.png",
                MimeType = "image/png",
                Buffer = ValidPngBytes,
            }
        );
        await Expect(Page.Locator("img.cover-preview-img")).ToBeVisibleAsync();

        // Select the newly created Collection
        await Page.Locator("mat-chip-option", new() { HasText = collectionName }).ClickAsync();

        // Select seeded Tags: "Vegetarian" and "Sheet Pan"
        await Page.Locator("mat-chip-option", new() { HasText = "Vegetarian" }).ClickAsync();
        await Page.Locator("mat-chip-option", new() { HasText = "Sheet Pan" }).ClickAsync();

        // Fill Ingredient 1
        await Page.Locator("input[formcontrolname='displayName']").First.FillAsync("Zucchini");
        await Page.Locator("input[formcontrolname='quantity']").First.FillAsync("2");

        // Upload Step 1 Photo
        await Page.SetInputFilesAsync(
            "input.step-file-input",
            new FilePayload
            {
                Name = "step.png",
                MimeType = "image/png",
                Buffer = ValidPngBytes,
            }
        );
        await Expect(Page.Locator("img.step-preview-img")).ToBeVisibleAsync();

        // Fill Step Instruction
        await Page.Locator("textarea[formcontrolname='instruction']")
            .First.FillAsync(
                "Cut zucchini into thick rounds, thread onto skewers, and grill 8 minutes."
            );

        // Save Recipe
        await Page.Locator("button[type='submit']", new() { HasText = "Save Recipe" })
            .First.ClickAsync();

        // 3. Verify Recipe Detail Page
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+$"));
        await Expect(Page.Locator(".recipe-title")).ToContainTextAsync(recipeTitle);

        // Verify Hero Image and Step Reference Image rendered
        var heroImage = Page.Locator("img.recipe-hero-image");
        await Expect(heroImage).ToBeVisibleAsync();
        var heroSrc = await heroImage.GetAttributeAsync("src");
        StringAssert.StartsWith(
            heroSrc,
            "/api/uploads/recipes/",
            $"Hero image src '{heroSrc}' should start with '/api/uploads/recipes/'"
        );

        var stepImage = Page.Locator("img.step-reference-image");
        await Expect(stepImage).ToBeVisibleAsync();
        var stepSrc = await stepImage.GetAttributeAsync("src");
        StringAssert.StartsWith(
            stepSrc,
            "/api/uploads/steps/",
            $"Step image src '{stepSrc}' should start with '/api/uploads/steps/'"
        );

        // Verify Hero Badges
        await Expect(Page.Locator(".hero-collection-badge")).ToContainTextAsync(collectionName);
        await Expect(Page.Locator(".hero-taxonomy-strip")).ToContainTextAsync("Vegetarian");
        await Expect(Page.Locator(".hero-taxonomy-strip")).ToContainTextAsync("Sheet Pan");

        // 4. Navigate back to /recipes (Cookbook)
        await Page.Locator("a.back-link").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes$"));

        // Verify Recipe Card with Thumbnail
        var recipeCard = Page.Locator(".recipe-card", new() { HasText = recipeTitle });
        await Expect(recipeCard).ToBeVisibleAsync();
        await Expect(recipeCard.Locator("img.recipe-card-thumb")).ToBeVisibleAsync();
        await Expect(recipeCard.Locator(".collection-badge-chip"))
            .ToContainTextAsync(collectionName);

        // Filter by the custom Collection pill
        await Page.Locator(".collection-pill", new() { HasText = collectionName }).ClickAsync();
        await Expect(recipeCard).ToBeVisibleAsync();

        // Filter by Tag
        await Page.Locator(".tag-filter-field mat-select").ClickAsync();
        await Page.Locator("mat-option", new() { HasText = "Vegetarian" }).ClickAsync();
        await Page.Keyboard.PressAsync("Escape");
        await Expect(recipeCard).ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task FridgeMatcherAndGlobalSearch_RanksRecipesIgnoringPantryStaples_AndNavigatesViaCmdK()
    {
        await RegisterAndLoginAsync();

        // 1. Create Recipe A: Uses Chicken Thighs, Lime, Kosher Salt, Extra-Virgin Olive Oil (2 staples)
        await Page.Locator("a[routerlink='/recipes/new']").First.ClickAsync();
        var recipeATitle = $"Weeknight Lime Chicken {Guid.NewGuid():N}"[..26];
        await Page.Locator("input[formcontrolname='title']").FillAsync(recipeATitle);
        await Page.Locator("textarea[formcontrolname='description']")
            .FillAsync("Crispy skin chicken thighs with a bright lime pan glaze.");

        // Ingredient 1: Chicken Thighs (select from autocomplete)
        await Page.Locator("input[formcontrolname='displayName']")
            .First.FillAsync("Chicken Thighs");
        await Page.Locator("mat-option", new() { HasText = "Chicken Thighs" }).First.ClickAsync();
        await Page.Locator("input[formcontrolname='quantity']").First.FillAsync("4");

        // Ingredient 2: Lime (select from autocomplete)
        await Page.Locator("button", new() { HasText = "Add Ingredient" }).ClickAsync();
        await Page.Locator("input[formcontrolname='displayName']").Nth(1).FillAsync("Lime");
        await Page.Locator("mat-option", new() { HasText = "Lime" }).First.ClickAsync();
        await Page.Locator("input[formcontrolname='quantity']").Nth(1).FillAsync("2");

        // Ingredient 3: Kosher Salt (pantry staple, select from autocomplete)
        await Page.Locator("button", new() { HasText = "Add Ingredient" }).ClickAsync();
        await Page.Locator("input[formcontrolname='displayName']").Nth(2).FillAsync("Kosher Salt");
        await Page.Locator("mat-option", new() { HasText = "Kosher Salt" }).First.ClickAsync();
        await Page.Locator("input[formcontrolname='quantity']").Nth(2).FillAsync("1");

        // Ingredient 4: Extra-Virgin Olive Oil (pantry staple, select from autocomplete)
        await Page.Locator("button", new() { HasText = "Add Ingredient" }).ClickAsync();
        await Page.Locator("input[formcontrolname='displayName']")
            .Nth(3)
            .FillAsync("Extra-Virgin Olive Oil");
        await Page.Locator("mat-option", new() { HasText = "Extra-Virgin Olive Oil" })
            .First.ClickAsync();
        await Page.Locator("input[formcontrolname='quantity']").Nth(3).FillAsync("2");

        // Step
        await Page.Locator("textarea[formcontrolname='instruction']")
            .First.FillAsync("Sear chicken in olive oil, finish with squeeze of fresh lime juice.");

        await Page.Locator("button[type='submit']", new() { HasText = "Save Recipe" })
            .First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+$"));

        // 2. Create Recipe B: Uses Chicken Thighs, Lime, Cilantro, Yellow Onion (2 extra non-staples)
        await Page.Locator("a[routerlink='/recipes']", new() { HasText = "Cookbook" })
            .First.ClickAsync();
        await Page.Locator("a[routerlink='/recipes/new']").First.ClickAsync();

        var recipeBTitle = $"Loaded Chicken Bowls {Guid.NewGuid():N}"[..24];
        await Page.Locator("input[formcontrolname='title']").FillAsync(recipeBTitle);

        // Ingredient 1: Chicken Thighs (select from autocomplete)
        await Page.Locator("input[formcontrolname='displayName']")
            .First.FillAsync("Chicken Thighs");
        await Page.Locator("mat-option", new() { HasText = "Chicken Thighs" }).First.ClickAsync();

        // Ingredient 2: Lime (select from autocomplete)
        await Page.Locator("button", new() { HasText = "Add Ingredient" }).ClickAsync();
        await Page.Locator("input[formcontrolname='displayName']").Nth(1).FillAsync("Lime");
        await Page.Locator("mat-option", new() { HasText = "Lime" }).First.ClickAsync();

        // Ingredient 3: Cilantro (non-staple, select from autocomplete)
        await Page.Locator("button", new() { HasText = "Add Ingredient" }).ClickAsync();
        await Page.Locator("input[formcontrolname='displayName']").Nth(2).FillAsync("Cilantro");
        await Page.Locator("mat-option", new() { HasText = "Cilantro" }).First.ClickAsync();

        // Ingredient 4: Yellow Onion (non-staple, select from autocomplete)
        await Page.Locator("button", new() { HasText = "Add Ingredient" }).ClickAsync();
        await Page.Locator("input[formcontrolname='displayName']").Nth(3).FillAsync("Yellow Onion");
        await Page.Locator("mat-option", new() { HasText = "Yellow Onion" }).First.ClickAsync();

        // Step
        await Page.Locator("textarea[formcontrolname='instruction']")
            .First.FillAsync(
                "Shred cooked chicken and assemble bowls with fresh onion and cilantro."
            );

        await Page.Locator("button[type='submit']", new() { HasText = "Save Recipe" })
            .First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+$"));

        // 3. Navigate to /matcher ("Use Up Ingredients")
        await Page.Locator("nav.desktop-nav a", new() { HasText = "Matcher" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/matcher.*"));

        // Select "Chicken Thighs" and "Lime"
        var searchInput = Page.Locator("input[aria-label='Search ingredients to match']");
        await searchInput.FillAsync("Chicken Thighs");
        await Page.Locator("mat-option", new() { HasText = "Chicken Thighs" }).First.ClickAsync();

        await searchInput.FillAsync("Lime");
        await Page.Locator("mat-option", new() { HasText = "Lime" }).First.ClickAsync();

        // 4. Assert Ranked Results
        var resultCards = Page.Locator(".matcher-result-card");
        await Expect(resultCards).ToHaveCountAsync(2);

        // Recipe A must rank FIRST: 0 additional non-staples needed -> "Ready to cook!"
        var firstCard = resultCards.First;
        await Expect(firstCard.Locator(".recipe-title")).ToContainTextAsync(recipeATitle);
        await Expect(firstCard.Locator(".match-badge")).ToContainTextAsync("2 of 2 matched");
        await Expect(firstCard.Locator(".readiness-badge")).ToContainTextAsync("Ready to cook!");

        // Recipe B must rank SECOND: needs 2 more non-staples
        var secondCard = resultCards.Nth(1);
        await Expect(secondCard.Locator(".recipe-title")).ToContainTextAsync(recipeBTitle);
        await Expect(secondCard.Locator(".readiness-badge")).ToContainTextAsync("Needs 2 more");

        // 5. Test Global Instant Search Dialog via Ctrl+K
        await Page.Keyboard.PressAsync("Control+k");
        var searchDialog = Page.Locator("app-global-search-dialog");
        await Expect(searchDialog).ToBeVisibleAsync();

        var dialogInput = searchDialog.Locator(".dialog-search-input");
        await dialogInput.FillAsync(recipeATitle);

        var matchedResult = searchDialog.Locator(".result-item", new() { HasText = recipeATitle });
        await Expect(matchedResult).ToBeVisibleAsync();
        await matchedResult.ClickAsync();

        // Verify navigated to Recipe A detail page
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+$"));
        await Expect(Page.Locator(".recipe-title")).ToContainTextAsync(recipeATitle);
    }

    [TestMethod]
    public async Task DiscoveryAndMatcher_MobileViewport_360px_RendersAndNavigatesCleanly()
    {
        await RegisterAndLoginAsync();

        // 1. Resize viewport to 360x640 mobile
        await Page.SetViewportSizeAsync(360, 640);

        // 2. Verify mobile bottom nav is displayed with all 3 tabs
        var mobileNav = Page.Locator("nav.mobile-bottom-nav");
        await Expect(mobileNav).ToBeVisibleAsync();
        await Expect(mobileNav.Locator(".mobile-tab", new() { HasText = "Cookbook" }))
            .ToBeVisibleAsync();
        await Expect(mobileNav.Locator(".mobile-tab", new() { HasText = "Matcher" }))
            .ToBeVisibleAsync();
        await Expect(mobileNav.Locator(".mobile-tab", new() { HasText = "Catalog" }))
            .ToBeVisibleAsync();

        // Verify zero horizontal scrolling
        var hasHorizontalScroll = await Page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth"
        );
        Assert.IsFalse(
            hasHorizontalScroll,
            "Cookbook page should not overflow horizontally at 360px."
        );

        // 3. Tap Matcher in mobile bottom bar
        await mobileNav.Locator(".mobile-tab", new() { HasText = "Matcher" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/matcher.*"));

        hasHorizontalScroll = await Page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth"
        );
        Assert.IsFalse(
            hasHorizontalScroll,
            "Matcher page should not overflow horizontally at 360px."
        );

        // Tap a suggested ingredient pill if present
        var suggestionPill = Page.Locator(".suggestion-pill").First;
        if (await suggestionPill.IsVisibleAsync())
        {
            await suggestionPill.ClickAsync();
            await Expect(Page.Locator("mat-chip-row")).ToBeVisibleAsync();
        }

        // 4. Open Global Search Dialog via mobile top-bar search button
        var mobileSearchBtn = Page.Locator("button.mobile-search-btn");
        await Expect(mobileSearchBtn).ToBeVisibleAsync();
        await mobileSearchBtn.ClickAsync();

        var searchDialog = Page.Locator("app-global-search-dialog");
        await Expect(searchDialog).ToBeVisibleAsync();
    }
}

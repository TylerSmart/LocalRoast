using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace App.Client.E2ETests;

[TestClass]
public sealed class QueryParamAndUiEnhancementsE2ETests : E2ETestBase
{
    private async Task<string> CreateRecipeViaApiAsync(object payload)
    {
        var result = await Page.EvaluateAsync<string>(
            @"async (payload) => {
                const tokenKey = Object.keys(localStorage).find(k => k.endsWith('-token'));
                const token = tokenKey ? localStorage.getItem(tokenKey) : null;
                const res = await fetch('/api/recipes', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        'Authorization': `Bearer ${token}`
                    },
                    body: JSON.stringify(payload)
                });
                const text = await res.text();
                if (!res.ok) return 'ERROR ' + res.status + ': ' + text;
                return JSON.parse(text).id;
            }",
            payload
        );
        Assert.IsFalse(result.StartsWith("ERROR", StringComparison.Ordinal), result);
        return result;
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task RecipeDescription_LongAtkDescriptionOver2000Chars_SavesAndRendersCompletely()
    {
        await RegisterAndLoginAsync();

        var uniqueTag = Guid.NewGuid().ToString("N")[..8];
        var title = $"ATK Lamb Rack {uniqueTag}";
        var paragraph =
            "To achieve a deeply browned herb crust without overcooking the center, we salt the lamb rack ahead of time and sear gently before roasting over indirect heat.";
        var longDescription = string.Join("\n\n", Enumerable.Repeat(paragraph, 16));
        Assert.IsGreaterThan(2400, longDescription.Length);
        Assert.IsLessThanOrEqualTo(4000, longDescription.Length);

        await Page.GotoAsync($"{ServerUrl}/recipes/new");
        await Expect(Page.Locator(".page-title")).ToContainTextAsync("New Recipe");

        await Page.Locator("input[formcontrolname='title']").FillAsync(title);
        await Page.Locator("textarea[formcontrolname='description']").FillAsync(longDescription);

        // Fill default ingredient & step rows
        await Page.Locator("input[formcontrolname='displayName']").First.FillAsync("Lamb Rack");
        var stepEditable = Page.Locator(".step-instruction-editable").First;
        await stepEditable.ClickAsync();
        await Page.Keyboard.TypeAsync("Roast lamb until medium-rare.");

        await Page.GetByRole(AriaRole.Button, new() { Name = "Save Recipe", Exact = false })
            .First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+$"));

        var descriptionEl = Page.Locator(".recipe-description");
        await Expect(descriptionEl).ToBeVisibleAsync();
        var renderedText = (await descriptionEl.TextContentAsync())?.Trim() ?? string.Empty;
        Assert.AreEqual(longDescription.Length, renderedText.Length);
        Assert.StartsWith(paragraph, renderedText);
        Assert.EndsWith(paragraph, renderedText);
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task DividedIngredients_RenderEachUseOnSeparateLines_DesktopAndMobile360px()
    {
        await RegisterAndLoginAsync();

        var recipeId = await CreateRecipeViaApiAsync(
            new
            {
                title = $"Pepper Steak {Guid.NewGuid():N}"[..24],
                baseServings = 4,
                ingredients = new object[]
                {
                    new
                    {
                        displayName = "pepper",
                        quantity = 0.625,
                        unit = "Tsp",
                        preparationNote = "divided",
                    },
                },
                steps = new object[]
                {
                    new
                    {
                        instruction = "Season beef with ||{\"ingredient\":\"pepper\",\"amount\":0.25,\"unit\":\"tsp\"}||.",
                    },
                    new
                    {
                        instruction = "Whisk sauce with ||{\"ingredient\":\"pepper\",\"amount\":0.125,\"unit\":\"tsp\"}||.",
                    },
                    new
                    {
                        instruction = "Finish dish with ||{\"ingredient\":\"pepper\",\"amount\":0.25,\"unit\":\"tsp\"}||.",
                    },
                },
            }
        );

        await Page.GotoAsync($"{ServerUrl}/recipes/{recipeId}");
        var usesList = Page.Locator("ul.ingredient-uses");
        await Expect(usesList).ToHaveCountAsync(1);
        var listStyleType = await usesList.EvaluateAsync<string>(
            "el => getComputedStyle(el).listStyleType"
        );
        Assert.AreEqual("disc", listStyleType);

        var useItems = Page.Locator("ul.ingredient-uses > li.ingredient-use-item");
        await Expect(useItems).ToHaveCountAsync(3);
        await Expect(useItems.Nth(0)).ToHaveTextAsync("1/4 tsp pepper (step 1)");
        await Expect(useItems.Nth(1)).ToHaveTextAsync("1/8 tsp pepper (step 2)");
        await Expect(useItems.Nth(2)).ToHaveTextAsync("1/4 tsp pepper (step 3)");

        var usesContainerText =
            (await Page.Locator(".ingredient-uses").First.TextContentAsync()) ?? string.Empty;
        Assert.DoesNotContain("Divided:", usesContainerText);
        Assert.DoesNotContain("·", usesContainerText);

        // Verify stacked vertically on desktop (each line's top Y strictly below previous line's top Y)
        var box0 = await useItems.Nth(0).BoundingBoxAsync();
        var box1 = await useItems.Nth(1).BoundingBoxAsync();
        var box2 = await useItems.Nth(2).BoundingBoxAsync();
        Assert.IsNotNull(box0);
        Assert.IsNotNull(box1);
        Assert.IsNotNull(box2);
        Assert.IsGreaterThan(box0.Y, box1.Y, "Second divided ingredient line must be below first.");
        Assert.IsGreaterThan(box1.Y, box2.Y, "Third divided ingredient line must be below second.");

        // Verify clean stacked rendering with zero horizontal overflow at 360px mobile viewport
        await Page.SetViewportSizeAsync(360, 800);
        var mobileBox0 = await useItems.Nth(0).BoundingBoxAsync();
        var mobileBox1 = await useItems.Nth(1).BoundingBoxAsync();
        Assert.IsNotNull(mobileBox0);
        Assert.IsNotNull(mobileBox1);
        Assert.IsGreaterThan(mobileBox0.Y, mobileBox1.Y);

        var hasHorizontalOverflow = await Page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > window.innerWidth + 2"
        );
        Assert.IsFalse(hasHorizontalOverflow, "Recipe detail should not overflow at 360px width.");
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task QueryParams_PersistAndRestoreStateAcrossReloads_CookbookDetailCookModeAndMealPlan()
    {
        await RegisterAndLoginAsync();

        var tag = Guid.NewGuid().ToString("N")[..6];
        var easyTitle = $"Quick Lemon Pasta {tag}";
        var projectTitle = $"Slow Braised Brisket {tag}";

        var recipeId = await CreateRecipeViaApiAsync(
            new
            {
                title = easyTitle,
                difficulty = "Easy",
                baseServings = 4,
                ingredients = new object[]
                {
                    new
                    {
                        displayName = "Spaghetti",
                        quantity = 1,
                        unit = "Lb",
                    },
                    new
                    {
                        displayName = "Lemon Juice",
                        quantity = 2,
                        unit = "Tbsp",
                    },
                },
                steps = new object[]
                {
                    new { instruction = "Boil pasta in salted water." },
                    new { instruction = "Toss hot pasta with lemon juice." },
                    new { instruction = "Serve immediately." },
                },
            }
        );

        await CreateRecipeViaApiAsync(
            new
            {
                title = projectTitle,
                difficulty = "Project",
                baseServings = 8,
                ingredients = new object[]
                {
                    new
                    {
                        displayName = "Beef Brisket",
                        quantity = 4,
                        unit = "Lb",
                    },
                },
                steps = new object[] { new { instruction = "Braise low and slow." } },
            }
        );

        // 1. Cookbook (/recipes): search + difficulty filter survive reload
        await Page.GotoAsync($"{ServerUrl}/recipes");
        var searchInput = Page.Locator(".search-field input");
        await Expect(searchInput).ToBeVisibleAsync();
        await searchInput.FillAsync(tag);
        await Expect(Page).ToHaveURLAsync(new Regex($"[?&]q={tag}"));
        await Expect(Page.Locator(".recipe-card")).ToHaveCountAsync(2);

        // Open filters drawer and filter Difficulty = Easy
        await Page.Locator("button.filter-toggle-btn").ClickAsync();
        await Page.Locator("mat-form-field", new() { HasText = "Difficulty" })
            .Locator("mat-select")
            .ClickAsync();
        await Page.GetByRole(AriaRole.Option, new() { Name = "Easy", Exact = true }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("[?&]difficulty=Easy"));
        await Expect(Page.Locator(".recipe-card")).ToHaveCountAsync(1);
        await Expect(Page.Locator(".recipe-card-title").First).ToContainTextAsync(easyTitle);

        await Page.ReloadAsync();
        await Expect(Page.Locator(".search-field input")).ToHaveValueAsync(tag);
        await Expect(Page.Locator(".recipe-card")).ToHaveCountAsync(1);
        await Expect(Page.Locator(".recipe-card-title").First).ToContainTextAsync(easyTitle);

        // 2. Food Catalog (/catalog): search + pantry toggle survive reload
        await Page.GotoAsync($"{ServerUrl}/catalog");
        var catalogSearch = Page.Locator(".catalog-page .search-field input");
        await Expect(catalogSearch).ToBeVisibleAsync();
        await catalogSearch.FillAsync("salt");
        await Expect(Page).ToHaveURLAsync(new Regex("[?&]q=salt"));

        await Page.Locator("mat-slide-toggle button[role='switch']").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("[?&]staples=1"));

        await Page.ReloadAsync();
        await Expect(Page.Locator(".catalog-page .search-field input")).ToHaveValueAsync("salt");
        await Expect(Page.Locator("mat-slide-toggle button[role='switch']"))
            .ToHaveAttributeAsync("aria-checked", "true");

        // 3. Recipe Detail (/recipes/:id): servings, Metric units, and checked ingredients survive reload
        await Page.GotoAsync($"{ServerUrl}/recipes/{recipeId}");
        await Expect(Page.Locator(".recipe-title")).ToContainTextAsync(easyTitle);

        await Page.Locator(".mult-chip", new() { HasText = "2x" }).ClickAsync();
        await Page.Locator("mat-button-toggle", new() { HasText = "Metric" }).ClickAsync();
        var firstIngCheckbox = Page.Locator(".ingredient-row mat-checkbox input").First;
        await firstIngCheckbox.CheckAsync(new() { Force = true });

        await Expect(Page).ToHaveURLAsync(new Regex("[?&]servings=8"));
        await Expect(Page).ToHaveURLAsync(new Regex("[?&]units=Metric"));
        await Expect(Page).ToHaveURLAsync(new Regex("[?&]ing=1"));

        await Page.ReloadAsync();
        await Expect(Page.Locator(".servings-display")).ToHaveTextAsync("8");
        await Expect(Page.Locator(".ingredient-row").First)
            .ToHaveClassAsync(new Regex("checked-row"));

        // 4. Cook Mode (/recipes/:id/cook): checked ingredients, completed steps, and active step survive reload
        await Page.Locator("button.start-cooking-btn").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+/cook"));
        await Expect(Page).ToHaveURLAsync(new Regex("[?&]ing=1"));
        await Expect(Page.Locator(".cook-ingredients-pane .ingredient-item").First)
            .ToHaveClassAsync(new Regex("is-checked"));

        // Mark step 1 done and advance to step 2
        var stepDoneCheckbox = Page.Locator("mat-checkbox.step-checkbox input").First;
        await stepDoneCheckbox.CheckAsync(new() { Force = true });
        await Expect(Page).ToHaveURLAsync(new Regex("[?&]steps=1"));

        await Page.GetByRole(AriaRole.Button, new() { Name = "Next Step", Exact = false })
            .ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("[?&]step=2"));
        await Expect(Page.Locator(".step-counter")).ToContainTextAsync("Step 2 of 3");

        await Page.ReloadAsync();
        await Expect(Page.Locator(".step-counter")).ToContainTextAsync("Step 2 of 3");
        await Expect(Page.Locator(".cook-ingredients-pane .ingredient-item").First)
            .ToHaveClassAsync(new Regex("is-checked"));
        await Expect(Page.Locator(".step-stepper-dots .step-dot-btn").First)
            .ToHaveClassAsync(new Regex("is-completed"));

        // 5. Meal Plan (/meal-plan): Month view & next month navigation survive reload
        await Page.GotoAsync($"{ServerUrl}/meal-plan");
        await Page.Locator("mat-button-toggle", new() { HasText = "Month" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("[?&]view=month"));

        await Page.GetByRole(AriaRole.Button, new() { Name = "Next period", Exact = false })
            .ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("[?&]month=\\d{4}-\\d{2}"));
        var expectedPeriodTitle = (await Page.Locator(".period-label").TextContentAsync())?.Trim();

        await Page.ReloadAsync();
        await Expect(Page.Locator(".period-label")).ToHaveTextAsync(expectedPeriodTitle!);
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task ClickingIngredient_InRecipeDetailAndCookMode_PreservesWindowScrollPosition()
    {
        await RegisterAndLoginAsync();

        var manyIngredients = Enumerable
            .Range(1, 20)
            .Select(i =>
                (object)
                    new
                    {
                        displayName = $"Test Ingredient {i}",
                        quantity = 1,
                        unit = "Tsp",
                    }
            )
            .ToArray();

        var recipeId = await CreateRecipeViaApiAsync(
            new
            {
                title = $"Long Scroll Recipe {Guid.NewGuid():N}"[..26],
                description = string.Join(
                    " ",
                    Enumerable.Repeat(
                        "A detailed headnote explaining the culinary technique and testing history.",
                        6
                    )
                ),
                baseServings = 4,
                ingredients = manyIngredients,
                steps = new object[]
                {
                    new
                    {
                        instruction = "Combine the first half of the ingredients in a large bowl.",
                    },
                    new
                    {
                        instruction = "Fold in the remaining ingredients and bake until golden.",
                    },
                },
            }
        );

        // Use a short viewport so the page scrolls vertically on both Recipe Detail and Cook Mode
        await Page.SetViewportSizeAsync(1024, 480);

        // 1. Recipe Detail: scroll down, toggle a lower ingredient, verify window does NOT jump to top
        await Page.GotoAsync($"{ServerUrl}/recipes/{recipeId}");
        var lastRow = Page.Locator(".ingredient-row").Last;
        await Expect(lastRow).ToBeVisibleAsync();
        await lastRow.ScrollIntoViewIfNeededAsync();
        var scrollBeforeDetail = await Page.EvaluateAsync<double>("() => window.scrollY");
        Assert.IsGreaterThan(150.0, scrollBeforeDetail, "Expected page to be scrolled down.");

        await lastRow.Locator("mat-checkbox").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("[?&]ing=20"));
        await Page.WaitForTimeoutAsync(200);

        var scrollAfterDetail = await Page.EvaluateAsync<double>("() => window.scrollY");
        Assert.IsGreaterThan(
            150.0,
            scrollAfterDetail,
            $"Clicking an ingredient on Recipe Detail jumped scroll from {scrollBeforeDetail} to {scrollAfterDetail}."
        );

        // 2. Cook Mode: scroll down on short viewport, click an ingredient, verify window & pane scroll do NOT jump to top
        await Page.SetViewportSizeAsync(1024, 420);
        await Page.GotoAsync($"{ServerUrl}/recipes/{recipeId}/cook");
        var cookPaneScroll = Page.Locator(".cook-ingredients-pane .ingredients-scroll");
        var cookRow = Page.Locator(".cook-ingredients-pane .ingredient-item").Nth(10);
        await Expect(cookRow).ToBeVisibleAsync();

        await Page.EvaluateAsync("() => window.scrollTo(0, 120)");
        await cookPaneScroll.EvaluateAsync("el => { el.scrollTop = 180; }");
        var scrollBeforeCook = await Page.EvaluateAsync<double>("() => window.scrollY");
        var paneScrollBeforeCook = await cookPaneScroll.EvaluateAsync<double>("el => el.scrollTop");
        Assert.IsGreaterThan(50.0, scrollBeforeCook, "Expected Cook Mode document to scroll down.");
        Assert.IsGreaterThan(
            50.0,
            paneScrollBeforeCook,
            "Expected Cook Mode ingredient pane to scroll down."
        );

        await cookRow.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("[?&]ing=11"));
        await Page.WaitForTimeoutAsync(200);

        var scrollAfterCook = await Page.EvaluateAsync<double>("() => window.scrollY");
        var paneScrollAfterCook = await cookPaneScroll.EvaluateAsync<double>("el => el.scrollTop");
        Assert.IsGreaterThanOrEqualTo(
            scrollBeforeCook - 50.0,
            scrollAfterCook,
            $"Clicking an ingredient in Cook Mode jumped window scroll from {scrollBeforeCook} to {scrollAfterCook}."
        );
        Assert.IsGreaterThanOrEqualTo(
            paneScrollBeforeCook - 50.0,
            paneScrollAfterCook,
            $"Clicking an ingredient in Cook Mode jumped pane scroll from {paneScrollBeforeCook} to {paneScrollAfterCook}."
        );
    }
}

using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace App.Client.E2ETests;

[TestClass]
public sealed class RecipeCookbookE2ETests : E2ETestBase
{
    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task Recipe_CreateWithSectionsAndTimers_DetailRendersAndScales()
    {
        await RegisterAndLoginAsync();

        // 1. Navigate to New Recipe
        await Page.Locator("a[routerlink='/recipes/new']").First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/new.*"));
        await Expect(Page.Locator(".page-title")).ToContainTextAsync("New Recipe");

        var recipeTitle = $"Tuscan Bean Soup {Guid.NewGuid():N}"[..24];
        await Page.Locator("input[formcontrolname='title']").FillAsync(recipeTitle);
        await Page.Locator("input[formcontrolname='baseServings']").FillAsync("6");
        await Page.Locator("input[formcontrolname='yieldDisplay']").FillAsync("6 hearty bowls");
        await Page.Locator("input[formcontrolname='prepTimeMinutes']").FillAsync("15");
        await Page.Locator("input[formcontrolname='cookTimeMinutes']").FillAsync("45");
        await Page.Locator("textarea[formcontrolname='description']")
            .FillAsync("Classic rustic Tuscan soup with cannellini beans.");

        // First Ingredient
        await Page.Locator("input[formcontrolname='sectionName']").First.FillAsync("Soup Base");
        await Page.Locator("input[formcontrolname='displayName']")
            .First.FillAsync("Cannellini Beans");
        await Page.Locator("input[formcontrolname='quantity']").First.FillAsync("2");
        await Page.Locator("mat-select[formcontrolname='unit']").First.ClickAsync();
        await Page.Locator("mat-option", new() { HasText = "Can" }).First.ClickAsync();
        await Page.Locator("input[formcontrolname='preparationNote']")
            .First.FillAsync("rinsed and drained");

        // First Step
        await Page.Locator(
                "input[formcontrolname='sectionName']",
                new() { HasNot = Page.Locator("input[formcontrolname='displayName']") }
            )
            .First.FillAsync("Simmering");
        await Page.Locator("[formcontrolname='instruction'] .step-instruction-editable")
            .First.FillAsync("Combine beans and broth in pot; simmer gently.");

        // Step Timer
        await Page.Locator(".add-timer-btn").First.ClickAsync();
        var timerDialog = Page.Locator("app-step-token-dialog");
        await Expect(timerDialog).ToBeVisibleAsync();
        var minutesInput = timerDialog.Locator("input[formcontrolname='minutes']");
        var labelInput = timerDialog.Locator("input[formcontrolname='label']");
        await Expect(minutesInput).ToBeFocusedAsync();
        await minutesInput.FillAsync("30");
        await labelInput.FillAsync("Low simmer");
        await Expect(minutesInput).ToHaveValueAsync("30");
        await Expect(labelInput).ToHaveValueAsync("Low simmer");
        await timerDialog.Locator("button", new() { HasText = "Insert Token" }).ClickAsync();
        await Expect(timerDialog).ToHaveCountAsync(0);

        // Save
        await Page.Locator("button[type='submit']", new() { HasText = "Save Recipe" })
            .First.ClickAsync();

        // 2. Verify Recipe Detail Page
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+$"));
        await Expect(Page.Locator(".recipe-title")).ToContainTextAsync(recipeTitle);
        await Expect(Page.Locator(".recipe-stats-strip")).ToContainTextAsync("6 hearty bowls");
        await Expect(Page.Locator(".recipe-stats-strip")).ToContainTextAsync("15 min");
        await Expect(Page.Locator(".recipe-stats-strip")).ToContainTextAsync("45 min");

        // Verify Ingredients
        await Expect(Page.Locator(".ingredient-row")).ToContainTextAsync("2 cans");
        await Expect(Page.Locator(".ingredient-row")).ToContainTextAsync("Cannellini Beans");
        await Expect(Page.Locator(".ingredient-row")).ToContainTextAsync("rinsed and drained");

        // Verify Step Timer
        await Expect(Page.Locator(".timer-badge")).ToContainTextAsync("30 min");

        // 3. Test Live 2x Scaling
        await Page.Locator("button.mult-chip", new() { HasText = "2x" }).ClickAsync();
        await Expect(Page.Locator(".ingredient-row")).ToContainTextAsync("4 cans");

        // 4. Test Live 0.5x Scaling
        await Page.Locator("button.mult-chip", new() { HasText = "0.5x" }).ClickAsync();
        await Expect(Page.Locator(".ingredient-row")).ToContainTextAsync("1 can");
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task UnresolvedRecipeComponent_IsVisibleAndCanBeLinkedLater()
    {
        await RegisterAndLoginAsync();

        var recipeTitle = $"Blueberry Pie {Guid.NewGuid():N}"[..24];
        const string referencedRecipeTitle = "Foolproof All-Butter Dough";
        var recipeId = await Page.EvaluateAsync<string>(
            """
            async ({ recipeTitle, referencedRecipeTitle }) => {
              const tokenKey = Object.keys(localStorage).find((key) => key.endsWith('-token'));
              const token = tokenKey ? localStorage.getItem(tokenKey) : null;
              const response = await fetch('/api/recipes', {
                method: 'POST',
                headers: {
                  'Content-Type': 'application/json',
                  Authorization: `Bearer ${token}`,
                },
                body: JSON.stringify({
                  title: recipeTitle,
                  status: 'Published',
                  baseServings: 8,
                  ingredients: [
                    {
                      quantity: 1,
                      unit: 'Custom',
                      customUnitName: 'recipe',
                      displayName: referencedRecipeTitle,
                      unresolvedRecipeTitle: referencedRecipeTitle,
                    },
                  ],
                  steps: [],
                }),
              });
              if (!response.ok) {
                throw new Error(`Failed to create test recipe: ${await response.text()}`);
              }
              return (await response.json()).id;
            }
            """,
            new { recipeTitle, referencedRecipeTitle }
        );

        await Page.SetViewportSizeAsync(360, 800);
        await Page.GotoAsync($"{ServerUrl}/recipes/{recipeId}");
        var warning = Page.Locator(".unresolved-recipe-reference");
        await Expect(warning).ToContainTextAsync(referencedRecipeTitle);
        await Expect(warning).ToContainTextAsync("Edit this recipe to link it");

        await Page.GotoAsync($"{ServerUrl}/recipes/{recipeId}/edit");
        await Expect(Page.Locator(".unresolved-recipe-hint"))
            .ToContainTextAsync(referencedRecipeTitle);
        var autoInput = Page.Locator("app-recipe-autocomplete input").First;
        await Expect(autoInput).ToBeVisibleAsync();
        await Expect(autoInput).ToHaveValueAsync(referencedRecipeTitle);
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task Recipe_FilterByStatus_AndSearchCookbook()
    {
        await RegisterAndLoginAsync();

        // Create Recipe 1: Draft
        await Page.Locator("a[routerlink='/recipes/new']").First.ClickAsync();
        var draftTitle = $"Draft Goulash {Guid.NewGuid():N}"[..22];
        await Page.Locator("input[formcontrolname='title']").FillAsync(draftTitle);
        await Page.Locator("mat-select[formcontrolname='status']").ClickAsync();
        await Page.Locator("mat-option", new() { HasText = "Draft" }).ClickAsync();
        await Page.Locator("input[formcontrolname='displayName']").First.FillAsync("Beef Shank");
        await Page.Locator("input[formcontrolname='quantity']").First.FillAsync("1");
        await Page.Locator("[formcontrolname='instruction'] .step-instruction-editable")
            .First.FillAsync("Brown meat in batches.");
        await Page.Locator("button[type='submit']", new() { HasText = "Save Recipe" })
            .First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+$"));

        // Create Recipe 2: Published
        await Page.Locator("nav.desktop-nav a", new() { HasText = "Cookbook" }).ClickAsync();
        await Page.Locator("a[routerlink='/recipes/new']").First.ClickAsync();
        var pubTitle = $"Published Risotto {Guid.NewGuid():N}"[..24];
        await Page.Locator("input[formcontrolname='title']").FillAsync(pubTitle);
        await Page.Locator("mat-select[formcontrolname='status']").ClickAsync();
        await Page.Locator("mat-option", new() { HasText = "Published" }).ClickAsync();
        await Page.Locator("input[formcontrolname='displayName']").First.FillAsync("Arborio Rice");
        await Page.Locator("input[formcontrolname='quantity']").First.FillAsync("2");
        await Page.Locator("[formcontrolname='instruction'] .step-instruction-editable")
            .First.FillAsync("Stir constantly with hot stock.");
        await Page.Locator("button[type='submit']", new() { HasText = "Save Recipe" })
            .First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+$"));

        // Go to Cookbook list
        await Page.Locator("nav.desktop-nav a", new() { HasText = "Cookbook" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes$"));

        // Default status is Published, so Published recipe is visible and Draft is hidden
        await Expect(Page.Locator(".recipe-card", new() { HasText = pubTitle })).ToBeVisibleAsync();
        await Expect(Page.Locator(".recipe-card", new() { HasText = draftTitle }))
            .Not.ToBeVisibleAsync();

        // Filter by Draft / Review
        await Page.Locator("button.filter-toggle-btn").ClickAsync();
        await Page.Locator("mat-select", new() { HasText = "Published" }).ClickAsync();
        await Page.Locator("mat-option", new() { HasText = "Draft / Review" }).ClickAsync();
        await Expect(Page.Locator("mat-option")).ToHaveCountAsync(0);
        await Expect(Page.Locator(".recipe-card", new() { HasText = draftTitle }))
            .ToBeVisibleAsync();
        await Expect(Page.Locator(".recipe-card", new() { HasText = pubTitle }))
            .Not.ToBeVisibleAsync();

        // Switch filter to All Recipes
        await Page.Locator("mat-select", new() { HasText = "Draft / Review" }).ClickAsync();
        await Page.Locator("mat-option", new() { HasText = "All Recipes" }).ClickAsync();
        await Expect(Page.Locator(".recipe-card", new() { HasText = draftTitle }))
            .ToBeVisibleAsync();
        await Expect(Page.Locator(".recipe-card", new() { HasText = pubTitle })).ToBeVisibleAsync();

        // Search by keyword
        var searchInput = Page.Locator(".search-field input");
        await searchInput.FillAsync(pubTitle);
        await Expect(Page.Locator(".recipe-card", new() { HasText = pubTitle })).ToBeVisibleAsync();
        await Expect(Page.Locator(".recipe-card", new() { HasText = draftTitle }))
            .Not.ToBeVisibleAsync();

        await searchInput.FillAsync(
            pubTitle.Replace("Risotto", "Risoto", StringComparison.Ordinal)
        );
        var pubCard = Page.Locator(".recipe-card", new() { HasText = pubTitle });
        await Expect(pubCard).ToBeVisibleAsync();

        var cardLink = pubCard.Locator("a.recipe-card-link");
        await Expect(cardLink).ToBeVisibleAsync();
        await Expect(cardLink).ToHaveAttributeAsync("href", new Regex("^/recipes/[a-f0-9-]+$"));

        var cardHref = await cardLink.GetAttributeAsync("href");
        await pubCard.ScrollIntoViewIfNeededAsync();
        var hitTargetClass = await pubCard.EvaluateAsync<string>(
            """
            (card) => {
              const rect = card.getBoundingClientRect();
              const el = document.elementFromPoint(rect.left + rect.width / 2, rect.top + rect.height * 0.65);
              return el?.closest('a')?.className ?? '';
            }
            """
        );
        Assert.IsTrue(
            hitTargetClass.Contains("recipe-card-link", StringComparison.Ordinal),
            $"Expected card hit target to resolve to recipe-card-link, got '{hitTargetClass}'."
        );

        var editLink = pubCard.Locator("a[aria-label='Edit recipe']");
        await Expect(editLink).ToBeVisibleAsync();
        await editLink.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex($".*{Regex.Escape(cardHref!)}/edit$"));
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task Recipe_EditExistingRecipe_UpdatesSuccessfullyAndRendersDetail()
    {
        await RegisterAndLoginAsync();

        // 1. Create a new recipe
        await Page.Locator("a[routerlink='/recipes/new']").First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/new.*"));

        var initialTitle = $"Original Skillet Cornbread {Guid.NewGuid():N}"[..28];
        await Page.Locator("input[formcontrolname='title']").FillAsync(initialTitle);
        await Page.Locator("input[formcontrolname='baseServings']").FillAsync("4");
        await Page.Locator("input[formcontrolname='yieldDisplay']").FillAsync("4 wedges");
        await Page.Locator("textarea[formcontrolname='description']")
            .FillAsync("Initial cornbread description before edit.");

        // First Ingredient
        await Page.Locator("input[formcontrolname='displayName']")
            .First.FillAsync("Yellow Cornmeal");
        await Page.Locator("input[formcontrolname='quantity']").First.FillAsync("1.5");
        await Page.Locator("mat-select[formcontrolname='unit']").First.ClickAsync();
        await Page.Locator("mat-option", new() { HasText = "Cup" }).First.ClickAsync();

        // First Step
        await Page.Locator("[formcontrolname='instruction'] .step-instruction-editable")
            .First.FillAsync("Whisk cornmeal with dry ingredients.");

        // Save
        await Page.Locator("button[type='submit']", new() { HasText = "Save Recipe" })
            .First.ClickAsync();

        // 2. Verify Detail Page
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+$"));
        await Expect(Page.Locator(".recipe-title")).ToContainTextAsync(initialTitle);
        await Expect(Page.Locator(".ingredient-row")).ToContainTextAsync("Yellow Cornmeal");

        // 3. Click Edit
        await Page.Locator(".recipe-actions-menu-btn").ClickAsync();
        await Page.Locator("a[aria-label='Edit recipe']").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+/edit$"));
        await Expect(Page.Locator(".page-title")).ToContainTextAsync("Edit Recipe");

        // 4. Modify Title and Servings
        var updatedTitle = $"Crispy Cast-Iron Gluten-Free Cornbreadd {Guid.NewGuid():N}"[..38];
        await Page.Locator("input[formcontrolname='title']").FillAsync(updatedTitle);
        await Page.Locator("input[formcontrolname='baseServings']").FillAsync("8");
        await Page.Locator("input[formcontrolname='yieldDisplay']").FillAsync("8 wedges");

        // Add a second ingredient
        await Page.Locator("button", new() { HasText = "Add Ingredient" }).ClickAsync();
        await Page.Locator("input[formcontrolname='displayName']").Nth(1).FillAsync("Buttermilk");
        await Page.Locator("input[formcontrolname='quantity']").Nth(1).FillAsync("1.25");
        await Page.Locator("mat-select[formcontrolname='unit']").Nth(1).ClickAsync();
        await Page.Locator("mat-option", new() { HasText = "Cup" }).First.ClickAsync();

        // 5. Save Edited Recipe
        await Page.Locator("button[type='submit']", new() { HasText = "Save Recipe" })
            .First.ClickAsync();

        // 6. Verify Updated Detail Page
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+$"));
        await Expect(Page.Locator(".recipe-title")).ToContainTextAsync(updatedTitle);
        await Expect(Page.Locator(".recipe-stats-strip")).ToContainTextAsync("8 wedges");
        await Expect(Page.Locator(".ingredient-row", new() { HasText = "Yellow Cornmeal" }))
            .ToBeVisibleAsync();
        await Expect(Page.Locator(".ingredient-row", new() { HasText = "Buttermilk" }))
            .ToBeVisibleAsync();
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task RecipeEditor_SubRecipeAutofill_FiltersByTypedTitleAndLinksOnSelection()
    {
        await RegisterAndLoginAsync();

        var suffix = Guid.NewGuid().ToString("N")[..6];
        var targetSubTitle = $"All-Butter Pie Dough {suffix}";
        var unrelatedTitle = $"Spicy Black Bean Chili {suffix}";

        await Page.EvaluateAsync(
            """
            async ({ targetSubTitle, unrelatedTitle }) => {
              const tokenKey = Object.keys(localStorage).find((key) => key.endsWith('-token'));
              const token = tokenKey ? localStorage.getItem(tokenKey) : null;
              for (const title of [targetSubTitle, unrelatedTitle]) {
                const response = await fetch('/api/recipes', {
                  method: 'POST',
                  headers: {
                    'Content-Type': 'application/json',
                    Authorization: `Bearer ${token}`,
                  },
                  body: JSON.stringify({
                    title,
                    status: 'Published',
                    baseServings: 6,
                    yieldDisplay: '1 batch',
                    ingredients: [{ quantity: 1, unit: 'Cup', displayName: 'Flour' }],
                    steps: [{ instruction: 'Mix thoroughly.' }],
                  }),
                });
                if (!response.ok) {
                  throw new Error(`Failed to seed recipe: ${await response.text()}`);
                }
              }
            }
            """,
            new { targetSubTitle, unrelatedTitle }
        );

        await Page.SetViewportSizeAsync(360, 800);
        await Page.GotoAsync($"{ServerUrl}/recipes/new");

        var parentTitle = $"Apple Galette {suffix}";
        await Page.Locator("input[formcontrolname='title']").FillAsync(parentTitle);
        await Page.Locator("input[formcontrolname='baseServings']").FillAsync("6");

        var firstIng = Page.Locator(".builder-item-card").First;
        await firstIng.Locator("mat-checkbox.mode-checkbox").ClickAsync();

        var subInput = firstIng.Locator("app-recipe-autocomplete input");
        await Expect(subInput).ToBeVisibleAsync();
        await subInput.FillAsync($"butter dough {suffix}");

        var matchingOption = Page.Locator("mat-option", new() { HasText = targetSubTitle }).First;
        await Expect(matchingOption).ToBeVisibleAsync();
        await Expect(Page.Locator("mat-option", new() { HasText = unrelatedTitle }))
            .ToHaveCountAsync(0);

        await matchingOption.ClickAsync();
        await Expect(subInput).ToHaveValueAsync(targetSubTitle);

        await firstIng.Locator("input[formcontrolname='quantity']").FillAsync("1");
        await Page.Locator("[formcontrolname='instruction'] .step-instruction-editable")
            .First.FillAsync("Roll out the dough and bake.");

        Assert.IsTrue(
            await Page.EvaluateAsync<bool>(
                "document.documentElement.scrollWidth <= window.innerWidth"
            ),
            "Viewport horizontal overflow detected at 360px on Recipe Editor."
        );

        await Page.Locator("button[type='submit']", new() { HasText = "Save Recipe" })
            .First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+$"));
        await Expect(Page.Locator(".sub-recipe-inline-panel")).ToContainTextAsync(targetSubTitle);

        // Verify Edit Recipe loads the linked recipe title right in the autofill input
        await Page.GotoAsync($"{Page.Url}/edit");
        var editSubInput = Page.Locator("app-recipe-autocomplete input").First;
        await Expect(editSubInput).ToBeVisibleAsync();
        await Expect(editSubInput).ToHaveValueAsync(targetSubTitle);
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task RecipeEditor_RedesignedControls_PasteListTagFilterDisclosureReorderAndErrorStates()
    {
        await RegisterAndLoginAsync();
        await EnsureTagExistsViaApiAsync("Vegetarian", "Dietary", 1);
        await EnsureTagExistsViaApiAsync("Italian", "Cuisine", 2);
        await EnsureFoodItemExistsViaApiAsync("All-Purpose Flour", isPantryStaple: true);

        // 1. Missing recipe edit route displays a clean load error instead of a stuck spinner
        await Page.GotoAsync($"{ServerUrl}/recipes/{Guid.NewGuid()}/edit");
        await Expect(Page.Locator(".load-error-card")).ToBeVisibleAsync();
        await Expect(Page.Locator("button[aria-label='Save recipe']")).ToBeDisabledAsync();

        // 2. Navigate to New Recipe and verify tag filter + hidden manage buttons by default
        await Page.GotoAsync($"{ServerUrl}/recipes/new");
        await Expect(Page.Locator(".page-title")).ToContainTextAsync("New Recipe");
        await Expect(Page.Locator(".chip-manage-btn")).ToHaveCountAsync(0);

        // Filter tags and select Vegetarian
        var tagFilter = Page.Locator("input[aria-label='Filter tags']");
        await tagFilter.FillAsync("veg");
        await Expect(
                Page.Locator(".tags-scroll-well mat-chip-option", new() { HasText = "Vegetarian" })
            )
            .ToBeVisibleAsync();
        await Expect(
                Page.Locator(".tags-scroll-well mat-chip-option", new() { HasText = "Italian" })
            )
            .ToHaveCountAsync(0);
        await Page.Locator(".tags-scroll-well mat-chip-option", new() { HasText = "Vegetarian" })
            .ClickAsync();
        await Expect(Page.Locator(".selected-taxonomy-bar")).ToContainTextAsync("Vegetarian");

        // Toggle Manage mode to confirm edit/delete controls appear on demand
        await Page.Locator("button.manage-toggle-btn").ClickAsync();
        await Expect(Page.Locator(".chip-manage-btn").First).ToBeVisibleAsync();
        await Page.Locator("button.manage-toggle-btn").ClickAsync();
        await Expect(Page.Locator(".chip-manage-btn")).ToHaveCountAsync(0);

        // 3. Clicking Save with empty required fields surfaces actionable validation jump chips
        await Page.Locator("button[type='submit']", new() { HasText = "Save Recipe" })
            .First.ClickAsync();
        await Expect(Page.Locator(".editor-error-banner")).ToBeVisibleAsync();
        await Expect(
                Page.Locator(
                    ".validation-jump-chip",
                    new() { HasText = "Recipe title is required" }
                )
            )
            .ToBeVisibleAsync();

        // Fill title & timing (auto-computed hint)
        var recipeTitle = $"Blueberry Galette {Guid.NewGuid():N}"[..24];
        await Page.Locator("input[formcontrolname='title']").FillAsync(recipeTitle);
        await Page.Locator("input[formcontrolname='prepTimeMinutes']").FillAsync("20");
        await Page.Locator("input[formcontrolname='cookTimeMinutes']").FillAsync("40");
        await Expect(Page.Locator(".basics-timing-grid")).ToContainTextAsync("Auto: 60 min");

        // 4. Bulk-paste multi-section ingredients & verify catalog auto-linking + section dividers
        await Page.Locator("button.paste-toggle-btn").ClickAsync();
        await Page.Locator("textarea[aria-label='Paste ingredient lines']")
            .FillAsync("Crust:\n2 cups All-Purpose Flour, sifted\nFilling:\n1-2 tbsp Sugar");
        await Page.Locator(".quick-paste-actions button[color='primary']").ClickAsync();

        await Expect(Page.Locator(".section-group-divider", new() { HasText = "Crust" }))
            .ToBeVisibleAsync();
        await Expect(Page.Locator(".section-group-divider", new() { HasText = "Filling" }))
            .ToBeVisibleAsync();

        var firstIng = Page.Locator(".ingredient-card").First;
        await Expect(firstIng.Locator(".catalog-linked-pill"))
            .ToContainTextAsync("All-Purpose Flour");

        // 5. Expand Alt measurement on #1, fill it, then reorder #2 above #1 and verify state stays synced
        await firstIng
            .Locator("button.option-toggle-pill", new() { HasText = "Alt measurement" })
            .ClickAsync();
        await Expect(firstIng.Locator(".alt-measurement-panel")).ToBeVisibleAsync();
        var altEditor = firstIng.Locator("app-measurement-editor");
        await altEditor.Locator("input[formcontrolname='quantity']").FillAsync("240");
        await altEditor.Locator("mat-select[formcontrolname='unit']").ClickAsync();
        await Page.Locator("mat-option", new() { HasText = "Gram (g)" }).First.ClickAsync();
        await firstIng
            .Locator("mat-checkbox[formcontrolname='preferAlternativeMeasurement'] input")
            .CheckAsync();
        await Expect(
                firstIng.Locator(
                    "mat-checkbox[formcontrolname='preferAlternativeMeasurement'] input"
                )
            )
            .ToBeCheckedAsync();

        var secondIng = Page.Locator(".ingredient-card").Nth(1);
        await secondIng.Locator("button[aria-label='Move ingredient up']").ClickAsync();

        // Now Sugar is #1 (no alt panel) and All-Purpose Flour is #2 (with alt panel & prefer checked)
        await Expect(
                Page.Locator(".ingredient-card")
                    .First.Locator("input[formcontrolname='displayName']")
            )
            .ToHaveValueAsync("Sugar");
        await Expect(Page.Locator(".ingredient-card").First.Locator(".alt-measurement-panel"))
            .ToHaveCountAsync(0);
        await Expect(
                Page.Locator(".ingredient-card")
                    .Nth(1)
                    .Locator("input[formcontrolname='displayName']")
            )
            .ToHaveValueAsync("All-Purpose Flour");
        await Expect(Page.Locator(".ingredient-card").Nth(1).Locator(".alt-measurement-panel"))
            .ToBeVisibleAsync();
        await Expect(
                Page.Locator(".ingredient-card")
                    .Nth(1)
                    .Locator("mat-checkbox[formcontrolname='preferAlternativeMeasurement'] input")
            )
            .ToBeCheckedAsync();

        // 6. Add step instruction & verify 360px mobile layout has zero horizontal overflow
        await Page.Locator("[formcontrolname='instruction'] .step-instruction-editable")
            .First.FillAsync("Fold dough around filling and bake until golden.");

        await Page.SetViewportSizeAsync(360, 740);
        Assert.IsTrue(
            await Page.EvaluateAsync<bool>(
                "document.documentElement.scrollWidth <= window.innerWidth"
            ),
            "Redesigned Recipe Editor must not overflow horizontally at 360px."
        );

        // 7. Save and verify detail page renders sections, preferred alt measurement, and tag
        await Page.Locator("button[type='submit']", new() { HasText = "Save Recipe" })
            .First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+$"));
        await Expect(Page.Locator(".recipe-title")).ToContainTextAsync(recipeTitle);
        await Expect(Page.Locator(".hero-taxonomy-strip")).ToContainTextAsync("Vegetarian");
        await Expect(Page.Locator(".ingredient-row", new() { HasText = "All-Purpose Flour" }))
            .ToContainTextAsync("240 g");
    }
}

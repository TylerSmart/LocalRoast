using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace App.Client.E2ETests;

[TestClass]
public sealed class StepTokensE2ETests : E2ETestBase
{
    private static readonly Regex TwoX = new(@"^\s*2x\s*$");

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

    private Task<string> SeedPancakesRecipeAsync(string title)
    {
        var payload = new
        {
            title,
            baseServings = 4,
            ingredients = new object[]
            {
                new
                {
                    displayName = "butter",
                    quantity = 1,
                    unit = "Tsp",
                },
            },
            steps = new object[]
            {
                new
                {
                    instruction = "Top pancakes with ||{\"ingredient\":\"butter\",\"amount\":1,\"unit\":\"teaspoon\"}||.",
                },
            },
        };

        return CreateRecipeViaApiAsync(payload);
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task StepTokens_InlineIngredient_ScalesCorrectlyInCookMode()
    {
        await RegisterAndLoginAsync();
        var recipeId = await SeedPancakesRecipeAsync(
            $"Grandma's Pancakes {Guid.NewGuid():N}"[..25]
        );

        await Page.GotoAsync($"{ServerUrl}/recipes/{recipeId}/cook");
        var instruction = Page.Locator(".instruction-text");
        await Expect(instruction).ToContainTextAsync("1 tsp butter");
        await Expect(Page.Locator(".step-callouts-box")).ToHaveCountAsync(0);

        var chip = Page.Locator(".mult-chip", new() { HasTextRegex = TwoX });
        await chip.ClickAsync();
        await Expect(chip).ToHaveClassAsync(new Regex("is-active"));

        var amountChip = instruction.Locator(".inline-amount[data-kind=Ingredient]");
        await Expect(amountChip).ToHaveTextAsync("2 tsp butter");
        await Expect(amountChip).ToHaveClassAsync(new Regex("is-scaled"));

        // Verify tooltip on hover shows ingredient name
        var titleAttr = await amountChip.GetAttributeAsync("title");
        Assert.AreEqual("butter", titleAttr);
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task StepTokens_ReviewAndEditInUI_DialogUpdatesToken()
    {
        await RegisterAndLoginAsync();

        await Page.GotoAsync($"{ServerUrl}/recipes/new");
        await Expect(Page.Locator(".page-title")).ToContainTextAsync("New Recipe");

        // Add ingredient Eggs
        await Page.GetByRole(AriaRole.Button, new() { Name = "Add Ingredient", Exact = false })
            .ClickAsync();
        var ingCard = Page.Locator(".ingredient-row-card, .builder-item-card").First;
        await ingCard.Locator("input[formcontrolname='displayName']").FillAsync("Eggs");

        // Add Step
        await Page.GetByRole(AriaRole.Button, new() { Name = "Add Step", Exact = true })
            .ClickAsync();
        var stepCard = Page.Locator(".builder-list").Last.Locator(".builder-item-card").Last;
        var editor = stepCard.Locator("[formcontrolname='instruction']");
        var editable = editor.Locator(".step-instruction-editable");
        await editable.ClickAsync();
        await Page.Keyboard.TypeAsync("Whisk ");

        // Click + Ingredient
        await stepCard.Locator(".add-ingredient-btn").ClickAsync();
        var dialog = Page.Locator("app-step-token-dialog");
        await Expect(dialog).ToBeVisibleAsync();

        // Fill dialog fields
        await dialog.Locator("input[formcontrolname='ingredient']").FillAsync("Eggs");
        await dialog.Locator("input[formcontrolname='amount']").FillAsync("4");
        await dialog.Locator("input[formcontrolname='unit']").FillAsync("whole");
        // Click back to top field to dismiss unit autocomplete dropdown before clicking Insert Token
        await dialog.Locator("input[formcontrolname='ingredient']").ClickAsync();

        // Save
        await dialog.Locator("button", new() { HasText = "Insert Token" }).ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0);

        // Verify token serialized without raw JSON rendered visibly
        var val = await editor.GetAttributeAsync("data-serialized-value");
        Assert.IsNotNull(val);
        Assert.Contains(
            "||{\"type\":\"ingredient\",\"ingredient\":\"Eggs\",\"amount\":4,\"unit\":\"whole\"}||",
            val
        );

        var visibleText = await editable.InnerTextAsync();
        Assert.DoesNotContain(
            "||{",
            visibleText,
            "Raw token JSON must not appear in hybrid editor."
        );

        // Verify inline chip inside editable surface
        var inlineChip = editable.Locator(".inline-step-chip.is-ingredient");
        await Expect(inlineChip).ToBeVisibleAsync();
        await Expect(inlineChip).ToContainTextAsync("4 Eggs");

        // Continue typing after chip and insert timer & temperature chips dynamically
        await Page.Keyboard.TypeAsync("and cook ");
        await stepCard.Locator(".add-timer-btn").ClickAsync();
        await Expect(dialog).ToBeVisibleAsync();
        var minutesInput = dialog.Locator("input[formcontrolname='minutes']");
        var labelInput = dialog.Locator("input[formcontrolname='label']");
        await Expect(minutesInput).ToBeFocusedAsync();
        await minutesInput.FillAsync("3");
        await labelInput.FillAsync("Scramble");
        await Expect(minutesInput).ToHaveValueAsync("3");
        await Expect(labelInput).ToHaveValueAsync("Scramble");
        await dialog.Locator("button", new() { HasText = "Insert Token" }).ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0);
        await Expect(editable.Locator(".inline-step-chip.is-timer")).ToContainTextAsync("3m");

        await stepCard.Locator(".add-temp-btn").ClickAsync();
        await Expect(dialog).ToBeVisibleAsync();
        await dialog.Locator("input[formcontrolname='fahrenheit']").FillAsync("325");
        await dialog.Locator("button", new() { HasText = "Insert Token" }).ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0);
        await Expect(editable.Locator(".inline-step-chip.is-temperature"))
            .ToContainTextAsync("325°F");

        // Click chip edit button to edit ingredient amount
        await inlineChip.Locator(".chip-edit-btn").ClickAsync();
        await Expect(dialog).ToBeVisibleAsync();
        await dialog.Locator("input[formcontrolname='amount']").FillAsync("6");
        await dialog.Locator("button", new() { HasText = "Update Token" }).ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0);

        // Verify token updated in serialized value
        val = await editor.GetAttributeAsync("data-serialized-value");
        Assert.IsNotNull(val);
        Assert.Contains("\"amount\":6", val);
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task StepTokens_MobileViewport_360px_RendersCorrectly()
    {
        await Page.SetViewportSizeAsync(360, 640);
        await RegisterAndLoginAsync();
        var recipeId = await SeedPancakesRecipeAsync($"Mobile Pancakes {Guid.NewGuid():N}"[..22]);

        await Page.GotoAsync($"{ServerUrl}/recipes/{recipeId}/cook");
        var instruction = Page.Locator(".instruction-text");
        await Expect(instruction).ToBeVisibleAsync();

        var fits = await instruction.EvaluateAsync<bool>(
            "el => el.scrollWidth <= el.clientWidth + 1"
        );
        Assert.IsTrue(fits, "Instruction text must wrap without horizontal scrolling on 360px.");

        var pageOverflow = await Page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth"
        );
        Assert.IsFalse(pageOverflow, "Cook Mode at 360px must not overflow horizontally.");

        var chip = instruction.Locator(".inline-amount");
        var box = await chip.BoundingBoxAsync();
        Assert.IsNotNull(box);
        Assert.IsGreaterThanOrEqualTo(box.Height, 24);
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task RecipeDetail_ContiguousSections_FixedRate_SubRecipeBatch_AndIngredientSync_WorkEndToEnd()
    {
        await Page.SetViewportSizeAsync(1280, 800);
        await RegisterAndLoginAsync();

        var suffix = Guid.NewGuid().ToString("N")[..6];
        var subRecipeAId = await CreateRecipeViaApiAsync(
            new
            {
                title = $"Herb Dressing {suffix}",
                baseServings = 4,
                yieldDisplay = "Makes 1 cup",
                ingredients = new object[]
                {
                    new
                    {
                        displayName = "olive oil",
                        quantity = 2,
                        unit = "Tsp",
                    },
                },
                steps = new object[] { new { instruction = "Whisk oil." } },
            }
        );

        var subRecipeBId = await CreateRecipeViaApiAsync(
            new
            {
                title = $"Braised Beef {suffix}",
                baseServings = 4,
                yieldDisplay = "Serves 4",
                ingredients = new object[]
                {
                    new
                    {
                        displayName = "kosher salt",
                        quantity = 2,
                        unit = "Tsp",
                    },
                },
                steps = new object[] { new { instruction = "Season beef." } },
            }
        );

        var parentId = await CreateRecipeViaApiAsync(
            new
            {
                title = $"Plated Beef Bowl {suffix}",
                baseServings = 4,
                ingredients = new object[]
                {
                    new
                    {
                        sectionName = (string?)null,
                        displayName = "unsalted butter",
                        quantity = 1,
                        unit = "Tsp",
                    },
                    new
                    {
                        sectionName = "Sauce",
                        displayName = $"Herb Dressing {suffix}",
                        quantity = 0.5m,
                        unit = "Cup",
                        linkedRecipeId = subRecipeAId,
                    },
                    new
                    {
                        sectionName = (string?)null,
                        displayName = $"Braised Beef {suffix}",
                        quantity = 4,
                        unit = "Custom",
                        customUnitName = "servings",
                        linkedRecipeId = subRecipeBId,
                    },
                },
                steps = new object[]
                {
                    new
                    {
                        sectionName = (string?)null,
                        instruction = "Add ||{\"type\":\"ingredient\",\"ingredient\":\"unsalted butter\",\"amount\":1,\"unit\":\"tsp\",\"ingredientIndex\":0}||, whisking ||{\"type\":\"fixed\",\"amount\":1,\"unit\":\"tbsp\",\"hyphenated\":false}|| at a time.",
                    },
                    new
                    {
                        sectionName = "Sauce",
                        instruction = "Drizzle ||{\"type\":\"ingredient\",\"ingredient\":\"dressing\",\"ingredientIndex\":1}|| over top.",
                    },
                    new
                    {
                        sectionName = (string?)null,
                        instruction = "Serve over ||{\"type\":\"ingredient\",\"ingredient\":\"beef\",\"ingredientIndex\":2}||.",
                    },
                },
            }
        );

        await Page.GotoAsync($"{ServerUrl}/recipes/{parentId}");

        // 1. Mixed contiguous [null, "Sauce", null] sections & continuation divider
        var ingGroups = Page.Locator(".ingredient-section-group");
        await Expect(ingGroups).ToHaveCountAsync(3);
        await Expect(ingGroups.Nth(0).Locator(".group-subtitle")).ToHaveCountAsync(0);
        await Expect(ingGroups.Nth(1).Locator(".group-subtitle")).ToHaveTextAsync("Sauce");
        await Expect(ingGroups.Nth(2)).ToHaveClassAsync(new Regex("section-continuation-divider"));
        await Expect(ingGroups.Nth(2).Locator(".group-subtitle")).ToHaveCountAsync(0);

        var stepGroups = Page.Locator(".step-section-group");
        await Expect(stepGroups).ToHaveCountAsync(3);
        await Expect(stepGroups.Nth(1).Locator(".group-subtitle")).ToHaveTextAsync("Sauce");
        await Expect(stepGroups.Nth(2)).ToHaveClassAsync(new Regex("section-continuation-divider"));

        // 2. ingredientIndex -> ingredientId binding & interactive hover sync
        var butterRow = Page.Locator("li.ingredient-row").First;
        var butterRowId = await butterRow.GetAttributeAsync("data-ingredient-id");
        Assert.IsFalse(string.IsNullOrWhiteSpace(butterRowId));

        var step1Linked = stepGroups
            .Nth(0)
            .Locator(".step-instruction-text .inline-amount.is-linked");
        await Expect(step1Linked).ToHaveTextAsync("1 tsp unsalted butter");
        await Expect(step1Linked).ToHaveAttributeAsync("data-ingredient-id", butterRowId);

        await step1Linked.HoverAsync();
        await Expect(Page.Locator($"li.ingredient-row[data-ingredient-id='{butterRowId}']"))
            .ToHaveClassAsync(new Regex("is-highlighted"));

        var dressingRowId = await Page.Locator("li.ingredient-row")
            .Nth(1)
            .GetAttributeAsync("data-ingredient-id");
        Assert.IsFalse(string.IsNullOrWhiteSpace(dressingRowId));

        var step2Linked = stepGroups
            .Nth(1)
            .Locator(
                ".step-instruction-text .inline-amount.is-linked.is-subrecipe.is-unquantified"
            );
        await Expect(step2Linked).ToHaveTextAsync("dressing");
        await Expect(step2Linked).ToHaveAttributeAsync("data-ingredient-id", dressingRowId);
        var decStyle = await step2Linked.EvaluateAsync<string>(
            "el => getComputedStyle(el).textDecorationStyle"
        );
        Assert.AreNotEqual("dotted", decStyle);

        // 3. Fixed "1 tbsp at a time" non-scaling vs ingredient scaling
        var twoXChip = Page.Locator(".mult-chip", new() { HasTextRegex = TwoX });
        await twoXChip.ClickAsync();
        await Expect(twoXChip).ToHaveClassAsync(new Regex("active-mult"));

        await Expect(step1Linked).ToHaveTextAsync("2 tsp unsalted butter");
        await Expect(step1Linked).ToHaveClassAsync(new Regex("is-scaled"));
        var fixedToken = stepGroups.Nth(0).Locator(".step-instruction-text .fixed-amount.is-fixed");
        await Expect(fixedToken).ToHaveTextAsync("1 tbsp");

        var oneXChip = Page.Locator(".mult-chip", new() { HasTextRegex = new(@"^\s*1x\s*$") });
        await oneXChip.ClickAsync();
        await Expect(oneXChip).ToHaveClassAsync(new Regex("active-mult"));

        // 4. Sub-recipe yield batch multiplier scaling (0.5x cup vs 4 servings = 1x batch)
        var subPanels = Page.Locator(".sub-recipe-inline-panel");
        await Expect(subPanels).ToHaveCountAsync(2);

        var herbPanel = subPanels.Nth(0);
        await Expect(herbPanel.Locator(".sub-batch-pill")).ToHaveTextAsync("(0.5x batch)");
        await herbPanel.Locator("mat-expansion-panel-header").ClickAsync();
        await Expect(herbPanel.Locator(".sub-ingredients-list"))
            .ToContainTextAsync("1 tsp olive oil");

        var beefPanel = subPanels.Nth(1);
        await Expect(beefPanel.Locator(".sub-batch-pill")).ToHaveCountAsync(0);
        await beefPanel.Locator("mat-expansion-panel-header").ClickAsync();
        await Expect(beefPanel.Locator(".sub-ingredients-list"))
            .ToContainTextAsync("2 tsp kosher salt");
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task CookMode_MobileViewport_360px_ClickingLinkedIngredientOpensDrawerAndHighlights()
    {
        await Page.SetViewportSizeAsync(360, 800);
        await RegisterAndLoginAsync();

        var suffix = Guid.NewGuid().ToString("N")[..6];
        var recipeId = await CreateRecipeViaApiAsync(
            new
            {
                title = $"Mobile Sync {suffix}",
                baseServings = 4,
                ingredients = new object[]
                {
                    new
                    {
                        displayName = "unsalted butter",
                        quantity = 1,
                        unit = "Tsp",
                    },
                },
                steps = new object[]
                {
                    new
                    {
                        instruction = "Melt ||{\"type\":\"ingredient\",\"ingredient\":\"unsalted butter\",\"amount\":1,\"unit\":\"tsp\",\"ingredientIndex\":0}|| gently.",
                    },
                },
            }
        );

        await Page.GotoAsync($"{ServerUrl}/recipes/{recipeId}/cook");
        await Expect(Page.Locator(".mobile-ingredients-drawer")).ToHaveCountAsync(0);

        var linkedChip = Page.Locator(".instruction-text .inline-amount.is-linked");
        await Expect(linkedChip).ToBeVisibleAsync();
        await linkedChip.ClickAsync();

        var drawer = Page.Locator(".mobile-ingredients-drawer");
        await Expect(drawer).ToBeVisibleAsync();

        var highlighted = drawer.Locator(".drawer-body li.ingredient-item.is-highlighted");
        await Expect(highlighted).ToHaveCountAsync(1);
        await Expect(highlighted).ToContainTextAsync("unsalted butter");

        var pageOverflow = await Page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth"
        );
        Assert.IsFalse(pageOverflow, "Cook Mode at 360px must not overflow horizontally.");
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task StepTokens_FixedIngredientCheckbox_RoomyDialogAmount_AndSingleParenPrepNote_WorkEndToEnd()
    {
        await Page.SetViewportSizeAsync(1280, 900);
        await RegisterAndLoginAsync();

        var suffix = Guid.NewGuid().ToString("N")[..6];

        // 1. Seed a recipe via API whose prepNote arrived double-parenthesized "(8-inch square)"
        // and verify both Recipe Detail display & Cook Mode render a single pair "(8-inch square)".
        var prepRecipeId = await CreateRecipeViaApiAsync(
            new
            {
                title = $"Nori Rolls {suffix}",
                baseServings = 4,
                ingredients = new object[]
                {
                    new
                    {
                        displayName = "nori sheet",
                        quantity = 1,
                        unit = "Custom",
                        customUnitName = "sheet",
                        preparationNote = "(8-inch square)",
                    },
                    new
                    {
                        displayName = "unsalted butter",
                        quantity = 4,
                        unit = "Tbsp",
                    },
                },
                steps = new object[]
                {
                    new
                    {
                        instruction = "Spread ||{\"type\":\"ingredient\",\"ingredient\":\"unsalted butter\",\"amount\":1,\"unit\":\"tbsp\",\"ingredientIndex\":1,\"fixed\":true}|| on each roll.",
                    },
                },
            }
        );

        await Page.GotoAsync($"{ServerUrl}/recipes/{prepRecipeId}");
        var firstIngText = await Page.Locator("li.ingredient-row").First.InnerTextAsync();
        Assert.Contains("(8-inch square)", firstIngText);
        Assert.DoesNotContain("((8-inch square))", firstIngText);

        // Fixed ingredient token stays "1 tbsp" at 2x, links to butter row, and converts to Metric.
        var butterRowId = await Page.Locator("li.ingredient-row")
            .Nth(1)
            .GetAttributeAsync("data-ingredient-id");
        Assert.IsFalse(string.IsNullOrWhiteSpace(butterRowId));

        var fixedIngChip = Page.Locator(
            ".step-instruction-text .inline-amount.is-fixed-ingredient"
        );
        await Expect(fixedIngChip).ToHaveTextAsync("1 tbsp unsalted butter");
        await Expect(fixedIngChip).ToHaveAttributeAsync("data-ingredient-id", butterRowId);

        var twoXChip = Page.Locator(".mult-chip", new() { HasTextRegex = TwoX });
        await twoXChip.ClickAsync();
        await Expect(fixedIngChip).ToHaveTextAsync("1 tbsp unsalted butter");

        await fixedIngChip.HoverAsync();
        await Expect(Page.Locator($"li.ingredient-row[data-ingredient-id='{butterRowId}']"))
            .ToHaveClassAsync(new Regex("is-highlighted"));

        var metricChip = Page.Locator(
            ".unit-sys-chip, mat-button-toggle",
            new() { HasTextRegex = new(@"^\s*Metric\s*$") }
        );
        if (await metricChip.CountAsync() > 0)
        {
            await metricChip.First.ClickAsync();
            await Expect(fixedIngChip).ToContainTextAsync("15 ml unsalted butter");
        }

        // 2. Open /recipes/new and verify Insert Ingredient Token dialog Amount input is not squished
        // and the Fixed measurement checkbox toggles and serializes "fixed":true.
        await Page.GotoAsync($"{ServerUrl}/recipes/new");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Add Ingredient", Exact = false })
            .ClickAsync();
        var ingCard = Page.Locator(".ingredient-row-card, .builder-item-card").First;
        await ingCard.Locator("input[formcontrolname='displayName']").FillAsync("unsalted butter");
        await ingCard.Locator("input[formcontrolname='quantity']").FillAsync("4");
        await ingCard.Locator("mat-select[formcontrolname='unit']").ClickAsync();
        await Page.Locator(
                "mat-option",
                new() { HasTextRegex = new(@"^\s*Tablespoon\b", RegexOptions.IgnoreCase) }
            )
            .ClickAsync();

        await Page.GetByRole(AriaRole.Button, new() { Name = "Add Step", Exact = true })
            .ClickAsync();
        var stepCard = Page.Locator(".builder-list").Last.Locator(".builder-item-card").Last;
        var editor = stepCard.Locator("[formcontrolname='instruction']");
        var editable = editor.Locator(".step-instruction-editable");
        await editable.ClickAsync();
        await Page.Keyboard.TypeAsync("Brush ");

        await stepCard.Locator(".add-ingredient-btn").ClickAsync();
        var dialog = Page.Locator("app-step-token-dialog");
        await Expect(dialog).ToBeVisibleAsync();

        await dialog.Locator(".quick-chip-btn").First.ClickAsync();
        var amountInput = dialog.Locator("input[formcontrolname='amount']");
        var amountBox = await amountInput.BoundingBoxAsync();
        Assert.IsNotNull(amountBox);
        Assert.IsGreaterThanOrEqualTo(
            90,
            amountBox.Width,
            $"Expected un-squished Amount input width >= 90px, actual {amountBox.Width}"
        );

        await amountInput.FillAsync("1");
        await dialog.Locator("mat-checkbox[formcontrolname='fixed']").ClickAsync();
        await dialog.Locator("button", new() { HasText = "Insert Token" }).ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0);

        var serialized = await editor.GetAttributeAsync("data-serialized-value");
        Assert.IsNotNull(serialized);
        Assert.Contains("\"fixed\":true", serialized);
        await Expect(editable.Locator(".inline-step-chip.is-fixed-ingredient"))
            .ToContainTextAsync("[fixed]");
    }
}

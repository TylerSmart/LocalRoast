using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace App.Client.E2ETests;

[TestClass]
public sealed class StepTokensE2ETests : E2ETestBase
{
    private static readonly Regex TwoX = new(@"^\s*2x\s*$");

    private async Task<string> SeedPancakesRecipeAsync(string title)
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
                    quantity = 4,
                    unit = "Tsp",
                },
            },
            steps = new object[]
            {
                new
                {
                    instruction = "Top each of the 4 pancakes with ||{\"ingredient\":\"butter\",\"amount\":4,\"unit\":\"teaspoon\",\"displayAmount\":1,\"displayUnit\":\"teaspoon\"}||.",
                    ingredientIndices = new[] { 0 },
                },
            },
        };

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
    public async Task StepTokens_PancakePerItemDisplay_ScalesCorrectlyInCookMode()
    {
        await RegisterAndLoginAsync();
        var recipeId = await SeedPancakesRecipeAsync(
            $"Grandma's Pancakes {Guid.NewGuid():N}"[..25]
        );

        await Page.GotoAsync($"{ServerUrl}/recipes/{recipeId}/cook");
        var instruction = Page.Locator(".instruction-text");
        await Expect(instruction).ToContainTextAsync("1 tsp butter");

        var chip = Page.Locator(".mult-chip", new() { HasTextRegex = TwoX });
        await chip.ClickAsync();
        await Expect(chip).ToHaveClassAsync(new Regex("is-active"));

        var amountChip = instruction.Locator(".inline-amount[data-kind=Ingredient]");
        await Expect(amountChip).ToHaveTextAsync("2 tsp butter");
        await Expect(amountChip).ToHaveClassAsync(new Regex("is-scaled"));

        // Verify tooltip on hover
        var titleAttr = await amountChip.GetAttributeAsync("title");
        Assert.IsTrue(
            titleAttr != null
                && (
                    titleAttr.Contains("2 tbsp + 2 tsp butter")
                    || titleAttr.Contains("8 tsp butter")
                ),
            $"Expected tooltip to mention scaled total butter, got: {titleAttr}"
        );
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

        // Toggle custom display amount
        await dialog.Locator("mat-checkbox[formcontrolname='useDisplayAmount']").ClickAsync();
        await dialog.Locator("input[formcontrolname='displayAmount']").FillAsync("2");
        await dialog.Locator("input[formcontrolname='displayUnit']").FillAsync("whole");

        // Save
        await dialog.Locator("button", new() { HasText = "Insert Token" }).ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0);

        // Verify token serialized without raw JSON rendered visibly
        var val = await editor.GetAttributeAsync("data-serialized-value");
        Assert.IsNotNull(val);
        Assert.Contains(
            "||{\"ingredient\":\"Eggs\",\"amount\":4,\"unit\":\"whole\",\"displayAmount\":2,\"displayUnit\":\"whole\"}||",
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
        await Expect(inlineChip).ToContainTextAsync("2 Eggs");

        // Continue typing after chip and insert timer & temperature chips dynamically
        await Page.Keyboard.TypeAsync("and cook ");
        await stepCard.Locator(".add-timer-btn").ClickAsync();
        await Expect(dialog).ToBeVisibleAsync();
        await dialog.Locator("input[formcontrolname='minutes']").FillAsync("3");
        await dialog.Locator("input[formcontrolname='label']").FillAsync("Scramble");
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
}

using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace App.Client.E2ETests;

[TestClass]
public sealed class TaggedInstructionAmountsE2ETests : E2ETestBase
{
    private const string WhiskStep =
        "Whisk 1 cup flour with the milk, then cook until reduced to 1 cup.";
    private const string StirStep = "Stir in the remaining 1 cup flour.";

    private static readonly Regex TwoX = new(@"^\s*2x\s*$");

    private async Task<string> SeedTaggedRecipeAsync(string title)
    {
        var cupMeasurement = new { quantity = 1, unit = "Cup" };
        var payload = new
        {
            title,
            baseServings = 4,
            ingredients = new object[]
            {
                new
                {
                    displayName = "all-purpose flour",
                    quantity = 2,
                    unit = "Cup",
                },
                new
                {
                    displayName = "whole milk",
                    quantity = 1,
                    unit = "Cup",
                },
            },
            steps = new object[]
            {
                new
                {
                    instruction = WhiskStep,
                    ingredientIndices = new[] { 0, 1 },
                    inlineAmounts = new object[]
                    {
                        new
                        {
                            start = WhiskStep.IndexOf("1 cup", StringComparison.Ordinal),
                            length = 5,
                            kind = "Ingredient",
                            ingredientIndex = (int?)0,
                            measurement = cupMeasurement,
                        },
                        new
                        {
                            start = WhiskStep.LastIndexOf("1 cup", StringComparison.Ordinal),
                            length = 5,
                            kind = "Fixed",
                            ingredientIndex = (int?)null,
                            measurement = cupMeasurement,
                        },
                    },
                },
                new
                {
                    instruction = StirStep,
                    ingredientIndices = new[] { 0 },
                    inlineAmounts = new object[]
                    {
                        new
                        {
                            start = StirStep.IndexOf("1 cup", StringComparison.Ordinal),
                            length = 5,
                            kind = "Ingredient",
                            ingredientIndex = (int?)0,
                            measurement = cupMeasurement,
                        },
                    },
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
    public async Task InlineAmounts_RecipeDetail_ScalesChipsAndKeepsFixedText()
    {
        await RegisterAndLoginAsync();
        var recipeId = await SeedTaggedRecipeAsync($"Tagged Roux {Guid.NewGuid():N}"[..20]);

        await Page.GotoAsync($"{ServerUrl}/recipes/{recipeId}");
        var stepTexts = Page.Locator(".step-instruction-text");
        await Expect(stepTexts).ToHaveCountAsync(2);
        await Expect(stepTexts.Nth(0)).ToHaveTextAsync(WhiskStep);
        await Expect(stepTexts.Nth(1)).ToHaveTextAsync(StirStep);

        var firstStepChips = stepTexts.Nth(0).Locator(".inline-amount");
        await Expect(firstStepChips).ToHaveCountAsync(1);
        await Expect(firstStepChips.First).ToHaveAttributeAsync("data-kind", "Ingredient");
        await Expect(firstStepChips.First).ToHaveAttributeAsync("title", "all-purpose flour");

        await Page.Locator(".mult-chip", new() { HasTextRegex = TwoX }).ClickAsync();
        await Expect(Page.Locator(".mult-chip", new() { HasTextRegex = TwoX }))
            .ToHaveClassAsync(new Regex("active-mult"));

        await Expect(firstStepChips.First).ToHaveTextAsync("2 cups");
        await Expect(firstStepChips.First).ToHaveClassAsync(new Regex("is-scaled"));
        await Expect(stepTexts.Nth(0).Locator(".fixed-amount[data-kind=Fixed]"))
            .ToHaveTextAsync("1 cup");
        await Expect(stepTexts.Nth(0))
            .ToHaveTextAsync("Whisk 2 cups flour with the milk, then cook until reduced to 1 cup.");
        await Expect(stepTexts.Nth(1)).ToHaveTextAsync("Stir in the remaining 2 cups flour.");

        var flourRow = Page.Locator(".ingredient-text", new() { HasText = "all-purpose flour" });
        await Expect(flourRow.Locator(".ingredient-uses"))
            .ToHaveTextAsync("Divided: 2 cups (step 1) · 2 cups (step 2)");
        var milkRow = Page.Locator(".ingredient-text", new() { HasText = "whole milk" });
        await Expect(milkRow.Locator(".ingredient-uses")).ToHaveCountAsync(0);
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task InlineAmounts_CookMode_ShowsScaledChips()
    {
        await RegisterAndLoginAsync();
        var recipeId = await SeedTaggedRecipeAsync($"Tagged Cook {Guid.NewGuid():N}"[..20]);

        await Page.GotoAsync($"{ServerUrl}/recipes/{recipeId}/cook");
        var instruction = Page.Locator(".instruction-text");
        await Expect(instruction).ToHaveTextAsync(WhiskStep);

        var chip = Page.Locator(".mult-chip", new() { HasTextRegex = TwoX });
        await chip.ClickAsync();
        await Expect(chip).ToHaveClassAsync(new Regex("is-active"));

        var amountChip = instruction.Locator(".inline-amount[data-kind=Ingredient]");
        await Expect(amountChip).ToHaveTextAsync("2 cups");
        await Expect(amountChip).ToHaveClassAsync(new Regex("is-scaled"));
        await Expect(instruction.Locator(".fixed-amount")).ToHaveTextAsync("1 cup");

        // The flour amount is already in the text, so only the untagged milk gets a callout.
        var callouts = Page.Locator(".step-callouts-box");
        await Expect(callouts).ToContainTextAsync("whole milk");
        await Expect(callouts).Not.ToContainTextAsync("flour");
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task InlineAmounts_Editor_DetectsTagsAndSaves()
    {
        await RegisterAndLoginAsync();
        var recipeId = await SeedTaggedRecipeAsync($"Tagged Edit {Guid.NewGuid():N}"[..20]);

        await Page.GotoAsync($"{ServerUrl}/recipes/{recipeId}/edit");
        var editors = Page.Locator("[data-testid=inline-amount-editor]");
        await Expect(editors).ToHaveCountAsync(2);
        await Expect(editors.Nth(0).Locator("[data-testid=inline-amount-row]")).ToHaveCountAsync(2);

        await Page.GetByRole(AriaRole.Button, new() { Name = "Add Step", Exact = true })
            .ClickAsync();
        await Expect(editors).ToHaveCountAsync(3);
        await Page.Locator("textarea[formcontrolname='instruction']")
            .Nth(2)
            .FillAsync("Add 3 tablespoons milk.");

        var newEditor = editors.Nth(2);
        await newEditor.Locator("[data-testid=detect-amounts]").ClickAsync();
        var row = newEditor.Locator("[data-testid=inline-amount-row]");
        await Expect(row).ToHaveCountAsync(1);
        await Expect(row).ToContainTextAsync("3 tablespoons");

        await row.Locator("[data-testid=inline-amount-kind]").ClickAsync();
        await Page.Locator("mat-option", new() { HasText = "Ingredient" }).ClickAsync();
        await row.Locator("[data-testid=inline-amount-ingredient]").ClickAsync();
        await Page.Locator("mat-option", new() { HasText = "2. whole milk" }).ClickAsync();
        await Expect(row.Locator("[data-testid=inline-amount-ingredient]"))
            .ToContainTextAsync("2. whole milk");

        await Page.Locator("button[type='submit']", new() { HasText = "Save Recipe" })
            .First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex($".*/recipes/{recipeId}$"));

        var stepTexts = Page.Locator(".step-instruction-text");
        await Expect(stepTexts).ToHaveCountAsync(3);
        await Expect(stepTexts.Nth(2)).ToHaveTextAsync("Add 3 tablespoons milk.");

        await Page.Locator(".mult-chip", new() { HasTextRegex = TwoX }).ClickAsync();
        var milkChip = stepTexts.Nth(2).Locator(".inline-amount[data-kind=Ingredient]");
        await Expect(milkChip).ToHaveTextAsync(new Regex(@"^6 (tbsp|tablespoons)$"));
        await Expect(milkChip).ToHaveAttributeAsync("title", "whole milk");
        // Tagged amounts from the seeded steps survive the editor round trip.
        await Expect(stepTexts.Nth(1)).ToHaveTextAsync("Stir in the remaining 2 cups flour.");
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task InlineAmounts_MobileViewport_360px_RendersCorrectly()
    {
        await Page.SetViewportSizeAsync(360, 640);
        await RegisterAndLoginAsync();
        var recipeId = await SeedTaggedRecipeAsync($"Tagged Mobile {Guid.NewGuid():N}"[..22]);

        await Page.GotoAsync($"{ServerUrl}/recipes/{recipeId}");
        await Page.Locator(".mult-chip", new() { HasTextRegex = TwoX }).ClickAsync();
        var stepTexts = Page.Locator(".step-instruction-text");
        await Expect(stepTexts.Nth(0).Locator(".inline-amount")).ToHaveTextAsync("2 cups");

        for (var i = 0; i < 2; i++)
        {
            var fits = await stepTexts
                .Nth(i)
                .EvaluateAsync<bool>("el => el.scrollWidth <= el.clientWidth");
            Assert.IsTrue(fits, $"Step {i + 1} text must wrap inside the step at 360px.");
        }
        var pageOverflow = await Page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth"
        );
        Assert.IsFalse(pageOverflow, "Recipe detail at 360px must not overflow horizontally.");

        await Page.GotoAsync($"{ServerUrl}/recipes/{recipeId}/edit");
        var row = Page.Locator("[data-testid=inline-amount-editor]")
            .First.Locator("[data-testid=inline-amount-row]")
            .First;
        await Expect(row).ToBeVisibleAsync();
        await row.ScrollIntoViewIfNeededAsync();

        var label = await row.Locator(".span-label").BoundingBoxAsync();
        var kind = await row.Locator(".kind-field").BoundingBoxAsync();
        var ingredient = await row.Locator(".ingredient-field").BoundingBoxAsync();
        Assert.IsNotNull(label);
        Assert.IsNotNull(kind);
        Assert.IsNotNull(ingredient);
        Assert.IsLessThanOrEqualTo(kind.Y + 1, label.Y + label.Height);
        Assert.IsLessThanOrEqualTo(ingredient.Y + 1, kind.Y + kind.Height);

        var editorOverflow = await Page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth"
        );
        Assert.IsFalse(editorOverflow, "Recipe editor at 360px must not overflow horizontally.");
    }
}

using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace App.Client.E2ETests;

[TestClass]
public sealed class CulinaryUnitAndUiPolishE2ETests : E2ETestBase
{
    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task RecipeDetail_ScalingSimplifiesUnits_QuadrupleTbspToQuarterCupAndHalfQuarterCupToTwoTbsp()
    {
        await RegisterAndLoginAsync();

        var title = $"Scaling Test {Guid.NewGuid():N}"[..20];
        var recipeId = await Page.EvaluateAsync<string>(
            """
            async ({ title }) => {
                const tokenKey = Object.keys(localStorage).find((key) => key.endsWith('-token'));
                const token = tokenKey ? localStorage.getItem(tokenKey) : null;
                const response = await fetch('/api/recipes', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        Authorization: `Bearer ${token}`,
                    },
                    body: JSON.stringify({
                        title: title,
                        status: 'Published',
                        baseServings: 1,
                        ingredients: [
                            {
                                displayName: 'Olive Oil',
                                quantity: 1,
                                unit: 'Tbsp',
                            },
                            {
                                displayName: 'Lemon Juice',
                                quantity: 0.25,
                                unit: 'Cup',
                            },
                        ],
                        steps: [
                            {
                                instruction: 'Mix olive oil and lemon juice.',
                                ingredientIds: [],
                            }
                        ],
                    }),
                });
                if (!response.ok) {
                    throw new Error(await response.text());
                }
                return (await response.json()).id;
            }
            """,
            new { title }
        );

        await Page.GotoAsync($"{ServerUrl}/recipes/{recipeId}");
        await Expect(Page.Locator(".recipe-title")).ToContainTextAsync(title);

        // Initial 1x scale: 1 tbsp and 1/4 cup
        await Expect(Page.Locator(".ingredient-row", new() { HasText = "Olive Oil" }))
            .ToContainTextAsync("1 tbsp");
        await Expect(Page.Locator(".ingredient-row", new() { HasText = "Lemon Juice" }))
            .ToContainTextAsync("1/4 cup");

        // Scale to 4x: Click '+' 3 times (from 1 to 4 servings)
        var increaseBtn = Page.Locator("button[aria-label='Increase servings']");
        await increaseBtn.ClickAsync();
        await increaseBtn.ClickAsync();
        await increaseBtn.ClickAsync();

        // At 4x: 1 tbsp -> 1/4 cup; 1/4 cup -> 1 cup
        await Expect(Page.Locator(".ingredient-row", new() { HasText = "Olive Oil" }))
            .ToContainTextAsync("1/4 cup");
        await Expect(Page.Locator(".ingredient-row", new() { HasText = "Lemon Juice" }))
            .ToContainTextAsync("1 cup");

        // Scale to 0.5x via multiplier chip
        await Page.Locator("button.mult-chip", new() { HasText = "0.5x" }).ClickAsync();

        // At 0.5x: 1/4 cup -> 2 tbsp; 1 tbsp -> 1 1/2 tsp
        await Expect(Page.Locator(".ingredient-row", new() { HasText = "Lemon Juice" }))
            .ToContainTextAsync("2 tbsp");
        await Expect(Page.Locator(".ingredient-row", new() { HasText = "Olive Oil" }))
            .ToContainTextAsync("1 1/2 tsp");
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task RecipeDetail_SubRecipeIngredient_RendersFullWidthExpansionPanelBelowIngredientRow()
    {
        await RegisterAndLoginAsync();

        var subTitle = $"Dough {Guid.NewGuid():N}"[..18];
        var parentTitle = $"Blueberry Pie {Guid.NewGuid():N}"[..24];

        var ids = await Page.EvaluateAsync<string[]>(
            """
            async ({ subTitle, parentTitle }) => {
                const tokenKey = Object.keys(localStorage).find((key) => key.endsWith('-token'));
                const token = tokenKey ? localStorage.getItem(tokenKey) : null;

                // Create Sub-recipe
                const subRes = await fetch('/api/recipes', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        Authorization: `Bearer ${token}`,
                    },
                    body: JSON.stringify({
                        title: subTitle,
                        status: 'Published',
                        baseServings: 1,
                        ingredients: [
                            { displayName: 'Flour', quantity: 2, unit: 'Cup' },
                            { displayName: 'Butter', quantity: 1, unit: 'Cup' }
                        ],
                        steps: [],
                    }),
                });
                if (!subRes.ok) throw new Error(await subRes.text());
                const subData = await subRes.json();

                // Create Parent recipe linking to sub-recipe
                const parentRes = await fetch('/api/recipes', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        Authorization: `Bearer ${token}`,
                    },
                    body: JSON.stringify({
                        title: parentTitle,
                        status: 'Published',
                        baseServings: 8,
                        ingredients: [
                            {
                                displayName: subTitle,
                                quantity: 1,
                                unit: 'Custom',
                                customUnitName: 'crust',
                                linkedRecipeId: subData.id,
                            },
                            {
                                displayName: 'Blueberries',
                                quantity: 4,
                                unit: 'Cup',
                            }
                        ],
                        steps: [],
                    }),
                });
                if (!parentRes.ok) throw new Error(await parentRes.text());
                const parentData = await parentRes.json();

                return [subData.id, parentData.id];
            }
            """,
            new { subTitle, parentTitle }
        );

        var parentId = ids[1];
        await Page.GotoAsync($"{ServerUrl}/recipes/{parentId}");
        await Expect(Page.Locator(".recipe-title")).ToContainTextAsync(parentTitle);

        var subPanel = Page.Locator(".sub-recipe-inline-panel").First;
        await Expect(subPanel).ToBeVisibleAsync();

        // Verify it renders on its own row below the ingredient checkbox
        var coords = await Page.EvaluateAsync<float[]>(
            """
            () => {
                const row = document.querySelector('.ingredient-row:has(.sub-recipe-inline-panel)');
                const checkbox = row.querySelector('mat-checkbox');
                const panel = row.querySelector('.sub-recipe-inline-panel');
                const cRect = checkbox.getBoundingClientRect();
                const pRect = panel.getBoundingClientRect();
                return [cRect.top, cRect.bottom, pRect.top, pRect.bottom];
            }
            """
        );
        var cTop = coords[0];
        var cBottom = coords[1];
        var pTop = coords[2];
        var pBottom = coords[3];

        Assert.IsGreaterThanOrEqualTo(
            value: pTop,
            lowerBound: cBottom - 4,
            message: $"Sub-recipe panel top ({pTop}..{pBottom}) should be below checkbox bottom ({cTop}..{cBottom})"
        );

        // Expand panel and verify contents
        await subPanel.Locator("mat-expansion-panel-header").ClickAsync();
        var content = subPanel.Locator(".sub-recipe-content");
        await Expect(content).ToBeVisibleAsync();
        await Expect(content).ToContainTextAsync("Flour");
        await Expect(content).ToContainTextAsync("Butter");
        await Expect(content.Locator(".view-sub-link")).ToBeVisibleAsync();
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task RecipeList_LongCollectionPillAndActionButtons_MobileViewport360px_NoOverflowAndCircularButtons()
    {
        await RegisterAndLoginAsync();

        const string collectionName = "The Complete America's Test Kitchen TV Show Cookbook";
        await Page.EvaluateAsync(
            """
            async ({ name }) => {
                const tokenKey = Object.keys(localStorage).find((key) => key.endsWith('-token'));
                const token = tokenKey ? localStorage.getItem(tokenKey) : null;
                await fetch('/api/collections', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        Authorization: `Bearer ${token}`,
                    },
                    body: JSON.stringify({ name, sortOrder: 0 }),
                });
            }
            """,
            new { name = collectionName }
        );

        await Page.SetViewportSizeAsync(360, 640);
        await Page.GotoAsync($"{ServerUrl}/recipes");

        var pillGroup = Page.Locator(
            ".collection-pill-group",
            new() { HasText = collectionName }
        ).First;
        await Expect(pillGroup).ToBeVisibleAsync();

        var groupBox = await pillGroup.BoundingBoxAsync();
        Assert.IsNotNull(groupBox);

        var actionBtn = pillGroup.Locator(".collection-pill-action").First;
        await Expect(actionBtn).ToBeVisibleAsync();

        var actionBox = await actionBtn.BoundingBoxAsync();
        Assert.IsNotNull(actionBox);

        // Button must remain inside the collection-pill-group bounds (no horizontal overflow)
        Assert.IsLessThanOrEqualTo(
            upperBound: groupBox.X + groupBox.Width + 2,
            value: actionBox.X + actionBox.Width,
            message: $"Action button right ({actionBox.X + actionBox.Width}) should not exceed pill group right ({groupBox.X + groupBox.Width})"
        );

        // Equal width and height (circular hover button)
        Assert.IsLessThanOrEqualTo(
            upperBound: 1.5,
            value: Math.Abs(actionBox.Width - actionBox.Height),
            message: $"Action button should be circular: width ({actionBox.Width}) vs height ({actionBox.Height})"
        );

        // Verify user profile button has compact width <= 44px
        var profileBtn = Page.Locator("[aria-label='User profile and account options']");
        await Expect(profileBtn).ToBeVisibleAsync();
        var profileBox = await profileBtn.BoundingBoxAsync();
        Assert.IsNotNull(profileBox);
        Assert.IsLessThanOrEqualTo(
            upperBound: 44,
            value: profileBox.Width,
            message: $"User profile button width ({profileBox.Width}) should be <= 44px"
        );
    }
}

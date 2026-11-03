using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace App.Client.E2ETests;

[TestClass]
public sealed class ShoppingListsE2ETests : E2ETestBase
{
    private async Task<string> CreateRecipeWithIngredientsAsync(
        string title,
        int baseServings,
        params (string Name, decimal Quantity, string Unit)[] ingredients
    )
    {
        await Page.Locator("nav.desktop-nav a", new() { HasText = "Cookbook" }).ClickAsync();
        await Page.Locator("a[routerlink='/recipes/new']").First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/new.*"));

        await Page.Locator("input[formcontrolname='title']").FillAsync(title);
        await Page.Locator("input[formcontrolname='baseServings']")
            .FillAsync(baseServings.ToString(CultureInfo.InvariantCulture));

        for (var i = 0; i < ingredients.Length; i++)
        {
            if (i > 0)
            {
                await Page.Locator("button", new() { HasText = "Add Ingredient" }).ClickAsync();
            }

            var (name, qty, unit) = ingredients[i];
            await Page.Locator("input[formcontrolname='displayName']").Nth(i).FillAsync(name);
            await Page.Locator("input[formcontrolname='quantity']")
                .Nth(i)
                .FillAsync(qty.ToString(CultureInfo.InvariantCulture));
            await Page.Locator("mat-select[formcontrolname='unit']").Nth(i).ClickAsync();
            await Page.Locator("mat-option", new() { HasText = unit }).First.ClickAsync();
        }

        await Page.Locator("textarea[formcontrolname='instruction']")
            .First.FillAsync("Cook and prepare thoroughly.");
        await Page.Locator("button[type='submit']", new() { HasText = "Save Recipe" })
            .First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+$"));

        var url = Page.Url;
        return url[(url.LastIndexOf('/') + 1)..];
    }

    [TestMethod]
    public async Task ShoppingList_GenerateFromMealPlan_ConsolidatesUnits_ExcludesLeftoversAndPantryStaples_AndPreventsDuplicates()
    {
        await RegisterAndLoginAsync();

        var recipeATitle = $"Taco Recipe {Guid.NewGuid():N}"[..18];
        var recipeBTitle = $"Salsa Recipe {Guid.NewGuid():N}"[..18];

        // 1. Create Recipe A (base 4 servings): Extra-Virgin Olive Oil (2 tbsp), Garlic (2 cloves), Yellow Onion (2 whole)
        await CreateRecipeWithIngredientsAsync(
            recipeATitle,
            4,
            ("Extra-Virgin Olive Oil", 2m, "Tablespoon"),
            ("Garlic", 2m, "Clove"),
            ("Yellow Onion", 2m, "Whole")
        );

        // Schedule Recipe A with 8 servings on Monday
        await Page.Locator("button.recipe-actions-menu-btn").ClickAsync();
        var planMealBtn = Page.Locator("button[aria-label='Plan meal']");
        await planMealBtn.ClickAsync();

        var planDialog = Page.Locator("mat-dialog-container");
        await Expect(planDialog.Locator("h2")).ToContainTextAsync("Plan a Meal");
        await planDialog.Locator("input[formcontrolname='targetServings']").FillAsync("8");
        await planDialog
            .Locator("button[color='primary']", new() { HasText = "Plan Meal" })
            .ClickAsync();
        await Expect(planDialog).Not.ToBeVisibleAsync();

        // 2. Create Recipe B (base 4 servings): Extra-Virgin Olive Oil (0.25 cup), Garlic (3 cloves), Yellow Onion (1 cup)
        await CreateRecipeWithIngredientsAsync(
            recipeBTitle,
            4,
            ("Extra-Virgin Olive Oil", 0.25m, "Cup"),
            ("Garlic", 3m, "Clove"),
            ("Yellow Onion", 1m, "Cup")
        );

        // Put Recipe B in On-Deck
        await Page.Locator("button.recipe-actions-menu-btn").ClickAsync();
        await Page.Locator("button[aria-label='Plan meal']").ClickAsync();
        var ondeckDialog = Page.Locator("mat-dialog-container");
        await Expect(ondeckDialog.Locator("h2")).ToContainTextAsync("Plan a Meal");
        await ondeckDialog.Locator("mat-button-toggle[value='ondeck'] button").ClickAsync();
        await ondeckDialog
            .Locator("button[color='primary']", new() { HasText = "Plan Meal" })
            .ClickAsync();
        await Expect(ondeckDialog).Not.ToBeVisibleAsync();

        // 3. Navigate to Meal Plan
        await Page.Locator("nav.desktop-nav a", new() { HasText = "Meal Plan" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/meal-plan.*"));

        // Add Leftovers dish
        await Page.Locator("button.add-meal-primary-btn").ClickAsync();
        var leftoverDialog = Page.Locator("mat-dialog-container");
        await leftoverDialog.Locator("mat-button-toggle[value='Leftovers'] button").ClickAsync();
        await leftoverDialog
            .Locator("input[formcontrolname='customTitle']")
            .FillAsync("Leftovers: Taco Night");
        await leftoverDialog
            .Locator("button[color='primary']", new() { HasText = "Plan Meal" })
            .ClickAsync();
        await Expect(leftoverDialog).Not.ToBeVisibleAsync();

        // Add Freeform Note dish
        await Page.Locator("button.add-meal-primary-btn").ClickAsync();
        var noteDialog = Page.Locator("mat-dialog-container");
        await noteDialog.Locator("mat-button-toggle[value='FreeformNote'] button").ClickAsync();
        await noteDialog.Locator("input[formcontrolname='customTitle']").FillAsync("Friday Pizza");
        await noteDialog
            .Locator("button[color='primary']", new() { HasText = "Plan Meal" })
            .ClickAsync();
        await Expect(noteDialog).Not.ToBeVisibleAsync();

        // 4. Click "Shop Week"
        var shopWeekBtn = Page.Locator("button.shop-week-btn");
        await Expect(shopWeekBtn).ToBeVisibleAsync();
        await shopWeekBtn.ClickAsync();

        var genDialog = Page.Locator("mat-dialog-container");
        await Expect(genDialog.Locator("h2")).ToContainTextAsync("Generate Shopping List");

        // Verify preview reflects eligible meals and ignored leftovers/notes
        await Expect(genDialog.Locator(".preview-card")).ToContainTextAsync("Meals");
        await Expect(genDialog.Locator(".ignored-note")).ToContainTextAsync("leftovers");
        await Expect(genDialog.Locator(".ignored-note")).ToContainTextAsync("freeform notes");

        // Generate into Weekly Groceries
        await genDialog.Locator("button", new() { HasText = "Generate Items" }).ClickAsync();
        await Expect(genDialog).Not.ToBeVisibleAsync();

        // 5. Navigate to Shopping Workspace
        await Page.Locator("nav.desktop-nav a", new() { HasText = "Shop" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/shopping.*"));
        await Expect(Page.Locator(".page-title")).ToContainTextAsync("Shopping Lists");

        // 6. Verify Active Items
        var activeCard = Page.Locator(".active-items-card");

        // Garlic: 2 cloves * 2 (from 8/4 scale) + 3 cloves = 7 cloves
        var garlicRow = activeCard.Locator(".shopping-item-row", new() { HasText = "Garlic" });
        await Expect(garlicRow).ToBeVisibleAsync();
        await Expect(garlicRow.Locator(".item-qty-badge")).ToContainTextAsync("7 cloves");
        await Expect(garlicRow.Locator(".provenance-chip")).ToHaveCountAsync(2);

        // Yellow Onion: 4 whole (Recipe A 2x scale) and 1 cup (Recipe B) must be separate rows
        var onionRows = activeCard.Locator(
            ".shopping-item-row",
            new() { HasText = "Yellow Onion" }
        );
        await Expect(onionRows).ToHaveCountAsync(2);
        await Expect(onionRows.Filter(new() { HasText = "1 cup" })).ToBeVisibleAsync();
        await Expect(onionRows.Filter(new() { HasNotText = "1 cup" }).Locator(".item-qty-badge"))
            .ToContainTextAsync("4");

        // Leftovers & Notes must not be present in active items
        await Expect(activeCard.Locator(".shopping-item-row", new() { HasText = "Leftovers" }))
            .Not.ToBeVisibleAsync();
        await Expect(activeCard.Locator(".shopping-item-row", new() { HasText = "Friday Pizza" }))
            .Not.ToBeVisibleAsync();

        // 7. Verify Pantry Staples Excluded Drawer
        var pantryDrawer = Page.Locator(".pantry-staples-drawer");
        await Expect(pantryDrawer).ToBeVisibleAsync();
        await Expect(pantryDrawer).ToContainTextAsync("Verify Pantry Staples (Excluded) (1)");

        // Expand drawer
        await pantryDrawer.Locator("button.drawer-header-btn").ClickAsync();

        // Extra-Virgin Olive Oil: 2 tbsp * 2 = 4 tbsp = 1/4 cup + 1/4 cup = 1/2 cup
        var evooRow = pantryDrawer.Locator(
            ".pantry-item-row",
            new() { HasText = "Extra-Virgin Olive Oil" }
        );
        await Expect(evooRow).ToBeVisibleAsync();
        await Expect(evooRow.Locator(".item-qty-badge")).ToContainTextAsync("1/2 cup");

        // 1-Tap Promote Pantry Staple to Active Checklist
        await evooRow.Locator("button.promote-staple-btn").ClickAsync();

        // Extra-Virgin Olive Oil is now active
        var activeEvoo = activeCard.Locator(
            ".shopping-item-row",
            new() { HasText = "Extra-Virgin Olive Oil" }
        );
        await Expect(activeEvoo).ToBeVisibleAsync();
        await Expect(activeEvoo.Locator(".item-qty-badge")).ToContainTextAsync("1/2 cup");

        // 8. Re-open Generate Dialog to test Idempotent Duplicate Detection
        await Page.Locator("button.generate-btn").ClickAsync();
        var reGenDialog = Page.Locator("mat-dialog-container");
        await Expect(reGenDialog.Locator("h2")).ToContainTextAsync("Generate Shopping List");
        await Expect(reGenDialog.Locator(".duplicate-meals-banner")).ToBeVisibleAsync();
        await Expect(reGenDialog.Locator(".duplicate-meals-banner"))
            .ToContainTextAsync("Already Imported Meals");

        await reGenDialog.Locator("button", new() { HasText = "Cancel" }).ClickAsync();
        await Expect(reGenDialog).Not.ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task ShoppingList_MultipleNamedLists_QuickAdd_Edit_CheckOffAndClear()
    {
        await RegisterAndLoginAsync();

        await Page.Locator("nav.desktop-nav a", new() { HasText = "Shop" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/shopping.*"));

        // 1. Create a new list "Costco Run"
        await Page.Locator("button.new-list-btn").ClickAsync();
        var listDialog = Page.Locator("mat-dialog-container");
        await Expect(listDialog.Locator("h2")).ToContainTextAsync("New Shopping List");

        await listDialog.Locator("input[formcontrolname='name']").FillAsync("Costco Run");
        await listDialog
            .Locator("button[type='submit']", new() { HasText = "Create" })
            .ClickAsync();
        await Expect(listDialog).Not.ToBeVisibleAsync();

        // Verify Costco Run is active in pills
        var costcoPill = Page.Locator(".desktop-list-pills .list-pill.active");
        await Expect(costcoPill).ToContainTextAsync("Costco Run");

        // 2. Quick-add items
        var quickInput = Page.Locator(".quick-add-bar input");
        await quickInput.FillAsync("Paper Towels");
        await Page.Locator("button.quick-add-submit-btn").ClickAsync();

        var activeCard = Page.Locator(".active-items-card");
        await Expect(activeCard.Locator(".shopping-item-row", new() { HasText = "Paper Towels" }))
            .ToBeVisibleAsync();

        await quickInput.FillAsync("Rotisserie Chicken");
        await Page.Locator("button.quick-add-submit-btn").ClickAsync();
        await Expect(
                activeCard.Locator(".shopping-item-row", new() { HasText = "Rotisserie Chicken" })
            )
            .ToBeVisibleAsync();

        // 3. Edit "Paper Towels" with quantity and notes
        var paperTowelsRow = activeCard.Locator(
            ".shopping-item-row",
            new() { HasText = "Paper Towels" }
        );
        await paperTowelsRow.Locator("button[aria-label='Edit Paper Towels']").ClickAsync();

        var itemDialog = Page.Locator("mat-dialog-container");
        await Expect(itemDialog.Locator("h2")).ToContainTextAsync("Edit Item");
        await itemDialog.Locator("input[formcontrolname='quantity']").FillAsync("2");
        await itemDialog
            .Locator("input[formcontrolname='notes']")
            .FillAsync("Bulk pack from aisle 4");
        await itemDialog.Locator("button", new() { HasText = "Save Changes" }).ClickAsync();
        await Expect(itemDialog).Not.ToBeVisibleAsync();

        // Verify updated badge and notes
        await Expect(paperTowelsRow.Locator(".item-qty-badge")).ToContainTextAsync("2");
        await Expect(paperTowelsRow.Locator(".item-notes"))
            .ToContainTextAsync("Bulk pack from aisle 4");

        // 4. Check off "Paper Towels"
        await paperTowelsRow.Locator("mat-checkbox").ClickAsync();

        // Verify moved into Checked Off section
        var checkedSection = Page.Locator(".checked-items-section");
        await Expect(checkedSection).ToBeVisibleAsync();
        await Expect(checkedSection).ToContainTextAsync("Checked Off (1)");
        await Expect(
                checkedSection.Locator(".shopping-item-row", new() { HasText = "Paper Towels" })
            )
            .ToBeVisibleAsync();

        // Rotisserie Chicken stays active
        await Expect(
                activeCard.Locator(".shopping-item-row", new() { HasText = "Rotisserie Chicken" })
            )
            .ToBeVisibleAsync();

        // 5. Clear Checked Items
        await checkedSection.Locator("button.clear-checked-btn").ClickAsync();
        await Expect(checkedSection).Not.ToBeVisibleAsync();
        await Expect(
                activeCard.Locator(".shopping-item-row", new() { HasText = "Rotisserie Chicken" })
            )
            .ToBeVisibleAsync();

        // 6. Switch back to "Weekly Groceries" and verify independent list
        var weeklyPill = Page.Locator(
            ".desktop-list-pills .list-pill",
            new() { HasText = "Weekly Groceries" }
        );
        await weeklyPill.ClickAsync();
        await Expect(Page.Locator(".desktop-list-pills .list-pill.active"))
            .ToContainTextAsync("Weekly Groceries");
        await Expect(
            activeCard.Locator(".shopping-item-row", new() { HasText = "Rotisserie Chicken" })
        )
            .Not.ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task ShoppingList_AddFromRecipeDetail_AndMobileViewport_360px_RendersCleanly()
    {
        await RegisterAndLoginAsync();

        var recipeTitle = $"Mojito Chicken {Guid.NewGuid():N}"[..18];

        // 1. Create Recipe with 2 Whole Limes (base 4 servings)
        await CreateRecipeWithIngredientsAsync(recipeTitle, 4, ("Limes", 2m, "Whole"));

        // 2. On Recipe Detail: scale servings to 8 via 2x multiplier and click "Add to List"
        await Page.Locator("button.mult-chip", new() { HasText = "2x" }).ClickAsync();

        await Page.Locator("button.recipe-actions-menu-btn").ClickAsync();
        var addToListBtn = Page.Locator("button[aria-label='Add ingredients to shopping list']");
        await Expect(addToListBtn).ToBeVisibleAsync();
        await addToListBtn.ClickAsync();

        var addDialog = Page.Locator("mat-dialog-container");
        await Expect(addDialog.Locator("h2")).ToContainTextAsync("Add to Shopping List");

        await addDialog.Locator("button", new() { HasText = "Add Ingredients" }).ClickAsync();
        await Expect(addDialog).Not.ToBeVisibleAsync();

        // 3. Switch to 360x640 mobile viewport
        await Page.SetViewportSizeAsync(360, 640);

        // Navigate via mobile bottom navigation
        var shopTab = Page.Locator("nav.mobile-bottom-nav a[aria-label='Shop']");
        await Expect(shopTab).ToBeVisibleAsync();
        await shopTab.ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex(".*/shopping.*"));
        await Expect(shopTab).ToHaveClassAsync(new Regex(".*active-tab.*"));

        // Verify zero horizontal overflow at 360px
        var scrollWidth = await Page.EvaluateAsync<int>(
            "() => document.documentElement.scrollWidth"
        );
        var innerWidth = await Page.EvaluateAsync<int>("() => window.innerWidth");
        Assert.IsLessThanOrEqualTo(
            scrollWidth,
            innerWidth,
            $"Page horizontally overflows at 360px: scrollWidth={scrollWidth}, innerWidth={innerWidth}"
        );

        // 4. Verify "Limes" scaled to 4 whole on active checklist
        var activeCard = Page.Locator(".active-items-card");
        var limesRow = activeCard.Locator(".shopping-item-row", new() { HasText = "Limes" });
        await Expect(limesRow).ToBeVisibleAsync();
        await Expect(limesRow.Locator(".item-qty-badge")).ToContainTextAsync("4");

        // 5. Quick-add "Avocados" at 360px
        await Page.Locator(".quick-add-bar input").FillAsync("Avocados");
        await Page.Locator("button.quick-add-submit-btn").ClickAsync();

        var avoRow = activeCard.Locator(".shopping-item-row", new() { HasText = "Avocados" });
        await Expect(avoRow).ToBeVisibleAsync();

        // 6. Check off Avocados with touch-friendly checkbox
        await avoRow.Locator("mat-checkbox").ClickAsync();

        var checkedSection = Page.Locator(".checked-items-section");
        await Expect(checkedSection).ToBeVisibleAsync();
        await Expect(checkedSection).ToContainTextAsync("Checked Off (1)");

        // 7. Verify horizontal overflow remains zero after dynamic interactions
        scrollWidth = await Page.EvaluateAsync<int>("() => document.documentElement.scrollWidth");
        innerWidth = await Page.EvaluateAsync<int>("() => window.innerWidth");
        Assert.IsLessThanOrEqualTo(
            scrollWidth,
            innerWidth,
            $"Page horizontally overflows at 360px after interactions: scrollWidth={scrollWidth}, innerWidth={innerWidth}"
        );
    }
}

using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace App.Client.E2ETests;

[TestClass]
public sealed class MealPlanE2ETests : E2ETestBase
{
    private async Task CreateRecipeAsync(string title, int baseServings = 4)
    {
        await Page.Locator("nav.desktop-nav a", new() { HasText = "Cookbook" }).ClickAsync();
        await Page.Locator("a[routerlink='/recipes/new']").First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/new.*"));

        await Page.Locator("input[formcontrolname='title']").FillAsync(title);
        await Page.Locator("input[formcontrolname='baseServings']")
            .FillAsync(baseServings.ToString());
        await Page.Locator("input[formcontrolname='displayName']")
            .First.FillAsync("Main Ingredient");
        await Page.Locator("input[formcontrolname='quantity']").First.FillAsync("1");
        await Page.Locator("textarea[formcontrolname='instruction']")
            .First.FillAsync("Prepare and cook thoroughly.");

        await Page.Locator("button[type='submit']", new() { HasText = "Save Recipe" })
            .First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/[a-f0-9-]+$"));
    }

    [TestMethod]
    public async Task MealPlan_ScheduleFromRecipeDetail_AndAddMultiDishLeftoversAndNotes()
    {
        await RegisterAndLoginAsync();

        var chiliTitle = $"Chili {Guid.NewGuid():N}"[..18];
        var breadTitle = $"Cornbread {Guid.NewGuid():N}"[..18];

        // 1. Create Sunday Chili (base 4 servings)
        await CreateRecipeAsync(chiliTitle, 4);

        // 2. Click "Plan Meal" directly on Recipe Detail
        await Page.Locator("button.recipe-actions-menu-btn").ClickAsync();
        var planMealBtn = Page.Locator("button[aria-label='Plan meal']");
        await planMealBtn.ClickAsync();

        var dialog = Page.Locator("mat-dialog-container");
        await Expect(dialog.Locator("h2")).ToContainTextAsync("Plan a Meal");

        // Increase servings from 4 to 6 via stepper (2 clicks of +1)
        var incBtn = dialog.Locator("button[aria-label='Increase servings']");
        await incBtn.ClickAsync();
        await incBtn.ClickAsync();

        // Set placement to On-Deck This Week
        await dialog.Locator("mat-button-toggle[value='ondeck'] button").ClickAsync();

        // Submit
        await dialog
            .Locator("button[color='primary']", new() { HasText = "Plan Meal" })
            .ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();

        // 3. Navigate to Meal Plan
        var mealPlanNav = Page.Locator("nav.desktop-nav a", new() { HasText = "Meal Plan" });
        await mealPlanNav.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/meal-plan.*"));
        await Expect(Page.Locator(".page-title")).ToContainTextAsync("Meal Plan");

        // 4. Verify Sunday Chili appears in On-Deck This Week sidebar
        var ondeckCard = Page.Locator(
            ".ondeck-sidebar .ondeck-card",
            new() { HasText = chiliTitle }
        );
        await Expect(ondeckCard).ToBeVisibleAsync();
        await Expect(ondeckCard.Locator(".servings-badge")).ToContainTextAsync("6 srv");

        // 5. Click "Cook Tonight" on the On-Deck card
        await ondeckCard.Locator("button.cook-tonight-btn").ClickAsync();

        // Chili moves from On-Deck to today's Dinner slot
        await Expect(ondeckCard).Not.ToBeVisibleAsync();
        var todayCol = Page.Locator(".day-column.is-today");
        await Expect(todayCol).ToBeVisibleAsync();
        var scheduledChili = todayCol.Locator(".meal-entry-card", new() { HasText = chiliTitle });
        await Expect(scheduledChili).ToBeVisibleAsync();
        await Expect(scheduledChili.Locator(".servings-badge")).ToContainTextAsync("6 srv");

        // 6. Create Cornbread recipe
        await CreateRecipeAsync(breadTitle, 4);

        // Navigate back to Meal Plan
        await Page.Locator("nav.desktop-nav a", new() { HasText = "Meal Plan" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/meal-plan.*"));

        // 7. Add Cornbread to the same Dinner slot (multi-dish verification)
        var addDishBtn = Page.Locator("button.add-meal-primary-btn");
        await addDishBtn.ClickAsync();

        var addDishDialog = Page.Locator("mat-dialog-container");
        await Expect(addDishDialog.Locator("h2")).ToContainTextAsync("Plan a Meal");
        await addDishDialog
            .Locator(
                "app-searchable-select[formcontrolname='recipeId'], mat-select[formcontrolname='recipeId']"
            )
            .ClickAsync();
        await Page.Locator("mat-option", new() { HasText = breadTitle }).ClickAsync();
        await addDishDialog
            .Locator("button[color='primary']", new() { HasText = "Plan Meal" })
            .ClickAsync();
        await Expect(addDishDialog).Not.ToBeVisibleAsync();

        // Verify both dishes are present in the scheduled meal plan (distinguishing Recipe from Leftovers)
        await Expect(
                Page.Locator(".meal-entry-card .meal-title-link", new() { HasText = chiliTitle })
            )
            .ToBeVisibleAsync();
        await Expect(
                Page.Locator(".meal-entry-card .meal-title-link", new() { HasText = breadTitle })
            )
            .ToBeVisibleAsync();

        // 8. Add Leftovers entry
        await Page.Locator("button.add-meal-primary-btn").ClickAsync();
        var leftoverDialog = Page.Locator("mat-dialog-container");
        await leftoverDialog.Locator("mat-button-toggle[value='Leftovers'] button").ClickAsync();
        await leftoverDialog
            .Locator("input[formcontrolname='customTitle']")
            .FillAsync($"Leftovers: {chiliTitle}");
        await leftoverDialog
            .Locator("button[color='primary']", new() { HasText = "Plan Meal" })
            .ClickAsync();
        await Expect(leftoverDialog).Not.ToBeVisibleAsync();

        // Verify Leftovers badge
        var leftoverCard = Page.Locator(
            ".meal-entry-card",
            new() { HasText = $"Leftovers: {chiliTitle}" }
        );
        await Expect(leftoverCard).ToBeVisibleAsync();
        await Expect(leftoverCard.Locator(".leftovers-badge")).ToContainTextAsync("Leftovers");

        // 9. Add FreeformNote entry
        await Page.Locator("button.add-meal-primary-btn").ClickAsync();
        var noteDialog = Page.Locator("mat-dialog-container");
        await noteDialog.Locator("mat-button-toggle[value='FreeformNote'] button").ClickAsync();
        await noteDialog
            .Locator("input[formcontrolname='customTitle']")
            .FillAsync("Friday Takeout");
        await noteDialog
            .Locator("button[color='primary']", new() { HasText = "Plan Meal" })
            .ClickAsync();
        await Expect(noteDialog).Not.ToBeVisibleAsync();

        // Verify Note badge
        var noteCard = Page.Locator(".meal-entry-card", new() { HasText = "Friday Takeout" });
        await Expect(noteCard).ToBeVisibleAsync();
        await Expect(noteCard.Locator(".note-badge")).ToContainTextAsync("Note");

        // 10. Reload and verify persistence
        await Page.ReloadAsync();
        await Expect(
                Page.Locator(".meal-entry-card .meal-title-link", new() { HasText = chiliTitle })
            )
            .ToBeVisibleAsync();
        await Expect(
                Page.Locator(".meal-entry-card .meal-title-link", new() { HasText = breadTitle })
            )
            .ToBeVisibleAsync();
        await Expect(
                Page.Locator(".meal-entry-card", new() { HasText = $"Leftovers: {chiliTitle}" })
            )
            .ToBeVisibleAsync();
        await Expect(Page.Locator(".meal-entry-card", new() { HasText = "Friday Takeout" }))
            .ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task MealPlan_WeekTemplates_SaveCurrentWeek_NavigateAndApplyMergeAndReplace()
    {
        await RegisterAndLoginAsync();
        await Page.Locator("nav.desktop-nav a", new() { HasText = "Meal Plan" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/meal-plan.*"));

        // Navigate 1 week ahead to an isolated, fresh week
        await Page.Locator("button[aria-label='Next period']").ClickAsync();
        await Expect(Page.Locator(".meal-entry-card")).ToHaveCountAsync(0);

        // 1. Add an On-Deck meal to this week
        var addOnDeckBtn = Page.Locator(
            ".ondeck-sidebar button[aria-label='Add meal to On-Deck tray']"
        );
        await addOnDeckBtn.ClickAsync();

        var deckDialog = Page.Locator("mat-dialog-container");
        await deckDialog.Locator("mat-button-toggle[value='FreeformNote'] button").ClickAsync();
        await deckDialog
            .Locator("input[formcontrolname='customTitle']")
            .FillAsync("Prep Salad Mix");
        await deckDialog
            .Locator("button[color='primary']", new() { HasText = "Plan Meal" })
            .ClickAsync();
        await Expect(deckDialog).Not.ToBeVisibleAsync();
        await Expect(Page.Locator(".ondeck-card", new() { HasText = "Prep Salad Mix" }))
            .ToBeVisibleAsync();

        // 2. Add a scheduled meal to this week
        await Page.Locator("button.add-meal-primary-btn").ClickAsync();
        var schedDialog = Page.Locator("mat-dialog-container");
        await schedDialog.Locator("mat-button-toggle[value='FreeformNote'] button").ClickAsync();
        await schedDialog.Locator("input[formcontrolname='customTitle']").FillAsync("Monday Tacos");
        await schedDialog
            .Locator("button[color='primary']", new() { HasText = "Plan Meal" })
            .ClickAsync();
        await Expect(schedDialog).Not.ToBeVisibleAsync();
        await Expect(Page.Locator(".meal-entry-card", new() { HasText = "Monday Tacos" }))
            .ToBeVisibleAsync();

        // 3. Open Templates dialog and save current week
        await Page.Locator("button.template-action-btn").ClickAsync();
        var tplDialog = Page.Locator("mat-dialog-container");
        await Expect(tplDialog.Locator("h2")).ToContainTextAsync("Meal Plan Templates");

        // Switch to "Save Current Week as Template" tab
        await tplDialog
            .Locator(".mat-mdc-tab", new() { HasText = "Save Current Week" })
            .ClickAsync();
        var tplName = $"Busy Fall Week {Guid.NewGuid():N}"[..20];
        await tplDialog.Locator("input[formcontrolname='name']").FillAsync(tplName);
        await tplDialog
            .Locator("textarea[formcontrolname='description']")
            .FillAsync("Tacos and prep");
        await tplDialog
            .Locator("button[type='submit']", new() { HasText = "Save as Template" })
            .ClickAsync();

        // Switch to Saved Templates tab to verify newly created template
        await tplDialog.Locator(".mat-mdc-tab", new() { HasText = "Saved Templates" }).ClickAsync();
        var tplCard = tplDialog.Locator(".template-card", new() { HasText = tplName });
        await Expect(tplCard).ToBeVisibleAsync();
        await Expect(tplCard.Locator(".item-badge").First).ToContainTextAsync("2 total meals");

        // Close templates dialog
        await tplDialog.Locator("button", new() { HasText = "Close" }).ClickAsync();
        await Expect(tplDialog).Not.ToBeVisibleAsync();

        // 4. Navigate another week ahead
        await Page.Locator("button[aria-label='Next period']").ClickAsync();
        await Expect(Page.Locator(".meal-entry-card")).ToHaveCountAsync(0);

        // 5. Open Templates dialog again and Apply in Merge mode
        await Page.Locator("button.template-action-btn").ClickAsync();
        var applyDialog = Page.Locator("mat-dialog-container");
        var targetCard = applyDialog.Locator(".template-card", new() { HasText = tplName });
        await Expect(targetCard).ToBeVisibleAsync();

        await targetCard.Locator("button.apply-btn").ClickAsync();
        await Expect(applyDialog).Not.ToBeVisibleAsync();

        // Verify week now has Monday Tacos and Prep Salad Mix
        await Expect(Page.Locator(".meal-entry-card", new() { HasText = "Monday Tacos" }))
            .ToBeVisibleAsync();
        await Expect(Page.Locator(".ondeck-card", new() { HasText = "Prep Salad Mix" }))
            .ToBeVisibleAsync();

        // 6. Add an extra ad-hoc note to this week
        await Page.Locator("button.add-meal-primary-btn").ClickAsync();
        var adhocDialog = Page.Locator("mat-dialog-container");
        await adhocDialog.Locator("mat-button-toggle[value='FreeformNote'] button").ClickAsync();
        await adhocDialog.Locator("input[formcontrolname='customTitle']").FillAsync("Ad-hoc Pizza");
        await adhocDialog
            .Locator("button[color='primary']", new() { HasText = "Plan Meal" })
            .ClickAsync();
        await Expect(adhocDialog).Not.ToBeVisibleAsync();
        await Expect(Page.Locator(".meal-entry-card", new() { HasText = "Ad-hoc Pizza" }))
            .ToBeVisibleAsync();

        // 7. Re-apply template in Replace mode
        await Page.Locator("button.template-action-btn").ClickAsync();
        var replaceDialog = Page.Locator("mat-dialog-container");
        var replaceCard = replaceDialog.Locator(".template-card", new() { HasText = tplName });
        await replaceDialog.Locator("mat-button-toggle[value='Replace'] button").ClickAsync();
        await replaceCard.Locator("button.apply-btn").ClickAsync();
        await Expect(replaceDialog).Not.ToBeVisibleAsync();

        // Verify Ad-hoc Pizza is replaced, but template meals remain
        await Expect(Page.Locator(".meal-entry-card", new() { HasText = "Ad-hoc Pizza" }))
            .Not.ToBeVisibleAsync();
        await Expect(Page.Locator(".meal-entry-card", new() { HasText = "Monday Tacos" }))
            .ToBeVisibleAsync();
        await Expect(Page.Locator(".ondeck-card", new() { HasText = "Prep Salad Mix" }))
            .ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task MealPlan_MonthlyOverview_AndMobileViewport_360px_RendersCleanly()
    {
        await RegisterAndLoginAsync();
        await Page.Locator("nav.desktop-nav a", new() { HasText = "Meal Plan" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/meal-plan.*"));

        // Clear current week if any entries exist from prior tests
        if (await Page.Locator("button.clear-week-btn").IsVisibleAsync())
        {
            await Page.Locator("button.clear-week-btn").ClickAsync();
            var confirmBtn = Page.Locator(
                "mat-dialog-container button",
                new() { HasText = "Clear Week" }
            );
            await confirmBtn.ClickAsync();
            await Expect(Page.Locator("mat-dialog-container")).Not.ToBeVisibleAsync();
            await Expect(Page.Locator("button.clear-week-btn")).Not.ToBeVisibleAsync();
        }

        // 1. Add a scheduled meal for today
        await Page.Locator("button.add-meal-primary-btn").ClickAsync();
        var dialog = Page.Locator("mat-dialog-container");
        await dialog.Locator("mat-button-toggle[value='FreeformNote'] button").ClickAsync();
        await dialog.Locator("input[formcontrolname='customTitle']").FillAsync("Pasta Night");
        await dialog
            .Locator("button[color='primary']", new() { HasText = "Plan Meal" })
            .ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();
        await Expect(Page.Locator(".meal-entry-card", new() { HasText = "Pasta Night" }))
            .ToBeVisibleAsync();

        // 2. Switch to Month View
        await Page.Locator("mat-button-toggle[value='month'] button").ClickAsync();
        var monthGrid = Page.Locator(".month-days-grid");
        await Expect(monthGrid).ToBeVisibleAsync();

        // Verify month meal pill contains Pasta Night
        var pill = monthGrid.Locator(".month-meal-pill", new() { HasText = "Pasta Night" });
        await Expect(pill).ToBeVisibleAsync();

        // Click the day cell containing Pasta Night to jump back to Week view
        var cell = monthGrid.Locator(".month-cell", new() { HasText = "Pasta Night" });
        await cell.ClickAsync();

        // Verify it switched back to Week view and displays Pasta Night
        await Expect(Page.Locator(".week-days-grid")).ToBeVisibleAsync();
        await Expect(Page.Locator(".meal-entry-card", new() { HasText = "Pasta Night" }))
            .ToBeVisibleAsync();

        // 3. Mobile Viewport (360x640)
        await Page.SetViewportSizeAsync(360, 640);

        // Desktop nav hidden, mobile nav visible
        await Expect(Page.Locator("nav.desktop-nav")).Not.ToBeVisibleAsync();
        var mobileNav = Page.Locator("nav.mobile-bottom-nav");
        await Expect(mobileNav).ToBeVisibleAsync();

        // Verify Meal Plan link in mobile bottom nav is active
        var mealPlanTab = mobileNav.Locator("a", new() { HasText = "Meal Plan" });
        await Expect(mealPlanTab).ToBeVisibleAsync();
        await Expect(mealPlanTab).ToHaveClassAsync(new Regex(".*active-tab.*"));

        // Verify no horizontal overflow at 360px
        var hasHorizontalOverflow = await Page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > window.innerWidth"
        );
        Assert.IsFalse(
            hasHorizontalOverflow,
            "Page has horizontal overflow at 360px mobile viewport."
        );

        // 4. Test Mobile On-Deck Drawer Toggle
        var ondeckToggle = Page.Locator("button.mobile-ondeck-header");
        await Expect(ondeckToggle).ToBeVisibleAsync();
        await ondeckToggle.ClickAsync();

        // In expanded mobile drawer, tap "Add to On-Deck"
        var addMobileBtn = Page.Locator(".mobile-ondeck-card button.add-to-ondeck-btn");
        await addMobileBtn.ClickAsync();

        var mobileAddDialog = Page.Locator("mat-dialog-container");
        await mobileAddDialog
            .Locator("mat-button-toggle[value='FreeformNote'] button")
            .ClickAsync();
        await mobileAddDialog
            .Locator("input[formcontrolname='customTitle']")
            .FillAsync("Weekend BBQ");
        await mobileAddDialog
            .Locator("button[color='primary']", new() { HasText = "Plan Meal" })
            .ClickAsync();
        await Expect(mobileAddDialog).Not.ToBeVisibleAsync();

        // Mobile drawer now displays Weekend BBQ with a Cook Tonight button
        var mobileItem = Page.Locator(
            ".mobile-ondeck-card .ondeck-item",
            new() { HasText = "Weekend BBQ" }
        );
        await Expect(mobileItem).ToBeVisibleAsync();
        await mobileItem.Locator("button.cook-tonight-btn").ClickAsync();

        // Weekend BBQ promoted from On-Deck to today's schedule
        await Expect(mobileItem).Not.ToBeVisibleAsync();
        var todayCol = Page.Locator(".day-column.is-today");
        await Expect(todayCol.Locator(".meal-entry-card", new() { HasText = "Weekend BBQ" }))
            .ToBeVisibleAsync();
    }
}

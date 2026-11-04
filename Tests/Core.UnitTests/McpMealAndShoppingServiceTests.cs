using App.Core.Exceptions;
using App.Core.Interfaces;
using App.Core.Mcp;
using App.Core.MealPlans;
using App.Core.Recipes;
using App.Core.Shopping;
using App.Data;
using App.Data.Entities;
using App.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class McpMealAndShoppingServiceTests
{
    private sealed class FixedUserContext(string userId) : IUserContext
    {
        public string UserId => userId;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private AppDbContext _dbContext = null!;
    private FixedTimeProvider _timeProvider = null!;
    private MealPlanService _mealPlanService = null!;
    private ShoppingListService _shoppingListService = null!;
    private McpMealAndShoppingService _service = null!;
    private Recipe _recipePasta = null!;
    private FoodItem _foodGarlic = null!;
    private FoodItem _foodSalt = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new AppDbContext(options);
        // Fixed date: Monday, Nov 2, 2026 (week starts Sunday, Nov 1, 2026)
        _timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero)
        );

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "mcpuser@example.com",
            NormalizedEmail = "MCPUSER@EXAMPLE.COM",
            Username = "MCP User",
            NormalizedUsername = "MCP USER",
            PasswordHash = "hash",
        };
        _dbContext.Users.Add(user);

        _foodGarlic = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Garlic",
            NormalizedName = "garlic",
            IsPantryStaple = false,
        };
        _foodSalt = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Kosher Salt",
            NormalizedName = "kosher salt",
            IsPantryStaple = true,
        };
        _dbContext.FoodItems.AddRange(_foodGarlic, _foodSalt);

        _recipePasta = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Garlic Butter Pasta",
            Slug = "garlic-butter-pasta",
            Status = RecipeStatus.Published,
            BaseServings = 4,
            Ingredients =
            [
                new RecipeIngredient
                {
                    Id = Guid.NewGuid(),
                    DisplayName = "Garlic cloves",
                    Quantity = 4,
                    Unit = MeasurementUnit.Clove,
                    FoodItemId = _foodGarlic.Id,
                },
                new RecipeIngredient
                {
                    Id = Guid.NewGuid(),
                    DisplayName = "Kosher salt",
                    Quantity = 1,
                    Unit = MeasurementUnit.Tsp,
                    FoodItemId = _foodSalt.Id,
                },
            ],
            Steps =
            [
                new RecipeStep
                {
                    Id = Guid.NewGuid(),
                    StepNumber = 1,
                    Instruction = "Boil pasta and toss with garlic and salt.",
                },
            ],
        };
        _dbContext.Recipes.Add(_recipePasta);
        _dbContext.SaveChanges();

        var userContext = new FixedUserContext(user.Id.ToString());
        _mealPlanService = new MealPlanService(_dbContext, _timeProvider);
        _shoppingListService = new ShoppingListService(_dbContext, userContext, _timeProvider);
        _service = new McpMealAndShoppingService(
            _dbContext,
            _mealPlanService,
            _shoppingListService,
            _timeProvider
        );
    }

    [TestMethod]
    public async Task GetMealPlan_WhenDatesOmitted_DefaultsToCurrentWeekSundayToSaturday()
    {
        var result = await _service.GetMealPlanAsync(new McpGetMealPlanRequest());
        Assert.AreEqual(new DateOnly(2026, 11, 1), result.StartDate);
        Assert.AreEqual(new DateOnly(2026, 11, 7), result.EndDate);
    }

    [TestMethod]
    public async Task GetMealPlan_WhenOnlyStartDateProvided_DefaultsEndDateToSevenDays()
    {
        var result = await _service.GetMealPlanAsync(
            new McpGetMealPlanRequest(StartDate: "2026-11-08")
        );
        Assert.AreEqual(new DateOnly(2026, 11, 8), result.StartDate);
        Assert.AreEqual(new DateOnly(2026, 11, 14), result.EndDate);
    }

    [TestMethod]
    public async Task GetMealPlan_WhenOnlyEndDateProvided_ThrowsDomainValidation()
    {
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            _service.GetMealPlanAsync(new McpGetMealPlanRequest(EndDate: "2026-11-15"))
        );
    }

    [TestMethod]
    public async Task PlanMeals_ResolvesRecipeBySlugAndTitle_SchedulesAllEntryTypes()
    {
        var planReq = new McpPlanMealsRequest([
            new McpPlanMealItemInput(
                EntryType: "Recipe",
                ScheduledDate: "2026-11-02",
                Slot: "Dinner",
                RecipeSlug: "garlic-butter-pasta",
                TargetServings: 8
            ),
            new McpPlanMealItemInput(
                EntryType: "Leftovers",
                ScheduledDate: "2026-11-03",
                Slot: "Lunch",
                CustomTitle: "Leftover Pasta"
            ),
            new McpPlanMealItemInput(
                EntryType: "FreeformNote",
                ScheduledDate: "2026-11-04",
                Slot: "Dinner",
                CustomTitle: "Date Night Pizza"
            ),
            new McpPlanMealItemInput(
                EntryType: "Recipe",
                IsOnDeck: true,
                RecipeTitle: "garlic butter pasta"
            ),
        ]);

        var result = await _service.PlanMealsAsync(planReq);
        Assert.AreEqual(4, result.PlannedCount);
        Assert.AreEqual(4, result.Entries.Count);

        var scheduledRecipe = result.Entries[0];
        Assert.AreEqual(_recipePasta.Id, scheduledRecipe.Recipe?.Id);
        Assert.AreEqual(8m, scheduledRecipe.TargetServings);
        Assert.AreEqual(MealSlot.Dinner, scheduledRecipe.Slot);

        var onDeckRecipe = result.Entries[3];
        Assert.IsTrue(onDeckRecipe.IsOnDeck);
        Assert.AreEqual(new DateOnly(2026, 11, 1), onDeckRecipe.WeekStartDate);
        Assert.IsNull(onDeckRecipe.ScheduledDate);
    }

    [TestMethod]
    public async Task RemoveOrRescheduleMealPlanEntry_SupportsMoveToOnDeck_RescheduleAndRemove()
    {
        // 1. Create a planned meal
        var planReq = new McpPlanMealsRequest([
            new McpPlanMealItemInput(
                EntryType: "Recipe",
                ScheduledDate: "2026-11-02",
                Slot: "Dinner",
                RecipeId: _recipePasta.Id
            ),
        ]);
        var planRes = await _service.PlanMealsAsync(planReq);
        var entryId = planRes.Entries[0].Id;

        // 2. Reschedule to Thursday Lunch
        var reschedRes = await _service.RemoveOrRescheduleMealPlanEntryAsync(
            new McpRemoveMealPlanEntryRequest(
                EntryId: entryId,
                RescheduleToDate: "2026-11-05",
                RescheduleToSlot: "Lunch"
            )
        );
        Assert.AreEqual("Rescheduled", reschedRes.Action);
        Assert.IsFalse(reschedRes.Removed);
        Assert.AreEqual(new DateOnly(2026, 11, 5), reschedRes.RescheduledEntry?.ScheduledDate);
        Assert.AreEqual(MealSlot.Lunch, reschedRes.RescheduledEntry?.Slot);

        // 3. Move to On-Deck
        var onDeckRes = await _service.RemoveOrRescheduleMealPlanEntryAsync(
            new McpRemoveMealPlanEntryRequest(EntryId: entryId, MoveToOnDeck: true)
        );
        Assert.AreEqual("RescheduledToOnDeck", onDeckRes.Action);
        Assert.IsTrue(onDeckRes.RescheduledEntry?.IsOnDeck);

        // 4. Remove completely
        var removeRes = await _service.RemoveOrRescheduleMealPlanEntryAsync(
            new McpRemoveMealPlanEntryRequest(EntryId: entryId)
        );
        Assert.AreEqual("Removed", removeRes.Action);
        Assert.IsTrue(removeRes.Removed);
        Assert.IsNull(removeRes.RescheduledEntry);
    }

    [TestMethod]
    public async Task ShoppingLists_GetUpsertCheckAndGenerateFromMealPlan()
    {
        // 1. Plan pasta on Monday Dinner (scaled to 8)
        await _service.PlanMealsAsync(
            new McpPlanMealsRequest([
                new McpPlanMealItemInput(
                    EntryType: "Recipe",
                    ScheduledDate: "2026-11-02",
                    Slot: "Dinner",
                    RecipeId: _recipePasta.Id,
                    TargetServings: 8
                ),
            ])
        );

        // 2. Generate shopping list from meal plan into "Weekly Groceries" (auto-created)
        var genRes = await _service.GenerateShoppingListFromMealPlanAsync(
            new McpGenerateShoppingListRequest(
                StartDate: "2026-11-01",
                EndDate: "2026-11-07",
                ListName: "Weekly Groceries",
                CreateListIfMissing: true
            )
        );
        Assert.IsNotNull(genRes);
        Assert.AreEqual("Weekly Groceries", genRes.ShoppingList.Name);
        Assert.AreEqual(1, genRes.ActiveItemsAffectedCount);
        Assert.AreEqual(1, genRes.ExcludedPantryItemsCount);

        // Active items has Garlic: 4 * (8/4) = 8 cloves
        var garlicItem = genRes.ShoppingList.ActiveItems.FirstOrDefault(i =>
            i.DisplayName.Contains("Garlic")
        );
        Assert.IsNotNull(garlicItem);
        Assert.AreEqual(8m, garlicItem.Quantity);

        // Excluded pantry items has Kosher salt: 1 * 2 = 2 tsp
        var saltItem = genRes.ShoppingList.ExcludedPantryItems.FirstOrDefault(i =>
            i.DisplayName.Contains("Salt", StringComparison.OrdinalIgnoreCase)
        );
        Assert.IsNotNull(saltItem);
        Assert.AreEqual(2m, saltItem.Quantity);

        // 3. Upsert shopping list item with unit string normalization ("gallon")
        var upsertRes = await _service.UpsertShoppingListItemsAsync(
            new McpUpsertShoppingListItemsRequest(
                Items:
                [
                    new McpShoppingListItemUpsertInput(
                        DisplayName: "Whole Milk",
                        Quantity: 2,
                        Unit: "gallon"
                    ),
                ],
                ListName: "Weekly Groceries"
            )
        );
        var milkItem = upsertRes.ShoppingList.ActiveItems.FirstOrDefault(i =>
            i.DisplayName == "Whole Milk"
        );
        Assert.IsNotNull(milkItem);
        Assert.AreEqual(MeasurementUnit.Gal, milkItem.Unit);
        Assert.AreEqual(2m, milkItem.Quantity);

        // 4. Set item checked by item name (case-insensitive)
        var checkedRes = await _service.SetShoppingListItemCheckedAsync(
            new McpSetShoppingItemCheckedRequest(
                IsChecked: true,
                ListName: "Weekly Groceries",
                ItemName: "whole milk"
            )
        );
        Assert.IsTrue(checkedRes.IsChecked);
        Assert.AreEqual("Whole Milk", checkedRes.DisplayName);

        // 5. Query shopping lists via MCP
        var queryRes = await _service.GetShoppingListsAsync(
            new McpGetShoppingListsRequest(ListName: "Weekly Groceries", IncludeItems: true)
        );
        Assert.AreEqual(1, queryRes.Summaries.Count);
        Assert.AreEqual(1, queryRes.Lists.Count);
        Assert.AreEqual(1, queryRes.Lists[0].CheckedItems.Count);
        Assert.AreEqual("Whole Milk", queryRes.Lists[0].CheckedItems[0].DisplayName);
    }
}

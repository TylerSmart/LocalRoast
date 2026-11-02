using App.Core.Exceptions;
using App.Core.MealPlans;
using App.Core.Recipes;
using App.Data;
using App.Data.Entities;
using App.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class MealPlanServiceTests
{
    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private AppDbContext _dbContext = null!;
    private FixedTimeProvider _timeProvider = null!;
    private MealPlanService _service = null!;
    private Recipe _recipe1 = null!;
    private Recipe _recipe2 = null!;
    private Recipe _archivedRecipe = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new AppDbContext(options);
        _timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero)
        );
        _service = new MealPlanService(_dbContext, _timeProvider);

        _recipe1 = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Sunday Chili",
            Slug = "sunday-chili",
            BaseServings = 4m,
            Status = RecipeStatus.Published,
        };

        _recipe2 = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Cornbread",
            Slug = "cornbread",
            BaseServings = 8m,
            Status = RecipeStatus.Published,
        };

        _archivedRecipe = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Old Stew",
            Slug = "old-stew",
            BaseServings = 4m,
            Status = RecipeStatus.Archived,
        };

        _dbContext.Recipes.AddRange(_recipe1, _recipe2, _archivedRecipe);
        _dbContext.SaveChanges();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dbContext.Database.EnsureDeleted();
        _dbContext.Dispose();
    }

    [TestMethod]
    public async Task CreateEntryAsync_Recipe_OnDeck_Succeeds()
    {
        var request = new CreateMealPlanEntryRequest(
            EntryType: MealPlanEntryType.Recipe,
            IsOnDeck: true,
            WeekStartDate: new DateOnly(2026, 11, 2),
            RecipeId: _recipe1.Id,
            TargetServings: 6m
        );

        var created = await _service.CreateEntryAsync(request);

        Assert.IsNotNull(created);
        Assert.IsTrue(created.IsOnDeck);
        Assert.IsNull(created.ScheduledDate);
        Assert.IsNull(created.Slot);
        Assert.AreEqual(new DateOnly(2026, 11, 2), created.WeekStartDate);
        Assert.AreEqual("Sunday Chili", created.DisplayTitle);
        Assert.AreEqual(6m, created.TargetServings);
        Assert.IsFalse(created.IsLeftovers);
        Assert.AreEqual(0, created.SortOrder);
    }

    [TestMethod]
    public async Task CreateEntryAsync_ScheduledRecipe_MultiDish_AssignsSequentialSortOrder()
    {
        var date = new DateOnly(2026, 11, 4);
        var req1 = new CreateMealPlanEntryRequest(
            EntryType: MealPlanEntryType.Recipe,
            IsOnDeck: false,
            ScheduledDate: date,
            Slot: MealSlot.Dinner,
            RecipeId: _recipe1.Id
        );
        var req2 = new CreateMealPlanEntryRequest(
            EntryType: MealPlanEntryType.Recipe,
            IsOnDeck: false,
            ScheduledDate: date,
            Slot: MealSlot.Dinner,
            RecipeId: _recipe2.Id
        );

        var dish1 = await _service.CreateEntryAsync(req1);
        var dish2 = await _service.CreateEntryAsync(req2);

        Assert.AreEqual(0, dish1.SortOrder);
        Assert.AreEqual(1, dish2.SortOrder);
        Assert.AreEqual(MealSlot.Dinner, dish1.Slot);
        Assert.AreEqual(MealSlot.Dinner, dish2.Slot);
        Assert.AreEqual(date, dish1.ScheduledDate);
        Assert.AreEqual(new DateOnly(2026, 11, 2), dish1.WeekStartDate);
    }

    [TestMethod]
    public async Task CreateEntryAsync_Leftovers_WithSourceEntry_SetsLeftoverFlagAndTitle()
    {
        var scheduledDate = new DateOnly(2026, 11, 2);
        var sourceReq = new CreateMealPlanEntryRequest(
            EntryType: MealPlanEntryType.Recipe,
            IsOnDeck: false,
            ScheduledDate: scheduledDate,
            Slot: MealSlot.Dinner,
            RecipeId: _recipe1.Id,
            TargetServings: 8m
        );
        var source = await _service.CreateEntryAsync(sourceReq);

        var leftoverReq = new CreateMealPlanEntryRequest(
            EntryType: MealPlanEntryType.Leftovers,
            IsOnDeck: false,
            ScheduledDate: scheduledDate.AddDays(1),
            Slot: MealSlot.Lunch,
            SourceMealPlanEntryId: source.Id
        );

        var leftover = await _service.CreateEntryAsync(leftoverReq);

        Assert.IsTrue(leftover.IsLeftovers);
        Assert.AreEqual(MealPlanEntryType.Leftovers, leftover.EntryType);
        Assert.AreEqual("Leftovers: Sunday Chili", leftover.DisplayTitle);
        Assert.AreEqual(_recipe1.Id, leftover.Recipe?.Id);
        Assert.AreEqual(8m, leftover.TargetServings);
        Assert.AreEqual(source.Id, leftover.SourceMealPlanEntryId);
    }

    [TestMethod]
    public async Task CreateEntryAsync_FreeformNote_RequiresCustomTitle()
    {
        var scheduledDate = new DateOnly(2026, 11, 6);
        var validReq = new CreateMealPlanEntryRequest(
            EntryType: MealPlanEntryType.FreeformNote,
            IsOnDeck: false,
            ScheduledDate: scheduledDate,
            Slot: MealSlot.Dinner,
            CustomTitle: "Pizza Night!"
        );

        var note = await _service.CreateEntryAsync(validReq);
        Assert.AreEqual("Pizza Night!", note.DisplayTitle);
        Assert.IsNull(note.Recipe);
        Assert.IsNull(note.TargetServings);
        Assert.IsFalse(note.IsLeftovers);

        var invalidReq = new CreateMealPlanEntryRequest(
            EntryType: MealPlanEntryType.FreeformNote,
            IsOnDeck: false,
            ScheduledDate: scheduledDate,
            Slot: MealSlot.Dinner,
            CustomTitle: "   "
        );

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            _service.CreateEntryAsync(invalidReq)
        );
    }

    [TestMethod]
    public async Task CreateEntryAsync_ArchivedRecipe_ThrowsDomainValidationException()
    {
        var req = new CreateMealPlanEntryRequest(
            EntryType: MealPlanEntryType.Recipe,
            IsOnDeck: false,
            ScheduledDate: new DateOnly(2026, 11, 3),
            Slot: MealSlot.Dinner,
            RecipeId: _archivedRecipe.Id
        );

        await Assert.ThrowsAsync<DomainValidationException>(() => _service.CreateEntryAsync(req));
    }

    [TestMethod]
    public async Task GetWeekAsync_Returns7DaysAndOnDeck_OrderedProperly()
    {
        var monday = new DateOnly(2026, 11, 2);
        await _service.CreateEntryAsync(
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.Recipe,
                IsOnDeck: true,
                WeekStartDate: monday,
                RecipeId: _recipe1.Id
            )
        );

        await _service.CreateEntryAsync(
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.Recipe,
                IsOnDeck: false,
                ScheduledDate: monday,
                Slot: MealSlot.Breakfast,
                RecipeId: _recipe2.Id
            )
        );

        var week = await _service.GetWeekAsync(monday);

        Assert.AreEqual(monday, week.WeekStartDate);
        Assert.AreEqual(monday.AddDays(6), week.WeekEndDate);
        Assert.AreEqual(7, week.Days.Count);
        Assert.AreEqual(1, week.OnDeckEntries.Count);
        Assert.AreEqual(1, week.Days[0].Entries.Count);
        Assert.AreEqual(0, week.Days[1].Entries.Count);
    }

    [TestMethod]
    public async Task MoveEntryAsync_BetweenOnDeckAndDay_ReSequencesSortOrders()
    {
        var monday = new DateOnly(2026, 11, 2);
        var onDeck1 = await _service.CreateEntryAsync(
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.Recipe,
                IsOnDeck: true,
                WeekStartDate: monday,
                RecipeId: _recipe1.Id
            )
        );
        var onDeck2 = await _service.CreateEntryAsync(
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.Recipe,
                IsOnDeck: true,
                WeekStartDate: monday,
                RecipeId: _recipe2.Id
            )
        );

        Assert.AreEqual(0, onDeck1.SortOrder);
        Assert.AreEqual(1, onDeck2.SortOrder);

        // Move onDeck1 to Monday Dinner
        var moved = await _service.MoveEntryAsync(
            onDeck1.Id,
            new MoveMealPlanEntryRequest(
                IsOnDeck: false,
                ScheduledDate: monday,
                Slot: MealSlot.Dinner,
                TargetSortOrder: 0
            )
        );

        Assert.IsNotNull(moved);
        Assert.IsFalse(moved.IsOnDeck);
        Assert.AreEqual(monday, moved.ScheduledDate);
        Assert.AreEqual(MealSlot.Dinner, moved.Slot);
        Assert.AreEqual(0, moved.SortOrder);

        // Verify remaining on-deck item is re-sequenced to 0
        var remainingOnDeck = await _service.GetEntryByIdAsync(onDeck2.Id);
        Assert.AreEqual(0, remainingOnDeck!.SortOrder);
    }

    [TestMethod]
    public async Task CookTonightAsync_PromotesOnDeckMealToTodayDinner()
    {
        var monday = new DateOnly(2026, 11, 2);
        var entry = await _service.CreateEntryAsync(
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.Recipe,
                IsOnDeck: true,
                WeekStartDate: monday,
                RecipeId: _recipe1.Id
            )
        );

        var cooked = await _service.CookTonightAsync(entry.Id, new CookTonightRequest());

        Assert.IsNotNull(cooked);
        Assert.IsFalse(cooked.IsOnDeck);
        Assert.AreEqual(monday, cooked.ScheduledDate);
        Assert.AreEqual(MealSlot.Dinner, cooked.Slot);
    }

    [TestMethod]
    public async Task DeleteEntryAsync_DetachesDerivedLeftovers()
    {
        var monday = new DateOnly(2026, 11, 2);
        var source = await _service.CreateEntryAsync(
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.Recipe,
                IsOnDeck: false,
                ScheduledDate: monday,
                Slot: MealSlot.Dinner,
                RecipeId: _recipe1.Id
            )
        );

        var leftover = await _service.CreateEntryAsync(
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.Leftovers,
                IsOnDeck: false,
                ScheduledDate: monday.AddDays(1),
                Slot: MealSlot.Lunch,
                SourceMealPlanEntryId: source.Id
            )
        );

        var deleted = await _service.DeleteEntryAsync(source.Id);
        Assert.IsTrue(deleted);

        var remainingLeftover = await _service.GetEntryByIdAsync(leftover.Id);
        Assert.IsNotNull(remainingLeftover);
        Assert.IsNull(remainingLeftover.SourceMealPlanEntryId);
        Assert.AreEqual("Leftovers: Sunday Chili", remainingLeftover.DisplayTitle);
    }

    [TestMethod]
    public async Task ClearWeekAsync_DeletesScheduledMeals_AndPreservesOnDeckWhenNotSpecified()
    {
        var monday = new DateOnly(2026, 11, 2);
        await _service.CreateEntryAsync(
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.Recipe,
                IsOnDeck: true,
                WeekStartDate: monday,
                RecipeId: _recipe1.Id
            )
        );
        await _service.CreateEntryAsync(
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.Recipe,
                IsOnDeck: false,
                ScheduledDate: monday,
                Slot: MealSlot.Dinner,
                RecipeId: _recipe2.Id
            )
        );

        var count = await _service.ClearWeekAsync(monday, includeOnDeck: false);
        Assert.AreEqual(1, count);

        var week = await _service.GetWeekAsync(monday);
        Assert.AreEqual(1, week.OnDeckEntries.Count);
        Assert.AreEqual(0, week.Days[0].Entries.Count);
    }
}

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
            new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero)
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
    public async Task ApplyDraft_LinksLeftoversAcrossWeeks_AndAssignsDistinctSortOrders()
    {
        var start = new DateOnly(2026, 11, 7);
        await _service.ApplyDraftEntriesAsync(
            new(
                start,
                [
                    new(
                        ScheduledDate: start,
                        Slot: MealSlot.Dinner,
                        RecipeId: _recipe1.Id,
                        TargetServings: 8
                    ),
                    new(
                        EntryType: MealPlanEntryType.Leftovers,
                        ScheduledDate: start.AddDays(1),
                        Slot: MealSlot.Dinner,
                        RecipeId: _recipe1.Id,
                        TargetServings: 4,
                        SourceDraftEntryIndex: 0
                    ),
                    new(
                        ScheduledDate: start,
                        Slot: MealSlot.Dinner,
                        RecipeId: _recipe2.Id,
                        TargetServings: 4
                    ),
                ]
            )
        );
        var range = await _service.GetRangeAsync(start, start.AddDays(1));
        var cooked = range.ScheduledEntries.Single(e =>
            !e.IsLeftovers && e.Recipe!.Id == _recipe1.Id
        );
        var leftovers = range.ScheduledEntries.Single(e => e.IsLeftovers);
        Assert.AreEqual(cooked.Id, leftovers.SourceMealPlanEntryId);
        Assert.AreEqual(4m, leftovers.TargetServings);
        Assert.AreEqual(new DateOnly(2026, 11, 8), leftovers.WeekStartDate);
        CollectionAssert.AreEqual(
            new[] { 0, 1 },
            range.ScheduledEntries.Where(e => !e.IsLeftovers).Select(e => e.SortOrder).ToArray()
        );
    }

    [TestMethod]
    public async Task ApplyDraft_InvalidEntryOrSourceLeavesNoPartiallySavedMeals()
    {
        var start = new DateOnly(2026, 11, 5);
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            _service.ApplyDraftEntriesAsync(
                new(
                    start,
                    [
                        new(ScheduledDate: start, Slot: MealSlot.Dinner, RecipeId: _recipe1.Id),
                        new(
                            ScheduledDate: start.AddDays(1),
                            Slot: MealSlot.Dinner,
                            RecipeId: Guid.NewGuid()
                        ),
                    ]
                )
            )
        );
        await _dbContext.SaveChangesAsync();
        Assert.AreEqual(0, await _dbContext.MealPlanEntries.CountAsync());

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            _service.ApplyDraftEntriesAsync(
                new(
                    start,
                    [
                        new(ScheduledDate: start, Slot: MealSlot.Dinner, RecipeId: _recipe1.Id),
                        new(
                            EntryType: MealPlanEntryType.Leftovers,
                            ScheduledDate: start.AddDays(1),
                            Slot: MealSlot.Dinner,
                            RecipeId: _recipe2.Id,
                            SourceDraftEntryIndex: 0
                        ),
                    ]
                )
            )
        );
        Assert.AreEqual(0, await _dbContext.MealPlanEntries.CountAsync());
    }

    [TestMethod]
    public async Task CreateEntryAsync_Recipe_OnDeck_Succeeds()
    {
        var request = new CreateMealPlanEntryRequest(
            EntryType: MealPlanEntryType.Recipe,
            IsOnDeck: true,
            WeekStartDate: new DateOnly(2026, 11, 1),
            RecipeId: _recipe1.Id,
            TargetServings: 6m
        );

        var created = await _service.CreateEntryAsync(request);

        Assert.IsNotNull(created);
        Assert.IsTrue(created.IsOnDeck);
        Assert.IsNull(created.ScheduledDate);
        Assert.IsNull(created.Slot);
        Assert.AreEqual(new DateOnly(2026, 11, 1), created.WeekStartDate);
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
        Assert.AreEqual(new DateOnly(2026, 11, 1), dish1.WeekStartDate);
    }

    [TestMethod]
    public async Task CreateEntryAsync_Leftovers_WithSourceEntry_SetsLeftoverFlagAndTitle()
    {
        var scheduledDate = new DateOnly(2026, 11, 1);
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
        var sunday = new DateOnly(2026, 11, 1);
        await _service.CreateEntryAsync(
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.Recipe,
                IsOnDeck: true,
                WeekStartDate: sunday,
                RecipeId: _recipe1.Id
            )
        );

        await _service.CreateEntryAsync(
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.Recipe,
                IsOnDeck: false,
                ScheduledDate: sunday,
                Slot: MealSlot.Breakfast,
                RecipeId: _recipe2.Id
            )
        );

        var week = await _service.GetWeekAsync(sunday);

        Assert.AreEqual(sunday, week.WeekStartDate);
        Assert.AreEqual(sunday.AddDays(6), week.WeekEndDate);
        Assert.AreEqual(7, week.Days.Count);
        Assert.AreEqual(1, week.OnDeckEntries.Count);
        Assert.AreEqual(1, week.Days[0].Entries.Count);
        Assert.AreEqual(0, week.Days[1].Entries.Count);
    }

    [TestMethod]
    public async Task MoveEntryAsync_BetweenOnDeckAndDay_ReSequencesSortOrders()
    {
        var sunday = new DateOnly(2026, 11, 1);
        var onDeck1 = await _service.CreateEntryAsync(
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.Recipe,
                IsOnDeck: true,
                WeekStartDate: sunday,
                RecipeId: _recipe1.Id
            )
        );
        var onDeck2 = await _service.CreateEntryAsync(
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.Recipe,
                IsOnDeck: true,
                WeekStartDate: sunday,
                RecipeId: _recipe2.Id
            )
        );

        Assert.AreEqual(0, onDeck1.SortOrder);
        Assert.AreEqual(1, onDeck2.SortOrder);

        // Move onDeck1 to Sunday Dinner
        var moved = await _service.MoveEntryAsync(
            onDeck1.Id,
            new MoveMealPlanEntryRequest(
                IsOnDeck: false,
                ScheduledDate: sunday,
                Slot: MealSlot.Dinner,
                TargetSortOrder: 0
            )
        );

        Assert.IsNotNull(moved);
        Assert.IsFalse(moved.IsOnDeck);
        Assert.AreEqual(sunday, moved.ScheduledDate);
        Assert.AreEqual(MealSlot.Dinner, moved.Slot);
        Assert.AreEqual(0, moved.SortOrder);

        // Verify remaining on-deck item is re-sequenced to 0
        var remainingOnDeck = await _service.GetEntryByIdAsync(onDeck2.Id);
        Assert.AreEqual(0, remainingOnDeck!.SortOrder);
    }

    [TestMethod]
    public async Task CookTonightAsync_PromotesOnDeckMealToTodayDinner()
    {
        var sunday = new DateOnly(2026, 11, 1);
        var entry = await _service.CreateEntryAsync(
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.Recipe,
                IsOnDeck: true,
                WeekStartDate: sunday,
                RecipeId: _recipe1.Id
            )
        );

        var cooked = await _service.CookTonightAsync(entry.Id, new CookTonightRequest());

        Assert.IsNotNull(cooked);
        Assert.IsFalse(cooked.IsOnDeck);
        Assert.AreEqual(sunday, cooked.ScheduledDate);
        Assert.AreEqual(MealSlot.Dinner, cooked.Slot);
    }

    [TestMethod]
    public async Task DeleteEntryAsync_DetachesDerivedLeftovers()
    {
        var sunday = new DateOnly(2026, 11, 1);
        var source = await _service.CreateEntryAsync(
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.Recipe,
                IsOnDeck: false,
                ScheduledDate: sunday,
                Slot: MealSlot.Dinner,
                RecipeId: _recipe1.Id
            )
        );

        var leftover = await _service.CreateEntryAsync(
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.Leftovers,
                IsOnDeck: false,
                ScheduledDate: sunday.AddDays(1),
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
        var sunday = new DateOnly(2026, 11, 1);
        await _service.CreateEntryAsync(
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.Recipe,
                IsOnDeck: true,
                WeekStartDate: sunday,
                RecipeId: _recipe1.Id
            )
        );
        await _service.CreateEntryAsync(
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.Recipe,
                IsOnDeck: false,
                ScheduledDate: sunday,
                Slot: MealSlot.Dinner,
                RecipeId: _recipe2.Id
            )
        );

        var count = await _service.ClearWeekAsync(sunday, includeOnDeck: false);
        Assert.AreEqual(1, count);

        var week = await _service.GetWeekAsync(sunday);
        Assert.AreEqual(1, week.OnDeckEntries.Count);
        Assert.AreEqual(0, week.Days[0].Entries.Count);
    }
}

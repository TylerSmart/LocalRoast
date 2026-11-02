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
public sealed class MealPlanTemplateServiceTests
{
    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private AppDbContext _dbContext = null!;
    private MealPlanService _mealPlanService = null!;
    private MealPlanTemplateService _service = null!;
    private Recipe _recipe1 = null!;
    private Recipe _recipe2 = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new AppDbContext(options);
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero)
        );
        _mealPlanService = new MealPlanService(_dbContext, timeProvider);
        _service = new MealPlanTemplateService(_dbContext, _mealPlanService);

        _recipe1 = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Taco Tuesday",
            Slug = "taco-tuesday",
            BaseServings = 4m,
            Status = RecipeStatus.Published,
        };

        _recipe2 = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Sheet Pan Salmon",
            Slug = "sheet-pan-salmon",
            BaseServings = 2m,
            Status = RecipeStatus.Published,
        };

        _dbContext.Recipes.AddRange(_recipe1, _recipe2);
        _dbContext.SaveChanges();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dbContext.Database.EnsureDeleted();
        _dbContext.Dispose();
    }

    [TestMethod]
    public async Task CreateTemplateAsync_ValidRequest_CreatesTemplateAndItems()
    {
        var request = new UpsertMealPlanTemplateRequest(
            Name: "Standard Busy Week",
            Description: "Quick meals for busy weekdays",
            Items:
            [
                new MealPlanTemplateItemInput(
                    IsOnDeck: false,
                    DayOffset: 1, // Tuesday
                    Slot: MealSlot.Dinner,
                    EntryType: MealPlanEntryType.Recipe,
                    RecipeId: _recipe1.Id
                ),
                new MealPlanTemplateItemInput(
                    IsOnDeck: true,
                    EntryType: MealPlanEntryType.Recipe,
                    RecipeId: _recipe2.Id
                ),
            ]
        );

        var created = await _service.CreateTemplateAsync(request);

        Assert.IsNotNull(created);
        Assert.AreEqual("Standard Busy Week", created.Name);
        Assert.AreEqual(2, created.Items.Count);

        var tuesday = created.Items.First(i => !i.IsOnDeck);
        Assert.AreEqual(1, tuesday.DayOffset);
        Assert.AreEqual(MealSlot.Dinner, tuesday.Slot);
        Assert.AreEqual("Taco Tuesday", tuesday.DisplayTitle);

        var onDeck = created.Items.First(i => i.IsOnDeck);
        Assert.IsNull(onDeck.DayOffset);
        Assert.IsNull(onDeck.Slot);
        Assert.AreEqual("Sheet Pan Salmon", onDeck.DisplayTitle);
    }

    [TestMethod]
    public async Task CreateTemplateAsync_DuplicateName_ThrowsConflictException()
    {
        await _service.CreateTemplateAsync(new UpsertMealPlanTemplateRequest("Standard Week"));

        await Assert.ThrowsAsync<ConflictException>(() =>
            _service.CreateTemplateAsync(new UpsertMealPlanTemplateRequest("standard week"))
        );
    }

    [TestMethod]
    public async Task CreateTemplateFromWeekAsync_SnapshotsExistingWeek()
    {
        var monday = new DateOnly(2026, 11, 2);
        await _mealPlanService.CreateEntryAsync(
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.Recipe,
                IsOnDeck: false,
                ScheduledDate: monday.AddDays(1),
                Slot: MealSlot.Dinner,
                RecipeId: _recipe1.Id
            )
        );
        await _mealPlanService.CreateEntryAsync(
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.Recipe,
                IsOnDeck: true,
                WeekStartDate: monday,
                RecipeId: _recipe2.Id
            )
        );

        var template = await _service.CreateTemplateFromWeekAsync(
            new CreateTemplateFromWeekRequest(
                WeekStartDate: monday,
                Name: "Snapshotted Week",
                IncludeOnDeck: true
            )
        );

        Assert.IsNotNull(template);
        Assert.AreEqual("Snapshotted Week", template.Name);
        Assert.AreEqual(2, template.Items.Count);
    }

    [TestMethod]
    public async Task ApplyTemplateToWeekAsync_MergeMode_AppendsWithoutDuplication()
    {
        var template = await _service.CreateTemplateAsync(
            new UpsertMealPlanTemplateRequest(
                Name: "Merge Template",
                Items:
                [
                    new MealPlanTemplateItemInput(
                        IsOnDeck: false,
                        DayOffset: 0, // Monday
                        Slot: MealSlot.Dinner,
                        EntryType: MealPlanEntryType.Recipe,
                        RecipeId: _recipe1.Id
                    ),
                    new MealPlanTemplateItemInput(
                        IsOnDeck: false,
                        DayOffset: 1, // Tuesday
                        Slot: MealSlot.Dinner,
                        EntryType: MealPlanEntryType.Recipe,
                        RecipeId: _recipe2.Id
                    ),
                ]
            )
        );

        var targetMonday = new DateOnly(2026, 11, 9);
        // Pre-create Monday Dinner with recipe1
        await _mealPlanService.CreateEntryAsync(
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.Recipe,
                IsOnDeck: false,
                ScheduledDate: targetMonday,
                Slot: MealSlot.Dinner,
                RecipeId: _recipe1.Id
            )
        );

        // Apply template in Merge mode
        var week = await _service.ApplyTemplateToWeekAsync(
            template.Id,
            new ApplyMealPlanTemplateRequest(
                TargetWeekStartDate: targetMonday,
                Mode: ApplyTemplateMode.Merge
            )
        );

        Assert.IsNotNull(week);
        // Monday should still have 1 entry (not duplicated)
        Assert.AreEqual(1, week.Days[0].Entries.Count);
        // Tuesday should have recipe2 added
        Assert.AreEqual(1, week.Days[1].Entries.Count);
        Assert.AreEqual("Sheet Pan Salmon", week.Days[1].Entries[0].DisplayTitle);
    }

    [TestMethod]
    public async Task ApplyTemplateToWeekAsync_ReplaceMode_ClearsWeekFirst()
    {
        var template = await _service.CreateTemplateAsync(
            new UpsertMealPlanTemplateRequest(
                Name: "Replace Template",
                Items:
                [
                    new MealPlanTemplateItemInput(
                        IsOnDeck: false,
                        DayOffset: 1, // Tuesday
                        Slot: MealSlot.Dinner,
                        EntryType: MealPlanEntryType.Recipe,
                        RecipeId: _recipe2.Id
                    ),
                ]
            )
        );

        var targetMonday = new DateOnly(2026, 11, 9);
        // Pre-create an ad-hoc note on Monday
        await _mealPlanService.CreateEntryAsync(
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.FreeformNote,
                IsOnDeck: false,
                ScheduledDate: targetMonday,
                Slot: MealSlot.Lunch,
                CustomTitle: "Old Lunch"
            )
        );

        // Apply in Replace mode
        var week = await _service.ApplyTemplateToWeekAsync(
            template.Id,
            new ApplyMealPlanTemplateRequest(
                TargetWeekStartDate: targetMonday,
                Mode: ApplyTemplateMode.Replace
            )
        );

        Assert.IsNotNull(week);
        Assert.AreEqual(0, week.Days[0].Entries.Count);
        Assert.AreEqual(1, week.Days[1].Entries.Count);
        Assert.AreEqual("Sheet Pan Salmon", week.Days[1].Entries[0].DisplayTitle);
    }
}

using App.Core.Exceptions;
using App.Core.Interfaces;
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
public sealed class ShoppingListServiceTests
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
    private FixedUserContext _userContext = null!;
    private ShoppingListService _service = null!;
    private User _user = null!;
    private FoodItem _foodGarlic = null!;
    private FoodItem _foodOil = null!;
    private FoodItem _foodOnion = null!;
    private Recipe _recipeTacos = null!;
    private Recipe _subRecipeSalsa = null!;

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

        _user = new User
        {
            Id = Guid.NewGuid(),
            Email = "chef@example.com",
            NormalizedEmail = "CHEF@EXAMPLE.COM",
            Username = "Chef Tyler",
            NormalizedUsername = "CHEF TYLER",
            PasswordHash = "hash",
        };
        _dbContext.Users.Add(_user);

        _userContext = new FixedUserContext(_user.Id.ToString());
        _service = new ShoppingListService(_dbContext, _userContext, _timeProvider);

        _foodGarlic = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Garlic",
            NormalizedName = "garlic",
            IsPantryStaple = false,
            Aliases =
            [
                new FoodItemAlias { Alias = "Garlic Cloves", NormalizedAlias = "garlic cloves" },
            ],
        };

        _foodOil = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Extra-Virgin Olive Oil",
            NormalizedName = "extra-virgin olive oil",
            IsPantryStaple = true,
            Aliases = [new FoodItemAlias { Alias = "Olive Oil", NormalizedAlias = "olive oil" }],
        };

        _foodOnion = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Yellow Onion",
            NormalizedName = "yellow onion",
            IsPantryStaple = false,
        };

        _dbContext.FoodItems.AddRange(_foodGarlic, _foodOil, _foodOnion);

        _subRecipeSalsa = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Salsa Verde",
            Slug = "salsa-verde",
            BaseServings = 2m,
            Status = RecipeStatus.Published,
            Ingredients =
            [
                new RecipeIngredient
                {
                    Id = Guid.NewGuid(),
                    FoodItemId = _foodGarlic.Id,
                    FoodItem = _foodGarlic,
                    DisplayName = "Garlic",
                    Quantity = 2m,
                    Unit = MeasurementUnit.Clove,
                    SortOrder = 0,
                },
            ],
        };

        _recipeTacos = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Chicken Tacos",
            Slug = "chicken-tacos",
            BaseServings = 4m,
            Status = RecipeStatus.Published,
            Ingredients =
            [
                new RecipeIngredient
                {
                    Id = Guid.NewGuid(),
                    FoodItemId = _foodGarlic.Id,
                    FoodItem = _foodGarlic,
                    DisplayName = "Garlic",
                    Quantity = 2m,
                    Unit = MeasurementUnit.Clove,
                    SortOrder = 0,
                },
                new RecipeIngredient
                {
                    Id = Guid.NewGuid(),
                    FoodItemId = _foodOil.Id,
                    FoodItem = _foodOil,
                    DisplayName = "Olive Oil",
                    Quantity = 2m,
                    Unit = MeasurementUnit.Tbsp,
                    SortOrder = 1,
                },
                new RecipeIngredient
                {
                    Id = Guid.NewGuid(),
                    LinkedRecipeId = _subRecipeSalsa.Id,
                    LinkedRecipe = _subRecipeSalsa,
                    DisplayName = "Salsa Verde",
                    Quantity = 1m,
                    SortOrder = 2,
                },
            ],
        };

        _dbContext.Recipes.AddRange(_subRecipeSalsa, _recipeTacos);
        _dbContext.SaveChanges();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dbContext.Database.EnsureDeleted();
        _dbContext.Dispose();
    }

    [TestMethod]
    public async Task CreateShoppingList_ThrowsOnDuplicateName()
    {
        await _service.CreateShoppingListAsync(new CreateShoppingListRequest("Costco Run"));

        await Assert.ThrowsAsync<ConflictException>(() =>
            _service.CreateShoppingListAsync(new CreateShoppingListRequest("costco run"))
        );
    }

    [TestMethod]
    public async Task AddItem_AutoBindsFoodCatalog_AndConsolidatesCompatibleItems()
    {
        var list = await _service.CreateShoppingListAsync(
            new CreateShoppingListRequest("Groceries")
        );

        // Add 2 cloves of garlic manually
        var item1 = await _service.AddItemAsync(
            list.Id,
            new CreateShoppingListItemRequest(
                "Garlic Cloves",
                2m,
                null,
                MeasurementUnit.Clove,
                null,
                null
            )
        );

        Assert.IsNotNull(item1);
        Assert.AreEqual(_foodGarlic.Id, item1.FoodItemId);
        Assert.AreEqual(2m, item1.Quantity);
        Assert.IsFalse(item1.IsPantryStapleExcluded);

        // Add 3 more cloves with consolidate = true
        var item2 = await _service.AddItemAsync(
            list.Id,
            new CreateShoppingListItemRequest(
                "Garlic",
                3m,
                null,
                MeasurementUnit.Clove,
                null,
                null,
                null,
                true
            )
        );

        Assert.IsNotNull(item2);
        Assert.AreEqual(item1.Id, item2.Id);
        Assert.AreEqual(5m, item2.Quantity);

        var detail = await _service.GetShoppingListByIdAsync(list.Id);
        Assert.IsNotNull(detail);
        Assert.AreEqual(1, detail.ActiveItems.Count);
    }

    [TestMethod]
    public async Task SetItemChecked_SetsAttribution_AndClearCheckedRemovesOnlyChecked()
    {
        var list = await _service.CreateShoppingListAsync(
            new CreateShoppingListRequest("Groceries")
        );

        var item = await _service.AddItemAsync(
            list.Id,
            new CreateShoppingListItemRequest("Avocados", 3m, null, MeasurementUnit.Whole)
        );
        Assert.IsNotNull(item);

        var checkedItem = await _service.SetItemCheckedAsync(
            list.Id,
            item.Id,
            new SetShoppingListItemCheckedRequest(true)
        );
        Assert.IsNotNull(checkedItem);
        Assert.IsTrue(checkedItem.IsChecked);
        Assert.AreEqual(_user.Id, checkedItem.CheckedByUserId);
        Assert.AreEqual("Chef Tyler", checkedItem.CheckedByDisplayName);

        var detailAfterCheck = await _service.GetShoppingListByIdAsync(list.Id);
        Assert.IsNotNull(detailAfterCheck);
        Assert.AreEqual(0, detailAfterCheck.ActiveItems.Count);
        Assert.AreEqual(1, detailAfterCheck.CheckedItems.Count);

        var clearResult = await _service.ClearCheckedItemsAsync(list.Id);
        Assert.IsNotNull(clearResult);
        Assert.AreEqual(1, clearResult.RemovedCount);

        var detailAfterClear = await _service.GetShoppingListByIdAsync(list.Id);
        Assert.IsNotNull(detailAfterClear);
        Assert.AreEqual(0, detailAfterClear.CheckedItems.Count);
    }

    [TestMethod]
    public async Task GenerateFromMealPlan_SeparatesPantryStaples_ExcludesLeftovers_AndPromotesStaple()
    {
        var list = await _service.CreateShoppingListAsync(
            new CreateShoppingListRequest("Weekly Groceries")
        );

        var scheduledDate = new DateOnly(2026, 11, 2); // Monday
        var weekMonday = new DateOnly(2026, 11, 2);

        // Monday dinner: Chicken Tacos (scaled to 8 servings = 2x)
        var entryTacos = new MealPlanEntry
        {
            Id = Guid.NewGuid(),
            WeekStartDate = weekMonday,
            ScheduledDate = scheduledDate,
            Slot = MealSlot.Dinner,
            EntryType = MealPlanEntryType.Recipe,
            RecipeId = _recipeTacos.Id,
            TargetServings = 8m,
            IsLeftovers = false,
        };

        // Wednesday dinner: Leftovers (should contribute ZERO ingredients)
        var entryLeftovers = new MealPlanEntry
        {
            Id = Guid.NewGuid(),
            WeekStartDate = weekMonday,
            ScheduledDate = new DateOnly(2026, 11, 4),
            Slot = MealSlot.Dinner,
            EntryType = MealPlanEntryType.Leftovers,
            RecipeId = _recipeTacos.Id,
            IsLeftovers = true,
        };

        // Friday dinner: Freeform note (should contribute ZERO ingredients)
        var entryNote = new MealPlanEntry
        {
            Id = Guid.NewGuid(),
            WeekStartDate = weekMonday,
            ScheduledDate = new DateOnly(2026, 11, 6),
            Slot = MealSlot.Dinner,
            EntryType = MealPlanEntryType.FreeformNote,
            CustomTitle = "Pizza Night",
            IsLeftovers = false,
        };

        _dbContext.MealPlanEntries.AddRange(entryTacos, entryLeftovers, entryNote);
        await _dbContext.SaveChangesAsync();

        var genRequest = new GenerateFromMealPlanRequest(
            new DateOnly(2026, 11, 2),
            new DateOnly(2026, 11, 8),
            IncludeOnDeck: true,
            IncludeSubRecipes: true
        );

        // Preview
        var preview = await _service.PreviewGenerateFromMealPlanAsync(list.Id, genRequest);
        Assert.IsNotNull(preview);
        Assert.AreEqual(1, preview.EligibleMealsCount);
        Assert.AreEqual(1, preview.IgnoredLeftoversCount);
        Assert.AreEqual(1, preview.IgnoredNotesCount);
        Assert.AreEqual(1, preview.ActiveCandidateItems.Count); // Garlic
        Assert.AreEqual(1, preview.ExcludedPantryCandidateItems.Count); // Olive oil

        // Execute Generation
        var result = await _service.GenerateFromMealPlanAsync(list.Id, genRequest);
        Assert.IsNotNull(result);

        var detail = result.ShoppingList;
        Assert.AreEqual(1, detail.ActiveItems.Count);
        Assert.AreEqual(1, detail.ExcludedPantryItems.Count);

        // Garlic should be: 2x base recipe (2*2 = 4 cloves) + 2x sub-recipe (2*2 = 4 cloves) = 8 cloves!
        var garlicItem = detail.ActiveItems[0];
        Assert.AreEqual(_foodGarlic.Id, garlicItem.FoodItemId);
        Assert.AreEqual(8m, garlicItem.Quantity);

        // Olive oil should be: 2x 2 tbsp = 4 tbsp = 1/4 cup in excluded pantry staples
        var oilItem = detail.ExcludedPantryItems[0];
        Assert.AreEqual(_foodOil.Id, oilItem.FoodItemId);
        Assert.IsTrue(oilItem.IsPantryStapleExcluded);

        // Promote Olive Oil to active
        var promoted = await _service.PromotePantryStapleItemAsync(list.Id, oilItem.Id);
        Assert.IsNotNull(promoted);
        Assert.IsFalse(promoted.IsPantryStapleExcluded);

        var detailAfterPromotion = await _service.GetShoppingListByIdAsync(list.Id);
        Assert.IsNotNull(detailAfterPromotion);
        Assert.AreEqual(2, detailAfterPromotion.ActiveItems.Count);
        Assert.AreEqual(0, detailAfterPromotion.ExcludedPantryItems.Count);
    }
}

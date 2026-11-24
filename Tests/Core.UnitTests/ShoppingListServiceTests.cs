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

    [TestMethod]
    public async Task AddRecipeToShoppingList_NormalizesSubRecipeByServingsAndVolumeYield()
    {
        var beefFood = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Ground Beef",
            NormalizedName = "ground beef",
            IsPantryStaple = false,
        };
        _dbContext.FoodItems.Add(beefFood);

        var beefSub = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Spiced Beef Topping",
            Slug = "spiced-beef-topping",
            BaseServings = 4m,
            YieldDisplay = "Serves 4",
            Status = RecipeStatus.Published,
            Ingredients =
            [
                new RecipeIngredient
                {
                    Id = Guid.NewGuid(),
                    FoodItemId = beefFood.Id,
                    FoodItem = beefFood,
                    DisplayName = "ground beef",
                    Quantity = 1m,
                    Unit = MeasurementUnit.Lb,
                    SortOrder = 0,
                },
            ],
        };

        var loadedHummus = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Loaded Hummus",
            Slug = "loaded-hummus",
            BaseServings = 4m,
            Status = RecipeStatus.Published,
            Ingredients =
            [
                new RecipeIngredient
                {
                    Id = Guid.NewGuid(),
                    LinkedRecipeId = beefSub.Id,
                    LinkedRecipe = beefSub,
                    DisplayName = "Spiced Beef Topping",
                    Quantity = 4m,
                    Unit = MeasurementUnit.Custom,
                    CustomUnitName = "servings",
                    OriginalRawText = "4 servings Spiced Beef Topping",
                    SortOrder = 0,
                },
            ],
        };

        _dbContext.Recipes.AddRange(beefSub, loadedHummus);
        await _dbContext.SaveChangesAsync();

        var list = await _service.CreateShoppingListAsync(
            new CreateShoppingListRequest("Sub-Recipe Scaling Test")
        );

        var res = await _service.AddRecipeIngredientsAsync(
            list.Id,
            new AddRecipeToShoppingListRequest(
                RecipeId: loadedHummus.Id,
                TargetServings: 4m,
                IncludeSubRecipes: true
            )
        );

        Assert.IsNotNull(res);
        var beefItem = res.ShoppingList.ActiveItems.Single(i => i.FoodItemId == beefFood.Id);
        // 4 servings of a 4-serving sub-recipe = 1x batch -> 1 lb ground beef (NOT 4 lb!)
        Assert.AreEqual(1m, beefItem.Quantity);
        Assert.AreEqual(MeasurementUnit.Lb, beefItem.Unit);
    }

    [TestMethod]
    public async Task PreviewAndCreateAndGenerateFromMealPlan_CreatesNewWeeklyListInOneStep()
    {
        var weekStart = new DateOnly(2026, 11, 2);
        _dbContext.MealPlanEntries.Add(
            new MealPlanEntry
            {
                Id = Guid.NewGuid(),
                WeekStartDate = MealPlanDateHelper.NormalizeToSunday(weekStart),
                ScheduledDate = weekStart,
                Slot = MealSlot.Dinner,
                EntryType = MealPlanEntryType.Recipe,
                RecipeId = _recipeTacos.Id,
                TargetServings = 8m,
                IsLeftovers = false,
            }
        );
        await _dbContext.SaveChangesAsync();

        var genRequest = new GenerateFromMealPlanRequest(
            StartDate: weekStart,
            EndDate: weekStart.AddDays(6),
            IncludeOnDeck: true,
            IncludeSubRecipes: true
        );

        var preview = await _service.PreviewGenerateFromNewListAsync(genRequest);
        Assert.AreEqual(1, preview.EligibleMealsCount);
        Assert.AreEqual(0, preview.AlreadyImportedMeals.Count);
        Assert.AreEqual(1, preview.ActiveCandidateItems.Count);
        Assert.AreEqual(1, preview.ExcludedPantryCandidateItems.Count);

        var createdResult = await _service.CreateAndGenerateFromMealPlanAsync(
            new CreateAndGenerateFromMealPlanRequest(
                Name: "Week of Nov 2 - Nov 8",
                IsPinned: false,
                Generation: genRequest
            )
        );

        Assert.AreEqual("Week of Nov 2 - Nov 8", createdResult.ShoppingList.Name);
        Assert.AreEqual(1, createdResult.ImportedMealsCount);
        Assert.AreEqual(8m, createdResult.ShoppingList.ActiveItems.Single().Quantity);

        // Invalid date range must fail before creating a list
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            _service.CreateAndGenerateFromMealPlanAsync(
                new CreateAndGenerateFromMealPlanRequest(
                    Name: "Should Not Exist",
                    IsPinned: false,
                    Generation: genRequest with
                    {
                        StartDate = weekStart.AddDays(5),
                        EndDate = weekStart,
                    }
                )
            )
        );
        Assert.IsFalse(await _dbContext.ShoppingLists.AnyAsync(l => l.Name == "Should Not Exist"));
    }

    [TestMethod]
    public async Task CreateAndAddRecipeIngredients_CreatesNewListAndPopulatesScaledIngredients_AndRejectsMissingRecipeWithoutOrphanList()
    {
        var recipeRequest = new AddRecipeToShoppingListRequest(
            RecipeId: _recipeTacos.Id,
            TargetServings: 8m,
            IncludeSubRecipes: true,
            IncludeOptionalIngredients: true
        );

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            _service.CreateAndAddRecipeIngredientsAsync(
                new CreateAndAddRecipeToShoppingListRequest(
                    Name: "Orphan List Check",
                    IsPinned: false,
                    Recipe: recipeRequest with
                    {
                        RecipeId = Guid.NewGuid(),
                    }
                )
            )
        );
        Assert.IsFalse(await _dbContext.ShoppingLists.AnyAsync(l => l.Name == "Orphan List Check"));

        var createdResult = await _service.CreateAndAddRecipeIngredientsAsync(
            new CreateAndAddRecipeToShoppingListRequest(
                Name: "Taco Night List",
                IsPinned: false,
                Recipe: recipeRequest
            )
        );

        Assert.AreEqual("Taco Night List", createdResult.ShoppingList.Name);
        Assert.AreEqual(
            8m,
            createdResult
                .ShoppingList.ActiveItems.Single(i => i.FoodItemId == _foodGarlic.Id)
                .Quantity
        );
    }
}

using System.Text.Json;
using App.Core.Exceptions;
using App.Core.FoodCatalog;
using App.Core.Ingestion;
using App.Core.Recipes;
using App.Core.Shopping;
using App.Data;
using App.Data.Entities;
using App.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class FoodCatalogMergeServiceTests
{
    private AppDbContext _dbContext = null!;
    private FoodCatalogService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new AppDbContext(options);
        _service = new FoodCatalogService(_dbContext);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dbContext.Dispose();
    }

    [TestMethod]
    public async Task MergeFoodItems_ValidationErrors_ThrowsDomainValidationException()
    {
        // Target is empty
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            _service.MergeFoodItemsAsync(new MergeFoodItemsRequest(Guid.Empty, [Guid.NewGuid()]))
        );

        // Sources empty
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            _service.MergeFoodItemsAsync(new MergeFoodItemsRequest(Guid.NewGuid(), []))
        );

        // Self-merge
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            _service.MergeFoodItemsAsync(new MergeFoodItemsRequest(id, [id]))
        );
    }

    [TestMethod]
    public async Task MergeFoodItems_TargetOrSourceNotFound_ThrowsNotFoundException()
    {
        var targetId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();

        // Target missing
        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.MergeFoodItemsAsync(new MergeFoodItemsRequest(targetId, [sourceId]))
        );

        // Create target, source missing
        var target = new FoodItem
        {
            Id = targetId,
            Name = "Garlic",
            NormalizedName = "garlic",
        };
        _dbContext.FoodItems.Add(target);
        await _dbContext.SaveChangesAsync();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.MergeFoodItemsAsync(new MergeFoodItemsRequest(targetId, [sourceId]))
        );
    }

    [TestMethod]
    public async Task MergeFoodItems_RepointsRecipeIngredients_AndPreservesAliases_AndInheritsAisle()
    {
        var aisle = new StoreAisle
        {
            Id = Guid.NewGuid(),
            Name = "Produce",
            SortOrder = 1,
        };
        _dbContext.StoreAisles.Add(aisle);

        var target = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Green Onion",
            NormalizedName = "green onion",
            DefaultStoreAisleId = null,
        };
        _dbContext.FoodItems.Add(target);

        var source1 = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Scallion",
            NormalizedName = "scallion",
            DefaultStoreAisleId = aisle.Id,
        };
        source1.Aliases.Add(
            new FoodItemAlias
            {
                Id = Guid.NewGuid(),
                FoodItemId = source1.Id,
                Alias = "Spring Onion",
                NormalizedAlias = "spring onion",
            }
        );
        _dbContext.FoodItems.Add(source1);

        var source2 = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Salad Onion",
            NormalizedName = "salad onion",
        };
        _dbContext.FoodItems.Add(source2);

        var recipe = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Fried Rice",
            Slug = "fried-rice",
        };
        var ing1 = new RecipeIngredient
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            FoodItemId = source1.Id,
            DisplayName = "Scallions",
            Quantity = 2,
            Unit = MeasurementUnit.Bunch,
        };
        var ing2 = new RecipeIngredient
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            FoodItemId = source2.Id,
            DisplayName = "Salad Onions",
            Quantity = 1,
            Unit = MeasurementUnit.Bunch,
        };
        recipe.Ingredients.Add(ing1);
        recipe.Ingredients.Add(ing2);
        _dbContext.Recipes.Add(recipe);

        await _dbContext.SaveChangesAsync();

        var result = await _service.MergeFoodItemsAsync(
            new MergeFoodItemsRequest(
                TargetFoodItemId: target.Id,
                SourceFoodItemIds: [source1.Id, source2.Id],
                KeepSourceNamesAsAliases: true
            )
        );

        Assert.AreEqual(2, result.MergedSourceCount);
        Assert.AreEqual(2, result.RepointedRecipeIngredientCount);
        Assert.IsTrue(result.AddedAliases.Contains("Scallion"));
        Assert.IsTrue(result.AddedAliases.Contains("Spring Onion"));
        Assert.IsTrue(result.AddedAliases.Contains("Salad Onion"));

        // Refresh target
        var refreshedTarget = await _dbContext
            .FoodItems.Include(f => f.Aliases)
            .FirstAsync(f => f.Id == target.Id);
        Assert.AreEqual(aisle.Id, refreshedTarget.DefaultStoreAisleId);
        Assert.AreEqual(3, refreshedTarget.Aliases.Count);

        // Sources should be removed
        Assert.IsFalse(
            await _dbContext.FoodItems.AnyAsync(f => f.Id == source1.Id || f.Id == source2.Id)
        );

        // Recipe ingredients repointed to target
        var refreshedIng1 = await _dbContext.RecipeIngredients.FindAsync(ing1.Id);
        var refreshedIng2 = await _dbContext.RecipeIngredients.FindAsync(ing2.Id);
        Assert.AreEqual(target.Id, refreshedIng1!.FoodItemId);
        Assert.AreEqual(target.Id, refreshedIng2!.FoodItemId);
    }

    [TestMethod]
    public async Task MergeFoodItems_ConsolidatesShoppingListItems_WhenCompatibleDimensions()
    {
        var target = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Olive Oil",
            NormalizedName = "olive oil",
        };
        var source = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Extra Virgin Olive Oil",
            NormalizedName = "extra virgin olive oil",
        };
        _dbContext.FoodItems.AddRange(target, source);

        var list = new ShoppingList
        {
            Id = Guid.NewGuid(),
            Name = "Weekly Groceries",
            NormalizedName = "weekly groceries",
        };
        _dbContext.ShoppingLists.Add(list);

        var targetItem = new ShoppingListItem
        {
            Id = Guid.NewGuid(),
            ShoppingListId = list.Id,
            FoodItemId = target.Id,
            DisplayName = "Olive Oil",
            Quantity = 1,
            Unit = MeasurementUnit.Cup,
            Dimension = MeasurementDimension.Volume,
            IsChecked = false,
        };
        targetItem.Sources.Add(
            new ShoppingListItemSource
            {
                Id = Guid.NewGuid(),
                ShoppingListItemId = targetItem.Id,
                SourceLabel = "Recipe A",
                ContributedQuantity = 1,
            }
        );

        var sourceItem = new ShoppingListItem
        {
            Id = Guid.NewGuid(),
            ShoppingListId = list.Id,
            FoodItemId = source.Id,
            DisplayName = "Extra Virgin Olive Oil",
            Quantity = 2,
            Unit = MeasurementUnit.Tbsp,
            Dimension = MeasurementDimension.Volume,
            IsChecked = false,
        };
        sourceItem.Sources.Add(
            new ShoppingListItemSource
            {
                Id = Guid.NewGuid(),
                ShoppingListItemId = sourceItem.Id,
                SourceLabel = "Recipe B",
                ContributedQuantity = 2,
            }
        );

        _dbContext.ShoppingListItems.AddRange(targetItem, sourceItem);
        await _dbContext.SaveChangesAsync();

        var result = await _service.MergeFoodItemsAsync(
            new MergeFoodItemsRequest(
                TargetFoodItemId: target.Id,
                SourceFoodItemIds: [source.Id],
                KeepSourceNamesAsAliases: true
            )
        );

        Assert.AreEqual(1, result.MergedSourceCount);
        Assert.AreEqual(1, result.RepointedShoppingListItemCount);
        Assert.AreEqual(1, result.ConsolidatedShoppingListItemCount);

        var remainingItems = await _dbContext
            .ShoppingListItems.Include(i => i.Sources)
            .Where(i => i.ShoppingListId == list.Id)
            .ToListAsync();

        Assert.AreEqual(1, remainingItems.Count);
        var mergedItem = remainingItems[0];
        Assert.AreEqual(target.Id, mergedItem.FoodItemId);
        Assert.AreEqual(target.Name, mergedItem.DisplayName);
        Assert.AreEqual(2, mergedItem.Sources.Count);
    }

    [TestMethod]
    public async Task MergeFoodItems_UpdatesReadyForReviewIngestionDrafts()
    {
        var target = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Cilantro",
            NormalizedName = "cilantro",
        };
        var source = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Fresh Coriander",
            NormalizedName = "fresh coriander",
        };
        _dbContext.FoodItems.AddRange(target, source);

        var draft = new IngestionExtractedDraftDto(
            Title: "Guacamole",
            Ingredients:
            [
                new IngestionDraftIngredientDto(
                    DisplayName: "Fresh Coriander",
                    MatchedFoodItemId: source.Id,
                    MatchedFoodItemName: source.Name
                ),
            ]
        );

        var job = new IngestionJob
        {
            Id = Guid.NewGuid(),
            Channel = IngestionChannel.RawText,
            Status = IngestionJobStatus.ReadyForReview,
            ExtractedDraftJson = JsonSerializer.Serialize(draft),
        };
        _dbContext.IngestionJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        await _service.MergeFoodItemsAsync(
            new MergeFoodItemsRequest(TargetFoodItemId: target.Id, SourceFoodItemIds: [source.Id])
        );

        var updatedJob = await _dbContext.IngestionJobs.FindAsync(job.Id);
        var updatedDraft = JsonSerializer.Deserialize<IngestionExtractedDraftDto>(
            updatedJob!.ExtractedDraftJson!
        );
        Assert.IsNotNull(updatedDraft);
        Assert.AreEqual(target.Id, updatedDraft.Ingredients![0].MatchedFoodItemId);
        Assert.AreEqual(target.Name, updatedDraft.Ingredients[0].MatchedFoodItemName);
    }
}

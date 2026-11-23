using App.Core.Exceptions;
using App.Core.FoodCatalog;
using App.Data;
using App.Data.Entities;
using App.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class FoodCatalogBulkDeleteTests
{
    private AppDbContext _dbContext = null!;
    private FoodCatalogService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"FoodCatalogBulkDeleteTests_{Guid.NewGuid()}")
            .Options;

        _dbContext = new AppDbContext(options);
        _service = new FoodCatalogService(_dbContext);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dbContext.Database.EnsureDeleted();
        _dbContext.Dispose();
    }

    [TestMethod]
    public async Task BulkDeleteFoodItemsAsync_DeletesUnusedItemsAndAliases_AndUnlinksShoppingItems()
    {
        var item1 = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Unused Herb",
            NormalizedName = "unused herb",
            Aliases = [new FoodItemAlias { Alias = "Herb A", NormalizedAlias = "herb a" }],
        };
        var item2 = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Unused Spice",
            NormalizedName = "unused spice",
        };
        var kept = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Kept Flour",
            NormalizedName = "kept flour",
        };

        var list = new ShoppingList
        {
            Id = Guid.NewGuid(),
            Name = "Groceries",
            NormalizedName = "groceries",
        };
        var shoppingItem = new ShoppingListItem
        {
            Id = Guid.NewGuid(),
            ShoppingListId = list.Id,
            FoodItemId = item1.Id,
            DisplayName = "Unused Herb",
        };

        _dbContext.FoodItems.AddRange(item1, item2, kept);
        _dbContext.ShoppingLists.Add(list);
        _dbContext.ShoppingListItems.Add(shoppingItem);
        await _dbContext.SaveChangesAsync();

        var result = await _service.BulkDeleteFoodItemsAsync(
            new BulkDeleteFoodItemsRequest([item1.Id, item2.Id, item1.Id, Guid.NewGuid()])
        );

        Assert.AreEqual(2, result.DeletedCount);
        Assert.AreEqual(0, result.UnlinkedRecipeCount);
        Assert.AreEqual(0, result.UnlinkedIngredientCount);
        Assert.AreEqual(1, result.UnlinkedShoppingListItemCount);

        Assert.IsNull(await _service.GetFoodItemByIdAsync(item1.Id));
        Assert.IsNull(await _service.GetFoodItemByIdAsync(item2.Id));
        Assert.IsNotNull(await _service.GetFoodItemByIdAsync(kept.Id));
        Assert.AreEqual(0, await _dbContext.FoodItemAliases.CountAsync());

        var updatedShoppingItem = await _dbContext.ShoppingListItems.FindAsync(shoppingItem.Id);
        Assert.IsNotNull(updatedShoppingItem);
        Assert.IsNull(updatedShoppingItem.FoodItemId);
    }

    [TestMethod]
    public async Task BulkDeleteFoodItemsAsync_ReferencedByIngredientOrReplacement_ThrowsConflictAndLeavesAllIntact()
    {
        var unused = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Unused",
            NormalizedName = "unused",
        };
        var usedAsIngredient = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Used Main",
            NormalizedName = "used main",
        };
        var usedAsReplacement = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Used Sub",
            NormalizedName = "used sub",
        };

        var recipe = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Test Soup",
            Slug = "test-soup",
        };
        var ingredient = new RecipeIngredient
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            FoodItemId = usedAsIngredient.Id,
            SortOrder = 1,
            DisplayName = "Used Main",
        };
        var replacement = new RecipeIngredientReplacement
        {
            Id = Guid.NewGuid(),
            RecipeIngredientId = ingredient.Id,
            FoodItemId = usedAsReplacement.Id,
            SortOrder = 1,
            DisplayName = "Used Sub",
        };

        _dbContext.FoodItems.AddRange(unused, usedAsIngredient, usedAsReplacement);
        _dbContext.Recipes.Add(recipe);
        _dbContext.RecipeIngredients.Add(ingredient);
        _dbContext.RecipeIngredientReplacements.Add(replacement);
        await _dbContext.SaveChangesAsync();

        await Assert.ThrowsExactlyAsync<ConflictException>(() =>
            _service.BulkDeleteFoodItemsAsync(
                new BulkDeleteFoodItemsRequest(
                    [unused.Id, usedAsIngredient.Id, usedAsReplacement.Id],
                    UnlinkFromRecipes: false
                )
            )
        );

        // Ensure NO partial deletion occurred
        Assert.IsNotNull(await _service.GetFoodItemByIdAsync(unused.Id));
        Assert.IsNotNull(await _service.GetFoodItemByIdAsync(usedAsIngredient.Id));
        Assert.IsNotNull(await _service.GetFoodItemByIdAsync(usedAsReplacement.Id));

        // Now with UnlinkFromRecipes = true
        var result = await _service.BulkDeleteFoodItemsAsync(
            new BulkDeleteFoodItemsRequest(
                [unused.Id, usedAsIngredient.Id, usedAsReplacement.Id],
                UnlinkFromRecipes: true
            )
        );

        Assert.AreEqual(3, result.DeletedCount);
        Assert.AreEqual(1, result.UnlinkedRecipeCount);
        Assert.AreEqual(1, result.UnlinkedIngredientCount);
        Assert.AreEqual(0, await _dbContext.FoodItems.CountAsync());

        var savedIng = await _dbContext.RecipeIngredients.FindAsync(ingredient.Id);
        var savedRep = await _dbContext.RecipeIngredientReplacements.FindAsync(replacement.Id);
        Assert.IsNotNull(savedIng);
        Assert.IsNull(savedIng.FoodItemId);
        Assert.IsNotNull(savedRep);
        Assert.IsNull(savedRep.FoodItemId);
    }

    [TestMethod]
    public async Task BulkDeleteFoodItemsAsync_ValidatesEmptyAndOversizedRequests()
    {
        await Assert.ThrowsExactlyAsync<DomainValidationException>(() =>
            _service.BulkDeleteFoodItemsAsync(new BulkDeleteFoodItemsRequest([]))
        );

        await Assert.ThrowsExactlyAsync<DomainValidationException>(() =>
            _service.BulkDeleteFoodItemsAsync(new BulkDeleteFoodItemsRequest([Guid.Empty]))
        );

        var overLimit = Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()).ToList();
        await Assert.ThrowsExactlyAsync<DomainValidationException>(() =>
            _service.BulkDeleteFoodItemsAsync(new BulkDeleteFoodItemsRequest(overLimit))
        );

        var missingOnly = await _service.BulkDeleteFoodItemsAsync(
            new BulkDeleteFoodItemsRequest([Guid.NewGuid()])
        );
        Assert.AreEqual(0, missingOnly.DeletedCount);
    }
}

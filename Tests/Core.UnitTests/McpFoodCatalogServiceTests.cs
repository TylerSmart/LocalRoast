using App.Core.Exceptions;
using App.Core.FoodCatalog;
using App.Core.Mcp;
using App.Data;
using App.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class McpFoodCatalogServiceTests
{
    private AppDbContext _dbContext = null!;
    private FoodCatalogService _foodCatalogService = null!;
    private McpFoodCatalogService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"McpFoodCatalogTests_{Guid.NewGuid()}")
            .Options;
        _dbContext = new AppDbContext(options);
        _foodCatalogService = new FoodCatalogService(_dbContext);
        _service = new McpFoodCatalogService(_dbContext, _foodCatalogService);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dbContext.Database.EnsureDeleted();
        _dbContext.Dispose();
    }

    [TestMethod]
    public async Task ListFoodItemsAsync_FiltersByPantryStapleCategoryAndSearch()
    {
        await _service.CreateFoodItemAsync(
            "Kosher Salt",
            isPantryStaple: true,
            category: "SpicesAndSeasonings",
            aliases: ["Coarse Salt"]
        );
        await _service.CreateFoodItemAsync(
            "Yellow Onion",
            isPantryStaple: false,
            category: "Produce"
        );

        var staples = await _service.ListFoodItemsAsync(
            new McpListFoodItemsRequest(IsPantryStaple: true)
        );
        Assert.AreEqual(1, staples.TotalCount);
        Assert.AreEqual("Kosher Salt", staples.Items[0].Name);

        var produce = await _service.ListFoodItemsAsync(
            new McpListFoodItemsRequest(Category: "Produce", Search: "onion")
        );
        Assert.AreEqual(1, produce.TotalCount);
        Assert.AreEqual("Yellow Onion", produce.Items[0].Name);

        await Assert.ThrowsExactlyAsync<DomainValidationException>(async () =>
            await _service.ListFoodItemsAsync(
                new McpListFoodItemsRequest(Category: "NotARealCategory")
            )
        );
    }

    [TestMethod]
    public async Task UpdateFoodItemsAsync_PartialPatchByIdAndCurrentName_PreservesUnspecifiedFields()
    {
        var salt = await _service.CreateFoodItemAsync(
            "Kosher Salt",
            isPantryStaple: true,
            category: "SpicesAndSeasonings",
            aliases: ["Coarse Salt"]
        );
        var oil = await _service.CreateFoodItemAsync(
            "Extra-Virgin Olive Oil",
            isPantryStaple: true,
            category: "CondimentsAndSauces",
            aliases: ["EVOO"]
        );

        var result = await _service.UpdateFoodItemsAsync(
            new McpUpdateFoodItemsRequest([
                new McpFoodItemUpdateInput(FoodItemId: salt.Id, IsPantryStaple: false),
                new McpFoodItemUpdateInput(CurrentName: "evoo", IsPantryStaple: false),
            ])
        );

        Assert.AreEqual(2, result.UpdatedCount);
        var updatedSalt = result.UpdatedItems.Single(i => i.Id == salt.Id);
        Assert.IsFalse(updatedSalt.IsPantryStaple);
        Assert.AreEqual("Kosher Salt", updatedSalt.Name);
        Assert.AreEqual(FoodCategory.SpicesAndSeasonings, updatedSalt.Category);
        CollectionAssert.Contains(updatedSalt.Aliases.ToList(), "Coarse Salt");

        var updatedOil = result.UpdatedItems.Single(i => i.Id == oil.Id);
        Assert.IsFalse(updatedOil.IsPantryStaple);
        Assert.AreEqual("Extra-Virgin Olive Oil", updatedOil.Name);
        Assert.AreEqual(FoodCategory.CondimentsAndSauces, updatedOil.Category);
        CollectionAssert.Contains(updatedOil.Aliases.ToList(), "EVOO");
    }

    [TestMethod]
    public async Task UpdateFoodItemsAsync_ValidatesMissingAndEmptyRequests()
    {
        await Assert.ThrowsExactlyAsync<DomainValidationException>(async () =>
            await _service.UpdateFoodItemsAsync(new McpUpdateFoodItemsRequest([]))
        );

        await Assert.ThrowsExactlyAsync<DomainValidationException>(async () =>
            await _service.UpdateFoodItemsAsync(
                new McpUpdateFoodItemsRequest([new McpFoodItemUpdateInput(IsPantryStaple: false)])
            )
        );

        await Assert.ThrowsExactlyAsync<NotFoundException>(async () =>
            await _service.UpdateFoodItemsAsync(
                new McpUpdateFoodItemsRequest([
                    new McpFoodItemUpdateInput(FoodItemId: Guid.NewGuid(), IsPantryStaple: false),
                ])
            )
        );
    }

    [TestMethod]
    public async Task MergeFoodItemsAsync_ConsolidatesDuplicatesIntoTarget()
    {
        var target = await _service.CreateFoodItemAsync("Scallion", category: "Produce");
        var source = await _service.CreateFoodItemAsync("Green Onion", category: "Produce");

        var mergeResult = await _service.MergeFoodItemsAsync(
            new MergeFoodItemsRequest(target.Id, [source.Id], KeepSourceNamesAsAliases: true)
        );

        Assert.AreEqual(1, mergeResult.MergedSourceCount);
        Assert.AreEqual(target.Id, mergeResult.TargetFoodItem.Id);
        CollectionAssert.Contains(mergeResult.TargetFoodItem.Aliases.ToList(), "Green Onion");
        Assert.IsNull(await _foodCatalogService.GetFoodItemByIdAsync(source.Id));
    }

    [TestMethod]
    public async Task ListFoodItemsAsync_SupportsSortingAndValidatesSortParameters()
    {
        var rare = await _service.CreateFoodItemAsync("Cardamom", category: "SpicesAndSeasonings");
        var common = await _service.CreateFoodItemAsync("Garlic", category: "Produce");
        var unused = await _service.CreateFoodItemAsync("Sumac", category: "SpicesAndSeasonings");

        var recipe1 = new App.Data.Entities.Recipe { Title = "Curry", Slug = "curry" };
        var recipe2 = new App.Data.Entities.Recipe { Title = "Stir Fry", Slug = "stir-fry" };
        _dbContext.Recipes.AddRange(recipe1, recipe2);
        _dbContext.RecipeIngredients.AddRange(
            new App.Data.Entities.RecipeIngredient
            {
                RecipeId = recipe1.Id,
                FoodItemId = common.Id,
                SortOrder = 1,
                DisplayName = "garlic",
            },
            new App.Data.Entities.RecipeIngredient
            {
                RecipeId = recipe2.Id,
                FoodItemId = common.Id,
                SortOrder = 1,
                DisplayName = "garlic",
            },
            new App.Data.Entities.RecipeIngredient
            {
                RecipeId = recipe1.Id,
                FoodItemId = rare.Id,
                SortOrder = 2,
                DisplayName = "cardamom",
            }
        );
        await _dbContext.SaveChangesAsync();

        var mostUsed = await _service.ListFoodItemsAsync(
            new McpListFoodItemsRequest(SortBy: "RecipeCount", SortDirection: "Desc")
        );
        CollectionAssert.AreEqual(
            new[] { common.Name, rare.Name, unused.Name },
            mostUsed.Items.Select(i => i.Name).ToArray()
        );

        var leastUsed = await _service.ListFoodItemsAsync(
            new McpListFoodItemsRequest(SortBy: "recipecount", SortDirection: "asc")
        );
        CollectionAssert.AreEqual(
            new[] { unused.Name, rare.Name, common.Name },
            leastUsed.Items.Select(i => i.Name).ToArray()
        );

        await Assert.ThrowsExactlyAsync<DomainValidationException>(async () =>
            await _service.ListFoodItemsAsync(new McpListFoodItemsRequest(SortBy: "InvalidField"))
        );

        await Assert.ThrowsExactlyAsync<DomainValidationException>(async () =>
            await _service.ListFoodItemsAsync(
                new McpListFoodItemsRequest(SortDirection: "Sideways")
            )
        );

        await Assert.ThrowsExactlyAsync<DomainValidationException>(async () =>
            await _service.ListFoodItemsAsync(new McpListFoodItemsRequest(SortBy: "99"))
        );
    }
}

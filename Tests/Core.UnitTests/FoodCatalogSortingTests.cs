using App.Core.FoodCatalog;
using App.Core.Recipes;
using App.Data;
using App.Data.Entities;
using App.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class FoodCatalogSortingTests
{
    private AppDbContext _dbContext = null!;
    private FoodCatalogService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"FoodCatalogSortingTests_{Guid.NewGuid()}")
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

    private async Task SeedCatalogAsync()
    {
        var baseTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        // Created order: Olive Oil (0h) -> butter (1h) -> Salt (2h) -> Anise (3h)
        // Modified order: Anise (3h) -> Salt (4h) -> Olive Oil (5h) -> butter (6h)
        // Name normalized order: Anise, butter, Olive Oil, Salt
        // Recipe counts: Olive Oil = 2, butter = 1 (2 ingredient rows in 1 recipe), Salt = 1, Anise = 0
        var oliveOil = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Olive Oil",
            NormalizedName = "olive oil",
            Category = FoodCategory.CondimentsAndSauces,
            CreatedAt = baseTime,
            ModifiedAt = baseTime.AddHours(5),
        };
        var butter = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "butter",
            NormalizedName = "butter",
            Category = FoodCategory.DairyAndEggs,
            CreatedAt = baseTime.AddHours(1),
            ModifiedAt = baseTime.AddHours(6),
        };
        var salt = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Salt",
            NormalizedName = "salt",
            Category = FoodCategory.SpicesAndSeasonings,
            CreatedAt = baseTime.AddHours(2),
            ModifiedAt = baseTime.AddHours(4),
        };
        var anise = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Anise",
            NormalizedName = "anise",
            Category = FoodCategory.SpicesAndSeasonings,
            CreatedAt = baseTime.AddHours(3),
            ModifiedAt = baseTime.AddHours(3),
        };

        var recipe1 = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Pasta A",
            Slug = "pasta-a",
            CreatedAt = baseTime,
            ModifiedAt = baseTime,
        };
        var recipe2 = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Salad B",
            Slug = "salad-b",
            CreatedAt = baseTime,
            ModifiedAt = baseTime,
        };
        var recipe3 = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Soup C",
            Slug = "soup-c",
            CreatedAt = baseTime,
            ModifiedAt = baseTime,
        };

        _dbContext.FoodItems.AddRange(oliveOil, butter, salt, anise);
        _dbContext.Recipes.AddRange(recipe1, recipe2, recipe3);
        _dbContext.RecipeIngredients.AddRange(
            // Olive Oil in 2 distinct recipes
            new RecipeIngredient
            {
                RecipeId = recipe1.Id,
                FoodItemId = oliveOil.Id,
                SortOrder = 1,
                DisplayName = "olive oil",
            },
            new RecipeIngredient
            {
                RecipeId = recipe2.Id,
                FoodItemId = oliveOil.Id,
                SortOrder = 1,
                DisplayName = "olive oil",
            },
            // butter used twice in the SAME recipe (counts as 1 distinct recipe)
            new RecipeIngredient
            {
                RecipeId = recipe1.Id,
                FoodItemId = butter.Id,
                SortOrder = 2,
                DisplayName = "butter, divided",
            },
            new RecipeIngredient
            {
                RecipeId = recipe1.Id,
                FoodItemId = butter.Id,
                SortOrder = 3,
                DisplayName = "butter for finishing",
            },
            // Salt in 1 recipe
            new RecipeIngredient
            {
                RecipeId = recipe3.Id,
                FoodItemId = salt.Id,
                SortOrder = 1,
                DisplayName = "kosher salt",
            }
        );

        await _dbContext.SaveChangesAsync();
    }

    [TestMethod]
    public async Task GetFoodItems_SupportsAllSortFieldsAndDirections_PagedAndUnpaged()
    {
        await SeedCatalogAsync();

        async Task AssertOrderAsync(
            FoodItemSortBy? sortBy,
            FoodItemSortDirection? sortDirection,
            params string[] expectedNames
        )
        {
            var query = new FoodItemQueryParameters(
                Page: 1,
                PageSize: 10,
                SortBy: sortBy,
                SortDirection: sortDirection
            );
            var paged = await _service.GetFoodItemsPagedAsync(query);
            var unpaged = await _service.GetFoodItemsAsync(
                query with
                {
                    Page = null,
                    PageSize = null,
                }
            );

            CollectionAssert.AreEqual(
                expectedNames,
                paged.Items.Select(i => i.Name).ToArray(),
                $"Paged mismatch for {sortBy} {sortDirection}"
            );
            CollectionAssert.AreEqual(
                expectedNames,
                unpaged.Select(i => i.Name).ToArray(),
                $"Unpaged mismatch for {sortBy} {sortDirection}"
            );
        }

        // Default (Name Asc, case-insensitive)
        await AssertOrderAsync(null, null, "Anise", "butter", "Olive Oil", "Salt");
        await AssertOrderAsync(
            FoodItemSortBy.Name,
            FoodItemSortDirection.Asc,
            "Anise",
            "butter",
            "Olive Oil",
            "Salt"
        );
        await AssertOrderAsync(
            FoodItemSortBy.Name,
            FoodItemSortDirection.Desc,
            "Salt",
            "Olive Oil",
            "butter",
            "Anise"
        );

        // RecipeCount Desc (distinct recipes: Olive Oil 2, butter 1, Salt 1, Anise 0)
        await AssertOrderAsync(
            FoodItemSortBy.RecipeCount,
            FoodItemSortDirection.Desc,
            "Olive Oil",
            "butter",
            "Salt",
            "Anise"
        );

        // RecipeCount Asc (distinct recipes: Anise 0, butter 1, Salt 1, Olive Oil 2)
        await AssertOrderAsync(
            FoodItemSortBy.RecipeCount,
            FoodItemSortDirection.Asc,
            "Anise",
            "butter",
            "Salt",
            "Olive Oil"
        );

        // CreatedAt Asc & Desc
        await AssertOrderAsync(
            FoodItemSortBy.CreatedAt,
            FoodItemSortDirection.Asc,
            "Olive Oil",
            "butter",
            "Salt",
            "Anise"
        );
        await AssertOrderAsync(
            FoodItemSortBy.CreatedAt,
            FoodItemSortDirection.Desc,
            "Anise",
            "Salt",
            "butter",
            "Olive Oil"
        );

        // ModifiedAt Asc & Desc
        await AssertOrderAsync(
            FoodItemSortBy.ModifiedAt,
            FoodItemSortDirection.Asc,
            "Anise",
            "Salt",
            "Olive Oil",
            "butter"
        );
        await AssertOrderAsync(
            FoodItemSortBy.ModifiedAt,
            FoodItemSortDirection.Desc,
            "butter",
            "Olive Oil",
            "Salt",
            "Anise"
        );
    }

    [TestMethod]
    public async Task GetFoodItemsPagedAsync_PreservesGlobalSortOrderAcrossPages()
    {
        await SeedCatalogAsync();

        var page1 = await _service.GetFoodItemsPagedAsync(
            new FoodItemQueryParameters(
                Page: 1,
                PageSize: 2,
                SortBy: FoodItemSortBy.RecipeCount,
                SortDirection: FoodItemSortDirection.Desc
            )
        );
        var page2 = await _service.GetFoodItemsPagedAsync(
            new FoodItemQueryParameters(
                Page: 2,
                PageSize: 2,
                SortBy: FoodItemSortBy.RecipeCount,
                SortDirection: FoodItemSortDirection.Desc
            )
        );

        CollectionAssert.AreEqual(
            new[] { "Olive Oil", "butter" },
            page1.Items.Select(i => i.Name).ToArray()
        );
        Assert.AreEqual(2, page1.Items[0].ReferencedRecipeCount);
        Assert.AreEqual(1, page1.Items[1].ReferencedRecipeCount);

        CollectionAssert.AreEqual(
            new[] { "Salt", "Anise" },
            page2.Items.Select(i => i.Name).ToArray()
        );
        Assert.AreEqual(1, page2.Items[0].ReferencedRecipeCount);
        Assert.AreEqual(0, page2.Items[1].ReferencedRecipeCount);
    }
}

using App.Core.FoodCatalog;
using Microsoft.EntityFrameworkCore;

namespace App.Data.Services;

public static class FoodCategoryBackfill
{
    /// <summary>
    /// Assigns a keyword-classified category to food items created before categories existed.
    /// Items that already have a category (AI-suggested or user-chosen) are never touched.
    /// </summary>
    public static async Task<int> AssignMissingCategoriesAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken = default
    )
    {
        var uncategorized = await dbContext
            .FoodItems.Where(f => f.Category == null)
            .ToListAsync(cancellationToken);

        if (uncategorized.Count == 0)
        {
            return 0;
        }

        foreach (var foodItem in uncategorized)
        {
            foodItem.Category = FoodCategoryClassifier.Classify(foodItem.Name);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return uncategorized.Count;
    }
}

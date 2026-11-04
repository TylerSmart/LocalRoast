using App.Core.Maintenance;
using App.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace App.Data.Services;

public sealed class AppDataMaintenanceService(
    AppDbContext dbContext,
    IMediaStorageService mediaStorageService,
    ILogger<AppDataMaintenanceService> logger
) : IAppDataMaintenanceService
{
    public async Task<ResetAppDataResultDto> ResetAppDataAsync(
        CancellationToken cancellationToken = default
    )
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();

        var result = await strategy.ExecuteAsync(async () =>
        {
            await using var tx = dbContext.Database.IsRelational()
                ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
                : null;

            logger.LogWarning(
                "Executing nuclear app data reset: wiping all culinary data while preserving user accounts."
            );

            // Stage 1: Leaf dependencies & ingestion jobs
            var cookLogs = await dbContext.CookLogs.ToListAsync(cancellationToken);
            dbContext.CookLogs.RemoveRange(cookLogs);

            var listSources = await dbContext.ShoppingListItemSources.ToListAsync(
                cancellationToken
            );
            dbContext.ShoppingListItemSources.RemoveRange(listSources);

            var listItems = await dbContext.ShoppingListItems.ToListAsync(cancellationToken);
            dbContext.ShoppingListItems.RemoveRange(listItems);

            var shoppingLists = await dbContext.ShoppingLists.ToListAsync(cancellationToken);
            dbContext.ShoppingLists.RemoveRange(shoppingLists);

            var ingestionJobs = await dbContext.IngestionJobs.ToListAsync(cancellationToken);
            dbContext.IngestionJobs.RemoveRange(ingestionJobs);

            await dbContext.SaveChangesAsync(cancellationToken);

            // Stage 2: Meal plans & templates
            var templateItems = await dbContext.MealPlanTemplateItems.ToListAsync(
                cancellationToken
            );
            dbContext.MealPlanTemplateItems.RemoveRange(templateItems);

            var templates = await dbContext.MealPlanTemplates.ToListAsync(cancellationToken);
            dbContext.MealPlanTemplates.RemoveRange(templates);

            var mealPlanEntries = await dbContext.MealPlanEntries.ToListAsync(cancellationToken);
            dbContext.MealPlanEntries.RemoveRange(mealPlanEntries);

            await dbContext.SaveChangesAsync(cancellationToken);

            // Stage 3: Recipe children (steps, ingredients, timers, tags, collections, attachments, share links)
            var stepTimers = await dbContext.RecipeStepTimers.ToListAsync(cancellationToken);
            dbContext.RecipeStepTimers.RemoveRange(stepTimers);

            var stepIngredients = await dbContext.RecipeStepIngredients.ToListAsync(
                cancellationToken
            );
            dbContext.RecipeStepIngredients.RemoveRange(stepIngredients);

            var steps = await dbContext.RecipeSteps.ToListAsync(cancellationToken);
            dbContext.RecipeSteps.RemoveRange(steps);

            var recipeIngredients = await dbContext.RecipeIngredients.ToListAsync(
                cancellationToken
            );
            dbContext.RecipeIngredients.RemoveRange(recipeIngredients);

            var recipeTags = await dbContext.RecipeTags.ToListAsync(cancellationToken);
            dbContext.RecipeTags.RemoveRange(recipeTags);

            var recipeCollections = await dbContext.RecipeCollections.ToListAsync(
                cancellationToken
            );
            dbContext.RecipeCollections.RemoveRange(recipeCollections);

            var recipeAttachments = await dbContext.RecipeAttachments.ToListAsync(
                cancellationToken
            );
            dbContext.RecipeAttachments.RemoveRange(recipeAttachments);

            var shareLinks = await dbContext.ShareLinks.ToListAsync(cancellationToken);
            dbContext.ShareLinks.RemoveRange(shareLinks);

            await dbContext.SaveChangesAsync(cancellationToken);

            // Stage 4: Recipes
            var recipes = await dbContext.Recipes.ToListAsync(cancellationToken);
            dbContext.Recipes.RemoveRange(recipes);

            await dbContext.SaveChangesAsync(cancellationToken);

            // Stage 5: Food items & aliases
            var aliases = await dbContext.FoodItemAliases.ToListAsync(cancellationToken);
            dbContext.FoodItemAliases.RemoveRange(aliases);

            var foodItems = await dbContext.FoodItems.ToListAsync(cancellationToken);
            dbContext.FoodItems.RemoveRange(foodItems);

            await dbContext.SaveChangesAsync(cancellationToken);

            // Stage 6: Tags & Collections
            var tags = await dbContext.Tags.ToListAsync(cancellationToken);
            dbContext.Tags.RemoveRange(tags);

            var collections = await dbContext.Collections.ToListAsync(cancellationToken);
            dbContext.Collections.RemoveRange(collections);

            await dbContext.SaveChangesAsync(cancellationToken);

            if (tx is not null)
            {
                await tx.CommitAsync(cancellationToken);
            }

            logger.LogWarning(
                "Nuclear app data reset completed: {RecipeCount} recipes, {FoodItemCount} food items, {JobCount} ingestion jobs wiped.",
                recipes.Count,
                foodItems.Count,
                ingestionJobs.Count
            );

            return new ResetAppDataResultDto(
                DeletedRecipesCount: recipes.Count,
                DeletedMealPlansCount: mealPlanEntries.Count + templates.Count,
                DeletedShoppingListsCount: shoppingLists.Count,
                DeletedIngestionJobsCount: ingestionJobs.Count,
                DeletedFoodItemsCount: foodItems.Count,
                DeletedTagsCount: tags.Count,
                DeletedCollectionsCount: collections.Count,
                ResetAt: DateTimeOffset.UtcNow
            );
        });

        // Purge media uploads on disk only after database transaction successfully commits
        await mediaStorageService.PurgeAllMediaAsync(cancellationToken);

        return result;
    }
}

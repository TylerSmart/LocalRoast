using System.Text.Json;
using App.Core.Ingestion;
using App.Core.Maintenance;
using App.Core.Media;
using App.Core.Recipes;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace App.Data.Services;

public sealed class AppDataMaintenanceService(
    AppDbContext dbContext,
    IMediaStorageService mediaStorageService,
    ILogger<AppDataMaintenanceService> logger,
    IIngestionQueueSignal? queueSignal = null
) : IAppDataMaintenanceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<ResetAppDataResultDto> ResetAppDataAsync(
        ResetAppDataRequest? request = null,
        CancellationToken cancellationToken = default
    )
    {
        var keepIngestionJobs = request?.KeepIngestionJobs ?? false;

        await CancelActiveIngestionIfRunningAsync(cancellationToken);

        var preservedMediaPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var strategy = dbContext.Database.CreateExecutionStrategy();

        var result = await strategy.ExecuteAsync(async () =>
        {
            preservedMediaPaths.Clear();

            await using var tx = dbContext.Database.IsRelational()
                ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
                : null;

            logger.LogWarning(
                "Executing app data reset (KeepIngestionJobs={KeepIngestionJobs}): wiping culinary data while preserving user accounts.",
                keepIngestionJobs
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
            var deletedIngestionJobsCount = 0;
            var resetIngestionJobsCount = 0;
            var referencedCollectionIds = new HashSet<Guid>();

            if (!keepIngestionJobs)
            {
                dbContext.IngestionJobs.RemoveRange(ingestionJobs);
                deletedIngestionJobsCount = ingestionJobs.Count;
            }
            else
            {
                var discardedJobs = new List<IngestionJob>();
                var jobsToReset = new List<IngestionJob>();

                foreach (var job in ingestionJobs)
                {
                    if (job.Status == IngestionJobStatus.Discarded)
                    {
                        discardedJobs.Add(job);
                    }
                    else
                    {
                        jobsToReset.Add(job);
                    }
                }

                dbContext.IngestionJobs.RemoveRange(discardedJobs);
                deletedIngestionJobsCount = discardedJobs.Count;

                ResetRetainedJobsAndCollectAssets(
                    jobsToReset,
                    referencedCollectionIds,
                    preservedMediaPaths
                );
                resetIngestionJobsCount = jobsToReset.Count;
            }

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

            var steps = await dbContext.RecipeSteps.ToListAsync(cancellationToken);
            dbContext.RecipeSteps.RemoveRange(steps);

            var replacements = await dbContext.RecipeIngredientReplacements.ToListAsync(
                cancellationToken
            );
            dbContext.RecipeIngredientReplacements.RemoveRange(replacements);

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

            // Stage 5: Food items, aliases & catalog embeddings
            var aliases = await dbContext.FoodItemAliases.ToListAsync(cancellationToken);
            dbContext.FoodItemAliases.RemoveRange(aliases);

            var embeddings = await dbContext.CatalogEmbeddings.ToListAsync(cancellationToken);
            dbContext.CatalogEmbeddings.RemoveRange(embeddings);

            var foodItems = await dbContext.FoodItems.ToListAsync(cancellationToken);
            dbContext.FoodItems.RemoveRange(foodItems);

            await dbContext.SaveChangesAsync(cancellationToken);

            // Stage 6: Tags & Collections
            var tags = await dbContext.Tags.ToListAsync(cancellationToken);
            dbContext.Tags.RemoveRange(tags);

            var collections = await dbContext.Collections.ToListAsync(cancellationToken);
            var collectionsToDelete = keepIngestionJobs
                ? collections.Where(c => !referencedCollectionIds.Contains(c.Id)).ToList()
                : collections;
            dbContext.Collections.RemoveRange(collectionsToDelete);

            await dbContext.SaveChangesAsync(cancellationToken);

            // Reseed standard built-in default tags so standard taxonomy is restored
            await TagSeeder.EnsureBuiltInTagsSeededAsync(dbContext, cancellationToken);

            if (tx is not null)
            {
                await tx.CommitAsync(cancellationToken);
            }

            logger.LogWarning(
                "App data reset completed (KeepIngestionJobs={KeepIngestionJobs}): {RecipeCount} recipes, {FoodItemCount} food items, {DeletedJobCount} ingestion jobs wiped, {ResetJobCount} ingestion jobs re-queued. Built-in default tags restored.",
                keepIngestionJobs,
                recipes.Count,
                foodItems.Count,
                deletedIngestionJobsCount,
                resetIngestionJobsCount
            );

            return new ResetAppDataResultDto(
                DeletedRecipesCount: recipes.Count,
                DeletedMealPlansCount: mealPlanEntries.Count + templates.Count,
                DeletedShoppingListsCount: shoppingLists.Count,
                DeletedIngestionJobsCount: deletedIngestionJobsCount,
                DeletedFoodItemsCount: foodItems.Count,
                DeletedTagsCount: tags.Count,
                DeletedCollectionsCount: collectionsToDelete.Count,
                ResetAt: DateTimeOffset.UtcNow,
                RestoredDefaultTagsCount: BuiltInTags.All.Count,
                ResetIngestionJobsCount: resetIngestionJobsCount
            );
        });

        // Purge media uploads on disk only after database transaction successfully commits
        if (keepIngestionJobs)
        {
            await mediaStorageService.PurgeUnreferencedMediaAsync(
                preservedMediaPaths,
                cancellationToken
            );

            if (result.ResetIngestionJobsCount > 0)
            {
                queueSignal?.NotifyWorkAvailable();
            }
        }
        else
        {
            await mediaStorageService.PurgeAllMediaAsync(cancellationToken);
        }

        return result;
    }

    private async Task CancelActiveIngestionIfRunningAsync(CancellationToken cancellationToken)
    {
        if (queueSignal is null)
        {
            return;
        }

        queueSignal.CancelActiveExecution();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (queueSignal.ActiveJobIds.Count > 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50, cancellationToken);
        }
    }

    private static void ResetRetainedJobsAndCollectAssets(
        IEnumerable<IngestionJob> jobsToReset,
        HashSet<Guid> referencedCollectionIds,
        HashSet<string> preservedMediaPaths
    )
    {
        foreach (var job in jobsToReset)
        {
            CollectCollectionIds(job.InitialCollectionIdsJson, referencedCollectionIds);

            // WebUrl covers/scraped payloads are fetched during Step 1 preprocessing, so clear them on reset;
            // RawText, Images, and Pdf uploaded assets are created when queueing and must be preserved.
            if (job.Channel == IngestionChannel.WebUrl)
            {
                job.DownloadedCoverImagePath = null;
                job.DownloadedThumbnailImagePath = null;
            }
            else
            {
                AddMediaPath(job.DownloadedCoverImagePath, preservedMediaPaths);
                AddMediaPath(job.DownloadedThumbnailImagePath, preservedMediaPaths);
                CollectSourceFilePaths(job.SourceFilesJson, preservedMediaPaths);
            }

            job.Status = IngestionJobStatus.Queued;
            job.StatusDetail = GetInitialQueuedStatusDetail(job.Channel);
            job.ResultingRecipeId = null;
            job.ExtractedDraftJson = null;
            job.ExtractedModel = null;
            job.ConfidenceScore = null;
            job.AttemptCount = 0;
            job.NextRetryAt = null;
            job.RunNowRequested = false;
            job.ProcessedAt = null;
            job.NormalizedSourceText = null;
            job.ScrapedPageJson = null;
            job.PdfInspectionJson = null;
        }
    }

    private static string GetInitialQueuedStatusDetail(IngestionChannel channel) =>
        channel switch
        {
            IngestionChannel.Images => "Queued for vision extraction.",
            IngestionChannel.Pdf => "Queued for PDF inspection and processing.",
            _ => "Queued for extraction.",
        };

    private static void CollectCollectionIds(string? json, HashSet<Guid> destination)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            var ids = JsonSerializer.Deserialize<List<Guid>>(json, JsonOptions);
            if (ids is null)
            {
                return;
            }

            foreach (var id in ids)
            {
                if (id != Guid.Empty)
                {
                    destination.Add(id);
                }
            }
        }
        catch
        {
            // Ignore malformed InitialCollectionIdsJson
        }
    }

    private static void CollectSourceFilePaths(string? json, HashSet<string> destination)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            var files = JsonSerializer.Deserialize<List<IngestionSourceFileDto>>(json, JsonOptions);
            if (files is null)
            {
                return;
            }

            foreach (var file in files)
            {
                AddMediaPath(file.UrlPath, destination);
            }
        }
        catch
        {
            // Ignore malformed SourceFilesJson
        }
    }

    private static void AddMediaPath(string? path, HashSet<string> destination)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            destination.Add(path.Trim());
        }
    }
}

using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using App.Core.Exceptions;
using App.Core.Ingestion;
using App.Core.Maintenance;
using App.Core.Media;
using App.Core.Recipes;
using App.Data.Auditing;
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
    private const int CurrentBackupFormatVersion = 1;
    private const string BackupApplicationName = "LocalRoast";
    private const string ManifestEntryPath = "manifest.json";
    private const string SnapshotEntryPath = "data/snapshot.json";
    private const string UploadsEntryPrefix = "uploads/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions BackupJsonOptions = new(
        JsonSerializerDefaults.Web
    )
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
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
            using var _ = BeginExtendedCommandTimeoutScope();

            await using var tx = dbContext.Database.IsRelational()
                ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
                : null;

            logger.LogWarning(
                "Executing app data reset (KeepIngestionJobs={KeepIngestionJobs}): wiping culinary data while preserving user accounts.",
                keepIngestionJobs
            );

            var wipeCounts = await WipeCulinaryDataAsync(
                keepIngestionJobs,
                preservedMediaPaths,
                cancellationToken
            );

            // Reseed standard built-in default tags so standard taxonomy is restored
            await TagSeeder.EnsureBuiltInTagsSeededAsync(dbContext, cancellationToken);

            if (tx is not null)
            {
                await tx.CommitAsync(cancellationToken);
            }

            logger.LogWarning(
                "App data reset completed (KeepIngestionJobs={KeepIngestionJobs}): {RecipeCount} recipes, {FoodItemCount} food items, {DeletedJobCount} ingestion jobs wiped, {ResetJobCount} ingestion jobs re-queued. Built-in default tags restored.",
                keepIngestionJobs,
                wipeCounts.DeletedRecipesCount,
                wipeCounts.DeletedFoodItemsCount,
                wipeCounts.DeletedIngestionJobsCount,
                wipeCounts.ResetIngestionJobsCount
            );

            return new ResetAppDataResultDto(
                DeletedRecipesCount: wipeCounts.DeletedRecipesCount,
                DeletedMealPlansCount: wipeCounts.DeletedMealPlansCount,
                DeletedShoppingListsCount: wipeCounts.DeletedShoppingListsCount,
                DeletedIngestionJobsCount: wipeCounts.DeletedIngestionJobsCount,
                DeletedFoodItemsCount: wipeCounts.DeletedFoodItemsCount,
                DeletedTagsCount: wipeCounts.DeletedTagsCount,
                DeletedCollectionsCount: wipeCounts.DeletedCollectionsCount,
                ResetAt: DateTimeOffset.UtcNow,
                RestoredDefaultTagsCount: BuiltInTags.All.Count,
                ResetIngestionJobsCount: wipeCounts.ResetIngestionJobsCount
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

    public async Task ExportBackupToStreamAsync(
        Stream destination,
        ExportAppDataBackupRequest? request = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(destination);

        var includeMedia = request?.IncludeMedia ?? true;
        var exportedAt = DateTimeOffset.UtcNow;

        var snapshot = await BuildBackupSnapshotAsync(cancellationToken);
        var mediaFiles = includeMedia
            ? await mediaStorageService.ListAllMediaFilesAsync(cancellationToken)
            : [];

        var counts = new AppDataBackupCounts(
            Tags: snapshot.Tags.Count,
            Collections: snapshot.Collections.Count,
            FoodItems: snapshot.FoodItems.Count,
            FoodItemAliases: snapshot.FoodItemAliases.Count,
            CatalogEmbeddings: snapshot.CatalogEmbeddings.Count,
            Recipes: snapshot.Recipes.Count,
            RecipeIngredients: snapshot.RecipeIngredients.Count,
            RecipeIngredientReplacements: snapshot.RecipeIngredientReplacements.Count,
            RecipeSteps: snapshot.RecipeSteps.Count,
            RecipeStepTimers: snapshot.RecipeStepTimers.Count,
            RecipeTags: snapshot.RecipeTags.Count,
            RecipeCollections: snapshot.RecipeCollections.Count,
            RecipeAttachments: snapshot.RecipeAttachments.Count,
            RecipeEmbeddings: snapshot.RecipeEmbeddings.Count,
            ShareLinks: snapshot.ShareLinks.Count,
            IngestionJobs: snapshot.IngestionJobs.Count,
            MealPlanTemplates: snapshot.MealPlanTemplates.Count,
            MealPlanTemplateItems: snapshot.MealPlanTemplateItems.Count,
            MealPlanEntries: snapshot.MealPlanEntries.Count,
            ShoppingLists: snapshot.ShoppingLists.Count,
            ShoppingListItems: snapshot.ShoppingListItems.Count,
            ShoppingListItemSources: snapshot.ShoppingListItemSources.Count,
            CookLogs: snapshot.CookLogs.Count
        );

        var manifest = new AppDataBackupManifest(
            FormatVersion: CurrentBackupFormatVersion,
            ExportedAt: exportedAt,
            Application: BackupApplicationName,
            IncludesMedia: includeMedia,
            Counts: counts,
            MediaFilesCount: mediaFiles.Count
        );

        using (var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteJsonZipEntryAsync(archive, ManifestEntryPath, manifest, cancellationToken);
            await WriteJsonZipEntryAsync(archive, SnapshotEntryPath, snapshot, cancellationToken);

            foreach (var mediaFile in mediaFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entry = archive.CreateEntry(
                    $"{UploadsEntryPrefix}{mediaFile.RelativePath}",
                    CompressionLevel.NoCompression
                );
                await using var sourceStream = mediaFile.OpenReadStream();
                await using var entryStream = entry.Open();
                await sourceStream.CopyToAsync(entryStream, cancellationToken);
            }
        }

        logger.LogInformation(
            "Exported app data backup archive: {RecipeCount} recipes, {FoodItemCount} food items, {IngestionJobCount} ingestion jobs, {MediaFilesCount} media files.",
            snapshot.Recipes.Count,
            snapshot.FoodItems.Count,
            snapshot.IngestionJobs.Count,
            mediaFiles.Count
        );
    }

    public async Task<ImportAppDataBackupResultDto> ImportBackupAsync(
        Stream archiveStream,
        Guid? importingUserId = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(archiveStream);

        if (!archiveStream.CanRead)
        {
            throw new DomainValidationException("Backup archive stream is not readable.");
        }

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            throw new DomainValidationException(
                "Invalid backup archive. Please upload a valid LocalRoast .zip backup file."
            );
        }

        using (archive)
        {
            var (manifest, snapshot, mediaEntries) = await ReadAndValidateArchiveAsync(
                archive,
                cancellationToken
            );

            await CancelActiveIngestionIfRunningAsync(cancellationToken);

            var existingUserIds = await dbContext
                .Users.Select(u => u.Id)
                .ToHashSetAsync(cancellationToken);

            Guid? RemapUserId(Guid? originalUserId) =>
                originalUserId is { } originalId && existingUserIds.Contains(originalId)
                    ? originalId
                : originalUserId.HasValue
                && importingUserId is { } impId
                && existingUserIds.Contains(impId)
                    ? impId
                : null;

            var strategy = dbContext.Database.CreateExecutionStrategy();

            await strategy.ExecuteAsync(async () =>
            {
                using var _ = BeginExtendedCommandTimeoutScope();

                await using var tx = dbContext.Database.IsRelational()
                    ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
                    : null;

                logger.LogWarning(
                    "Executing app data backup import (FormatVersion={FormatVersion}, Recipes={RecipeCount}, MediaFiles={MediaCount}).",
                    manifest.FormatVersion,
                    snapshot.Recipes.Count,
                    mediaEntries.Count
                );

                // Pause ingestion worker if configured so imported Queued jobs do not auto-run unexpectedly
                var llmSettings = await dbContext.HouseholdLlmSettings.FirstOrDefaultAsync(
                    cancellationToken
                );
                if (llmSettings is not null && llmSettings.EnableIngestion)
                {
                    llmSettings.EnableIngestion = false;
                    await dbContext.SaveChangesAsync(cancellationToken);
                }

                await WipeCulinaryDataAsync(
                    keepIngestionJobs: false,
                    preservedMediaPaths: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    cancellationToken
                );

                dbContext.ChangeTracker.Clear();

                using (AuditSaveChangesInterceptor.BeginRestoreScope())
                {
                    await RestoreSnapshotAsync(snapshot, RemapUserId, cancellationToken);
                }

                await TagSeeder.EnsureBuiltInTagsSeededAsync(dbContext, cancellationToken);

                if (tx is not null)
                {
                    await tx.CommitAsync(cancellationToken);
                }
            });

            // Replace media uploads on disk only after the DB transaction commits
            await mediaStorageService.PurgeAllMediaAsync(cancellationToken);

            var restoredMediaCount = 0;
            foreach (var (entry, relativePath) in mediaEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var entryStream = entry.Open();
                await mediaStorageService.RestoreMediaFileAsync(
                    relativePath,
                    entryStream,
                    cancellationToken
                );
                restoredMediaCount++;
            }

            logger.LogWarning(
                "App data backup import completed: {RecipeCount} recipes, {FoodItemCount} food items, {IngestionJobCount} ingestion jobs, {MediaCount} media files restored.",
                snapshot.Recipes.Count,
                snapshot.FoodItems.Count,
                snapshot.IngestionJobs.Count,
                restoredMediaCount
            );

            return new ImportAppDataBackupResultDto(
                ImportedRecipesCount: snapshot.Recipes.Count,
                ImportedFoodItemsCount: snapshot.FoodItems.Count,
                ImportedTagsCount: snapshot.Tags.Count,
                ImportedCollectionsCount: snapshot.Collections.Count,
                ImportedMealPlansCount: snapshot.MealPlanEntries.Count
                    + snapshot.MealPlanTemplates.Count,
                ImportedShoppingListsCount: snapshot.ShoppingLists.Count,
                ImportedIngestionJobsCount: snapshot.IngestionJobs.Count,
                ImportedCookLogsCount: snapshot.CookLogs.Count,
                ImportedMediaFilesCount: restoredMediaCount,
                ImportedAt: DateTimeOffset.UtcNow
            );
        }
    }

    private async Task<WipeCounts> WipeCulinaryDataAsync(
        bool keepIngestionJobs,
        HashSet<string> preservedMediaPaths,
        CancellationToken cancellationToken
    )
    {
        // Stage 1: Leaf dependencies & ingestion jobs
        await DeleteAllAsync(dbContext.CookLogs, cancellationToken);
        await DeleteAllAsync(dbContext.ShoppingListItemSources, cancellationToken);
        await DeleteAllAsync(dbContext.ShoppingListItems, cancellationToken);
        var deletedShoppingListsCount = await DeleteAllAsync(
            dbContext.ShoppingLists,
            cancellationToken
        );

        var deletedIngestionJobsCount = 0;
        var resetIngestionJobsCount = 0;
        var referencedCollectionIds = new HashSet<Guid>();

        if (!keepIngestionJobs)
        {
            deletedIngestionJobsCount = await DeleteAllAsync(
                dbContext.IngestionJobs,
                cancellationToken
            );
        }
        else
        {
            var ingestionJobs = await dbContext.IngestionJobs.ToListAsync(cancellationToken);
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
        await DeleteAllAsync(dbContext.MealPlanTemplateItems, cancellationToken);
        var deletedTemplatesCount = await DeleteAllAsync(
            dbContext.MealPlanTemplates,
            cancellationToken
        );

        if (dbContext.Database.IsRelational())
        {
            await dbContext
                .MealPlanEntries.Where(e => e.SourceMealPlanEntryId != null)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(e => e.SourceMealPlanEntryId, (Guid?)null),
                    cancellationToken
                );
        }

        var deletedMealPlanEntriesCount = await DeleteAllAsync(
            dbContext.MealPlanEntries,
            cancellationToken
        );

        await dbContext.SaveChangesAsync(cancellationToken);

        // Stage 3: Recipe children
        await DeleteAllAsync(dbContext.RecipeStepTimers, cancellationToken);
        await DeleteAllAsync(dbContext.RecipeSteps, cancellationToken);
        await DeleteAllAsync(dbContext.RecipeIngredientReplacements, cancellationToken);
        await DeleteAllAsync(dbContext.RecipeIngredients, cancellationToken);
        await DeleteAllAsync(dbContext.RecipeTags, cancellationToken);
        await DeleteAllAsync(dbContext.RecipeCollections, cancellationToken);
        await DeleteAllAsync(dbContext.RecipeAttachments, cancellationToken);
        await DeleteAllAsync(dbContext.ShareLinks, cancellationToken);
        await DeleteAllAsync(dbContext.RecipeEmbeddings, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        // Stage 4: Recipes
        var deletedRecipesCount = await DeleteAllAsync(dbContext.Recipes, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        // Stage 5: Food items, aliases & catalog embeddings
        await DeleteAllAsync(dbContext.FoodItemAliases, cancellationToken);
        await DeleteAllAsync(dbContext.CatalogEmbeddings, cancellationToken);
        var deletedFoodItemsCount = await DeleteAllAsync(dbContext.FoodItems, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        // Stage 6: Tags & Collections
        var deletedTagsCount = await DeleteAllAsync(dbContext.Tags, cancellationToken);
        var deletedCollectionsCount = 0;

        if (!keepIngestionJobs)
        {
            deletedCollectionsCount = await DeleteAllAsync(
                dbContext.Collections,
                cancellationToken
            );
        }
        else
        {
            var collections = await dbContext.Collections.ToListAsync(cancellationToken);
            var collectionsToDelete = collections
                .Where(c => !referencedCollectionIds.Contains(c.Id))
                .ToList();
            dbContext.Collections.RemoveRange(collectionsToDelete);
            deletedCollectionsCount = collectionsToDelete.Count;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return new WipeCounts(
            DeletedRecipesCount: deletedRecipesCount,
            DeletedMealPlansCount: deletedMealPlanEntriesCount + deletedTemplatesCount,
            DeletedShoppingListsCount: deletedShoppingListsCount,
            DeletedIngestionJobsCount: deletedIngestionJobsCount,
            DeletedFoodItemsCount: deletedFoodItemsCount,
            DeletedTagsCount: deletedTagsCount,
            DeletedCollectionsCount: deletedCollectionsCount,
            ResetIngestionJobsCount: resetIngestionJobsCount
        );
    }

    private async Task<int> DeleteAllAsync<TEntity>(
        DbSet<TEntity> dbSet,
        CancellationToken cancellationToken
    )
        where TEntity : class
    {
        if (dbContext.Database.IsRelational())
        {
            return await dbSet.ExecuteDeleteAsync(cancellationToken);
        }

        var items = await dbSet.ToListAsync(cancellationToken);
        dbSet.RemoveRange(items);
        return items.Count;
    }

    private async Task InsertBatchedAsync<TSnapshot, TEntity>(
        IReadOnlyList<TSnapshot> snapshots,
        Func<TSnapshot, TEntity> projector,
        int batchSize,
        CancellationToken cancellationToken
    )
        where TEntity : class
    {
        if (snapshots.Count == 0)
        {
            return;
        }

        var dbSet = dbContext.Set<TEntity>();
        for (var offset = 0; offset < snapshots.Count; offset += batchSize)
        {
            var take = Math.Min(batchSize, snapshots.Count - offset);
            var batch = new List<TEntity>(take);
            for (var i = 0; i < take; i++)
            {
                batch.Add(projector(snapshots[offset + i]));
            }

            await dbSet.AddRangeAsync(batch, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
        }
    }

    private IDisposable? BeginExtendedCommandTimeoutScope()
    {
        if (!dbContext.Database.IsRelational())
        {
            return null;
        }

        var previousTimeout = dbContext.Database.GetCommandTimeout();
        dbContext.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));
        return new CommandTimeoutScope(dbContext, previousTimeout);
    }

    private sealed class CommandTimeoutScope(AppDbContext dbContext, int? previousTimeout)
        : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                dbContext.Database.SetCommandTimeout(previousTimeout);
            }
        }
    }

    private async Task<AppDataBackupSnapshot> BuildBackupSnapshotAsync(
        CancellationToken cancellationToken
    )
    {
        var tags = await dbContext
            .Tags.AsNoTracking()
            .OrderBy(t => t.Id)
            .Select(t => new TagSnapshot(
                t.Id,
                t.Name,
                t.NormalizedName,
                t.Slug,
                t.Category,
                t.SortOrder,
                t.IsSystemSeeded,
                t.CreatedAt,
                t.CreatedBy,
                t.ModifiedAt,
                t.ModifiedBy
            ))
            .ToListAsync(cancellationToken);

        var collections = await dbContext
            .Collections.AsNoTracking()
            .OrderBy(c => c.Id)
            .Select(c => new CollectionSnapshot(
                c.Id,
                c.Name,
                c.NormalizedName,
                c.Slug,
                c.Description,
                c.SortOrder,
                c.IsSystemSeeded,
                c.CreatedAt,
                c.CreatedBy,
                c.ModifiedAt,
                c.ModifiedBy
            ))
            .ToListAsync(cancellationToken);

        var foodItems = await dbContext
            .FoodItems.AsNoTracking()
            .OrderBy(f => f.Id)
            .Select(f => new FoodItemSnapshot(
                f.Id,
                f.Name,
                f.NormalizedName,
                f.IsPantryStaple,
                f.Category,
                f.CreatedAt,
                f.CreatedBy,
                f.ModifiedAt,
                f.ModifiedBy
            ))
            .ToListAsync(cancellationToken);

        var foodItemAliases = await dbContext
            .FoodItemAliases.AsNoTracking()
            .OrderBy(a => a.Id)
            .Select(a => new FoodItemAliasSnapshot(
                a.Id,
                a.FoodItemId,
                a.Alias,
                a.NormalizedAlias,
                a.CreatedAt,
                a.CreatedBy,
                a.ModifiedAt,
                a.ModifiedBy
            ))
            .ToListAsync(cancellationToken);

        var catalogEmbeddings = await dbContext
            .CatalogEmbeddings.AsNoTracking()
            .OrderBy(e => e.Id)
            .Select(e => new CatalogEmbeddingSnapshot(
                e.Id,
                e.ModelTag,
                e.NormalizedText,
                e.Dimensions,
                e.VectorBytes,
                e.CreatedAt,
                e.CreatedBy,
                e.ModifiedAt,
                e.ModifiedBy
            ))
            .ToListAsync(cancellationToken);

        var recipes = await dbContext
            .Recipes.AsNoTracking()
            .OrderBy(r => r.Id)
            .Select(r => new RecipeSnapshot(
                r.Id,
                r.Title,
                r.Slug,
                r.Description,
                r.Status,
                r.BaseServings,
                r.YieldDisplay,
                r.PrepTimeMinutes,
                r.CookTimeMinutes,
                r.TotalTimeMinutes,
                r.Difficulty,
                r.SourceUrl,
                r.SourceName,
                r.SourceAuthor,
                r.SourceBookTitle,
                r.SourceBookPage,
                r.CoverImagePath,
                r.ThumbnailImagePath,
                r.ExtractedModel,
                r.RefinedModel,
                r.RefinementStatus,
                r.RefinementRequestedAt,
                r.RefinementStatusDetail,
                r.TimesCooked,
                r.LastCookedAt,
                r.AverageRating,
                r.RatingCount,
                r.CreatedAt,
                r.CreatedBy,
                r.ModifiedAt,
                r.ModifiedBy
            ))
            .ToListAsync(cancellationToken);

        var recipeIngredients = await dbContext
            .RecipeIngredients.AsNoTracking()
            .OrderBy(i => i.Id)
            .Select(i => new RecipeIngredientSnapshot(
                i.Id,
                i.RecipeId,
                i.SortOrder,
                i.SectionName,
                i.Quantity,
                i.MaxQuantity,
                i.Unit,
                i.CustomUnitName,
                i.AlternativeQuantity,
                i.AlternativeMaxQuantity,
                i.AlternativeUnit,
                i.AlternativeCustomUnitName,
                i.PreferAlternativeMeasurement,
                i.FoodItemId,
                i.LinkedRecipeId,
                i.UnresolvedRecipeTitle,
                i.DisplayName,
                i.PreparationNote,
                i.IsOptional,
                i.OriginalRawText,
                i.CreatedAt,
                i.CreatedBy,
                i.ModifiedAt,
                i.ModifiedBy
            ))
            .ToListAsync(cancellationToken);

        var replacements = await dbContext
            .RecipeIngredientReplacements.AsNoTracking()
            .OrderBy(r => r.Id)
            .Select(r => new RecipeIngredientReplacementSnapshot(
                r.Id,
                r.RecipeIngredientId,
                r.SortOrder,
                r.DisplayName,
                r.Quantity,
                r.MaxQuantity,
                r.Unit,
                r.CustomUnitName,
                r.FoodItemId,
                r.PreparationNote,
                r.CreatedAt,
                r.CreatedBy,
                r.ModifiedAt,
                r.ModifiedBy
            ))
            .ToListAsync(cancellationToken);

        var recipeSteps = await dbContext
            .RecipeSteps.AsNoTracking()
            .OrderBy(s => s.Id)
            .Select(s => new RecipeStepSnapshot(
                s.Id,
                s.RecipeId,
                s.StepNumber,
                s.SectionName,
                s.Instruction,
                s.ImagePath,
                s.CreatedAt,
                s.CreatedBy,
                s.ModifiedAt,
                s.ModifiedBy
            ))
            .ToListAsync(cancellationToken);

        var stepTimers = await dbContext
            .RecipeStepTimers.AsNoTracking()
            .OrderBy(t => t.Id)
            .Select(t => new RecipeStepTimerSnapshot(
                t.Id,
                t.RecipeStepId,
                t.SortOrder,
                t.DurationSeconds,
                t.Label,
                t.CreatedAt,
                t.CreatedBy,
                t.ModifiedAt,
                t.ModifiedBy
            ))
            .ToListAsync(cancellationToken);

        var recipeTags = await dbContext
            .RecipeTags.AsNoTracking()
            .OrderBy(rt => rt.Id)
            .Select(rt => new RecipeTagSnapshot(
                rt.Id,
                rt.RecipeId,
                rt.TagId,
                rt.CreatedAt,
                rt.CreatedBy,
                rt.ModifiedAt,
                rt.ModifiedBy
            ))
            .ToListAsync(cancellationToken);

        var recipeCollections = await dbContext
            .RecipeCollections.AsNoTracking()
            .OrderBy(rc => rc.Id)
            .Select(rc => new RecipeCollectionSnapshot(
                rc.Id,
                rc.RecipeId,
                rc.CollectionId,
                rc.CreatedAt,
                rc.CreatedBy,
                rc.ModifiedAt,
                rc.ModifiedBy
            ))
            .ToListAsync(cancellationToken);

        var recipeAttachments = await dbContext
            .RecipeAttachments.AsNoTracking()
            .OrderBy(a => a.Id)
            .Select(a => new RecipeAttachmentSnapshot(
                a.Id,
                a.RecipeId,
                a.SortOrder,
                a.FileName,
                a.MediaUrlPath,
                a.ContentType,
                a.FileSizeBytes,
                a.Label,
                a.CreatedAt,
                a.CreatedBy,
                a.ModifiedAt,
                a.ModifiedBy
            ))
            .ToListAsync(cancellationToken);

        var recipeEmbeddings = await dbContext
            .RecipeEmbeddings.AsNoTracking()
            .OrderBy(e => e.Id)
            .Select(e => new RecipeEmbeddingSnapshot(
                e.Id,
                e.RecipeId,
                e.ModelTag,
                e.ContentHash,
                e.EmbeddingText,
                e.Dimensions,
                e.VectorBytes,
                e.CreatedAt,
                e.CreatedBy,
                e.ModifiedAt,
                e.ModifiedBy
            ))
            .ToListAsync(cancellationToken);

        var shareLinks = await dbContext
            .ShareLinks.AsNoTracking()
            .OrderBy(s => s.Id)
            .Select(s => new ShareLinkSnapshot(
                s.Id,
                s.RecipeId,
                s.Token,
                s.Label,
                s.IncludeCookLogs,
                s.ExpiresAt,
                s.RevokedAt,
                s.ViewCount,
                s.LastAccessedAt,
                s.CreatedAt,
                s.CreatedBy,
                s.ModifiedAt,
                s.ModifiedBy
            ))
            .ToListAsync(cancellationToken);

        var ingestionJobs = await dbContext
            .IngestionJobs.AsNoTracking()
            .OrderBy(j => j.Id)
            .Select(j => new IngestionJobSnapshot(
                j.Id,
                j.Channel,
                j.Status,
                j.DisplayTitle,
                j.SourceUrl,
                j.RawText,
                j.ScrapedPageJson,
                j.NormalizedSourceText,
                j.DownloadedCoverImagePath,
                j.DownloadedThumbnailImagePath,
                j.SourceFilesJson,
                j.PdfInspectionJson,
                j.SourceName,
                j.SourceAuthor,
                j.SourceBookTitle,
                j.SourceBookPage,
                j.InitialCollectionIdsJson,
                j.AutoPublish,
                j.ExtractedDraftJson,
                j.ExtractedModel,
                j.RefinedModel,
                j.RefinedAt,
                j.RefinementAttemptCount,
                j.RefinementNextRetryAt,
                j.ConfidenceScore,
                j.StatusDetail,
                j.AttemptCount,
                j.NextRetryAt,
                j.RunNowRequested,
                j.RefinementRunNowRequested,
                j.ProcessedAt,
                j.ResultingRecipeId,
                j.CreatedAt,
                j.CreatedBy,
                j.ModifiedAt,
                j.ModifiedBy
            ))
            .ToListAsync(cancellationToken);

        var mealPlanTemplates = await dbContext
            .MealPlanTemplates.AsNoTracking()
            .OrderBy(t => t.Id)
            .Select(t => new MealPlanTemplateSnapshot(
                t.Id,
                t.Name,
                t.NormalizedName,
                t.Description,
                t.CreatedAt,
                t.CreatedBy,
                t.ModifiedAt,
                t.ModifiedBy
            ))
            .ToListAsync(cancellationToken);

        var mealPlanTemplateItems = await dbContext
            .MealPlanTemplateItems.AsNoTracking()
            .OrderBy(i => i.Id)
            .Select(i => new MealPlanTemplateItemSnapshot(
                i.Id,
                i.TemplateId,
                i.IsOnDeck,
                i.DayOffset,
                i.Slot,
                i.SortOrder,
                i.EntryType,
                i.RecipeId,
                i.TargetServings,
                i.IsLeftovers,
                i.CustomTitle,
                i.Notes,
                i.CreatedAt,
                i.CreatedBy,
                i.ModifiedAt,
                i.ModifiedBy
            ))
            .ToListAsync(cancellationToken);

        var mealPlanEntries = await dbContext
            .MealPlanEntries.AsNoTracking()
            .OrderBy(e => e.Id)
            .Select(e => new MealPlanEntrySnapshot(
                e.Id,
                e.WeekStartDate,
                e.IsOnDeck,
                e.ScheduledDate,
                e.Slot,
                e.SortOrder,
                e.EntryType,
                e.RecipeId,
                e.TargetServings,
                e.IsLeftovers,
                e.SourceMealPlanEntryId,
                e.CustomTitle,
                e.Notes,
                e.CreatedAt,
                e.CreatedBy,
                e.ModifiedAt,
                e.ModifiedBy
            ))
            .ToListAsync(cancellationToken);

        var shoppingLists = await dbContext
            .ShoppingLists.AsNoTracking()
            .OrderBy(l => l.Id)
            .Select(l => new ShoppingListSnapshot(
                l.Id,
                l.Name,
                l.NormalizedName,
                l.IsPinned,
                l.CreatedAt,
                l.CreatedBy,
                l.ModifiedAt,
                l.ModifiedBy
            ))
            .ToListAsync(cancellationToken);

        var shoppingListItems = await dbContext
            .ShoppingListItems.AsNoTracking()
            .OrderBy(i => i.Id)
            .Select(i => new ShoppingListItemSnapshot(
                i.Id,
                i.ShoppingListId,
                i.FoodItemId,
                i.DisplayName,
                i.Quantity,
                i.MaxQuantity,
                i.Unit,
                i.CustomUnitName,
                i.Dimension,
                i.Notes,
                i.IsPantryStapleExcluded,
                i.IsChecked,
                i.CheckedAt,
                i.CheckedByUserId,
                i.CheckedByDisplayName,
                i.SortOrder,
                i.CreatedAt,
                i.CreatedBy,
                i.ModifiedAt,
                i.ModifiedBy
            ))
            .ToListAsync(cancellationToken);

        var shoppingListItemSources = await dbContext
            .ShoppingListItemSources.AsNoTracking()
            .OrderBy(s => s.Id)
            .Select(s => new ShoppingListItemSourceSnapshot(
                s.Id,
                s.ShoppingListItemId,
                s.SourceKind,
                s.MealPlanEntryId,
                s.RecipeId,
                s.SourceLabel,
                s.ScheduledDate,
                s.ContributedQuantity,
                s.ContributedMaxQuantity,
                s.CreatedAt,
                s.CreatedBy,
                s.ModifiedAt,
                s.ModifiedBy
            ))
            .ToListAsync(cancellationToken);

        var cookLogs = await dbContext
            .CookLogs.AsNoTracking()
            .OrderBy(c => c.Id)
            .Select(c => new CookLogSnapshot(
                c.Id,
                c.RecipeId,
                c.CookedByUserId,
                c.CookedByDisplayName,
                c.CookedAt,
                c.ServingsMade,
                c.Rating,
                c.Notes,
                c.PhotoImagePath,
                c.PhotoThumbnailPath,
                c.CreatedAt,
                c.CreatedBy,
                c.ModifiedAt,
                c.ModifiedBy
            ))
            .ToListAsync(cancellationToken);

        return new AppDataBackupSnapshot
        {
            Tags = tags,
            Collections = collections,
            FoodItems = foodItems,
            FoodItemAliases = foodItemAliases,
            CatalogEmbeddings = catalogEmbeddings,
            Recipes = recipes,
            RecipeIngredients = recipeIngredients,
            RecipeIngredientReplacements = replacements,
            RecipeSteps = recipeSteps,
            RecipeStepTimers = stepTimers,
            RecipeTags = recipeTags,
            RecipeCollections = recipeCollections,
            RecipeAttachments = recipeAttachments,
            RecipeEmbeddings = recipeEmbeddings,
            ShareLinks = shareLinks,
            IngestionJobs = ingestionJobs,
            MealPlanTemplates = mealPlanTemplates,
            MealPlanTemplateItems = mealPlanTemplateItems,
            MealPlanEntries = mealPlanEntries,
            ShoppingLists = shoppingLists,
            ShoppingListItems = shoppingListItems,
            ShoppingListItemSources = shoppingListItemSources,
            CookLogs = cookLogs,
        };
    }

    private static async Task WriteJsonZipEntryAsync<T>(
        ZipArchive archive,
        string entryPath,
        T payload,
        CancellationToken cancellationToken
    )
    {
        var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
        await using var entryStream = entry.Open();
        await JsonSerializer.SerializeAsync(
            entryStream,
            payload,
            BackupJsonOptions,
            cancellationToken
        );
    }

    private static async Task<(
        AppDataBackupManifest Manifest,
        AppDataBackupSnapshot Snapshot,
        List<(ZipArchiveEntry Entry, string RelativePath)> MediaEntries
    )> ReadAndValidateArchiveAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        ZipArchiveEntry? manifestEntry = null;
        ZipArchiveEntry? snapshotEntry = null;
        var mediaEntries = new List<(ZipArchiveEntry Entry, string RelativePath)>();

        foreach (var entry in archive.Entries)
        {
            var rawName = entry.FullName;
            if (rawName.Contains("..", StringComparison.Ordinal))
            {
                throw new DomainValidationException(
                    $"Backup archive contains an unsafe path '{rawName}'."
                );
            }

            var normalized = rawName.Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrEmpty(normalized) || normalized.EndsWith('/'))
            {
                continue;
            }

            if (string.Equals(normalized, ManifestEntryPath, StringComparison.OrdinalIgnoreCase))
            {
                manifestEntry = entry;
            }
            else if (
                string.Equals(normalized, SnapshotEntryPath, StringComparison.OrdinalIgnoreCase)
            )
            {
                snapshotEntry = entry;
            }
            else if (normalized.StartsWith(UploadsEntryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var rawRelative = normalized[UploadsEntryPrefix.Length..];
                if (
                    !MediaBackupPathValidator.TryNormalizeRelativePath(
                        rawRelative,
                        out var validRelative
                    )
                )
                {
                    throw new DomainValidationException(
                        $"Backup archive contains an invalid media path '{rawName}'."
                    );
                }

                mediaEntries.Add((entry, validRelative));
            }
        }

        if (manifestEntry is null)
        {
            throw new DomainValidationException(
                "Invalid backup archive: missing required manifest.json."
            );
        }

        if (snapshotEntry is null)
        {
            throw new DomainValidationException(
                "Invalid backup archive: missing required data/snapshot.json."
            );
        }

        AppDataBackupManifest? manifest;
        try
        {
            await using var manifestStream = manifestEntry.Open();
            manifest = await JsonSerializer.DeserializeAsync<AppDataBackupManifest>(
                manifestStream,
                BackupJsonOptions,
                cancellationToken
            );
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new DomainValidationException(
                "Invalid backup archive: manifest.json could not be parsed."
            );
        }

        if (manifest is null || manifest.FormatVersion != CurrentBackupFormatVersion)
        {
            throw new DomainValidationException(
                $"Unsupported backup archive format version '{manifest?.FormatVersion}'."
            );
        }

        AppDataBackupSnapshot? snapshot;
        try
        {
            await using var snapshotStream = snapshotEntry.Open();
            snapshot = await JsonSerializer.DeserializeAsync<AppDataBackupSnapshot>(
                snapshotStream,
                BackupJsonOptions,
                cancellationToken
            );
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new DomainValidationException(
                "Invalid backup archive: data/snapshot.json could not be parsed."
            );
        }

        if (snapshot is null)
        {
            throw new DomainValidationException(
                "Invalid backup archive: data/snapshot.json is empty."
            );
        }

        return (manifest, snapshot, mediaEntries);
    }

    private async Task RestoreSnapshotAsync(
        AppDataBackupSnapshot snapshot,
        Func<Guid?, Guid?> remapUserId,
        CancellationToken cancellationToken
    )
    {
        const int standardBatchSize = 500;
        const int largePayloadBatchSize = 100;

        var previousAutoDetect = dbContext.ChangeTracker.AutoDetectChangesEnabled;
        dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            // Stage A: Independent root tables
            await InsertBatchedAsync(
                snapshot.Tags,
                t => new Tag
                {
                    Id = t.Id,
                    Name = t.Name,
                    NormalizedName = t.NormalizedName,
                    Slug = t.Slug,
                    Category = t.Category,
                    SortOrder = t.SortOrder,
                    IsSystemSeeded = t.IsSystemSeeded,
                    CreatedAt = t.CreatedAt,
                    CreatedBy = t.CreatedBy,
                    ModifiedAt = t.ModifiedAt,
                    ModifiedBy = t.ModifiedBy,
                },
                standardBatchSize,
                cancellationToken
            );

            await InsertBatchedAsync(
                snapshot.Collections,
                c => new Collection
                {
                    Id = c.Id,
                    Name = c.Name,
                    NormalizedName = c.NormalizedName,
                    Slug = c.Slug,
                    Description = c.Description,
                    SortOrder = c.SortOrder,
                    IsSystemSeeded = c.IsSystemSeeded,
                    CreatedAt = c.CreatedAt,
                    CreatedBy = c.CreatedBy,
                    ModifiedAt = c.ModifiedAt,
                    ModifiedBy = c.ModifiedBy,
                },
                standardBatchSize,
                cancellationToken
            );

            await InsertBatchedAsync(
                snapshot.FoodItems,
                f => new FoodItem
                {
                    Id = f.Id,
                    Name = f.Name,
                    NormalizedName = f.NormalizedName,
                    IsPantryStaple = f.IsPantryStaple,
                    Category = f.Category,
                    CreatedAt = f.CreatedAt,
                    CreatedBy = f.CreatedBy,
                    ModifiedAt = f.ModifiedAt,
                    ModifiedBy = f.ModifiedBy,
                },
                standardBatchSize,
                cancellationToken
            );

            await InsertBatchedAsync(
                snapshot.CatalogEmbeddings,
                e => new CatalogEmbedding
                {
                    Id = e.Id,
                    ModelTag = e.ModelTag,
                    NormalizedText = e.NormalizedText,
                    Dimensions = e.Dimensions,
                    VectorBytes = e.VectorBytes,
                    CreatedAt = e.CreatedAt,
                    CreatedBy = e.CreatedBy,
                    ModifiedAt = e.ModifiedAt,
                    ModifiedBy = e.ModifiedBy,
                },
                largePayloadBatchSize,
                cancellationToken
            );

            // Stage B: Food item aliases & Recipes
            await InsertBatchedAsync(
                snapshot.FoodItemAliases,
                a => new FoodItemAlias
                {
                    Id = a.Id,
                    FoodItemId = a.FoodItemId,
                    Alias = a.Alias,
                    NormalizedAlias = a.NormalizedAlias,
                    CreatedAt = a.CreatedAt,
                    CreatedBy = a.CreatedBy,
                    ModifiedAt = a.ModifiedAt,
                    ModifiedBy = a.ModifiedBy,
                },
                standardBatchSize,
                cancellationToken
            );

            await InsertBatchedAsync(
                snapshot.Recipes,
                r => new Recipe
                {
                    Id = r.Id,
                    Title = r.Title,
                    Slug = r.Slug,
                    Description = r.Description,
                    Status = r.Status,
                    BaseServings = r.BaseServings,
                    YieldDisplay = r.YieldDisplay,
                    PrepTimeMinutes = r.PrepTimeMinutes,
                    CookTimeMinutes = r.CookTimeMinutes,
                    TotalTimeMinutes = r.TotalTimeMinutes,
                    Difficulty = r.Difficulty,
                    SourceUrl = r.SourceUrl,
                    SourceName = r.SourceName,
                    SourceAuthor = r.SourceAuthor,
                    SourceBookTitle = r.SourceBookTitle,
                    SourceBookPage = r.SourceBookPage,
                    CoverImagePath = r.CoverImagePath,
                    ThumbnailImagePath = r.ThumbnailImagePath,
                    ExtractedModel = r.ExtractedModel,
                    RefinedModel = r.RefinedModel,
                    RefinementStatus = r.RefinementStatus,
                    RefinementRequestedAt = r.RefinementRequestedAt,
                    RefinementStatusDetail = r.RefinementStatusDetail,
                    TimesCooked = r.TimesCooked,
                    LastCookedAt = r.LastCookedAt,
                    AverageRating = r.AverageRating,
                    RatingCount = r.RatingCount,
                    CreatedAt = r.CreatedAt,
                    CreatedBy = r.CreatedBy,
                    ModifiedAt = r.ModifiedAt,
                    ModifiedBy = r.ModifiedBy,
                },
                standardBatchSize,
                cancellationToken
            );

            // Stage C: Recipe children, sub-recipe links & Ingestion jobs
            await InsertBatchedAsync(
                snapshot.RecipeIngredients,
                i => new RecipeIngredient
                {
                    Id = i.Id,
                    RecipeId = i.RecipeId,
                    SortOrder = i.SortOrder,
                    SectionName = i.SectionName,
                    Quantity = i.Quantity,
                    MaxQuantity = i.MaxQuantity,
                    Unit = i.Unit,
                    CustomUnitName = i.CustomUnitName,
                    AlternativeQuantity = i.AlternativeQuantity,
                    AlternativeMaxQuantity = i.AlternativeMaxQuantity,
                    AlternativeUnit = i.AlternativeUnit,
                    AlternativeCustomUnitName = i.AlternativeCustomUnitName,
                    PreferAlternativeMeasurement = i.PreferAlternativeMeasurement,
                    FoodItemId = i.FoodItemId,
                    LinkedRecipeId = i.LinkedRecipeId,
                    UnresolvedRecipeTitle = i.UnresolvedRecipeTitle,
                    DisplayName = i.DisplayName,
                    PreparationNote = i.PreparationNote,
                    IsOptional = i.IsOptional,
                    OriginalRawText = i.OriginalRawText,
                    CreatedAt = i.CreatedAt,
                    CreatedBy = i.CreatedBy,
                    ModifiedAt = i.ModifiedAt,
                    ModifiedBy = i.ModifiedBy,
                },
                standardBatchSize,
                cancellationToken
            );

            await InsertBatchedAsync(
                snapshot.RecipeSteps,
                s => new RecipeStep
                {
                    Id = s.Id,
                    RecipeId = s.RecipeId,
                    StepNumber = s.StepNumber,
                    SectionName = s.SectionName,
                    Instruction = s.Instruction,
                    ImagePath = s.ImagePath,
                    CreatedAt = s.CreatedAt,
                    CreatedBy = s.CreatedBy,
                    ModifiedAt = s.ModifiedAt,
                    ModifiedBy = s.ModifiedBy,
                },
                standardBatchSize,
                cancellationToken
            );

            await InsertBatchedAsync(
                snapshot.RecipeTags,
                rt => new RecipeTag
                {
                    Id = rt.Id,
                    RecipeId = rt.RecipeId,
                    TagId = rt.TagId,
                    CreatedAt = rt.CreatedAt,
                    CreatedBy = rt.CreatedBy,
                    ModifiedAt = rt.ModifiedAt,
                    ModifiedBy = rt.ModifiedBy,
                },
                standardBatchSize,
                cancellationToken
            );

            await InsertBatchedAsync(
                snapshot.RecipeCollections,
                rc => new RecipeCollection
                {
                    Id = rc.Id,
                    RecipeId = rc.RecipeId,
                    CollectionId = rc.CollectionId,
                    CreatedAt = rc.CreatedAt,
                    CreatedBy = rc.CreatedBy,
                    ModifiedAt = rc.ModifiedAt,
                    ModifiedBy = rc.ModifiedBy,
                },
                standardBatchSize,
                cancellationToken
            );

            await InsertBatchedAsync(
                snapshot.RecipeAttachments,
                a => new RecipeAttachment
                {
                    Id = a.Id,
                    RecipeId = a.RecipeId,
                    SortOrder = a.SortOrder,
                    FileName = a.FileName,
                    MediaUrlPath = a.MediaUrlPath,
                    ContentType = a.ContentType,
                    FileSizeBytes = a.FileSizeBytes,
                    Label = a.Label,
                    CreatedAt = a.CreatedAt,
                    CreatedBy = a.CreatedBy,
                    ModifiedAt = a.ModifiedAt,
                    ModifiedBy = a.ModifiedBy,
                },
                standardBatchSize,
                cancellationToken
            );

            await InsertBatchedAsync(
                snapshot.RecipeEmbeddings,
                e => new RecipeEmbedding
                {
                    Id = e.Id,
                    RecipeId = e.RecipeId,
                    ModelTag = e.ModelTag,
                    ContentHash = e.ContentHash,
                    EmbeddingText = e.EmbeddingText,
                    Dimensions = e.Dimensions,
                    VectorBytes = e.VectorBytes,
                    CreatedAt = e.CreatedAt,
                    CreatedBy = e.CreatedBy,
                    ModifiedAt = e.ModifiedAt,
                    ModifiedBy = e.ModifiedBy,
                },
                largePayloadBatchSize,
                cancellationToken
            );

            await InsertBatchedAsync(
                snapshot.ShareLinks,
                s => new ShareLink
                {
                    Id = s.Id,
                    RecipeId = s.RecipeId,
                    Token = s.Token,
                    Label = s.Label,
                    IncludeCookLogs = s.IncludeCookLogs,
                    ExpiresAt = s.ExpiresAt,
                    RevokedAt = s.RevokedAt,
                    ViewCount = s.ViewCount,
                    LastAccessedAt = s.LastAccessedAt,
                    CreatedAt = s.CreatedAt,
                    CreatedBy = s.CreatedBy,
                    ModifiedAt = s.ModifiedAt,
                    ModifiedBy = s.ModifiedBy,
                },
                standardBatchSize,
                cancellationToken
            );

            await InsertBatchedAsync(
                snapshot.IngestionJobs,
                j => new IngestionJob
                {
                    Id = j.Id,
                    Channel = j.Channel,
                    Status = j.Status,
                    DisplayTitle = j.DisplayTitle,
                    SourceUrl = j.SourceUrl,
                    RawText = j.RawText,
                    ScrapedPageJson = j.ScrapedPageJson,
                    NormalizedSourceText = j.NormalizedSourceText,
                    DownloadedCoverImagePath = j.DownloadedCoverImagePath,
                    DownloadedThumbnailImagePath = j.DownloadedThumbnailImagePath,
                    SourceFilesJson = j.SourceFilesJson,
                    PdfInspectionJson = j.PdfInspectionJson,
                    SourceName = j.SourceName,
                    SourceAuthor = j.SourceAuthor,
                    SourceBookTitle = j.SourceBookTitle,
                    SourceBookPage = j.SourceBookPage,
                    InitialCollectionIdsJson = j.InitialCollectionIdsJson,
                    AutoPublish = j.AutoPublish,
                    ExtractedDraftJson = j.ExtractedDraftJson,
                    ExtractedModel = j.ExtractedModel,
                    RefinedModel = j.RefinedModel,
                    RefinedAt = j.RefinedAt,
                    RefinementAttemptCount = j.RefinementAttemptCount,
                    RefinementNextRetryAt = j.RefinementNextRetryAt,
                    ConfidenceScore = j.ConfidenceScore,
                    StatusDetail = j.StatusDetail,
                    AttemptCount = j.AttemptCount,
                    NextRetryAt = j.NextRetryAt,
                    RunNowRequested = j.RunNowRequested,
                    RefinementRunNowRequested = j.RefinementRunNowRequested,
                    ProcessedAt = j.ProcessedAt,
                    ResultingRecipeId = j.ResultingRecipeId,
                    CreatedAt = j.CreatedAt,
                    CreatedBy = j.CreatedBy,
                    ModifiedAt = j.ModifiedAt,
                    ModifiedBy = j.ModifiedBy,
                },
                largePayloadBatchSize,
                cancellationToken
            );

            // Stage D: Step timers & Ingredient replacements
            await InsertBatchedAsync(
                snapshot.RecipeIngredientReplacements,
                r => new RecipeIngredientReplacement
                {
                    Id = r.Id,
                    RecipeIngredientId = r.RecipeIngredientId,
                    SortOrder = r.SortOrder,
                    DisplayName = r.DisplayName,
                    Quantity = r.Quantity,
                    MaxQuantity = r.MaxQuantity,
                    Unit = r.Unit,
                    CustomUnitName = r.CustomUnitName,
                    FoodItemId = r.FoodItemId,
                    PreparationNote = r.PreparationNote,
                    CreatedAt = r.CreatedAt,
                    CreatedBy = r.CreatedBy,
                    ModifiedAt = r.ModifiedAt,
                    ModifiedBy = r.ModifiedBy,
                },
                standardBatchSize,
                cancellationToken
            );

            await InsertBatchedAsync(
                snapshot.RecipeStepTimers,
                t => new RecipeStepTimer
                {
                    Id = t.Id,
                    RecipeStepId = t.RecipeStepId,
                    SortOrder = t.SortOrder,
                    DurationSeconds = t.DurationSeconds,
                    Label = t.Label,
                    CreatedAt = t.CreatedAt,
                    CreatedBy = t.CreatedBy,
                    ModifiedAt = t.ModifiedAt,
                    ModifiedBy = t.ModifiedBy,
                },
                standardBatchSize,
                cancellationToken
            );

            // Stage E: MealPlanTemplates & ShoppingLists
            await InsertBatchedAsync(
                snapshot.MealPlanTemplates,
                t => new MealPlanTemplate
                {
                    Id = t.Id,
                    Name = t.Name,
                    NormalizedName = t.NormalizedName,
                    Description = t.Description,
                    CreatedAt = t.CreatedAt,
                    CreatedBy = t.CreatedBy,
                    ModifiedAt = t.ModifiedAt,
                    ModifiedBy = t.ModifiedBy,
                },
                standardBatchSize,
                cancellationToken
            );

            await InsertBatchedAsync(
                snapshot.ShoppingLists,
                l => new ShoppingList
                {
                    Id = l.Id,
                    Name = l.Name,
                    NormalizedName = l.NormalizedName,
                    IsPinned = l.IsPinned,
                    CreatedAt = l.CreatedAt,
                    CreatedBy = l.CreatedBy,
                    ModifiedAt = l.ModifiedAt,
                    ModifiedBy = l.ModifiedBy,
                },
                standardBatchSize,
                cancellationToken
            );

            // Stage F: Template items, shopping list items, and self-referential MealPlanEntries (topological waves)
            await InsertBatchedAsync(
                snapshot.MealPlanTemplateItems,
                i => new MealPlanTemplateItem
                {
                    Id = i.Id,
                    TemplateId = i.TemplateId,
                    IsOnDeck = i.IsOnDeck,
                    DayOffset = i.DayOffset,
                    Slot = i.Slot,
                    SortOrder = i.SortOrder,
                    EntryType = i.EntryType,
                    RecipeId = i.RecipeId,
                    TargetServings = i.TargetServings,
                    IsLeftovers = i.IsLeftovers,
                    CustomTitle = i.CustomTitle,
                    Notes = i.Notes,
                    CreatedAt = i.CreatedAt,
                    CreatedBy = i.CreatedBy,
                    ModifiedAt = i.ModifiedAt,
                    ModifiedBy = i.ModifiedBy,
                },
                standardBatchSize,
                cancellationToken
            );

            await InsertBatchedAsync(
                snapshot.ShoppingListItems,
                i => new ShoppingListItem
                {
                    Id = i.Id,
                    ShoppingListId = i.ShoppingListId,
                    FoodItemId = i.FoodItemId,
                    DisplayName = i.DisplayName,
                    Quantity = i.Quantity,
                    MaxQuantity = i.MaxQuantity,
                    Unit = i.Unit,
                    CustomUnitName = i.CustomUnitName,
                    Dimension = i.Dimension,
                    Notes = i.Notes,
                    IsPantryStapleExcluded = i.IsPantryStapleExcluded,
                    IsChecked = i.IsChecked,
                    CheckedAt = i.CheckedAt,
                    CheckedByUserId = remapUserId(i.CheckedByUserId),
                    CheckedByDisplayName = i.CheckedByDisplayName,
                    SortOrder = i.SortOrder,
                    CreatedAt = i.CreatedAt,
                    CreatedBy = i.CreatedBy,
                    ModifiedAt = i.ModifiedAt,
                    ModifiedBy = i.ModifiedBy,
                },
                standardBatchSize,
                cancellationToken
            );

            if (snapshot.MealPlanEntries.Count > 0)
            {
                var allEntryIds = snapshot.MealPlanEntries.Select(e => e.Id).ToHashSet();
                var insertedEntryIds = new HashSet<Guid>();
                var remainingEntries = snapshot.MealPlanEntries.ToList();

                while (remainingEntries.Count > 0)
                {
                    var wave = remainingEntries
                        .Where(e =>
                            !e.SourceMealPlanEntryId.HasValue
                            || insertedEntryIds.Contains(e.SourceMealPlanEntryId.Value)
                            || !allEntryIds.Contains(e.SourceMealPlanEntryId.Value)
                        )
                        .ToList();

                    if (wave.Count == 0)
                    {
                        // Cycle guard: break self-referential cycle cleanly if snapshot is malformed
                        wave = remainingEntries;
                    }

                    await InsertBatchedAsync(
                        wave,
                        e => new MealPlanEntry
                        {
                            Id = e.Id,
                            WeekStartDate = e.WeekStartDate,
                            IsOnDeck = e.IsOnDeck,
                            ScheduledDate = e.ScheduledDate,
                            Slot = e.Slot,
                            SortOrder = e.SortOrder,
                            EntryType = e.EntryType,
                            RecipeId = e.RecipeId,
                            TargetServings = e.TargetServings,
                            IsLeftovers = e.IsLeftovers,
                            SourceMealPlanEntryId = e.SourceMealPlanEntryId,
                            CustomTitle = e.CustomTitle,
                            Notes = e.Notes,
                            CreatedAt = e.CreatedAt,
                            CreatedBy = e.CreatedBy,
                            ModifiedAt = e.ModifiedAt,
                            ModifiedBy = e.ModifiedBy,
                        },
                        standardBatchSize,
                        cancellationToken
                    );

                    foreach (var inserted in wave)
                    {
                        insertedEntryIds.Add(inserted.Id);
                        remainingEntries.Remove(inserted);
                    }
                }
            }

            // Stage G: Shopping list item sources & CookLogs
            await InsertBatchedAsync(
                snapshot.ShoppingListItemSources,
                s => new ShoppingListItemSource
                {
                    Id = s.Id,
                    ShoppingListItemId = s.ShoppingListItemId,
                    SourceKind = s.SourceKind,
                    MealPlanEntryId = s.MealPlanEntryId,
                    RecipeId = s.RecipeId,
                    SourceLabel = s.SourceLabel,
                    ScheduledDate = s.ScheduledDate,
                    ContributedQuantity = s.ContributedQuantity,
                    ContributedMaxQuantity = s.ContributedMaxQuantity,
                    CreatedAt = s.CreatedAt,
                    CreatedBy = s.CreatedBy,
                    ModifiedAt = s.ModifiedAt,
                    ModifiedBy = s.ModifiedBy,
                },
                standardBatchSize,
                cancellationToken
            );

            await InsertBatchedAsync(
                snapshot.CookLogs,
                c => new CookLog
                {
                    Id = c.Id,
                    RecipeId = c.RecipeId,
                    CookedByUserId = remapUserId(c.CookedByUserId),
                    CookedByDisplayName = c.CookedByDisplayName,
                    CookedAt = c.CookedAt,
                    ServingsMade = c.ServingsMade,
                    Rating = c.Rating,
                    Notes = c.Notes,
                    PhotoImagePath = c.PhotoImagePath,
                    PhotoThumbnailPath = c.PhotoThumbnailPath,
                    CreatedAt = c.CreatedAt,
                    CreatedBy = c.CreatedBy,
                    ModifiedAt = c.ModifiedAt,
                    ModifiedBy = c.ModifiedBy,
                },
                standardBatchSize,
                cancellationToken
            );
        }
        finally
        {
            dbContext.ChangeTracker.AutoDetectChangesEnabled = previousAutoDetect;
            dbContext.ChangeTracker.Clear();
        }
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
            job.RefinedModel = null;
            job.RefinedAt = null;
            job.RefinementAttemptCount = 0;
            job.RefinementNextRetryAt = null;
            job.ConfidenceScore = null;
            job.AttemptCount = 0;
            job.NextRetryAt = null;
            job.RunNowRequested = false;
            job.RefinementRunNowRequested = false;
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

    private sealed record WipeCounts(
        int DeletedRecipesCount,
        int DeletedMealPlansCount,
        int DeletedShoppingListsCount,
        int DeletedIngestionJobsCount,
        int DeletedFoodItemsCount,
        int DeletedTagsCount,
        int DeletedCollectionsCount,
        int ResetIngestionJobsCount
    );
}

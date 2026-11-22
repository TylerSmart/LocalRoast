using System.IO.Compression;
using System.Text;
using System.Text.Json;
using App.Core.Exceptions;
using App.Core.FoodCatalog;
using App.Core.Ingestion;
using App.Core.Interfaces;
using App.Core.Maintenance;
using App.Core.MealPlans;
using App.Core.Media;
using App.Core.Recipes;
using App.Core.Shopping;
using App.Data;
using App.Data.Auditing;
using App.Data.Entities;
using App.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class AppDataMaintenanceServiceTests
{
    private AppDbContext _dbContext = null!;
    private FakeMediaStorageService _mediaStorage = null!;
    private FakeQueueSignal _queueSignal = null!;
    private AppDataMaintenanceService _service = null!;
    private Guid _userId;

    private sealed class FakeQueueSignal : IIngestionQueueSignal
    {
        public int CancelCount { get; private set; }
        public int NotifyCount { get; private set; }
        public Guid? ActiveJobId => null;
        public IReadOnlyCollection<Guid> ActiveJobIds => [];

        public void NotifyWorkAvailable() => NotifyCount++;

        public void NotifyBackgroundWorkAvailable() { }

        public void PreemptBackgroundWork() { }

        public ValueTask<bool> WaitForWorkAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(false);

        public IDisposable RegisterActiveJobExecution(Guid jobId, CancellationTokenSource cts) =>
            throw new NotImplementedException();

        public IDisposable RegisterPreemptibleExecution(Guid jobId, CancellationTokenSource cts) =>
            throw new NotImplementedException();

        public Task<IDisposable> AcquireJobExecutionAsync(
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<IDisposable> AcquireJobExecutionAsync(
            int maxConcurrency,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<IDisposable> AcquireExecutionSlotAsync(
            int maxConcurrency,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public void CancelActiveExecution(Guid? jobId = null) => CancelCount++;

        public bool IsJobActive(Guid jobId) => false;
    }

    private sealed class FakeMediaStorageService : IMediaStorageService
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool PurgeCalled { get; private set; }
        public bool PurgeUnreferencedCalled { get; private set; }
        public IReadOnlyCollection<string> PreservedPaths { get; private set; } = [];

        public Task<UploadedRecipeCoverDto> SaveRecipeCoverAsync(
            Stream content,
            string originalFileName,
            long contentLength,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<UploadedMediaDto> SaveStepImageAsync(
            Stream content,
            string originalFileName,
            long contentLength,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<UploadedCookLogPhotoDto> SaveCookLogPhotoAsync(
            Stream content,
            string originalFileName,
            long contentLength,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task DeleteMediaFileAsync(
            string? mediaUrlPath,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;

        public Task<SavedIngestionImageDto> SaveIngestionPageImageAsync(
            Stream content,
            string originalFileName,
            long contentLength,
            string jobToken,
            int pageNumber,
            bool generateThumbnail,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<SavedIngestionPdfDto> SaveIngestionPdfAsync(
            Stream content,
            string originalFileName,
            long contentLength,
            string jobToken,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<Stream> OpenMediaReadStreamAsync(
            string mediaUrlPath,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<byte[]> EncodeMediaImageAsVisionJpegAsync(
            string mediaUrlPath,
            int maxDimensionPx = 1568,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<UploadedRecipeCoverDto> PromoteIngestionImageToRecipeCoverAsync(
            string sourceMediaUrlPath,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<SavedRecipeAttachmentFileDto> PromoteIngestionFileToAttachmentAsync(
            string sourceMediaUrlPath,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<SavedRecipeAttachmentFileDto> SaveDirectRecipeAttachmentAsync(
            Stream content,
            string originalFileName,
            string? contentType,
            long contentLength,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<SavedRecipeAttachmentFileDto> SaveIngestionSourceTextAsync(
            string text,
            string originalFileName,
            string jobToken,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task PurgeAllMediaAsync(CancellationToken cancellationToken = default)
        {
            PurgeCalled = true;
            Files.Clear();
            return Task.CompletedTask;
        }

        public Task PurgeUnreferencedMediaAsync(
            IReadOnlyCollection<string> preservedMediaUrlPaths,
            CancellationToken cancellationToken = default
        )
        {
            PurgeUnreferencedCalled = true;
            PreservedPaths = preservedMediaUrlPaths.ToList();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StoredMediaFileEntry>> ListAllMediaFilesAsync(
            CancellationToken cancellationToken = default
        )
        {
            var entries = Files
                .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                .Select(kvp => new StoredMediaFileEntry(
                    kvp.Key,
                    () => new MemoryStream(kvp.Value, writable: false)
                ))
                .ToList();
            return Task.FromResult<IReadOnlyList<StoredMediaFileEntry>>(entries);
        }

        public async Task RestoreMediaFileAsync(
            string relativePath,
            Stream content,
            CancellationToken cancellationToken = default
        )
        {
            if (
                !MediaBackupPathValidator.TryNormalizeRelativePath(relativePath, out var normalized)
            )
            {
                throw new InvalidOperationException($"Invalid media path '{relativePath}'.");
            }

            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, cancellationToken);
            Files[normalized] = ms.ToArray();
        }
    }

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new AppDbContext(options);
        _mediaStorage = new FakeMediaStorageService();
        _queueSignal = new FakeQueueSignal();
        _service = new AppDataMaintenanceService(
            _dbContext,
            _mediaStorage,
            NullLogger<AppDataMaintenanceService>.Instance,
            _queueSignal
        );

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "TestChef",
            NormalizedUsername = "TESTCHEF",
            Email = "chef@example.com",
            NormalizedEmail = "CHEF@EXAMPLE.COM",
            PasswordHash = "hash123",
        };
        _dbContext.Users.Add(user);
        _dbContext.SaveChanges();
        _userId = user.Id;
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dbContext.Database.EnsureDeleted();
        _dbContext.Dispose();
    }

    [TestMethod]
    public async Task ResetAppDataAsync_WipesCustomData_AndRestoresAllBuiltInDefaultTags()
    {
        // 1. Seed custom tag and a collection
        _dbContext.Tags.Add(
            new Tag
            {
                Id = Guid.NewGuid(),
                Name = "My Custom Tag",
                NormalizedName = "my custom tag",
                Slug = "custom-my-custom-tag",
                Category = TagCategory.Custom,
                SortOrder = 999,
                IsSystemSeeded = false,
                CreatedBy = "TestChef",
                ModifiedBy = "TestChef",
            }
        );

        _dbContext.Collections.Add(
            new Collection
            {
                Id = Guid.NewGuid(),
                Name = "Weeknight Dinners",
                Slug = "weeknight-dinners",
                SortOrder = 1,
                CreatedBy = "TestChef",
                ModifiedBy = "TestChef",
            }
        );

        // 2. Also seed only one built-in tag to simulate deleted/missing defaults
        _dbContext.Tags.Add(
            new Tag
            {
                Id = Guid.NewGuid(),
                Name = "Breakfast",
                NormalizedName = "breakfast",
                Slug = "course-breakfast",
                Category = TagCategory.Course,
                SortOrder = 10,
                IsSystemSeeded = true,
                CreatedBy = "System",
                ModifiedBy = "System",
            }
        );

        await _dbContext.SaveChangesAsync();
        Assert.AreEqual(2, await _dbContext.Tags.CountAsync());

        // 3. Run ResetAppDataAsync
        var result = await _service.ResetAppDataAsync();

        // 4. Assert
        Assert.AreEqual(2, result.DeletedTagsCount);
        Assert.AreEqual(1, result.DeletedCollectionsCount);
        Assert.AreEqual(BuiltInTags.All.Count, result.RestoredDefaultTagsCount);
        Assert.IsTrue(_mediaStorage.PurgeCalled);

        // User is still present
        var user = await _dbContext.Users.FindAsync(_userId);
        Assert.IsNotNull(user);

        // All built-in tags are present and no custom tags remain
        var allTags = await _dbContext.Tags.ToListAsync();
        Assert.AreEqual(BuiltInTags.All.Count, allTags.Count);
        Assert.IsTrue(allTags.All(t => t.IsSystemSeeded));
        Assert.IsFalse(allTags.Any(t => t.Name == "My Custom Tag"));

        // Verify key default categories are populated
        Assert.IsTrue(allTags.Any(t => t.Name == "Breakfast" && t.Category == TagCategory.Course));
        Assert.IsTrue(allTags.Any(t => t.Name == "Italian" && t.Category == TagCategory.Cuisine));
        Assert.IsTrue(
            allTags.Any(t => t.Name == "Vegetarian" && t.Category == TagCategory.Dietary)
        );
        Assert.IsTrue(allTags.Any(t => t.Name == "Alcohol" && t.Category == TagCategory.Dietary));
        Assert.IsTrue(
            allTags.Any(t => t.Name == "Sheet Pan" && t.Category == TagCategory.Equipment)
        );
        Assert.IsTrue(allTags.Any(t => t.Name == "Chicken" && t.Category == TagCategory.Protein));
        Assert.IsTrue(
            allTags.Any(t => t.Name == "Thanksgiving" && t.Category == TagCategory.Occasion)
        );
        Assert.IsTrue(
            allTags.Any(t => t.Name == "Freezer-Friendly" && t.Category == TagCategory.MealPrep)
        );
    }

    [TestMethod]
    public async Task ResetAppDataAsync_KeepIngestionJobsTrue_ResetsJobsAndPreservesReferencedAssets()
    {
        var bookCollection = new Collection
        {
            Id = Guid.NewGuid(),
            Name = "America's Test Kitchen",
            Slug = "americas-test-kitchen",
            SortOrder = 1,
            CreatedBy = "TestChef",
            ModifiedBy = "TestChef",
        };
        var extraCollection = new Collection
        {
            Id = Guid.NewGuid(),
            Name = "Favorites",
            Slug = "favorites",
            SortOrder = 2,
            CreatedBy = "TestChef",
            ModifiedBy = "TestChef",
        };
        _dbContext.Collections.AddRange(bookCollection, extraCollection);

        var recipe = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Test Chili",
            Slug = "test-chili",
            CoverImagePath = "/api/uploads/recipes/recipe-cover.webp",
            CreatedBy = "TestChef",
            ModifiedBy = "TestChef",
        };
        _dbContext.Recipes.Add(recipe);

        var rawJob = new IngestionJob
        {
            Id = Guid.NewGuid(),
            Channel = IngestionChannel.RawText,
            Status = IngestionJobStatus.Published,
            DisplayTitle = "Test Chili",
            RawText = "Chili recipe text",
            NormalizedSourceText = "Normalized text",
            SourceFilesJson = JsonSerializer.Serialize(
                new[]
                {
                    new IngestionSourceFileDto(
                        1,
                        "chili.txt",
                        "/api/uploads/ingestion/raw-source.txt",
                        "text/plain",
                        120
                    ),
                }
            ),
            DownloadedCoverImagePath = "/api/uploads/recipes/job-cover.webp",
            DownloadedThumbnailImagePath = "/api/uploads/recipes/job-thumb.webp",
            InitialCollectionIdsJson = JsonSerializer.Serialize(new[] { bookCollection.Id }),
            ResultingRecipeId = recipe.Id,
            ExtractedDraftJson = "{\"Title\":\"Test Chili\"}",
            ExtractedModel = "gemma4:e4b",
            RefinedModel = "gemma4:12b",
            RefinedAt = DateTimeOffset.UtcNow,
            RefinementAttemptCount = 2,
            RefinementNextRetryAt = DateTimeOffset.UtcNow.AddMinutes(10),
            ConfidenceScore = 0.96m,
            AttemptCount = 1,
            NextRetryAt = DateTimeOffset.UtcNow.AddMinutes(5),
            RunNowRequested = true,
            ProcessedAt = DateTimeOffset.UtcNow,
            StatusDetail = "Auto-published",
            CreatedBy = "TestChef",
            ModifiedBy = "TestChef",
        };

        var webJob = new IngestionJob
        {
            Id = Guid.NewGuid(),
            Channel = IngestionChannel.WebUrl,
            Status = IngestionJobStatus.Failed,
            DisplayTitle = "Web Pasta",
            SourceUrl = "https://example.com/pasta",
            ScrapedPageJson = "{\"PageTitle\":\"Web Pasta\"}",
            NormalizedSourceText = "Pasta text",
            DownloadedCoverImagePath = "/api/uploads/recipes/scraped-cover.webp",
            DownloadedThumbnailImagePath = "/api/uploads/recipes/scraped-thumb.webp",
            AttemptCount = 3,
            StatusDetail = "Failed extraction",
            CreatedBy = "TestChef",
            ModifiedBy = "TestChef",
        };

        var discardedJob = new IngestionJob
        {
            Id = Guid.NewGuid(),
            Channel = IngestionChannel.RawText,
            Status = IngestionJobStatus.Discarded,
            DisplayTitle = "Discarded Job",
            RawText = "Old",
            CreatedBy = "TestChef",
            ModifiedBy = "TestChef",
        };

        _dbContext.IngestionJobs.AddRange(rawJob, webJob, discardedJob);
        await _dbContext.SaveChangesAsync();

        var result = await _service.ResetAppDataAsync(
            new ResetAppDataRequest(KeepIngestionJobs: true)
        );

        Assert.AreEqual(1, result.DeletedRecipesCount);
        Assert.AreEqual(1, result.DeletedCollectionsCount);
        Assert.AreEqual(1, result.DeletedIngestionJobsCount);
        Assert.AreEqual(2, result.ResetIngestionJobsCount);

        Assert.IsFalse(_mediaStorage.PurgeCalled);
        Assert.IsTrue(_mediaStorage.PurgeUnreferencedCalled);
        Assert.IsTrue(_mediaStorage.PreservedPaths.Contains("/api/uploads/recipes/job-cover.webp"));
        Assert.IsTrue(_mediaStorage.PreservedPaths.Contains("/api/uploads/recipes/job-thumb.webp"));
        Assert.IsTrue(
            _mediaStorage.PreservedPaths.Contains("/api/uploads/ingestion/raw-source.txt")
        );
        Assert.IsFalse(
            _mediaStorage.PreservedPaths.Contains("/api/uploads/recipes/scraped-cover.webp")
        );
        Assert.AreEqual(1, _queueSignal.CancelCount);
        Assert.AreEqual(1, _queueSignal.NotifyCount);

        var remainingCollections = await _dbContext.Collections.ToListAsync();
        Assert.HasCount(1, remainingCollections);
        Assert.AreEqual(bookCollection.Id, remainingCollections[0].Id);

        var remainingJobs = await _dbContext
            .IngestionJobs.OrderBy(j => j.DisplayTitle)
            .ToListAsync();
        Assert.HasCount(2, remainingJobs);

        var resetRaw = remainingJobs.Single(j => j.Id == rawJob.Id);
        Assert.AreEqual(IngestionJobStatus.Queued, resetRaw.Status);
        Assert.IsNull(resetRaw.ResultingRecipeId);
        Assert.IsNull(resetRaw.ExtractedDraftJson);
        Assert.IsNull(resetRaw.ExtractedModel);
        Assert.IsNull(resetRaw.RefinedModel);
        Assert.IsNull(resetRaw.RefinedAt);
        Assert.AreEqual(0, resetRaw.RefinementAttemptCount);
        Assert.IsNull(resetRaw.RefinementNextRetryAt);
        Assert.IsNull(resetRaw.ConfidenceScore);
        Assert.AreEqual(0, resetRaw.AttemptCount);
        Assert.IsFalse(resetRaw.RunNowRequested);
        Assert.IsNull(resetRaw.ProcessedAt);
        Assert.IsNull(resetRaw.NormalizedSourceText);
        Assert.AreEqual("Chili recipe text", resetRaw.RawText);
        Assert.AreEqual("/api/uploads/recipes/job-cover.webp", resetRaw.DownloadedCoverImagePath);

        var resetWeb = remainingJobs.Single(j => j.Id == webJob.Id);
        Assert.AreEqual(IngestionJobStatus.Queued, resetWeb.Status);
        Assert.IsNull(resetWeb.ScrapedPageJson);
        Assert.IsNull(resetWeb.DownloadedCoverImagePath);
        Assert.IsNull(resetWeb.DownloadedThumbnailImagePath);
        Assert.AreEqual("https://example.com/pasta", resetWeb.SourceUrl);
    }

    [TestMethod]
    public async Task ExportAndImportBackup_RoundTripsAllTablesMediaAndEmbeddings_AcrossEnvironments()
    {
        var historicalCreatedAt = new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero);
        const string historicalCreator = "prod-origin-user";

        var tag = new Tag
        {
            Id = Guid.NewGuid(),
            Name = "Weeknight Backup",
            NormalizedName = "weeknight backup",
            Slug = "custom-weeknight-backup",
            Category = TagCategory.Custom,
            SortOrder = 99,
            CreatedAt = historicalCreatedAt,
            CreatedBy = historicalCreator,
            ModifiedAt = historicalCreatedAt,
            ModifiedBy = historicalCreator,
        };
        var collection = new Collection
        {
            Id = Guid.NewGuid(),
            Name = "ATK Favorites",
            NormalizedName = "atk favorites",
            Slug = "atk-favorites",
            SortOrder = 1,
            CreatedAt = historicalCreatedAt,
            CreatedBy = historicalCreator,
            ModifiedAt = historicalCreatedAt,
            ModifiedBy = historicalCreator,
        };
        var foodItem = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Kosher Salt",
            NormalizedName = "kosher salt",
            Category = FoodCategory.SpicesAndSeasonings,
            IsPantryStaple = true,
            CreatedAt = historicalCreatedAt,
            CreatedBy = historicalCreator,
            ModifiedAt = historicalCreatedAt,
            ModifiedBy = historicalCreator,
        };
        var foodAlias = new FoodItemAlias
        {
            Id = Guid.NewGuid(),
            FoodItemId = foodItem.Id,
            Alias = "Diamond Crystal Salt",
            NormalizedAlias = "diamond crystal salt",
            CreatedAt = historicalCreatedAt,
            CreatedBy = historicalCreator,
            ModifiedAt = historicalCreatedAt,
            ModifiedBy = historicalCreator,
        };
        var catalogEmbedding = new CatalogEmbedding
        {
            Id = Guid.NewGuid(),
            ModelTag = "qwen3-embedding:0.6b",
            NormalizedText = "kosher salt",
            Dimensions = 4,
            VectorBytes = [1, 2, 3, 4],
            CreatedAt = historicalCreatedAt,
            CreatedBy = historicalCreator,
            ModifiedAt = historicalCreatedAt,
            ModifiedBy = historicalCreator,
        };

        var subRecipe = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Brown Butter Sauce",
            Slug = "brown-butter-sauce",
            Status = RecipeStatus.Published,
            BaseServings = 2m,
            CreatedAt = historicalCreatedAt,
            CreatedBy = historicalCreator,
            ModifiedAt = historicalCreatedAt,
            ModifiedBy = historicalCreator,
        };
        var parentRecipe = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Brown Butter Pasta",
            Slug = "brown-butter-pasta",
            Status = RecipeStatus.Published,
            BaseServings = 4m,
            CoverImagePath = "/api/uploads/recipes/hero.webp",
            ThumbnailImagePath = "/api/uploads/recipes/hero-thumb.webp",
            TimesCooked = 1,
            RatingCount = 1,
            AverageRating = 5m,
            CreatedAt = historicalCreatedAt,
            CreatedBy = historicalCreator,
            ModifiedAt = historicalCreatedAt,
            ModifiedBy = historicalCreator,
        };
        var subLinkIngredient = new RecipeIngredient
        {
            Id = Guid.NewGuid(),
            RecipeId = parentRecipe.Id,
            SortOrder = 1,
            Quantity = 1m,
            Unit = MeasurementUnit.Whole,
            FoodItemId = foodItem.Id,
            LinkedRecipeId = subRecipe.Id,
            DisplayName = "Brown Butter Sauce",
            CreatedAt = historicalCreatedAt,
            CreatedBy = historicalCreator,
            ModifiedAt = historicalCreatedAt,
            ModifiedBy = historicalCreator,
        };
        var replacement = new RecipeIngredientReplacement
        {
            Id = Guid.NewGuid(),
            RecipeIngredientId = subLinkIngredient.Id,
            SortOrder = 1,
            DisplayName = "olive oil",
            Quantity = 2m,
            Unit = MeasurementUnit.Tbsp,
            FoodItemId = foodItem.Id,
            CreatedAt = historicalCreatedAt,
            CreatedBy = historicalCreator,
            ModifiedAt = historicalCreatedAt,
            ModifiedBy = historicalCreator,
        };
        var step = new RecipeStep
        {
            Id = Guid.NewGuid(),
            RecipeId = parentRecipe.Id,
            StepNumber = 1,
            Instruction = "Simmer sauce for 5 minutes.",
            ImagePath = "/api/uploads/steps/step1.webp",
            CreatedAt = historicalCreatedAt,
            CreatedBy = historicalCreator,
            ModifiedAt = historicalCreatedAt,
            ModifiedBy = historicalCreator,
        };
        var timer = new RecipeStepTimer
        {
            Id = Guid.NewGuid(),
            RecipeStepId = step.Id,
            SortOrder = 1,
            DurationSeconds = 300,
            Label = "Simmer",
            CreatedAt = historicalCreatedAt,
            CreatedBy = historicalCreator,
            ModifiedAt = historicalCreatedAt,
            ModifiedBy = historicalCreator,
        };
        var recipeTag = new RecipeTag
        {
            Id = Guid.NewGuid(),
            RecipeId = parentRecipe.Id,
            TagId = tag.Id,
            CreatedAt = historicalCreatedAt,
            CreatedBy = historicalCreator,
            ModifiedAt = historicalCreatedAt,
            ModifiedBy = historicalCreator,
        };
        var recipeCollection = new RecipeCollection
        {
            Id = Guid.NewGuid(),
            RecipeId = parentRecipe.Id,
            CollectionId = collection.Id,
            CreatedAt = historicalCreatedAt,
            CreatedBy = historicalCreator,
            ModifiedAt = historicalCreatedAt,
            ModifiedBy = historicalCreator,
        };
        var attachment = new RecipeAttachment
        {
            Id = Guid.NewGuid(),
            RecipeId = parentRecipe.Id,
            SortOrder = 1,
            FileName = "notes.pdf",
            MediaUrlPath = "/api/uploads/attachments/notes.pdf",
            ContentType = "application/pdf",
            FileSizeBytes = 12,
            CreatedAt = historicalCreatedAt,
            CreatedBy = historicalCreator,
            ModifiedAt = historicalCreatedAt,
            ModifiedBy = historicalCreator,
        };
        var recipeEmbedding = new RecipeEmbedding
        {
            Id = Guid.NewGuid(),
            RecipeId = parentRecipe.Id,
            ModelTag = "qwen3-embedding:0.6b",
            ContentHash = "abc123",
            EmbeddingText = "Brown Butter Pasta",
            Dimensions = 4,
            VectorBytes = [9, 8, 7, 6],
            CreatedAt = historicalCreatedAt,
            CreatedBy = historicalCreator,
            ModifiedAt = historicalCreatedAt,
            ModifiedBy = historicalCreator,
        };
        var shareLink = new ShareLink
        {
            Id = Guid.NewGuid(),
            RecipeId = parentRecipe.Id,
            Token = "share-token-001",
            Label = "Mom",
            IncludeCookLogs = true,
            CreatedAt = historicalCreatedAt,
            CreatedBy = historicalCreator,
            ModifiedAt = historicalCreatedAt,
            ModifiedBy = historicalCreator,
        };
        var ingestionJob = new IngestionJob
        {
            Id = Guid.NewGuid(),
            Channel = IngestionChannel.RawText,
            Status = IngestionJobStatus.Published,
            DisplayTitle = "Brown Butter Pasta",
            RawText = "Original pasta recipe",
            ResultingRecipeId = parentRecipe.Id,
            DownloadedCoverImagePath = "/api/uploads/ingestion/source.webp",
            CreatedAt = historicalCreatedAt,
            CreatedBy = historicalCreator,
            ModifiedAt = historicalCreatedAt,
            ModifiedBy = historicalCreator,
        };
        var template = new MealPlanTemplate
        {
            Id = Guid.NewGuid(),
            Name = "Autumn Week",
            NormalizedName = "autumn week",
            CreatedAt = historicalCreatedAt,
            CreatedBy = historicalCreator,
            ModifiedAt = historicalCreatedAt,
            ModifiedBy = historicalCreator,
        };
        var templateItem = new MealPlanTemplateItem
        {
            Id = Guid.NewGuid(),
            TemplateId = template.Id,
            DayOffset = 1,
            Slot = MealSlot.Dinner,
            SortOrder = 1,
            EntryType = MealPlanEntryType.Recipe,
            RecipeId = parentRecipe.Id,
            CreatedAt = historicalCreatedAt,
            CreatedBy = historicalCreator,
            ModifiedAt = historicalCreatedAt,
            ModifiedBy = historicalCreator,
        };
        var rootMealPlanEntry = new MealPlanEntry
        {
            Id = Guid.NewGuid(),
            WeekStartDate = new DateOnly(2026, 11, 16),
            ScheduledDate = new DateOnly(2026, 11, 17),
            Slot = MealSlot.Dinner,
            SortOrder = 1,
            EntryType = MealPlanEntryType.Recipe,
            RecipeId = parentRecipe.Id,
            CreatedAt = historicalCreatedAt,
            CreatedBy = historicalCreator,
            ModifiedAt = historicalCreatedAt,
            ModifiedBy = historicalCreator,
        };
        var leftoverMealPlanEntry = new MealPlanEntry
        {
            Id = Guid.NewGuid(),
            WeekStartDate = new DateOnly(2026, 11, 16),
            ScheduledDate = new DateOnly(2026, 11, 18),
            Slot = MealSlot.Lunch,
            SortOrder = 1,
            EntryType = MealPlanEntryType.Recipe,
            RecipeId = parentRecipe.Id,
            IsLeftovers = true,
            SourceMealPlanEntryId = rootMealPlanEntry.Id,
            CreatedAt = historicalCreatedAt,
            CreatedBy = historicalCreator,
            ModifiedAt = historicalCreatedAt,
            ModifiedBy = historicalCreator,
        };
        var shoppingList = new ShoppingList
        {
            Id = Guid.NewGuid(),
            Name = "Weekly Groceries",
            NormalizedName = "weekly groceries",
            IsPinned = true,
            CreatedAt = historicalCreatedAt,
            CreatedBy = historicalCreator,
            ModifiedAt = historicalCreatedAt,
            ModifiedBy = historicalCreator,
        };
        var shoppingItem = new ShoppingListItem
        {
            Id = Guid.NewGuid(),
            ShoppingListId = shoppingList.Id,
            FoodItemId = foodItem.Id,
            DisplayName = "Kosher Salt",
            IsChecked = true,
            CheckedByUserId = _userId,
            CheckedByDisplayName = "ProdChef",
            SortOrder = 1,
            CreatedAt = historicalCreatedAt,
            CreatedBy = historicalCreator,
            ModifiedAt = historicalCreatedAt,
            ModifiedBy = historicalCreator,
        };
        var shoppingSource = new ShoppingListItemSource
        {
            Id = Guid.NewGuid(),
            ShoppingListItemId = shoppingItem.Id,
            SourceKind = ShoppingListSourceKind.MealPlanEntry,
            MealPlanEntryId = rootMealPlanEntry.Id,
            RecipeId = parentRecipe.Id,
            SourceLabel = "Brown Butter Pasta",
            CreatedAt = historicalCreatedAt,
            CreatedBy = historicalCreator,
            ModifiedAt = historicalCreatedAt,
            ModifiedBy = historicalCreator,
        };
        var cookLog = new CookLog
        {
            Id = Guid.NewGuid(),
            RecipeId = parentRecipe.Id,
            CookedByUserId = _userId,
            CookedByDisplayName = "ProdChef",
            CookedAt = historicalCreatedAt,
            Rating = 5,
            PhotoImagePath = "/api/uploads/cook-logs/log.webp",
            CreatedAt = historicalCreatedAt,
            CreatedBy = historicalCreator,
            ModifiedAt = historicalCreatedAt,
            ModifiedBy = historicalCreator,
        };

        _dbContext.Tags.Add(tag);
        _dbContext.Collections.Add(collection);
        _dbContext.FoodItems.Add(foodItem);
        _dbContext.FoodItemAliases.Add(foodAlias);
        _dbContext.CatalogEmbeddings.Add(catalogEmbedding);
        _dbContext.Recipes.AddRange(subRecipe, parentRecipe);
        _dbContext.RecipeIngredients.Add(subLinkIngredient);
        _dbContext.RecipeIngredientReplacements.Add(replacement);
        _dbContext.RecipeSteps.Add(step);
        _dbContext.RecipeStepTimers.Add(timer);
        _dbContext.RecipeTags.Add(recipeTag);
        _dbContext.RecipeCollections.Add(recipeCollection);
        _dbContext.RecipeAttachments.Add(attachment);
        _dbContext.RecipeEmbeddings.Add(recipeEmbedding);
        _dbContext.ShareLinks.Add(shareLink);
        _dbContext.IngestionJobs.Add(ingestionJob);
        _dbContext.MealPlanTemplates.Add(template);
        _dbContext.MealPlanTemplateItems.Add(templateItem);
        _dbContext.MealPlanEntries.AddRange(rootMealPlanEntry, leftoverMealPlanEntry);
        _dbContext.ShoppingLists.Add(shoppingList);
        _dbContext.ShoppingListItems.Add(shoppingItem);
        _dbContext.ShoppingListItemSources.Add(shoppingSource);
        _dbContext.CookLogs.Add(cookLog);
        await _dbContext.SaveChangesAsync();

        _mediaStorage.Files["recipes/hero.webp"] = [10, 20, 30];
        _mediaStorage.Files["cook-logs/log.webp"] = [40, 50, 60];

        using var archiveStream = new MemoryStream();
        await _service.ExportBackupToStreamAsync(archiveStream);
        archiveStream.Position = 0;

        // Import into a completely separate target database with a different user + AuditSaveChangesInterceptor
        var targetUserId = Guid.NewGuid();
        var interceptor = new AuditSaveChangesInterceptor(
            new StubUserContext(targetUserId.ToString())
        );
        var targetOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(interceptor)
            .Options;

        await using var targetDb = new AppDbContext(targetOptions);
        targetDb.Users.Add(
            new User
            {
                Id = targetUserId,
                Username = "LocalChef",
                Email = "local@example.com",
                PasswordHash = "hash",
            }
        );
        targetDb.PersonalAccessTokens.Add(
            new PersonalAccessToken
            {
                Id = Guid.NewGuid(),
                UserId = targetUserId,
                Name = "Local Token",
                TokenHash = "hash",
                TokenPrefix = "lr_pat_local",
            }
        );
        targetDb.HouseholdLlmSettings.Add(
            new HouseholdLlmSettings
            {
                Id = Guid.NewGuid(),
                SingletonKey = HouseholdLlmSettings.DefaultSingletonKey,
                EnableIngestion = true,
                AutoResumeQueue = false,
            }
        );
        targetDb.Recipes.Add(
            new Recipe
            {
                Id = Guid.NewGuid(),
                Title = "Stale Local Recipe",
                Slug = "stale-local-recipe",
            }
        );
        await targetDb.SaveChangesAsync();

        var targetMedia = new FakeMediaStorageService();
        targetMedia.Files["recipes/stale.webp"] = [99, 99];

        var targetService = new AppDataMaintenanceService(
            targetDb,
            targetMedia,
            NullLogger<AppDataMaintenanceService>.Instance
        );

        var importResult = await targetService.ImportBackupAsync(archiveStream, targetUserId);

        Assert.AreEqual(2, importResult.ImportedRecipesCount);
        Assert.AreEqual(1, importResult.ImportedFoodItemsCount);
        Assert.AreEqual(1, importResult.ImportedTagsCount);
        Assert.AreEqual(1, importResult.ImportedCollectionsCount);
        Assert.AreEqual(3, importResult.ImportedMealPlansCount);
        Assert.AreEqual(1, importResult.ImportedShoppingListsCount);
        Assert.AreEqual(1, importResult.ImportedIngestionJobsCount);
        Assert.AreEqual(1, importResult.ImportedCookLogsCount);
        Assert.AreEqual(2, importResult.ImportedMediaFilesCount);

        // Verify stale local culinary data & media were wiped while target auth & settings survived
        Assert.IsFalse(await targetDb.Recipes.AnyAsync(r => r.Title == "Stale Local Recipe"));
        Assert.IsFalse(targetMedia.Files.ContainsKey("recipes/stale.webp"));
        CollectionAssert.AreEqual(
            new byte[] { 10, 20, 30 },
            targetMedia.Files["recipes/hero.webp"]
        );
        CollectionAssert.AreEqual(
            new byte[] { 40, 50, 60 },
            targetMedia.Files["cook-logs/log.webp"]
        );

        Assert.AreEqual(1, await targetDb.Users.CountAsync());
        Assert.AreEqual(1, await targetDb.PersonalAccessTokens.CountAsync());
        var targetSettings = await targetDb.HouseholdLlmSettings.SingleAsync();
        Assert.IsFalse(targetSettings.EnableIngestion);

        // Verify historical audit metadata & relationships survived AuditSaveChangesInterceptor
        var restoredParent = await targetDb.Recipes.SingleAsync(r => r.Id == parentRecipe.Id);
        Assert.AreEqual(historicalCreatedAt, restoredParent.CreatedAt);
        Assert.AreEqual(historicalCreator, restoredParent.CreatedBy);

        var restoredIng = await targetDb.RecipeIngredients.SingleAsync(i =>
            i.Id == subLinkIngredient.Id
        );
        Assert.AreEqual(subRecipe.Id, restoredIng.LinkedRecipeId);

        var restoredLeftover = await targetDb.MealPlanEntries.SingleAsync(e =>
            e.Id == leftoverMealPlanEntry.Id
        );
        Assert.AreEqual(rootMealPlanEntry.Id, restoredLeftover.SourceMealPlanEntryId);

        var restoredCatEmb = await targetDb.CatalogEmbeddings.SingleAsync();
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, restoredCatEmb.VectorBytes);

        // Verify cross-env user FK remapped cleanly while display name stayed intact
        var restoredCookLog = await targetDb.CookLogs.SingleAsync();
        Assert.AreEqual(targetUserId, restoredCookLog.CookedByUserId);
        Assert.AreEqual("ProdChef", restoredCookLog.CookedByDisplayName);

        var restoredShoppingItem = await targetDb.ShoppingListItems.SingleAsync();
        Assert.AreEqual(targetUserId, restoredShoppingItem.CheckedByUserId);
        Assert.AreEqual("ProdChef", restoredShoppingItem.CheckedByDisplayName);
    }

    [TestMethod]
    public async Task ImportBackup_InvalidZipOrPathTraversal_ThrowsDomainValidationWithoutWipingData()
    {
        var existingRecipe = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Do Not Wipe Me",
            Slug = "do-not-wipe-me",
        };
        _dbContext.Recipes.Add(existingRecipe);
        await _dbContext.SaveChangesAsync();
        _mediaStorage.Files["recipes/keep.webp"] = [7, 7, 7];

        // 1. Corrupt non-ZIP stream
        using (var invalidStream = new MemoryStream(Encoding.UTF8.GetBytes("not-a-zip")))
        {
            await Assert.ThrowsAsync<DomainValidationException>(async () =>
                await _service.ImportBackupAsync(invalidStream, _userId)
            );
        }

        // 2. Crafted ZIP with unsafe path traversal upload entry
        using (var traversalZipStream = new MemoryStream())
        {
            using (
                var zip = new ZipArchive(traversalZipStream, ZipArchiveMode.Create, leaveOpen: true)
            )
            {
                var mEntry = zip.CreateEntry("manifest.json");
                await using (var s = new StreamWriter(mEntry.Open()))
                {
                    await s.WriteAsync(
                        """{"formatVersion":1,"exportedAt":"2026-11-22T00:00:00Z","application":"LocalRoast","includesMedia":true,"counts":{},"mediaFilesCount":1}"""
                    );
                }

                var dEntry = zip.CreateEntry("data/snapshot.json");
                await using (var s = new StreamWriter(dEntry.Open()))
                {
                    await s.WriteAsync("{}");
                }

                var badMedia = zip.CreateEntry("uploads/../../evil.txt");
                await using var badStream = badMedia.Open();
                await badStream.WriteAsync(Encoding.UTF8.GetBytes("evil"));
            }

            traversalZipStream.Position = 0;
            await Assert.ThrowsAsync<DomainValidationException>(async () =>
                await _service.ImportBackupAsync(traversalZipStream, _userId)
            );
        }

        // Existing recipe & media must be completely untouched
        Assert.IsFalse(_mediaStorage.PurgeCalled);
        Assert.IsTrue(_mediaStorage.Files.ContainsKey("recipes/keep.webp"));
        Assert.IsTrue(await _dbContext.Recipes.AnyAsync(r => r.Id == existingRecipe.Id));
    }

    private sealed class StubUserContext(string userId) : IUserContext
    {
        public string UserId => userId;
    }
}

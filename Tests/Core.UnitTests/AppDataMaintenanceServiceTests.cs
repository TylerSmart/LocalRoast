using System.Text.Json;
using App.Core.Ingestion;
using App.Core.Maintenance;
using App.Core.Media;
using App.Core.Recipes;
using App.Data;
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
}

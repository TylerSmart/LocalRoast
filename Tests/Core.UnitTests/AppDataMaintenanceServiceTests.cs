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
    private AppDataMaintenanceService _service = null!;
    private Guid _userId;

    private sealed class FakeMediaStorageService : IMediaStorageService
    {
        public bool PurgeCalled { get; private set; }

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
    }

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new AppDbContext(options);
        _mediaStorage = new FakeMediaStorageService();
        _service = new AppDataMaintenanceService(
            _dbContext,
            _mediaStorage,
            NullLogger<AppDataMaintenanceService>.Instance
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
}

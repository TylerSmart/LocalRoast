using App.Core.Exceptions;
using App.Core.Interfaces;
using App.Core.Media;
using App.Core.Recipes;
using App.Data;
using App.Data.Entities;
using App.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class CookLogServiceTests
{
    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class MockMediaStorageService : IMediaStorageService
    {
        public List<string?> DeletedFiles { get; } = [];

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
        )
        {
            DeletedFiles.Add(mediaUrlPath);
            return Task.CompletedTask;
        }

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

        public Task PurgeAllMediaAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class TestUserContext(string userId) : IUserContext
    {
        public string UserId => userId;
    }

    private AppDbContext _dbContext = null!;
    private FixedTimeProvider _timeProvider = null!;
    private MockMediaStorageService _mediaStorageService = null!;
    private Recipe _recipe = null!;
    private User _user = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new AppDbContext(options);
        _timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero)
        );
        _mediaStorageService = new MockMediaStorageService();

        _user = new User
        {
            Id = Guid.NewGuid(),
            Username = "tyler",
            Email = "tyler@example.com",
            PasswordHash = "hash",
        };
        _dbContext.Users.Add(_user);

        _recipe = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Homemade Bolognese",
            Slug = "homemade-bolognese",
            BaseServings = 4m,
            Status = RecipeStatus.Published,
            TimesCooked = 0,
            AverageRating = null,
            RatingCount = 0,
        };
        _dbContext.Recipes.Add(_recipe);
        _dbContext.SaveChanges();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dbContext.Dispose();
    }

    [TestMethod]
    public async Task CreateCookLogAsync_ValidRequestWithActor_CreatesLogAndRecomputesStats()
    {
        var userContext = new TestUserContext(_user.Id.ToString());
        var service = new CookLogService(
            _dbContext,
            _mediaStorageService,
            userContext,
            _timeProvider
        );

        var request = new UpsertCookLogRequest(
            CookedAt: _timeProvider.GetUtcNow().AddHours(-2),
            ServingsMade: 6m,
            Rating: 5,
            Notes: "Turned out rich and savory! Used extra wine.",
            PhotoImagePath: "/api/uploads/cook-logs/bolognese-photo.webp",
            PhotoThumbnailPath: "/api/uploads/cook-logs/bolognese-thumb.webp"
        );

        var result = await service.CreateCookLogAsync(_recipe.Id, request);

        Assert.IsNotNull(result);
        Assert.AreEqual(_user.Id, result.CookLog.CookedByUserId);
        Assert.AreEqual("tyler", result.CookLog.CookedByDisplayName);
        Assert.AreEqual(5, result.CookLog.Rating);
        Assert.AreEqual(6m, result.CookLog.ServingsMade);
        Assert.AreEqual("Turned out rich and savory! Used extra wine.", result.CookLog.Notes);

        // Check recipe stats
        Assert.AreEqual(1, result.RecipeStats.TimesCooked);
        Assert.AreEqual(5.00m, result.RecipeStats.AverageRating);
        Assert.AreEqual(1, result.RecipeStats.RatingCount);
        Assert.AreEqual(request.CookedAt, result.RecipeStats.LastCookedAt);
        Assert.AreEqual(
            "Turned out rich and savory! Used extra wine.",
            result.RecipeStats.LatestCookNote
        );

        var refreshedRecipe = await _dbContext.Recipes.FindAsync(_recipe.Id);
        Assert.IsNotNull(refreshedRecipe);
        Assert.AreEqual(1, refreshedRecipe.TimesCooked);
        Assert.AreEqual(5.00m, refreshedRecipe.AverageRating);
        Assert.AreEqual(1, refreshedRecipe.RatingCount);
    }

    [TestMethod]
    public async Task CreateCookLogAsync_UnauthenticatedActor_DefaultsToHouseholdMember()
    {
        var service = new CookLogService(_dbContext, _mediaStorageService, null, _timeProvider);

        var request = new UpsertCookLogRequest(Rating: 4, Notes: "Simple cook");
        var result = await service.CreateCookLogAsync(_recipe.Id, request);

        Assert.IsNull(result.CookLog.CookedByUserId);
        Assert.AreEqual("Household Member", result.CookLog.CookedByDisplayName);
        Assert.AreEqual(_timeProvider.GetUtcNow(), result.CookLog.CookedAt);
    }

    [TestMethod]
    public async Task CreateCookLogAsync_FutureDate_ThrowsValidationException()
    {
        var service = new CookLogService(_dbContext, _mediaStorageService, null, _timeProvider);
        var request = new UpsertCookLogRequest(CookedAt: _timeProvider.GetUtcNow().AddDays(2));

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.CreateCookLogAsync(_recipe.Id, request)
        );
    }

    [TestMethod]
    public async Task CreateCookLogAsync_InvalidRating_ThrowsValidationException()
    {
        var service = new CookLogService(_dbContext, _mediaStorageService, null, _timeProvider);
        var request = new UpsertCookLogRequest(Rating: 6);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.CreateCookLogAsync(_recipe.Id, request)
        );
    }

    [TestMethod]
    public async Task CreateCookLogAsync_InvalidPhotoPath_ThrowsValidationException()
    {
        var service = new CookLogService(_dbContext, _mediaStorageService, null, _timeProvider);
        var request = new UpsertCookLogRequest(PhotoImagePath: "/etc/passwd");

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.CreateCookLogAsync(_recipe.Id, request)
        );
    }

    [TestMethod]
    public async Task UpdateCookLogAsync_ReplacesPhoto_DeletesOldFilesAndRecalculates()
    {
        var service = new CookLogService(_dbContext, _mediaStorageService, null, _timeProvider);

        var createResult = await service.CreateCookLogAsync(
            _recipe.Id,
            new UpsertCookLogRequest(
                Rating: 4,
                Notes: "First attempt",
                PhotoImagePath: "/api/uploads/cook-logs/photo1.webp",
                PhotoThumbnailPath: "/api/uploads/cook-logs/thumb1.webp"
            )
        );

        var updateRequest = new UpsertCookLogRequest(
            Rating: 5,
            Notes: "Refined recipe",
            PhotoImagePath: "/api/uploads/cook-logs/photo2.webp",
            PhotoThumbnailPath: "/api/uploads/cook-logs/thumb2.webp"
        );

        var updated = await service.UpdateCookLogAsync(
            _recipe.Id,
            createResult.CookLog.Id,
            updateRequest
        );

        Assert.IsNotNull(updated);
        Assert.AreEqual(5, updated.CookLog.Rating);
        Assert.AreEqual("Refined recipe", updated.CookLog.Notes);
        Assert.AreEqual(5.00m, updated.RecipeStats.AverageRating);

        CollectionAssert.Contains(
            _mediaStorageService.DeletedFiles,
            "/api/uploads/cook-logs/photo1.webp"
        );
        CollectionAssert.Contains(
            _mediaStorageService.DeletedFiles,
            "/api/uploads/cook-logs/thumb1.webp"
        );
    }

    [TestMethod]
    public async Task DeleteCookLogAsync_RecalculatesStatsAndCleansUpPhotos()
    {
        var service = new CookLogService(_dbContext, _mediaStorageService, null, _timeProvider);

        var log1 = await service.CreateCookLogAsync(
            _recipe.Id,
            new UpsertCookLogRequest(
                CookedAt: _timeProvider.GetUtcNow().AddDays(-5),
                Rating: 3,
                Notes: "A bit dry"
            )
        );

        var log2 = await service.CreateCookLogAsync(
            _recipe.Id,
            new UpsertCookLogRequest(
                CookedAt: _timeProvider.GetUtcNow().AddDays(-1),
                Rating: 5,
                Notes: "Perfected cooking time!",
                PhotoImagePath: "/api/uploads/cook-logs/delicious.webp",
                PhotoThumbnailPath: "/api/uploads/cook-logs/delicious-thumb.webp"
            )
        );

        var recipeAfterBoth = await _dbContext.Recipes.FindAsync(_recipe.Id);
        Assert.AreEqual(2, recipeAfterBoth!.TimesCooked);
        Assert.AreEqual(4.00m, recipeAfterBoth.AverageRating);
        Assert.AreEqual(2, recipeAfterBoth.RatingCount);

        var deleted = await service.DeleteCookLogAsync(_recipe.Id, log2.CookLog.Id);
        Assert.IsTrue(deleted);

        var recipeAfterDelete = await _dbContext.Recipes.FindAsync(_recipe.Id);
        Assert.AreEqual(1, recipeAfterDelete!.TimesCooked);
        Assert.AreEqual(3.00m, recipeAfterDelete.AverageRating);
        Assert.AreEqual(1, recipeAfterDelete.RatingCount);
        Assert.AreEqual(_timeProvider.GetUtcNow().AddDays(-5), recipeAfterDelete.LastCookedAt);

        CollectionAssert.Contains(
            _mediaStorageService.DeletedFiles,
            "/api/uploads/cook-logs/delicious.webp"
        );
        CollectionAssert.Contains(
            _mediaStorageService.DeletedFiles,
            "/api/uploads/cook-logs/delicious-thumb.webp"
        );
    }
}

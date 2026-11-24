using App.Core.Media;
using App.Core.Recipes;
using App.Data;
using App.Data.Entities;
using App.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class RecipeRandomizerServiceTests
{
    private sealed class StubMediaStorageService : IMediaStorageService
    {
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

        public Task PurgeAllMediaAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task PurgeUnreferencedMediaAsync(
            IReadOnlyCollection<string> preservedMediaUrlPaths,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;

        public Task<IReadOnlyList<StoredMediaFileEntry>> ListAllMediaFilesAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<StoredMediaFileEntry>>([]);

        public Task RestoreMediaFileAsync(
            string relativePath,
            Stream content,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;
    }

    private AppDbContext _dbContext = null!;
    private RecipeService _recipeService = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new AppDbContext(options);
        _recipeService = new RecipeService(_dbContext, new StubMediaStorageService());
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dbContext.Dispose();
    }

    private async Task<RecipeDetailDto> SeedRecipeAsync(
        string title,
        RecipeStatus status = RecipeStatus.Published,
        int ingredientCount = 2,
        int stepCount = 2
    )
    {
        var ingredients = Enumerable
            .Range(1, ingredientCount)
            .Select(i => new RecipeIngredientInput(
                Quantity: i,
                Unit: MeasurementUnit.Cup,
                DisplayName: $"{title} Ingredient {i}"
            ))
            .ToArray();

        var steps = Enumerable
            .Range(1, stepCount)
            .Select(i => new RecipeStepInput(Instruction: $"{title} Step {i} instruction."))
            .ToArray();

        return await _recipeService.CreateRecipeAsync(
            new UpsertRecipeRequest(
                Title: title,
                Description: $"Description for {title}",
                Status: status,
                BaseServings: 4m,
                Ingredients: ingredients,
                Steps: steps
            )
        );
    }

    [TestMethod]
    public async Task GetRandomRecipesAsync_DefaultsToPublishedOnly_AndMapsOrderedDetails()
    {
        var pub1 = await SeedRecipeAsync("Published Pasta", RecipeStatus.Published, 3, 2);
        var pub2 = await SeedRecipeAsync("Published Tacos", RecipeStatus.Published, 2, 3);
        var draft = await SeedRecipeAsync("Draft Soup", RecipeStatus.DraftReview);
        var archived = await SeedRecipeAsync("Archived Salad", RecipeStatus.Archived);

        var results = await _recipeService.GetRandomRecipesAsync(
            new RandomRecipeQueryParameters(Count: 10)
        );

        Assert.AreEqual(2, results.Count);
        var returnedIds = results.Select(r => r.Id).ToHashSet();
        Assert.IsTrue(returnedIds.Contains(pub1.Id));
        Assert.IsTrue(returnedIds.Contains(pub2.Id));
        Assert.IsFalse(returnedIds.Contains(draft.Id));
        Assert.IsFalse(returnedIds.Contains(archived.Id));

        var pastaResult = results.Single(r => r.Id == pub1.Id);
        Assert.AreEqual(3, pastaResult.Ingredients.Count);
        Assert.AreEqual("Published Pasta Ingredient 1", pastaResult.Ingredients[0].DisplayName);
        Assert.AreEqual(2, pastaResult.Steps.Count);
        Assert.AreEqual(1, pastaResult.Steps[0].StepNumber);
        Assert.AreEqual(2, pastaResult.Steps[1].StepNumber);
    }

    [TestMethod]
    public async Task GetRandomRecipesAsync_RespectsExcludeIdsAndCountClamping()
    {
        var r1 = await SeedRecipeAsync("Recipe One");
        var r2 = await SeedRecipeAsync("Recipe Two");
        var r3 = await SeedRecipeAsync("Recipe Three");

        var singleResult = await _recipeService.GetRandomRecipesAsync(
            new RandomRecipeQueryParameters(Count: 0, ExcludeIds: [r1.Id])
        );

        Assert.AreEqual(1, singleResult.Count);
        Assert.AreNotEqual(r1.Id, singleResult[0].Id);

        var allExcluded = await _recipeService.GetRandomRecipesAsync(
            new RandomRecipeQueryParameters(Count: 10, ExcludeIds: [r1.Id, r2.Id, r3.Id])
        );
        Assert.AreEqual(0, allExcluded.Count);

        var archivedQuery = await _recipeService.GetRandomRecipesAsync(
            new RandomRecipeQueryParameters(Count: 10, Status: RecipeStatus.Archived)
        );
        Assert.AreEqual(0, archivedQuery.Count);
    }

    [TestMethod]
    public async Task GetRandomRecipesAsync_ClampsExcessiveCountToMax25()
    {
        for (var i = 1; i <= 28; i++)
        {
            await SeedRecipeAsync($"Seeded Recipe {i}", RecipeStatus.Published, 1, 1);
        }

        var results = await _recipeService.GetRandomRecipesAsync(
            new RandomRecipeQueryParameters(Count: 100)
        );

        Assert.AreEqual(25, results.Count);
        Assert.AreEqual(25, results.Select(r => r.Id).Distinct().Count());
    }
}

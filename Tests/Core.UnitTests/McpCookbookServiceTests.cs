using App.Core.Exceptions;
using App.Core.FoodCatalog;
using App.Core.Ingestion;
using App.Core.Mcp;
using App.Core.Media;
using App.Core.Recipes;
using App.Data;
using App.Data.Entities;
using App.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class McpCookbookServiceTests
{
    private AppDbContext _dbContext = null!;
    private McpCookbookService _service = null!;
    private RecipeService _recipeService = null!;

    private sealed class DummyMediaStorageService : IMediaStorageService
    {
        public Task<UploadedRecipeCoverDto> SaveRecipeCoverAsync(
            Stream content,
            string originalFileName,
            long contentLength,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(
                new UploadedRecipeCoverDto(
                    "/api/uploads/recipes/cover.webp",
                    "/api/uploads/recipes/thumb.webp"
                )
            );

        public Task<UploadedMediaDto> SaveStepImageAsync(
            Stream content,
            string originalFileName,
            long contentLength,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new UploadedMediaDto("/api/uploads/recipes/step.webp"));

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
        ) =>
            Task.FromResult(
                new SavedIngestionImageDto("/api/uploads/ingestion/page.webp", null, 100, 100, 1000)
            );

        public Task<SavedIngestionPdfDto> SaveIngestionPdfAsync(
            Stream content,
            string originalFileName,
            long contentLength,
            string jobToken,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new SavedIngestionPdfDto("/api/uploads/ingestion/doc.pdf", 1000));

        public Task<Stream> OpenMediaReadStreamAsync(
            string mediaUrlPath,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<Stream>(new MemoryStream());

        public Task<byte[]> EncodeMediaImageAsVisionJpegAsync(
            string mediaUrlPath,
            int maxDimensionPx = 1568,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(Array.Empty<byte>());

        public Task<UploadedRecipeCoverDto> PromoteIngestionImageToRecipeCoverAsync(
            string sourceMediaUrlPath,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(
                new UploadedRecipeCoverDto(
                    "/api/uploads/recipes/promoted.webp",
                    "/api/uploads/recipes/promoted-thumb.webp"
                )
            );

        public Task<SavedRecipeAttachmentFileDto> PromoteIngestionFileToAttachmentAsync(
            string sourceMediaUrlPath,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(
                new SavedRecipeAttachmentFileDto(
                    "/api/uploads/recipes/promoted.webp",
                    "image/webp",
                    1000
                )
            );

        public Task<SavedRecipeAttachmentFileDto> SaveDirectRecipeAttachmentAsync(
            Stream content,
            string originalFileName,
            string? contentType,
            long contentLength,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(
                new SavedRecipeAttachmentFileDto(
                    "/api/uploads/recipes/att.pdf",
                    contentType ?? "application/pdf",
                    1000
                )
            );
    }

    private sealed class DummyIngestionQueueSignal : IIngestionQueueSignal
    {
        public void NotifyWorkAvailable() { }

        public ValueTask<bool> WaitForWorkAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(false);
    }

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new AppDbContext(options);
        var mediaService = new DummyMediaStorageService();
        _recipeService = new RecipeService(_dbContext, mediaService);
        var foodCatalogService = new FoodCatalogService(_dbContext);
        var taxonomyService = new TaxonomyService(_dbContext);
        var queueSignal = new DummyIngestionQueueSignal();
        var ingestionService = new IngestionService(
            _dbContext,
            _recipeService,
            mediaService,
            queueSignal
        );

        _service = new McpCookbookService(
            _dbContext,
            _recipeService,
            ingestionService,
            taxonomyService,
            foodCatalogService
        );

        // Seed basic aisle and food items
        var produceAisle = new StoreAisle
        {
            Id = Guid.NewGuid(),
            Name = "Produce",
            NormalizedName = "produce",
            SortOrder = 1,
        };
        var pantryAisle = new StoreAisle
        {
            Id = Guid.NewGuid(),
            Name = "Pantry",
            NormalizedName = "pantry",
            SortOrder = 2,
        };
        _dbContext.StoreAisles.AddRange(produceAisle, pantryAisle);

        var garlic = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Garlic",
            NormalizedName = "garlic",
            DefaultStoreAisleId = produceAisle.Id,
            IsPantryStaple = false,
        };
        garlic.Aliases.Add(
            new FoodItemAlias
            {
                Id = Guid.NewGuid(),
                FoodItemId = garlic.Id,
                Alias = "Garlic Cloves",
                NormalizedAlias = "garlic cloves",
            }
        );

        var salt = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Kosher Salt",
            NormalizedName = "kosher salt",
            DefaultStoreAisleId = pantryAisle.Id,
            IsPantryStaple = true,
        };

        _dbContext.FoodItems.AddRange(garlic, salt);
        _dbContext.SaveChanges();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dbContext.Database.EnsureDeleted();
        _dbContext.Dispose();
    }

    [TestMethod]
    public async Task GetHouseholdTaxonomyAsync_ReturnsAislesUnitsAndTaxonomy()
    {
        var taxonomy = await _service.GetHouseholdTaxonomyAsync();

        Assert.IsNotNull(taxonomy);
        Assert.IsTrue(taxonomy.StoreAisles.Count >= 2);
        Assert.IsTrue(taxonomy.SupportedUnits.Contains("tbsp"));
        Assert.IsTrue(taxonomy.SupportedUnits.Contains("cup"));
    }

    [TestMethod]
    public async Task SaveStructuredRecipeAsync_ValidRequest_CreatesRecipeWithStepsAndTimers()
    {
        var request = new McpSaveStructuredRecipeRequest(
            Title: "Garlic Butter Pasta",
            Description: "Quick weeknight dish",
            PublishImmediately: true,
            BaseServings: 2m,
            Difficulty: "Easy",
            Ingredients:
            [
                new McpStructuredIngredientInput(
                    DisplayName: "3 cloves garlic, minced",
                    CanonicalFoodName: "Garlic",
                    Quantity: 3m,
                    Unit: "clove",
                    PreparationNote: "minced"
                ),
                new McpStructuredIngredientInput(
                    DisplayName: "1 pinch kosher salt",
                    CanonicalFoodName: "Kosher Salt",
                    Quantity: 1m,
                    Unit: "pinch"
                ),
            ],
            Steps:
            [
                new McpStructuredStepInput(
                    Instruction: "Boil pasta in salted water for 10 minutes until al dente.",
                    IngredientIndices: [1]
                ),
                new McpStructuredStepInput(
                    Instruction: "Saute minced garlic in butter for 3 minutes.",
                    IngredientIndices: [0]
                ),
            ]
        );

        var recipe = await _service.SaveStructuredRecipeAsync(request);

        Assert.IsNotNull(recipe);
        Assert.AreEqual("Garlic Butter Pasta", recipe.Title);
        Assert.AreEqual(RecipeStatus.Published, recipe.Status);
        Assert.AreEqual(2, recipe.Ingredients.Count);
        Assert.AreEqual(2, recipe.Steps.Count);

        // Auto-detected timers
        Assert.AreEqual(1, recipe.Steps[0].Timers.Count);
        Assert.AreEqual(600, recipe.Steps[0].Timers[0].DurationSeconds);
        Assert.AreEqual(1, recipe.Steps[1].Timers.Count);
        Assert.AreEqual(180, recipe.Steps[1].Timers[0].DurationSeconds);
    }

    [TestMethod]
    public async Task SearchRecipesAsync_FiltersByRatingAndQuery()
    {
        await _service.SaveStructuredRecipeAsync(
            new McpSaveStructuredRecipeRequest(
                Title: "Spicy Tacos",
                PublishImmediately: true,
                Ingredients:
                [
                    new McpStructuredIngredientInput("Garlic", CanonicalFoodName: "Garlic"),
                ],
                Steps: [new McpStructuredStepInput("Assemble tacos.")]
            )
        );

        var results = await _service.SearchRecipesAsync(new McpRecipeSearchRequest(Query: "tacos"));
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("Spicy Tacos", results[0].Title);

        var emptyResults = await _service.SearchRecipesAsync(
            new McpRecipeSearchRequest(Query: "nonexistent")
        );
        Assert.AreEqual(0, emptyResults.Count);
    }

    [TestMethod]
    public async Task GetRecipeAsync_BySlug_ReturnsMcpRecipeDetailWithStepCallouts()
    {
        var created = await _service.SaveStructuredRecipeAsync(
            new McpSaveStructuredRecipeRequest(
                Title: "Crispy Roasted Potatoes",
                PublishImmediately: true,
                Ingredients:
                [
                    new McpStructuredIngredientInput(
                        "Garlic",
                        CanonicalFoodName: "Garlic",
                        Quantity: 2m,
                        Unit: "clove"
                    ),
                ],
                Steps:
                [
                    new McpStructuredStepInput(
                        "Toss potatoes with garlic.",
                        IngredientIndices: [0]
                    ),
                ]
            )
        );

        var detail = await _service.GetRecipeAsync(slug: created.Slug);

        Assert.IsNotNull(detail);
        Assert.AreEqual(created.Id, detail.Id);
        Assert.AreEqual(1, detail.Steps.Count);
        Assert.AreEqual(1, detail.Steps[0].StepIngredients.Count);
        Assert.AreEqual("Garlic", detail.Steps[0].StepIngredients[0].DisplayName);
    }

    [TestMethod]
    public async Task MatchRecipesByIngredientNamesAsync_ResolvesAliasesAndRanks()
    {
        await _service.SaveStructuredRecipeAsync(
            new McpSaveStructuredRecipeRequest(
                Title: "Garlic Soup",
                PublishImmediately: true,
                Ingredients:
                [
                    new McpStructuredIngredientInput(
                        "Garlic Cloves",
                        CanonicalFoodName: "Garlic Cloves"
                    ),
                ],
                Steps: [new McpStructuredStepInput("Simmer garlic soup.")]
            )
        );

        var matchResult = await _service.MatchRecipesByIngredientNamesAsync([
            "garlic cloves",
            "mystery spice",
        ]);

        Assert.IsNotNull(matchResult);
        Assert.AreEqual(1, matchResult.ResolvedIngredients.Count);
        Assert.AreEqual("garlic cloves", matchResult.ResolvedIngredients[0].InputName);
        Assert.AreEqual("Garlic", matchResult.ResolvedIngredients[0].CanonicalName);
        Assert.AreEqual(1, matchResult.UnresolvedIngredientNames.Count);
        Assert.AreEqual("mystery spice", matchResult.UnresolvedIngredientNames[0]);
        Assert.AreEqual(1, matchResult.Matches.Count);
        Assert.AreEqual("Garlic Soup", matchResult.Matches[0].Recipe.Title);
    }

    [TestMethod]
    public async Task EnqueueIngestionAsync_UrlOrText_EnqueuesProperly()
    {
        var textJob = await _service.EnqueueIngestionAsync(
            new McpEnqueueIngestionRequest(RawText: "Recipe Text Here", TitleHint: "Pasted Recipe")
        );

        Assert.IsNotNull(textJob);
        Assert.AreEqual(IngestionChannel.RawText, textJob.Channel);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            _service.EnqueueIngestionAsync(
                new McpEnqueueIngestionRequest(Url: "https://example.com/recipe", RawText: "both")
            )
        );
    }
}

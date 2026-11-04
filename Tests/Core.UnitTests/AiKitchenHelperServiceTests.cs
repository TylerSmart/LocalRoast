using App.Core.AiHelpers;
using App.Core.Exceptions;
using App.Core.Ingestion;
using App.Core.MealPlans;
using App.Core.Recipes;
using App.Data;
using App.Data.Entities;
using App.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class AiKitchenHelperServiceTests
{
    private sealed class MockLlmSettingsService(EffectiveLlmConnectionConfig config)
        : ILlmSettingsService
    {
        public EffectiveLlmConnectionConfig Config { get; set; } = config;

        public Task<LlmSettingsDto> GetSettingsAsync(
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<EffectiveLlmConnectionConfig> GetEffectiveConfigAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult(Config);

        public Task<LlmSettingsDto> UpdateSettingsAsync(
            UpdateLlmSettingsRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<LlmConnectionTestResultDto> TestConnectionAsync(
            TestLlmConnectionRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<LlmSettingsDto> SetIngestionEnabledAsync(
            bool enabled,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();
    }

    private sealed class MockLlmExtractionClient : ILlmExtractionClient
    {
        public bool ProbeReachable { get; set; } = true;
        public LlmSubstitutionPayload SubstitutionResult { get; set; } = new([]);
        public LlmDraftMealPlanPayload DraftResult { get; set; } = new([]);
        public LlmSubstitutionContext? LastSubstitutionContext { get; private set; }
        public LlmDraftMealPlanContext? LastDraftContext { get; private set; }

        public Task<LlmConnectionTestResultDto> ProbeAndListModelsAsync(
            EffectiveLlmConnectionConfig config,
            int timeoutSeconds = 15,
            CancellationToken cancellationToken = default
        )
        {
            return Task.FromResult(
                new LlmConnectionTestResultDto(
                    IsReachable: ProbeReachable,
                    Provider: config.Provider,
                    NormalizedBaseUrl: config.BaseUrl,
                    LatencyMs: 42,
                    Models: ProbeReachable ? ["qwen2.5:14b"] : [],
                    ErrorMessage: ProbeReachable ? null : "Host offline"
                )
            );
        }

        public Task<LlmExtractedRecipePayload> ExtractRecipeFromTextAsync(
            EffectiveLlmConnectionConfig config,
            LlmExtractionContext context,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<LlmExtractedRecipePayload> ExtractRecipeFromImagesAsync(
            EffectiveLlmConnectionConfig config,
            LlmVisionExtractionContext context,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<LlmSubstitutionPayload> SuggestIngredientSubstitutionsAsync(
            EffectiveLlmConnectionConfig config,
            LlmSubstitutionContext context,
            CancellationToken cancellationToken = default
        )
        {
            LastSubstitutionContext = context;
            return Task.FromResult(SubstitutionResult);
        }

        public Task<LlmDraftMealPlanPayload> DraftWeeklyMealPlanAsync(
            EffectiveLlmConnectionConfig config,
            LlmDraftMealPlanContext context,
            CancellationToken cancellationToken = default
        )
        {
            LastDraftContext = context;
            return Task.FromResult(DraftResult);
        }
    }

    private AppDbContext _dbContext = null!;
    private MockLlmSettingsService _settingsService = null!;
    private MockLlmExtractionClient _llmClient = null!;
    private AiKitchenHelperService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new AppDbContext(options);

        var defaultConfig = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "qwen2.5:14b",
            VisionModel: "qwen2.5vl:7b",
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 60,
            AutoPublishHighConfidenceText: false
        );

        _settingsService = new MockLlmSettingsService(defaultConfig);
        _llmClient = new MockLlmExtractionClient();
        _service = new AiKitchenHelperService(_dbContext, _settingsService, _llmClient);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dbContext.Dispose();
    }

    [TestMethod]
    public async Task GetStatusAsync_NoTextModelConfigured_ReturnsUnavailableImmediately()
    {
        _settingsService.Config = _settingsService.Config with { TextModel = null };

        var status = await _service.GetStatusAsync();

        Assert.IsFalse(status.IsAvailable);
        Assert.AreEqual(0, status.LatencyMs);
        Assert.IsNotNull(status.UnavailableReason);
        StringAssert.Contains(status.UnavailableReason, "No Text LLM model");
    }

    [TestMethod]
    public async Task GetStatusAsync_ConfiguredAndReachable_ReturnsAvailable()
    {
        _llmClient.ProbeReachable = true;

        var status = await _service.GetStatusAsync();

        Assert.IsTrue(status.IsAvailable);
        Assert.AreEqual("qwen2.5:14b", status.TextModel);
        Assert.AreEqual(42, status.LatencyMs);
        Assert.IsNull(status.UnavailableReason);
    }

    [TestMethod]
    public async Task SuggestIngredientSubstitutionsAsync_SubRecipeIngredient_ThrowsDomainValidationException()
    {
        var recipe = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Fish Tacos",
            Slug = "fish-tacos",
            BaseServings = 4,
        };
        var subRecipe = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Chipotle Crema",
            Slug = "chipotle-crema",
        };
        var subIng = new RecipeIngredient
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            LinkedRecipeId = subRecipe.Id,
            DisplayName = "Chipotle Crema",
        };
        recipe.Ingredients.Add(subIng);
        _dbContext.Recipes.AddRange(recipe, subRecipe);
        await _dbContext.SaveChangesAsync();

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            _service.SuggestIngredientSubstitutionsAsync(
                new SuggestIngredientSubstitutionsRequest(recipe.Id, subIng.Id)
            )
        );
    }

    [TestMethod]
    public async Task SuggestIngredientSubstitutionsAsync_ValidIngredient_CallsLlmAndReturnsFormattedOptions()
    {
        var recipe = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Fluffy Pancakes",
            Slug = "fluffy-pancakes",
            BaseServings = 4,
        };
        var buttermilk = new RecipeIngredient
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            DisplayName = "Buttermilk",
            Quantity = 1,
            Unit = MeasurementUnit.Cup,
        };
        var flour = new RecipeIngredient
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            DisplayName = "Flour",
            Quantity = 2,
            Unit = MeasurementUnit.Cup,
        };
        recipe.Ingredients.Add(buttermilk);
        recipe.Ingredients.Add(flour);

        var step1 = new RecipeStep
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            StepNumber = 1,
            Instruction = "Whisk buttermilk and flour together.",
        };
        step1.IngredientReferences.Add(
            new RecipeStepIngredient { RecipeStepId = step1.Id, RecipeIngredientId = buttermilk.Id }
        );
        recipe.Steps.Add(step1);

        _dbContext.Recipes.Add(recipe);
        await _dbContext.SaveChangesAsync();

        _llmClient.SubstitutionResult = new LlmSubstitutionPayload([
            new LlmSubstitutionOptionItem(
                Label: "Milk + Lemon Juice",
                RatioAndAmount: "2 tbsp lemon juice + milk to measure 2 cups",
                CulinaryNotes: "Let sit 5 minutes to curdle.",
                FlavorOrTextureImpact: "Very close to buttermilk with similar acidity."
            ),
        ]);

        var response = await _service.SuggestIngredientSubstitutionsAsync(
            new SuggestIngredientSubstitutionsRequest(
                RecipeId: recipe.Id,
                RecipeIngredientId: buttermilk.Id,
                TargetServings: 8,
                TargetSystem: UnitSystem.Original
            )
        );

        Assert.AreEqual(recipe.Id, response.RecipeId);
        Assert.AreEqual("Buttermilk", response.IngredientDisplayName);
        Assert.AreEqual("2 cups", response.ScaledAmountDisplay);
        Assert.AreEqual(1, response.Substitutions.Count);
        Assert.AreEqual("Milk + Lemon Juice", response.Substitutions[0].Label);

        Assert.IsNotNull(_llmClient.LastSubstitutionContext);
        Assert.AreEqual(8m, _llmClient.LastSubstitutionContext.TargetServings);
        Assert.AreEqual("2 cups", _llmClient.LastSubstitutionContext.ScaledAmountDisplay);
    }

    [TestMethod]
    public async Task DraftWeeklyMealPlanAsync_NoPublishedRecipes_ThrowsDomainValidationException()
    {
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            _service.DraftWeeklyMealPlanAsync(
                new DraftWeeklyMealPlanRequest(Prompt: "Fast dinners")
            )
        );
    }

    [TestMethod]
    public async Task DraftWeeklyMealPlanAsync_FiltersOutHallucinatedRecipes_AndReturnsGroundedCandidates()
    {
        var recipe1 = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Lemon Herb Chicken",
            Slug = "lemon-herb-chicken",
            Status = RecipeStatus.Published,
            BaseServings = 4,
            TotalTimeMinutes = 30,
        };
        var recipe2 = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Sesame Tofu Bowls",
            Slug = "sesame-tofu-bowls",
            Status = RecipeStatus.Published,
            BaseServings = 4,
            TotalTimeMinutes = 25,
        };
        _dbContext.Recipes.AddRange(recipe1, recipe2);
        await _dbContext.SaveChangesAsync();

        _llmClient.DraftResult = new LlmDraftMealPlanPayload([
            new LlmDraftMealProposalItem(
                CatalogIndex: 1,
                RecipeTitle: "Lemon Herb Chicken",
                DayOffset: 0,
                Slot: "Dinner",
                IsOnDeck: false,
                TargetServings: 4,
                Rationale: "Quick weeknight protein."
            ),
            new LlmDraftMealProposalItem(
                CatalogIndex: 999, // Hallucination!
                RecipeTitle: "Imaginary Dragon Stew",
                DayOffset: 1,
                Slot: "Dinner",
                IsOnDeck: false,
                TargetServings: 4,
                Rationale: "Non-existent dish."
            ),
        ]);

        var response = await _service.DraftWeeklyMealPlanAsync(
            new DraftWeeklyMealPlanRequest(Prompt: "2 quick dinners", MealCount: 2)
        );

        Assert.AreEqual(1, response.Candidates.Count);
        Assert.AreEqual(recipe1.Id, response.Candidates[0].RecipeId);
        Assert.AreEqual("Lemon Herb Chicken", response.Candidates[0].Recipe.Title);
        Assert.AreEqual(0, response.Candidates[0].DayOffset);
    }
}

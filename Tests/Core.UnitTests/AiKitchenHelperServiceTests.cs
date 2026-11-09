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
    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    }

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

        public Task<IReadOnlyList<LlmModelProfileDto>> GetModelProfilesAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<LlmModelProfileDto>>(LlmModelProfiles.All);

        public Task<LlmModelProfileDto> CreateModelProfileAsync(
            UpsertLlmModelProfileRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<LlmModelProfileDto> UpdateModelProfileAsync(
            string id,
            UpsertLlmModelProfileRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task DeleteModelProfileAsync(
            string id,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<IReadOnlyList<LlmModelProfileDto>> RestoreDefaultModelProfilesAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<LlmModelProfileDto>>(LlmModelProfiles.All);
    }

    private sealed class MockLlmExtractionClient : ILlmExtractionClient
    {
        public Task<LlmAutoRepairAndTriageResult> TriageAndRepairRecipeAsync(
            EffectiveLlmConnectionConfig config,
            LlmAutoRepairAndTriageContext context,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<LlmRecipeRepairResult> RepairRecipeDraftAsync(
            EffectiveLlmConnectionConfig config,
            LlmRecipeRepairContext context,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public bool ProbeReachable { get; set; } = true;
        public LlmSubstitutionPayload SubstitutionResult { get; set; } = new([]);
        public LlmDraftMealPlanPayload DraftResult { get; set; } = new([]);
        public string KitchenAnswer { get; set; } = "Cut the vegetable into thin matchsticks.";
        public LlmSubstitutionContext? LastSubstitutionContext { get; private set; }
        public LlmDraftMealPlanContext? LastDraftContext { get; private set; }
        public LlmKitchenQuestionContext? LastKitchenQuestionContext { get; private set; }
        public CancellationToken LastKitchenQuestionCancellationToken { get; private set; }

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

        public Task<string?> StructureRecipeSourceAsync(
            EffectiveLlmConnectionConfig config,
            string sourceText,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<LlmExtractedRecipePayload> ExtractRecipeFromTextAsync(
            EffectiveLlmConnectionConfig config,
            LlmExtractionContext context,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<string?> TranscribeRecipeImagesAsync(
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

        public Task<string> AnswerKitchenQuestionAsync(
            EffectiveLlmConnectionConfig config,
            LlmKitchenQuestionContext context,
            CancellationToken cancellationToken = default
        )
        {
            LastKitchenQuestionContext = context;
            LastKitchenQuestionCancellationToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(KitchenAnswer);
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
        _service = new AiKitchenHelperService(
            _dbContext,
            _settingsService,
            _llmClient,
            new FixedTimeProvider()
        );
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
    public async Task AskKitchenQuestionAsync_GeneralQuestion_DoesNotRequireARecipe()
    {
        _llmClient.KitchenAnswer = "  Aquafaba is the liquid from cooked chickpeas.  ";

        var result = await _service.AskKitchenQuestionAsync(
            new AskKitchenQuestionRequest("  What is aquafaba?  ")
        );

        Assert.AreEqual("Aquafaba is the liquid from cooked chickpeas.", result.Answer);
        Assert.AreEqual("qwen2.5:14b", result.ModelUsed);
        var context = _llmClient.LastKitchenQuestionContext;
        Assert.IsNotNull(context);
        Assert.AreEqual("What is aquafaba?", context.Question);
        Assert.IsNull(context.RecipeTitle);
        Assert.IsNull(context.IngredientName);
        Assert.IsNull(context.StepInstruction);
        Assert.IsEmpty(context.RecipeIngredients);
    }

    [TestMethod]
    public async Task AskKitchenQuestionAsync_UsesOnlyTheSelectedRecipeContext()
    {
        var recipe = new Recipe
        {
            Title = "Kiwi Salad",
            Slug = "kiwi-salad",
            Description = "A fresh salad",
        };
        var ingredient = new RecipeIngredient
        {
            RecipeId = recipe.Id,
            DisplayName = "Kiwi",
            PreparationNote = "peeled and sliced",
            SortOrder = 0,
        };
        var step = new RecipeStep
        {
            RecipeId = recipe.Id,
            StepNumber = 1,
            Instruction = "Julienne the peeled kiwi.",
        };
        recipe.Ingredients.Add(ingredient);
        recipe.Steps.Add(step);
        var otherRecipe = new Recipe { Title = "Unrelated Recipe", Slug = "unrelated" };
        var foreignIngredient = new RecipeIngredient
        {
            RecipeId = otherRecipe.Id,
            DisplayName = "Unrelated ingredient",
        };
        var foreignStep = new RecipeStep
        {
            RecipeId = otherRecipe.Id,
            StepNumber = 1,
            Instruction = "Unrelated instruction.",
        };
        otherRecipe.Ingredients.Add(foreignIngredient);
        otherRecipe.Steps.Add(foreignStep);
        _dbContext.Recipes.AddRange(recipe, otherRecipe);
        await _dbContext.SaveChangesAsync();

        var response = await _service.AskKitchenQuestionAsync(
            new AskKitchenQuestionRequest(
                "How should I prepare this?",
                recipe.Id,
                ingredient.Id,
                step.Id
            )
        );

        Assert.AreEqual("Cut the vegetable into thin matchsticks.", response.Answer);
        var context = _llmClient.LastKitchenQuestionContext;
        Assert.IsNotNull(context);
        Assert.AreEqual(recipe.Title, context.RecipeTitle);
        Assert.AreEqual(recipe.Description, context.RecipeDescription);
        CollectionAssert.AreEqual(new[] { "Kiwi" }, context.RecipeIngredients.ToArray());
        Assert.AreEqual("Kiwi", context.IngredientName);
        Assert.AreEqual("peeled and sliced", context.IngredientPreparationNote);
        Assert.AreEqual(1, context.StepNumber);
        Assert.AreEqual(step.Instruction, context.StepInstruction);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.AskKitchenQuestionAsync(
                new AskKitchenQuestionRequest("Explain this.", recipe.Id, foreignIngredient.Id)
            )
        );
        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.AskKitchenQuestionAsync(
                new AskKitchenQuestionRequest(
                    "Explain this.",
                    RecipeId: recipe.Id,
                    RecipeStepId: foreignStep.Id
                )
            )
        );
        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.AskKitchenQuestionAsync(
                new AskKitchenQuestionRequest("Explain this.", Guid.NewGuid())
            )
        );
    }

    [TestMethod]
    public async Task AskKitchenQuestionAsync_ValidatesQuestionAndContextIdentifiers()
    {
        foreach (var question in new[] { "", "   ", new string('x', 601) })
        {
            await Assert.ThrowsAsync<DomainValidationException>(() =>
                _service.AskKitchenQuestionAsync(new AskKitchenQuestionRequest(question))
            );
        }

        var exactlyAtLimit = await _service.AskKitchenQuestionAsync(
            new AskKitchenQuestionRequest(new string('x', 600))
        );
        Assert.IsFalse(string.IsNullOrWhiteSpace(exactlyAtLimit.Answer));

        foreach (
            var request in new[]
            {
                new AskKitchenQuestionRequest("Help", RecipeId: Guid.Empty),
                new AskKitchenQuestionRequest("Help", RecipeIngredientId: Guid.NewGuid()),
                new AskKitchenQuestionRequest("Help", RecipeStepId: Guid.NewGuid()),
                new AskKitchenQuestionRequest(
                    "Help",
                    RecipeId: Guid.NewGuid(),
                    RecipeIngredientId: Guid.Empty
                ),
                new AskKitchenQuestionRequest(
                    "Help",
                    RecipeId: Guid.NewGuid(),
                    RecipeStepId: Guid.Empty
                ),
            }
        )
        {
            await Assert.ThrowsAsync<DomainValidationException>(() =>
                _service.AskKitchenQuestionAsync(request)
            );
        }
    }

    [TestMethod]
    public async Task AskKitchenQuestionAsync_BoundsRecipeIngredientContext()
    {
        var recipe = new Recipe { Title = "Large Recipe", Slug = "large-recipe" };
        for (var index = 0; index < 35; index++)
        {
            recipe.Ingredients.Add(
                new RecipeIngredient
                {
                    RecipeId = recipe.Id,
                    SortOrder = index,
                    DisplayName = $"Ingredient {index}",
                }
            );
        }
        _dbContext.Recipes.Add(recipe);
        await _dbContext.SaveChangesAsync();

        await _service.AskKitchenQuestionAsync(new AskKitchenQuestionRequest("Help", recipe.Id));

        var context = _llmClient.LastKitchenQuestionContext;
        Assert.IsNotNull(context);
        Assert.HasCount(30, context.RecipeIngredients);
        Assert.AreEqual("Ingredient 0", context.RecipeIngredients[0]);
        Assert.AreEqual("Ingredient 29", context.RecipeIngredients[29]);
    }

    [TestMethod]
    public async Task AskKitchenQuestionAsync_ReportsUnavailableAndEmptyAnswers()
    {
        _settingsService.Config = _settingsService.Config with { TextModel = null };
        await Assert.ThrowsAsync<LlmHostUnavailableException>(() =>
            _service.AskKitchenQuestionAsync(new AskKitchenQuestionRequest("What is aquafaba?"))
        );

        _settingsService.Config = _settingsService.Config with { TextModel = "model" };
        _llmClient.KitchenAnswer = "  ";
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            _service.AskKitchenQuestionAsync(new AskKitchenQuestionRequest("What is aquafaba?"))
        );
    }

    [TestMethod]
    public async Task AskKitchenQuestionAsync_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _service.AskKitchenQuestionAsync(
                new AskKitchenQuestionRequest("What is aquafaba?"),
                cts.Token
            )
        );
        Assert.AreEqual(cts.Token, _llmClient.LastKitchenQuestionCancellationToken);
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
                CatalogIndex: 0,
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
            new DraftWeeklyMealPlanRequest(Prompt: "Quick dinners", MealCount: 1)
        );

        Assert.AreEqual(1, response.Candidates.Count);
        Assert.AreEqual(recipe1.Id, response.Candidates[0].RecipeId);
        Assert.AreEqual("Lemon Herb Chicken", response.Candidates[0].Recipe.Title);
        Assert.AreEqual(0, response.Candidates[0].DayOffset);
    }

    [TestMethod]
    public async Task DraftMealPlan_RollingDates_CookingCountAndServingsIncludeLeftovers()
    {
        var recipes = await SeedDraftRecipesAsync();
        _llmClient.DraftResult = new([
            Proposal(recipes[0].Title, -5),
            Proposal(recipes[1].Title, 999),
        ]);

        var response = await _service.DraftWeeklyMealPlanAsync(
            new(
                WeekStartDate: new DateOnly(2026, 11, 1),
                Prompt: "Two cooking sessions",
                MealCount: 2,
                ServingsPerMeal: 2
            )
        );

        Assert.AreEqual(new DateOnly(2026, 11, 5), response.WeekStartDate);
        Assert.AreEqual(new DateOnly(2026, 11, 11), response.WeekEndDate);
        Assert.AreEqual(response.WeekStartDate, _llmClient.LastDraftContext!.WeekStartDate);
        Assert.AreEqual(2, response.Candidates.Count);
        Assert.AreEqual(8m, response.Candidates[0].TargetServings);
        Assert.AreEqual(6m, response.Candidates[1].TargetServings);
        Assert.AreEqual(3, response.Candidates[0].LeftoverDates!.Count);
        Assert.AreEqual(2, response.Candidates[1].LeftoverDates!.Count);
        var coveredDates = response
            .Candidates.SelectMany(c => new[] { c.ScheduledDate!.Value }.Concat(c.LeftoverDates!))
            .OrderBy(d => d)
            .ToArray();
        CollectionAssert.AreEqual(
            Enumerable.Range(0, 7).Select(i => response.WeekStartDate.AddDays(i)).ToArray(),
            coveredDates
        );
        Assert.IsTrue(response.Candidates.All(c => c.Slot == MealSlot.Dinner && !c.IsOnDeck));
    }

    [TestMethod]
    public async Task DraftMealPlan_ExistingMealsAreNotCoveredAgain_AndFutureStartIsPreserved()
    {
        var recipes = await SeedDraftRecipesAsync();
        var start = new DateOnly(2026, 11, 12);
        _dbContext.MealPlanEntries.Add(
            new MealPlanEntry
            {
                WeekStartDate = MealPlanDateHelper.NormalizeToSunday(start),
                ScheduledDate = start.AddDays(1),
                Slot = MealSlot.Dinner,
                EntryType = MealPlanEntryType.Leftovers,
                IsLeftovers = true,
                CustomTitle = "Already planned",
            }
        );
        await _dbContext.SaveChangesAsync();
        _llmClient.DraftResult = new([Proposal(recipes[0].Title)]);
        var response = await _service.DraftWeeklyMealPlanAsync(
            new(WeekStartDate: start, Prompt: "One batch", MealCount: 1, ServingsPerMeal: 1)
        );
        Assert.AreEqual(start, response.WeekStartDate);
        Assert.AreEqual(6m, response.Candidates[0].TargetServings);
        Assert.IsFalse(response.Candidates[0].LeftoverDates!.Contains(start.AddDays(1)));
    }

    [TestMethod]
    public async Task DraftMealPlan_OnDeckRetry_ExcludesOriginalChoices_AndHasNoLeftovers()
    {
        var recipes = await SeedDraftRecipesAsync();
        _llmClient.DraftResult = new([Proposal(recipes[2].Title)]);
        var response = await _service.DraftWeeklyMealPlanAsync(
            new(
                Prompt: "Try another quick dinner",
                MealCount: 1,
                PlaceOnDeck: true,
                ExcludedRecipeIds: [recipes[0].Id, recipes[1].Id],
                ServingsPerMeal: 2
            )
        );
        Assert.AreEqual(recipes[2].Id, response.Candidates[0].RecipeId);
        Assert.AreEqual(1, _llmClient.LastDraftContext!.CookbookCatalog.Count);
        Assert.IsTrue(response.Candidates[0].IsOnDeck);
        Assert.IsNull(response.Candidates[0].ScheduledDate);
        Assert.AreEqual(0, response.Candidates[0].LeftoverDates!.Count);
        Assert.AreEqual(2m, response.Candidates[0].TargetServings);

        _llmClient.DraftResult = new([Proposal(recipes[0].Title)]);
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            _service.DraftWeeklyMealPlanAsync(
                new(
                    Prompt: "No excluded recipes",
                    MealCount: 1,
                    PlaceOnDeck: true,
                    ExcludedRecipeIds: [recipes[0].Id, recipes[1].Id]
                )
            )
        );
    }

    [TestMethod]
    public async Task DraftMealPlan_InvalidCountsDatesAndIncompleteSuggestionsFailExplicitly()
    {
        var recipes = await SeedDraftRecipesAsync();
        _llmClient.DraftResult = new([Proposal(recipes[0].Title)]);
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            _service.DraftWeeklyMealPlanAsync(new(Prompt: "Dinner", MealCount: 2))
        );
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            _service.DraftWeeklyMealPlanAsync(new(Prompt: "Dinner", MealCount: 8))
        );
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            _service.DraftWeeklyMealPlanAsync(
                new(Prompt: "Dinner", MealCount: 1, ServingsPerMeal: 999)
            )
        );
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            _service.DraftWeeklyMealPlanAsync(
                new(Prompt: "Dinner", TodayDate: new DateOnly(2026, 11, 1))
            )
        );
        var response = await _service.DraftWeeklyMealPlanAsync(
            new(Prompt: "Dinner", MealCount: 1, TodayDate: new DateOnly(2026, 11, 4))
        );
        Assert.AreEqual(new DateOnly(2026, 11, 4), response.WeekStartDate);
    }

    private async Task<Recipe[]> SeedDraftRecipesAsync()
    {
        var recipes = Enumerable
            .Range(1, 3)
            .Select(i => new Recipe
            {
                Id = Guid.NewGuid(),
                Title = $"Dinner {i}",
                Slug = $"dinner-{i}",
                BaseServings = 4,
                Status = RecipeStatus.Published,
            })
            .ToArray();
        _dbContext.Recipes.AddRange(recipes);
        await _dbContext.SaveChangesAsync();
        return recipes;
    }

    private static LlmDraftMealProposalItem Proposal(string title, int dayOffset = 0) =>
        new(0, title, dayOffset, "Lunch", true, 999, "Fits your preferences.");
}

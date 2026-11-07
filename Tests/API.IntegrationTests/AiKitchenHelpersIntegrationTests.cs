using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using App.Core.AiHelpers;
using App.Core.Authentication;
using App.Core.FoodCatalog;
using App.Core.Ingestion;
using App.Core.MealPlans;
using App.Core.Recipes;
using App.Core.Shopping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.API.IntegrationTests;

[TestClass]
public sealed class AiKitchenHelpersIntegrationTests
{
    private AppWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    [TestInitialize]
    public void Setup()
    {
        _factory = new AppWebApplicationFactory();
        _client = _factory.CreateClient();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static async Task<AuthResponse> AuthenticateAsync(HttpClient client)
    {
        var email = $"chef_{Guid.NewGuid():N}@example.com";
        var username = $"chef_{Guid.NewGuid():N}"[..12];
        var register = new RegisterRequest(email, username, "KitchenSecret123!");
        var response = await client.PostAsJsonAsync("/api/auth/register", register);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.IsNotNull(auth);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            auth.Token
        );
        return auth;
    }

    private sealed class MockLlmExtractionClient : ILlmExtractionClient
    {
        public bool ProbeReachable { get; set; } = true;
        public bool ThrowUnavailable { get; set; }
        public LlmKitchenQuestionContext? LastKitchenQuestionContext { get; private set; }
        public string KitchenAnswer { get; set; } = "Julienne means cutting into thin matchsticks.";
        public LlmSubstitutionPayload SubstitutionResult { get; set; } =
            new([
                new LlmSubstitutionOptionItem(
                    Label: "Greek Yogurt",
                    RatioAndAmount: "1:1 creamy ratio",
                    CulinaryNotes: "Adds mild tang",
                    FlavorOrTextureImpact: "Creamy finish"
                ),
            ]);
        public LlmDraftMealPlanPayload DraftResult { get; set; } =
            new([
                new LlmDraftMealProposalItem(
                    CatalogIndex: 0,
                    RecipeTitle: "Test Grounded Recipe",
                    DayOffset: 0,
                    Slot: "Dinner",
                    IsOnDeck: false,
                    TargetServings: 4,
                    Rationale: "High protein meal"
                ),
                new LlmDraftMealProposalItem(
                    CatalogIndex: 999,
                    RecipeTitle: "Imaginary Hallucinated Dish",
                    DayOffset: 1,
                    Slot: "Dinner",
                    IsOnDeck: false,
                    TargetServings: 2,
                    Rationale: "Not in DB"
                ),
            ]);

        public Task<LlmConnectionTestResultDto> ProbeAndListModelsAsync(
            EffectiveLlmConnectionConfig config,
            int timeoutSeconds = 15,
            CancellationToken cancellationToken = default
        )
        {
            return Task.FromResult(
                new LlmConnectionTestResultDto(
                    IsReachable: ProbeReachable && !ThrowUnavailable,
                    Provider: config.Provider,
                    NormalizedBaseUrl: config.BaseUrl,
                    LatencyMs: 42,
                    Models: ProbeReachable && !ThrowUnavailable ? ["llama3"] : [],
                    ErrorMessage: ProbeReachable && !ThrowUnavailable ? null : "Host offline"
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
            if (ThrowUnavailable)
            {
                throw new LlmHostUnavailableException("Host offline");
            }
            return Task.FromResult(SubstitutionResult);
        }

        public Task<LlmDraftMealPlanPayload> DraftWeeklyMealPlanAsync(
            EffectiveLlmConnectionConfig config,
            LlmDraftMealPlanContext context,
            CancellationToken cancellationToken = default
        )
        {
            if (ThrowUnavailable)
            {
                throw new LlmHostUnavailableException("Host offline");
            }
            return Task.FromResult(DraftResult);
        }

        public Task<string> AnswerKitchenQuestionAsync(
            EffectiveLlmConnectionConfig config,
            LlmKitchenQuestionContext context,
            CancellationToken cancellationToken = default
        )
        {
            if (ThrowUnavailable)
            {
                throw new LlmHostUnavailableException("Host offline");
            }

            LastKitchenQuestionContext = context;
            return Task.FromResult(KitchenAnswer);
        }
    }

    [TestMethod]
    public async Task Endpoints_Unauthenticated_ReturnUnauthorized()
    {
        var statusRes = await _client.GetAsync("/api/ai-helpers/status");
        Assert.AreEqual(HttpStatusCode.Unauthorized, statusRes.StatusCode);

        var subRes = await _client.PostAsJsonAsync(
            "/api/ai-helpers/ingredient-substitutions",
            new SuggestIngredientSubstitutionsRequest(Guid.NewGuid(), Guid.NewGuid())
        );
        Assert.AreEqual(HttpStatusCode.Unauthorized, subRes.StatusCode);

        var draftRes = await _client.PostAsJsonAsync(
            "/api/ai-helpers/draft-meal-plan",
            new DraftWeeklyMealPlanRequest(Prompt: "Fast dinners")
        );
        Assert.AreEqual(HttpStatusCode.Unauthorized, draftRes.StatusCode);

        var questionRes = await _client.PostAsJsonAsync(
            "/api/ai-helpers/kitchen-question",
            new AskKitchenQuestionRequest("What is aquafaba?")
        );
        Assert.AreEqual(HttpStatusCode.Unauthorized, questionRes.StatusCode);

        var applyRes = await _client.PostAsJsonAsync(
            "/api/meal-plans/week/apply-draft",
            new ApplyDraftMealPlanRequest(
                DateOnly.FromDateTime(DateTime.UtcNow),
                [
                    new CreateMealPlanEntryRequest(
                        RecipeId: Guid.NewGuid(),
                        ScheduledDate: DateOnly.FromDateTime(DateTime.UtcNow),
                        Slot: MealSlot.Dinner
                    ),
                ]
            )
        );
        Assert.AreEqual(HttpStatusCode.Unauthorized, applyRes.StatusCode);
    }

    [TestMethod]
    public async Task GetStatus_WhenUnconfigured_Returns200WithNotAvailable()
    {
        await AuthenticateAsync(_client);

        var statusRes = await _client.GetAsync("/api/ai-helpers/status");
        Assert.AreEqual(HttpStatusCode.OK, statusRes.StatusCode);

        var status = await statusRes.Content.ReadFromJsonAsync<AiHelperStatusDto>();
        Assert.IsNotNull(status);
        Assert.IsFalse(status.IsAvailable);
        Assert.IsNotNull(status.UnavailableReason);

        var question = await _client.PostAsJsonAsync(
            "/api/ai-helpers/kitchen-question",
            new AskKitchenQuestionRequest("What is aquafaba?")
        );
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, question.StatusCode);
    }

    [TestMethod]
    public async Task AskKitchenQuestion_ValidatesContextAndReturnsExplicitFailures()
    {
        var fakeLlm = new MockLlmExtractionClient();
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var descriptor = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(ILlmExtractionClient)
                );
                if (descriptor != null)
                {
                    services.Remove(descriptor);
                }
                services.AddSingleton<ILlmExtractionClient>(fakeLlm);
            });
        });
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);
        var settings = await client.PutAsJsonAsync(
            "/api/llm-settings",
            new UpdateLlmSettingsRequest(
                Provider: LlmProvider.Ollama,
                BaseUrl: "http://localhost:11434",
                ApiKey: null,
                TextModel: "llama3",
                VisionModel: null,
                RequestTimeoutSeconds: 30
            )
        );
        Assert.AreEqual(HttpStatusCode.OK, settings.StatusCode);

        var recipeResponse = await client.PostAsJsonAsync(
            "/api/recipes",
            new UpsertRecipeRequest(
                Title: "Kiwi Salad",
                Ingredients:
                [
                    new RecipeIngredientInput(
                        Quantity: 1,
                        Unit: MeasurementUnit.Whole,
                        DisplayName: "Kiwi",
                        PreparationNote: "peeled"
                    ),
                ],
                Steps:
                [
                    new RecipeStepInput(Instruction: "Julienne the kiwi.", IngredientIndices: [0]),
                ]
            )
        );
        Assert.AreEqual(HttpStatusCode.Created, recipeResponse.StatusCode);
        var recipe = await recipeResponse.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(recipe);

        var general = await client.PostAsJsonAsync(
            "/api/ai-helpers/kitchen-question",
            new AskKitchenQuestionRequest("What is aquafaba?")
        );
        Assert.AreEqual(HttpStatusCode.OK, general.StatusCode);
        var generalAnswer = await general.Content.ReadFromJsonAsync<KitchenAnswerDto>();
        Assert.IsNotNull(generalAnswer);
        Assert.AreEqual("llama3", generalAnswer.ModelUsed);
        Assert.IsNull(fakeLlm.LastKitchenQuestionContext?.RecipeTitle);

        var contextual = await client.PostAsJsonAsync(
            "/api/ai-helpers/kitchen-question",
            new AskKitchenQuestionRequest(
                "Explain this step.",
                recipe.Id,
                recipe.Ingredients[0].Id,
                recipe.Steps[0].Id
            )
        );
        Assert.AreEqual(HttpStatusCode.OK, contextual.StatusCode);
        var answer = await contextual.Content.ReadFromJsonAsync<KitchenAnswerDto>();
        Assert.IsNotNull(answer);
        StringAssert.Contains(answer.Answer, "thin matchsticks");
        Assert.AreEqual("Kiwi", fakeLlm.LastKitchenQuestionContext?.IngredientName);
        Assert.AreEqual("peeled", fakeLlm.LastKitchenQuestionContext?.IngredientPreparationNote);
        Assert.AreEqual("Julienne the kiwi.", fakeLlm.LastKitchenQuestionContext?.StepInstruction);

        foreach (
            var invalidRequest in new[]
            {
                new AskKitchenQuestionRequest(" "),
                new AskKitchenQuestionRequest(new string('x', 601)),
                new AskKitchenQuestionRequest("Help", RecipeIngredientId: recipe.Ingredients[0].Id),
                new AskKitchenQuestionRequest("Help", RecipeId: Guid.Empty),
            }
        )
        {
            var invalid = await client.PostAsJsonAsync(
                "/api/ai-helpers/kitchen-question",
                invalidRequest
            );
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        foreach (
            var missingRequest in new[]
            {
                new AskKitchenQuestionRequest("Help", Guid.NewGuid()),
                new AskKitchenQuestionRequest("Help", recipe.Id, Guid.NewGuid()),
                new AskKitchenQuestionRequest(
                    "Help",
                    RecipeId: recipe.Id,
                    RecipeStepId: Guid.NewGuid()
                ),
            }
        )
        {
            var missing = await client.PostAsJsonAsync(
                "/api/ai-helpers/kitchen-question",
                missingRequest
            );
            Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        }

        fakeLlm.KitchenAnswer = " ";
        var empty = await client.PostAsJsonAsync(
            "/api/ai-helpers/kitchen-question",
            new AskKitchenQuestionRequest("What is julienne?")
        );
        Assert.AreEqual(HttpStatusCode.BadRequest, empty.StatusCode);
        StringAssert.Contains(await empty.Content.ReadAsStringAsync(), "empty answer");

        fakeLlm.ThrowUnavailable = true;
        var offline = await client.PostAsJsonAsync(
            "/api/ai-helpers/kitchen-question",
            new AskKitchenQuestionRequest("What is julienne?")
        );
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, offline.StatusCode);
        StringAssert.Contains(await offline.Content.ReadAsStringAsync(), "Host offline");
    }

    [TestMethod]
    public async Task SuggestSubstitutions_AndDraftMealPlan_WhenLlmHostUnavailable_Return503ProblemDetails()
    {
        var fakeLlm = new MockLlmExtractionClient { ThrowUnavailable = true };

        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var descriptor = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(ILlmExtractionClient)
                );
                if (descriptor != null)
                {
                    services.Remove(descriptor);
                }
                services.AddSingleton<ILlmExtractionClient>(fakeLlm);
            });
        });

        using var client = factory.CreateClient();
        await AuthenticateAsync(client);

        // Configure LLM settings with an endpoint so IsConfigured is true
        var putRes = await client.PutAsJsonAsync(
            "/api/llm-settings",
            new UpdateLlmSettingsRequest(
                Provider: LlmProvider.Ollama,
                BaseUrl: "http://localhost:11434",
                ApiKey: null,
                TextModel: "llama3",
                VisionModel: null,
                RequestTimeoutSeconds: 30
            )
        );
        Assert.AreEqual(HttpStatusCode.OK, putRes.StatusCode);

        // Create a recipe with an ingredient
        var foodRes = await client.PostAsJsonAsync(
            "/api/food-items",
            new CreateFoodItemRequest("Sour Cream")
        );
        Assert.AreEqual(HttpStatusCode.Created, foodRes.StatusCode);
        var food = await foodRes.Content.ReadFromJsonAsync<FoodItemDto>();
        Assert.IsNotNull(food);

        var recipeRes = await client.PostAsJsonAsync(
            "/api/recipes",
            new UpsertRecipeRequest(
                Title: "Tacos",
                Description: "Tasty tacos",
                BaseServings: 4m,
                Ingredients:
                [
                    new RecipeIngredientInput(
                        SectionName: null,
                        Quantity: 1m,
                        MaxQuantity: null,
                        Unit: MeasurementUnit.Cup,
                        CustomUnitName: null,
                        FoodItemId: food.Id,
                        LinkedRecipeId: null,
                        DisplayName: "Sour Cream",
                        PreparationNote: null
                    ),
                ],
                Steps:
                [
                    new RecipeStepInput(
                        SectionName: null,
                        Instruction: "Top with sour cream",
                        IngredientIndices: [0]
                    ),
                ]
            )
        );
        Assert.AreEqual(HttpStatusCode.Created, recipeRes.StatusCode);
        var recipe = await recipeRes.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(recipe);

        // 1. Suggest Substitutions -> should return 503
        var subRes = await client.PostAsJsonAsync(
            "/api/ai-helpers/ingredient-substitutions",
            new SuggestIngredientSubstitutionsRequest(recipe.Id, recipe.Ingredients[0].Id)
        );
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, subRes.StatusCode);

        // 2. Draft Meal Plan -> should return 503
        var draftRes = await client.PostAsJsonAsync(
            "/api/ai-helpers/draft-meal-plan",
            new DraftWeeklyMealPlanRequest(Prompt: "Fast dinners")
        );
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, draftRes.StatusCode);
    }

    [TestMethod]
    public async Task SuggestSubstitutions_AndDraftWeeklyMealPlan_AndApplyDraft_SuccessWorkflow()
    {
        var fakeLlm = new MockLlmExtractionClient { ThrowUnavailable = false };

        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var descriptor = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(ILlmExtractionClient)
                );
                if (descriptor != null)
                {
                    services.Remove(descriptor);
                }
                services.AddSingleton<ILlmExtractionClient>(fakeLlm);
            });
        });

        using var client = factory.CreateClient();
        await AuthenticateAsync(client);

        // 1. Configure settings
        await client.PutAsJsonAsync(
            "/api/llm-settings",
            new UpdateLlmSettingsRequest(
                Provider: LlmProvider.Ollama,
                BaseUrl: "http://localhost:11434",
                ApiKey: null,
                TextModel: "llama3",
                VisionModel: null,
                RequestTimeoutSeconds: 30
            )
        );

        // 2. Create recipe "Test Grounded Recipe"
        var foodRes = await client.PostAsJsonAsync(
            "/api/food-items",
            new CreateFoodItemRequest("Heavy Cream")
        );
        Assert.AreEqual(HttpStatusCode.Created, foodRes.StatusCode);
        var food = await foodRes.Content.ReadFromJsonAsync<FoodItemDto>();
        Assert.IsNotNull(food);

        var recipeRes = await client.PostAsJsonAsync(
            "/api/recipes",
            new UpsertRecipeRequest(
                Title: "Test Grounded Recipe",
                Description: "Creamy pasta",
                BaseServings: 4m,
                Ingredients:
                [
                    new RecipeIngredientInput(
                        SectionName: null,
                        Quantity: 1m,
                        MaxQuantity: null,
                        Unit: MeasurementUnit.Cup,
                        CustomUnitName: null,
                        FoodItemId: food.Id,
                        LinkedRecipeId: null,
                        DisplayName: "Heavy Cream",
                        PreparationNote: null
                    ),
                ],
                Steps:
                [
                    new RecipeStepInput(
                        SectionName: null,
                        Instruction: "Stir in cream",
                        IngredientIndices: [0]
                    ),
                ]
            )
        );
        Assert.AreEqual(HttpStatusCode.Created, recipeRes.StatusCode);
        var recipe = await recipeRes.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(recipe);

        // 3. Test Suggest Substitutions
        var subRes = await client.PostAsJsonAsync(
            "/api/ai-helpers/ingredient-substitutions",
            new SuggestIngredientSubstitutionsRequest(
                RecipeId: recipe.Id,
                RecipeIngredientId: recipe.Ingredients[0].Id,
                TargetServings: 8m
            )
        );
        Assert.AreEqual(HttpStatusCode.OK, subRes.StatusCode);
        var subResult = await subRes.Content.ReadFromJsonAsync<IngredientSubstitutionResponseDto>();
        Assert.IsNotNull(subResult);
        Assert.AreEqual(recipe.Id, subResult.RecipeId);
        Assert.AreEqual("2 cups", subResult.ScaledAmountDisplay);
        Assert.IsTrue(subResult.Substitutions.Count >= 1);
        Assert.AreEqual("Greek Yogurt", subResult.Substitutions[0].Label);

        // 4. Test Draft Weekly Meal Plan (strictly grounded)
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var draftRes = await client.PostAsJsonAsync(
            "/api/ai-helpers/draft-meal-plan",
            new DraftWeeklyMealPlanRequest(
                WeekStartDate: today.AddDays(-3),
                Prompt: "Healthy Dinners",
                MealCount: 1,
                ServingsPerMeal: 2,
                TodayDate: today
            )
        );
        Assert.AreEqual(HttpStatusCode.OK, draftRes.StatusCode);
        var draftResult = await draftRes.Content.ReadFromJsonAsync<DraftMealPlanResponseDto>();
        Assert.IsNotNull(draftResult);
        // "Imaginary Hallucinated Dish" must have been filtered out
        Assert.AreEqual(1, draftResult.Candidates.Count);
        Assert.AreEqual(recipe.Id, draftResult.Candidates[0].RecipeId);
        Assert.AreEqual("Test Grounded Recipe", draftResult.Candidates[0].Recipe.Title);
        Assert.AreEqual(today, draftResult.WeekStartDate);
        Assert.AreEqual(today.AddDays(6), draftResult.WeekEndDate);
        Assert.AreEqual(14m, draftResult.Candidates[0].TargetServings);
        Assert.AreEqual(6, draftResult.Candidates[0].LeftoverDates!.Count);

        var alternativeRes = await client.PostAsJsonAsync(
            "/api/recipes",
            new UpsertRecipeRequest(Title: "Alternative Dinner")
        );
        Assert.AreEqual(HttpStatusCode.Created, alternativeRes.StatusCode);
        var alternative = await alternativeRes.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(alternative);
        fakeLlm.DraftResult = new([
            new LlmDraftMealProposalItem(
                0,
                alternative.Title,
                0,
                "Dinner",
                true,
                4,
                "Another option"
            ),
        ]);
        var retryRes = await client.PostAsJsonAsync(
            "/api/ai-helpers/draft-meal-plan",
            new DraftWeeklyMealPlanRequest(
                Prompt: "Healthy Dinners",
                MealCount: 1,
                PlaceOnDeck: true,
                ExcludedRecipeIds: [recipe.Id]
            )
        );
        Assert.AreEqual(HttpStatusCode.OK, retryRes.StatusCode);
        var replacement = await retryRes.Content.ReadFromJsonAsync<DraftMealPlanResponseDto>();
        Assert.IsNotNull(replacement);
        Assert.AreEqual(alternative.Id, replacement.Candidates[0].RecipeId);
        Assert.AreEqual(0, replacement.Candidates[0].LeftoverDates!.Count);
        var noAlternatives = await client.PostAsJsonAsync(
            "/api/ai-helpers/draft-meal-plan",
            new DraftWeeklyMealPlanRequest(
                Prompt: "Healthy Dinners",
                MealCount: 1,
                PlaceOnDeck: true,
                ExcludedRecipeIds: [recipe.Id, alternative.Id]
            )
        );
        Assert.AreEqual(HttpStatusCode.BadRequest, noAlternatives.StatusCode);

        // 5. Test Apply Draft Meal Plan
        var candidate = draftResult.Candidates[0];
        var draftEntries = new List<CreateMealPlanEntryRequest>
        {
            new(
                ScheduledDate: candidate.ScheduledDate,
                Slot: candidate.Slot,
                RecipeId: candidate.RecipeId,
                TargetServings: candidate.TargetServings
            ),
        };
        draftEntries.AddRange(
            candidate.LeftoverDates!.Select(date => new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.Leftovers,
                ScheduledDate: date,
                Slot: candidate.Slot,
                RecipeId: candidate.RecipeId,
                TargetServings: 2,
                SourceDraftEntryIndex: 0
            ))
        );

        var applyRes = await client.PostAsJsonAsync(
            "/api/meal-plans/week/apply-draft",
            new ApplyDraftMealPlanRequest(WeekStartDate: today, Entries: draftEntries)
        );
        Assert.AreEqual(HttpStatusCode.OK, applyRes.StatusCode);
        var weekResult = await applyRes.Content.ReadFromJsonAsync<MealPlanWeekDto>();
        Assert.IsNotNull(weekResult);
        Assert.IsTrue(
            weekResult.Days.SelectMany(d => d.Entries).Any(e => e.Recipe?.Id == recipe.Id)
        );
        var rangeRes = await client.GetAsync(
            $"/api/meal-plans?startDate={today:yyyy-MM-dd}&endDate={today.AddDays(6):yyyy-MM-dd}"
        );
        var range = await rangeRes.Content.ReadFromJsonAsync<MealPlanRangeResponseDto>();
        Assert.IsNotNull(range);
        Assert.AreEqual(7, range.ScheduledEntries.Count);
        var cookingEntry = range.ScheduledEntries.Single(e => !e.IsLeftovers);
        Assert.IsTrue(
            range
                .ScheduledEntries.Where(e => e.IsLeftovers)
                .All(e => e.SourceMealPlanEntryId == cookingEntry.Id && e.TargetServings == 2)
        );

        var listRes = await client.PostAsJsonAsync(
            "/api/shopping-lists",
            new CreateShoppingListRequest("Draft Groceries")
        );
        var list = await listRes.Content.ReadFromJsonAsync<ShoppingListDetailDto>();
        Assert.IsNotNull(list);
        var previewRes = await client.PostAsJsonAsync(
            $"/api/shopping-lists/{list.Id}/generate/meal-plan/preview",
            new GenerateFromMealPlanRequest(today, today.AddDays(6), IncludeOnDeck: false)
        );
        Assert.AreEqual(HttpStatusCode.OK, previewRes.StatusCode);
        var preview =
            await previewRes.Content.ReadFromJsonAsync<ShoppingListGenerationPreviewDto>();
        Assert.IsNotNull(preview);
        Assert.AreEqual(1, preview.EligibleMealsCount);
        Assert.AreEqual(6, preview.IgnoredLeftoversCount);
        Assert.AreEqual(3.5m, preview.ActiveCandidateItems.Single().Quantity);

        var invalidApplyRes = await client.PostAsJsonAsync(
            "/api/meal-plans/week/apply-draft",
            new ApplyDraftMealPlanRequest(
                today,
                [
                    new(ScheduledDate: today, Slot: MealSlot.Lunch, RecipeId: recipe.Id),
                    new(
                        EntryType: MealPlanEntryType.Leftovers,
                        ScheduledDate: today.AddDays(1),
                        Slot: MealSlot.Lunch,
                        RecipeId: recipe.Id,
                        SourceDraftEntryIndex: 1
                    ),
                ]
            )
        );
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidApplyRes.StatusCode);
    }
}

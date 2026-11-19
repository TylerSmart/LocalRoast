using System.Text.Json;
using System.Text.Json.Nodes;
using App.Core.FoodCatalog;
using App.Core.Ingestion;
using App.Core.Recipes;
using App.Data;
using App.Data.Entities;
using App.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class IngestionRecipeRefinerTests
{
    private AppDbContext _dbContext = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"RefinerUnitTests_{Guid.NewGuid()}")
            .Options;
        _dbContext = new AppDbContext(options);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dbContext.Dispose();
    }

    [TestMethod]
    public async Task RefineDraftAsync_CleansBadFoodCatalogNameAndMatchesExistingFoodItem()
    {
        var sweetPotato = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Sweet Potato",
            NormalizedName = "sweet potato",
            Category = FoodCategory.Produce,
            IsPantryStaple = false,
            CreatedBy = "Test",
            ModifiedBy = "Test",
        };
        _dbContext.FoodItems.Add(sweetPotato);
        await _dbContext.SaveChangesAsync();

        var llm = new StubRefinerLlmClient(
            """
            {
              "title": null,
              "description": null,
              "servings": null,
              "yield": null,
              "timing": null,
              "tags": null,
              "ingredientPatches": [
                {
                  "index": 0,
                  "displayName": "sweet potatoes",
                  "canonicalFoodName": "sweet potato",
                  "category": "Produce",
                  "isPantryStaple": false,
                  "quantity": null,
                  "maxQuantity": null,
                  "unit": null,
                  "prepNote": "peeled and cut into 1/2-inch cubes",
                  "isOptional": null,
                  "isRecipeReference": null,
                  "referencedRecipeTitle": null,
                  "alternateMeasurements": null,
                  "replacementPatches": null
                },
                {
                  "index": 1,
                  "displayName": "smoked paprika",
                  "canonicalFoodName": "smoked paprika",
                  "category": "SpicesAndSeasonings",
                  "isPantryStaple": true,
                  "quantity": null,
                  "maxQuantity": null,
                  "unit": null,
                  "prepNote": null,
                  "isOptional": null,
                  "isRecipeReference": null,
                  "referencedRecipeTitle": null,
                  "alternateMeasurements": null,
                  "replacementPatches": null
                }
              ],
              "stepPatches": [],
              "refinementNotes": ["Cleaned sweet potato and smoked paprika canonical names."]
            }
            """
        );

        var refiner = new IngestionRecipeRefiner(_dbContext, llm);
        var draft = new IngestionExtractedDraftDto(
            Title: "Roasted Sweet Potatoes",
            Description: "Crispy roasted cubes.",
            BaseServings: 4m,
            ExtractedModel: "gemma4:e4b",
            Ingredients:
            [
                new IngestionDraftIngredientDto(
                    Quantity: 2m,
                    Unit: MeasurementUnit.Lb,
                    DisplayName: "1/2-inch cubed peeled sweet potatoes",
                    IsNewFoodItem: true,
                    CreateNewFoodItem: true,
                    ProposedFoodItemName: "1/2-inch cubed peeled sweet potatoes",
                    SuggestedCategory: FoodCategory.Other
                ),
                new IngestionDraftIngredientDto(
                    Quantity: 1m,
                    Unit: MeasurementUnit.Tsp,
                    DisplayName: "smoked paprika, divided",
                    IsNewFoodItem: true,
                    CreateNewFoodItem: true,
                    ProposedFoodItemName: "smoked paprika, divided",
                    SuggestedCategory: FoodCategory.Other
                ),
            ],
            Steps:
            [
                new IngestionDraftStepDto(
                    Instruction: "Roast sweet potatoes with smoked paprika until tender."
                ),
            ]
        );

        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "gemma4:e4b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 120,
            AutoPublishHighConfidenceText: false,
            RefineModel: "gemma4:12b",
            RefineDuringIngestion: true
        );

        var result = await refiner.RefineDraftAsync(
            new IngestionRefineDraftRequest(
                SourceText: "Roasted Sweet Potatoes\nIngredients:\n- 2 lb sweet potatoes, peeled and cut into 1/2-inch cubes\n- 1 tsp smoked paprika\nInstructions:\n1. Roast sweet potatoes with smoked paprika until tender.",
                Draft: draft,
                Config: config
            )
        );

        Assert.IsTrue(result.MadeChanges);
        Assert.AreEqual("gemma4:12b", result.RefinedModel);
        Assert.AreEqual("gemma4:e4b", result.Draft.ExtractedModel);
        Assert.AreEqual("gemma4:12b", result.Draft.RefinedModel);
        Assert.AreEqual("gemma4:12b", llm.LastModelUsed);
        Assert.AreEqual("stage6_refine_recipe", llm.LastStageName);

        var ing0 = result.Draft.Ingredients![0];
        Assert.AreEqual(sweetPotato.Id, ing0.MatchedFoodItemId);
        Assert.AreEqual("Sweet Potato", ing0.MatchedFoodItemName);
        Assert.IsFalse(ing0.IsNewFoodItem);
        Assert.IsFalse(ing0.CreateNewFoodItem);
        Assert.IsNull(ing0.ProposedFoodItemName);
        Assert.AreEqual("peeled and cut into 1/2-inch cubes", ing0.PreparationNote);

        var ing1 = result.Draft.Ingredients![1];
        Assert.IsNull(ing1.MatchedFoodItemId);
        Assert.IsTrue(ing1.IsNewFoodItem);
        Assert.IsTrue(ing1.CreateNewFoodItem);
        Assert.AreEqual("smoked paprika", ing1.ProposedFoodItemName);
        Assert.AreEqual(FoodCategory.SpicesAndSeasonings, ing1.SuggestedCategory);
        Assert.IsTrue(ing1.SuggestedIsPantryStaple);
    }

    [TestMethod]
    public async Task RefineDraftAsync_RejectsCorruptStepPatchThatDropsProse()
    {
        var llm = new StubRefinerLlmClient(
            """
            {
              "title": null,
              "description": null,
              "servings": null,
              "yield": null,
              "timing": null,
              "tags": null,
              "ingredientPatches": [],
              "stepPatches": [
                {
                  "stepNumber": 1,
                  "tokens": ["Bake."]
                }
              ],
              "refinementNotes": ["Truncated step."]
            }
            """
        );

        var refiner = new IngestionRecipeRefiner(_dbContext, llm);
        const string originalStep =
            "Preheat oven to ||{\"type\":\"Temperature\",\"fahrenheit\":400}|| and bake chicken breasts until golden brown and juicy.";
        var draft = new IngestionExtractedDraftDto(
            Title: "Baked Chicken",
            BaseServings: 4m,
            ExtractedModel: "gemma4:e4b",
            Ingredients:
            [
                new IngestionDraftIngredientDto(
                    Quantity: 2m,
                    Unit: MeasurementUnit.Lb,
                    DisplayName: "chicken breasts",
                    IsNewFoodItem: true,
                    ProposedFoodItemName: "chicken breast"
                ),
            ],
            Steps: [new IngestionDraftStepDto(Instruction: originalStep)]
        );

        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "gemma4:e4b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 120,
            AutoPublishHighConfidenceText: false,
            RefineModel: "gemma4:26b-a4b"
        );

        var result = await refiner.RefineDraftAsync(
            new IngestionRefineDraftRequest(
                SourceText: "Baked Chicken\nIngredients:\n- 2 lb chicken breasts\nInstructions:\n1. Preheat oven to 400°F and bake chicken breasts until golden brown and juicy.",
                Draft: draft,
                Config: config
            )
        );

        Assert.AreEqual(originalStep, result.Draft.Steps![0].Instruction);
        Assert.IsFalse(result.MadeChanges);
        Assert.AreEqual("gemma4:26b-a4b", result.RefinedModel);
    }

    [TestMethod]
    public async Task RefineDraftAsync_StepWithNamedTimerToken_ProducesNoSpuriousQualityWarnings()
    {
        var draft = new IngestionExtractedDraftDto(
            Title: "Garlic Oil",
            Description: "Simple garlic oil.",
            BaseServings: 4m,
            YieldDisplay: "4 servings",
            PrepTimeMinutes: 5,
            CookTimeMinutes: 2,
            TotalTimeMinutes: 7,
            Difficulty: RecipeDifficulty.Easy,
            Ingredients:
            [
                new IngestionDraftIngredientDto(
                    Quantity: 2m,
                    Unit: MeasurementUnit.Clove,
                    DisplayName: "garlic",
                    PreparationNote: "minced",
                    IsNewFoodItem: true,
                    ProposedFoodItemName: "Garlic",
                    SuggestedIsPantryStaple: true
                ),
            ],
            Steps:
            [
                new IngestionDraftStepDto(
                    Instruction: """Heat oil and sauté minced garlic for ||{"type":"timer","seconds":120,"label":"Sauté garlic"}||.""",
                    Timers: [new RecipeStepTimerInput(120, "Sauté garlic")]
                ),
            ],
            ConfidenceScore: 0.96m,
            ExtractedModel: "qwen2.5:14b"
        );

        var config = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "qwen2.5:14b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 120,
            AutoPublishHighConfidenceText: true,
            RefineModel: "gemma4:26b-a4b"
        );

        var llmClient = new StubRefinerLlmClient(
            """{"title":null,"description":null,"servings":null,"yield":null,"timing":null,"tags":null,"ingredientPatches":[],"stepPatches":[],"refinementNotes":["No changes needed"]}"""
        );
        var refiner = new IngestionRecipeRefiner(_dbContext, llmClient);

        var result = await refiner.RefineDraftAsync(
            new IngestionRefineDraftRequest(
                SourceText: "Garlic Oil\n2 cloves garlic\nHeat oil and sauté minced garlic for 2 minutes.",
                Draft: draft,
                Config: config
            )
        );

        Assert.IsNull(
            result.Draft.QualityWarnings,
            $"Expected no quality warnings, got: {string.Join("; ", result.Draft.QualityWarnings ?? [])}"
        );
        Assert.IsGreaterThanOrEqualTo(0.90m, result.Draft.ConfidenceScore);
    }

    [TestMethod]
    public void BuildDraftAndFallbackSourceText_ForManualRecipe_PreservesMeasurementsReplacementsAndSections()
    {
        var foodButter = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Unsalted Butter",
            NormalizedName = "unsalted butter",
            Category = FoodCategory.DairyAndEggs,
            IsPantryStaple = false,
        };
        var recipe = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Manual Garlic Toast",
            Slug = "manual-garlic-toast",
            Description = "Crisp buttery garlic toast.",
            Status = RecipeStatus.DraftReview,
            BaseServings = 4m,
            YieldDisplay = "4 slices",
            PrepTimeMinutes = 5,
            CookTimeMinutes = 10,
            TotalTimeMinutes = 15,
            Ingredients =
            [
                new RecipeIngredient
                {
                    Id = Guid.NewGuid(),
                    SortOrder = 0,
                    SectionName = "For the Spread",
                    Quantity = 4m,
                    Unit = MeasurementUnit.Tbsp,
                    FoodItemId = foodButter.Id,
                    FoodItem = foodButter,
                    DisplayName = "unsalted butter",
                    PreparationNote = "softened",
                    AlternativeQuantity = 56m,
                    AlternativeUnit = MeasurementUnit.G,
                    Replacements =
                    [
                        new RecipeIngredientReplacement
                        {
                            Id = Guid.NewGuid(),
                            SortOrder = 0,
                            DisplayName = "olive oil",
                            Quantity = 3m,
                            Unit = MeasurementUnit.Tbsp,
                        },
                    ],
                },
                new RecipeIngredient
                {
                    Id = Guid.NewGuid(),
                    SortOrder = 1,
                    SectionName = "For the Spread",
                    Quantity = 2m,
                    Unit = MeasurementUnit.Clove,
                    DisplayName = "minced garlic, fresh",
                    IsOptional = true,
                },
            ],
            Steps =
            [
                new RecipeStep
                {
                    Id = Guid.NewGuid(),
                    StepNumber = 1,
                    SectionName = "For the Spread",
                    Instruction = "Bake at 400°F for 10 minutes until golden.",
                    Timers =
                    [
                        new RecipeStepTimer
                        {
                            Id = Guid.NewGuid(),
                            SortOrder = 0,
                            DurationSeconds = 600,
                            Label = "Bake",
                        },
                    ],
                },
            ],
        };

        var draft = IngestionProcessor.BuildDraftFromRecipe(recipe);

        Assert.AreEqual("Manual Garlic Toast", draft.Title);
        Assert.IsNull(draft.ExtractedModel);
        Assert.AreEqual(2, draft.Ingredients!.Count);
        Assert.AreEqual(foodButter.Id, draft.Ingredients[0].MatchedFoodItemId);
        Assert.IsFalse(draft.Ingredients[0].IsNewFoodItem);
        Assert.AreEqual(56m, draft.Ingredients[0].AlternativeMeasurement?.Quantity);
        Assert.AreEqual(MeasurementUnit.G, draft.Ingredients[0].AlternativeMeasurement?.Unit);
        Assert.AreEqual(1, draft.Ingredients[0].Replacements?.Count);
        Assert.IsTrue(draft.Ingredients[1].IsNewFoodItem);
        Assert.IsTrue(draft.Ingredients[1].CreateNewFoodItem);
        Assert.AreEqual("minced garlic, fresh", draft.Ingredients[1].ProposedFoodItemName);

        var sourceText = IngestionProcessor.BuildFallbackSourceTextFromDraft(draft);
        StringAssert.Contains(sourceText, "Yield: 4 slices");
        StringAssert.Contains(sourceText, "Prep Time: 5 minutes");
        StringAssert.Contains(sourceText, "For the Spread:");
        StringAssert.Contains(
            sourceText,
            "- 4 tbsp (56 g) unsalted butter, softened or 3 tbsp olive oil"
        );
        StringAssert.Contains(sourceText, "- 2 clove minced garlic, fresh (optional)");
        StringAssert.Contains(sourceText, "1. Bake at 400°F for 10 minutes until golden.");
    }

    private sealed class StubRefinerLlmClient(string responseJson) : ILlmExtractionClient
    {
        public string? LastModelUsed { get; private set; }
        public string? LastStageName { get; private set; }

        public Task<LlmConnectionTestResultDto> ProbeAndListModelsAsync(
            EffectiveLlmConnectionConfig config,
            int timeoutSeconds = 10,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<float[][]> GenerateEmbeddingsAsync(
            EffectiveLlmConnectionConfig config,
            IReadOnlyList<string> inputs,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<LlmStructuredStageResult<T>> ExecuteStructuredStageAsync<T>(
            EffectiveLlmConnectionConfig config,
            string stageName,
            IReadOnlyList<LlmStageChatMessage> messages,
            JsonNode? jsonSchema,
            bool? overrideThinking = null,
            int maxTokens = 16384,
            CancellationToken cancellationToken = default
        )
        {
            LastModelUsed = config.TextModel;
            LastStageName = stageName;
            var parsed = JsonSerializer.Deserialize<T>(
                responseJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );
            return Task.FromResult(
                new LlmStructuredStageResult<T>(parsed, responseJson, config.TextModel ?? "", 5)
            );
        }

        public Task<string?> TranscribeRecipeImagesAsync(
            EffectiveLlmConnectionConfig config,
            LlmVisionExtractionContext context,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<LlmSubstitutionPayload> SuggestIngredientSubstitutionsAsync(
            EffectiveLlmConnectionConfig config,
            LlmSubstitutionContext context,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<string> AnswerKitchenQuestionAsync(
            EffectiveLlmConnectionConfig config,
            LlmKitchenQuestionContext context,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<LlmDraftMealPlanPayload> DraftWeeklyMealPlanAsync(
            EffectiveLlmConnectionConfig config,
            LlmDraftMealPlanContext context,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();
    }
}

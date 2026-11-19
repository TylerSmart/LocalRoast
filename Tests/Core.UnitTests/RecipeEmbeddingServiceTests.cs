using App.Core.Exceptions;
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
public sealed class RecipeEmbeddingServiceTests
{
    private sealed class FakeEmbeddingLlmClient : ILlmExtractionClient
    {
        public Func<IReadOnlyList<string>, float[][]>? EmbeddingFactory { get; set; }
        public int EmbeddingCallCount { get; private set; }
        public List<IReadOnlyList<string>> RecordedInputs { get; } = [];
        public bool ThrowOnEmbeddings { get; set; }

        public Task<float[][]> GenerateEmbeddingsAsync(
            EffectiveLlmConnectionConfig config,
            IReadOnlyList<string> inputs,
            CancellationToken cancellationToken = default
        )
        {
            EmbeddingCallCount++;
            RecordedInputs.Add(inputs.ToList());

            if (ThrowOnEmbeddings)
            {
                throw new LlmHostUnavailableException("Embedding host offline");
            }

            if (EmbeddingFactory is not null)
            {
                return Task.FromResult(EmbeddingFactory(inputs));
            }

            return Task.FromResult(inputs.Select(BuildSemanticVector).ToArray());
        }

        public Task<LlmConnectionTestResultDto> ProbeAndListModelsAsync(
            EffectiveLlmConnectionConfig config,
            int timeoutSeconds = 15,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<LlmStructuredStageResult<T>> ExecuteStructuredStageAsync<T>(
            EffectiveLlmConnectionConfig config,
            string stageName,
            IReadOnlyList<LlmStageChatMessage> messages,
            global::System.Text.Json.Nodes.JsonNode? jsonSchema,
            bool? overrideThinking = null,
            int maxTokens = 16384,
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

    private AppDbContext _dbContext = null!;
    private FakeEmbeddingLlmClient _llmClient = null!;
    private RecipeEmbeddingService _service = null!;
    private EffectiveLlmConnectionConfig _configWithEmbedding = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new AppDbContext(options);
        _llmClient = new FakeEmbeddingLlmClient();
        _service = new RecipeEmbeddingService(_dbContext, _llmClient);

        _configWithEmbedding = new EffectiveLlmConnectionConfig(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "qwen2.5:14b",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 60,
            AutoPublishHighConfidenceText: false,
            EmbeddingModel: "nomic-embed-text"
        );
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dbContext.Dispose();
    }

    [TestMethod]
    public void RecipeEmbeddingTextBuilder_BuildsDeterministicCard_ExcludesPantryStaples_AndChangesHashOnEdits()
    {
        var doc1 = new RecipeEmbeddingDocument(
            Title: "  Tuscan   Garlic Chicken  ",
            Description: "Creamy skillet chicken with baby spinach.",
            TotalTimeMinutes: 30,
            Difficulty: RecipeDifficulty.Easy,
            Tags: ["Italian", "Quick", "italian"],
            Collections: ["Weeknight Staples"],
            Ingredients:
            [
                new("Kosher Salt", "kosher salt", IsPantryStaple: true, IsOptional: false),
                new("Olive Oil", "extra-virgin olive oil", IsPantryStaple: true, IsOptional: false),
                new(
                    "Chicken Breast",
                    "boneless chicken breasts",
                    IsPantryStaple: false,
                    IsOptional: false
                ),
                new("Baby Spinach", "fresh baby spinach", IsPantryStaple: false, IsOptional: false),
                new(
                    null,
                    "1 batch Pickled Shallots",
                    IsPantryStaple: false,
                    IsOptional: true,
                    LinkedRecipeTitle: "Pickled Shallots"
                ),
            ]
        );

        var built1 = RecipeEmbeddingTextBuilder.Build(doc1);
        var built2 = RecipeEmbeddingTextBuilder.Build(doc1 with { Tags = ["Quick", "Italian"] });

        Assert.AreEqual(built1.EmbeddingText, built2.EmbeddingText);
        Assert.AreEqual(built1.ContentHash, built2.ContentHash);
        Assert.AreEqual(64, built1.ContentHash.Length);
        StringAssert.Contains(built1.EmbeddingText, "Title: Tuscan Garlic Chicken");
        StringAssert.Contains(built1.EmbeddingText, "30 min total (quick weeknight)");
        StringAssert.Contains(
            built1.EmbeddingText,
            "Chicken Breast, Baby Spinach, Pickled Shallots"
        );
        Assert.IsFalse(
            built1.EmbeddingText.Contains("Kosher Salt", StringComparison.OrdinalIgnoreCase)
        );
        Assert.IsFalse(
            built1.EmbeddingText.Contains("Olive Oil", StringComparison.OrdinalIgnoreCase)
        );

        var edited = RecipeEmbeddingTextBuilder.Build(doc1 with { Title = "Spicy Tuscan Chicken" });
        Assert.AreNotEqual(built1.ContentHash, edited.ContentHash);
    }

    [TestMethod]
    public async Task UpsertRecipeEmbeddingsAsync_SkipsUnchangedHash_UpdatesOnEdit_AndPrunesArchived()
    {
        var recipe = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Butternut Squash Soup",
            Slug = "butternut-squash-soup",
            Description = "Velvety autumn soup.",
            Status = RecipeStatus.Published,
            TotalTimeMinutes = 40,
        };
        _dbContext.Recipes.Add(recipe);
        await _dbContext.SaveChangesAsync();

        var embConfig = _configWithEmbedding.ForEmbedding()!;

        // 1. Initial upsert embeds once
        var count1 = await _service.UpsertRecipeEmbeddingsAsync(embConfig, [recipe.Id]);
        Assert.AreEqual(1, count1);
        Assert.AreEqual(1, _llmClient.EmbeddingCallCount);

        var stored1 = await _dbContext.RecipeEmbeddings.AsNoTracking().SingleAsync();
        Assert.AreEqual("nomic-embed-text", stored1.ModelTag);
        Assert.AreEqual(4, stored1.Dimensions);
        var initialHash = stored1.ContentHash;

        // 2. Unchanged upsert skips embedding call
        var count2 = await _service.UpsertRecipeEmbeddingsAsync(embConfig, [recipe.Id]);
        Assert.AreEqual(0, count2);
        Assert.AreEqual(1, _llmClient.EmbeddingCallCount);

        // 3. Edit title -> ContentHash changes -> re-embeds & updates in place
        var tracked = await _dbContext.Recipes.SingleAsync(r => r.Id == recipe.Id);
        tracked.Title = "Roasted Butternut Squash & Sage Soup";
        await _dbContext.SaveChangesAsync();

        var count3 = await _service.UpsertRecipeEmbeddingsAsync(embConfig, [recipe.Id]);
        Assert.AreEqual(1, count3);
        Assert.AreEqual(2, _llmClient.EmbeddingCallCount);

        var stored2 = await _dbContext.RecipeEmbeddings.AsNoTracking().SingleAsync();
        Assert.AreNotEqual(initialHash, stored2.ContentHash);
        StringAssert.Contains(stored2.EmbeddingText, "Roasted Butternut Squash & Sage Soup");

        // 4. Archive recipe -> prunes RecipeEmbedding row
        tracked.Status = RecipeStatus.Archived;
        await _dbContext.SaveChangesAsync();

        var count4 = await _service.UpsertRecipeEmbeddingsAsync(embConfig, [recipe.Id]);
        Assert.AreEqual(0, count4);
        Assert.AreEqual(0, await _dbContext.RecipeEmbeddings.CountAsync());
    }

    [TestMethod]
    public async Task SyncPendingEmbeddingsAsync_BatchesPublishedOnly_AndIsolatesByModelTag()
    {
        var pub1 = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Chicken Tacos",
            Slug = "chicken-tacos",
            Status = RecipeStatus.Published,
        };
        var pub2 = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Mushroom Risotto",
            Slug = "mushroom-risotto",
            Status = RecipeStatus.Published,
        };
        var draft = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Unreviewed Draft",
            Slug = "unreviewed-draft",
            Status = RecipeStatus.DraftReview,
        };
        _dbContext.Recipes.AddRange(pub1, pub2, draft);
        await _dbContext.SaveChangesAsync();

        var embedded = await _service.SyncPendingEmbeddingsAsync(_configWithEmbedding);
        Assert.AreEqual(2, embedded);
        Assert.AreEqual(2, await _dbContext.RecipeEmbeddings.CountAsync());

        // Switching embedding model creates fresh rows for the new model tag
        var altModelConfig = _configWithEmbedding with
        {
            EmbeddingModel = "embeddinggemma",
        };
        var embeddedAlt = await _service.SyncPendingEmbeddingsAsync(altModelConfig);
        Assert.AreEqual(2, embeddedAlt);
        Assert.AreEqual(4, await _dbContext.RecipeEmbeddings.CountAsync());
        Assert.AreEqual(
            2,
            await _dbContext.RecipeEmbeddings.CountAsync(e => e.ModelTag == "embeddinggemma")
        );
    }

    [TestMethod]
    public async Task RetrieveMealPlanCandidatesAsync_EnforcesHardExclusions_AcrossFoodItemsAliasesSubRecipesAndTags()
    {
        var shrimp = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Shrimp",
            NormalizedName = "shrimp",
            Category = FoodCategory.MeatAndSeafood,
        };
        shrimp.Aliases.Add(new FoodItemAlias { Alias = "Prawns", NormalizedAlias = "prawns" });

        var eggplant = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Eggplant",
            NormalizedName = "eggplant",
            Category = FoodCategory.Produce,
        };
        var egg = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Egg",
            NormalizedName = "egg",
            Category = FoodCategory.DairyAndEggs,
        };
        var peanutSauce = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Thai Peanut Sauce",
            Slug = "thai-peanut-sauce",
            Status = RecipeStatus.Published,
            Ingredients = [new RecipeIngredient { DisplayName = "peanut butter", SortOrder = 0 }],
        };

        var shrimpPasta = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Garlic Scampi Pasta",
            Slug = "garlic-scampi-pasta",
            Status = RecipeStatus.Published,
            Ingredients =
            [
                new RecipeIngredient
                {
                    DisplayName = "large shrimp",
                    FoodItem = shrimp,
                    FoodItemId = shrimp.Id,
                },
            ],
        };
        var peanutNoodles = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Cold Sesame Noodles",
            Slug = "cold-sesame-noodles",
            Status = RecipeStatus.Published,
            Ingredients =
            [
                new RecipeIngredient
                {
                    DisplayName = "Thai Peanut Sauce",
                    LinkedRecipe = peanutSauce,
                    LinkedRecipeId = peanutSauce.Id,
                },
            ],
        };
        var roastedEggplant = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Roasted Eggplant Parmesan",
            Slug = "roasted-eggplant-parmesan",
            Status = RecipeStatus.Published,
            Ingredients =
            [
                new RecipeIngredient
                {
                    DisplayName = "eggplant",
                    FoodItem = eggplant,
                    FoodItemId = eggplant.Id,
                },
            ],
        };
        var omelette = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Cheese Omelette",
            Slug = "cheese-omelette",
            Status = RecipeStatus.Published,
            Ingredients =
            [
                new RecipeIngredient
                {
                    DisplayName = "large eggs",
                    FoodItem = egg,
                    FoodItemId = egg.Id,
                },
            ],
        };

        _dbContext.FoodItems.AddRange(shrimp, eggplant, egg);
        _dbContext.Recipes.AddRange(
            peanutSauce,
            shrimpPasta,
            peanutNoodles,
            roastedEggplant,
            omelette
        );
        await _dbContext.SaveChangesAsync();

        var result = await _service.RetrieveMealPlanCandidatesAsync(
            _configWithEmbedding with
            {
                EmbeddingModel = null,
            },
            new MealPlanRetrievalRequest(
                RawUserPrompt: "Dinners with no seafood, no peanuts, and no eggs",
                RequestedMealCount: 2,
                ExcludedRecipeIds: [],
                RecentlyScheduledRecipeIds: [],
                ExcludedTerms: ["seafood", "peanut", "egg"],
                PrioritizeIngredients: [],
                GlobalMaxTotalTimeMinutes: null,
                SearchBuckets:
                [
                    new MealPlanSearchBucketInput("Dinners", "roasted dinner", [], null, 2),
                ],
                UtcNow: DateTimeOffset.UtcNow
            )
        );

        Assert.AreEqual(5, result.TotalEligiblePublishedCount);
        Assert.AreEqual(1, result.PostExclusionEligibleCount);
        Assert.AreEqual(1, result.Candidates.Count);
        // Eggplant must NOT be falsely excluded by "egg"!
        Assert.AreEqual(roastedEggplant.Id, result.Candidates[0].RecipeId);
    }

    [TestMethod]
    public async Task RetrieveMealPlanCandidatesAsync_UsesEmbeddingsToSurfaceUnratedRecipeBeyondTop150()
    {
        // Seed 170 highly rated generic recipes + 1 unrated cozy autumn squash soup (would be #171)
        var recipes = new List<Recipe>(171);
        for (var i = 1; i <= 170; i++)
        {
            recipes.Add(
                new Recipe
                {
                    Id = Guid.NewGuid(),
                    Title = $"Classic Grilled Burger #{i}",
                    Slug = $"classic-grilled-burger-{i}",
                    Status = RecipeStatus.Published,
                    AverageRating = 5.0m,
                    TimesCooked = 10,
                    TotalTimeMinutes = 25,
                }
            );
        }

        var target = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Velvety Autumn Harvest Bisque",
            Slug = "velvety-autumn-harvest-bisque",
            Description = "Cozy roasted butternut squash and sage bowl.",
            Status = RecipeStatus.Published,
            AverageRating = null,
            TimesCooked = 0,
            TotalTimeMinutes = 35,
        };
        recipes.Add(target);

        _dbContext.Recipes.AddRange(recipes);
        await _dbContext.SaveChangesAsync();

        // Pre-index all 171 recipes via SyncPendingEmbeddingsAsync (like the background worker does)
        while (
            await _service.SyncPendingEmbeddingsAsync(_configWithEmbedding, maxBatchCount: 64) > 0
        ) { }

        Assert.AreEqual(171, await _dbContext.RecipeEmbeddings.CountAsync());

        // Map any "cozy"/"autumn"/"squash"/"bisque"/"soup" text to axis 0, and "burger" to axis 1
        var result = await _service.RetrieveMealPlanCandidatesAsync(
            _configWithEmbedding,
            new MealPlanRetrievalRequest(
                RawUserPrompt: "Cozy fall soup for dinner",
                RequestedMealCount: 3,
                ExcludedRecipeIds: [],
                RecentlyScheduledRecipeIds: [],
                ExcludedTerms: [],
                PrioritizeIngredients: [],
                GlobalMaxTotalTimeMinutes: null,
                SearchBuckets:
                [
                    new MealPlanSearchBucketInput(
                        "Cozy fall soup",
                        "cozy autumn squash soup",
                        [],
                        null,
                        3
                    ),
                ],
                UtcNow: DateTimeOffset.UtcNow
            )
        );

        Assert.IsTrue(result.UsedEmbeddings);
        Assert.AreEqual(171, result.TotalEligiblePublishedCount);
        Assert.IsTrue(result.Candidates.Count <= 40);
        Assert.AreEqual(target.Id, result.Candidates[0].RecipeId);
    }

    [TestMethod]
    public async Task RetrieveMealPlanCandidatesAsync_AppliesPrioritizedIngredientsAndPerishableSynergyBoosts()
    {
        var spinach = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Spinach",
            NormalizedName = "spinach",
            Category = FoodCategory.Produce,
        };
        var heavyCream = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Heavy Cream",
            NormalizedName = "heavy cream",
            Category = FoodCategory.DairyAndEggs,
        };
        var beef = new FoodItem
        {
            Id = Guid.NewGuid(),
            Name = "Ground Beef",
            NormalizedName = "ground beef",
            Category = FoodCategory.MeatAndSeafood,
        };

        var tuscanChicken = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Tuscan Garlic Chicken",
            Slug = "tuscan-garlic-chicken",
            Status = RecipeStatus.Published,
            Ingredients =
            [
                new RecipeIngredient
                {
                    DisplayName = "spinach",
                    FoodItem = spinach,
                    FoodItemId = spinach.Id,
                },
                new RecipeIngredient
                {
                    DisplayName = "heavy cream",
                    FoodItem = heavyCream,
                    FoodItemId = heavyCream.Id,
                },
            ],
        };
        var creamyPasta = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Creamy Florentine Pasta",
            Slug = "creamy-florentine-pasta",
            Status = RecipeStatus.Published,
            Ingredients =
            [
                new RecipeIngredient
                {
                    DisplayName = "baby spinach",
                    FoodItem = spinach,
                    FoodItemId = spinach.Id,
                },
                new RecipeIngredient
                {
                    DisplayName = "heavy cream",
                    FoodItem = heavyCream,
                    FoodItemId = heavyCream.Id,
                },
            ],
        };
        var plainBurger = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Plain Beef Burger",
            Slug = "plain-beef-burger",
            Status = RecipeStatus.Published,
            Ingredients =
            [
                new RecipeIngredient
                {
                    DisplayName = "ground beef",
                    FoodItem = beef,
                    FoodItemId = beef.Id,
                },
            ],
        };

        _dbContext.FoodItems.AddRange(spinach, heavyCream, beef);
        _dbContext.Recipes.AddRange(tuscanChicken, creamyPasta, plainBurger);
        await _dbContext.SaveChangesAsync();

        var result = await _service.RetrieveMealPlanCandidatesAsync(
            _configWithEmbedding with
            {
                EmbeddingModel = null,
            },
            new MealPlanRetrievalRequest(
                RawUserPrompt: "Use up my spinach this week",
                RequestedMealCount: 2,
                ExcludedRecipeIds: [],
                RecentlyScheduledRecipeIds: [],
                ExcludedTerms: [],
                PrioritizeIngredients: ["spinach"],
                GlobalMaxTotalTimeMinutes: null,
                SearchBuckets:
                [
                    new MealPlanSearchBucketInput(
                        "Weeknight dinners",
                        "weeknight dinner",
                        [],
                        null,
                        2
                    ),
                ],
                UtcNow: DateTimeOffset.UtcNow
            )
        );

        var topTwo = result.Candidates.Take(2).Select(c => c.RecipeId).ToHashSet();
        Assert.IsTrue(topTwo.Contains(tuscanChicken.Id));
        Assert.IsTrue(topTwo.Contains(creamyPasta.Id));
    }

    [TestMethod]
    public async Task RetrieveMealPlanCandidatesAsync_FallsBackToLexical_WhenEmbeddingsFail_AndFlagsUnmatchedBuckets()
    {
        _llmClient.ThrowOnEmbeddings = true;

        var pancakes = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Buttermilk Pancakes",
            Slug = "buttermilk-pancakes",
            Status = RecipeStatus.Published,
        };
        _dbContext.Recipes.Add(pancakes);
        await _dbContext.SaveChangesAsync();

        var result = await _service.RetrieveMealPlanCandidatesAsync(
            _configWithEmbedding,
            new MealPlanRetrievalRequest(
                RawUserPrompt: "Authentic Moroccan tagine",
                RequestedMealCount: 1,
                ExcludedRecipeIds: [],
                RecentlyScheduledRecipeIds: [],
                ExcludedTerms: [],
                PrioritizeIngredients: [],
                GlobalMaxTotalTimeMinutes: null,
                SearchBuckets:
                [
                    new MealPlanSearchBucketInput(
                        "Moroccan tagine",
                        "authentic moroccan lamb tagine",
                        ["Moroccan"],
                        null,
                        1
                    ),
                ],
                UtcNow: DateTimeOffset.UtcNow
            )
        );

        Assert.IsFalse(result.UsedEmbeddings);
        Assert.AreEqual(1, result.Candidates.Count);
        Assert.AreEqual(1, result.UnmatchedBuckets.Count);
        Assert.AreEqual("Moroccan tagine", result.UnmatchedBuckets[0]);
    }

    private static float[] BuildSemanticVector(string text)
    {
        var lower = text.ToLowerInvariant();
        var v = new float[4];
        if (
            lower.Contains("autumn")
            || lower.Contains("fall")
            || lower.Contains("squash")
            || lower.Contains("bisque")
            || lower.Contains("soup")
            || lower.Contains("cozy")
        )
        {
            v[0] = 1f;
        }
        else if (lower.Contains("burger") || lower.Contains("grill"))
        {
            v[1] = 1f;
        }
        else if (lower.Contains("taco") || lower.Contains("mexican") || lower.Contains("salsa"))
        {
            v[2] = 1f;
        }
        else
        {
            v[3] = 1f;
        }

        var norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm > 1e-6f)
        {
            for (var i = 0; i < v.Length; i++)
            {
                v[i] /= norm;
            }
        }
        return v;
    }
}

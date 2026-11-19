using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using App.Core.Authentication;
using App.Core.FoodCatalog;
using App.Core.Ingestion;
using App.Core.Recipes;
using App.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.API.IntegrationTests;

[TestClass]
public sealed class MultiStageIngestionAndReplacementsIntegrationTests
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
        var email = $"ms_{Guid.NewGuid():N}@example.com";
        var username = $"ms_{Guid.NewGuid():N}"[..12];
        var register = new RegisterRequest(email, username, "KitchenSecret123!");
        var res = await client.PostAsJsonAsync("/api/auth/register", register);
        Assert.AreEqual(HttpStatusCode.OK, res.StatusCode);

        var auth = await res.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.IsNotNull(auth);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            auth.Token
        );
        return auth;
    }

    [TestMethod]
    public async Task VectorPipeline_PopulatesCatalogEmbeddings_AndHandlesStage3cStage4bAndStage5bFallbacks()
    {
        var mockLlm = new FallbackExercisingLlmClient();

        await using var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Llm:TextModel", "qwen2.5:14b");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILlmExtractionClient>();
                services.AddSingleton<ILlmExtractionClient>(mockLlm);
            });
        });

        using var client = customFactory.CreateClient();
        await AuthenticateAsync(client);

        // Create connection + embedding & text model configurations and assign workflows
        var connRes = await client.PostAsJsonAsync(
            "/api/llm-settings/connections",
            new UpsertLlmConnectionRequest(
                Name: "Local GPU",
                Provider: LlmProvider.Ollama,
                BaseUrl: "http://localhost:11434"
            )
        );
        Assert.AreEqual(HttpStatusCode.Created, connRes.StatusCode);
        var conn = await connRes.Content.ReadFromJsonAsync<LlmConnectionDto>();
        Assert.IsNotNull(conn);

        var textCfgRes = await client.PostAsJsonAsync(
            "/api/llm-settings/model-configurations",
            new UpsertLlmModelConfigurationRequest("Qwen Text", conn.Id, "qwen2.5:14b")
        );
        Assert.AreEqual(HttpStatusCode.Created, textCfgRes.StatusCode);
        var textCfg = await textCfgRes.Content.ReadFromJsonAsync<LlmModelConfigurationDto>();
        Assert.IsNotNull(textCfg);

        var embedCfgRes = await client.PostAsJsonAsync(
            "/api/llm-settings/model-configurations",
            new UpsertLlmModelConfigurationRequest("EmbeddingGemma", conn.Id, "embeddinggemma")
        );
        Assert.AreEqual(HttpStatusCode.Created, embedCfgRes.StatusCode);
        var embedCfg = await embedCfgRes.Content.ReadFromJsonAsync<LlmModelConfigurationDto>();
        Assert.IsNotNull(embedCfg);

        var wfRes = await client.PutAsJsonAsync(
            "/api/llm-settings/workflows",
            new UpdateLlmWorkflowsRequest(
                TextModelProfileId: textCfg.DefaultProfileId,
                EmbeddingModelProfileId: embedCfg.DefaultProfileId
            )
        );
        Assert.AreEqual(HttpStatusCode.OK, wfRes.StatusCode);
        var wfSettings = await wfRes.Content.ReadFromJsonAsync<LlmSettingsDto>();
        Assert.IsNotNull(wfSettings);
        Assert.AreEqual(embedCfg.DefaultProfileId, wfSettings.EmbeddingModelProfileId);
        Assert.AreEqual("embeddinggemma", wfSettings.EmbeddingModel);

        // Seed existing catalog food item
        var butterRes = await client.PostAsJsonAsync(
            "/api/food-items",
            new CreateFoodItemRequest(
                "Unsalted Butter",
                IsPantryStaple: true,
                Category: FoodCategory.DairyAndEggs
            )
        );
        Assert.AreEqual(HttpStatusCode.Created, butterRes.StatusCode);

        var enqueueRes = await client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(
                "Toasted Butter Bread\nIngredients:\n- 2 tbsp butter (or 2 tbsp margarine)\nInstructions:\n1. Melt 2 tablespoons butter.",
                AutoPublish: true
            )
        );
        Assert.AreEqual(HttpStatusCode.Accepted, enqueueRes.StatusCode);
        var job = await enqueueRes.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(job);

        using (var scope = customFactory.Services.CreateScope())
        {
            var processed = await scope
                .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                .ProcessNextQueuedJobAsync();
            Assert.IsTrue(processed);
        }

        Assert.IsGreaterThanOrEqualTo(1, mockLlm.EmbeddingCallCount);
        Assert.IsTrue(mockLlm.StageCalls.Contains("stage3c_match_batch"));
        Assert.IsTrue(mockLlm.StageCalls.Contains("stage3b_match_ingredient"));
        Assert.IsTrue(mockLlm.StageCalls.Contains("stage4b_tokenize_batch"));
        Assert.IsTrue(mockLlm.StageCalls.Contains("stage4_tokenize_step"));
        Assert.IsTrue(mockLlm.StageCalls.Contains("stage5b_polish_targeted"));

        using (var scope = customFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cachedEmbeddings = await db.CatalogEmbeddings.ToListAsync();
            Assert.IsNotEmpty(cachedEmbeddings);
            var recipeEmbeddings = await db.RecipeEmbeddings.ToListAsync();
            Assert.HasCount(1, recipeEmbeddings);
            Assert.AreEqual("embeddinggemma", recipeEmbeddings[0].ModelTag);
            StringAssert.Contains(recipeEmbeddings[0].EmbeddingText, "Toasted Butter Bread");
        }

        var jobDetail = await client.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{job.Id}"
        );
        Assert.IsNotNull(jobDetail);
        Assert.AreEqual(IngestionJobStatus.Published, jobDetail.Status, jobDetail.StatusDetail);
        Assert.IsNotNull(jobDetail.ResultingRecipeId);

        var published = await client.GetFromJsonAsync<RecipeDetailDto>(
            $"/api/recipes/{jobDetail.ResultingRecipeId}"
        );
        Assert.IsNotNull(published);
        Assert.AreEqual(12, published.TotalTimeMinutes);
        Assert.IsNotNull(published.Ingredients[0].Replacements);
        Assert.HasCount(1, published.Ingredients[0].Replacements!);
        Assert.AreEqual("margarine", published.Ingredients[0].Replacements![0].DisplayName);
        Assert.IsNotNull(published.Ingredients[0].Replacements![0].FoodItemId);
    }

    [TestMethod]
    public async Task LexicalFallbackPipeline_WhenEmbeddingUnset_UsesStage3aWithoutGenerateEmbeddings()
    {
        var mockLlm = new FallbackExercisingLlmClient { ForceSimpleBatch = true };

        await using var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Llm:TextModel", "qwen2.5:14b");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILlmExtractionClient>();
                services.AddSingleton<ILlmExtractionClient>(mockLlm);
            });
        });

        using var client = customFactory.CreateClient();
        await AuthenticateAsync(client);

        await client.PostAsJsonAsync(
            "/api/food-items",
            new CreateFoodItemRequest(
                "Unsalted Butter",
                IsPantryStaple: true,
                Category: FoodCategory.DairyAndEggs
            )
        );

        var enqueueRes = await client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(
                "Toasted Butter Bread\nIngredients:\n- 2 tbsp butter (or 2 tbsp margarine)\nInstructions:\n1. Melt 2 tablespoons butter.",
                AutoPublish: true
            )
        );
        enqueueRes.EnsureSuccessStatusCode();

        using (var scope = customFactory.Services.CreateScope())
        {
            Assert.IsTrue(
                await scope
                    .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                    .ProcessNextQueuedJobAsync()
            );
        }

        Assert.AreEqual(0, mockLlm.EmbeddingCallCount);
        Assert.IsTrue(mockLlm.StageCalls.Contains("stage3a_ingredient_search_terms"));
    }

    [TestMethod]
    public async Task ManualCreateEditAndArchive_SyncsAndPrunesRecipeEmbeddings_ViaProcessor()
    {
        var mockLlm = new FallbackExercisingLlmClient();

        await using var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Llm:TextModel", "qwen2.5:14b");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILlmExtractionClient>();
                services.AddSingleton<ILlmExtractionClient>(mockLlm);
            });
        });

        using var client = customFactory.CreateClient();
        await AuthenticateAsync(client);

        var connRes = await client.PostAsJsonAsync(
            "/api/llm-settings/connections",
            new UpsertLlmConnectionRequest(
                Name: "Local GPU",
                Provider: LlmProvider.Ollama,
                BaseUrl: "http://localhost:11434"
            )
        );
        connRes.EnsureSuccessStatusCode();
        var conn = (await connRes.Content.ReadFromJsonAsync<LlmConnectionDto>())!;

        var textCfg = (
            await (
                await client.PostAsJsonAsync(
                    "/api/llm-settings/model-configurations",
                    new UpsertLlmModelConfigurationRequest("Qwen Text", conn.Id, "qwen2.5:14b")
                )
            ).Content.ReadFromJsonAsync<LlmModelConfigurationDto>()
        )!;
        var embedCfg = (
            await (
                await client.PostAsJsonAsync(
                    "/api/llm-settings/model-configurations",
                    new UpsertLlmModelConfigurationRequest("EmbedModel", conn.Id, "embeddinggemma")
                )
            ).Content.ReadFromJsonAsync<LlmModelConfigurationDto>()
        )!;

        var wfRes = await client.PutAsJsonAsync(
            "/api/llm-settings/workflows",
            new UpdateLlmWorkflowsRequest(
                TextModelProfileId: textCfg.DefaultProfileId,
                EmbeddingModelProfileId: embedCfg.DefaultProfileId,
                EnableIngestion: true
            )
        );
        wfRes.EnsureSuccessStatusCode();

        // 1. Manually create published recipe
        var createRes = await client.PostAsJsonAsync(
            "/api/recipes",
            new UpsertRecipeRequest(
                Title: "Weeknight Chickpea Curry",
                Description: "Quick spiced coconut curry.",
                Status: RecipeStatus.Published,
                BaseServings: 4m,
                TotalTimeMinutes: 30,
                Ingredients: [new RecipeIngredientInput(DisplayName: "chickpeas")],
                Steps: [new RecipeStepInput(Instruction: "Simmer chickpeas in coconut milk.")]
            )
        );
        Assert.AreEqual(HttpStatusCode.Created, createRes.StatusCode);
        var created = (await createRes.Content.ReadFromJsonAsync<RecipeDetailDto>())!;

        using (var scope = customFactory.Services.CreateScope())
        {
            var embeddedCount = await scope
                .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                .SyncPendingRecipeEmbeddingsAsync();
            Assert.AreEqual(1, embeddedCount);
        }

        string initialHash;
        using (var scope = customFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.RecipeEmbeddings.SingleAsync(e => e.RecipeId == created.Id);
            initialHash = row.ContentHash;
            StringAssert.Contains(row.EmbeddingText, "Weeknight Chickpea Curry");
        }

        // 2. Second sync with no edits is a no-op
        using (var scope = customFactory.Services.CreateScope())
        {
            var embeddedAgain = await scope
                .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                .SyncPendingRecipeEmbeddingsAsync();
            Assert.AreEqual(0, embeddedAgain);
        }

        // 3. Edit recipe -> hash drifts -> next sync updates embedding in place
        var editRes = await client.PutAsJsonAsync(
            $"/api/recipes/{created.Id}",
            new UpsertRecipeRequest(
                Title: "Smoky Lentil & Chickpea Curry",
                Description: "Quick spiced coconut curry with lentils.",
                Status: RecipeStatus.Published,
                BaseServings: 4m,
                TotalTimeMinutes: 30,
                Ingredients: [new RecipeIngredientInput(DisplayName: "red lentils")],
                Steps: [new RecipeStepInput(Instruction: "Simmer lentils.")]
            )
        );
        Assert.AreEqual(HttpStatusCode.OK, editRes.StatusCode);

        using (var scope = customFactory.Services.CreateScope())
        {
            var updatedCount = await scope
                .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                .SyncPendingRecipeEmbeddingsAsync();
            Assert.AreEqual(1, updatedCount);
        }

        using (var scope = customFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.RecipeEmbeddings.SingleAsync(e => e.RecipeId == created.Id);
            Assert.AreNotEqual(initialHash, row.ContentHash);
            StringAssert.Contains(row.EmbeddingText, "Smoky Lentil & Chickpea Curry");
        }

        // 4. Archive recipe -> RecipeEmbedding is pruned immediately
        var archiveRes = await client.PutAsJsonAsync(
            $"/api/recipes/{created.Id}",
            new UpsertRecipeRequest(
                Title: "Smoky Lentil & Chickpea Curry",
                Status: RecipeStatus.Archived,
                BaseServings: 4m
            )
        );
        Assert.AreEqual(HttpStatusCode.OK, archiveRes.StatusCode);

        using (var scope = customFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.AreEqual(0, await db.RecipeEmbeddings.CountAsync(e => e.RecipeId == created.Id));
        }
    }

    [TestMethod]
    public async Task RecipeIngredientReplacements_CrudShareLinkAndFoodCatalogMergeUnlinkReset_WorkEndToEnd()
    {
        await AuthenticateAsync(_client);

        // Seed 2 food items for merge & unlink tests
        var sourceRes = await _client.PostAsJsonAsync(
            "/api/food-items",
            new CreateFoodItemRequest("Ground Turkey", Category: FoodCategory.MeatAndSeafood)
        );
        Assert.AreEqual(HttpStatusCode.Created, sourceRes.StatusCode);
        var sourceFood = await sourceRes.Content.ReadFromJsonAsync<FoodItemDto>();

        var targetRes = await _client.PostAsJsonAsync(
            "/api/food-items",
            new CreateFoodItemRequest("Lean Ground Turkey", Category: FoodCategory.MeatAndSeafood)
        );
        Assert.AreEqual(HttpStatusCode.Created, targetRes.StatusCode);
        var targetFood = await targetRes.Content.ReadFromJsonAsync<FoodItemDto>();
        Assert.IsNotNull(sourceFood);
        Assert.IsNotNull(targetFood);

        // 1. Create recipe with Replacement referencing sourceFood
        var createRes = await _client.PostAsJsonAsync(
            "/api/recipes",
            new UpsertRecipeRequest(
                Title: "Weeknight Tacos",
                Description: "Quick savory tacos.",
                Status: RecipeStatus.Published,
                BaseServings: 4,
                Ingredients:
                [
                    new RecipeIngredientInput(
                        DisplayName: "ground beef",
                        Quantity: 1m,
                        Unit: MeasurementUnit.Lb,
                        Replacements:
                        [
                            new RecipeIngredientReplacementInput(
                                DisplayName: "ground turkey",
                                Quantity: 1m,
                                MaxQuantity: 1.5m,
                                Unit: MeasurementUnit.Lb,
                                FoodItemId: sourceFood.Id,
                                PreparationNote: "lean"
                            ),
                        ]
                    ),
                ],
                Steps: [new RecipeStepInput(Instruction: "Brown 1 lb ground beef.")]
            )
        );
        Assert.AreEqual(HttpStatusCode.Created, createRes.StatusCode);
        var created = await createRes.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(created);
        Assert.IsNotNull(created.Ingredients[0].Replacements);
        Assert.HasCount(1, created.Ingredients[0].Replacements!);
        Assert.AreEqual(sourceFood.Id, created.Ingredients[0].Replacements![0].FoodItemId);
        Assert.AreEqual(1.5m, created.Ingredients[0].Replacements![0].MaxQuantity);
        Assert.AreEqual("lean", created.Ingredients[0].Replacements![0].PreparationNote);

        // 2. Verify ShareLink projection includes Replacements
        var shareRes = await _client.PostAsJsonAsync(
            $"/api/recipes/{created.Id}/shares",
            new CreateShareLinkRequest("Public")
        );
        Assert.AreEqual(HttpStatusCode.Created, shareRes.StatusCode);
        var share = await shareRes.Content.ReadFromJsonAsync<ShareLinkDto>();
        Assert.IsNotNull(share);

        using var anonClient = _factory.CreateClient();
        var publicRecipe = await anonClient.GetFromJsonAsync<PublicSharedRecipeDto>(
            $"/api/public/shares/{share.Token}"
        );
        Assert.IsNotNull(publicRecipe);
        Assert.IsNotNull(publicRecipe.Ingredients[0].Replacements);
        Assert.HasCount(1, publicRecipe.Ingredients[0].Replacements!);
        Assert.AreEqual("ground turkey", publicRecipe.Ingredients[0].Replacements![0].DisplayName);

        // 3. Merge sourceFood -> targetFood and verify Replacement.FoodItemId re-points
        var mergeRes = await _client.PostAsJsonAsync(
            "/api/food-items/merge",
            new MergeFoodItemsRequest(targetFood.Id, [sourceFood.Id])
        );
        Assert.AreEqual(HttpStatusCode.OK, mergeRes.StatusCode);

        var afterMerge = await _client.GetFromJsonAsync<RecipeDetailDto>(
            $"/api/recipes/{created.Id}"
        );
        Assert.IsNotNull(afterMerge);
        Assert.AreEqual(targetFood.Id, afterMerge.Ingredients[0].Replacements![0].FoodItemId);

        // 4. Unlink targetFood and verify Replacement.FoodItemId becomes null while replacement row stays
        var unlinkRes = await _client.PostAsync(
            $"/api/food-items/{targetFood.Id}/unlink-recipes",
            null
        );
        Assert.AreEqual(HttpStatusCode.OK, unlinkRes.StatusCode);

        var afterUnlink = await _client.GetFromJsonAsync<RecipeDetailDto>(
            $"/api/recipes/{created.Id}"
        );
        Assert.IsNotNull(afterUnlink);
        Assert.IsNotNull(afterUnlink.Ingredients[0].Replacements);
        Assert.HasCount(1, afterUnlink.Ingredients[0].Replacements!);
        Assert.IsNull(afterUnlink.Ingredients[0].Replacements![0].FoodItemId);

        // 5. Seed a CatalogEmbedding row and verify ResetAppData wipes both RecipeIngredientReplacements and CatalogEmbeddings
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.CatalogEmbeddings.Add(
                new App.Data.Entities.CatalogEmbedding
                {
                    ModelTag = "embeddinggemma",
                    NormalizedText = "ground turkey",
                    Dimensions = 2,
                    VectorBytes = [0, 0, 128, 63, 0, 0, 0, 0],
                }
            );
            await db.SaveChangesAsync();
        }

        var resetRes = await _client.PostAsync("/api/account/reset-data", null);
        Assert.AreEqual(HttpStatusCode.OK, resetRes.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.AreEqual(0, await db.RecipeIngredientReplacements.CountAsync());
            Assert.AreEqual(0, await db.CatalogEmbeddings.CountAsync());
        }
    }

    [TestMethod]
    public async Task Stage1SubRecipeReplacement_IsDroppedInsteadOfCreatingPlaceholderFoodItem()
    {
        var mockLlm = new FallbackExercisingLlmClient
        {
            ForceSimpleBatch = true,
            Stage1JsonOverride = """
                {
                    "title": "Crunchy Kettle Potato Chips",
                    "description": null,
                    "servings": 4,
                    "yield": null,
                    "sections": [
                        {
                            "name": null,
                            "ingredients": [
                                {
                                    "name": "table salt",
                                    "quantity": 0.5,
                                    "maxQuantity": null,
                                    "unit": "teaspoon",
                                    "prepNote": null,
                                    "isOptional": false,
                                    "replacements": [
                                        { "name": "topping", "quantity": 1, "unit": "recipe", "prepNote": "recipes follow" }
                                    ],
                                    "alternateMeasurements": []
                                }
                            ],
                            "instructions": ["Sprinkle 1/2 teaspoon table salt over warm chips."]
                        }
                    ],
                    "timing": { "prep": null, "cook": null, "total": null },
                    "tags": ["Snack"]
                }
                """,
            Stage4BatchJsonOverride = """
                {"steps":[{"stepNumber":1,"tokens":["Sprinkle ",{"type":"ingredient","ingredientIndex":0,"name":"table salt","quantity":0.5,"maxQuantity":null,"unit":"teaspoon"}," over warm chips."]}]}
                """,
        };

        await using var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Llm:TextModel", "qwen2.5:14b");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILlmExtractionClient>();
                services.AddSingleton<ILlmExtractionClient>(mockLlm);
            });
        });

        using var client = customFactory.CreateClient();
        await AuthenticateAsync(client);

        var enqueueRes = await client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(
                "Crunchy Kettle Potato Chips\nIngredients:\n- 1/2 teaspoon table salt or 1 recipe topping (recipes follow)\nInstructions:\n1. Sprinkle 1/2 teaspoon table salt over warm chips.",
                AutoPublish: true
            )
        );
        Assert.AreEqual(HttpStatusCode.Accepted, enqueueRes.StatusCode);
        var job = await enqueueRes.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(job);

        using (var scope = customFactory.Services.CreateScope())
        {
            var processed = await scope
                .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                .ProcessNextQueuedJobAsync();
            Assert.IsTrue(processed);
        }

        var jobDetail = await client.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{job.Id}"
        );
        Assert.IsNotNull(jobDetail);
        Assert.AreEqual(IngestionJobStatus.Published, jobDetail.Status, jobDetail.StatusDetail);
        Assert.IsNotNull(jobDetail.ResultingRecipeId);

        var published = await client.GetFromJsonAsync<RecipeDetailDto>(
            $"/api/recipes/{jobDetail.ResultingRecipeId}"
        );
        Assert.IsNotNull(published);
        Assert.HasCount(1, published.Ingredients);
        Assert.IsTrue(
            published.Ingredients[0].Replacements is null
                || published.Ingredients[0].Replacements!.Count == 0
        );

        using (var scope = customFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.IsFalse(await db.FoodItems.AnyAsync(f => f.NormalizedName == "topping"));
            Assert.IsFalse(await db.CatalogEmbeddings.AnyAsync(e => e.NormalizedText == "topping"));
        }
    }

    [TestMethod]
    public async Task ModelQuotaExhausted_OnRefinementModel_SkipsStage5WithoutPausingTextIngestion()
    {
        var mockLlm = new FallbackExercisingLlmClient
        {
            ForceSimpleBatch = true,
            ThrowOnStage = (cfg, stage) =>
                stage == "stage5b_polish_targeted"
                    ? new LlmHostUnavailableException(
                        "Daily request limit reached for model 'gemini-2.5-pro' (50 / 50 requests today).",
                        isQuotaExhausted: true
                    )
                    {
                        ExhaustedQuotaScope = LlmQuotaScope.Model,
                        ExhaustedGateKey = cfg.ModelKey,
                        QuotaResetTimezone = cfg.EffectiveQuotaResetTimezone,
                        QuotaResetTime = cfg.EffectiveQuotaResetTime,
                        SafeguardsEnabled = cfg.EnableRateLimitSafeguards,
                    }
                    : null,
        };

        await using var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILlmExtractionClient>();
                services.AddSingleton<ILlmExtractionClient>(mockLlm);
            });
        });

        using var client = customFactory.CreateClient();
        await AuthenticateAsync(client);

        var conn = await (
            await client.PostAsJsonAsync(
                "/api/llm-settings/connections",
                new UpsertLlmConnectionRequest(
                    Name: "Google AI Studio",
                    Provider: LlmProvider.OpenAiCompatible,
                    BaseUrl: "https://generativelanguage.googleapis.com/v1beta/openai",
                    EnableRateLimitSafeguards: true
                )
            )
        ).Content.ReadFromJsonAsync<LlmConnectionDto>();
        Assert.IsNotNull(conn);

        var textCfg = await (
            await client.PostAsJsonAsync(
                "/api/llm-settings/model-configurations",
                new UpsertLlmModelConfigurationRequest(
                    "Flash",
                    conn.Id,
                    "gemini-2.5-flash",
                    DailyRequestQuota: 1000
                )
            )
        ).Content.ReadFromJsonAsync<LlmModelConfigurationDto>();
        var refineCfg = await (
            await client.PostAsJsonAsync(
                "/api/llm-settings/model-configurations",
                new UpsertLlmModelConfigurationRequest(
                    "Pro",
                    conn.Id,
                    "gemini-2.5-pro",
                    DailyRequestQuota: 50,
                    DailyQuotaResetTimezone: "America/Los_Angeles",
                    DailyQuotaResetTime: "00:00"
                )
            )
        ).Content.ReadFromJsonAsync<LlmModelConfigurationDto>();
        Assert.IsNotNull(textCfg);
        Assert.IsNotNull(refineCfg);

        var wfRes = await client.PutAsJsonAsync(
            "/api/llm-settings/workflows",
            new UpdateLlmWorkflowsRequest(
                TextModelProfileId: textCfg.DefaultProfileId,
                RefineModelProfileId: refineCfg.DefaultProfileId,
                EnableIngestion: true,
                AutoResumeQueue: true
            )
        );
        Assert.AreEqual(HttpStatusCode.OK, wfRes.StatusCode);

        var enqueueRes = await client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(
                "Toasted Butter Bread\nIngredients:\n- 2 tbsp butter\nInstructions:\n1. Melt 2 tablespoons butter."
            )
        );
        Assert.AreEqual(HttpStatusCode.Accepted, enqueueRes.StatusCode);
        var job = await enqueueRes.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(job);

        using (var scope = customFactory.Services.CreateScope())
        {
            var processed = await scope
                .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                .ProcessNextQueuedJobAsync();
            Assert.IsTrue(processed);
        }

        Assert.IsTrue(mockLlm.StageCalls.Contains("stage5b_polish_targeted"));

        var settings = await client.GetFromJsonAsync<LlmSettingsDto>("/api/llm-settings");
        Assert.IsNotNull(settings);
        Assert.IsTrue(
            settings.EnableIngestion,
            "Exhausting an optional Refinement model's quota must not pause Text ingestion."
        );

        var detail = await client.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{job.Id}"
        );
        Assert.IsNotNull(detail);
        Assert.AreEqual(IngestionJobStatus.ReadyForReview, detail.Status);
    }

    [TestMethod]
    public async Task ModelQuotaExhausted_OnTextModel_AutoPausesQueueUsingModelResetSchedule()
    {
        var mockLlm = new FallbackExercisingLlmClient
        {
            ForceSimpleBatch = true,
            ThrowOnStage = (cfg, stage) =>
                stage == "stage1_extract_recipe"
                    ? new LlmHostUnavailableException(
                        "Daily request limit reached for model 'gemini-2.5-flash' (5 / 5 requests today).",
                        isQuotaExhausted: true
                    )
                    {
                        ExhaustedQuotaScope = LlmQuotaScope.Model,
                        ExhaustedGateKey = cfg.ModelKey,
                        QuotaResetTimezone = cfg.EffectiveQuotaResetTimezone,
                        QuotaResetTime = cfg.EffectiveQuotaResetTime,
                        SafeguardsEnabled = cfg.EnableRateLimitSafeguards,
                    }
                    : null,
        };

        await using var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILlmExtractionClient>();
                services.AddSingleton<ILlmExtractionClient>(mockLlm);
            });
        });

        using var client = customFactory.CreateClient();
        await AuthenticateAsync(client);

        var conn = await (
            await client.PostAsJsonAsync(
                "/api/llm-settings/connections",
                new UpsertLlmConnectionRequest(
                    Name: "Google AI Studio",
                    Provider: LlmProvider.OpenAiCompatible,
                    BaseUrl: "https://generativelanguage.googleapis.com/v1beta/openai",
                    EnableRateLimitSafeguards: true,
                    DailyQuotaResetTimezone: "UTC",
                    DailyQuotaResetTime: "00:00"
                )
            )
        ).Content.ReadFromJsonAsync<LlmConnectionDto>();
        Assert.IsNotNull(conn);

        var textCfg = await (
            await client.PostAsJsonAsync(
                "/api/llm-settings/model-configurations",
                new UpsertLlmModelConfigurationRequest(
                    "Flash",
                    conn.Id,
                    "gemini-2.5-flash",
                    DailyRequestQuota: 5,
                    DailyQuotaResetTimezone: "America/Los_Angeles",
                    DailyQuotaResetTime: "03:00"
                )
            )
        ).Content.ReadFromJsonAsync<LlmModelConfigurationDto>();
        Assert.IsNotNull(textCfg);

        var wfRes = await client.PutAsJsonAsync(
            "/api/llm-settings/workflows",
            new UpdateLlmWorkflowsRequest(
                TextModelProfileId: textCfg.DefaultProfileId,
                EnableIngestion: true,
                AutoResumeQueue: true
            )
        );
        Assert.AreEqual(HttpStatusCode.OK, wfRes.StatusCode);

        var enqueueRes = await client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(
                "Toasted Butter Bread\nIngredients:\n- 2 tbsp butter\nInstructions:\n1. Melt 2 tablespoons butter."
            )
        );
        Assert.AreEqual(HttpStatusCode.Accepted, enqueueRes.StatusCode);
        var job = await enqueueRes.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(job);

        var before = DateTimeOffset.UtcNow;
        using (var scope = customFactory.Services.CreateScope())
        {
            var processed = await scope
                .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                .ProcessNextQueuedJobAsync();
            Assert.IsFalse(processed);
        }

        var expectedReset = LlmQuotaSchedule.GetNextResetUtc(
            before,
            "America/Los_Angeles",
            "03:00"
        );
        var settings = await client.GetFromJsonAsync<LlmSettingsDto>("/api/llm-settings");
        Assert.IsNotNull(settings);
        Assert.IsFalse(settings.EnableIngestion);
        Assert.IsNotNull(settings.PausedUntilUtc);
        Assert.IsLessThan(
            5.0,
            Math.Abs((settings.PausedUntilUtc.Value - expectedReset).TotalSeconds),
            $"Expected PausedUntilUtc ({settings.PausedUntilUtc:u}) to match model reset ({expectedReset:u})."
        );

        var detail = await client.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{job.Id}"
        );
        Assert.IsNotNull(detail);
        Assert.AreEqual(IngestionJobStatus.Queued, detail.Status);
        StringAssert.Contains(detail.StatusDetail ?? "", "America/Los_Angeles");
    }

    private sealed class FallbackExercisingLlmClient : ILlmExtractionClient
    {
        public bool ForceSimpleBatch { get; set; }
        public string? Stage1JsonOverride { get; set; }
        public string? Stage4BatchJsonOverride { get; set; }
        public Func<EffectiveLlmConnectionConfig, string, Exception?>? ThrowOnStage { get; set; }
        public int EmbeddingCallCount { get; private set; }
        public List<string> StageCalls { get; } = [];

        public Task<LlmConnectionTestResultDto> ProbeAndListModelsAsync(
            EffectiveLlmConnectionConfig config,
            int timeoutSeconds = 15,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(
                new LlmConnectionTestResultDto(
                    true,
                    config.Provider,
                    config.BaseUrl,
                    5,
                    ["qwen2.5:14b", "embeddinggemma"],
                    null
                )
            );

        public Task<float[][]> GenerateEmbeddingsAsync(
            EffectiveLlmConnectionConfig config,
            IReadOnlyList<string> inputs,
            CancellationToken cancellationToken = default
        )
        {
            EmbeddingCallCount++;
            var vectors = inputs
                .Select(text =>
                    text.Contains("butter", StringComparison.OrdinalIgnoreCase)
                        ? new[] { 1.0f, 0.0f }
                        : new[] { 0.0f, 1.0f }
                )
                .ToArray();
            return Task.FromResult(vectors);
        }

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
            StageCalls.Add(stageName);
            var thrown = ThrowOnStage?.Invoke(config, stageName);
            if (thrown is not null)
            {
                throw thrown;
            }

            var userContent =
                messages
                    .FirstOrDefault(m =>
                        string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)
                    )
                    ?.Content
                ?? "";

            if (!ForceSimpleBatch && stageName == "stage3c_match_batch")
            {
                return Task.FromResult(
                    new LlmStructuredStageResult<T>(
                        default,
                        "",
                        config.TextModel ?? "qwen2.5:14b",
                        5,
                        Error: "Batch truncated",
                        DoneReason: "length"
                    )
                );
            }

            var raw = stageName switch
            {
                "stage1_extract_recipe" => Stage1JsonOverride
                    ?? """
                        {
                            "title": "Toasted Butter Bread",
                            "description": null,
                            "servings": 2,
                            "yield": null,
                            "sections": [
                                {
                                    "name": null,
                                    "ingredients": [
                                        {
                                            "name": "butter",
                                            "quantity": 2,
                                            "maxQuantity": null,
                                            "unit": "tbsp",
                                            "prepNote": null,
                                            "isOptional": false,
                                            "replacements": [
                                                { "name": "margarine", "quantity": 2, "unit": "tbsp", "prepNote": null }
                                            ],
                                            "alternateMeasurements": []
                                        }
                                    ],
                                    "instructions": ["Melt 2 tablespoons butter."]
                                }
                            ],
                            "timing": { "prep": null, "cook": null, "total": null },
                            "tags": ["Breakfast"]
                        }
                        """,
                "stage3a_ingredient_search_terms" => """{"searchTerms":["butter","margarine"]}""",
                "stage3c_match_batch" => BuildStage3cResponse(userContent),
                "stage3b_match_ingredient" => BuildStage3bResponse(userContent),
                "stage4b_tokenize_batch" => ForceSimpleBatch
                    ? (Stage4BatchJsonOverride ?? BuildStage4BatchValid())
                    : """{"steps":[]}""",
                "stage4_tokenize_step" => """
                    {
                        "tokens": [
                            "Melt ",
                            {
                                "type": "ingredient",
                                "ingredientIndex": 0,
                                "name": "butter",
                                "quantity": 2,
                                "maxQuantity": null,
                                "unit": "tablespoon"
                            },
                            "."
                        ]
                    }
                    """,
                "stage5b_polish_targeted" => """
                    {
                        "title": null,
                        "description": null,
                        "servings": null,
                        "yield": null,
                        "timing": { "prep": 2, "cook": 10, "total": 12 },
                        "tags": null,
                        "ingredientPatches": [],
                        "stepPatches": [],
                        "polishNotes": ["Populated missing timing."]
                    }
                    """,
                _ => throw new NotSupportedException(stageName),
            };

            var parsed = JsonSerializer.Deserialize<T>(
                raw,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );
            return Task.FromResult(
                new LlmStructuredStageResult<T>(parsed, raw, config.TextModel ?? "qwen2.5:14b", 5)
            );
        }

        private static string BuildStage3cResponse(string userContent)
        {
            using var doc = JsonDocument.Parse(userContent);
            var matches = new List<object>();
            foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                var idx = item.GetProperty("ingredientIndex").GetInt32();
                matches.Add(BuildMatchObj(item, includeIdx: true, idx));
            }
            return JsonSerializer.Serialize(new { matches });
        }

        private static string BuildStage3bResponse(string userContent)
        {
            using var doc = JsonDocument.Parse(userContent);
            return JsonSerializer.Serialize(BuildMatchObj(doc.RootElement, includeIdx: false, 0));
        }

        private static object BuildMatchObj(JsonElement item, bool includeIdx, int idx)
        {
            var name = item.GetProperty("name").GetString() ?? "";
            if (item.TryGetProperty("existingIngredients", out var cands))
            {
                foreach (var c in cands.EnumerateArray())
                {
                    var cname = c.GetProperty("name").GetString() ?? "";
                    if (cname.Contains(name, StringComparison.OrdinalIgnoreCase))
                    {
                        var id = c.GetProperty("id").GetString();
                        return includeIdx
                            ? (object)
                                new
                                {
                                    ingredientIndex = idx,
                                    matchType = "existing_ingredient",
                                    id,
                                    name = cname,
                                    category = "DairyAndEggs",
                                }
                            : new
                            {
                                matchType = "existing_ingredient",
                                id,
                                name = cname,
                                category = "DairyAndEggs",
                            };
                    }
                }
            }

            return includeIdx
                ? (object)
                    new
                    {
                        ingredientIndex = idx,
                        matchType = "new_ingredient",
                        id = (string?)null,
                        name,
                        category = "DairyAndEggs",
                    }
                : new
                {
                    matchType = "new_ingredient",
                    id = (string?)null,
                    name,
                    category = "DairyAndEggs",
                };
        }

        private static string BuildStage4BatchValid() =>
            """{"steps":[{"stepNumber":1,"tokens":["Melt ",{"type":"ingredient","ingredientIndex":0,"name":"butter","quantity":2,"maxQuantity":null,"unit":"tablespoon"},"."]}]}""";

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

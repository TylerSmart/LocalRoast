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
using App.Data.Entities;
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
    public async Task ModelQuotaExhausted_OnRefinementModel_SkipsStage6RefinementWithoutPausingTextIngestion()
    {
        var mockLlm = new FallbackExercisingLlmClient
        {
            ForceSimpleBatch = true,
            ThrowOnStage = (cfg, stage) =>
                stage == "stage6_refine_recipe"
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
                AutoResumeQueue: true,
                RefineDuringIngestion: true
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
        Assert.IsTrue(mockLlm.StageCalls.Contains("stage6_refine_recipe"));
        Assert.IsTrue(
            mockLlm.StageModels.Any(x =>
                x.Stage == "stage5b_polish_targeted" && x.Model == "gemini-2.5-flash"
            ),
            "Stage 5 Targeted Polish must run on the primary Text model."
        );
        Assert.IsTrue(
            mockLlm.StageModels.Any(x =>
                x.Stage == "stage6_refine_recipe" && x.Model == "gemini-2.5-pro"
            ),
            "Stage 6 Refinement must run on the Refinement model."
        );

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
        Assert.AreEqual("gemini-2.5-flash", detail.ExtractedModel);
        Assert.IsNull(detail.RefinedModel);
        Assert.IsNull(detail.RefinedAt);
    }

    [TestMethod]
    public async Task RefineDuringIngestion_RunsBeforeAutoPublishAndCleansFoodCatalogNames()
    {
        var mockLlm = new FallbackExercisingLlmClient
        {
            ForceSimpleBatch = true,
            Stage1JsonOverride = """
                {
                    "title": "Garlicky Roasted Squash",
                    "description": "Sweet roasted squash.",
                    "servings": 4,
                    "yield": "4 servings",
                    "sections": [
                        {
                            "name": null,
                            "ingredients": [
                                {
                                    "name": "1/2-inch cubed peeled butternut squash",
                                    "quantity": 2,
                                    "maxQuantity": null,
                                    "unit": "pound",
                                    "prepNote": null,
                                    "isOptional": false,
                                    "replacements": [],
                                    "alternateMeasurements": []
                                }
                            ],
                            "instructions": ["Roast 2 pounds butternut squash until tender."]
                        }
                    ],
                    "timing": { "prep": 10, "cook": 30, "total": 40 },
                    "tags": ["Side Dish"]
                }
                """,
            Stage4BatchJsonOverride = """
                {
                    "steps": [
                        {
                            "stepNumber": 1,
                            "tokens": [
                                "Roast ",
                                {
                                    "type": "ingredient",
                                    "ingredientIndex": 0,
                                    "name": "butternut squash",
                                    "quantity": 2,
                                    "maxQuantity": null,
                                    "unit": "pound"
                                },
                                " until tender."
                            ]
                        }
                    ]
                }
                """,
            Stage6JsonOverride = """
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
                            "displayName": "butternut squash",
                            "canonicalFoodName": "butternut squash",
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
                        }
                    ],
                    "stepPatches": [],
                    "refinementNotes": ["Cleaned butternut squash food catalog name before publish."]
                }
                """,
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
                    Name: "Local Ollama",
                    Provider: LlmProvider.Ollama,
                    BaseUrl: "http://localhost:11434"
                )
            )
        ).Content.ReadFromJsonAsync<LlmConnectionDto>();
        Assert.IsNotNull(conn);

        var textCfg = await (
            await client.PostAsJsonAsync(
                "/api/llm-settings/model-configurations",
                new UpsertLlmModelConfigurationRequest("Fast Text", conn.Id, "gemma4:e4b")
            )
        ).Content.ReadFromJsonAsync<LlmModelConfigurationDto>();
        var refineCfg = await (
            await client.PostAsJsonAsync(
                "/api/llm-settings/model-configurations",
                new UpsertLlmModelConfigurationRequest("Deep Refine", conn.Id, "gemma4:12b")
            )
        ).Content.ReadFromJsonAsync<LlmModelConfigurationDto>();
        Assert.IsNotNull(textCfg);
        Assert.IsNotNull(refineCfg);

        var wfRes = await client.PutAsJsonAsync(
            "/api/llm-settings/workflows",
            new UpdateLlmWorkflowsRequest(
                TextModelProfileId: textCfg.DefaultProfileId,
                RefineModelProfileId: refineCfg.DefaultProfileId,
                AutoPublishHighConfidenceText: true,
                EnableIngestion: true,
                RefineDuringIngestion: true,
                RefineDuringIdle: false
            )
        );
        Assert.AreEqual(HttpStatusCode.OK, wfRes.StatusCode);

        var enqueueRes = await client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(
                "Garlicky Roasted Squash\nIngredients:\n- 2 lb butternut squash, peeled and cut into 1/2-inch cubes\nInstructions:\n1. Roast 2 pounds butternut squash until tender."
            )
        );
        Assert.AreEqual(HttpStatusCode.Accepted, enqueueRes.StatusCode);
        var queuedJob = await enqueueRes.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(queuedJob);

        using (var scope = customFactory.Services.CreateScope())
        {
            var processed = await scope
                .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                .ProcessNextQueuedJobAsync();
            Assert.IsTrue(processed);
        }

        Assert.IsTrue(mockLlm.StageCalls.Contains("stage6_refine_recipe"));

        var jobDetail = await client.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{queuedJob.Id}"
        );
        Assert.IsNotNull(jobDetail);
        Assert.AreEqual(IngestionJobStatus.Published, jobDetail.Status);
        Assert.AreEqual("gemma4:e4b", jobDetail.ExtractedModel);
        Assert.AreEqual("gemma4:12b", jobDetail.RefinedModel);
        Assert.IsNotNull(jobDetail.RefinedAt);
        Assert.IsNotNull(jobDetail.ResultingRecipeId);

        var recipe = await client.GetFromJsonAsync<RecipeDetailDto>(
            $"/api/recipes/{jobDetail.ResultingRecipeId}"
        );
        Assert.IsNotNull(recipe);
        Assert.AreEqual("gemma4:e4b", recipe.ExtractedModel);
        Assert.AreEqual("gemma4:12b", recipe.RefinedModel);

        using (var scope = customFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.IsFalse(
                await db.FoodItems.AnyAsync(f =>
                    f.NormalizedName.Contains("1/2-inch") || f.NormalizedName.Contains("cubed")
                ),
                "Polluted food name must never be created in FoodItems when RefineDuringIngestion cleans it before publish."
            );
            Assert.IsTrue(
                await db.FoodItems.AnyAsync(f => f.NormalizedName == "butternut squash"),
                "Clean refined food name must be materialized in FoodItems."
            );
        }
    }

    [TestMethod]
    public async Task RefineDuringIdle_PrioritizesReviewThenPublished_UpdatesLiveRecipeAndCleansOrphans()
    {
        var mockLlm = new FallbackExercisingLlmClient
        {
            ForceSimpleBatch = true,
            Stage6JsonFactory = (_, userContent) =>
            {
                if (userContent.Contains("Polluted Published Stew", StringComparison.Ordinal))
                {
                    return """
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
                                    "displayName": "yukon gold potatoes",
                                    "canonicalFoodName": "yukon gold potato",
                                    "category": "Produce",
                                    "isPantryStaple": false,
                                    "quantity": null,
                                    "maxQuantity": null,
                                    "unit": null,
                                    "prepNote": "peeled and diced",
                                    "isOptional": null,
                                    "isRecipeReference": null,
                                    "referencedRecipeTitle": null,
                                    "alternateMeasurements": null,
                                    "replacementPatches": null
                                }
                            ],
                            "stepPatches": [],
                            "refinementNotes": ["Fixed polluted potato catalog item name."]
                        }
                        """;
                }

                return """
                    {
                        "title": "Refined Review Soup",
                        "description": null,
                        "servings": null,
                        "yield": null,
                        "timing": null,
                        "tags": null,
                        "ingredientPatches": [],
                        "stepPatches": [],
                        "refinementNotes": ["Polished title."]
                    }
                    """;
            },
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
                    Name: "Local Ollama",
                    Provider: LlmProvider.Ollama,
                    BaseUrl: "http://localhost:11434"
                )
            )
        ).Content.ReadFromJsonAsync<LlmConnectionDto>();
        Assert.IsNotNull(conn);

        var textCfg = await (
            await client.PostAsJsonAsync(
                "/api/llm-settings/model-configurations",
                new UpsertLlmModelConfigurationRequest("Fast Text", conn.Id, "gemma4:e4b")
            )
        ).Content.ReadFromJsonAsync<LlmModelConfigurationDto>();
        var refineCfg = await (
            await client.PostAsJsonAsync(
                "/api/llm-settings/model-configurations",
                new UpsertLlmModelConfigurationRequest("Idle Refine", conn.Id, "gemma4:26b-a4b")
            )
        ).Content.ReadFromJsonAsync<LlmModelConfigurationDto>();
        Assert.IsNotNull(textCfg);
        Assert.IsNotNull(refineCfg);

        var wfRes = await client.PutAsJsonAsync(
            "/api/llm-settings/workflows",
            new UpdateLlmWorkflowsRequest(
                TextModelProfileId: textCfg.DefaultProfileId,
                RefineModelProfileId: refineCfg.DefaultProfileId,
                AutoPublishHighConfidenceText: false,
                EnableIngestion: true,
                RefineDuringIngestion: false,
                RefineDuringIdle: true
            )
        );
        Assert.AreEqual(HttpStatusCode.OK, wfRes.StatusCode);

        // Seed: (1) an already-published job with a bad orphaned FoodItem + user collection,
        //       (2) a ReadyForReview job (should be refined FIRST even though Published is newer).
        Guid reviewJobId;
        Guid publishedJobId;
        Guid publishedRecipeId;
        Guid badFoodId;
        Guid sharedFoodId;
        Guid userCollectionId;

        using (var scope = customFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var badFood = new FoodItem
            {
                Id = Guid.NewGuid(),
                Name = "peeled diced yukon gold potatoes",
                NormalizedName = "peeled diced yukon gold potatoes",
                Category = FoodCategory.Other,
                IsPantryStaple = false,
                CreatedBy = "Test",
                ModifiedBy = "Test",
            };
            var sharedFood = new FoodItem
            {
                Id = Guid.NewGuid(),
                Name = "Kosher Salt",
                NormalizedName = "kosher salt",
                Category = FoodCategory.SpicesAndSeasonings,
                IsPantryStaple = true,
                CreatedBy = "Test",
                ModifiedBy = "Test",
            };
            var userCollection = new Collection
            {
                Id = Guid.NewGuid(),
                Name = "Winter Favorites",
                Slug = "winter-favorites",
                CreatedBy = "Test",
                ModifiedBy = "Test",
            };
            db.FoodItems.AddRange(badFood, sharedFood);
            db.Collections.Add(userCollection);

            var publishedRecipe = new Recipe
            {
                Id = Guid.NewGuid(),
                Title = "Polluted Published Stew",
                Slug = "polluted-published-stew",
                Status = RecipeStatus.Published,
                BaseServings = 4m,
                ExtractedModel = "gemma4:e4b",
                CoverImagePath = "/api/uploads/recipes/user-cover.webp",
                ThumbnailImagePath = "/api/uploads/recipes/user-thumb.webp",
                CreatedBy = "Test",
                ModifiedBy = "Test",
            };
            publishedRecipe.RecipeCollections.Add(
                new RecipeCollection
                {
                    RecipeId = publishedRecipe.Id,
                    CollectionId = userCollection.Id,
                }
            );
            publishedRecipe.Ingredients.Add(
                new RecipeIngredient
                {
                    Id = Guid.NewGuid(),
                    SortOrder = 0,
                    DisplayName = "peeled diced yukon gold potatoes",
                    Quantity = 2m,
                    Unit = MeasurementUnit.Lb,
                    FoodItemId = badFood.Id,
                }
            );
            publishedRecipe.Ingredients.Add(
                new RecipeIngredient
                {
                    Id = Guid.NewGuid(),
                    SortOrder = 1,
                    DisplayName = "kosher salt",
                    Quantity = 1m,
                    Unit = MeasurementUnit.Tsp,
                    FoodItemId = sharedFood.Id,
                }
            );
            publishedRecipe.Steps.Add(
                new RecipeStep
                {
                    Id = Guid.NewGuid(),
                    StepNumber = 1,
                    Instruction = "Simmer potatoes and kosher salt until tender.",
                }
            );
            db.Recipes.Add(publishedRecipe);

            // Second recipe using sharedFood so we prove shared FoodItems are never deleted
            var otherRecipe = new Recipe
            {
                Id = Guid.NewGuid(),
                Title = "Salted Broth",
                Slug = "salted-broth",
                Status = RecipeStatus.Published,
                BaseServings = 2m,
                CreatedBy = "Test",
                ModifiedBy = "Test",
            };
            otherRecipe.Ingredients.Add(
                new RecipeIngredient
                {
                    Id = Guid.NewGuid(),
                    SortOrder = 0,
                    DisplayName = "kosher salt",
                    FoodItemId = sharedFood.Id,
                }
            );
            otherRecipe.Steps.Add(
                new RecipeStep
                {
                    Id = Guid.NewGuid(),
                    StepNumber = 1,
                    Instruction = "Dissolve salt in hot water.",
                }
            );
            db.Recipes.Add(otherRecipe);

            var pubDraft = new IngestionExtractedDraftDto(
                Title: "Polluted Published Stew",
                BaseServings: 4m,
                ExtractedModel: "gemma4:e4b",
                Ingredients:
                [
                    new IngestionDraftIngredientDto(
                        Quantity: 2m,
                        Unit: MeasurementUnit.Lb,
                        MatchedFoodItemId: badFood.Id,
                        MatchedFoodItemName: badFood.Name,
                        IsNewFoodItem: false,
                        DisplayName: "peeled diced yukon gold potatoes"
                    ),
                    new IngestionDraftIngredientDto(
                        Quantity: 1m,
                        Unit: MeasurementUnit.Tsp,
                        MatchedFoodItemId: sharedFood.Id,
                        MatchedFoodItemName: sharedFood.Name,
                        IsNewFoodItem: false,
                        DisplayName: "kosher salt"
                    ),
                ],
                Steps:
                [
                    new IngestionDraftStepDto(
                        Instruction: "Simmer potatoes and kosher salt until tender."
                    ),
                ]
            );

            var reviewDraft = new IngestionExtractedDraftDto(
                Title: "Initial Review Soup",
                BaseServings: 4m,
                ExtractedModel: "gemma4:e4b",
                Ingredients:
                [
                    new IngestionDraftIngredientDto(
                        Quantity: 1m,
                        Unit: MeasurementUnit.Tsp,
                        MatchedFoodItemId: sharedFood.Id,
                        MatchedFoodItemName: sharedFood.Name,
                        IsNewFoodItem: false,
                        DisplayName: "kosher salt"
                    ),
                ],
                Steps: [new IngestionDraftStepDto(Instruction: "Heat broth with salt.")]
            );

            var reviewJob = new IngestionJob
            {
                Id = Guid.NewGuid(),
                Channel = IngestionChannel.RawText,
                Status = IngestionJobStatus.ReadyForReview,
                DisplayTitle = "Initial Review Soup",
                RawText =
                    "Initial Review Soup\nIngredients:\n- 1 tsp kosher salt\nInstructions:\n1. Heat broth with salt.",
                NormalizedSourceText =
                    "Initial Review Soup\nIngredients:\n- 1 tsp kosher salt\nInstructions:\n1. Heat broth with salt.",
                ExtractedDraftJson = JsonSerializer.Serialize(reviewDraft),
                ExtractedModel = "gemma4:e4b",
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
                CreatedBy = "Test",
                ModifiedBy = "Test",
            };

            var publishedJob = new IngestionJob
            {
                Id = Guid.NewGuid(),
                Channel = IngestionChannel.RawText,
                Status = IngestionJobStatus.Published,
                DisplayTitle = "Polluted Published Stew",
                RawText =
                    "Polluted Published Stew\nIngredients:\n- 2 lb yukon gold potatoes, peeled and diced\n- 1 tsp kosher salt\nInstructions:\n1. Simmer potatoes and kosher salt until tender.",
                NormalizedSourceText =
                    "Polluted Published Stew\nIngredients:\n- 2 lb yukon gold potatoes, peeled and diced\n- 1 tsp kosher salt\nInstructions:\n1. Simmer potatoes and kosher salt until tender.",
                ExtractedDraftJson = JsonSerializer.Serialize(pubDraft),
                ExtractedModel = "gemma4:e4b",
                ResultingRecipeId = publishedRecipe.Id,
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
                CreatedBy = "Test",
                ModifiedBy = "Test",
            };

            db.IngestionJobs.AddRange(reviewJob, publishedJob);
            await db.SaveChangesAsync();

            reviewJobId = reviewJob.Id;
            publishedJobId = publishedJob.Id;
            publishedRecipeId = publishedRecipe.Id;
            badFoodId = badFood.Id;
            sharedFoodId = sharedFood.Id;
            userCollectionId = userCollection.Id;
        }

        // First idle refinement tick MUST prioritize ReadyForReview before Published
        using (var scope = customFactory.Services.CreateScope())
        {
            var processed = await scope
                .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                .ProcessNextIdleRefinementAsync();
            Assert.IsTrue(processed);
        }

        var reviewAfterFirst = await client.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{reviewJobId}"
        );
        var pubAfterFirst = await client.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{publishedJobId}"
        );
        Assert.AreEqual("Refined Review Soup", reviewAfterFirst!.DisplayTitle);
        Assert.AreEqual("gemma4:e4b", reviewAfterFirst.ExtractedModel);
        Assert.AreEqual("gemma4:26b-a4b", reviewAfterFirst.RefinedModel);
        Assert.IsNotNull(reviewAfterFirst.RefinedAt);
        Assert.IsNull(
            pubAfterFirst!.RefinedAt,
            "Published job must wait until ReadyForReview jobs finish."
        );

        // Second idle refinement tick refines the Published job in-place & cleans up orphaned FoodItem
        using (var scope = customFactory.Services.CreateScope())
        {
            var processed = await scope
                .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                .ProcessNextIdleRefinementAsync();
            Assert.IsTrue(processed);
        }

        var pubAfterSecond = await client.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{publishedJobId}"
        );
        Assert.AreEqual(IngestionJobStatus.Published, pubAfterSecond!.Status);
        Assert.AreEqual("gemma4:e4b", pubAfterSecond.ExtractedModel);
        Assert.AreEqual("gemma4:26b-a4b", pubAfterSecond.RefinedModel);
        Assert.IsNotNull(pubAfterSecond.RefinedAt);

        var updatedLiveRecipe = await client.GetFromJsonAsync<RecipeDetailDto>(
            $"/api/recipes/{publishedRecipeId}"
        );
        Assert.IsNotNull(updatedLiveRecipe);
        Assert.AreEqual("gemma4:e4b", updatedLiveRecipe.ExtractedModel);
        Assert.AreEqual("gemma4:26b-a4b", updatedLiveRecipe.RefinedModel);
        Assert.AreEqual("/api/uploads/recipes/user-cover.webp", updatedLiveRecipe.CoverImagePath);
        Assert.IsTrue(
            updatedLiveRecipe.Collections.Any(c => c.Id == userCollectionId),
            "In-place idle refinement must preserve user-assigned recipe collections."
        );

        using (var scope = customFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.IsFalse(
                await db.FoodItems.AnyAsync(f => f.Id == badFoodId),
                "Orphaned bad FoodItem created solely for the published recipe must be removed."
            );
            Assert.IsTrue(
                await db.FoodItems.AnyAsync(f => f.NormalizedName == "yukon gold potato"),
                "Clean refined FoodItem must be created and linked."
            );
            Assert.IsTrue(
                await db.FoodItems.AnyAsync(f => f.Id == sharedFoodId),
                "Shared FoodItems must never be deleted."
            );
        }

        // Third tick returns false (all jobs refined)
        using (var scope = customFactory.Services.CreateScope())
        {
            var processed = await scope
                .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                .ProcessNextIdleRefinementAsync();
            Assert.IsFalse(processed);
        }

        // Retrying a refined job resets RefinedModel and RefinedAt so it can be refined again
        var retryRes = await client.PostAsync($"/api/ingestion/jobs/{reviewJobId}/retry", null);
        Assert.AreEqual(HttpStatusCode.Accepted, retryRes.StatusCode);
        var retriedDetail = await client.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{reviewJobId}"
        );
        Assert.IsNull(retriedDetail!.RefinedModel);
        Assert.IsNull(retriedDetail.RefinedAt);

        // Idle refinement must NOT run while a queued ingestion job exists
        using (var scope = customFactory.Services.CreateScope())
        {
            var processed = await scope
                .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                .ProcessNextIdleRefinementAsync();
            Assert.IsFalse(processed);
        }
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
        public string? Stage6JsonOverride { get; set; }
        public Func<EffectiveLlmConnectionConfig, string, string?>? Stage6JsonFactory { get; set; }
        public Func<EffectiveLlmConnectionConfig, string, Exception?>? ThrowOnStage { get; set; }
        public int EmbeddingCallCount { get; private set; }
        public List<string> StageCalls { get; } = [];
        public List<(string Stage, string? Model)> StageModels { get; } = [];

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
            StageModels.Add((stageName, config.TextModel));
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
                "stage6_refine_recipe" => Stage6JsonFactory?.Invoke(config, userContent)
                    ?? Stage6JsonOverride
                    ?? """
                        {
                            "title": null,
                            "description": null,
                            "servings": null,
                            "yield": null,
                            "timing": null,
                            "tags": null,
                            "ingredientPatches": [],
                            "stepPatches": [],
                            "refinementNotes": ["No changes needed"]
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

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using App.Core.Authentication;
using App.Core.FoodCatalog;
using App.Core.Ingestion;
using App.Core.Recipes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.API.IntegrationTests;

[TestClass]
public sealed class IngestionIntegrationTests
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

    [TestMethod]
    public async Task IngestionEndpoints_Unauthenticated_ReturnUnauthorized()
    {
        var summaryResp = await _client.GetAsync("/api/ingestion/jobs/summary");
        Assert.AreEqual(HttpStatusCode.Unauthorized, summaryResp.StatusCode);

        var jobsResp = await _client.GetAsync("/api/ingestion/jobs");
        Assert.AreEqual(HttpStatusCode.Unauthorized, jobsResp.StatusCode);

        var textResp = await _client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest("Sample")
        );
        Assert.AreEqual(HttpStatusCode.Unauthorized, textResp.StatusCode);
    }

    [TestMethod]
    public async Task EnqueueWebUrls_ValidUrls_ReturnsAcceptedAndUpdatesSummary()
    {
        await AuthenticateAsync(_client);

        var request = new EnqueueWebUrlIngestionRequest(
            Urls: ["https://example.com/recipe-1", "https://example.com/recipe-2"]
        );

        var response = await _client.PostAsJsonAsync("/api/ingestion/jobs/urls", request);
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);

        var jobs = await response.Content.ReadFromJsonAsync<
            IReadOnlyList<IngestionJobSummaryDto>
        >();
        Assert.IsNotNull(jobs);
        Assert.AreEqual(2, jobs.Count);
        Assert.AreEqual(IngestionChannel.WebUrl, jobs[0].Channel);
        Assert.AreEqual(IngestionJobStatus.Queued, jobs[0].Status);

        var summaryResp = await _client.GetAsync("/api/ingestion/jobs/summary");
        var summary = await summaryResp.Content.ReadFromJsonAsync<IngestionQueueSummaryDto>();
        Assert.IsNotNull(summary);
        Assert.IsTrue(summary.ActiveProcessingCount >= 2);
    }

    [TestMethod]
    public async Task EnqueueWebUrls_InvalidUrl_ReturnsBadRequest()
    {
        await AuthenticateAsync(_client);

        var request = new EnqueueWebUrlIngestionRequest(Urls: ["not-a-valid-url"]);

        var response = await _client.PostAsJsonAsync("/api/ingestion/jobs/urls", request);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task EnqueueRawText_ValidText_ReturnsAccepted()
    {
        await AuthenticateAsync(_client);

        var request = new EnqueueRawTextIngestionRequest(
            RawText: "Ingredients:\n- 1 cup oats\n- 2 cups milk\nInstructions:\nCook for 5 mins.",
            TitleHint: "Quick Oatmeal"
        );

        var response = await _client.PostAsJsonAsync("/api/ingestion/jobs/text", request);
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);

        var job = await response.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(job);
        Assert.AreEqual("Quick Oatmeal", job.DisplayTitle);
        Assert.AreEqual(IngestionChannel.RawText, job.Channel);
        Assert.AreEqual(IngestionJobStatus.Queued, job.Status);
    }

    [TestMethod]
    public async Task DraftSaveAndApprovePublish_CreatesRecipeAndNewFoodItem()
    {
        await AuthenticateAsync(_client);

        // 1. Enqueue text job
        var enqueue = await _client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(
                RawText: "Lemon Herb Tahini Sauce",
                TitleHint: "Tahini Sauce"
            )
        );
        var job = await enqueue.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(job);

        // 2. Save draft
        var uniqueFoodName = $"Sesame Paste {Guid.NewGuid():N}"[..20];
        var draft = new IngestionExtractedDraftDto(
            Title: "Creamy Lemon Tahini",
            Description: "Nutty tahini dressing.",
            BaseServings: 4,
            YieldDisplay: "1 cup",
            PrepTimeMinutes: 5,
            CookTimeMinutes: 0,
            TotalTimeMinutes: 5,
            Difficulty: RecipeDifficulty.Easy,
            SourceUrl: null,
            SourceName: null,
            SourceAuthor: null,
            CoverImagePath: null,
            ThumbnailImagePath: null,
            SuggestedCollectionIds: null,
            SuggestedTagIds: null,
            Ingredients:
            [
                new IngestionDraftIngredientDto(
                    SectionName: null,
                    Quantity: 0.5m,
                    MaxQuantity: null,
                    Unit: MeasurementUnit.Cup,
                    CustomUnitName: null,
                    MatchedFoodItemId: null,
                    MatchedFoodItemName: null,
                    IsNewFoodItem: true,
                    ProposedFoodItemName: uniqueFoodName,
                    SuggestedIsPantryStaple: false,
                    CreateNewFoodItem: true,
                    DisplayName: uniqueFoodName,
                    PreparationNote: null,
                    IsOptional: false,
                    OriginalRawText: $"1/2 cup {uniqueFoodName}"
                ),
            ],
            Steps:
            [
                new IngestionDraftStepDto(
                    SectionName: null,
                    Instruction: "Whisk ingredients together until smooth and creamy.",
                    IngredientIndices: [0],
                    Timers: null,
                    ImagePath: null
                ),
            ],
            ConfidenceScore: 0.95m
        );

        var saveDraftResp = await _client.PutAsJsonAsync(
            $"/api/ingestion/jobs/{job.Id}/draft",
            draft
        );
        Assert.AreEqual(HttpStatusCode.OK, saveDraftResp.StatusCode);

        var savedDetail = await saveDraftResp.Content.ReadFromJsonAsync<IngestionJobDetailDto>();
        Assert.IsNotNull(savedDetail);
        Assert.AreEqual("Creamy Lemon Tahini", savedDetail.DisplayTitle);
        Assert.IsNotNull(savedDetail.ExtractedDraft);
        Assert.AreEqual(1, savedDetail.ExtractedDraft.Ingredients?.Count);

        // 3. Approve and publish
        var approveResp = await _client.PostAsJsonAsync(
            $"/api/ingestion/jobs/{job.Id}/approve",
            draft
        );
        Assert.AreEqual(HttpStatusCode.Created, approveResp.StatusCode);

        var recipe = await approveResp.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(recipe);
        Assert.AreEqual("Creamy Lemon Tahini", recipe.Title);
        Assert.AreEqual(1, recipe.Ingredients.Count);
        Assert.IsNotNull(recipe.Ingredients[0].FoodItemId);
        Assert.AreEqual(uniqueFoodName, recipe.Ingredients[0].FoodItemName);

        // Verify job detail is now Published
        var jobDetailResp = await _client.GetAsync($"/api/ingestion/jobs/{job.Id}");
        var jobDetail = await jobDetailResp.Content.ReadFromJsonAsync<IngestionJobDetailDto>();
        Assert.IsNotNull(jobDetail);
        Assert.AreEqual(IngestionJobStatus.Published, jobDetail.Status);
        Assert.AreEqual(recipe.Id, jobDetail.ResultingRecipeId);

        // Approving again returns 409 Conflict
        var secondApprove = await _client.PostAsJsonAsync(
            $"/api/ingestion/jobs/{job.Id}/approve",
            draft
        );
        Assert.AreEqual(HttpStatusCode.Conflict, secondApprove.StatusCode);

        // 4. Discarding a published job removes it from the ingestion list while keeping the recipe intact
        var discardPublishedResp = await _client.DeleteAsync($"/api/ingestion/jobs/{job.Id}");
        Assert.AreEqual(HttpStatusCode.NoContent, discardPublishedResp.StatusCode);

        var listResp = await _client.GetAsync("/api/ingestion/jobs");
        var list = await listResp.Content.ReadFromJsonAsync<
            IReadOnlyList<IngestionJobSummaryDto>
        >();
        Assert.IsNotNull(list);
        Assert.IsFalse(list.Any(j => j.Id == job.Id));

        var recipeResp = await _client.GetAsync($"/api/recipes/{recipe.Id}");
        Assert.AreEqual(HttpStatusCode.OK, recipeResp.StatusCode);
    }

    [TestMethod]
    public async Task RetryWaitingAndDiscard_WorkCorrectly()
    {
        await AuthenticateAsync(_client);

        // Enqueue text job
        var enqueue = await _client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(RawText: "Quick Salad Recipe")
        );
        var job = await enqueue.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(job);

        // Discard job
        var discardResp = await _client.DeleteAsync($"/api/ingestion/jobs/{job.Id}");
        Assert.AreEqual(HttpStatusCode.NoContent, discardResp.StatusCode);

        // Verify marked discarded (excluded from default list)
        var listResp = await _client.GetAsync("/api/ingestion/jobs");
        var list = await listResp.Content.ReadFromJsonAsync<
            IReadOnlyList<IngestionJobSummaryDto>
        >();
        Assert.IsNotNull(list);
        Assert.IsFalse(list.Any(j => j.Id == job.Id));

        // Retry discarded job returns 409 Conflict
        var retryResp = await _client.PostAsync($"/api/ingestion/jobs/{job.Id}/retry", null);
        Assert.AreEqual(HttpStatusCode.Conflict, retryResp.StatusCode);
    }

    [TestMethod]
    public async Task RetryBatchJobs_RetriesAllSpecifiedJobs_RegardlessOfStatus()
    {
        await AuthenticateAsync(_client);

        // 1. Enqueue two recipes
        var enqueue1 = await _client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(RawText: "Recipe One Ingredients and Steps")
        );
        var job1 = await enqueue1.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(job1);

        var enqueue2 = await _client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(RawText: "Recipe Two Ingredients and Steps")
        );
        var job2 = await enqueue2.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(job2);

        // 2. Publish Job 1
        var draft1 = new IngestionExtractedDraftDto(
            Title: "Batch Pizza One",
            Ingredients:
            [
                new IngestionDraftIngredientDto(
                    DisplayName: "Flour",
                    MatchedFoodItemId: null,
                    ProposedFoodItemName: "Pizza Flour",
                    CreateNewFoodItem: true
                ),
            ],
            ExtractedModel: "llama3:8b"
        );
        await _client.PutAsJsonAsync($"/api/ingestion/jobs/{job1.Id}/draft", draft1);
        await _client.PostAsJsonAsync($"/api/ingestion/jobs/{job1.Id}/approve", draft1);

        // 3. Mark Job 2 as ReadyForReview
        var draft2 = new IngestionExtractedDraftDto(
            Title: "Batch Soup Two",
            Ingredients:
            [
                new IngestionDraftIngredientDto(
                    DisplayName: "Carrots",
                    MatchedFoodItemId: null,
                    ProposedFoodItemName: "Carrot",
                    CreateNewFoodItem: true
                ),
            ],
            ExtractedModel: "llama3:8b"
        );
        await _client.PutAsJsonAsync($"/api/ingestion/jobs/{job2.Id}/draft", draft2);

        // Verify statuses before batch retry
        var j1Before = await (
            await _client.GetAsync($"/api/ingestion/jobs/{job1.Id}")
        ).Content.ReadFromJsonAsync<IngestionJobDetailDto>();
        var j2Before = await (
            await _client.GetAsync($"/api/ingestion/jobs/{job2.Id}")
        ).Content.ReadFromJsonAsync<IngestionJobDetailDto>();
        Assert.AreEqual(IngestionJobStatus.Published, j1Before!.Status);
        Assert.AreEqual(IngestionJobStatus.Queued, j2Before!.Status);

        // 4. Call retry-batch with explicit job IDs
        var retryBatchResp = await _client.PostAsJsonAsync(
            "/api/ingestion/jobs/retry-batch",
            new RetryBatchJobsRequest(JobIds: [job1.Id, job2.Id])
        );
        Assert.AreEqual(HttpStatusCode.OK, retryBatchResp.StatusCode);
        var batchResult = await retryBatchResp.Content.ReadFromJsonAsync<RetryBatchJobsResultDto>();
        Assert.IsNotNull(batchResult);
        Assert.AreEqual(2, batchResult.RequeuedCount);

        // 5. Verify both jobs were re-queued
        var j1After = await (
            await _client.GetAsync($"/api/ingestion/jobs/{job1.Id}")
        ).Content.ReadFromJsonAsync<IngestionJobDetailDto>();
        var j2After = await (
            await _client.GetAsync($"/api/ingestion/jobs/{job2.Id}")
        ).Content.ReadFromJsonAsync<IngestionJobDetailDto>();
        Assert.AreEqual(IngestionJobStatus.Queued, j1After!.Status);
        Assert.AreEqual(IngestionJobStatus.Queued, j2After!.Status);
    }

    public async Task RerunPublishedJob_RetriesSuccessfully_AndUpdatesExistingRecipeWithModelTag()
    {
        await AuthenticateAsync(_client);

        // 1. Enqueue text job
        var enqueue = await _client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(RawText: "Original text recipe")
        );
        Assert.AreEqual(HttpStatusCode.Accepted, enqueue.StatusCode);
        var job = await enqueue.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(job);

        // 2. Save draft with initial model tag
        var initialDraft = new IngestionExtractedDraftDto(
            Title: "Initial Pizza Recipe",
            Ingredients:
            [
                new IngestionDraftIngredientDto(
                    DisplayName: "Flour",
                    MatchedFoodItemId: null,
                    ProposedFoodItemName: "Pizza Flour",
                    CreateNewFoodItem: true
                ),
            ],
            ExtractedModel: "llama3.2:latest"
        );

        var saveDraftResp = await _client.PutAsJsonAsync(
            $"/api/ingestion/jobs/{job.Id}/draft",
            initialDraft
        );
        Assert.AreEqual(HttpStatusCode.OK, saveDraftResp.StatusCode);

        // 3. Approve and publish initial recipe
        var approveResp = await _client.PostAsJsonAsync(
            $"/api/ingestion/jobs/{job.Id}/approve",
            initialDraft
        );
        Assert.AreEqual(HttpStatusCode.Created, approveResp.StatusCode);
        var recipe = await approveResp.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(recipe);
        Assert.AreEqual("Initial Pizza Recipe", recipe.Title);
        Assert.AreEqual("llama3.2:latest", recipe.ExtractedModel);
        Assert.AreEqual(job.Id, recipe.IngestionJobId);

        // Verify published job detail has ExtractedModel
        var jobDetailResp = await _client.GetAsync($"/api/ingestion/jobs/{job.Id}");
        var jobDetail = await jobDetailResp.Content.ReadFromJsonAsync<IngestionJobDetailDto>();
        Assert.IsNotNull(jobDetail);
        Assert.AreEqual(IngestionJobStatus.Published, jobDetail.Status);
        Assert.AreEqual("llama3.2:latest", jobDetail.ExtractedModel);
        Assert.AreEqual(recipe.Id, jobDetail.ResultingRecipeId);

        // 4. Rerun the published ingestion job!
        var retryResp = await _client.PostAsync($"/api/ingestion/jobs/{job.Id}/retry", null);
        Assert.AreEqual(HttpStatusCode.Accepted, retryResp.StatusCode);

        var retriedSummary = await retryResp.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(retriedSummary);
        Assert.AreEqual(IngestionJobStatus.Queued, retriedSummary.Status);
        Assert.AreEqual(recipe.Id, retriedSummary.ResultingRecipeId);

        // 5. Update draft with new model extraction (e.g. qwen2.5:14b)
        var updatedDraft = new IngestionExtractedDraftDto(
            Title: "Enhanced Sourdough Pizza",
            Ingredients:
            [
                new IngestionDraftIngredientDto(
                    DisplayName: "Organic 00 Flour",
                    MatchedFoodItemId: null,
                    ProposedFoodItemName: "Organic 00 Flour",
                    CreateNewFoodItem: true
                ),
                new IngestionDraftIngredientDto(
                    DisplayName: "Warm Water",
                    MatchedFoodItemId: null,
                    ProposedFoodItemName: "Water",
                    CreateNewFoodItem: true
                ),
            ],
            ExtractedModel: "qwen2.5:14b"
        );

        var saveSecondDraft = await _client.PutAsJsonAsync(
            $"/api/ingestion/jobs/{job.Id}/draft",
            updatedDraft
        );
        Assert.AreEqual(HttpStatusCode.OK, saveSecondDraft.StatusCode);

        // 6. Approve again -> should UPDATE the existing recipe, NOT create a second recipe!
        var approveSecondResp = await _client.PostAsJsonAsync(
            $"/api/ingestion/jobs/{job.Id}/approve",
            updatedDraft
        );
        Assert.AreEqual(HttpStatusCode.Created, approveSecondResp.StatusCode);
        var updatedRecipe = await approveSecondResp.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(updatedRecipe);
        Assert.AreEqual(recipe.Id, updatedRecipe.Id); // Same recipe ID!
        Assert.AreEqual("Enhanced Sourdough Pizza", updatedRecipe.Title);
        Assert.AreEqual("qwen2.5:14b", updatedRecipe.ExtractedModel);

        // Verify recipe detail endpoint shows updated model and ingestion job ID
        var getRecipeResp = await _client.GetAsync($"/api/recipes/{recipe.Id}");
        Assert.AreEqual(HttpStatusCode.OK, getRecipeResp.StatusCode);
        var fetchedRecipe = await getRecipeResp.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(fetchedRecipe);
        Assert.AreEqual("Enhanced Sourdough Pizza", fetchedRecipe.Title);
        Assert.AreEqual("qwen2.5:14b", fetchedRecipe.ExtractedModel);
        Assert.AreEqual(job.Id, fetchedRecipe.IngestionJobId);
    }

    [TestMethod]
    public async Task EnqueueRawText_InvalidCoverImagePath_ReturnsBadRequest()
    {
        await AuthenticateAsync(_client);

        var response = await _client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(
                RawText: "Title: Test\nIngredients: 1 cup water",
                CoverImagePath: "https://external.example.com/cover.jpg"
            )
        );
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task SequentialRawTextQueueOnEmptyApp_AutoCreatesAndReusesFoodItemsTagsAndPreservesBookMetadata()
    {
        var capturedContexts = new List<LlmExtractionContext>();
        var fakeLlm = new FakeSequentialLlmExtractionClient(ctx =>
        {
            capturedContexts.Add(ctx);
            if (ctx.SourceContent.Contains("Classic Roast Chicken", StringComparison.Ordinal))
            {
                return new LlmExtractedRecipePayload(
                    Title: "Classic Roast Chicken",
                    Description: "Crisp-skinned roast chicken.",
                    BaseServings: 4m,
                    YieldDisplay: "Serves 4",
                    PrepTimeMinutes: 15,
                    CookTimeMinutes: 60,
                    TotalTimeMinutes: 75,
                    Difficulty: "Medium",
                    SourceName: null,
                    SourceAuthor: null,
                    ConfidenceScore: 0.94m,
                    SuggestedTagNames: ["Dinner", "American", "Weeknight"],
                    Ingredients:
                    [
                        new LlmExtractedIngredientItem(
                            SectionName: null,
                            Quantity: 1m,
                            MaxQuantity: null,
                            Unit: "whole",
                            CanonicalFoodName: "whole chicken",
                            DisplayName: "whole broiler-fryer chicken",
                            PreparationNote: "giblets removed",
                            IsOptional: false,
                            OriginalRawText: "1 (4-pound) whole chicken",
                            SuggestedIsPantryStaple: false
                        ),
                        new LlmExtractedIngredientItem(
                            SectionName: null,
                            Quantity: 1m,
                            MaxQuantity: null,
                            Unit: "tbsp",
                            CanonicalFoodName: "kosher salt",
                            DisplayName: "kosher salt",
                            PreparationNote: null,
                            IsOptional: false,
                            OriginalRawText: "1 tablespoon kosher salt",
                            SuggestedIsPantryStaple: true
                        ),
                    ],
                    Steps:
                    [
                        new LlmExtractedStepItem(
                            SectionName: null,
                            Instruction: "Roast chicken for 60 minutes.",
                            IngredientIndices: [0, 1],
                            Timers: [new LlmExtractedTimerItem(3600, "Roast")]
                        ),
                    ]
                );
            }

            return new LlmExtractedRecipePayload(
                Title: "Skillet Lemon Chicken",
                Description: "Bright weeknight skillet chicken.",
                BaseServings: 4m,
                YieldDisplay: "Serves 4",
                PrepTimeMinutes: 10,
                CookTimeMinutes: 20,
                TotalTimeMinutes: 30,
                Difficulty: "Easy",
                SourceName: null,
                SourceAuthor: null,
                ConfidenceScore: 0.92m,
                SuggestedTagNames: ["Dinner", "Weeknight", "Mediterranean"],
                Ingredients:
                [
                    new LlmExtractedIngredientItem(
                        SectionName: null,
                        Quantity: 1m,
                        MaxQuantity: null,
                        Unit: "tsp",
                        CanonicalFoodName: "Kosher Salt",
                        DisplayName: "kosher salt",
                        PreparationNote: null,
                        IsOptional: false,
                        OriginalRawText: "1 teaspoon kosher salt",
                        SuggestedIsPantryStaple: true
                    ),
                    new LlmExtractedIngredientItem(
                        SectionName: null,
                        Quantity: 1m,
                        MaxQuantity: null,
                        Unit: "whole",
                        CanonicalFoodName: "lemon",
                        DisplayName: "lemon, sliced",
                        PreparationNote: "sliced",
                        IsOptional: false,
                        OriginalRawText: "1 lemon, sliced",
                        SuggestedIsPantryStaple: false
                    ),
                ],
                Steps:
                [
                    new LlmExtractedStepItem(
                        SectionName: null,
                        Instruction: "Sear with salt and lemon for 10 minutes.",
                        IngredientIndices: [0, 1],
                        Timers: [new LlmExtractedTimerItem(600, "Sear")]
                    ),
                ]
            );
        });

        await using var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Llm:TextModel", "qwen2.5:14b");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILlmExtractionClient>();
                services.AddSingleton<ILlmExtractionClient>(fakeLlm);
            });
        });

        using var client = customFactory.CreateClient();
        await AuthenticateAsync(client);

        // Create chapter collection (just like import_atk_epub.py does on an empty app)
        var colResp = await client.PostAsJsonAsync(
            "/api/collections",
            new UpsertCollectionRequest("Poultry", "ATK Chapter", 1)
        );
        Assert.AreEqual(HttpStatusCode.Created, colResp.StatusCode);
        var poultryCol = await colResp.Content.ReadFromJsonAsync<CollectionDto>();
        Assert.IsNotNull(poultryCol);

        // Enqueue Recipe #1 and Recipe #2 with AutoPublish = true (even though global AutoPublishHighConfidenceText is false)
        var job1Resp = await client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(
                RawText: "Title: Classic Roast Chicken\nIngredients:\n- 1 whole chicken\n- 1 tbsp kosher salt",
                TitleHint: "Classic Roast Chicken",
                SourceName: "America's Test Kitchen",
                SourceAuthor: "America's Test Kitchen",
                SourceBookTitle: "The Complete America's Test Kitchen TV Show Cookbook",
                SourceBookPage: 142,
                CoverImagePath: "/api/uploads/recipes/roast-chicken.webp",
                ThumbnailImagePath: "/api/uploads/recipes/roast-chicken-thumb.webp",
                CollectionIds: [poultryCol.Id],
                AutoPublish: true
            )
        );
        Assert.AreEqual(HttpStatusCode.Accepted, job1Resp.StatusCode);
        var job1 = await job1Resp.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(job1);

        var job2Resp = await client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(
                RawText: "Title: Skillet Lemon Chicken\nIngredients:\n- 1 tsp kosher salt\n- 1 lemon",
                TitleHint: "Skillet Lemon Chicken",
                SourceName: "America's Test Kitchen",
                SourceAuthor: "America's Test Kitchen",
                SourceBookTitle: "The Complete America's Test Kitchen TV Show Cookbook",
                SourceBookPage: 155,
                CollectionIds: [poultryCol.Id],
                AutoPublish: true
            )
        );
        Assert.AreEqual(HttpStatusCode.Accepted, job2Resp.StatusCode);
        var job2 = await job2Resp.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(job2);

        // Process Job #1 then Job #2 sequentially
        using (var scope1 = customFactory.Services.CreateScope())
        {
            var processor1 = scope1.ServiceProvider.GetRequiredService<IIngestionProcessor>();
            Assert.IsTrue(await processor1.ProcessNextQueuedJobAsync());
        }
        using (var scope2 = customFactory.Services.CreateScope())
        {
            var processor2 = scope2.ServiceProvider.GetRequiredService<IIngestionProcessor>();
            Assert.IsTrue(await processor2.ProcessNextQueuedJobAsync());
        }

        // Verify Job #1 ran with empty catalogs, and Job #2 received Job #1's newly created tags & food items!
        Assert.AreEqual(2, capturedContexts.Count);
        Assert.AreEqual(0, capturedContexts[0].AvailableTagNames.Count);
        Assert.AreEqual(0, capturedContexts[0].AvailableFoodNames?.Count ?? 0);

        CollectionAssert.AreEquivalent(
            new[] { "Dinner", "American", "Weeknight" },
            capturedContexts[1].AvailableTagNames.ToArray()
        );
        CollectionAssert.AreEquivalent(
            new[] { "Whole Chicken", "Kosher Salt" },
            capturedContexts[1].AvailableFoodNames!.ToArray()
        );

        // Verify FoodItems catalog has exactly 3 deduplicated items (Kosher Salt reused, marked as pantry staple)
        var foods = await client.GetFromJsonAsync<IReadOnlyList<FoodItemDto>>("/api/food-items");
        Assert.IsNotNull(foods);
        Assert.AreEqual(3, foods.Count);
        var salt = foods.Single(f => f.Name == "Kosher Salt");
        Assert.IsTrue(salt.IsPantryStaple);

        // Verify Tags catalog has exactly 4 deduplicated tags with inferred categories
        var allTags = await client.GetFromJsonAsync<IReadOnlyList<TagDto>>("/api/tags");
        Assert.IsNotNull(allTags);
        Assert.AreEqual(4, allTags.Count);
        Assert.AreEqual(TagCategory.Course, allTags.Single(t => t.Name == "Dinner").Category);
        Assert.AreEqual(TagCategory.Cuisine, allTags.Single(t => t.Name == "American").Category);
        Assert.AreEqual(
            TagCategory.Cuisine,
            allTags.Single(t => t.Name == "Mediterranean").Category
        );
        Assert.AreEqual(TagCategory.Custom, allTags.Single(t => t.Name == "Weeknight").Category);

        // Verify Recipe #1 preserved book metadata, cover image, chapter collection, and auto-created tags/foods
        var job1Detail = await client.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{job1.Id}"
        );
        Assert.IsNotNull(job1Detail?.ResultingRecipeId);

        var recipe1 = await client.GetFromJsonAsync<RecipeDetailDto>(
            $"/api/recipes/{job1Detail.ResultingRecipeId}"
        );
        Assert.IsNotNull(recipe1);
        Assert.AreEqual(
            "The Complete America's Test Kitchen TV Show Cookbook",
            recipe1.SourceBookTitle
        );
        Assert.AreEqual(142, recipe1.SourceBookPage);
        Assert.AreEqual("America's Test Kitchen", recipe1.SourceName);
        Assert.AreEqual("/api/uploads/recipes/roast-chicken.webp", recipe1.CoverImagePath);
        Assert.AreEqual(1, recipe1.Collections.Count);
        Assert.AreEqual("Poultry", recipe1.Collections[0].Name);
        Assert.AreEqual(3, recipe1.Tags.Count);
        Assert.AreEqual(1, recipe1.Attachments.Count);
    }

    [TestMethod]
    public async Task ConcurrentJobProcessing_UsesSingleLlmRequestAtATime()
    {
        var fakeLlm = new BlockingLlmExtractionClient();
        await using var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Llm:TextModel", "test-model");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILlmExtractionClient>();
                services.AddSingleton<ILlmExtractionClient>(fakeLlm);
            });
        });

        using var client = customFactory.CreateClient();
        await AuthenticateAsync(client);

        var firstJob = await EnqueueRawTextJobAsync(client, "Serialized Job One");
        var secondJob = await EnqueueRawTextJobAsync(client, "Serialized Job Two");

        using var firstScope = customFactory.Services.CreateScope();
        using var secondScope = customFactory.Services.CreateScope();
        var firstProcessor = firstScope.ServiceProvider.GetRequiredService<IIngestionProcessor>();
        var secondProcessor = secondScope.ServiceProvider.GetRequiredService<IIngestionProcessor>();

        var firstProcessing = firstProcessor.ProcessJobByIdAsync(firstJob.Id);
        await fakeLlm.FirstCallStarted.WaitAsync(TimeSpan.FromSeconds(10));
        var secondProcessing = secondProcessor.ProcessJobByIdAsync(secondJob.Id);

        await Task.Delay(100);
        Assert.AreEqual(1, fakeLlm.ExtractionCallCount);
        Assert.AreEqual(1, fakeLlm.MaxConcurrentRequests);

        fakeLlm.ReleaseFirstCall();
        Assert.IsTrue(await firstProcessing.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsTrue(await secondProcessing.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(2, fakeLlm.ExtractionCallCount);
        Assert.AreEqual(1, fakeLlm.MaxConcurrentRequests);
    }

    private static async Task<IngestionJobSummaryDto> EnqueueRawTextJobAsync(
        HttpClient client,
        string title
    )
    {
        var response = await client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(
                RawText: $"Title: {title}\nIngredients:\n- 1 cup flour\nInstructions:\n1. Mix the flour thoroughly and bake until cooked.",
                TitleHint: title
            )
        );
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);

        var job = await response.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(job);
        return job;
    }

    private sealed class FakeSequentialLlmExtractionClient(
        Func<LlmExtractionContext, LlmExtractedRecipePayload> handler
    ) : ILlmExtractionClient
    {
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
                    ["qwen2.5:14b"],
                    null
                )
            );

        public Task<LlmExtractedRecipePayload> ExtractRecipeFromTextAsync(
            EffectiveLlmConnectionConfig config,
            LlmExtractionContext context,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(handler(context));

        public Task<LlmExtractedRecipePayload> ExtractRecipeFromImagesAsync(
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

    private sealed class BlockingLlmExtractionClient : ILlmExtractionClient
    {
        private readonly TaskCompletionSource<bool> _firstCallStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource<bool> _releaseFirstCall = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _activeRequests;
        private int _maxConcurrentRequests;
        private int _extractionCallCount;

        public Task FirstCallStarted => _firstCallStarted.Task;
        public int ExtractionCallCount => Volatile.Read(ref _extractionCallCount);
        public int MaxConcurrentRequests => Volatile.Read(ref _maxConcurrentRequests);

        public void ReleaseFirstCall() => _releaseFirstCall.TrySetResult(true);

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
                    ["test-model"],
                    null
                )
            );

        public async Task<LlmExtractedRecipePayload> ExtractRecipeFromTextAsync(
            EffectiveLlmConnectionConfig config,
            LlmExtractionContext context,
            CancellationToken cancellationToken = default
        )
        {
            var call = Interlocked.Increment(ref _extractionCallCount);
            var active = Interlocked.Increment(ref _activeRequests);
            UpdateMaximum(active);
            try
            {
                if (call == 1)
                {
                    _firstCallStarted.TrySetResult(true);
                    await _releaseFirstCall.Task.WaitAsync(cancellationToken);
                }

                return new LlmExtractedRecipePayload(
                    Title: context.SourceContent.Contains(
                        "Serialized Job One",
                        StringComparison.Ordinal
                    )
                        ? "Serialized Job One"
                        : "Serialized Job Two",
                    ConfidenceScore: 0.8m,
                    Ingredients:
                    [
                        new LlmExtractedIngredientItem(
                            SectionName: null,
                            Quantity: 1m,
                            MaxQuantity: null,
                            Unit: "cup",
                            CanonicalFoodName: "flour",
                            DisplayName: "flour",
                            OriginalRawText: "1 cup flour"
                        ),
                    ],
                    Steps: [new LlmExtractedStepItem(null, "Mix and bake.", [0])]
                );
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequests);
            }
        }

        public Task<LlmExtractedRecipePayload> ExtractRecipeFromImagesAsync(
            EffectiveLlmConnectionConfig config,
            LlmVisionExtractionContext context,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<LlmSubstitutionPayload> SuggestIngredientSubstitutionsAsync(
            EffectiveLlmConnectionConfig config,
            LlmSubstitutionContext context,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<LlmDraftMealPlanPayload> DraftWeeklyMealPlanAsync(
            EffectiveLlmConnectionConfig config,
            LlmDraftMealPlanContext context,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        private void UpdateMaximum(int active)
        {
            int currentMaximum;
            do
            {
                currentMaximum = Volatile.Read(ref _maxConcurrentRequests);
                if (active <= currentMaximum)
                {
                    return;
                }
            } while (
                Interlocked.CompareExchange(ref _maxConcurrentRequests, active, currentMaximum)
                != currentMaximum
            );
        }
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using App.Core.Authentication;
using App.Core.FoodCatalog;
using App.Core.Ingestion;
using App.Core.Recipes;
using App.Data.Entities;
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

    private static IngestionExtractedDraftDto CreateRecipeReferenceDraft(
        string title,
        string referencedRecipeTitle
    ) =>
        new(
            Title: title,
            Ingredients:
            [
                new IngestionDraftIngredientDto(
                    Quantity: 1m,
                    Unit: MeasurementUnit.Custom,
                    CustomUnitName: "recipe",
                    CreateNewFoodItem: false,
                    DisplayName: referencedRecipeTitle,
                    OriginalRawText: $"1 recipe {referencedRecipeTitle} (this page)",
                    IsRecipeReference: true,
                    ReferencedRecipeTitle: referencedRecipeTitle
                ),
            ],
            Steps: []
        );

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

        var draft = new IngestionExtractedDraftDto("Sample");
        var diagnostics = await _client.PostAsJsonAsync(
            $"/api/ingestion/jobs/{Guid.NewGuid()}/review-diagnostics",
            new IngestionDraftDiagnosticsRequest(draft)
        );
        Assert.AreEqual(HttpStatusCode.Unauthorized, diagnostics.StatusCode);
        var repair = await _client.PostAsJsonAsync(
            $"/api/ingestion/jobs/{Guid.NewGuid()}/repair-preview",
            new IngestionRepairPreviewRequest(draft)
        );
        Assert.AreEqual(HttpStatusCode.Unauthorized, repair.StatusCode);
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
                    OriginalRawText: $"1/2 cup {uniqueFoodName}",
                    SuggestedCategory: FoodCategory.CondimentsAndSauces
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

        var createdFood = await _client.GetFromJsonAsync<FoodItemDto>(
            $"/api/food-items/{recipe.Ingredients[0].FoodItemId}"
        );
        Assert.AreEqual(FoodCategory.CondimentsAndSauces, createdFood?.Category);

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
    public async Task DraftApprove_PublishesInlineAmountsWithIngredientIds()
    {
        await AuthenticateAsync(_client);
        var job = await EnqueueRawTextJobAsync(_client, "Inline Roux");
        const string instruction = "Melt 2 tbsp butter, then whisk in 1/4 cup flour to a paste.";
        var butterStart = instruction.IndexOf("2 tbsp", StringComparison.Ordinal);
        var flourStart = instruction.IndexOf("1/4 cup", StringComparison.Ordinal);
        var draft = new IngestionExtractedDraftDto(
            Title: "Inline Roux",
            Ingredients:
            [
                new IngestionDraftIngredientDto(
                    Quantity: 0.25m,
                    Unit: MeasurementUnit.Cup,
                    CreateNewFoodItem: false,
                    DisplayName: "flour",
                    OriginalRawText: "1/4 cup flour"
                ),
                new IngestionDraftIngredientDto(
                    Quantity: 2,
                    Unit: MeasurementUnit.Tbsp,
                    CreateNewFoodItem: false,
                    DisplayName: "butter",
                    OriginalRawText: "2 tablespoons butter"
                ),
            ],
            Steps:
            [
                new IngestionDraftStepDto(
                    Instruction: instruction,
                    InlineAmounts:
                    [
                        new(
                            butterStart,
                            "2 tbsp".Length,
                            InlineStepAmountKind.Ingredient,
                            1,
                            new(2, MeasurementUnit.Tbsp)
                        ),
                        new(
                            flourStart,
                            "1/4 cup".Length,
                            InlineStepAmountKind.Ingredient,
                            0,
                            new(0.25m, MeasurementUnit.Cup)
                        ),
                    ]
                ),
            ]
        );

        var approveResp = await _client.PostAsJsonAsync(
            $"/api/ingestion/jobs/{job.Id}/approve",
            draft
        );
        Assert.AreEqual(
            HttpStatusCode.Created,
            approveResp.StatusCode,
            await approveResp.Content.ReadAsStringAsync()
        );
        var published = await approveResp.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(published);

        var recipe = await _client.GetFromJsonAsync<RecipeDetailDto>(
            $"/api/recipes/{published.Id}"
        );
        Assert.IsNotNull(recipe);
        var flour = recipe.Ingredients.Single(i => i.DisplayName == "flour");
        var butter = recipe.Ingredients.Single(i => i.DisplayName == "butter");
        var step = recipe.Steps.Single();
        Assert.AreEqual(instruction, step.Instruction);
        var spans = step.InlineAmounts!;
        Assert.HasCount(2, spans);
        Assert.AreEqual(butter.Id, spans[0].IngredientId);
        Assert.AreEqual(butterStart, spans[0].Start);
        Assert.AreEqual(flour.Id, spans[1].IngredientId);
        Assert.AreEqual(0.25m, spans[1].Measurement.Quantity);
        CollectionAssert.AreEquivalent(new[] { flour.Id, butter.Id }, step.IngredientIds.ToArray());
    }

    [TestMethod]
    public async Task DraftPublish_RecipeReferenceExactTitleLinksSavedRecipe()
    {
        await AuthenticateAsync(_client);

        const string referencedRecipeTitle = "Foolproof All-Butter Dough";
        var targetResponse = await _client.PostAsJsonAsync(
            "/api/recipes",
            new UpsertRecipeRequest(Title: referencedRecipeTitle)
        );
        Assert.AreEqual(HttpStatusCode.Created, targetResponse.StatusCode);
        var targetRecipe = await targetResponse.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(targetRecipe);

        var jobResponse = await _client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest("Blueberry Pie", TitleHint: "Blueberry Pie")
        );
        var job = await jobResponse.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(job);

        var draft = CreateRecipeReferenceDraft("Blueberry Pie", referencedRecipeTitle);
        var saveResponse = await _client.PutAsJsonAsync(
            $"/api/ingestion/jobs/{job.Id}/draft",
            draft
        );
        Assert.AreEqual(HttpStatusCode.OK, saveResponse.StatusCode);
        var savedJob = await saveResponse.Content.ReadFromJsonAsync<IngestionJobDetailDto>();
        Assert.IsNotNull(savedJob?.ExtractedDraft?.Ingredients);
        Assert.IsTrue(savedJob.ExtractedDraft.Ingredients[0].IsRecipeReference);
        Assert.AreEqual(
            referencedRecipeTitle,
            savedJob.ExtractedDraft.Ingredients[0].ReferencedRecipeTitle
        );

        var publishResponse = await _client.PostAsJsonAsync(
            $"/api/ingestion/jobs/{job.Id}/approve",
            draft
        );
        Assert.AreEqual(HttpStatusCode.Created, publishResponse.StatusCode);
        var publishedRecipe = await publishResponse.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(publishedRecipe);
        Assert.AreEqual(targetRecipe.Id, publishedRecipe.Ingredients[0].LinkedRecipeId);
        Assert.IsNull(publishedRecipe.Ingredients[0].FoodItemId);
        Assert.IsNull(publishedRecipe.Ingredients[0].UnresolvedRecipeTitle);
    }

    [TestMethod]
    public async Task DraftPublish_UnknownRecipeReferenceRemainsUnlinkedAndIsNotCreatedAsFood()
    {
        await AuthenticateAsync(_client);

        const string referencedRecipeTitle = "Foolproof All-Butter Dough";
        var jobResponse = await _client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest("Blueberry Pie", TitleHint: "Blueberry Pie")
        );
        var job = await jobResponse.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(job);

        var draft = CreateRecipeReferenceDraft("Blueberry Pie", referencedRecipeTitle);
        var saveResponse = await _client.PutAsJsonAsync(
            $"/api/ingestion/jobs/{job.Id}/draft",
            draft
        );
        Assert.AreEqual(HttpStatusCode.OK, saveResponse.StatusCode);

        var publishResponse = await _client.PostAsJsonAsync(
            $"/api/ingestion/jobs/{job.Id}/approve",
            draft
        );
        Assert.AreEqual(HttpStatusCode.Created, publishResponse.StatusCode);
        var publishedRecipe = await publishResponse.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(publishedRecipe);
        Assert.AreEqual(referencedRecipeTitle, publishedRecipe.Ingredients[0].DisplayName);
        Assert.IsNull(publishedRecipe.Ingredients[0].LinkedRecipeId);
        Assert.IsNull(publishedRecipe.Ingredients[0].FoodItemId);
        Assert.AreEqual(
            referencedRecipeTitle,
            publishedRecipe.Ingredients[0].UnresolvedRecipeTitle
        );

        var foodItemsResponse = await _client.GetAsync("/api/food-items");
        Assert.AreEqual(HttpStatusCode.OK, foodItemsResponse.StatusCode);
        var foodItems = await foodItemsResponse.Content.ReadFromJsonAsync<
            IReadOnlyList<FoodItemDto>
        >();
        Assert.IsNotNull(foodItems);
        Assert.IsFalse(foodItems.Any(item => item.Name == referencedRecipeTitle));
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
                        DisplayName: "lemon",
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
                RawText: "Title: Classic Roast Chicken\nIngredients:\n- 1 (4-pound) whole chicken\n- 1 tablespoon kosher salt",
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
                RawText: "Title: Skillet Lemon Chicken\nIngredients:\n- 1 teaspoon kosher salt\n- 1 lemon, sliced",
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

    [TestMethod]
    public async Task RunNow_ProcessesOnlyFlaggedJobWhilePaused_AndPauseClearsRunNext()
    {
        var extractedTitles = new List<string>();
        var fakeLlm = new FakeSequentialLlmExtractionClient(context =>
        {
            var title = context.SourceContent.Contains("Run Now Chosen Job")
                ? "Run Now Chosen Job"
                : "Run Now Held Job";
            extractedTitles.Add(title);
            return new LlmExtractedRecipePayload(Title: title, ConfidenceScore: 0.5m);
        });
        await using var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Llm:TextModel", "test-text");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILlmExtractionClient>();
                services.AddSingleton<ILlmExtractionClient>(fakeLlm);
            });
        });
        using var client = customFactory.CreateClient();
        await AuthenticateAsync(client);

        async Task<bool> ProcessNextAsync()
        {
            using var scope = customFactory.Services.CreateScope();
            return await scope
                .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                .ProcessNextQueuedJobAsync();
        }

        async Task<IngestionJobStatus> StatusOfAsync(Guid id) =>
            (
                await client.GetFromJsonAsync<IngestionJobDetailDto>($"/api/ingestion/jobs/{id}")
            )!.Status;

        var held = await EnqueueRawTextJobAsync(client, "Run Now Held Job");
        var chosen = await EnqueueRawTextJobAsync(client, "Run Now Chosen Job");

        // No settings saved yet: pausing must keep the configured Llm:TextModel default.
        (
            await client.PostAsync("/api/llm-settings/set-ingestion-enabled?enabled=false", null)
        ).EnsureSuccessStatusCode();
        try
        {
            Assert.IsFalse(await ProcessNextAsync());

            var runNow = await client.PostAsync($"/api/ingestion/jobs/{chosen.Id}/run-now", null);
            Assert.AreEqual(HttpStatusCode.Accepted, runNow.StatusCode);
            var runNowSummary = await runNow.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
            Assert.AreEqual(IngestionJobStatus.Queued, runNowSummary!.Status);
            StringAssert.Contains(runNowSummary.StatusDetail, "even while ingestion is paused");

            Assert.IsTrue(await ProcessNextAsync());
            CollectionAssert.Contains(extractedTitles, "Run Now Chosen Job");
            CollectionAssert.DoesNotContain(extractedTitles, "Run Now Held Job");
            Assert.AreEqual(IngestionJobStatus.ReadyForReview, await StatusOfAsync(chosen.Id));
            Assert.AreEqual(IngestionJobStatus.Queued, await StatusOfAsync(held.Id));
            Assert.IsFalse(await ProcessNextAsync());

            var conflict = await client.PostAsync($"/api/ingestion/jobs/{chosen.Id}/run-now", null);
            Assert.AreEqual(HttpStatusCode.Conflict, conflict.StatusCode);
            var missing = await client.PostAsync(
                $"/api/ingestion/jobs/{Guid.NewGuid()}/run-now",
                null
            );
            Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);

            // A "run next" request made while enabled is dropped when ingestion is paused.
            (
                await client.PostAsync("/api/llm-settings/set-ingestion-enabled?enabled=true", null)
            ).EnsureSuccessStatusCode();
            (
                await client.PostAsync($"/api/ingestion/jobs/{held.Id}/run-now", null)
            ).EnsureSuccessStatusCode();
            (
                await client.PostAsync(
                    "/api/llm-settings/set-ingestion-enabled?enabled=false",
                    null
                )
            ).EnsureSuccessStatusCode();
            Assert.IsFalse(await ProcessNextAsync());
            Assert.AreEqual(IngestionJobStatus.Queued, await StatusOfAsync(held.Id));
        }
        finally
        {
            (
                await client.PostAsync("/api/llm-settings/set-ingestion-enabled?enabled=true", null)
            ).EnsureSuccessStatusCode();
        }
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

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StarterIngestion_PreservesMeasurementsAndRequiresReviewForUnverifiedAmounts(
        bool invalidAmount
    )
    {
        var cups = invalidAmount ? 6 : 1;
        var mixStep = $"Mix {cups} cup{(cups == 1 ? "" : "s")} bread flour.";
        var amountLength = mixStep.IndexOf(" bread", StringComparison.Ordinal) - 4;
        var fakeLlm = new FakeSequentialLlmExtractionClient(_ => new LlmExtractedRecipePayload(
            Title: "Synthetic Starter",
            ConfidenceScore: 0.99m,
            Ingredients:
            [
                new(
                    null,
                    25,
                    null,
                    "oz",
                    "bread flour",
                    DisplayName: "bread flour",
                    OriginalRawText: "5 cups (25 ounces) bread flour",
                    AlternativeMeasurement: new(5, "cup")
                ),
            ],
            Steps:
            [
                new(
                    null,
                    mixStep,
                    [0],
                    IngredientAmounts: [new(0, new(cups, "cup"))],
                    InlineAmounts:
                    [
                        new(
                            4,
                            amountLength,
                            mixStep.Substring(4, amountLength),
                            InlineStepAmountKind.Ingredient,
                            0,
                            new(cups, MeasurementUnit.Cup)
                        ),
                    ]
                ),
                new(null, "Knead in the remaining flour.", [0]),
            ]
        ));
        await using var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Llm:TextModel", "test-text");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILlmExtractionClient>();
                services.AddSingleton<ILlmExtractionClient>(fakeLlm);
            });
        });
        using var client = customFactory.CreateClient();
        await AuthenticateAsync(client);
        var queuedResponse = await client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(
                $"Synthetic Starter\nIngredients:\n- 5 cups (25 ounces) bread flour\nInstructions:\n1. {mixStep}\n2. Knead in the remaining flour.",
                AutoPublish: true
            )
        );
        queuedResponse.EnsureSuccessStatusCode();
        var queued = await queuedResponse.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(queued);
        using (var scope = customFactory.Services.CreateScope())
            Assert.IsTrue(
                await scope
                    .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                    .ProcessNextQueuedJobAsync()
            );
        var job = await client.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{queued.Id}"
        );
        Assert.IsNotNull(job);
        Assert.AreEqual(
            invalidAmount ? IngestionJobStatus.ReadyForReview : IngestionJobStatus.Published,
            job.Status
        );
        Assert.AreEqual(5m, job.ExtractedDraft!.Ingredients![0].Quantity);
        Assert.AreEqual(25m, job.ExtractedDraft.Ingredients[0].AlternativeMeasurement!.Quantity);
        if (invalidAmount)
        {
            StringAssert.Contains(job.StatusDetail, "exceed the ingredient list");
            Assert.IsNull(job.ResultingRecipeId);
        }
        else
        {
            var recipe = await client.GetFromJsonAsync<RecipeDetailDto>(
                $"/api/recipes/{job.ResultingRecipeId}"
            );
            Assert.IsNotNull(recipe);
            var flour = recipe.Ingredients[0];
            Assert.AreEqual(25m, flour.AlternativeMeasurement!.Quantity);
            Assert.AreEqual(1m, recipe.Steps[0].IngredientAmounts![0].Measurement.Quantity);
            Assert.AreEqual(mixStep, recipe.Steps[0].Instruction);
            var inline = recipe.Steps[0].InlineAmounts!.Single();
            Assert.AreEqual(4, inline.Start);
            Assert.AreEqual("1 cup", mixStep.Substring(inline.Start, inline.Length));
            Assert.AreEqual(InlineStepAmountKind.Ingredient, inline.Kind);
            Assert.AreEqual(flour.Id, inline.IngredientId);
        }
    }

    [TestMethod]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, false, true)]
    [DataRow(false, true, true)]
    public async Task InvalidHighConfidenceExtraction_RequiresReviewEvenWhenAutoPublishRequested(
        bool jobAutoPublish,
        bool globalAutoPublish,
        bool invalidFoodName
    )
    {
        var fakeLlm = new FakeSequentialLlmExtractionClient(_ => new LlmExtractedRecipePayload(
            Title: "Oil sauce",
            ConfidenceScore: 0.99m,
            Ingredients:
            [
                new(
                    null,
                    4,
                    null,
                    "tbsp",
                    invalidFoodName ? "olive oil or butter" : "olive oil",
                    "olive oil",
                    OriginalRawText: "4 tablespoons olive oil"
                ),
                new(null, null, null, null, "salt", "salt", OriginalRawText: "Salt"),
            ],
            Steps:
            [
                invalidFoodName
                    ? new(null, "Heat the oil and season with salt.", [0, 1])
                    : new(
                        null,
                        "Heat 6 tablespoons oil and season with salt.",
                        [0, 1],
                        IngredientAmounts: [new(0, new(6, "tbsp"))]
                    ),
            ]
        ));
        await using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Llm:TextModel", "gemma4:e4b");
            builder.UseSetting("Llm:AutoPublishHighConfidenceText", globalAutoPublish.ToString());
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILlmExtractionClient>();
                services.AddSingleton<ILlmExtractionClient>(fakeLlm);
            });
        });
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);
        var response = await client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(
                "Ingredients:\n- 4 tablespoons olive oil\n- Salt\nInstructions:\nHeat oil and season.",
                AutoPublish: jobAutoPublish
            )
        );
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        var job = await response.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(job);
        using var scope = factory.Services.CreateScope();
        Assert.IsTrue(
            await scope
                .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                .ProcessNextQueuedJobAsync()
        );
        var detail = await client.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{job.Id}"
        );
        Assert.IsNotNull(detail);
        Assert.AreEqual(IngestionJobStatus.ReadyForReview, detail.Status);
        Assert.AreEqual(0.59m, detail.ConfidenceScore);
        StringAssert.Contains(
            detail.StatusDetail,
            invalidFoodName ? "out of food names" : "exceed the ingredient list"
        );
        Assert.IsNull(detail.ResultingRecipeId);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TextIngestion_SectionsUnstructuredSourceBeforeExtractionAndValidation(
        bool canonical
    )
    {
        const string structured =
            "Title: Plain Toast\n\nIngredients:\n- 2 slices bread\n- 1 tbsp butter\n\nInstructions:\nToast the bread and spread the butter.";
        var source = canonical
            ? structured
            : "Plain Toast\n2 slices bread\n1 tbsp butter\nToast the bread and spread the butter.";
        var extractionSources = new List<string>();
        var fakeLlm = new FakeSequentialLlmExtractionClient(
            context =>
            {
                extractionSources.Add(context.SourceContent);
                return new LlmExtractedRecipePayload(
                    Title: "Plain Toast",
                    ConfidenceScore: 0.99m,
                    Ingredients:
                    [
                        new(
                            null,
                            2,
                            null,
                            "slice",
                            "bread",
                            "bread",
                            OriginalRawText: "2 slices bread"
                        ),
                    ],
                    Steps: [new(null, "Toast the bread and spread the butter.", [0])]
                );
            },
            _ => structured
        );
        await using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Llm:TextModel", "gemma4:e4b");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILlmExtractionClient>();
                services.AddSingleton<ILlmExtractionClient>(fakeLlm);
            });
        });
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);
        var response = await client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(source, AutoPublish: true)
        );
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        var job = await response.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(job);
        using (var scope = factory.Services.CreateScope())
        {
            Assert.IsTrue(
                await scope
                    .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                    .ProcessNextQueuedJobAsync()
            );
        }

        if (canonical)
        {
            Assert.IsEmpty(fakeLlm.StructuredSources);
        }
        else
        {
            Assert.HasCount(1, fakeLlm.StructuredSources);
            StringAssert.Contains(fakeLlm.StructuredSources[0], "1 tbsp butter");
        }
        Assert.HasCount(1, extractionSources);
        Assert.AreEqual(structured, extractionSources[0].ReplaceLineEndings("\n"));

        // The structured ingredient list lets validation catch the dropped butter line.
        var detail = await client.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{job.Id}"
        );
        Assert.IsNotNull(detail);
        Assert.AreEqual(IngestionJobStatus.ReadyForReview, detail.Status);
        StringAssert.Contains(detail.StatusDetail, "Source ingredient line 2");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<App.Data.AppDbContext>();
            var entity = await db.IngestionJobs.FindAsync(job.Id);
            Assert.IsNotNull(entity);
            Assert.AreEqual(canonical ? null : structured, entity.StructuredSourceText);
            StringAssert.Contains(entity.NormalizedSourceText ?? entity.RawText, "Plain Toast");
            if (!canonical)
            {
                Assert.DoesNotContain("Ingredients:", entity.NormalizedSourceText ?? "");
            }
        }
    }

    [TestMethod]
    public async Task TextIngestion_FallsBackToUnstructuredSourceWhenSectioningTimesOut()
    {
        const string source =
            "Plain Toast\n2 slices bread\nInstructions:\nToast the bread until golden, about 3 minutes.";
        var extractionSources = new List<string>();
        var fakeLlm = new FakeSequentialLlmExtractionClient(
            context =>
            {
                extractionSources.Add(context.SourceContent);
                return new LlmExtractedRecipePayload(
                    Title: "Plain Toast",
                    ConfidenceScore: 0.99m,
                    Ingredients:
                    [
                        new(
                            null,
                            2,
                            null,
                            "slice",
                            "bread",
                            "bread",
                            OriginalRawText: "2 slices bread"
                        ),
                    ],
                    Steps: [new(null, "Toast the bread until golden, about 3 minutes.", [0])]
                );
            },
            _ => throw new LlmHostUnavailableException("The LLM request timed out.")
        );
        await using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Llm:TextModel", "gemma4:e4b");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILlmExtractionClient>();
                services.AddSingleton<ILlmExtractionClient>(fakeLlm);
            });
        });
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);
        var response = await client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(source, AutoPublish: false)
        );
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        var job = await response.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(job);
        using (var scope = factory.Services.CreateScope())
        {
            Assert.IsTrue(
                await scope
                    .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                    .ProcessNextQueuedJobAsync()
            );
        }

        Assert.HasCount(1, fakeLlm.StructuredSources);
        Assert.HasCount(1, extractionSources);
        StringAssert.Contains(extractionSources[0], "2 slices bread");
        Assert.DoesNotContain("Ingredients:", extractionSources[0]);
        var detail = await client.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{job.Id}"
        );
        Assert.IsNotNull(detail);
        Assert.AreEqual(IngestionJobStatus.ReadyForReview, detail.Status);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<App.Data.AppDbContext>();
            var entity = await db.IngestionJobs.FindAsync(job.Id);
            Assert.IsNotNull(entity);
            Assert.IsNull(entity.StructuredSourceText);
        }
    }

    [TestMethod]
    public async Task TextIngestion_WaitsForHostWhenSectioningFailsAndSourceHasNoMethod()
    {
        var extractionCalls = 0;
        var fakeLlm = new FakeSequentialLlmExtractionClient(
            _ =>
            {
                extractionCalls++;
                throw new InvalidOperationException("Extraction must not run without a method.");
            },
            _ => throw new LlmHostUnavailableException("LLM host connection failed.")
        );
        await using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Llm:TextModel", "gemma4:e4b");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILlmExtractionClient>();
                services.AddSingleton<ILlmExtractionClient>(fakeLlm);
            });
        });
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);
        var response = await client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(
                "Plain Toast\n2 slices bread\nToast the bread until golden.",
                AutoPublish: false
            )
        );
        var job = await response.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(job);
        using (var scope = factory.Services.CreateScope())
        {
            await scope
                .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                .ProcessNextQueuedJobAsync();
        }

        Assert.AreEqual(0, extractionCalls);
        var detail = await client.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{job.Id}"
        );
        Assert.IsNotNull(detail);
        Assert.AreEqual(IngestionJobStatus.WaitingForLlmHost, detail.Status);
    }

    [TestMethod]
    public async Task ReviewRepair_UsesUnsavedDraftAndStructuredSourceWithoutPersistingPreview()
    {
        const string structured =
            "Title: Beans\nIngredients:\n- 1 tbsp oil\n- 1 tbsp butter\nInstructions:\nHeat oil and add butter.";
        var original = new IngestionExtractedDraftDto(
            Title: "Stored title",
            Ingredients:
            [
                new(
                    Quantity: 1,
                    Unit: MeasurementUnit.Tbsp,
                    DisplayName: "oil",
                    OriginalRawText: "1 tbsp oil"
                ),
                new(
                    Quantity: 1,
                    Unit: MeasurementUnit.Tbsp,
                    DisplayName: "butter",
                    OriginalRawText: "1 tbsp butter"
                ),
            ],
            Steps: [new(Instruction: "Heat oil and add butter.", IngredientIndices: [0])]
        );
        var fakeLlm = new FakeSequentialLlmExtractionClient(
            _ => throw new InvalidOperationException("Extraction was not expected."),
            repair: context => new LlmRecipeRepairResult(
                context.Draft with
                {
                    Steps =
                    [
                        context.Draft.Steps![0] with
                        {
                            Instruction = "Heat oil, then add butter.",
                            IngredientIndices = [0, 1],
                        },
                    ],
                },
                []
            )
        );
        await using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Llm:TextModel", "test-text");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILlmExtractionClient>();
                services.AddSingleton<ILlmExtractionClient>(fakeLlm);
            });
        });
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);
        var id = Guid.NewGuid();
        var storedJson = JsonSerializer.Serialize(original);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<App.Data.AppDbContext>();
            db.IngestionJobs.Add(
                new IngestionJob
                {
                    Id = id,
                    Channel = IngestionChannel.RawText,
                    Status = IngestionJobStatus.ReadyForReview,
                    DisplayTitle = original.Title,
                    RawText = "unstructured original",
                    StructuredSourceText = structured,
                    ExtractedDraftJson = storedJson,
                }
            );
            await db.SaveChangesAsync();
        }

        var edited = original with { Title = "Unsaved user title" };
        var diagnosticsResponse = await client.PostAsJsonAsync(
            $"/api/ingestion/jobs/{id}/review-diagnostics",
            new IngestionDraftDiagnosticsRequest(edited)
        );
        Assert.AreEqual(HttpStatusCode.OK, diagnosticsResponse.StatusCode);
        var diagnostics =
            await diagnosticsResponse.Content.ReadFromJsonAsync<IngestionReviewDiagnosticsDto>();
        Assert.IsNotNull(diagnostics);
        Assert.IsTrue(diagnostics.ValidationIssues.Any(issue => issue.Contains("butter")));

        var previewResponse = await client.PostAsJsonAsync(
            $"/api/ingestion/jobs/{id}/repair-preview",
            new IngestionRepairPreviewRequest(edited, "Link the butter.")
        );
        Assert.AreEqual(
            HttpStatusCode.OK,
            previewResponse.StatusCode,
            await previewResponse.Content.ReadAsStringAsync()
        );
        var preview = await previewResponse.Content.ReadFromJsonAsync<IngestionRepairPreviewDto>();
        Assert.IsNotNull(preview);
        Assert.AreEqual("Unsaved user title", preview.Draft.Title);
        CollectionAssert.AreEqual(
            new[] { 0, 1 },
            preview.Draft.Steps![0].IngredientIndices!.ToArray()
        );
        Assert.IsNotNull(fakeLlm.LastRepairContext);
        Assert.AreEqual(structured, fakeLlm.LastRepairContext.SourceContent);
        Assert.AreEqual("Link the butter.", fakeLlm.LastRepairContext.UserHint);
        Assert.AreEqual("Unsaved user title", fakeLlm.LastRepairContext.Draft.Title);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<App.Data.AppDbContext>();
            var entity = await db.IngestionJobs.FindAsync(id);
            Assert.IsNotNull(entity);
            Assert.AreEqual(storedJson, entity.ExtractedDraftJson);
            Assert.AreEqual(IngestionJobStatus.ReadyForReview, entity.Status);
        }

        var saved = await client.PutAsJsonAsync($"/api/ingestion/jobs/{id}/draft", preview.Draft);
        Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
        var updated = await saved.Content.ReadFromJsonAsync<IngestionJobDetailDto>();
        Assert.AreEqual("Unsaved user title", updated!.ExtractedDraft!.Title);
    }

    [TestMethod]
    public async Task ReviewRepair_ReportsMissingJobsConflictsAndUnavailableHosts()
    {
        var draft = new IngestionExtractedDraftDto(
            "Review me",
            Ingredients: [new(Quantity: 1, Unit: MeasurementUnit.Cup, DisplayName: "rice")],
            Steps: [new(Instruction: "Cook rice.", IngredientIndices: [0])]
        );
        var fakeLlm = new FakeSequentialLlmExtractionClient(
            _ => throw new InvalidOperationException(),
            repair: _ => throw new LlmHostUnavailableException("Test host is offline.")
        );
        await using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILlmExtractionClient>();
                services.AddSingleton<ILlmExtractionClient>(fakeLlm);
            });
        });
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);
        var missing = await client.PostAsJsonAsync(
            $"/api/ingestion/jobs/{Guid.NewGuid()}/repair-preview",
            new IngestionRepairPreviewRequest(draft)
        );
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);

        var id = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<App.Data.AppDbContext>();
            db.IngestionJobs.Add(
                new IngestionJob
                {
                    Id = id,
                    Channel = IngestionChannel.RawText,
                    Status = IngestionJobStatus.Queued,
                    DisplayTitle = draft.Title,
                    RawText = "Rice\nIngredients:\n- 1 cup rice\nInstructions:\n1. Cook rice.",
                }
            );
            await db.SaveChangesAsync();
        }
        var conflict = await client.PostAsJsonAsync(
            $"/api/ingestion/jobs/{id}/repair-preview",
            new IngestionRepairPreviewRequest(draft)
        );
        Assert.AreEqual(HttpStatusCode.Conflict, conflict.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<App.Data.AppDbContext>();
            var entity = await db.IngestionJobs.FindAsync(id);
            entity!.Status = IngestionJobStatus.ReadyForReview;
            await db.SaveChangesAsync();
        }
        var unavailable = await client.PostAsJsonAsync(
            $"/api/ingestion/jobs/{id}/repair-preview",
            new IngestionRepairPreviewRequest(draft)
        );
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        StringAssert.Contains(
            await unavailable.Content.ReadAsStringAsync(),
            "Test host is offline."
        );
    }

    [TestMethod]
    public async Task ReviewEndpoints_RejectMalformedNestedDraftsAndReportUnknownIndices()
    {
        await AuthenticateAsync(_client);
        var id = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<App.Data.AppDbContext>();
            db.IngestionJobs.Add(
                new IngestionJob
                {
                    Id = id,
                    Channel = IngestionChannel.RawText,
                    Status = IngestionJobStatus.ReadyForReview,
                    DisplayTitle = "Rice",
                }
            );
            await db.SaveChangesAsync();
        }
        var malformed = new IngestionExtractedDraftDto("Rice", Ingredients: [null!]);
        var response = await _client.PostAsJsonAsync(
            $"/api/ingestion/jobs/{id}/review-diagnostics",
            new IngestionDraftDiagnosticsRequest(malformed)
        );
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);

        var unknown = new IngestionExtractedDraftDto(
            "Rice",
            Ingredients: [new(DisplayName: "rice")],
            Steps: [new(Instruction: "Cook rice.", IngredientIndices: [3])]
        );
        response = await _client.PostAsJsonAsync(
            $"/api/ingestion/jobs/{id}/review-diagnostics",
            new IngestionDraftDiagnosticsRequest(unknown)
        );
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var diagnostics = await response.Content.ReadFromJsonAsync<IngestionReviewDiagnosticsDto>();
        Assert.IsNotNull(diagnostics);
        Assert.IsTrue(diagnostics.ValidationIssues.Any(issue => issue.Contains("out of range")));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ReviewRepair_RejectsJobsChangedDuringInference(bool changeStatus)
    {
        var id = Guid.NewGuid();
        IServiceProvider? services = null;
        var fakeLlm = new FakeSequentialLlmExtractionClient(
            _ => throw new InvalidOperationException(),
            repair: context =>
            {
                using var scope = services!.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<App.Data.AppDbContext>();
                var entity = db.IngestionJobs.Find(id)!;
                if (changeStatus)
                {
                    entity.Status = IngestionJobStatus.Discarded;
                }
                else
                {
                    entity.DisplayTitle = "Saved elsewhere";
                }
                db.SaveChanges();
                return new LlmRecipeRepairResult(context.Draft, []);
            }
        );
        await using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(collection =>
            {
                collection.RemoveAll<ILlmExtractionClient>();
                collection.AddSingleton<ILlmExtractionClient>(fakeLlm);
            });
        });
        services = factory.Services;
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<App.Data.AppDbContext>();
            db.IngestionJobs.Add(
                new IngestionJob
                {
                    Id = id,
                    Channel = IngestionChannel.RawText,
                    Status = IngestionJobStatus.ReadyForReview,
                    DisplayTitle = "Rice",
                    RawText = "Rice\nIngredients:\n- rice\nInstructions:\n1. Cook rice.",
                }
            );
            await db.SaveChangesAsync();
        }
        var draft = new IngestionExtractedDraftDto(
            "Rice",
            Ingredients: [new(DisplayName: "rice")],
            Steps: [new(Instruction: "Cook rice.", IngredientIndices: [0])]
        );
        var response = await client.PostAsJsonAsync(
            $"/api/ingestion/jobs/{id}/repair-preview",
            new IngestionRepairPreviewRequest(draft)
        );
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "changed while");
    }

    [TestMethod]
    [DataRow(IngestionChannel.Images)]
    [DataRow(IngestionChannel.Pdf)]
    public async Task ReviewRepair_RejectsJobsWithoutSourceText(IngestionChannel channel)
    {
        await AuthenticateAsync(_client);
        var id = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<App.Data.AppDbContext>();
            db.IngestionJobs.Add(
                new IngestionJob
                {
                    Id = id,
                    Channel = channel,
                    Status = IngestionJobStatus.ReadyForReview,
                    DisplayTitle = "Rice",
                    SourceFilesJson = "[]",
                }
            );
            await db.SaveChangesAsync();
        }
        var draft = new IngestionExtractedDraftDto(
            "Rice",
            Ingredients: [new(DisplayName: "rice")],
            Steps: [new(Instruction: "Cook rice.", IngredientIndices: [0])]
        );
        var response = await _client.PostAsJsonAsync(
            $"/api/ingestion/jobs/{id}/repair-preview",
            new IngestionRepairPreviewRequest(draft)
        );
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        StringAssert.Contains(
            await response.Content.ReadAsStringAsync(),
            "no source text to re-extract from"
        );
    }

    [TestMethod]
    public async Task RecoverInterruptedJobs_WhenJobsAreInExtractingStatus_RecoversNonActiveJobsToQueued()
    {
        var job1Id = Guid.NewGuid();
        var job2Id = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<App.Data.AppDbContext>();
            db.IngestionJobs.AddRange(
                new IngestionJob
                {
                    Id = job1Id,
                    Channel = IngestionChannel.RawText,
                    Status = IngestionJobStatus.Extracting,
                    DisplayTitle = "Stuck Job 1",
                    RawText = "Some recipe text",
                },
                new IngestionJob
                {
                    Id = job2Id,
                    Channel = IngestionChannel.RawText,
                    Status = IngestionJobStatus.Extracting,
                    DisplayTitle = "Stuck Job 2",
                    RawText = "Some recipe text 2",
                }
            );
            await db.SaveChangesAsync();

            var processor = scope.ServiceProvider.GetRequiredService<IIngestionProcessor>();
            var recoveredCount = await processor.RecoverInterruptedJobsAsync();
            Assert.AreEqual(2, recoveredCount);

            var recoveredJobs = db
                .IngestionJobs.Where(j => j.Id == job1Id || j.Id == job2Id)
                .ToList();

            Assert.IsTrue(recoveredJobs.All(j => j.Status == IngestionJobStatus.Queued));
        }
    }

    [TestMethod]
    public async Task RetryJob_WhenJobIsStuckInExtracting_AllowsRetryIfJobIsNotActivelyExecuting()
    {
        await AuthenticateAsync(_client);
        var jobId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<App.Data.AppDbContext>();
            db.IngestionJobs.Add(
                new IngestionJob
                {
                    Id = jobId,
                    Channel = IngestionChannel.RawText,
                    Status = IngestionJobStatus.Extracting,
                    DisplayTitle = "Stuck In Extraction",
                    RawText = "Some recipe text",
                }
            );
            await db.SaveChangesAsync();
        }

        var retryResponse = await _client.PostAsync($"/api/ingestion/jobs/{jobId}/retry", null);
        Assert.AreEqual(HttpStatusCode.Accepted, retryResponse.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<App.Data.AppDbContext>();
            var job = db.IngestionJobs.FirstOrDefault(j => j.Id == jobId);
            Assert.IsNotNull(job);
            Assert.AreEqual(IngestionJobStatus.Queued, job.Status);
        }
    }

    private sealed class FakeSequentialLlmExtractionClient(
        Func<LlmExtractionContext, LlmExtractedRecipePayload> handler,
        Func<string, string?>? structureSource = null,
        Func<LlmRecipeRepairContext, LlmRecipeRepairResult>? repair = null
    ) : ILlmExtractionClient
    {
        public List<string> StructuredSources { get; } = [];
        public LlmRecipeRepairContext? LastRepairContext { get; private set; }

        public Task<LlmRecipeRepairResult> RepairRecipeDraftAsync(
            EffectiveLlmConnectionConfig config,
            LlmRecipeRepairContext context,
            CancellationToken cancellationToken = default
        )
        {
            LastRepairContext = context;
            return Task.FromResult(repair?.Invoke(context) ?? throw new NotImplementedException());
        }

        public Task<string?> StructureRecipeSourceAsync(
            EffectiveLlmConnectionConfig config,
            string sourceText,
            CancellationToken cancellationToken = default
        )
        {
            StructuredSources.Add(sourceText);
            return Task.FromResult(structureSource?.Invoke(sourceText));
        }

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

    private sealed class BlockingLlmExtractionClient : ILlmExtractionClient
    {
        public Task<LlmRecipeRepairResult> RepairRecipeDraftAsync(
            EffectiveLlmConnectionConfig config,
            LlmRecipeRepairContext context,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<string?> StructureRecipeSourceAsync(
            EffectiveLlmConnectionConfig config,
            string sourceText,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<string?>(null);

        public Task<string> AnswerKitchenQuestionAsync(
            EffectiveLlmConnectionConfig config,
            LlmKitchenQuestionContext context,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

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

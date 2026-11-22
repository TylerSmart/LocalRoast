using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using App.Core.Authentication;
using App.Core.Common;
using App.Core.FoodCatalog;
using App.Core.Ingestion;
using App.Core.Recipes;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
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

        // Approving again updates the published recipe in place (Phase 15)
        var secondApprove = await _client.PostAsJsonAsync(
            $"/api/ingestion/jobs/{job.Id}/approve",
            draft
        );
        Assert.AreEqual(HttpStatusCode.Created, secondApprove.StatusCode);

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
    public async Task DraftApprove_PublishesStepTokens()
    {
        await AuthenticateAsync(_client);
        var job = await EnqueueRawTextJobAsync(_client, "Inline Roux");
        const string instruction =
            "Melt ||{\"ingredient\": \"butter\", \"amount\": 2, \"unit\": \"tbsp\"}||, then whisk in ||{\"ingredient\": \"flour\", \"amount\": 0.25, \"unit\": \"cup\"}|| to a paste.";
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
            Steps: [new IngestionDraftStepDto(Instruction: instruction)]
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
        var step = recipe.Steps.Single();
        Assert.AreEqual(instruction, step.Instruction);
        var tokens = StepInstructionParser.ExtractTokens(step.Instruction);
        Assert.AreEqual(2, tokens.Count);
        Assert.AreEqual(2m, ((StepIngredientToken)tokens[0]).Amount);
        Assert.AreEqual(0.25m, ((StepIngredientToken)tokens[1]).Amount);
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
    public async Task GetJobsPaged_SupportsSortingByNameCreatedAtAndModifiedAt()
    {
        await AuthenticateAsync(_client);

        var firstJobResp = await _client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest("Alpha Recipe", TitleHint: "Beta Soup")
        );
        Assert.AreEqual(HttpStatusCode.Accepted, firstJobResp.StatusCode);
        var firstJob = await firstJobResp.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(firstJob);

        await Task.Delay(25);

        var secondJobResp = await _client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest("Gamma Recipe", TitleHint: "Gamma Salad")
        );
        Assert.AreEqual(HttpStatusCode.Accepted, secondJobResp.StatusCode);
        var secondJob = await secondJobResp.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(secondJob);

        await Task.Delay(25);

        var thirdJobResp = await _client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest("Zeta Recipe", TitleHint: "Alpha Bread")
        );
        Assert.AreEqual(HttpStatusCode.Accepted, thirdJobResp.StatusCode);
        var thirdJob = await thirdJobResp.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(thirdJob);

        // Update the first job's draft so its ModifiedAt becomes the newest
        await Task.Delay(25);
        var updateResp = await _client.PutAsJsonAsync(
            $"/api/ingestion/jobs/{firstJob.Id}/draft",
            CreateRecipeReferenceDraft("Beta Soup", "Chicken Stock")
        );
        Assert.AreEqual(HttpStatusCode.OK, updateResp.StatusCode);

        // 1. Name Asc -> Alpha Bread, Beta Soup, Gamma Salad
        var nameAsc = await _client.GetFromJsonAsync<PagedResultDto<IngestionJobSummaryDto>>(
            "/api/ingestion/jobs?page=1&pageSize=10&sortBy=Name&sortDirection=Asc"
        );
        Assert.IsNotNull(nameAsc);
        CollectionAssert.AreEqual(
            new[] { "Alpha Bread", "Beta Soup", "Gamma Salad" },
            nameAsc.Items.Select(j => j.DisplayTitle).ToArray()
        );

        // 2. Name Desc -> Gamma Salad, Beta Soup, Alpha Bread
        var nameDesc = await _client.GetFromJsonAsync<PagedResultDto<IngestionJobSummaryDto>>(
            "/api/ingestion/jobs?page=1&pageSize=10&sortBy=Name&sortDirection=Desc"
        );
        Assert.IsNotNull(nameDesc);
        CollectionAssert.AreEqual(
            new[] { "Gamma Salad", "Beta Soup", "Alpha Bread" },
            nameDesc.Items.Select(j => j.DisplayTitle).ToArray()
        );

        // 3. CreatedAt Asc -> Beta Soup (1st), Gamma Salad (2nd), Alpha Bread (3rd)
        var createdAsc = await _client.GetFromJsonAsync<PagedResultDto<IngestionJobSummaryDto>>(
            "/api/ingestion/jobs?page=1&pageSize=10&sortBy=CreatedAt&sortDirection=Asc"
        );
        Assert.IsNotNull(createdAsc);
        CollectionAssert.AreEqual(
            new[] { firstJob.Id, secondJob.Id, thirdJob.Id },
            createdAsc.Items.Select(j => j.Id).ToArray()
        );

        // 4. CreatedAt Desc -> Alpha Bread (3rd), Gamma Salad (2nd), Beta Soup (1st)
        var createdDesc = await _client.GetFromJsonAsync<PagedResultDto<IngestionJobSummaryDto>>(
            "/api/ingestion/jobs?page=1&pageSize=10&sortBy=CreatedAt&sortDirection=Desc"
        );
        Assert.IsNotNull(createdDesc);
        CollectionAssert.AreEqual(
            new[] { thirdJob.Id, secondJob.Id, firstJob.Id },
            createdDesc.Items.Select(j => j.Id).ToArray()
        );

        // 5. ModifiedAt Desc -> Beta Soup (updated last), Alpha Bread, Gamma Salad
        var modifiedDesc = await _client.GetFromJsonAsync<PagedResultDto<IngestionJobSummaryDto>>(
            "/api/ingestion/jobs?page=1&pageSize=10&sortBy=ModifiedAt&sortDirection=Desc"
        );
        Assert.IsNotNull(modifiedDesc);
        CollectionAssert.AreEqual(
            new[] { firstJob.Id, thirdJob.Id, secondJob.Id },
            modifiedDesc.Items.Select(j => j.Id).ToArray()
        );

        // 6. ModifiedAt Asc -> Gamma Salad, Alpha Bread, Beta Soup
        var modifiedAsc = await _client.GetFromJsonAsync<PagedResultDto<IngestionJobSummaryDto>>(
            "/api/ingestion/jobs?page=1&pageSize=10&sortBy=ModifiedAt&sortDirection=Asc"
        );
        Assert.IsNotNull(modifiedAsc);
        CollectionAssert.AreEqual(
            new[] { secondJob.Id, thirdJob.Id, firstJob.Id },
            modifiedAsc.Items.Select(j => j.Id).ToArray()
        );
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
                return new ExtractedRecipeDto(
                    Title: "Classic Roast Chicken",
                    Description: "Crisp-skinned roast chicken.",
                    Servings: 4m,
                    YieldDisplay: "Serves 4",
                    PrepTimeMinutes: 15,
                    CookTimeMinutes: 60,
                    TotalTimeMinutes: 75,
                    Difficulty: "Medium",
                    ProposedTags: ["Dinner", "American", "Weeknight"],
                    Ingredients:
                    [
                        new(
                            RefId: "ing_1",
                            CanonicalFoodItem: "Whole Chicken",
                            DisplayName: "whole broiler-fryer chicken",
                            Quantity: 1m,
                            Unit: "whole",
                            PreparationNotes: "giblets removed",
                            IsOptional: false,
                            RawText: "1 (4-pound) whole chicken"
                        ),
                        new(
                            RefId: "ing_2",
                            CanonicalFoodItem: "Kosher Salt",
                            DisplayName: "kosher salt",
                            Category: "Spices & Seasonings",
                            Quantity: 1m,
                            Unit: "tbsp",
                            IsOptional: false,
                            RawText: "1 tablespoon kosher salt"
                        ),
                    ],
                    Steps:
                    [
                        new(
                            StepNumber: 1,
                            TemplateText: "Roast {{ing_1_a}} with {{ing_2_a}} for {{timer_1}}.",
                            IngredientTokens: [new("ing_1_a", "ing_1"), new("ing_2_a", "ing_2")],
                            TimerTokens: [new("timer_1", 3600, "Roast chicken")]
                        ),
                    ],
                    QualityAudit: new RecipeQualityAuditDto(IsPlausibleRecipe: true, Warnings: [])
                );
            }

            return new ExtractedRecipeDto(
                Title: "Skillet Lemon Chicken",
                Description: "Bright weeknight skillet chicken.",
                Servings: 4m,
                YieldDisplay: "Serves 4",
                PrepTimeMinutes: 10,
                CookTimeMinutes: 20,
                TotalTimeMinutes: 30,
                Difficulty: "Easy",
                ProposedTags: ["Dinner", "Weeknight", "Mediterranean"],
                Ingredients:
                [
                    new(
                        RefId: "ing_1",
                        CanonicalFoodItem: "Kosher Salt",
                        DisplayName: "kosher salt",
                        Category: "Spices & Seasonings",
                        Quantity: 1m,
                        Unit: "tsp",
                        IsOptional: false,
                        RawText: "1 teaspoon kosher salt"
                    ),
                    new(
                        RefId: "ing_2",
                        CanonicalFoodItem: "Lemon",
                        DisplayName: "lemon",
                        Quantity: 1m,
                        Unit: "whole",
                        PreparationNotes: "sliced",
                        IsOptional: false,
                        RawText: "1 lemon, sliced"
                    ),
                ],
                Steps:
                [
                    new(
                        StepNumber: 1,
                        TemplateText: "Sear with {{ing_1_a}} and {{ing_2_a}} for {{timer_1}}.",
                        IngredientTokens: [new("ing_1_a", "ing_1"), new("ing_2_a", "ing_2")],
                        TimerTokens: [new("timer_1", 600, "Sear chicken")]
                    ),
                ],
                QualityAudit: new RecipeQualityAuditDto(IsPlausibleRecipe: true, Warnings: [])
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
                RawText: "Title: Classic Roast Chicken\nIngredients:\n- 1 (4-pound) whole chicken\n- 1 tablespoon kosher salt\nInstructions:\nRoast whole chicken with kosher salt for 1 hour.",
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
                RawText: "Title: Skillet Lemon Chicken\nIngredients:\n- 1 teaspoon kosher salt\n- 1 lemon, sliced\nInstructions:\nSear with kosher salt and sliced lemon for 10 minutes.",
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

        Assert.HasCount(2, capturedContexts);

        // Verify FoodItems catalog has exactly 3 deduplicated items (Kosher Salt reused, defaulting to manual pantry staple curation)
        var foods = await client.GetFromJsonAsync<IReadOnlyList<FoodItemDto>>("/api/food-items");
        Assert.IsNotNull(foods);
        Assert.AreEqual(3, foods.Count);
        var salt = foods.Single(f => f.Name == "Kosher Salt");
        Assert.IsFalse(salt.IsPantryStaple);

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
        Assert.AreEqual(TagCategory.Occasion, allTags.Single(t => t.Name == "Weeknight").Category);

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
            return new ExtractedRecipeDto(
                Title: title,
                QualityAudit: new RecipeQualityAuditDto(
                    IsPlausibleRecipe: false,
                    Warnings: ["Hold for review"]
                )
            );
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
        var mixStep = "Mix 1 cup bread flour.";
        var fakeLlm = new FakeSequentialLlmExtractionClient(_ => new ExtractedRecipeDto(
            Title: "Synthetic Starter",
            Servings: 4m,
            PrepTimeMinutes: 10,
            CookTimeMinutes: 20,
            TotalTimeMinutes: 30,
            Ingredients:
            [
                new(
                    RefId: "ing_1",
                    CanonicalFoodItem: "bread flour",
                    DisplayName: "bread flour",
                    Quantity: 5m,
                    Unit: "cup",
                    AltQuantity: 25m,
                    AltUnit: "oz",
                    RawText: "5 cups (25 ounces) bread flour"
                ),
            ],
            Steps:
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Mix {{ing_1_a}}.",
                    IngredientTokens: [new("ing_1_a", "ing_1", 1m, "cup")]
                ),
                new(
                    StepNumber: 2,
                    TemplateText: "Knead in {{ing_1_b}}.",
                    IngredientTokens: [new("ing_1_b", "ing_1")]
                ),
            ],
            QualityAudit: new RecipeQualityAuditDto(IsPlausibleRecipe: true, Warnings: []),
            ForceOutOfRangeStepIndex: invalidAmount ? 99 : null
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
            Assert.IsGreaterThan(0, job.ExtractedDraft.QualityWarnings?.Count ?? 0);
            StringAssert.Contains(job.StatusDetail, "Ready for side-by-side review");
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
            var tokens = StepInstructionParser.ExtractTokens(recipe.Steps[0].Instruction);
            var inline = (StepIngredientToken)tokens.Single();
            Assert.AreEqual("bread flour", inline.Ingredient);
            Assert.AreEqual(1m, inline.Amount);
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
        var fakeLlm = new FakeSequentialLlmExtractionClient(_ => new ExtractedRecipeDto(
            Title: "Oil sauce",
            Servings: 4m,
            PrepTimeMinutes: 5,
            CookTimeMinutes: 5,
            TotalTimeMinutes: 10,
            Ingredients:
            [
                new(
                    RefId: "ing_1",
                    CanonicalFoodItem: "olive oil",
                    DisplayName: "olive oil",
                    Quantity: 4m,
                    Unit: invalidFoodName ? "tbsp" : "tablespoon",
                    AltQuantity: invalidFoodName ? null : 25m,
                    AltUnit: invalidFoodName ? null : "cup",
                    RawText: "4 tablespoons olive oil"
                ),
                new(
                    RefId: "ing_2",
                    CanonicalFoodItem: "salt",
                    DisplayName: "salt",
                    RawText: "Salt"
                ),
            ],
            Steps:
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Heat {{ing_1_a}} and season with {{ing_2_a}}.",
                    IngredientTokens: [new("ing_1_a", "ing_1", 4m, "tbsp"), new("ing_2_a", "ing_2")]
                ),
            ],
            QualityAudit: new RecipeQualityAuditDto(IsPlausibleRecipe: true, Warnings: []),
            ForceOutOfRangeStepIndex: 99,
            ForceZeroQuantityTokenInStep1: true
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
        Assert.IsLessThan(0.85m, detail.ConfidenceScore!.Value);
        StringAssert.Contains(detail.StatusDetail, "Ready for side-by-side review");
        Assert.IsGreaterThanOrEqualTo(1, detail.ExtractedDraft!.QualityWarnings?.Count ?? 0);
        if (invalidFoodName)
        {
            Assert.IsTrue(
                detail.ExtractedDraft.QualityWarnings!.Any(w =>
                    w.Contains("out-of-range", StringComparison.OrdinalIgnoreCase)
                )
            );
        }
        else
        {
            Assert.IsGreaterThanOrEqualTo(1, detail.ExtractedDraft.MeasurementWarnings?.Count ?? 0);
        }
        Assert.IsNull(detail.ResultingRecipeId);
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

        var invalidTokenStep = new IngestionExtractedDraftDto(
            "Rice",
            Ingredients: [new(DisplayName: "rice")],
            Steps:
            [
                new(
                    Instruction: "Cook ||{\"ingredient\": \"rice\", \"amount\": 0, \"unit\": \"cup\"}||."
                ),
            ]
        );
        response = await _client.PostAsJsonAsync(
            $"/api/ingestion/jobs/{id}/review-diagnostics",
            new IngestionDraftDiagnosticsRequest(invalidTokenStep)
        );
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var diagnostics = await response.Content.ReadFromJsonAsync<IngestionReviewDiagnosticsDto>();
        Assert.IsNotNull(diagnostics);
        Assert.IsTrue(diagnostics.ValidationIssues.Any(issue => issue.Contains("non-positive")));
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

    [TestMethod]
    public async Task SubRecipe_WhenPublishedAfterParent_AutomaticallyBacklinksParentIngredient()
    {
        await AuthenticateAsync(_client);

        // 1. Create parent recipe with unresolved sub-recipe reference
        var parentReq = new UpsertRecipeRequest(
            Title: "Denver Omelets",
            Description: "Classic diner style",
            Status: RecipeStatus.Published,
            BaseServings: 2,
            Ingredients:
            [
                new RecipeIngredientInput(
                    DisplayName: "Filling for Denver Omelets (recipe follows)",
                    Quantity: 1,
                    Unit: MeasurementUnit.Whole,
                    UnresolvedRecipeTitle: "Filling for Denver Omelets"
                ),
                new RecipeIngredientInput(
                    DisplayName: "large eggs",
                    Quantity: 6,
                    Unit: MeasurementUnit.Whole
                ),
            ],
            Steps: [new RecipeStepInput(Instruction: "Fold the filling into the omelet.")]
        );

        var parentRes = await _client.PostAsJsonAsync("/api/recipes", parentReq);
        Assert.AreEqual(HttpStatusCode.Created, parentRes.StatusCode);
        var parent = await parentRes.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(parent);
        Assert.IsNull(parent.Ingredients[0].LinkedRecipeId);
        Assert.AreEqual("Filling for Denver Omelets", parent.Ingredients[0].UnresolvedRecipeTitle);

        // 2. Create child recipe "Filling for Denver Omelets"
        var childReq = new UpsertRecipeRequest(
            Title: "Filling for Denver Omelets",
            Description: "Ham, peppers, and onions",
            Status: RecipeStatus.Published,
            BaseServings: 2,
            Ingredients:
            [
                new RecipeIngredientInput(
                    DisplayName: "diced ham",
                    Quantity: 1,
                    Unit: MeasurementUnit.Cup
                ),
                new RecipeIngredientInput(
                    DisplayName: "onion",
                    Quantity: 1,
                    Unit: MeasurementUnit.Whole
                ),
            ],
            Steps:
            [
                new RecipeStepInput(Instruction: "Sauté the ham and onions until lightly browned."),
            ]
        );

        var childRes = await _client.PostAsJsonAsync("/api/recipes", childReq);
        Assert.AreEqual(HttpStatusCode.Created, childRes.StatusCode);
        var child = await childRes.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(child);

        // 3. Fetch parent recipe and verify it was automatically back-linked
        var updatedParentRes = await _client.GetAsync($"/api/recipes/{parent.Id}");
        Assert.AreEqual(HttpStatusCode.OK, updatedParentRes.StatusCode);
        var updatedParent = await updatedParentRes.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(updatedParent);
        Assert.AreEqual(child.Id, updatedParent.Ingredients[0].LinkedRecipeId);
        Assert.AreEqual(child.Title, updatedParent.Ingredients[0].LinkedRecipeTitle);
        Assert.IsNull(updatedParent.Ingredients[0].UnresolvedRecipeTitle);
    }

    [TestMethod]
    public async Task PausedQueue_SingleJobRerun_SetsRunNowRequestedAndRunsWhilePaused()
    {
        var fakeLlm = new FakeSequentialLlmExtractionClient(_ => new ExtractedRecipeDto(
            Title: "Single Job Rerun Paused Test",
            Ingredients:
            [
                new(
                    RefId: "ing_1",
                    CanonicalFoodItem: "sugar",
                    DisplayName: "sugar",
                    Quantity: 1m,
                    Unit: "cup",
                    RawText: "1 cup sugar"
                ),
            ],
            Steps:
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Stir {{ing_1_a}}.",
                    IngredientTokens: [new("ing_1_a", "ing_1")]
                ),
            ],
            QualityAudit: new RecipeQualityAuditDto(IsPlausibleRecipe: true, Warnings: [])
        ));

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

        Guid jobId;
        using (var scope = customFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<App.Data.AppDbContext>();
            var job = new IngestionJob
            {
                DisplayTitle = "Single Job Rerun Paused Test",
                Channel = IngestionChannel.RawText,
                Status = IngestionJobStatus.Failed,
                RunNowRequested = false,
                RawText = "Ingredients:\n- 1 cup sugar\nInstructions:\nStir sugar.",
            };
            db.IngestionJobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;

            // Pause queue
            var settings = scope.ServiceProvider.GetRequiredService<ILlmSettingsService>();
            await settings.SetIngestionEnabledAsync(false);
        }

        // Call POST /api/ingestion/jobs/{id}/retry
        var retryRes = await client.PostAsync($"/api/ingestion/jobs/{jobId}/retry", null);
        Assert.AreEqual(HttpStatusCode.Accepted, retryRes.StatusCode);
        var summary = await retryRes.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(summary);
        Assert.AreEqual(IngestionJobStatus.Queued, summary.Status);

        // Verify in DB that RunNowRequested is true
        using (var scope = customFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<App.Data.AppDbContext>();
            var job = await db.IngestionJobs.FindAsync(jobId);
            Assert.IsNotNull(job);
            Assert.AreEqual(IngestionJobStatus.Queued, job.Status);
            Assert.IsTrue(job.RunNowRequested);

            // Verify processor runs it even though EnableIngestion is false
            var processor = scope.ServiceProvider.GetRequiredService<IIngestionProcessor>();
            var processed = await processor.ProcessNextQueuedJobAsync();
            Assert.IsTrue(processed);
        }
    }

    [TestMethod]
    public async Task PublishedJob_CanUpdateDraft_RepairPreview_AndApproveUpdatesLinkedRecipe()
    {
        await AuthenticateAsync(_client);

        Guid jobId;
        Guid recipeId;
        var initialDraft = new IngestionExtractedDraftDto(
            Title: "Initial Pancake Recipe",
            Ingredients:
            [
                new IngestionDraftIngredientDto(
                    DisplayName: "Flour",
                    Quantity: 2m,
                    Unit: MeasurementUnit.Cup,
                    OriginalRawText: "2 cups flour"
                ),
            ],
            Steps: [new IngestionDraftStepDto(Instruction: "Mix flour.")]
        );

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<App.Data.AppDbContext>();
            var ingestion = scope.ServiceProvider.GetRequiredService<IIngestionService>();

            var job = new IngestionJob
            {
                DisplayTitle = "Initial Pancake Recipe",
                Status = IngestionJobStatus.ReadyForReview,
                RawText = "Ingredients:\n- 2 cups flour\nInstructions:\nMix flour.",
                ExtractedDraftJson = System.Text.Json.JsonSerializer.Serialize(initialDraft),
            };
            db.IngestionJobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;

            // Approve & publish it
            var published = await ingestion.ApproveAndPublishAsync(jobId, initialDraft);
            recipeId = published.Id;
        }

        // 1. Verify job is now Published
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<App.Data.AppDbContext>();
            var job = await db.IngestionJobs.FindAsync(jobId);
            Assert.AreEqual(IngestionJobStatus.Published, job!.Status);
            Assert.AreEqual(recipeId, job.ResultingRecipeId);
        }

        // 2. PUT /api/ingestion/jobs/{id}/draft with updated title & ingredients on the Published job
        var updatedDraft = initialDraft with
        {
            Title = "Fluffy Buttermilk Pancakes",
            Ingredients =
            [
                new IngestionDraftIngredientDto(
                    DisplayName: "Flour",
                    Quantity: 3m,
                    Unit: MeasurementUnit.Cup,
                    OriginalRawText: "3 cups flour"
                ),
            ],
        };
        var updateDraftRes = await _client.PutAsJsonAsync(
            $"/api/ingestion/jobs/{jobId}/draft",
            updatedDraft
        );
        Assert.AreEqual(HttpStatusCode.OK, updateDraftRes.StatusCode);

        // 3. POST /api/ingestion/jobs/{id}/approve to update the published recipe in place
        var approveRes = await _client.PostAsJsonAsync(
            $"/api/ingestion/jobs/{jobId}/approve",
            updatedDraft
        );
        Assert.AreEqual(HttpStatusCode.Created, approveRes.StatusCode);
        var updatedRecipe = await approveRes.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(updatedRecipe);
        Assert.AreEqual(recipeId, updatedRecipe.Id);
        Assert.AreEqual("Fluffy Buttermilk Pancakes", updatedRecipe.Title);

        // 4. Verify fetched recipe from /api/recipes/{recipeId} has the updated title
        var getRecipeRes = await _client.GetAsync($"/api/recipes/{recipeId}");
        Assert.AreEqual(HttpStatusCode.OK, getRecipeRes.StatusCode);
        var fetchedRecipe = await getRecipeRes.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.AreEqual("Fluffy Buttermilk Pancakes", fetchedRecipe!.Title);
    }

    [TestMethod]
    public async Task RetryBatchJobs_MatchesFilterAndSearchAcrossAllPages()
    {
        await AuthenticateAsync(_client);

        var batchTag = $"BatchSearch_{Guid.NewGuid():N}"[..18];
        const int totalJobs = 30; // Greater than standard page size of 25

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<App.Data.AppDbContext>();
            for (var i = 0; i < totalJobs; i++)
            {
                db.IngestionJobs.Add(
                    new IngestionJob
                    {
                        DisplayTitle = $"{batchTag} Job {i + 1}",
                        Status = IngestionJobStatus.Failed,
                        StatusDetail = "Temporary network error",
                        RawText = "Some recipe text",
                    }
                );
            }
            await db.SaveChangesAsync();
        }

        // Call POST /api/ingestion/jobs/retry-batch with empty jobIds, Filter = Failed, Search = batchTag
        var request = new RetryBatchJobsRequest(JobIds: [], Filter: "Failed", Search: batchTag);
        var response = await _client.PostAsJsonAsync("/api/ingestion/jobs/retry-batch", request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<RetryBatchJobsResultDto>();
        Assert.IsNotNull(result);
        Assert.AreEqual(totalJobs, result.RequeuedCount);

        // Verify in DB that all 30 jobs are now Queued
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<App.Data.AppDbContext>();
            var requeuedCount = (
                await db
                    .IngestionJobs.Where(j =>
                        j.DisplayTitle.StartsWith(batchTag) && j.Status == IngestionJobStatus.Queued
                    )
                    .ToListAsync()
            ).Count;
            Assert.AreEqual(totalJobs, requeuedCount);
        }
    }

    [TestMethod]
    public async Task IngestionReview_Diagnose_ReturnsTargetedDiagnostics()
    {
        await AuthenticateAsync(_client);

        Guid jobId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<App.Data.AppDbContext>();
            var job = new IngestionJob
            {
                DisplayTitle = "Diagnostics Target Test",
                Status = IngestionJobStatus.ReadyForReview,
                RawText = "Ingredients:\n- 1 cup sugar\nInstructions:\nStep 1: stir sugar.",
            };
            db.IngestionJobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        var draftWithIssues = new IngestionExtractedDraftDto(
            Title: "Diagnostics Test",
            Ingredients:
            [
                new IngestionDraftIngredientDto(
                    DisplayName: "Sugar",
                    Quantity: -1m, // Invalid: non-positive measurement
                    Unit: MeasurementUnit.Cup,
                    OriginalRawText: "1 cup sugar"
                ),
            ],
            Steps:
            [
                new IngestionDraftStepDto(
                    Instruction: "Stir ||{\"ingredient\": \"sugar\", \"amount\": 0, \"unit\": \"cup\"}||."
                ),
            ]
        );

        var response = await _client.PostAsJsonAsync(
            $"/api/ingestion/jobs/{jobId}/review-diagnostics",
            new IngestionDraftDiagnosticsRequest(draftWithIssues)
        );
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var diagnostics = await response.Content.ReadFromJsonAsync<IngestionReviewDiagnosticsDto>();
        Assert.IsNotNull(diagnostics);
        Assert.IsNotNull(diagnostics.TargetedDiagnostics);
        Assert.IsTrue(diagnostics.TargetedDiagnostics.Count >= 2);

        // One should target IngredientIndex 0
        var ingDiag = diagnostics.TargetedDiagnostics.FirstOrDefault(d => d.IngredientIndex == 0);
        Assert.IsNotNull(ingDiag, "Expected diagnostic targeting IngredientIndex 0");
        Assert.AreEqual("error", ingDiag.Severity);

        // One should target StepNumber 1
        var stepDiag = diagnostics.TargetedDiagnostics.FirstOrDefault(d => d.StepNumber == 1);
        Assert.IsNotNull(stepDiag, "Expected diagnostic targeting StepNumber 1");
        Assert.AreEqual("error", stepDiag.Severity);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TextIngestion_RecipeReference_AutoPublishesOnlyWhenReferencedRecipeExists(
        bool prerequisiteRecipeExists
    )
    {
        const string componentRecipeTitle = "Easy-Peel Hard-Cooked Eggs";
        const string compositeRecipeTitle = "Curry Deviled Eggs";

        var fakeLlm = new FakeSequentialLlmExtractionClient(_ => new ExtractedRecipeDto(
            Title: compositeRecipeTitle,
            Ingredients:
            [
                new(
                    RefId: "ing_1",
                    CanonicalFoodItem: componentRecipeTitle,
                    DisplayName: componentRecipeTitle,
                    Quantity: 1m,
                    Unit: "recipe",
                    IsRecipeReference: true,
                    ReferencedRecipeTitle: componentRecipeTitle,
                    RawText: $"1 recipe {componentRecipeTitle}"
                ),
                new(
                    RefId: "ing_2",
                    CanonicalFoodItem: "mayonnaise",
                    DisplayName: "mayonnaise",
                    Quantity: 2m,
                    Unit: "tablespoon",
                    RawText: "2 tablespoons mayonnaise"
                ),
            ],
            Steps:
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Halve {{ing_1_a}} and mash yolks with {{ing_2_a}}.",
                    IngredientTokens: [new("ing_1_a", "ing_1"), new("ing_2_a", "ing_2")]
                ),
            ],
            QualityAudit: new RecipeQualityAuditDto(IsPlausibleRecipe: true, Warnings: [])
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

        Guid? prerequisiteId = null;
        if (prerequisiteRecipeExists)
        {
            var createPrereqRes = await client.PostAsJsonAsync(
                "/api/recipes",
                new UpsertRecipeRequest(
                    Title: componentRecipeTitle,
                    Description: "Base hard-cooked eggs",
                    Status: RecipeStatus.Published,
                    BaseServings: 6,
                    Ingredients:
                    [
                        new RecipeIngredientInput(
                            DisplayName: "eggs",
                            Quantity: 6,
                            Unit: MeasurementUnit.Whole
                        ),
                    ],
                    Steps: [new RecipeStepInput(Instruction: "Steam eggs for 13 minutes and peel.")]
                )
            );
            createPrereqRes.EnsureSuccessStatusCode();
            var prereqRecipe = await createPrereqRes.Content.ReadFromJsonAsync<RecipeDetailDto>();
            Assert.IsNotNull(prereqRecipe);
            prerequisiteId = prereqRecipe.Id;
        }

        var queuedResponse = await client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(
                $"{compositeRecipeTitle}\nIngredients:\n- 1 recipe {componentRecipeTitle}\n- 2 tablespoons mayonnaise\nInstructions:\n1. Halve eggs and mash yolks with mayonnaise.",
                AutoPublish: true
            )
        );
        queuedResponse.EnsureSuccessStatusCode();
        var queued = await queuedResponse.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(queued);

        using (var scope = customFactory.Services.CreateScope())
        {
            var processed = await scope
                .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                .ProcessNextQueuedJobAsync();
            Assert.IsTrue(processed);
        }

        var job = await client.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{queued.Id}"
        );
        Assert.IsNotNull(job);

        if (prerequisiteRecipeExists)
        {
            Assert.AreEqual(IngestionJobStatus.Published, job.Status);
            Assert.IsNotNull(job.ResultingRecipeId);

            var published = await client.GetFromJsonAsync<RecipeDetailDto>(
                $"/api/recipes/{job.ResultingRecipeId}"
            );
            Assert.IsNotNull(published);
            var eggRef = published.Ingredients.FirstOrDefault(i =>
                i.DisplayName == componentRecipeTitle
            );
            Assert.IsNotNull(eggRef);
            Assert.AreEqual(prerequisiteId, eggRef.LinkedRecipeId);
            Assert.IsNull(eggRef.UnresolvedRecipeTitle);
        }
        else
        {
            Assert.AreEqual(IngestionJobStatus.ReadyForReview, job.Status);
            Assert.IsNull(job.ResultingRecipeId);
            StringAssert.Contains(job.StatusDetail, "could not be linked");

            var draftEggRef = job.ExtractedDraft?.Ingredients?.FirstOrDefault(i =>
                i.DisplayName == componentRecipeTitle
            );
            Assert.IsNotNull(draftEggRef);
            Assert.IsTrue(draftEggRef.IsRecipeReference);
            Assert.IsNull(draftEggRef.MatchedRecipeId);
        }
    }

    [TestMethod]
    public async Task TextIngestion_ResolvedSubRecipeWarning_SuppressesFalseSubRecipeWarningAndAutoPublishes()
    {
        const string componentRecipeTitle = "Filling for Denver Omelets";
        const string compositeRecipeTitle = "Denver Omelets";

        var fakeLlm = new FakeSequentialLlmExtractionClient(_ => new ExtractedRecipeDto(
            Title: compositeRecipeTitle,
            Ingredients:
            [
                new(
                    RefId: "ing_1",
                    CanonicalFoodItem: componentRecipeTitle,
                    DisplayName: componentRecipeTitle,
                    Quantity: 1m,
                    Unit: "recipe",
                    IsRecipeReference: true,
                    ReferencedRecipeTitle: componentRecipeTitle,
                    RawText: $"1 recipe {componentRecipeTitle} (recipe follows)"
                ),
                new(
                    RefId: "ing_2",
                    CanonicalFoodItem: "egg",
                    DisplayName: "large eggs",
                    Quantity: 6m,
                    Unit: "whole",
                    RawText: "6 large eggs"
                ),
            ],
            Steps:
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Cook {{ing_2_a}} and spoon {{ing_1_a}} over center.",
                    IngredientTokens:
                    [
                        new("ing_2_a", "ing_2", 6m, "whole"),
                        new("ing_1_a", "ing_1"),
                    ]
                ),
            ],
            QualityAudit: new RecipeQualityAuditDto(
                IsPlausibleRecipe: true,
                Warnings:
                [
                    "The recipe relies on a referenced sub-recipe ('Filling for Denver Omelets') which is not provided, making the instructions incomplete for a standalone execution.",
                ]
            )
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

        var createPrereqRes = await client.PostAsJsonAsync(
            "/api/recipes",
            new UpsertRecipeRequest(
                Title: componentRecipeTitle,
                Description: "Bell pepper and ham filling",
                Status: RecipeStatus.Published,
                BaseServings: 2,
                Ingredients:
                [
                    new RecipeIngredientInput(
                        DisplayName: "ham steak",
                        Quantity: 4,
                        Unit: MeasurementUnit.Oz
                    ),
                ],
                Steps: [new RecipeStepInput(Instruction: "Sauté ham and peppers.")]
            )
        );
        createPrereqRes.EnsureSuccessStatusCode();

        var queuedResponse = await client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(
                $"{compositeRecipeTitle}\nIngredients:\n- 1 recipe {componentRecipeTitle} (recipe follows)\n- 6 large eggs\nInstructions:\n1. Cook eggs and spoon filling over center.",
                AutoPublish: true
            )
        );
        queuedResponse.EnsureSuccessStatusCode();
        var queued = await queuedResponse.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(queued);

        using (var scope = customFactory.Services.CreateScope())
        {
            var processed = await scope
                .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                .ProcessNextQueuedJobAsync();
            Assert.IsTrue(processed);
        }

        var job = await client.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{queued.Id}"
        );
        Assert.IsNotNull(job);
        Assert.AreEqual(IngestionJobStatus.Published, job.Status);
        Assert.IsNotNull(job.ResultingRecipeId);
    }

    [TestMethod]
    public async Task TextIngestion_UnflaggedOptionalSubRecipeMatchingPublishedRecipe_PromotesAndLinksWithoutCreatingFoodItem()
    {
        var fakeLlm = new FakeSequentialLlmExtractionClient(_ => new ExtractedRecipeDto(
            Title: "Spanish Tortilla",
            Description: "Potato and onion omelet",
            Servings: 4,
            Ingredients:
            [
                new(
                    RefId: "ing_1",
                    CanonicalFoodItem: "potato",
                    DisplayName: "potatoes",
                    Quantity: 1,
                    Unit: "lb",
                    RawText: "1 pound potatoes"
                ),
                new(
                    RefId: "ing_2",
                    CanonicalFoodItem: "garlic mayonnaise",
                    DisplayName: "Garlic Mayonnaise",
                    Quantity: 0.5m,
                    Unit: "cup",
                    IsOptional: true,
                    IsRecipeReference: false,
                    RawText: "1/2 cup Garlic Mayonnaise (optional; recipe follows)"
                ),
            ],
            Steps:
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Cook {{ing_1_a}} and serve with {{ing_2_a}}.",
                    IngredientTokens:
                    [
                        new("ing_1_a", "ing_1", 1, "lb"),
                        new("ing_2_a", "ing_2", 0.5m, "cup"),
                    ]
                ),
            ],
            QualityAudit: new RecipeQualityAuditDto(IsPlausibleRecipe: true, Warnings: [])
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

        using var authClient = customFactory.CreateClient();
        await AuthenticateAsync(authClient);

        var componentRes = await authClient.PostAsJsonAsync(
            "/api/recipes",
            new UpsertRecipeRequest(
                Title: "Garlic Mayonnaise",
                Description: "Creamy roasted garlic aioli",
                Status: RecipeStatus.Published,
                BaseServings: 4,
                Ingredients:
                [
                    new RecipeIngredientInput(
                        DisplayName: "mayonnaise",
                        Quantity: 1,
                        Unit: MeasurementUnit.Cup
                    ),
                ],
                Steps: [new RecipeStepInput(Instruction: "Whisk garlic with mayonnaise.")]
            )
        );
        componentRes.EnsureSuccessStatusCode();
        var mayoRecipe = await componentRes.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(mayoRecipe);

        var enqueueRes = await authClient.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(
                "Spanish Tortilla\nIngredients:\n- 1 pound potatoes\n- 1/2 cup Garlic Mayonnaise (optional; recipe follows)\nInstructions:\n1. Cook potatoes and serve.",
                AutoPublish: true
            )
        );
        enqueueRes.EnsureSuccessStatusCode();
        var queued = await enqueueRes.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(queued);

        using (var scope = customFactory.Services.CreateScope())
        {
            var processed = await scope
                .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                .ProcessNextQueuedJobAsync();
            Assert.IsTrue(processed);
        }

        var job = await authClient.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{queued.Id}"
        );
        Assert.IsNotNull(job);
        Assert.AreEqual(IngestionJobStatus.Published, job.Status);
        Assert.IsNotNull(job.ResultingRecipeId);

        var published = await authClient.GetFromJsonAsync<RecipeDetailDto>(
            $"/api/recipes/{job.ResultingRecipeId}"
        );
        Assert.IsNotNull(published);
        var linkedIng = published.Ingredients.FirstOrDefault(i =>
            i.DisplayName == "Garlic Mayonnaise"
        );
        Assert.IsNotNull(linkedIng);
        Assert.AreEqual(mayoRecipe.Id, linkedIng.LinkedRecipeId);
        Assert.IsNull(linkedIng.FoodItemId);
    }

    [TestMethod]
    public async Task TextIngestion_PostNormalizationSelfHealing_AutoPublishesWhenNormalizerResolvesStepIssues()
    {
        var fakeLlm = new FakeSequentialLlmExtractionClient(_ => new ExtractedRecipeDto(
            Title: "Skillet Flatbread",
            Ingredients:
            [
                new(
                    RefId: "ing_1",
                    CanonicalFoodItem: "flour",
                    DisplayName: "flour",
                    Quantity: 1m,
                    Unit: "cup",
                    RawText: "1 cup flour"
                ),
            ],
            Steps:
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Knead {{ing_1_a}}, dust {{ing_1_b}}, and shape {{ing_1_c}}.",
                    IngredientTokens:
                    [
                        new("ing_1_a", "ing_1", 1m, "cup"),
                        new("ing_1_b", "ing_1"),
                        new("ing_1_c", "ing_1"),
                    ]
                ),
            ],
            QualityAudit: new RecipeQualityAuditDto(IsPlausibleRecipe: true, Warnings: [])
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
                "Skillet Flatbread\nIngredients:\n- 1 cup flour\nInstructions:\n1. Knead flour, dust flour, and shape flour.",
                AutoPublish: true
            )
        );
        queuedResponse.EnsureSuccessStatusCode();
        var queued = await queuedResponse.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(queued);

        using (var scope = customFactory.Services.CreateScope())
        {
            var processed = await scope
                .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                .ProcessNextQueuedJobAsync();
            Assert.IsTrue(processed);
        }

        var job = await client.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{queued.Id}"
        );
        Assert.IsNotNull(job);
        Assert.AreEqual(IngestionJobStatus.Published, job.Status);
        Assert.IsNotNull(job.ResultingRecipeId);
    }

    [TestMethod]
    public async Task TextIngestion_PublishingSubRecipe_PromotesWaitingReadyForReviewCompositeRecipeJob()
    {
        const string toppingTitle = "Orange, Cranberry, and Mint Topping";
        const string compositeTitle = "Holiday Tart";

        var fakeLlm = new FakeSequentialLlmExtractionClient(context =>
        {
            if (context.SourceContent.Contains(compositeTitle, StringComparison.Ordinal))
            {
                return new ExtractedRecipeDto(
                    Title: compositeTitle,
                    Ingredients:
                    [
                        new(
                            RefId: "ing_1",
                            CanonicalFoodItem: toppingTitle,
                            DisplayName: toppingTitle,
                            Quantity: 1m,
                            Unit: "recipe",
                            IsRecipeReference: true,
                            ReferencedRecipeTitle: toppingTitle,
                            RawText: $"1 recipe {toppingTitle}"
                        ),
                    ],
                    Steps:
                    [
                        new(
                            StepNumber: 1,
                            TemplateText: "Spoon {{ing_1_a}} over tart.",
                            IngredientTokens: [new("ing_1_a", "ing_1")]
                        ),
                    ],
                    QualityAudit: new RecipeQualityAuditDto(IsPlausibleRecipe: true, Warnings: [])
                );
            }

            return new ExtractedRecipeDto(
                Title: toppingTitle,
                Ingredients:
                [
                    new(
                        RefId: "ing_1",
                        CanonicalFoodItem: "cranberries",
                        DisplayName: "cranberries",
                        Quantity: 1m,
                        Unit: "cup",
                        RawText: "1 cup cranberries"
                    ),
                ],
                Steps:
                [
                    new(
                        StepNumber: 1,
                        TemplateText: "Simmer {{ing_1_a}} with orange and mint.",
                        IngredientTokens: [new("ing_1_a", "ing_1")]
                    ),
                ],
                QualityAudit: new RecipeQualityAuditDto(IsPlausibleRecipe: true, Warnings: [])
            );
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

        var compositeQueuedRes = await client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(
                $"{compositeTitle}\nIngredients:\n- 1 recipe {toppingTitle}\nInstructions:\n1. Spoon topping over tart.",
                AutoPublish: true
            )
        );
        compositeQueuedRes.EnsureSuccessStatusCode();
        var compositeQueued =
            await compositeQueuedRes.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(compositeQueued);

        using (var scope = customFactory.Services.CreateScope())
        {
            var processed = await scope
                .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                .ProcessNextQueuedJobAsync();
            Assert.IsTrue(processed);
        }

        var waitingJob = await client.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{compositeQueued.Id}"
        );
        Assert.IsNotNull(waitingJob);
        Assert.AreEqual(IngestionJobStatus.ReadyForReview, waitingJob.Status);
        Assert.IsNotNull(waitingJob.StatusDetail);
        Assert.Contains("is not published yet", waitingJob.StatusDetail);

        var subQueuedRes = await client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(
                $"{toppingTitle}\nIngredients:\n- 1 cup cranberries\nInstructions:\n1. Simmer cranberries with orange and mint.",
                AutoPublish: true
            )
        );
        subQueuedRes.EnsureSuccessStatusCode();
        var subQueued = await subQueuedRes.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(subQueued);

        using (var scope = customFactory.Services.CreateScope())
        {
            var processed = await scope
                .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                .ProcessNextQueuedJobAsync();
            Assert.IsTrue(processed);
        }

        var subJob = await client.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{subQueued.Id}"
        );
        Assert.IsNotNull(subJob);
        Assert.AreEqual(IngestionJobStatus.Published, subJob.Status);
        Assert.IsNotNull(subJob.ResultingRecipeId);

        var promotedJob = await client.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{compositeQueued.Id}"
        );
        Assert.IsNotNull(promotedJob);
        Assert.AreEqual(IngestionJobStatus.Published, promotedJob.Status);
        Assert.IsNotNull(promotedJob.ResultingRecipeId);

        var publishedComposite = await client.GetFromJsonAsync<RecipeDetailDto>(
            $"/api/recipes/{promotedJob.ResultingRecipeId}"
        );
        Assert.IsNotNull(publishedComposite);
        var linkedIngredient = publishedComposite.Ingredients.FirstOrDefault(i =>
            i.DisplayName == toppingTitle
        );
        Assert.IsNotNull(linkedIngredient);
        Assert.AreEqual(subJob.ResultingRecipeId, linkedIngredient.LinkedRecipeId);
        Assert.IsNull(linkedIngredient.UnresolvedRecipeTitle);
    }

    [TestMethod]
    public async Task AutoPublish_ResolvesAliasedBuiltInTagsAndSkipsAutoCreatingCustomTags()
    {
        var fakeLlm = new FakeSequentialLlmExtractionClient(_ => new ExtractedRecipeDto(
            Title: "Soft-Boiled Eggs with Spinach",
            Description: "Simple eggs.",
            Servings: 2m,
            PrepTimeMinutes: 5,
            CookTimeMinutes: 10,
            TotalTimeMinutes: 15,
            ProposedTags: ["Boiled", "Egg", "Spinach", "Creamy"],
            Ingredients:
            [
                new(
                    RefId: "ing_1",
                    CanonicalFoodItem: "egg",
                    DisplayName: "egg",
                    Quantity: 4m,
                    Unit: "whole",
                    RawText: "4 large eggs"
                ),
            ],
            Steps:
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Boil {{ing_1_a}} for {{timer_1}}.",
                    IngredientTokens: [new("ing_1_a", "ing_1", 4m, "whole")],
                    TimerTokens: [new("timer_1", 420, "Boil eggs")]
                ),
            ],
            QualityAudit: new RecipeQualityAuditDto(IsPlausibleRecipe: true, Warnings: [])
        ));

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

        var enqueueRes = await client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(
                "Soft-Boiled Eggs with Spinach\nIngredients:\n- 4 large eggs\nInstructions:\n1. Boil eggs for 7 minutes.",
                AutoPublish: true
            )
        );
        enqueueRes.EnsureSuccessStatusCode();
        var queued = await enqueueRes.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(queued);

        using (var scope = customFactory.Services.CreateScope())
        {
            var processed = await scope
                .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                .ProcessNextQueuedJobAsync();
            Assert.IsTrue(processed);
        }

        var job = await client.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{queued.Id}"
        );
        Assert.IsNotNull(job);
        Assert.AreEqual(IngestionJobStatus.Published, job.Status);
        Assert.IsNotNull(job.ResultingRecipeId);

        var recipe = await client.GetFromJsonAsync<RecipeDetailDto>(
            $"/api/recipes/{job.ResultingRecipeId}"
        );
        Assert.IsNotNull(recipe);
        Assert.IsNotNull(recipe.Tags);
        Assert.Contains(t => t.Name == "Boil & Simmer", recipe.Tags);
        Assert.Contains(t => t.Name == "Eggs", recipe.Tags);
        Assert.DoesNotContain(t => t.Name == "Spinach", recipe.Tags);
        Assert.DoesNotContain(t => t.Name == "Creamy", recipe.Tags);

        using (var scope = customFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<App.Data.AppDbContext>();
            Assert.IsFalse(db.Tags.Any(t => t.Name == "Spinach" || t.Name == "Creamy"));
        }
    }

    [TestMethod]
    public async Task ManualReviewApprove_MaterializesExplicitCustomTag()
    {
        var fakeLlm = new FakeSequentialLlmExtractionClient(_ => new ExtractedRecipeDto(
            Title: "Artisan Toast",
            Description: "Simple toast.",
            Servings: 1m,
            PrepTimeMinutes: 2,
            CookTimeMinutes: 3,
            TotalTimeMinutes: 5,
            ProposedTags: ["Homemade Artisanal"],
            Ingredients:
            [
                new(
                    RefId: "ing_1",
                    CanonicalFoodItem: "sourdough bread",
                    DisplayName: "sourdough bread",
                    Quantity: 2m,
                    Unit: "slice",
                    RawText: "2 slices sourdough bread"
                ),
            ],
            Steps:
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Toast {{ing_1_a}}.",
                    IngredientTokens: [new("ing_1_a", "ing_1", 2m, "slice")]
                ),
            ],
            QualityAudit: new RecipeQualityAuditDto(IsPlausibleRecipe: true, Warnings: [])
        ));

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

        var enqueueRes = await client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(
                "Artisan Toast\nIngredients:\n- 2 slices sourdough bread\nInstructions:\n1. Toast bread.",
                AutoPublish: false
            )
        );
        enqueueRes.EnsureSuccessStatusCode();
        var queued = await enqueueRes.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(queued);

        using (var scope = customFactory.Services.CreateScope())
        {
            var processed = await scope
                .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                .ProcessNextQueuedJobAsync();
            Assert.IsTrue(processed);
        }

        var job = await client.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{queued.Id}"
        );
        Assert.IsNotNull(job);
        Assert.AreEqual(IngestionJobStatus.ReadyForReview, job.Status);
        Assert.IsNotNull(job.ExtractedDraft);
        Assert.IsNotNull(job.ExtractedDraft.ProposedTagNames);
        Assert.Contains("Homemade Artisanal", job.ExtractedDraft.ProposedTagNames);

        var approveRes = await client.PostAsJsonAsync(
            $"/api/ingestion/jobs/{queued.Id}/approve",
            job.ExtractedDraft
        );
        Assert.AreEqual(HttpStatusCode.Created, approveRes.StatusCode);

        var published = await approveRes.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(published);
        Assert.IsNotNull(published.Tags);
        var customTag = published.Tags.SingleOrDefault(t => t.Name == "Homemade Artisanal");
        Assert.IsNotNull(customTag);
        Assert.AreEqual(TagCategory.Custom, customTag.Category);
    }

    [TestMethod]
    public async Task AutoPublish_ValidationExceptionDuringPublish_DemotesToReadyForReviewWithSavedDraft()
    {
        var fakeLlm = new FakeSequentialLlmExtractionClient(_ => new ExtractedRecipeDto(
            Title: "Easy-Peel Hard-Cooked Eggs",
            Description: "Steamed eggs.",
            Servings: 6m,
            Ingredients:
            [
                new(
                    RefId: "ing_1",
                    CanonicalFoodItem: "egg",
                    DisplayName: "large eggs",
                    Quantity: 6m,
                    Unit: "whole",
                    RawText: "6 large eggs"
                ),
            ],
            Steps:
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Steam {{ing_1_a}} for {{timer_1}}.",
                    IngredientTokens: [new("ing_1_a", "ing_1", 6m, "whole")],
                    TimerTokens: [new("timer_1", 780, "Steam eggs")]
                ),
            ],
            QualityAudit: new RecipeQualityAuditDto(IsPlausibleRecipe: true, Warnings: [])
        ));

        var throwingProxy = System.Reflection.DispatchProxy.Create<
            IRecipeService,
            ThrowingCreateRecipeServiceProxy
        >();

        await using var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Llm:TextModel", "test-model");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILlmExtractionClient>();
                services.AddSingleton<ILlmExtractionClient>(fakeLlm);
                services.RemoveAll<IRecipeService>();
                services.AddSingleton<IRecipeService>(throwingProxy);
            });
        });

        using var client = customFactory.CreateClient();
        await AuthenticateAsync(client);

        var enqueueRes = await client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(
                "Easy-Peel Hard-Cooked Eggs\nIngredients:\n- 6 large eggs\nInstructions:\n1. Steam eggs 13 minutes.",
                AutoPublish: true
            )
        );
        enqueueRes.EnsureSuccessStatusCode();
        var queued = await enqueueRes.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(queued);

        using (var scope = customFactory.Services.CreateScope())
        {
            var processed = await scope
                .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                .ProcessNextQueuedJobAsync();
            Assert.IsTrue(processed);
        }

        var job = await client.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{queued.Id}"
        );
        Assert.IsNotNull(job);
        Assert.AreEqual(IngestionJobStatus.ReadyForReview, job.Status);
        Assert.IsNotNull(job.ExtractedDraft);
        StringAssert.Contains(job.StatusDetail, "Simulated recipe publish failure");
        Assert.Contains(
            issue => issue.Contains("Simulated recipe publish failure", StringComparison.Ordinal),
            job.ExtractedDraft.ValidationIssues!
        );
    }

    [TestMethod]
    public async Task IngestionProcessor_SplitSubRecipeReferenceWithForDishPrepNote_LinksPublishedRecipeAndAutoPublishes()
    {
        var fakeLlm = new FakeSequentialLlmExtractionClient(_ => new ExtractedRecipeDto(
            Title: "Bruschetta",
            Description: "Crisp toasted bread topped with ripe tomatoes.",
            Servings: 4,
            CookTimeMinutes: 0,
            ProposedTags: ["Appetizer", "Italian"],
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "toasted bread",
                    DisplayName: "Toasted Bread",
                    Quantity: 1m,
                    Unit: "recipe",
                    PreparationNotes: "for Bruschetta",
                    IsRecipeReference: true,
                    ReferencedRecipeTitle: "toasted bread",
                    RawText: "1 recipe Toasted Bread for Bruschetta"
                ),
                new(
                    "ing_2",
                    CanonicalFoodItem: "tomato",
                    DisplayName: "ripe tomatoes",
                    Quantity: 4m,
                    Unit: "whole",
                    RawText: "4 ripe tomatoes"
                ),
            ],
            Steps:
            [
                new(
                    1,
                    TemplateText: "Dice {{ing_2_a}} and spoon over {{ing_1_a}}.",
                    IngredientTokens:
                    [
                        new("ing_2_a", "ing_2", 4m, "whole"),
                        new("ing_1_a", "ing_1", 1m, "recipe"),
                    ]
                ),
            ],
            QualityAudit: new RecipeQualityAuditDto(IsPlausibleRecipe: true, Warnings: [])
        ));

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

        var breadRes = await client.PostAsJsonAsync(
            "/api/recipes",
            new UpsertRecipeRequest(
                Title: "Toasted Bread for Bruschetta",
                Description: "Grilled bread slices.",
                Status: RecipeStatus.Published,
                BaseServings: 4,
                Ingredients:
                [
                    new RecipeIngredientInput(
                        DisplayName: "baguette",
                        Quantity: 1m,
                        Unit: MeasurementUnit.Whole
                    ),
                ],
                Steps: [new RecipeStepInput(Instruction: "Slice and toast baguette.")]
            )
        );
        Assert.AreEqual(HttpStatusCode.Created, breadRes.StatusCode);
        var bread = await breadRes.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(bread);

        var enqueueRes = await client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(
                "Bruschetta\nIngredients:\n- 1 recipe Toasted Bread for Bruschetta\n- 4 ripe tomatoes\nInstructions:\n1. Dice tomatoes and spoon over bread.",
                AutoPublish: true
            )
        );
        enqueueRes.EnsureSuccessStatusCode();
        var queued = await enqueueRes.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(queued);

        using (var scope = customFactory.Services.CreateScope())
        {
            var processed = await scope
                .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                .ProcessNextQueuedJobAsync();
            Assert.IsTrue(processed);
        }

        var job = await client.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{queued.Id}"
        );
        Assert.IsNotNull(job);
        Assert.AreEqual(IngestionJobStatus.Published, job.Status, job.StatusDetail);
        Assert.IsNotNull(job.ResultingRecipeId);

        var published = await client.GetFromJsonAsync<RecipeDetailDto>(
            $"/api/recipes/{job.ResultingRecipeId}"
        );
        Assert.IsNotNull(published);
        Assert.AreEqual(bread.Id, published.Ingredients[0].LinkedRecipeId);
        Assert.IsNull(published.Ingredients[0].UnresolvedRecipeTitle);
        Assert.IsNull(published.Ingredients[0].PreparationNote);
    }

    [TestMethod]
    public async Task IngestionProcessor_RawGroceryWithSeeThisPageOrFacingPage_DoesNotPromoteIntoUnresolvedReference()
    {
        var fakeLlm = new FakeSequentialLlmExtractionClient(_ => new ExtractedRecipeDto(
            Title: "Gỏi Cuốn (Vietnamese Summer Rolls)",
            Description: "Fresh rice paper rolls with shrimp.",
            Servings: 4,
            CookTimeMinutes: 0,
            ProposedTags: ["Appetizer", "Vietnamese"],
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "jumbo shrimp",
                    DisplayName: "jumbo shrimp",
                    Quantity: 1m,
                    Unit: "lb",
                    PreparationNotes: "peeled, deveined (see this page)",
                    IsRecipeReference: false,
                    RawText: "1 pound peeled, deveined jumbo shrimp (see this page)"
                ),
                new(
                    "ing_2",
                    CanonicalFoodItem: "rice paper wrappers",
                    DisplayName: "rice paper wrappers",
                    Quantity: 8m,
                    Unit: "whole",
                    PreparationNotes: "facing page",
                    IsRecipeReference: false,
                    RawText: "8 rice paper wrappers (facing page)"
                ),
            ],
            Steps:
            [
                new(
                    1,
                    TemplateText: "Wrap {{ing_1_a}} inside softened {{ing_2_a}}.",
                    IngredientTokens:
                    [
                        new("ing_1_a", "ing_1", 1m, "lb"),
                        new("ing_2_a", "ing_2", 8m, "whole"),
                    ]
                ),
            ],
            QualityAudit: new RecipeQualityAuditDto(IsPlausibleRecipe: true, Warnings: [])
        ));

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

        var enqueueRes = await client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(
                "Goi Cuon\nIngredients:\n- 1 pound peeled, deveined jumbo shrimp (see this page)\n- 8 rice paper wrappers (facing page)\nInstructions:\n1. Wrap shrimp in wrappers.",
                AutoPublish: true
            )
        );
        enqueueRes.EnsureSuccessStatusCode();
        var queued = await enqueueRes.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(queued);

        using (var scope = customFactory.Services.CreateScope())
        {
            var processed = await scope
                .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                .ProcessNextQueuedJobAsync();
            Assert.IsTrue(processed);
        }

        var job = await client.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{queued.Id}"
        );
        Assert.IsNotNull(job);
        Assert.AreEqual(IngestionJobStatus.Published, job.Status, job.StatusDetail);
        Assert.IsNotNull(job.ResultingRecipeId);

        var published = await client.GetFromJsonAsync<RecipeDetailDto>(
            $"/api/recipes/{job.ResultingRecipeId}"
        );
        Assert.IsNotNull(published);
        Assert.IsNull(published.Ingredients[0].LinkedRecipeId);
        Assert.IsNull(published.Ingredients[0].UnresolvedRecipeTitle);
        Assert.IsNull(published.Ingredients[1].LinkedRecipeId);
        Assert.IsNull(published.Ingredients[1].UnresolvedRecipeTitle);
    }

    [TestMethod]
    public async Task ApproveDraft_WhenReferencedRecipeTitleIsEdited_ReResolvesInsteadOfUsingStaleMatchedRecipeId()
    {
        await AuthenticateAsync(_client);

        var oldSauceRes = await _client.PostAsJsonAsync(
            "/api/recipes",
            new UpsertRecipeRequest(
                Title: "Old Tomato Sauce",
                Status: RecipeStatus.Published,
                BaseServings: 4m
            )
        );
        Assert.AreEqual(HttpStatusCode.Created, oldSauceRes.StatusCode);
        var oldSauce = await oldSauceRes.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(oldSauce);

        var newMarinaraRes = await _client.PostAsJsonAsync(
            "/api/recipes",
            new UpsertRecipeRequest(
                Title: "Fresh Basil Marinara",
                Status: RecipeStatus.Published,
                BaseServings: 4m
            )
        );
        Assert.AreEqual(HttpStatusCode.Created, newMarinaraRes.StatusCode);
        var newMarinara = await newMarinaraRes.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(newMarinara);

        var enqueueRes = await _client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(
                RawText: "Baked Ziti\nIngredients:\n- 1 recipe Fresh Basil Marinara\nSteps:\n1. Bake.",
                TitleHint: "Baked Ziti"
            )
        );
        Assert.AreEqual(HttpStatusCode.Accepted, enqueueRes.StatusCode);
        var job = await enqueueRes.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(job);

        // Save draft where title was changed to an unpublished recipe but stale MatchedRecipeId remained
        var unlinkedDraft = new IngestionExtractedDraftDto(
            Title: "Baked Ziti",
            BaseServings: 4m,
            Ingredients:
            [
                new IngestionDraftIngredientDto(
                    DisplayName: "Unpublished Vodka Sauce",
                    Quantity: 1m,
                    Unit: MeasurementUnit.Custom,
                    CustomUnitName: "recipe",
                    IsRecipeReference: true,
                    ReferencedRecipeTitle: "Unpublished Vodka Sauce",
                    MatchedRecipeId: oldSauce.Id,
                    MatchedRecipeTitle: oldSauce.Title,
                    MatchedRecipeSlug: oldSauce.Slug,
                    CreateNewFoodItem: false
                ),
            ],
            Steps:
            [
                new IngestionDraftStepDto(SectionName: null, Instruction: "Bake ziti with sauce."),
            ]
        );

        var saveUnlinkedRes = await _client.PutAsJsonAsync(
            $"/api/ingestion/jobs/{job.Id}/draft",
            unlinkedDraft
        );
        Assert.AreEqual(HttpStatusCode.OK, saveUnlinkedRes.StatusCode);
        var unlinkedDetail =
            await saveUnlinkedRes.Content.ReadFromJsonAsync<IngestionJobDetailDto>();
        Assert.IsNotNull(unlinkedDetail?.ExtractedDraft?.Ingredients);
        Assert.IsNull(unlinkedDetail.ExtractedDraft.Ingredients[0].MatchedRecipeId);

        // Approve draft where title was edited to Fresh Basil Marinara while stale MatchedRecipeId still pointed at Old Tomato Sauce
        var editedDraft = unlinkedDraft with
        {
            Ingredients =
            [
                unlinkedDraft.Ingredients![0] with
                {
                    DisplayName = newMarinara.Title,
                    ReferencedRecipeTitle = newMarinara.Title,
                    MatchedRecipeId = oldSauce.Id,
                    MatchedRecipeTitle = oldSauce.Title,
                    MatchedRecipeSlug = oldSauce.Slug,
                },
            ],
        };

        var approveRes = await _client.PostAsJsonAsync(
            $"/api/ingestion/jobs/{job.Id}/approve",
            editedDraft
        );
        Assert.AreEqual(HttpStatusCode.Created, approveRes.StatusCode);
        var published = await approveRes.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(published);
        Assert.AreEqual(newMarinara.Id, published.Ingredients[0].LinkedRecipeId);
        Assert.IsNull(published.Ingredients[0].UnresolvedRecipeTitle);
    }

    private class ThrowingCreateRecipeServiceProxy : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(
            System.Reflection.MethodInfo? targetMethod,
            object?[]? args
        )
        {
            if (targetMethod?.Name == nameof(IRecipeService.CreateRecipeAsync))
            {
                return Task.FromException<RecipeDetailDto>(
                    new App.Core.Exceptions.DomainValidationException(
                        "Simulated recipe publish failure."
                    )
                );
            }

            throw new NotSupportedException($"Unexpected call to {targetMethod?.Name}");
        }
    }

    private sealed class FakeSequentialLlmExtractionClient(
        Func<LlmExtractionContext, ExtractedRecipeDto> handler
    ) : ILlmExtractionClient
    {
        private readonly TestStagePipelineResponder _responder = new(handler);

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

        public Task<LlmStructuredStageResult<T>> ExecuteStructuredStageAsync<T>(
            EffectiveLlmConnectionConfig config,
            string stageName,
            IReadOnlyList<LlmStageChatMessage> messages,
            System.Text.Json.Nodes.JsonNode? jsonSchema,
            bool? overrideThinking = null,
            int maxTokens = 16384,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(_responder.Respond<T>(config, stageName, messages));

        public Task<float[][]> GenerateEmbeddingsAsync(
            EffectiveLlmConnectionConfig config,
            IReadOnlyList<string> inputs,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(Array.Empty<float[]>());

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
        public Task<string> AnswerKitchenQuestionAsync(
            EffectiveLlmConnectionConfig config,
            LlmKitchenQuestionContext context,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        private readonly TestStagePipelineResponder _responder = new(
            context => new ExtractedRecipeDto(
                Title: context.SourceContent.Contains(
                    "Serialized Job One",
                    StringComparison.Ordinal
                )
                    ? "Serialized Job One"
                    : "Serialized Job Two",
                Servings: 4m,
                PrepTimeMinutes: 5,
                CookTimeMinutes: 10,
                TotalTimeMinutes: 15,
                Ingredients:
                [
                    new(
                        RefId: "ing_1",
                        CanonicalFoodItem: "flour",
                        DisplayName: "flour",
                        Quantity: 1m,
                        Unit: "cup",
                        RawText: "1 cup flour"
                    ),
                ],
                Steps:
                [
                    new(
                        StepNumber: 1,
                        TemplateText: "Mix {{ing_1_a}} and bake.",
                        IngredientTokens: [new("ing_1_a", "ing_1")]
                    ),
                ],
                QualityAudit: new RecipeQualityAuditDto(IsPlausibleRecipe: true, Warnings: [])
            )
        );

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

        public async Task<LlmStructuredStageResult<T>> ExecuteStructuredStageAsync<T>(
            EffectiveLlmConnectionConfig config,
            string stageName,
            IReadOnlyList<LlmStageChatMessage> messages,
            System.Text.Json.Nodes.JsonNode? jsonSchema,
            bool? overrideThinking = null,
            int maxTokens = 16384,
            CancellationToken cancellationToken = default
        )
        {
            if (stageName == "stage1_extract_recipe")
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

                    return _responder.Respond<T>(config, stageName, messages);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeRequests);
                }
            }

            return _responder.Respond<T>(config, stageName, messages);
        }

        public Task<float[][]> GenerateEmbeddingsAsync(
            EffectiveLlmConnectionConfig config,
            IReadOnlyList<string> inputs,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(Array.Empty<float[]>());

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

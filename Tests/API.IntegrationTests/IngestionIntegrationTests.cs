using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using App.Core.Authentication;
using App.Core.FoodCatalog;
using App.Core.Ingestion;
using App.Core.Recipes;
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

        // Get Store Aisles to pick an aisle
        var aislesResp = await _client.GetAsync("/api/store-aisles");
        var aisles = await aislesResp.Content.ReadFromJsonAsync<IReadOnlyList<StoreAisleDto>>();
        Assert.IsNotNull(aisles);
        var spicesAisle =
            aisles.FirstOrDefault(a => a.Name.Contains("Spices") || a.Name.Contains("Other"))
            ?? aisles[0];

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
                    SuggestedStoreAisleId: spicesAisle.Id,
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
}

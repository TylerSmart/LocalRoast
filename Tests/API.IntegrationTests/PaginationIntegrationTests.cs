using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using App.Core.Authentication;
using App.Core.Common;
using App.Core.FoodCatalog;
using App.Core.Ingestion;
using App.Core.Recipes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.API.IntegrationTests;

[TestClass]
public sealed class PaginationIntegrationTests
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

    private static async Task AuthenticateAsync(HttpClient client)
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
    }

    [TestMethod]
    public async Task IngestionJobs_PagedEndpoint_ReturnsPagedResultWithCorrectMetadata()
    {
        await AuthenticateAsync(_client);

        // Enqueue two text jobs
        for (var i = 1; i <= 3; i++)
        {
            var textReq = new EnqueueRawTextIngestionRequest(
                RawText: $"Recipe content {i}",
                TitleHint: $"Paged Job Test Recipe {i}"
            );
            var enqueueResp = await _client.PostAsJsonAsync("/api/ingestion/jobs/text", textReq);
            Assert.AreEqual(HttpStatusCode.Accepted, enqueueResp.StatusCode);
        }

        // Request page 1 with pageSize 2
        var pagedResp = await _client.GetAsync("/api/ingestion/jobs?page=1&pageSize=2");
        Assert.AreEqual(HttpStatusCode.OK, pagedResp.StatusCode);

        var paged = await pagedResp.Content.ReadFromJsonAsync<
            PagedResultDto<IngestionJobSummaryDto>
        >();
        Assert.IsNotNull(paged);
        Assert.AreEqual(1, paged.PageNumber);
        Assert.AreEqual(2, paged.PageSize);
        Assert.IsTrue(paged.TotalCount >= 3);
        Assert.AreEqual(2, paged.Items.Count);
        Assert.IsTrue(paged.HasNextPage);
        Assert.IsFalse(paged.HasPreviousPage);

        // Request page 2 with pageSize 2
        var page2Resp = await _client.GetAsync("/api/ingestion/jobs?page=2&pageSize=2");
        Assert.AreEqual(HttpStatusCode.OK, page2Resp.StatusCode);
        var page2 = await page2Resp.Content.ReadFromJsonAsync<
            PagedResultDto<IngestionJobSummaryDto>
        >();
        Assert.IsNotNull(page2);
        Assert.AreEqual(2, page2.PageNumber);
        Assert.IsTrue(page2.HasPreviousPage);

        // Summary should include AllCount
        var summaryResp = await _client.GetAsync("/api/ingestion/jobs/summary");
        Assert.AreEqual(HttpStatusCode.OK, summaryResp.StatusCode);
        var summary = await summaryResp.Content.ReadFromJsonAsync<IngestionQueueSummaryDto>();
        Assert.IsNotNull(summary);
        Assert.IsTrue(summary.AllCount >= 3);
    }

    [TestMethod]
    public async Task Recipes_PagedEndpoint_ReturnsPagedResultWithCorrectMetadata()
    {
        await AuthenticateAsync(_client);

        for (var i = 1; i <= 3; i++)
        {
            var req = new UpsertRecipeRequest(
                Title: $"Paged Recipe {i} {Guid.NewGuid():N}",
                BaseServings: 4,
                Status: RecipeStatus.Published
            );
            var resp = await _client.PostAsJsonAsync("/api/recipes", req);
            Assert.AreEqual(HttpStatusCode.Created, resp.StatusCode);
        }

        var pagedResp = await _client.GetAsync("/api/recipes?page=1&pageSize=2");
        Assert.AreEqual(HttpStatusCode.OK, pagedResp.StatusCode);

        var paged = await pagedResp.Content.ReadFromJsonAsync<PagedResultDto<RecipeSummaryDto>>();
        Assert.IsNotNull(paged);
        Assert.AreEqual(1, paged.PageNumber);
        Assert.AreEqual(2, paged.PageSize);
        Assert.IsTrue(paged.TotalCount >= 3);
        Assert.AreEqual(2, paged.Items.Count);
    }

    [TestMethod]
    public async Task FoodCatalog_PagedEndpoints_ReturnPagedResultWithCorrectMetadata()
    {
        await AuthenticateAsync(_client);

        for (var i = 1; i <= 3; i++)
        {
            var req = new CreateFoodItemRequest($"Paged Food Item {i} {Guid.NewGuid():N}");
            var resp = await _client.PostAsJsonAsync("/api/food-items", req);
            Assert.AreEqual(HttpStatusCode.Created, resp.StatusCode);
        }

        var pagedResp = await _client.GetAsync("/api/food-items?page=1&pageSize=2");
        Assert.AreEqual(HttpStatusCode.OK, pagedResp.StatusCode);

        var paged = await pagedResp.Content.ReadFromJsonAsync<PagedResultDto<FoodItemDto>>();
        Assert.IsNotNull(paged);
        Assert.AreEqual(1, paged.PageNumber);
        Assert.AreEqual(2, paged.PageSize);
        Assert.IsTrue(paged.TotalCount >= 3);
        Assert.AreEqual(2, paged.Items.Count);
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using App.Core.Authentication;
using App.Core.Media;
using App.Core.Recipes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace App.API.IntegrationTests;

[TestClass]
public sealed class CookLogsAndDiscoveryIntegrationTests
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

    private static async Task<AuthResponse> RegisterAndAuthenticateAsync(
        HttpClient client,
        string email,
        string username
    )
    {
        var register = new RegisterRequest(email, username, "Password123!");
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

    private static byte[] CreateTestPng(int width = 400, int height = 300)
    {
        using var image = new Image<Rgba32>(width, height);
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private async Task<RecipeDetailDto> CreateTestRecipeAsync(string title, string slug)
    {
        var req = new UpsertRecipeRequest(
            Title: title,
            Description: "Delicious test recipe",
            Status: RecipeStatus.Published,
            BaseServings: 4m,
            Ingredients: [new RecipeIngredientInput(DisplayName: "Pasta", Quantity: 500)],
            Steps: [new RecipeStepInput(Instruction: "Boil pasta and sauce.")]
        );

        var res = await _client.PostAsJsonAsync("/api/recipes", req);
        Assert.AreEqual(HttpStatusCode.Created, res.StatusCode);
        var detail = await res.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(detail);
        return detail;
    }

    [TestMethod]
    public async Task CookLog_FullCrudLifecycle_AttributionAndStatsRecomputation()
    {
        var auth = await RegisterAndAuthenticateAsync(_client, "chef@example.com", "chef_tyler");
        var recipe = await CreateTestRecipeAsync("Creamy Carbonara", "creamy-carbonara");

        // 1. Photo upload
        var imageBytes = CreateTestPng(800, 600);
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(imageBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(fileContent, "file", "carbonara-dish.png");

        var photoRes = await _client.PostAsync("/api/media/cook-log-photo", content);
        Assert.AreEqual(HttpStatusCode.OK, photoRes.StatusCode);
        var photoDto = await photoRes.Content.ReadFromJsonAsync<UploadedCookLogPhotoDto>();
        Assert.IsNotNull(photoDto);
        StringAssert.StartsWith(photoDto.PhotoImagePath, "/api/uploads/cook-logs/");

        // 2. Create CookLog
        var cookReq = new UpsertCookLogRequest(
            ServingsMade: 4m,
            Rating: 5,
            Notes: "Used guanciale and pecorino romano. Crispy and silky!",
            PhotoImagePath: photoDto.PhotoImagePath,
            PhotoThumbnailPath: photoDto.PhotoThumbnailPath
        );

        var createRes = await _client.PostAsJsonAsync(
            $"/api/recipes/{recipe.Id}/cook-logs",
            cookReq
        );
        Assert.AreEqual(HttpStatusCode.Created, createRes.StatusCode);

        var mutation = await createRes.Content.ReadFromJsonAsync<CookLogMutationResultDto>();
        Assert.IsNotNull(mutation);
        Assert.AreEqual("chef_tyler", mutation.CookLog.CookedByDisplayName);
        Assert.AreEqual(5, mutation.CookLog.Rating);
        Assert.AreEqual(1, mutation.RecipeStats.TimesCooked);
        Assert.AreEqual(5.00m, mutation.RecipeStats.AverageRating);
        Assert.AreEqual(1, mutation.RecipeStats.RatingCount);
        Assert.AreEqual(
            "Used guanciale and pecorino romano. Crispy and silky!",
            mutation.RecipeStats.LatestCookNote
        );

        // 3. GET cook logs for recipe
        var getLogsRes = await _client.GetAsync($"/api/recipes/{recipe.Id}/cook-logs");
        Assert.AreEqual(HttpStatusCode.OK, getLogsRes.StatusCode);
        var logsList = await getLogsRes.Content.ReadFromJsonAsync<IReadOnlyList<CookLogDto>>();
        Assert.IsNotNull(logsList);
        Assert.AreEqual(1, logsList.Count);
        Assert.AreEqual(mutation.CookLog.Id, logsList[0].Id);

        // 4. Verify Recipe Detail includes CookLogs
        var recipeDetailRes = await _client.GetAsync($"/api/recipes/{recipe.Id}");
        Assert.AreEqual(HttpStatusCode.OK, recipeDetailRes.StatusCode);
        var recipeDetail = await recipeDetailRes.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(recipeDetail);
        Assert.AreEqual(1, recipeDetail.TimesCooked);
        Assert.AreEqual(5.00m, recipeDetail.AverageRating);
        Assert.AreEqual(1, recipeDetail.RatingCount);
        Assert.AreEqual(1, recipeDetail.CookLogs.Count);

        // 5. Update CookLog
        var updateReq = new UpsertCookLogRequest(
            ServingsMade: 2m,
            Rating: 4,
            Notes: "Turned out slightly salty, tone down pecorino next time."
        );
        var updateRes = await _client.PutAsJsonAsync(
            $"/api/recipes/{recipe.Id}/cook-logs/{mutation.CookLog.Id}",
            updateReq
        );
        Assert.AreEqual(HttpStatusCode.OK, updateRes.StatusCode);
        var updatedMutation = await updateRes.Content.ReadFromJsonAsync<CookLogMutationResultDto>();
        Assert.IsNotNull(updatedMutation);
        Assert.AreEqual(4, updatedMutation.CookLog.Rating);
        Assert.AreEqual(4.00m, updatedMutation.RecipeStats.AverageRating);
        Assert.AreEqual(
            "Turned out slightly salty, tone down pecorino next time.",
            updatedMutation.RecipeStats.LatestCookNote
        );

        // 6. Delete CookLog
        var deleteRes = await _client.DeleteAsync(
            $"/api/recipes/{recipe.Id}/cook-logs/{mutation.CookLog.Id}"
        );
        Assert.AreEqual(HttpStatusCode.NoContent, deleteRes.StatusCode);

        // Verify Recipe Stats after delete
        var recipeDetailAfterDelRes = await _client.GetAsync($"/api/recipes/{recipe.Id}");
        var recipeAfterDel =
            await recipeDetailAfterDelRes.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(recipeAfterDel);
        Assert.AreEqual(0, recipeAfterDel.TimesCooked);
        Assert.IsNull(recipeAfterDel.AverageRating);
        Assert.AreEqual(0, recipeAfterDel.RatingCount);
        Assert.AreEqual(0, recipeAfterDel.CookLogs.Count);
    }

    [TestMethod]
    public async Task CookLog_Validation_EnforcesConstraints()
    {
        await RegisterAndAuthenticateAsync(_client, "val@example.com", "val_user");
        var recipe = await CreateTestRecipeAsync("Test Stew", "test-stew");

        // Rating > 5
        var badRatingRes = await _client.PostAsJsonAsync(
            $"/api/recipes/{recipe.Id}/cook-logs",
            new UpsertCookLogRequest(Rating: 6)
        );
        Assert.AreEqual(HttpStatusCode.BadRequest, badRatingRes.StatusCode);

        // Rating < 1
        var badRatingLowRes = await _client.PostAsJsonAsync(
            $"/api/recipes/{recipe.Id}/cook-logs",
            new UpsertCookLogRequest(Rating: 0)
        );
        Assert.AreEqual(HttpStatusCode.BadRequest, badRatingLowRes.StatusCode);

        // ServingsMade < 0.25
        var badServingsRes = await _client.PostAsJsonAsync(
            $"/api/recipes/{recipe.Id}/cook-logs",
            new UpsertCookLogRequest(ServingsMade: 0.1m)
        );
        Assert.AreEqual(HttpStatusCode.BadRequest, badServingsRes.StatusCode);

        // Future date > 24 hours
        var badFutureDateRes = await _client.PostAsJsonAsync(
            $"/api/recipes/{recipe.Id}/cook-logs",
            new UpsertCookLogRequest(CookedAt: DateTimeOffset.UtcNow.AddDays(3))
        );
        Assert.AreEqual(HttpStatusCode.BadRequest, badFutureDateRes.StatusCode);

        // Invalid photo path
        var badPhotoRes = await _client.PostAsJsonAsync(
            $"/api/recipes/{recipe.Id}/cook-logs",
            new UpsertCookLogRequest(PhotoImagePath: "../../../secrets.txt")
        );
        Assert.AreEqual(HttpStatusCode.BadRequest, badPhotoRes.StatusCode);

        // Unknown recipe
        var notFoundRes = await _client.PostAsJsonAsync(
            $"/api/recipes/{Guid.NewGuid()}/cook-logs",
            new UpsertCookLogRequest(Rating: 5)
        );
        Assert.AreEqual(HttpStatusCode.NotFound, notFoundRes.StatusCode);
    }

    [TestMethod]
    public async Task CookLogNotes_SearchAndForgottenFavoritesFiltering()
    {
        await RegisterAndAuthenticateAsync(_client, "searcher@example.com", "searcher");
        var recipe1 = await CreateTestRecipeAsync("Spicy Ramen", "spicy-ramen");
        var recipe2 = await CreateTestRecipeAsync("Plain Oatmeal", "plain-oatmeal");

        // Add cook log to recipe 1 with distinctive note keyword
        var log1Res = await _client.PostAsJsonAsync(
            $"/api/recipes/{recipe1.Id}/cook-logs",
            new UpsertCookLogRequest(
                CookedAt: DateTimeOffset.UtcNow.AddDays(-100),
                Rating: 5,
                Notes: "Added black garlic oil and extra scallions!"
            )
        );
        Assert.AreEqual(HttpStatusCode.Created, log1Res.StatusCode);

        // Search by cook note keyword
        var searchRes = await _client.GetAsync("/api/recipes?search=black+garlic");
        Assert.AreEqual(HttpStatusCode.OK, searchRes.StatusCode);
        var searchResults = await searchRes.Content.ReadFromJsonAsync<
            IReadOnlyList<RecipeSummaryDto>
        >();
        Assert.IsNotNull(searchResults);
        Assert.AreEqual(1, searchResults.Count);
        Assert.AreEqual("Spicy Ramen", searchResults[0].Title);
        Assert.AreEqual(
            "Added black garlic oil and extra scallions!",
            searchResults[0].LatestCookNote
        );

        // Test MinRating filter
        var minRatingRes = await _client.GetAsync("/api/recipes?minRating=4.5");
        Assert.AreEqual(HttpStatusCode.OK, minRatingRes.StatusCode);
        var minRatingResults = await minRatingRes.Content.ReadFromJsonAsync<
            IReadOnlyList<RecipeSummaryDto>
        >();
        Assert.IsNotNull(minRatingResults);
        Assert.IsTrue(minRatingResults.Any(r => r.Id == recipe1.Id));
        Assert.IsFalse(minRatingResults.Any(r => r.Id == recipe2.Id));

        // Test ForgottenFavoritesOnly filter
        var forgottenRes = await _client.GetAsync(
            "/api/recipes?forgottenFavoritesOnly=true&forgottenDaysThreshold=60"
        );
        Assert.AreEqual(HttpStatusCode.OK, forgottenRes.StatusCode);
        var forgottenResults = await forgottenRes.Content.ReadFromJsonAsync<
            IReadOnlyList<RecipeSummaryDto>
        >();
        Assert.IsNotNull(forgottenResults);
        Assert.IsTrue(forgottenResults.Any(r => r.Id == recipe1.Id));
        Assert.IsFalse(forgottenResults.Any(r => r.Id == recipe2.Id));
    }

    [TestMethod]
    public async Task ShareLink_RespectsIncludeCookLogsPrivacyBoundary()
    {
        await RegisterAndAuthenticateAsync(_client, "sharer@example.com", "sharer_user");
        var recipe = await CreateTestRecipeAsync("Secret Family Lasagna", "secret-family-lasagna");

        await _client.PostAsJsonAsync(
            $"/api/recipes/{recipe.Id}/cook-logs",
            new UpsertCookLogRequest(Rating: 5, Notes: "Layered with homemade ricotta and fontina.")
        );

        // 1. Create ShareLink with IncludeCookLogs = true
        var linkWithLogsRes = await _client.PostAsJsonAsync(
            $"/api/recipes/{recipe.Id}/shares",
            new CreateShareLinkRequest(IncludeCookLogs: true)
        );
        Assert.AreEqual(HttpStatusCode.Created, linkWithLogsRes.StatusCode);
        var linkWithLogs = await linkWithLogsRes.Content.ReadFromJsonAsync<ShareLinkDto>();
        Assert.IsNotNull(linkWithLogs);

        // Fetch public recipe
        var publicClient = _factory.CreateClient();
        var publicWithLogsRes = await publicClient.GetAsync(
            $"/api/public/shares/{linkWithLogs.Token}"
        );
        Assert.AreEqual(HttpStatusCode.OK, publicWithLogsRes.StatusCode);
        var publicWithLogs =
            await publicWithLogsRes.Content.ReadFromJsonAsync<PublicSharedRecipeDto>();
        Assert.IsNotNull(publicWithLogs);
        Assert.AreEqual(1, publicWithLogs.TimesCooked);
        Assert.AreEqual(5.00m, publicWithLogs.AverageRating);
        Assert.AreEqual(1, publicWithLogs.CookLogs.Count);
        Assert.AreEqual(
            "Layered with homemade ricotta and fontina.",
            publicWithLogs.CookLogs[0].Notes
        );

        // 2. Create ShareLink with IncludeCookLogs = false
        var linkWithoutLogsRes = await _client.PostAsJsonAsync(
            $"/api/recipes/{recipe.Id}/shares",
            new CreateShareLinkRequest(IncludeCookLogs: false)
        );
        Assert.AreEqual(HttpStatusCode.Created, linkWithoutLogsRes.StatusCode);
        var linkWithoutLogs = await linkWithoutLogsRes.Content.ReadFromJsonAsync<ShareLinkDto>();
        Assert.IsNotNull(linkWithoutLogs);

        // Fetch public recipe
        var publicWithoutLogsRes = await publicClient.GetAsync(
            $"/api/public/shares/{linkWithoutLogs.Token}"
        );
        Assert.AreEqual(HttpStatusCode.OK, publicWithoutLogsRes.StatusCode);
        var publicWithoutLogs =
            await publicWithoutLogsRes.Content.ReadFromJsonAsync<PublicSharedRecipeDto>();
        Assert.IsNotNull(publicWithoutLogs);
        Assert.AreEqual(0, publicWithoutLogs.TimesCooked);
        Assert.IsNull(publicWithoutLogs.AverageRating);
        Assert.AreEqual(0, publicWithoutLogs.CookLogs.Count);
    }
}

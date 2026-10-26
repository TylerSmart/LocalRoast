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
public sealed class MediaUploadIntegrationTests
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

    [TestMethod]
    public async Task UploadRecipeCover_ValidImage_ReturnsPathsAndServesViaStaticFiles()
    {
        await RegisterAndAuthenticateAsync(_client, "media1@example.com", "media1");

        var pngBytes = CreateTestPng(800, 600);
        using var content = new MultipartFormDataContent();
        using var byteContent = new ByteArrayContent(pngBytes);
        byteContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(byteContent, "file", "cover.png");

        var response = await _client.PostAsync("/api/media/recipe-cover", content);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var coverResult = await response.Content.ReadFromJsonAsync<UploadedRecipeCoverDto>();
        Assert.IsNotNull(coverResult);
        StringAssert.StartsWith(coverResult.CoverImagePath, "/api/uploads/recipes/");
        StringAssert.EndsWith(coverResult.CoverImagePath, "-cover.webp");
        StringAssert.StartsWith(coverResult.ThumbnailImagePath, "/api/uploads/recipes/");
        StringAssert.EndsWith(coverResult.ThumbnailImagePath, "-thumb.webp");

        // Request images directly via unauthenticated client
        using var publicClient = _factory.CreateClient();
        var coverResponse = await publicClient.GetAsync(coverResult.CoverImagePath);
        Assert.AreEqual(HttpStatusCode.OK, coverResponse.StatusCode);
        Assert.AreEqual("image/webp", coverResponse.Content.Headers.ContentType?.MediaType);

        var thumbResponse = await publicClient.GetAsync(coverResult.ThumbnailImagePath);
        Assert.AreEqual(HttpStatusCode.OK, thumbResponse.StatusCode);
        Assert.AreEqual("image/webp", thumbResponse.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task UploadRecipeStep_ValidImage_ReturnsPathAndServesViaStaticFiles()
    {
        await RegisterAndAuthenticateAsync(_client, "media2@example.com", "media2");

        var pngBytes = CreateTestPng(600, 400);
        using var content = new MultipartFormDataContent();
        using var byteContent = new ByteArrayContent(pngBytes);
        byteContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(byteContent, "file", "step.png");

        var response = await _client.PostAsync("/api/media/recipe-step", content);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var stepResult = await response.Content.ReadFromJsonAsync<UploadedMediaDto>();
        Assert.IsNotNull(stepResult);
        StringAssert.StartsWith(stepResult.ImagePath, "/api/uploads/steps/");
        StringAssert.EndsWith(stepResult.ImagePath, ".webp");

        using var publicClient = _factory.CreateClient();
        var stepResponse = await publicClient.GetAsync(stepResult.ImagePath);
        Assert.AreEqual(HttpStatusCode.OK, stepResponse.StatusCode);
        Assert.AreEqual("image/webp", stepResponse.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task Upload_Unauthenticated_Returns401()
    {
        using var unauthClient = _factory.CreateClient();
        var pngBytes = CreateTestPng(100, 100);
        using var content = new MultipartFormDataContent();
        using var byteContent = new ByteArrayContent(pngBytes);
        content.Add(byteContent, "file", "test.png");

        var response = await unauthClient.PostAsync("/api/media/recipe-cover", content);
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Upload_CorruptBytes_Returns400()
    {
        await RegisterAndAuthenticateAsync(_client, "media3@example.com", "media3");

        using var content = new MultipartFormDataContent();
        using var byteContent = new ByteArrayContent("not real image"u8.ToArray());
        content.Add(byteContent, "file", "fake.png");

        var response = await _client.PostAsync("/api/media/recipe-cover", content);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task RecipeLifecycle_CleansUpOrphanedMediaOnUpdateAndDelete()
    {
        await RegisterAndAuthenticateAsync(_client, "media4@example.com", "media4");

        // 1. Upload cover 1
        var pngBytes1 = CreateTestPng(400, 300);
        using var content1 = new MultipartFormDataContent();
        using var byteContent1 = new ByteArrayContent(pngBytes1);
        content1.Add(byteContent1, "file", "cover1.png");
        var cover1Res = await _client.PostAsync("/api/media/recipe-cover", content1);
        var cover1 = await cover1Res.Content.ReadFromJsonAsync<UploadedRecipeCoverDto>();
        Assert.IsNotNull(cover1);

        // Create recipe with cover 1
        var createReq = new UpsertRecipeRequest(
            Title: "Media Cleanup Recipe",
            CoverImagePath: cover1.CoverImagePath,
            ThumbnailImagePath: cover1.ThumbnailImagePath
        );
        var createRes = await _client.PostAsJsonAsync("/api/recipes", createReq);
        Assert.AreEqual(HttpStatusCode.Created, createRes.StatusCode);
        var created = await createRes.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(created);

        // Verify cover 1 exists
        using var publicClient = _factory.CreateClient();
        var checkCover1 = await publicClient.GetAsync(cover1.CoverImagePath);
        Assert.AreEqual(HttpStatusCode.OK, checkCover1.StatusCode);

        // 2. Upload cover 2 and update recipe
        var pngBytes2 = CreateTestPng(500, 400);
        using var content2 = new MultipartFormDataContent();
        using var byteContent2 = new ByteArrayContent(pngBytes2);
        content2.Add(byteContent2, "file", "cover2.png");
        var cover2Res = await _client.PostAsync("/api/media/recipe-cover", content2);
        var cover2 = await cover2Res.Content.ReadFromJsonAsync<UploadedRecipeCoverDto>();
        Assert.IsNotNull(cover2);

        var updateReq = new UpsertRecipeRequest(
            Title: "Media Cleanup Recipe Updated",
            CoverImagePath: cover2.CoverImagePath,
            ThumbnailImagePath: cover2.ThumbnailImagePath
        );
        var updateRes = await _client.PutAsJsonAsync($"/api/recipes/{created.Id}", updateReq);
        Assert.AreEqual(HttpStatusCode.OK, updateRes.StatusCode);

        // Old cover 1 should now be deleted (404)
        var checkOldCover1 = await publicClient.GetAsync(cover1.CoverImagePath);
        Assert.AreEqual(HttpStatusCode.NotFound, checkOldCover1.StatusCode);

        // New cover 2 should exist (200)
        var checkCover2 = await publicClient.GetAsync(cover2.CoverImagePath);
        Assert.AreEqual(HttpStatusCode.OK, checkCover2.StatusCode);

        // 3. Delete recipe
        var deleteRes = await _client.DeleteAsync($"/api/recipes/{created.Id}");
        Assert.AreEqual(HttpStatusCode.NoContent, deleteRes.StatusCode);

        // Cover 2 and thumbnail 2 should now be deleted (404)
        var checkDeletedCover2 = await publicClient.GetAsync(cover2.CoverImagePath);
        Assert.AreEqual(HttpStatusCode.NotFound, checkDeletedCover2.StatusCode);
    }
}

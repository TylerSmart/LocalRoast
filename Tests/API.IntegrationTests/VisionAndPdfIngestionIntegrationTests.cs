using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using App.Core.Authentication;
using App.Core.Ingestion;
using App.Core.Recipes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace App.API.IntegrationTests;

[TestClass]
public sealed class VisionAndPdfIngestionIntegrationTests
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

    private static byte[] CreateSampleImageBytes(int width = 400, int height = 300)
    {
        using var image = new Image<Rgba32>(width, height);
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static byte[] CreateSamplePdfBytes(string content = "Sample Recipe Book")
    {
        var raw = "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" + content;
        return System.Text.Encoding.ASCII.GetBytes(raw);
    }

    [TestMethod]
    public async Task EnqueueImages_Unauthenticated_ReturnsUnauthorized()
    {
        using var content = new MultipartFormDataContent();
        var resp = await _client.PostAsync("/api/ingestion/jobs/images", content);
        Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [TestMethod]
    public async Task EnqueueImages_ValidMultipleImages_ReturnsAcceptedAndCreatesJob()
    {
        await AuthenticateAsync(_client);

        using var form = new MultipartFormDataContent();
        var img1 = CreateSampleImageBytes();
        var img2 = CreateSampleImageBytes();

        form.Add(
            new ByteArrayContent(img1)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("image/png") },
            },
            "files",
            "page1.png"
        );

        form.Add(
            new ByteArrayContent(img2)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("image/png") },
            },
            "files",
            "page2.png"
        );

        form.Add(new StringContent("Modern Baking"), "sourceBookTitle");
        form.Add(new StringContent("45"), "sourceBookPage");

        var response = await _client.PostAsync("/api/ingestion/jobs/images", form);
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);

        var jobSummary = await response.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(jobSummary);
        Assert.AreEqual(IngestionChannel.Images, jobSummary.Channel);
        Assert.AreEqual(2, jobSummary.SourceFileCount);
        StringAssert.Contains(jobSummary.DisplayTitle, "Modern Baking");

        // Verify detail endpoint
        var detailResp = await _client.GetAsync($"/api/ingestion/jobs/{jobSummary.Id}");
        Assert.AreEqual(HttpStatusCode.OK, detailResp.StatusCode);

        var detail = await detailResp.Content.ReadFromJsonAsync<IngestionJobDetailDto>();
        Assert.IsNotNull(detail);
        Assert.AreEqual(2, detail.SourceFiles.Count);
        Assert.AreEqual("Modern Baking", detail.SourceBookTitle);
        Assert.AreEqual(45, detail.SourceBookPage);
    }

    [TestMethod]
    public async Task EnqueuePdf_ValidPdf_ReturnsAcceptedAndCreatesJob()
    {
        await AuthenticateAsync(_client);

        using var form = new MultipartFormDataContent();
        var pdfBytes = CreateSamplePdfBytes();

        form.Add(
            new ByteArrayContent(pdfBytes)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/pdf") },
            },
            "file",
            "holiday-cookbook.pdf"
        );

        form.Add(new StringContent("Holiday Desserts"), "sourceBookTitle");

        var response = await _client.PostAsync("/api/ingestion/jobs/pdf", form);
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);

        var jobSummary = await response.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(jobSummary);
        Assert.AreEqual(IngestionChannel.Pdf, jobSummary.Channel);
        Assert.AreEqual(1, jobSummary.SourceFileCount);
        StringAssert.Contains(jobSummary.DisplayTitle, "Holiday Desserts");
    }

    [TestMethod]
    public async Task PromoteSourcePageToCover_ValidJob_UpdatesJobCoverImage()
    {
        await AuthenticateAsync(_client);

        using var form = new MultipartFormDataContent();
        var img1 = CreateSampleImageBytes();
        form.Add(
            new ByteArrayContent(img1)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("image/png") },
            },
            "files",
            "cover.png"
        );

        var enqResp = await _client.PostAsync("/api/ingestion/jobs/images", form);
        var jobSummary = await enqResp.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(jobSummary);

        var promoteReq = new PromotePageToCoverRequest(PageNumber: 1);
        var promoteResp = await _client.PostAsJsonAsync(
            $"/api/ingestion/jobs/{jobSummary.Id}/cover-from-page",
            promoteReq
        );

        Assert.AreEqual(HttpStatusCode.OK, promoteResp.StatusCode);
        var updatedJob = await promoteResp.Content.ReadFromJsonAsync<IngestionJobDetailDto>();
        Assert.IsNotNull(updatedJob);
        Assert.IsNotNull(updatedJob.DownloadedCoverImagePath);
        StringAssert.StartsWith(updatedJob.DownloadedCoverImagePath, "/api/uploads/recipes/");
    }

    [TestMethod]
    public async Task RecipeAttachments_UploadGetAndDelete_Succeeds()
    {
        await AuthenticateAsync(_client);

        // 1. Create a recipe
        var createReq = new UpsertRecipeRequest(
            Title: "Grandma's Pie",
            Description: "Delicious apple pie",
            Status: RecipeStatus.Published,
            BaseServings: 8,
            Ingredients: [new RecipeIngredientInput(DisplayName: "6 Granny Smith apples")],
            Steps: [new RecipeStepInput(Instruction: "Peel and slice apples.")]
        );

        var createResp = await _client.PostAsJsonAsync("/api/recipes", createReq);
        Assert.AreEqual(HttpStatusCode.Created, createResp.StatusCode);
        var recipe = await createResp.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(recipe);
        Assert.AreEqual(0, recipe.Attachments.Count);

        // 2. Upload an attachment
        using var form = new MultipartFormDataContent();
        var pdfBytes = CreateSamplePdfBytes();
        form.Add(
            new ByteArrayContent(pdfBytes)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/pdf") },
            },
            "file",
            "original-scanned-card.pdf"
        );
        form.Add(new StringContent("Handwritten Note"), "label");

        var attachResp = await _client.PostAsync($"/api/recipes/{recipe.Id}/attachments", form);
        Assert.AreEqual(HttpStatusCode.Created, attachResp.StatusCode);
        var attachment = await attachResp.Content.ReadFromJsonAsync<RecipeAttachmentDto>();
        Assert.IsNotNull(attachment);
        Assert.AreEqual("original-scanned-card.pdf", attachment.FileName);
        Assert.AreEqual("Handwritten Note", attachment.Label);
        StringAssert.StartsWith(attachment.MediaUrlPath, "/api/uploads/attachments/");

        // 3. Verify recipe includes attachment
        var getResp = await _client.GetAsync($"/api/recipes/{recipe.Id}");
        var recipeWithAttach = await getResp.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(recipeWithAttach);
        Assert.AreEqual(1, recipeWithAttach.Attachments.Count);
        Assert.AreEqual(attachment.Id, recipeWithAttach.Attachments[0].Id);

        // 4. Delete attachment
        var delResp = await _client.DeleteAsync(
            $"/api/recipes/{recipe.Id}/attachments/{attachment.Id}"
        );
        Assert.AreEqual(HttpStatusCode.NoContent, delResp.StatusCode);

        // 5. Verify deleted from recipe
        var getAfterDel = await _client.GetAsync($"/api/recipes/{recipe.Id}");
        var recipeAfterDel = await getAfterDel.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(recipeAfterDel);
        Assert.AreEqual(0, recipeAfterDel.Attachments.Count);
    }

    [TestMethod]
    public async Task RecipeAttachments_Unauthenticated_ReturnsUnauthorized()
    {
        using var form = new MultipartFormDataContent();
        var resp = await _client.PostAsync($"/api/recipes/{Guid.NewGuid()}/attachments", form);
        Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);

        var delResp = await _client.DeleteAsync(
            $"/api/recipes/{Guid.NewGuid()}/attachments/{Guid.NewGuid()}"
        );
        Assert.AreEqual(HttpStatusCode.Unauthorized, delResp.StatusCode);
    }

    [TestMethod]
    public async Task ApproveJob_WithRetainSourceAttachments_PromotesAttachmentsToPublishedRecipe()
    {
        await AuthenticateAsync(_client);

        using var form = new MultipartFormDataContent();
        var img1 = CreateSampleImageBytes();
        var img2 = CreateSampleImageBytes();

        form.Add(
            new ByteArrayContent(img1)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("image/png") },
            },
            "files",
            "page1.png"
        );

        form.Add(
            new ByteArrayContent(img2)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("image/png") },
            },
            "files",
            "page2.png"
        );

        form.Add(new StringContent("Heritage Baking"), "sourceBookTitle");

        var postResp = await _client.PostAsync("/api/ingestion/jobs/images", form);
        Assert.AreEqual(HttpStatusCode.Accepted, postResp.StatusCode);
        var jobSummary = await postResp.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(jobSummary);

        // Approve and publish with RetainSourceAttachments = true
        var draft = new IngestionExtractedDraftDto(
            Title: "Heritage Scones",
            Description: "Rich and buttery",
            BaseServings: 8,
            Ingredients:
            [
                new IngestionDraftIngredientDto(
                    DisplayName: "Flour",
                    Quantity: 2,
                    Unit: App.Core.Recipes.MeasurementUnit.Cup
                ),
            ],
            Steps: [new IngestionDraftStepDto(Instruction: "Mix flour.")],
            RetainSourceAttachments: true
        );

        var approveResp = await _client.PostAsJsonAsync(
            $"/api/ingestion/jobs/{jobSummary.Id}/approve",
            draft
        );
        Assert.AreEqual(HttpStatusCode.Created, approveResp.StatusCode);
        var approvedRecipe = await approveResp.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(approvedRecipe);

        var getResp = await _client.GetAsync($"/api/recipes/{approvedRecipe.Id}");
        var fetchedRecipe = await getResp.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(fetchedRecipe);
        Assert.AreEqual(2, fetchedRecipe.Attachments.Count);
    }
}

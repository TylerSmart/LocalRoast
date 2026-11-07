using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using App.Core.Authentication;
using App.Core.Ingestion;
using App.Core.Recipes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    public async Task EnqueuePdfBatch_MultiplePdfs_ReturnsAcceptedAndCreatesMultipleJobs()
    {
        await AuthenticateAsync(_client);

        using var form = new MultipartFormDataContent();

        var pdfBytes1 = CreateSamplePdfBytes();

        var pdfBytes2 = CreateSamplePdfBytes();

        form.Add(
            new ByteArrayContent(pdfBytes1)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/pdf") },
            },
            "files",
            "grandma-cookies.pdf"
        );

        form.Add(
            new ByteArrayContent(pdfBytes2)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/pdf") },
            },
            "files",
            "lemon-bars.pdf"
        );

        form.Add(new StringContent("Family Recipe Box"), "sourceBookTitle");

        var response = await _client.PostAsync("/api/ingestion/jobs/pdf/bulk", form);

        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);

        var jobs = await response.Content.ReadFromJsonAsync<List<IngestionJobSummaryDto>>();

        Assert.IsNotNull(jobs);

        Assert.AreEqual(2, jobs.Count);

        Assert.IsTrue(jobs.All(j => j.Channel == IngestionChannel.Pdf));

        StringAssert.Contains(jobs[0].DisplayTitle, "Family Recipe Box");

        StringAssert.Contains(jobs[1].DisplayTitle, "Family Recipe Box");
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
    public async Task ScannedPdfIngestion_ProcessedViaVision_DoesNotUseRenderedPageAsCoverImage()
    {
        var fakeInspector = new FakeScannedPdfInspector();
        var fakeLlm = new FakeVisionLlmExtractionClient();

        await using var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Llm:TextModel", "qwen2.5:14b");
            builder.UseSetting("Llm:VisionModel", "qwen2.5vl:7b");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPdfIngestionInspector>();
                services.AddSingleton<IPdfIngestionInspector>(fakeInspector);
                services.RemoveAll<ILlmExtractionClient>();
                services.AddSingleton<ILlmExtractionClient>(fakeLlm);
            });
        });

        using var client = customFactory.CreateClient();
        await AuthenticateAsync(client);

        // Store a real image so the vision pipeline can encode it as the rendered PDF page
        using var imageForm = new MultipartFormDataContent();
        imageForm.Add(
            new ByteArrayContent(CreateSampleImageBytes())
            {
                Headers = { ContentType = new MediaTypeHeaderValue("image/png") },
            },
            "files",
            "page1.png"
        );
        var imageJobResp = await client.PostAsync("/api/ingestion/jobs/images", imageForm);
        var imageJob = await imageJobResp.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(imageJob);
        var imageJobDetail = await client.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{imageJob.Id}"
        );
        Assert.IsNotNull(imageJobDetail);
        fakeInspector.RenderedPage = imageJobDetail.SourceFiles[0];

        using var pdfForm = new MultipartFormDataContent();
        pdfForm.Add(
            new ByteArrayContent(CreateSamplePdfBytes())
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/pdf") },
            },
            "file",
            "scanned-card.pdf"
        );
        var pdfResp = await client.PostAsync("/api/ingestion/jobs/pdf", pdfForm);
        Assert.AreEqual(HttpStatusCode.Accepted, pdfResp.StatusCode);
        var pdfJob = await pdfResp.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        Assert.IsNotNull(pdfJob);

        using (var scope = customFactory.Services.CreateScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<IIngestionProcessor>();
            Assert.IsTrue(await processor.ProcessJobByIdAsync(pdfJob.Id));
        }

        Assert.IsTrue(fakeLlm.VisionCalled, "Scanned PDF should be extracted via vision.");

        var processed = await client.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{pdfJob.Id}"
        );
        Assert.IsNotNull(processed);
        Assert.AreEqual(IngestionJobStatus.ReadyForReview, processed.Status);
        Assert.IsNull(processed.DownloadedCoverImagePath);
        Assert.IsNull(processed.ExtractedDraft?.CoverImagePath);
        Assert.IsNull(processed.ExtractedDraft?.ThumbnailImagePath);
    }

    private sealed class FakeScannedPdfInspector : IPdfIngestionInspector
    {
        public IngestionSourceFileDto? RenderedPage { get; set; }

        public Task<PdfInspectionResult> InspectAndPrepareAsync(
            string pdfMediaUrlPath,
            string jobToken,
            CancellationToken cancellationToken = default
        )
        {
            var page = RenderedPage ?? throw new InvalidOperationException("No page configured.");
            return Task.FromResult(
                new PdfInspectionResult(
                    Summary: new PdfInspectionSummaryDto(
                        TotalPageCount: 1,
                        ProcessedPageCount: 1,
                        TotalExtractedCharacters: 0,
                        AverageCharactersPerPage: 0,
                        ExtractionMode: PdfExtractionMode.VisionRaster,
                        RenderedPageImages: [page with { PageNumber = 1 }]
                    ),
                    NormalizedDigitalText: null,
                    GeneratedThumbnailImagePath: page.UrlPath
                )
            );
        }
    }

    private sealed class FakeVisionLlmExtractionClient : ILlmExtractionClient
    {
        public Task<string> AnswerKitchenQuestionAsync(
            EffectiveLlmConnectionConfig config,
            LlmKitchenQuestionContext context,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public bool VisionCalled { get; private set; }

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
                    ["qwen2.5:14b", "qwen2.5vl:7b"],
                    null
                )
            );

        public Task<string?> StructureRecipeSourceAsync(
            EffectiveLlmConnectionConfig config,
            string sourceText,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<LlmExtractedRecipePayload> ExtractRecipeFromTextAsync(
            EffectiveLlmConnectionConfig config,
            LlmExtractionContext context,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<LlmExtractedRecipePayload> ExtractRecipeFromImagesAsync(
            EffectiveLlmConnectionConfig config,
            LlmVisionExtractionContext context,
            CancellationToken cancellationToken = default
        )
        {
            VisionCalled = true;
            return Task.FromResult(
                new LlmExtractedRecipePayload(
                    Title: "Grandma's Shortbread",
                    Description: "Buttery shortbread from a scanned recipe card.",
                    BaseServings: 12m,
                    YieldDisplay: "12 cookies",
                    PrepTimeMinutes: 15,
                    CookTimeMinutes: 20,
                    TotalTimeMinutes: 35,
                    Difficulty: "Easy",
                    SourceName: null,
                    SourceAuthor: null,
                    ConfidenceScore: 0.6m,
                    SuggestedTagNames: ["Dessert"],
                    Ingredients:
                    [
                        new LlmExtractedIngredientItem(
                            SectionName: null,
                            Quantity: 1m,
                            MaxQuantity: null,
                            Unit: "cup",
                            CanonicalFoodName: "butter",
                            DisplayName: "butter",
                            PreparationNote: "softened",
                            IsOptional: false,
                            OriginalRawText: "1 cup butter, softened",
                            SuggestedIsPantryStaple: false
                        ),
                    ],
                    Steps:
                    [
                        new LlmExtractedStepItem(
                            SectionName: null,
                            Instruction: "Bake for 20 minutes.",
                            IngredientIndices: [0],
                            Timers: []
                        ),
                    ]
                )
            );
        }

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

    [TestMethod]
    public async Task RawTextIngestion_ApproveJob_WithRetainSourceAttachments_PromotesTextAttachmentToPublishedRecipe()
    {
        await AuthenticateAsync(_client);

        var rawText =
            "Title: ATK Classic Roast Chicken\nCookbook: The Complete ATK Cookbook\n\nIngredients:\n- 1 whole chicken\n- 2 tbsp olive oil\n\nInstructions:\n1. Roast at 425F for 60 mins.";

        var enqueueReq = new EnqueueRawTextIngestionRequest(
            RawText: rawText,
            TitleHint: "ATK Classic Roast Chicken",
            SourceName: "America's Test Kitchen",
            SourceAuthor: "America's Test Kitchen",
            SourceBookTitle: "The Complete ATK Cookbook",
            SourceBookPage: 120
        );

        var postResp = await _client.PostAsJsonAsync("/api/ingestion/jobs/text", enqueueReq);

        Assert.AreEqual(HttpStatusCode.Accepted, postResp.StatusCode);

        var jobSummary = await postResp.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();

        Assert.IsNotNull(jobSummary);

        Assert.AreEqual(1, jobSummary.SourceFileCount);

        // Approve with RetainSourceAttachments = true

        var draft = new IngestionExtractedDraftDto(
            Title: "ATK Classic Roast Chicken",
            Description: "Crispy skin and juicy meat",
            BaseServings: 4,
            Ingredients:
            [
                new IngestionDraftIngredientDto(
                    DisplayName: "Whole chicken",
                    Quantity: 1,
                    Unit: App.Core.Recipes.MeasurementUnit.Cup
                ),
            ],
            Steps: [new IngestionDraftStepDto(Instruction: "Roast at 425F.")],
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

        Assert.AreEqual(1, fetchedRecipe.Attachments.Count);

        var att = fetchedRecipe.Attachments[0];

        Assert.AreEqual("text/plain", att.ContentType);

        Assert.AreEqual("Original Source Text", att.Label);

        // Verify attachment file is served statically with raw text content

        var textResp = await _client.GetAsync(att.MediaUrlPath);

        Assert.AreEqual(HttpStatusCode.OK, textResp.StatusCode);

        var textBody = await textResp.Content.ReadAsStringAsync();

        Assert.IsTrue(textBody.Contains("ATK Classic Roast Chicken"));

        Assert.IsTrue(textBody.Contains("Roast at 425F for 60 mins."));
    }

    [TestMethod]
    public async Task DirectAttachmentUpload_WithTextFile_Succeeds()
    {
        await AuthenticateAsync(_client);

        var createReq = new UpsertRecipeRequest(
            Title: "Grandma's Notes Recipe",
            Description: "Family treasure",
            Status: RecipeStatus.Published,
            BaseServings: 4,
            Ingredients: [new RecipeIngredientInput(DisplayName: "Flour", Quantity: 2)],
            Steps: [new RecipeStepInput(Instruction: "Mix and bake.")]
        );
        var createResp = await _client.PostAsJsonAsync("/api/recipes", createReq);
        Assert.AreEqual(HttpStatusCode.Created, createResp.StatusCode);
        var recipe = await createResp.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(recipe);

        using var form = new MultipartFormDataContent();

        var textBytes = System.Text.Encoding.UTF8.GetBytes(
            "Old hand-typed recipe notes: bake slow, cool completely."
        );

        form.Add(
            new ByteArrayContent(textBytes)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("text/plain") },
            },
            "file",
            "notes.txt"
        );

        form.Add(new StringContent("Original Source Notes"), "label");

        var uploadResp = await _client.PostAsync($"/api/recipes/{recipe.Id}/attachments", form);

        Assert.AreEqual(HttpStatusCode.Created, uploadResp.StatusCode);

        var createdAtt = await uploadResp.Content.ReadFromJsonAsync<RecipeAttachmentDto>();

        Assert.IsNotNull(createdAtt);

        Assert.AreEqual("text/plain", createdAtt.ContentType);

        Assert.AreEqual("Original Source Notes", createdAtt.Label);

        var getResp = await _client.GetAsync(createdAtt.MediaUrlPath);

        Assert.AreEqual(HttpStatusCode.OK, getResp.StatusCode);

        var fetchedBody = await getResp.Content.ReadAsStringAsync();

        Assert.IsTrue(fetchedBody.Contains("Old hand-typed recipe notes"));
    }
}

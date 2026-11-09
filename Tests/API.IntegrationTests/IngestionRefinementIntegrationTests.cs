using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using App.Core.Authentication;
using App.Core.Ingestion;
using App.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.API.IntegrationTests;

[TestClass]
public sealed class IngestionRefinementIntegrationTests
{
    private const string TextModel = "gemma4:e4b";
    private const string RefineModel = "gemma4:26b-a4b";
    private const string Source =
        "Ingredients:\n- 4 tablespoons olive oil\n- Salt\nInstructions:\nHeat oil and season.";

    private AppWebApplicationFactory _factory = null!;

    [TestInitialize]
    public void Setup() => _factory = new AppWebApplicationFactory();

    [TestCleanup]
    public void Cleanup() => _factory.Dispose();

    private static LlmExtractedRecipePayload Extraction(bool linkSalt) =>
        new(
            Title: "Oil sauce",
            ConfidenceScore: 0.99m,
            Ingredients:
            [
                new(
                    null,
                    4,
                    null,
                    "tbsp",
                    "olive oil",
                    "olive oil",
                    OriginalRawText: "4 tablespoons olive oil"
                ),
                new(null, null, null, null, "salt", "salt", OriginalRawText: "Salt"),
            ],
            Steps: [new(null, "Heat the oil and season with salt.", linkSalt ? [0, 1] : [0])]
        );

    private WebApplicationFactory<Program> CreateFactory(
        RefineLlmClient fakeLlm,
        string? refineModel = RefineModel
    ) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Llm:TextModel", TextModel);
            builder.UseSetting("Llm:RefineModel", refineModel ?? string.Empty);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILlmExtractionClient>();
                services.AddSingleton<ILlmExtractionClient>(fakeLlm);
            });
        });

    private static async Task AuthenticateAsync(HttpClient client)
    {
        var register = new RegisterRequest(
            $"chef_{Guid.NewGuid():N}@example.com",
            $"chef_{Guid.NewGuid():N}"[..12],
            "KitchenSecret123!"
        );
        var response = await client.PostAsJsonAsync("/api/auth/register", register);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            auth!.Token
        );
    }

    private static async Task<Guid> ImportAndProcessAsync(
        WebApplicationFactory<Program> factory,
        HttpClient client
    )
    {
        var response = await client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(Source)
        );
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        var job = await response.Content.ReadFromJsonAsync<IngestionJobSummaryDto>();
        using var scope = factory.Services.CreateScope();
        Assert.IsTrue(
            await scope
                .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                .ProcessNextQueuedJobAsync()
        );
        return job!.Id;
    }

    private static async Task<bool> RefineNextAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope
            .ServiceProvider.GetRequiredService<IIngestionReviewService>()
            .RefineNextPendingDraftAsync();
    }

    private static async Task<IngestionRefinementDto?> GetRefinementAsync(
        HttpClient client,
        Guid id
    )
    {
        var response = await client.GetAsync($"/api/ingestion/jobs/{id}/refinement");
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<IngestionRefinementDto>();
    }

    [TestMethod]
    public async Task DraftWithIssues_IsRefinedInBackgroundAndPreviewedWithoutChangingTheJob()
    {
        var fakeLlm = new RefineLlmClient(
            Extraction(linkSalt: false),
            (_, context, _) =>
                Task.FromResult(
                    new LlmRecipeRepairResult(
                        context.Draft with
                        {
                            Steps = [context.Draft.Steps![0] with { IngredientIndices = [0, 1] }],
                        },
                        []
                    )
                )
        );
        await using var factory = CreateFactory(fakeLlm);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);
        var id = await ImportAndProcessAsync(factory, client);

        var pending = await GetRefinementAsync(client, id);
        Assert.IsNotNull(pending);
        Assert.AreEqual(IngestionRefinementStatus.Pending, pending.Status);
        Assert.AreEqual(RefineModel, pending.Model);

        var before = await client.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{id}"
        );
        Assert.IsTrue(await RefineNextAsync(factory));
        Assert.IsFalse(await RefineNextAsync(factory));

        Assert.IsNotNull(fakeLlm.LastRepairConfig);
        Assert.AreEqual(RefineModel, fakeLlm.LastRepairConfig.TextModel);
        Assert.IsNull(fakeLlm.LastRepairContext!.UserHint);

        var refined = await GetRefinementAsync(client, id);
        Assert.IsNotNull(refined);
        Assert.AreEqual(IngestionRefinementStatus.Improved, refined.Status, refined.Detail);
        Assert.IsTrue(refined.HasProposal);
        Assert.IsFalse(refined.BasedOnEarlierDraft);
        Assert.IsGreaterThan(0, refined.IssuesBefore!.Value);
        Assert.AreEqual(0, refined.IssuesAfter);
        Assert.IsNotNull(refined.CompletedAt);

        var after = await client.GetFromJsonAsync<IngestionJobDetailDto>(
            $"/api/ingestion/jobs/{id}"
        );
        Assert.AreEqual(before!.ModifiedAt, after!.ModifiedAt);
        CollectionAssert.AreEqual(
            new[] { 0 },
            after.ExtractedDraft!.Steps![0].IngredientIndices!.ToArray()
        );

        var edited = after.ExtractedDraft with { Title = "Unsaved title" };
        var previewResponse = await client.PostAsJsonAsync(
            $"/api/ingestion/jobs/{id}/refinement/preview",
            new IngestionDraftDiagnosticsRequest(edited)
        );
        Assert.AreEqual(
            HttpStatusCode.OK,
            previewResponse.StatusCode,
            await previewResponse.Content.ReadAsStringAsync()
        );
        var preview = await previewResponse.Content.ReadFromJsonAsync<IngestionRepairPreviewDto>();
        Assert.IsNotNull(preview);
        CollectionAssert.AreEqual(
            new[] { 0, 1 },
            preview.Draft.Steps![0].IngredientIndices!.ToArray()
        );
        Assert.AreEqual("Oil sauce", preview.Draft.Title);
        Assert.IsNotEmpty(preview.Before.QualityWarnings);
        Assert.IsEmpty(preview.After.QualityWarnings);
        Assert.IsEmpty(preview.After.ValidationIssues);
        Assert.HasCount(1, preview.Notices);

        var saved = await client.PutAsJsonAsync(
            $"/api/ingestion/jobs/{id}/draft",
            after.ExtractedDraft with
            {
                Title = "Saved title",
            }
        );
        Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
        var stale = await GetRefinementAsync(client, id);
        Assert.IsTrue(stale!.BasedOnEarlierDraft);
        var stalePreview = await client.PostAsJsonAsync(
            $"/api/ingestion/jobs/{id}/refinement/preview",
            new IngestionDraftDiagnosticsRequest(edited)
        );
        var stalePreviewDto =
            await stalePreview.Content.ReadFromJsonAsync<IngestionRepairPreviewDto>();
        Assert.HasCount(2, stalePreviewDto!.Notices);

        var dismissed = await client.DeleteAsync($"/api/ingestion/jobs/{id}/refinement");
        Assert.AreEqual(HttpStatusCode.NoContent, dismissed.StatusCode);
        Assert.IsNull(await GetRefinementAsync(client, id));
        var missingPreview = await client.PostAsJsonAsync(
            $"/api/ingestion/jobs/{id}/refinement/preview",
            new IngestionDraftDiagnosticsRequest(edited)
        );
        Assert.AreEqual(HttpStatusCode.Conflict, missingPreview.StatusCode);
    }

    [TestMethod]
    public async Task WithoutRefineModel_NothingIsQueuedAndManualRequestsAreRejected()
    {
        var fakeLlm = new RefineLlmClient(
            Extraction(linkSalt: false),
            (_, _, _) => throw new InvalidOperationException("Refinement was not expected.")
        );
        await using var factory = CreateFactory(fakeLlm, refineModel: null);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);
        var id = await ImportAndProcessAsync(factory, client);

        Assert.IsNull(await GetRefinementAsync(client, id));
        Assert.IsFalse(await RefineNextAsync(factory));
        var request = await client.PostAsync($"/api/ingestion/jobs/{id}/refinement", null);
        Assert.AreEqual(HttpStatusCode.BadRequest, request.StatusCode);
        StringAssert.Contains(await request.Content.ReadAsStringAsync(), "Refine Model");
        var missing = await client.PostAsync(
            $"/api/ingestion/jobs/{Guid.NewGuid()}/refinement",
            null
        );
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [TestMethod]
    public async Task UnhelpfulOrFailedRefinement_KeepsNoProposalAndCanBeRequestedAgain()
    {
        var attempts = 0;
        var fakeLlm = new RefineLlmClient(
            Extraction(linkSalt: false),
            (_, context, _) =>
                ++attempts == 1
                    ? Task.FromResult(new LlmRecipeRepairResult(context.Draft, []))
                    : throw new LlmHostUnavailableException("Refine host is offline.")
        );
        await using var factory = CreateFactory(fakeLlm);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);
        var id = await ImportAndProcessAsync(factory, client);

        Assert.IsTrue(await RefineNextAsync(factory));
        var unchanged = await GetRefinementAsync(client, id);
        Assert.AreEqual(IngestionRefinementStatus.NoImprovement, unchanged!.Status);
        Assert.IsFalse(unchanged.HasProposal);
        StringAssert.Contains(unchanged.Detail, "did not improve");

        var requested = await client.PostAsync($"/api/ingestion/jobs/{id}/refinement", null);
        Assert.AreEqual(HttpStatusCode.OK, requested.StatusCode);
        var requeued = await requested.Content.ReadFromJsonAsync<IngestionRefinementDto>();
        Assert.AreEqual(IngestionRefinementStatus.Pending, requeued!.Status);

        Assert.IsTrue(await RefineNextAsync(factory));
        var failed = await GetRefinementAsync(client, id);
        Assert.AreEqual(IngestionRefinementStatus.Failed, failed!.Status);
        StringAssert.Contains(failed.Detail, "Refine host is offline.");
        Assert.AreEqual(2, attempts);
    }

    [TestMethod]
    public async Task NewImport_PausesRunningRefinementUntilTheQueueIsEmpty()
    {
        var repairStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var fakeLlm = new RefineLlmClient(
            Extraction(linkSalt: false),
            async (_, context, ct) =>
            {
                repairStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return new LlmRecipeRepairResult(context.Draft, []);
            }
        );
        await using var factory = CreateFactory(fakeLlm);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);
        var id = await ImportAndProcessAsync(factory, client);

        var refining = RefineNextAsync(factory);
        await repairStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var running = await GetRefinementAsync(client, id);
        Assert.AreEqual(IngestionRefinementStatus.Running, running!.Status);

        var queued = await client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest(Source)
        );
        Assert.AreEqual(HttpStatusCode.Accepted, queued.StatusCode);
        Assert.IsTrue(await refining.WaitAsync(TimeSpan.FromSeconds(30)));

        var paused = await GetRefinementAsync(client, id);
        Assert.AreEqual(IngestionRefinementStatus.Pending, paused!.Status);
        StringAssert.Contains(paused.Detail, "Paused");
        Assert.IsFalse(await RefineNextAsync(factory));

        using var scope = factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IIngestionProcessor>();
        Assert.IsTrue(await processor.ProcessNextQueuedJobAsync());
        var db = scope.ServiceProvider.GetRequiredService<App.Data.AppDbContext>();
        Assert.AreEqual(
            2,
            await db.IngestionDraftRefinements.CountAsync(item =>
                item.Status == IngestionRefinementStatus.Pending
            )
        );
    }

    [TestMethod]
    public async Task PausedQueue_RefineWith_RunsEvenWhenQueueIsPausedAndInboxHasQueuedJobs()
    {
        var fakeLlm = new RefineLlmClient(
            Extraction(linkSalt: false),
            (_, context, _) =>
                Task.FromResult(
                    new LlmRecipeRepairResult(
                        context.Draft with
                        {
                            Steps = [context.Draft.Steps![0] with { IngredientIndices = [0, 1] }],
                        },
                        []
                    )
                )
        );
        await using var factory = CreateFactory(fakeLlm);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);
        var reviewJobId = await ImportAndProcessAsync(factory, client);

        // Pause ingestion queue and add an un-run queued job
        using (var scope = factory.Services.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<ILlmSettingsService>();
            await settings.SetIngestionEnabledAsync(false);

            var db = scope.ServiceProvider.GetRequiredService<App.Data.AppDbContext>();
            db.IngestionJobs.Add(
                new IngestionJob
                {
                    DisplayTitle = "Paused Background Job",
                    Status = IngestionJobStatus.Queued,
                    RunNowRequested = false,
                }
            );
            await db.SaveChangesAsync();
        }

        // Request refinement on the review job
        var refineRes = await client.PostAsync(
            $"/api/ingestion/jobs/{reviewJobId}/refinement",
            null
        );
        Assert.AreEqual(HttpStatusCode.OK, refineRes.StatusCode);

        // Run background refinement while queue is paused
        Assert.IsTrue(await RefineNextAsync(factory));

        var refinement = await GetRefinementAsync(client, reviewJobId);
        Assert.IsNotNull(refinement);
        Assert.AreEqual(IngestionRefinementStatus.Improved, refinement.Status);
    }

    [TestMethod]
    public async Task PublishedJob_CanRequestAndCompleteRefinement()
    {
        var fakeLlm = new RefineLlmClient(
            Extraction(linkSalt: false),
            (_, context, _) =>
                Task.FromResult(
                    new LlmRecipeRepairResult(
                        context.Draft with
                        {
                            Steps = [context.Draft.Steps![0] with { IngredientIndices = [0, 1] }],
                        },
                        []
                    )
                )
        );
        await using var factory = CreateFactory(fakeLlm);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);
        var jobId = await ImportAndProcessAsync(factory, client);

        // Publish the job first
        IngestionExtractedDraftDto draft;
        using (var scope = factory.Services.CreateScope())
        {
            var ingestion = scope.ServiceProvider.GetRequiredService<IIngestionService>();
            var detail = await ingestion.GetJobByIdAsync(jobId);
            Assert.IsNotNull(detail?.ExtractedDraft);
            draft = detail.ExtractedDraft;
            await ingestion.ApproveAndPublishAsync(jobId, draft);
        }

        // Verify it is Published
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<App.Data.AppDbContext>();
            var job = await db.IngestionJobs.FindAsync(jobId);
            Assert.AreEqual(IngestionJobStatus.Published, job!.Status);
        }

        // Request refinement on the Published job
        var refineRes = await client.PostAsync($"/api/ingestion/jobs/{jobId}/refinement", null);
        Assert.AreEqual(HttpStatusCode.OK, refineRes.StatusCode);

        // Run background refinement
        Assert.IsTrue(await RefineNextAsync(factory));

        var refinement = await GetRefinementAsync(client, jobId);
        Assert.IsNotNull(refinement);
        Assert.AreEqual(IngestionRefinementStatus.Improved, refinement.Status);

        // Preview refinement on Published job
        var previewRes = await client.PostAsJsonAsync(
            $"/api/ingestion/jobs/{jobId}/refinement/preview",
            new IngestionDraftDiagnosticsRequest(draft)
        );
        Assert.AreEqual(HttpStatusCode.OK, previewRes.StatusCode);
        var preview = await previewRes.Content.ReadFromJsonAsync<IngestionRepairPreviewDto>();
        Assert.IsNotNull(preview);
    }

    [TestMethod]
    public async Task ReprocessingAndRestartRecovery_ResetRefinementState()
    {
        var fakeLlm = new RefineLlmClient(
            Extraction(linkSalt: false),
            (_, _, _) => throw new InvalidOperationException("Refinement was not expected.")
        );
        await using var factory = CreateFactory(fakeLlm);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);
        var id = await ImportAndProcessAsync(factory, client);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<App.Data.AppDbContext>();
            var refinement = await db.IngestionDraftRefinements.SingleAsync(item =>
                item.IngestionJobId == id
            );
            refinement.Status = IngestionRefinementStatus.Running;
            await db.SaveChangesAsync();
            await scope
                .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                .RecoverInterruptedJobsAsync();
        }
        Assert.AreEqual(
            IngestionRefinementStatus.Pending,
            (await GetRefinementAsync(client, id))!.Status
        );

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<App.Data.AppDbContext>();
            var job = await db.IngestionJobs.SingleAsync(item => item.Id == id);
            job.Status = IngestionJobStatus.Queued;
            await db.SaveChangesAsync();
            fakeLlm.Extraction = Extraction(linkSalt: true);
            Assert.IsTrue(
                await scope
                    .ServiceProvider.GetRequiredService<IIngestionProcessor>()
                    .ProcessJobByIdAsync(id)
            );
        }
        Assert.IsNull(await GetRefinementAsync(client, id));
    }

    private sealed class RefineLlmClient(
        LlmExtractedRecipePayload extraction,
        Func<
            EffectiveLlmConnectionConfig,
            LlmRecipeRepairContext,
            CancellationToken,
            Task<LlmRecipeRepairResult>
        > repair
    ) : ILlmExtractionClient
    {
        public Task<LlmAutoRepairAndTriageResult> TriageAndRepairRecipeAsync(
            EffectiveLlmConnectionConfig config,
            LlmAutoRepairAndTriageContext context,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new LlmAutoRepairAndTriageResult(context.Draft, []));

        public LlmExtractedRecipePayload Extraction { get; set; } = extraction;
        public EffectiveLlmConnectionConfig? LastRepairConfig { get; private set; }
        public LlmRecipeRepairContext? LastRepairContext { get; private set; }

        public Task<LlmRecipeRepairResult> RepairRecipeDraftAsync(
            EffectiveLlmConnectionConfig config,
            LlmRecipeRepairContext context,
            CancellationToken cancellationToken = default
        )
        {
            LastRepairConfig = config;
            LastRepairContext = context;
            return repair(config, context, cancellationToken);
        }

        public Task<string?> StructureRecipeSourceAsync(
            EffectiveLlmConnectionConfig config,
            string sourceText,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<string?>(null);

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
                    [TextModel, RefineModel],
                    null
                )
            );

        public Task<LlmExtractedRecipePayload> ExtractRecipeFromTextAsync(
            EffectiveLlmConnectionConfig config,
            LlmExtractionContext context,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(Extraction);

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

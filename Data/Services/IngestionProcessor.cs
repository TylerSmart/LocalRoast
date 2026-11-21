using System.Text.Json;
using App.Core.Exceptions;
using App.Core.FoodCatalog;
using App.Core.Ingestion;
using App.Core.Media;
using App.Core.Recipes;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace App.Data.Services;

public sealed class IngestionProcessor(
    AppDbContext dbContext,
    ILlmSettingsService llmSettingsService,
    ILlmExtractionClient llmClient,
    IIngestionRecipePipeline recipePipeline,
    IIngestionRecipeRefiner recipeRefiner,
    IWebRecipeScraper scraper,
    IRecipeService recipeService,
    IMediaStorageService mediaStorageService,
    IPdfIngestionInspector pdfInspector,
    IIngestionQueueSignal queueSignal,
    ILogger<IngestionProcessor> logger,
    IRecipeEmbeddingService? recipeEmbeddingService = null,
    IIngestionLiveTracker? liveTracker = null,
    IIngestionRealtimeNotifier? realtimeNotifier = null
) : IIngestionProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<int> RecoverInterruptedJobsAsync(
        CancellationToken cancellationToken = default
    )
    {
        var activeIds = queueSignal.ActiveJobIds;

        var interrupted = await dbContext
            .IngestionJobs.Where(j =>
                (
                    j.Status == IngestionJobStatus.Preprocessing
                    || j.Status == IngestionJobStatus.Extracting
                ) && !activeIds.Contains(j.Id)
            )
            .ToListAsync(cancellationToken);

        var interruptedRefining = await dbContext
            .IngestionJobs.Where(j =>
                (
                    j.Status == IngestionJobStatus.ReadyForReview
                    || j.Status == IngestionJobStatus.Published
                )
                && j.StatusDetail != null
                && j.StatusDetail.StartsWith("Refining ")
                && !activeIds.Contains(j.Id)
            )
            .ToListAsync(cancellationToken);

        var interruptedRecipeRefinements = await dbContext
            .Recipes.Where(r =>
                r.RefinementStatus == RecipeRefinementStatus.Running && !activeIds.Contains(r.Id)
            )
            .ToListAsync(cancellationToken);

        if (
            interrupted.Count == 0
            && interruptedRefining.Count == 0
            && interruptedRecipeRefinements.Count == 0
        )
        {
            return 0;
        }

        foreach (var job in interrupted)
        {
            job.Status = IngestionJobStatus.Queued;

            job.NextRetryAt = null;

            job.StatusDetail = "Recovered from interrupted processing.";
        }

        foreach (var job in interruptedRefining)
        {
            job.StatusDetail =
                job.Status == IngestionJobStatus.Published
                    ? $"Published ({job.ExtractedModel ?? "ingested"})."
                    : $"Ready for side-by-side review ({job.ExtractedModel ?? "ingested"}).";
        }

        foreach (var recipe in interruptedRecipeRefinements)
        {
            recipe.RefinementStatus = RecipeRefinementStatus.Queued;
            recipe.RefinementRequestedAt ??= DateTimeOffset.UtcNow;
            recipe.RefinementStatusDetail = "Recovered from interrupted refinement.";
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        if (interrupted.Count > 0)
        {
            logger.LogInformation(
                "Recovered {Count} interrupted ingestion job(s) back to Queued state.",
                interrupted.Count
            );
        }

        await NotifyQueueChangedSafeAsync("recovered", cancellationToken: cancellationToken);

        return interrupted.Count + interruptedRefining.Count + interruptedRecipeRefinements.Count;
    }

    public async Task<bool> ProcessNextQueuedJobAsync(CancellationToken cancellationToken = default)
    {
        var config = await llmSettingsService.GetEffectiveConfigAsync(cancellationToken);
        var runNowOnly = !config.EnableIngestion;

        var now = DateTimeOffset.UtcNow;
        var activeIds = queueSignal.ActiveJobIds;

        var nextJob = await dbContext
            .IngestionJobs.Where(j =>
                j.Status == IngestionJobStatus.Queued
                && !activeIds.Contains(j.Id)
                && (j.NextRetryAt == null || j.NextRetryAt <= now)
                && (!runNowOnly || j.RunNowRequested)
            )
            .OrderByDescending(j => j.RunNowRequested)
            .ThenBy(j => j.CreatedAt)
            .Select(j => j.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (nextJob == Guid.Empty)
        {
            return false;
        }

        return await ProcessJobByIdAsync(nextJob, cancellationToken);
    }

    public async Task<bool> ProcessJobByIdAsync(
        Guid jobId,
        CancellationToken cancellationToken = default
    )
    {
        var initialConfig = await llmSettingsService.GetEffectiveConfigAsync(cancellationToken);
        using var executionLock = await queueSignal.AcquireJobExecutionAsync(
            initialConfig.EffectiveMaxConcurrentRequests,
            cancellationToken
        );

        var job = await dbContext.IngestionJobs.FirstOrDefaultAsync(
            j => j.Id == jobId,
            cancellationToken
        );

        if (
            job is null
            || job.Status is not IngestionJobStatus.Queued
            || (!initialConfig.EnableIngestion && !job.RunNowRequested)
        )
        {
            return false;
        }

        using var activeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        using var activeRegistration = queueSignal.RegisterActiveJobExecution(job.Id, activeCts);

        var jobCt = activeCts.Token;

        var includeVisionInitially = job.Channel == IngestionChannel.Images;
        var includeStage6Initially =
            initialConfig.HasRefinementModelConfigured && initialConfig.RefineDuringIngestion;

        using var workload = liveTracker?.BeginWorkload(
            kind: LlmWorkloadKind.RecipeIngestion,
            title: job.DisplayTitle,
            segments: BuildIngestionSegments(
                includeVisionInitially,
                includeStage6Initially,
                preprocessCompleted: false
            ),
            jobId: job.Id,
            channel: job.Channel,
            subtitle: job.SourceUrl ?? job.SourceBookTitle,
            thumbnailUrl: job.DownloadedThumbnailImagePath,
            activeModel: initialConfig.TextModel,
            statusDetail: "Preprocessing source content..."
        );

        try
        {
            // Step 1: Preprocessing

            job.Status = IngestionJobStatus.Preprocessing;

            job.StatusDetail = "Preprocessing source content...";

            // A run-now request covers this attempt only; later retries follow the pause setting.
            job.RunNowRequested = false;

            await dbContext.SaveChangesAsync(jobCt);
            workload?.ActivateSegment(
                "preprocess",
                subStatus: job.Channel switch
                {
                    IngestionChannel.WebUrl => "Scraping web page",
                    IngestionChannel.Pdf => "Inspecting PDF pages",
                    IngestionChannel.Images => "Preparing images",
                    _ => "Preparing source text",
                },
                statusDetail: job.StatusDetail
            );
            await NotifyQueueChangedSafeAsync("status-changed", job.Id, cancellationToken: jobCt);

            if (job.Channel == IngestionChannel.WebUrl)
            {
                if (string.IsNullOrWhiteSpace(job.ScrapedPageJson))
                {
                    if (string.IsNullOrWhiteSpace(job.SourceUrl))
                    {
                        throw new DomainValidationException("WebUrl job has no SourceUrl.");
                    }

                    var scrapeResult = await scraper.ScrapeRecipePageAsync(job.SourceUrl, jobCt);

                    var payload = scrapeResult.Payload;

                    job.ScrapedPageJson = JsonSerializer.Serialize(payload, JsonOptions);

                    job.NormalizedSourceText = BuildNormalizedWebText(payload);

                    if (!string.IsNullOrWhiteSpace(scrapeResult.DownloadedCoverImagePath))
                    {
                        job.DownloadedCoverImagePath = scrapeResult.DownloadedCoverImagePath;

                        job.DownloadedThumbnailImagePath =
                            scrapeResult.DownloadedThumbnailImagePath;
                    }

                    if (
                        !string.IsNullOrWhiteSpace(payload.PageTitle)
                        && (
                            string.IsNullOrWhiteSpace(job.DisplayTitle)
                            || job.DisplayTitle.Contains('/')
                            || job.DisplayTitle.StartsWith(
                                "http",
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                    )
                    {
                        job.DisplayTitle =
                            payload.PageTitle.Trim().Length > 200
                                ? payload.PageTitle.Trim()[..200]
                                : payload.PageTitle.Trim();
                    }

                    await dbContext.SaveChangesAsync(jobCt);
                    workload?.UpdateMetadata(
                        title: job.DisplayTitle,
                        thumbnailUrl: job.DownloadedThumbnailImagePath
                    );
                }
                else
                {
                    try
                    {
                        var cachedPayload = JsonSerializer.Deserialize<ScrapedWebRecipePayload>(
                            job.ScrapedPageJson,
                            JsonOptions
                        );

                        if (cachedPayload is not null)
                        {
                            var refreshedText = BuildNormalizedWebText(cachedPayload);

                            if (job.NormalizedSourceText != refreshedText)
                            {
                                job.NormalizedSourceText = refreshedText;

                                await dbContext.SaveChangesAsync(jobCt);
                            }
                        }
                    }
                    catch
                    {
                        // Keep existing NormalizedSourceText if cached payload cannot be parsed
                    }
                }
            }
            else if (job.Channel == IngestionChannel.RawText)
            {
                if (string.IsNullOrWhiteSpace(job.NormalizedSourceText))
                {
                    job.NormalizedSourceText = job.RawText;

                    await dbContext.SaveChangesAsync(jobCt);
                }
            }
            else if (job.Channel == IngestionChannel.Pdf)
            {
                if (string.IsNullOrWhiteSpace(job.PdfInspectionJson))
                {
                    string? pdfUrl = null;

                    if (!string.IsNullOrWhiteSpace(job.SourceFilesJson))
                    {
                        try
                        {
                            var sourceFiles = JsonSerializer.Deserialize<
                                List<IngestionSourceFileDto>
                            >(job.SourceFilesJson, JsonOptions);

                            pdfUrl = sourceFiles?.FirstOrDefault()?.UrlPath;
                        }
                        catch
                        {
                            // ignore
                        }
                    }

                    if (string.IsNullOrWhiteSpace(pdfUrl))
                    {
                        throw new DomainValidationException(
                            "PDF job does not have a valid source file path."
                        );
                    }

                    var jobPrefix = job.Id.ToString("N");

                    var inspection = await pdfInspector.InspectAndPrepareAsync(
                        pdfUrl,
                        jobPrefix,
                        jobCt
                    );

                    job.PdfInspectionJson = JsonSerializer.Serialize(
                        inspection.Summary,
                        JsonOptions
                    );

                    // Rendered PDF pages are text documents, not food photos, so we
                    // intentionally leave the cover empty; users can still promote a
                    // page to the cover manually from the review screen.
                    if (inspection.Summary.ExtractionMode == PdfExtractionMode.DigitalText)
                    {
                        job.NormalizedSourceText = inspection.NormalizedDigitalText;
                    }

                    await dbContext.SaveChangesAsync(jobCt);
                }
            }

            if (await IsJobDiscardedInDbAsync(job.Id, jobCt))
            {
                workload?.CancelWorkload("Job discarded.");
                return false;
            }

            workload?.CompleteSegment("preprocess");

            // Step 2: Extracting

            job.Status = IngestionJobStatus.Extracting;

            job.StatusDetail = "Extracting structured recipe via local LLM...";

            await dbContext.SaveChangesAsync(jobCt);
            await NotifyQueueChangedSafeAsync("status-changed", job.Id, cancellationToken: jobCt);

            var config = await llmSettingsService.GetEffectiveConfigAsync(jobCt);

            // Stage 1b: Transcribe images if Vision pipeline is required

            var requiresVision = job.Channel == IngestionChannel.Images;

            if (
                job.Channel == IngestionChannel.Pdf
                && !string.IsNullOrWhiteSpace(job.PdfInspectionJson)
            )
            {
                try
                {
                    var insp = JsonSerializer.Deserialize<PdfInspectionSummaryDto>(
                        job.PdfInspectionJson,
                        JsonOptions
                    );

                    if (insp?.ExtractionMode == PdfExtractionMode.VisionRaster)
                    {
                        requiresVision = true;
                    }
                }
                catch
                {
                    // ignore
                }
            }

            var includeStage6Now =
                config.HasRefinementModelConfigured && config.RefineDuringIngestion;
            if (
                requiresVision != includeVisionInitially
                || includeStage6Now != includeStage6Initially
            )
            {
                workload?.SetSegments(
                    BuildIngestionSegments(
                        requiresVision,
                        includeStage6Now,
                        preprocessCompleted: true
                    )
                );
            }

            string modelUsed;
            string sourceText;

            if (requiresVision)
            {
                if (string.IsNullOrWhiteSpace(config.VisionModel))
                {
                    throw new LlmHostUnavailableException(
                        "No Vision LLM model is configured in LLM Settings. Please select a Vision Model and retry."
                    );
                }

                if (string.IsNullOrWhiteSpace(config.TextModel))
                {
                    throw new LlmHostUnavailableException(
                        "No Text LLM model is configured in LLM Settings. Please select a Text Model and retry."
                    );
                }

                modelUsed = $"{config.VisionModel} → {config.TextModel}";

                var imageInputs = new List<LlmVisionImageInput>();

                if (
                    job.Channel == IngestionChannel.Images
                    && !string.IsNullOrWhiteSpace(job.SourceFilesJson)
                )
                {
                    var sourceFiles =
                        JsonSerializer.Deserialize<List<IngestionSourceFileDto>>(
                            job.SourceFilesJson,
                            JsonOptions
                        ) ?? [];

                    foreach (var sf in sourceFiles)
                    {
                        var jpegBytes = await mediaStorageService.EncodeMediaImageAsVisionJpegAsync(
                            sf.UrlPath,
                            1568,
                            jobCt
                        );

                        var base64 = Convert.ToBase64String(jpegBytes);

                        imageInputs.Add(
                            new LlmVisionImageInput(sf.PageNumber, "image/jpeg", base64)
                        );
                    }
                }
                else if (
                    job.Channel == IngestionChannel.Pdf
                    && !string.IsNullOrWhiteSpace(job.PdfInspectionJson)
                )
                {
                    var insp = JsonSerializer.Deserialize<PdfInspectionSummaryDto>(
                        job.PdfInspectionJson,
                        JsonOptions
                    );

                    if (insp?.RenderedPageImages is { Count: > 0 })
                    {
                        foreach (var page in insp.RenderedPageImages)
                        {
                            var jpegBytes =
                                await mediaStorageService.EncodeMediaImageAsVisionJpegAsync(
                                    page.UrlPath,
                                    1568,
                                    jobCt
                                );

                            var base64 = Convert.ToBase64String(jpegBytes);

                            imageInputs.Add(
                                new LlmVisionImageInput(page.PageNumber, "image/jpeg", base64)
                            );
                        }
                    }
                }

                if (imageInputs.Count == 0)
                {
                    throw new DomainValidationException(
                        "No images were available to send to the Vision LLM."
                    );
                }

                string? hintText = null;

                if (!string.IsNullOrWhiteSpace(job.SourceBookTitle))
                {
                    hintText = job.SourceBookPage.HasValue
                        ? $"Book: {job.SourceBookTitle}, Page: {job.SourceBookPage.Value}"
                        : $"Book: {job.SourceBookTitle}";
                }

                var visionContext = new LlmVisionExtractionContext(
                    Images: imageInputs,
                    SupplementalHintText: hintText
                );

                job.StatusDetail = "Reading recipe text from images via local LLM...";
                await dbContext.SaveChangesAsync(jobCt);
                workload?.ActivateSegment(
                    "vision",
                    subStatus: $"{imageInputs.Count} page{(imageInputs.Count == 1 ? "" : "s")}",
                    statusDetail: job.StatusDetail,
                    activeModel: config.ForVision().VisionModel ?? config.VisionModel
                );
                sourceText =
                    await llmClient.TranscribeRecipeImagesAsync(config, visionContext, jobCt)
                    ?? throw new DomainValidationException(
                        "No recipe text could be read from the images."
                    );
                workload?.CompleteSegment("vision");
                job.NormalizedSourceText = sourceText;
                job.StatusDetail = "Extracting structured recipe via local LLM...";
                await dbContext.SaveChangesAsync(jobCt);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(config.TextModel))
                {
                    throw new LlmHostUnavailableException(
                        "No Text LLM model is configured in LLM Settings. Please select a Text Model and retry."
                    );
                }

                modelUsed = config.TextModel;
                sourceText = job.NormalizedSourceText ?? job.RawText ?? "";
            }

            string? fallbackSourceName = job.SourceName;

            string? fallbackSourceAuthor = job.SourceAuthor;

            if (!string.IsNullOrWhiteSpace(job.ScrapedPageJson))
            {
                try
                {
                    var page = JsonSerializer.Deserialize<ScrapedWebRecipePayload>(
                        job.ScrapedPageJson,
                        JsonOptions
                    );

                    fallbackSourceName ??= page?.SiteName;

                    fallbackSourceAuthor ??= page?.Author;
                }
                catch
                {
                    // Ignore parse error
                }
            }

            IReadOnlyList<Guid>? fallbackCollectionIds = null;

            if (!string.IsNullOrWhiteSpace(job.InitialCollectionIdsJson))
            {
                try
                {
                    var parsedIds = JsonSerializer.Deserialize<List<Guid>>(
                        job.InitialCollectionIdsJson,
                        JsonOptions
                    );

                    if (parsedIds is { Count: > 0 })
                    {
                        var distinctIds = parsedIds.Distinct().ToList();

                        fallbackCollectionIds = await dbContext
                            .Collections.AsNoTracking()
                            .Where(c => distinctIds.Contains(c.Id))
                            .Select(c => c.Id)
                            .ToListAsync(jobCt);
                    }
                }
                catch
                {
                    // Ignore parse error
                }
            }

            // Stage 1-5: Multi-Stage Pipeline -> IngestionExtractedDraftDto

            var pipelineResult = await recipePipeline.RunAsync(
                new IngestionPipelineRequest(
                    SourceText: sourceText,
                    Config: config,
                    ExcludeRecipeId: job.ResultingRecipeId,
                    SourceUrl: job.SourceUrl,
                    SourceName: fallbackSourceName,
                    SourceAuthor: fallbackSourceAuthor,
                    SourceBookTitle: job.SourceBookTitle,
                    SourceBookPage: job.SourceBookPage,
                    CoverImagePath: job.DownloadedCoverImagePath,
                    ThumbnailImagePath: job.DownloadedThumbnailImagePath,
                    RetainSourceAttachments: job.Channel
                        is IngestionChannel.Images
                            or IngestionChannel.Pdf
                            or IngestionChannel.RawText,
                    SuggestedCollectionIds: fallbackCollectionIds,
                    OnStageProgress: async statusDetail =>
                    {
                        job.StatusDetail = statusDetail;
                        await dbContext.SaveChangesAsync(jobCt);
                    }
                ),
                jobCt
            );

            if (await IsJobDiscardedInDbAsync(job.Id, jobCt))
            {
                workload?.CancelWorkload("Job discarded.");
                return false;
            }

            var draft = pipelineResult.Draft with
            {
                ExtractedModel = IngestionMetadataAndTagHelper.CleanOptionalText(modelUsed, 200),
            };

            var allValidationIssues = new List<string>(draft.ValidationIssues ?? []);

            if (draft.Ingredients is { Count: > 0 })
            {
                var (resolvedDraft, referenceIssues) =
                    await IngestionCatalogMaterializer.ResolveDraftRecipeReferencesAsync(
                        dbContext,
                        draft,
                        job.ResultingRecipeId,
                        jobCt
                    );
                // Pipeline placeholder sub-recipe errors are superseded by catalog resolution above.
                allValidationIssues.RemoveAll(s =>
                    s.Contains("references sub-recipe", StringComparison.OrdinalIgnoreCase)
                );
                allValidationIssues.AddRange(referenceIssues);
                var distinctIssues = allValidationIssues
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var filteredWarnings = FilterBenignSubRecipeQualityWarnings(
                    resolvedDraft.QualityWarnings,
                    resolvedDraft.Ingredients
                );
                var updatedDraft = resolvedDraft with
                {
                    QualityWarnings = filteredWarnings,
                    ValidationIssues = distinctIssues,
                };
                draft = updatedDraft with
                {
                    ConfidenceScore = IngestionConfidenceScorer.Compute(
                        updatedDraft,
                        updatedDraft.PipelineRepairCount
                    ),
                };
            }

            job.ExtractedModel = modelUsed;
            job.RefinedModel = null;
            job.RefinedAt = null;
            job.RefinementAttemptCount = 0;
            job.RefinementNextRetryAt = null;

            // Stage 6 (optional): Post-polish Refinement pass before Auto-Publish / Food Catalog creation
            if (config.HasRefinementModelConfigured && config.RefineDuringIngestion)
            {
                var refineConfig = config.ForRefinement();
                var refineModelLabel =
                    refineConfig.TextModel?.Trim()
                    ?? config.RefineModel?.Trim()
                    ?? "refinement model";
                job.StatusDetail =
                    $"Stage 6: Refining recipe & catalog names via {refineModelLabel}...";
                await dbContext.SaveChangesAsync(jobCt);
                workload?.ActivateSegment(
                    "stage6",
                    subStatus: refineModelLabel,
                    statusDetail: job.StatusDetail,
                    activeModel: refineModelLabel
                );

                try
                {
                    var refineResult = await recipeRefiner.RefineDraftAsync(
                        new IngestionRefineDraftRequest(
                            SourceText: sourceText,
                            Draft: draft,
                            Config: config,
                            ExcludeRecipeId: job.ResultingRecipeId
                        ),
                        jobCt
                    );

                    if (await IsJobDiscardedInDbAsync(job.Id, jobCt))
                    {
                        workload?.CancelWorkload("Job discarded.");
                        return false;
                    }

                    draft = refineResult.Draft with
                    {
                        ExtractedModel = IngestionMetadataAndTagHelper.CleanOptionalText(
                            modelUsed,
                            200
                        ),
                        RefinedModel = refineResult.RefinedModel,
                    };
                    allValidationIssues = new List<string>(draft.ValidationIssues ?? []);
                    job.RefinedModel = refineResult.RefinedModel;
                    job.RefinedAt = DateTimeOffset.UtcNow;
                    workload?.CompleteSegment("stage6");
                }
                catch (LlmHostUnavailableException ex)
                    when (ex.IsQuotaExhausted
                        && !IngestionRecipePipeline.QuotaExhaustionAffectsPrimaryTextWorkflow(
                            ex,
                            config
                        )
                    )
                {
                    workload?.SkipSegment("stage6");
                    logger.LogWarning(
                        ex,
                        "Refinement model quota exhausted on '{Model}' during ingestion of job {JobId}; continuing without refinement.",
                        refineModelLabel,
                        job.Id
                    );
                }
                catch (Exception ex)
                    when (ex is not OperationCanceledException and not LlmHostUnavailableException)
                {
                    workload?.SkipSegment("stage6");
                    logger.LogWarning(
                        ex,
                        "Refinement pass failed during ingestion of job {JobId} on '{Model}'; keeping initial extracted draft so refinement can retry later.",
                        job.Id,
                        refineModelLabel
                    );
                }
            }

            if (allValidationIssues.Count > 0)
            {
                logger.LogWarning(
                    "Recipe extraction for job {JobId} requires review: {Issues}",
                    job.Id,
                    string.Join(" ", allValidationIssues)
                );
            }

            if (
                string.IsNullOrWhiteSpace(job.SourceBookTitle)
                && !string.IsNullOrWhiteSpace(draft.SourceBookTitle)
            )
            {
                job.SourceBookTitle = draft.SourceBookTitle;
            }

            if (!job.SourceBookPage.HasValue && draft.SourceBookPage.HasValue)
            {
                job.SourceBookPage = draft.SourceBookPage;
            }

            // Stage 5/6 -> Auto-Publish vs ReadyForReview

            var hasUnresolvedRecipeReferences =
                draft.Ingredients?.Any(i => i.IsRecipeReference && !i.MatchedRecipeId.HasValue)
                == true;

            var canAutoPublish =
                pipelineResult.IsPlausibleRecipe
                && (draft.ValidationIssues?.Count ?? 0) == 0
                && (draft.QualityWarnings?.Count ?? 0) == 0
                && !hasUnresolvedRecipeReferences
                && (config.AutoPublishHighConfidenceText || job.AutoPublish)
                && (
                    job.Channel == IngestionChannel.WebUrl
                    || job.Channel == IngestionChannel.RawText
                )
                && !string.IsNullOrWhiteSpace(draft.Title)
                && (draft.Ingredients?.Count ?? 0) >= 1
                && (draft.Steps?.Count ?? 0) >= 1
                && draft.ConfidenceScore >= (job.AutoPublish ? 0.60m : 0.85m);

            var publishedSuccessfully = false;
            workload?.ActivateSegment(
                "finalize",
                subStatus: canAutoPublish ? "Auto-publishing recipe" : "Saving draft for review",
                statusDetail: canAutoPublish
                    ? "Auto-publishing high-confidence recipe..."
                    : "Saving extracted draft for review..."
            );
            if (canAutoPublish)
            {
                try
                {
                    var createdRecipe = await PublishDraftInternalAsync(job, draft, jobCt);

                    await IngestionCatalogMaterializer.BacklinkUnresolvedRecipeReferencesAsync(
                        dbContext,
                        createdRecipe.Id,
                        createdRecipe.Title,
                        jobCt
                    );

                    job.Status = IngestionJobStatus.Published;

                    job.ResultingRecipeId = createdRecipe.Id;

                    job.ExtractedDraftJson = JsonSerializer.Serialize(draft, JsonOptions);

                    job.ConfidenceScore = draft.ConfidenceScore;

                    job.DisplayTitle = draft.Title;

                    job.ProcessedAt = DateTimeOffset.UtcNow;

                    job.StatusDetail = !string.IsNullOrWhiteSpace(job.RefinedModel)
                        ? $"Auto-published high-confidence extraction ({modelUsed}, refined by {job.RefinedModel})."
                        : $"Auto-published high-confidence extraction ({modelUsed}).";
                    publishedSuccessfully = true;
                }
                catch (DomainValidationException ex)
                {
                    logger.LogWarning(
                        ex,
                        "Auto-publish failed validation for job {JobId}; demoting to ReadyForReview with saved draft.",
                        job.Id
                    );

                    var combinedIssues = (draft.ValidationIssues ?? [])
                        .Append(ex.Message)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    draft = draft with
                    {
                        ValidationIssues = combinedIssues,
                        ConfidenceScore = Math.Min(draft.ConfidenceScore, 0.70m),
                    };

                    var preservedBookTitle = job.SourceBookTitle;
                    var preservedBookPage = job.SourceBookPage;
                    var preservedRefinedModel = job.RefinedModel;
                    var preservedRefinedAt = job.RefinedAt;
                    dbContext.ChangeTracker.Clear();
                    job =
                        await dbContext.IngestionJobs.FirstOrDefaultAsync(
                            j => j.Id == job.Id,
                            jobCt
                        ) ?? job;
                    job.ExtractedModel = modelUsed;
                    job.RefinedModel = preservedRefinedModel;
                    job.RefinedAt = preservedRefinedAt;
                    if (!string.IsNullOrWhiteSpace(preservedBookTitle))
                    {
                        job.SourceBookTitle = preservedBookTitle;
                    }
                    if (preservedBookPage.HasValue)
                    {
                        job.SourceBookPage = preservedBookPage;
                    }

                    allValidationIssues.Add(ex.Message);
                }
            }

            if (!publishedSuccessfully)
            {
                job.Status = IngestionJobStatus.ReadyForReview;

                job.ExtractedDraftJson = JsonSerializer.Serialize(draft, JsonOptions);

                job.ConfidenceScore = draft.ConfidenceScore;

                job.DisplayTitle = draft.Title;

                job.ProcessedAt = DateTimeOffset.UtcNow;

                job.StatusDetail = !string.IsNullOrWhiteSpace(job.RefinedModel)
                    ? $"Ready for side-by-side review ({modelUsed}, refined by {job.RefinedModel})."
                    : $"Ready for side-by-side review ({modelUsed}).";
                if (allValidationIssues.Count > 0)
                {
                    var detail =
                        $"{job.StatusDetail} Validation: {string.Join(" ", allValidationIssues.Distinct(StringComparer.Ordinal))}";
                    job.StatusDetail = detail[..Math.Min(detail.Length, 2000)];
                }
            }

            await dbContext.SaveChangesAsync(jobCt);

            if (publishedSuccessfully)
            {
                var publishedRecipeIds = new List<Guid>();
                if (job.ResultingRecipeId.HasValue)
                {
                    publishedRecipeIds.Add(job.ResultingRecipeId.Value);
                }

                await IngestionCatalogMaterializer.PromoteWaitingRecipeReferenceJobsAsync(
                    dbContext,
                    recipeService,
                    mediaStorageService,
                    globalAutoPublish: config.AutoPublishHighConfidenceText,
                    jobCt,
                    promotedRecipeIds: publishedRecipeIds
                );

                await TryEmbedPublishedRecipesAsync(config, publishedRecipeIds, jobCt);
            }

            workload?.UpdateMetadata(
                title: job.DisplayTitle,
                thumbnailUrl: job.DownloadedThumbnailImagePath
            );
            workload?.CompleteSegment("finalize");
            workload?.CompleteWorkload(job.StatusDetail);
            await NotifyQueueChangedSafeAsync("completed", job.Id, cancellationToken: jobCt);

            return true;
        }
        catch (OperationCanceledException)
        {
            if (await IsJobDiscardedInDbAsync(job.Id, CancellationToken.None))
            {
                workload?.CancelWorkload("Job discarded.");
                return false;
            }

            logger.LogInformation(
                "Job {JobId} execution was canceled or ingestion was paused. Reverting to Queued.",
                job.Id
            );

            try
            {
                dbContext.ChangeTracker.Clear();
                var trackedJob = await dbContext.IngestionJobs.FirstOrDefaultAsync(
                    j => j.Id == job.Id,
                    CancellationToken.None
                );

                if (trackedJob is not null)
                {
                    trackedJob.Status = IngestionJobStatus.Queued;
                    trackedJob.NextRetryAt = null;
                    trackedJob.StatusDetail = "Paused. Queued to resume when ingestion is enabled.";
                    await dbContext.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to revert canceled job {JobId} to Queued.", job.Id);
            }

            workload?.CancelWorkload("Paused. Queued to resume when ingestion is enabled.");
            await NotifyQueueChangedSafeAsync("status-changed", job.Id);

            return false;
        }
        catch (LlmHostUnavailableException ex)
        {
            if (await IsJobDiscardedInDbAsync(job.Id, CancellationToken.None))
            {
                workload?.CancelWorkload("Job discarded.");
                return false;
            }

            logger.LogWarning(
                ex,
                "LLM host unavailable while processing job {JobId}. Transitioning to WaitingForLlmHost.",
                job.Id
            );

            try
            {
                dbContext.ChangeTracker.Clear();
                var trackedJob = await dbContext.IngestionJobs.FirstOrDefaultAsync(
                    j => j.Id == job.Id,
                    CancellationToken.None
                );

                if (trackedJob is not null)
                {
                    var safeguardsEnabled = ex.ExhaustedQuotaScope.HasValue
                        ? ex.SafeguardsEnabled
                        : initialConfig.EnableRateLimitSafeguards;
                    if (ex.IsQuotaExhausted && safeguardsEnabled)
                    {
                        var resetTimezone = !string.IsNullOrWhiteSpace(ex.QuotaResetTimezone)
                            ? ex.QuotaResetTimezone
                            : initialConfig.EffectiveQuotaResetTimezone;
                        var resetTime = !string.IsNullOrWhiteSpace(ex.QuotaResetTime)
                            ? ex.QuotaResetTime
                            : initialConfig.EffectiveQuotaResetTime;
                        var nextReset = initialConfig.AutoResumeQueue
                            ? LlmQuotaSchedule.GetNextResetUtc(
                                DateTimeOffset.UtcNow,
                                resetTimezone,
                                resetTime
                            )
                            : (DateTimeOffset?)null;
                        var reason = $"Provider quota exhausted ({ex.Message})";

                        await llmSettingsService.SetIngestionEnabledAsync(
                            false,
                            autoResumeAt: nextReset,
                            pauseReason: reason,
                            CancellationToken.None
                        );
                        trackedJob.Status = IngestionJobStatus.Queued;
                        trackedJob.NextRetryAt = null;
                        var tzLabel = !string.IsNullOrWhiteSpace(resetTimezone)
                            ? $" ({resetTimezone})"
                            : string.Empty;
                        trackedJob.StatusDetail = nextReset.HasValue
                            ? $"Paused: {reason}. Ingestion will automatically resume at {nextReset.Value:u}{tzLabel}."
                            : $"Paused: {reason}. Ingestion was auto-paused until manually resumed.";
                        await dbContext.SaveChangesAsync(CancellationToken.None);
                        logger.LogWarning(
                            "Auto-paused ingestion for job {JobId} because provider quota is exhausted.",
                            job.Id
                        );
                        workload?.CancelWorkload(trackedJob.StatusDetail);
                        await NotifyQueueChangedSafeAsync(
                            "paused-changed",
                            job.Id,
                            enableIngestion: false,
                            pausedUntilUtc: nextReset,
                            pauseReason: reason
                        );
                        return false;
                    }

                    trackedJob.Status = IngestionJobStatus.WaitingForLlmHost;
                    trackedJob.AttemptCount++;
                    var delaySeconds = Math.Min(
                        300,
                        15 * (1 << Math.Min(trackedJob.AttemptCount - 1, 5))
                    );
                    trackedJob.NextRetryAt = DateTimeOffset.UtcNow.AddSeconds(delaySeconds);
                    trackedJob.StatusDetail = ex.Message;
                    await dbContext.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch (Exception saveEx)
            {
                logger.LogError(
                    saveEx,
                    "Failed to persist WaitingForLlmHost state for job {JobId}.",
                    job.Id
                );
            }

            workload?.FailWorkload(ex.Message);
            await NotifyQueueChangedSafeAsync("status-changed", job.Id);

            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (await IsJobDiscardedInDbAsync(job.Id, CancellationToken.None))
            {
                workload?.CancelWorkload("Job discarded.");
                return false;
            }

            try
            {
                dbContext.ChangeTracker.Clear();
                var trackedJob = await dbContext.IngestionJobs.FirstOrDefaultAsync(
                    j => j.Id == job.Id,
                    CancellationToken.None
                );

                if (trackedJob is not null)
                {
                    trackedJob.AttemptCount++;

                    int maxRetries;
                    try
                    {
                        var cfg = await llmSettingsService.GetEffectiveConfigAsync(
                            CancellationToken.None
                        );
                        maxRetries = cfg.MaxIngestionRetries;
                    }
                    catch
                    {
                        maxRetries = 3;
                    }

                    if (trackedJob.AttemptCount <= maxRetries)
                    {
                        var delaySeconds = Math.Min(
                            60,
                            5 * (1 << Math.Min(trackedJob.AttemptCount - 1, 4))
                        );
                        trackedJob.Status = IngestionJobStatus.Queued;
                        trackedJob.NextRetryAt = DateTimeOffset.UtcNow.AddSeconds(delaySeconds);
                        trackedJob.StatusDetail =
                            $"Attempt {trackedJob.AttemptCount}/{maxRetries} failed: {ex.Message}. Retrying shortly...";

                        logger.LogWarning(
                            ex,
                            "Error processing job {JobId} (attempt {AttemptCount}/{MaxRetries}). Scheduled auto-retry in {DelaySeconds}s.",
                            trackedJob.Id,
                            trackedJob.AttemptCount,
                            maxRetries,
                            delaySeconds
                        );

                        await dbContext.SaveChangesAsync(CancellationToken.None);
                        workload?.FailWorkload(trackedJob.StatusDetail);
                        await NotifyQueueChangedSafeAsync("status-changed", job.Id);
                        return false;
                    }

                    logger.LogError(
                        ex,
                        "Fatal error processing job {JobId} after {AttemptCount} attempt(s). Transitioning to Failed.",
                        trackedJob.Id,
                        trackedJob.AttemptCount
                    );

                    trackedJob.Status = IngestionJobStatus.Failed;
                    trackedJob.NextRetryAt = null;
                    trackedJob.StatusDetail = ex.Message;

                    await dbContext.SaveChangesAsync(CancellationToken.None);
                    workload?.FailWorkload(ex.Message);
                    await NotifyQueueChangedSafeAsync("status-changed", job.Id);
                    return false;
                }
            }
            catch (Exception saveEx)
            {
                logger.LogError(
                    saveEx,
                    "Failed to persist error state for job {JobId}. Original error: {OriginalError}",
                    job.Id,
                    ex.Message
                );
            }

            workload?.FailWorkload(ex.Message);
            await NotifyQueueChangedSafeAsync("status-changed", job.Id);

            return false;
        }
    }

    private Task<bool> IsJobDiscardedInDbAsync(Guid jobId, CancellationToken cancellationToken) =>
        dbContext
            .IngestionJobs.AsNoTracking()
            .AnyAsync(
                j => j.Id == jobId && j.Status == IngestionJobStatus.Discarded,
                cancellationToken
            );

    public async Task<int> ProbeAndPromoteWaitingJobsAsync(
        CancellationToken cancellationToken = default
    )
    {
        var now = DateTimeOffset.UtcNow;
        var activeIds = queueSignal.ActiveJobIds;

        var waitingJobs = await dbContext
            .IngestionJobs.Where(j =>
                j.Status == IngestionJobStatus.WaitingForLlmHost
                && (j.NextRetryAt == null || j.NextRetryAt <= now)
            )
            .ToListAsync(cancellationToken);

        if (waitingJobs.Count == 0)
        {
            return 0;
        }

        var config = await llmSettingsService.GetEffectiveConfigAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(config.TextModel))
        {
            return 0;
        }

        var probeResult = await llmClient.ProbeAndListModelsAsync(config, 10, cancellationToken);

        if (probeResult.IsReachable)
        {
            var promotedCount = 0;
            var visionConfig = config.ForVision();
            bool? visionHostReachable =
                visionConfig.ConnectionKey == config.ConnectionKey ? true : null;

            foreach (var job in waitingJobs)
            {
                var isVisionJob = job.Channel == IngestionChannel.Images;

                if (
                    job.Channel == IngestionChannel.Pdf
                    && !string.IsNullOrWhiteSpace(job.PdfInspectionJson)
                )
                {
                    try
                    {
                        var insp = JsonSerializer.Deserialize<PdfInspectionSummaryDto>(
                            job.PdfInspectionJson,
                            JsonOptions
                        );

                        if (insp?.ExtractionMode == PdfExtractionMode.VisionRaster)
                        {
                            isVisionJob = true;
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                }

                if (isVisionJob && !string.IsNullOrWhiteSpace(visionConfig.VisionModel))
                {
                    // Vision may run on a different machine/provider than text extraction.
                    visionHostReachable ??= (
                        await llmClient.ProbeAndListModelsAsync(visionConfig, 10, cancellationToken)
                    ).IsReachable;
                }

                var canPromote = isVisionJob
                    ? !string.IsNullOrWhiteSpace(visionConfig.VisionModel)
                        && visionHostReachable == true
                    : !string.IsNullOrWhiteSpace(config.TextModel);

                if (canPromote)
                {
                    job.Status = IngestionJobStatus.Queued;

                    job.NextRetryAt = null;

                    job.StatusDetail = "LLM host is online; queued for extraction.";

                    promotedCount++;

                    if (config.EnableRateLimitSafeguards)
                    {
                        logger.LogInformation(
                            "Rate limit safeguard: promoted single canary job {JobId} from WaitingForLlmHost back to Queued.",
                            job.Id
                        );
                        break;
                    }
                }
            }

            if (promotedCount > 0)
            {
                await dbContext.SaveChangesAsync(cancellationToken);

                queueSignal.NotifyWorkAvailable();
                await NotifyQueueChangedSafeAsync(
                    "promoted-waiting",
                    cancellationToken: cancellationToken
                );

                logger.LogInformation(
                    "LLM host is back online; promoted {Count} waiting job(s) to Queued.",
                    promotedCount
                );

                return promotedCount;
            }
        }

        // Host still down; increment backoff

        foreach (var job in waitingJobs)
        {
            job.NextRetryAt = DateTimeOffset.UtcNow.AddSeconds(30);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return 0;
    }

    public async Task<bool> ProcessNextRequestedRefinementAsync(
        CancellationToken cancellationToken = default
    )
    {
        var config = await llmSettingsService.GetEffectiveConfigAsync(cancellationToken);
        if (!config.HasRefinementModelConfigured)
        {
            return false;
        }

        var activeIds = queueSignal.ActiveJobIds;

        var requestedRecipeId = await dbContext
            .Recipes.AsNoTracking()
            .Where(r =>
                r.RefinementStatus == RecipeRefinementStatus.Queued
                && r.Status != RecipeStatus.Archived
                && !activeIds.Contains(r.Id)
            )
            .OrderBy(r => r.RefinementRequestedAt)
            .ThenBy(r => r.Id)
            .Select(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (requestedRecipeId != Guid.Empty)
        {
            return await ProcessRequestedRecipeRefinementByIdAsync(
                requestedRecipeId,
                cancellationToken
            );
        }

        var requestedJobId = await dbContext
            .IngestionJobs.AsNoTracking()
            .Where(j =>
                j.RefinementRunNowRequested
                && (
                    j.Status == IngestionJobStatus.ReadyForReview
                    || j.Status == IngestionJobStatus.Published
                )
                && j.ExtractedDraftJson != null
                && !activeIds.Contains(j.Id)
            )
            .OrderBy(j => j.ModifiedAt)
            .ThenBy(j => j.Id)
            .Select(j => j.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (requestedJobId != Guid.Empty)
        {
            return await ProcessJobRefinementInternalAsync(
                requestedJobId,
                isManualRunNow: true,
                cancellationToken
            );
        }

        return false;
    }

    public async Task<bool> ProcessNextIdleRefinementAsync(
        CancellationToken cancellationToken = default
    )
    {
        var config = await llmSettingsService.GetEffectiveConfigAsync(cancellationToken);
        if (
            !config.EnableIngestion
            || !config.HasRefinementModelConfigured
            || !config.RefineDuringIdle
        )
        {
            return false;
        }

        if (await HasActiveOrRunnableIngestionJobsAsync(cancellationToken))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var activeIds = queueSignal.ActiveJobIds;
        var maxRetries = Math.Max(0, config.MaxIngestionRetries);

        var candidateId = await dbContext
            .IngestionJobs.AsNoTracking()
            .Where(j =>
                (
                    j.Status == IngestionJobStatus.ReadyForReview
                    || j.Status == IngestionJobStatus.Published
                )
                && j.RefinedAt == null
                && j.ExtractedDraftJson != null
                && !activeIds.Contains(j.Id)
                && j.RefinementAttemptCount <= maxRetries
                && (j.RefinementNextRetryAt == null || j.RefinementNextRetryAt <= now)
            )
            .OrderBy(j => j.Status == IngestionJobStatus.ReadyForReview ? 0 : 1)
            .ThenByDescending(j => j.CreatedAt)
            .ThenBy(j => j.Id)
            .Select(j => j.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (candidateId == Guid.Empty)
        {
            return false;
        }

        return await ProcessJobRefinementInternalAsync(
            candidateId,
            isManualRunNow: false,
            cancellationToken
        );
    }

    private async Task<bool> ProcessRequestedRecipeRefinementByIdAsync(
        Guid recipeId,
        CancellationToken cancellationToken
    )
    {
        var config = await llmSettingsService.GetEffectiveConfigAsync(cancellationToken);
        if (!config.HasRefinementModelConfigured)
        {
            return false;
        }

        var refineConfig = config.ForRefinement();
        using var executionLock = await queueSignal.AcquireJobExecutionAsync(
            refineConfig.EffectiveMaxConcurrentRequests,
            cancellationToken
        );

        config = await llmSettingsService.GetEffectiveConfigAsync(cancellationToken);
        if (!config.HasRefinementModelConfigured)
        {
            return false;
        }

        refineConfig = config.ForRefinement();
        var refineModelLabel =
            refineConfig.TextModel?.Trim() ?? config.RefineModel?.Trim() ?? "refinement model";

        var trackedRecipe = await dbContext.Recipes.FirstOrDefaultAsync(
            r => r.Id == recipeId,
            cancellationToken
        );

        if (
            trackedRecipe is null
            || trackedRecipe.Status == RecipeStatus.Archived
            || trackedRecipe.RefinementStatus != RecipeRefinementStatus.Queued
        )
        {
            return false;
        }

        using var activeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var activeRegistration = queueSignal.RegisterActiveJobExecution(recipeId, activeCts);
        var runCt = activeCts.Token;

        using var workload = liveTracker?.BeginWorkload(
            kind: LlmWorkloadKind.RecipeRefinement,
            title: trackedRecipe.Title,
            segments: BuildRefinementSegments("Load Recipe", "Apply & Re-embed"),
            recipeId: recipeId,
            subtitle: "On-demand recipe refinement",
            thumbnailUrl: trackedRecipe.ThumbnailImagePath,
            activeModel: refineModelLabel,
            statusDetail: $"Refining recipe via {refineModelLabel}..."
        );

        try
        {
            workload?.ActivateSegment("prepare", "Loading recipe & catalog");
            trackedRecipe.RefinementStatus = RecipeRefinementStatus.Running;
            trackedRecipe.RefinementStatusDetail = $"Refining recipe via {refineModelLabel}...";
            await dbContext.SaveChangesAsync(runCt);

            var runningModifiedAt = trackedRecipe.ModifiedAt;

            dbContext.ChangeTracker.Clear();
            var fullRecipe = await dbContext
                .Recipes.AsNoTracking()
                .Include(r => r.Ingredients)
                    .ThenInclude(i => i.FoodItem)
                .Include(r => r.Ingredients)
                    .ThenInclude(i => i.LinkedRecipe)
                .Include(r => r.Ingredients)
                    .ThenInclude(i => i.Replacements)
                        .ThenInclude(rep => rep.FoodItem)
                .Include(r => r.Steps)
                    .ThenInclude(s => s.Timers)
                .Include(r => r.RecipeCollections)
                .Include(r => r.RecipeTags)
                .Include(r => r.IngestionJobs)
                .FirstOrDefaultAsync(r => r.Id == recipeId, runCt);

            if (fullRecipe is null || fullRecipe.Status == RecipeStatus.Archived)
            {
                return false;
            }

            var previousFoodItemIds = new HashSet<Guid>();
            foreach (var ing in fullRecipe.Ingredients)
            {
                if (ing.FoodItemId.HasValue)
                {
                    previousFoodItemIds.Add(ing.FoodItemId.Value);
                }

                foreach (var rep in ing.Replacements)
                {
                    if (rep.FoodItemId.HasValue)
                    {
                        previousFoodItemIds.Add(rep.FoodItemId.Value);
                    }
                }
            }

            var linkedJob = fullRecipe
                .IngestionJobs.Where(j => j.Status != IngestionJobStatus.Discarded)
                .OrderByDescending(j => j.CreatedAt)
                .FirstOrDefault();

            var draft = BuildDraftFromRecipe(fullRecipe);
            var sourceText = ResolveSourceTextForRecipeRefinement(linkedJob, draft);

            workload?.CompleteSegment("prepare");
            workload?.ActivateSegment(
                "stage6",
                subStatus: refineModelLabel,
                statusDetail: $"Refining recipe via {refineModelLabel}...",
                activeModel: refineModelLabel
            );

            var refineResult = await recipeRefiner.RefineDraftAsync(
                new IngestionRefineDraftRequest(
                    SourceText: sourceText,
                    Draft: draft,
                    Config: config,
                    ExcludeRecipeId: recipeId
                ),
                runCt
            );

            workload?.CompleteSegment("stage6");
            workload?.ActivateSegment("finalize", "Applying refined recipe");

            dbContext.ChangeTracker.Clear();
            var freshState = await dbContext
                .Recipes.AsNoTracking()
                .Where(r => r.Id == recipeId)
                .Select(r => new
                {
                    r.Id,
                    r.Status,
                    r.RefinementStatus,
                    r.ModifiedAt,
                    r.CoverImagePath,
                    r.ThumbnailImagePath,
                    r.ExtractedModel,
                })
                .FirstOrDefaultAsync(runCt);

            if (
                freshState is null
                || freshState.Status == RecipeStatus.Archived
                || freshState.RefinementStatus != RecipeRefinementStatus.Running
            )
            {
                return false;
            }

            if (freshState.ModifiedAt != runningModifiedAt)
            {
                await PersistRecipeRefinementStateAsync(
                    recipeId,
                    RecipeRefinementStatus.Failed,
                    "Recipe was edited while refinement was running; please run refinement again.",
                    CancellationToken.None
                );
                return false;
            }

            var preservedCollections = fullRecipe
                .RecipeCollections.Select(c => c.CollectionId)
                .Distinct()
                .ToList();

            var refinedDraft = refineResult.Draft with
            {
                SuggestedCollectionIds = preservedCollections,
                CoverImagePath = freshState.CoverImagePath ?? refineResult.Draft.CoverImagePath,
                ThumbnailImagePath =
                    freshState.ThumbnailImagePath ?? refineResult.Draft.ThumbnailImagePath,
                ExtractedModel = freshState.ExtractedModel ?? refineResult.Draft.ExtractedModel,
                RefinedModel = refineResult.RefinedModel,
            };

            var updatedRecipe = await IngestionCatalogMaterializer.ApplyRefinedDraftToRecipeAsync(
                dbContext,
                recipeService,
                recipeId,
                preservedStatus: freshState.Status,
                preservedExtractedModel: freshState.ExtractedModel,
                refinedModel: refineResult.RefinedModel,
                draft: refinedDraft,
                runCt
            );

            if (updatedRecipe is null)
            {
                return false;
            }

            await IngestionCatalogMaterializer.BacklinkUnresolvedRecipeReferencesAsync(
                dbContext,
                updatedRecipe.Id,
                updatedRecipe.Title,
                runCt
            );

            if (previousFoodItemIds.Count > 0)
            {
                var currentFoodItemIds = updatedRecipe
                    .Ingredients.Where(i => i.FoodItemId.HasValue)
                    .Select(i => i.FoodItemId!.Value)
                    .Concat(
                        updatedRecipe
                            .Ingredients.SelectMany(i => i.Replacements ?? [])
                            .Where(r => r.FoodItemId.HasValue)
                            .Select(r => r.FoodItemId!.Value)
                    )
                    .ToHashSet();

                var detachedFoodItemIds = previousFoodItemIds
                    .Where(id => !currentFoodItemIds.Contains(id))
                    .ToList();

                if (detachedFoodItemIds.Count > 0)
                {
                    var deletedOrphans =
                        await IngestionCatalogMaterializer.CleanupOrphanedFoodItemsAsync(
                            dbContext,
                            detachedFoodItemIds,
                            linkedJob?.Id ?? Guid.Empty,
                            runCt
                        );
                    if (deletedOrphans > 0)
                    {
                        logger.LogInformation(
                            "On-demand refinement of recipe {RecipeId} removed {Count} orphaned food item(s).",
                            recipeId,
                            deletedOrphans
                        );
                    }
                }
            }

            dbContext.ChangeTracker.Clear();
            var finalRecipe = await dbContext.Recipes.FirstOrDefaultAsync(
                r => r.Id == recipeId,
                runCt
            );
            if (finalRecipe is not null)
            {
                finalRecipe.RefinementStatus = RecipeRefinementStatus.None;
                finalRecipe.RefinementRequestedAt = null;
                finalRecipe.RefinementStatusDetail = null;
            }

            if (linkedJob is not null && linkedJob.Status == IngestionJobStatus.Published)
            {
                var trackedLinkedJob = await dbContext.IngestionJobs.FirstOrDefaultAsync(
                    j => j.Id == linkedJob.Id,
                    runCt
                );
                if (trackedLinkedJob is not null)
                {
                    trackedLinkedJob.RefinedModel = refineResult.RefinedModel;
                    trackedLinkedJob.RefinedAt = DateTimeOffset.UtcNow;
                    trackedLinkedJob.RefinementRunNowRequested = false;
                    trackedLinkedJob.RefinementAttemptCount = 0;
                    trackedLinkedJob.RefinementNextRetryAt = null;
                    trackedLinkedJob.ExtractedDraftJson = JsonSerializer.Serialize(
                        refinedDraft,
                        JsonOptions
                    );
                    trackedLinkedJob.ConfidenceScore = refinedDraft.ConfidenceScore;
                    trackedLinkedJob.DisplayTitle = refinedDraft.Title;
                    trackedLinkedJob.StatusDetail = !string.IsNullOrWhiteSpace(
                        trackedLinkedJob.ExtractedModel
                    )
                        ? $"Published ({trackedLinkedJob.ExtractedModel}, refined by {refineResult.RefinedModel})."
                        : $"Published (refined by {refineResult.RefinedModel}).";
                }
            }

            await dbContext.SaveChangesAsync(runCt);

            if (config.EnableIngestion && updatedRecipe.Status == RecipeStatus.Published)
            {
                var publishedRecipeIds = new List<Guid> { updatedRecipe.Id };
                await IngestionCatalogMaterializer.PromoteWaitingRecipeReferenceJobsAsync(
                    dbContext,
                    recipeService,
                    mediaStorageService,
                    globalAutoPublish: config.AutoPublishHighConfidenceText,
                    runCt,
                    promotedRecipeIds: publishedRecipeIds
                );
                await TryEmbedPublishedRecipesAsync(config, publishedRecipeIds, runCt);
            }

            workload?.UpdateMetadata(title: updatedRecipe.Title);
            workload?.CompleteSegment("finalize");
            workload?.CompleteWorkload($"Refined via {refineResult.RefinedModel}.");
            await NotifyQueueChangedSafeAsync("refined", linkedJob?.Id, cancellationToken: runCt);

            return true;
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                await PersistRecipeRefinementStateAsync(
                    recipeId,
                    RecipeRefinementStatus.Queued,
                    "Recovered from interrupted refinement.",
                    CancellationToken.None,
                    preserveRequestedAt: true
                );
            }
            else
            {
                await PersistRecipeRefinementStateAsync(
                    recipeId,
                    RecipeRefinementStatus.None,
                    null,
                    CancellationToken.None
                );
            }

            workload?.CancelWorkload("Recipe refinement canceled.");
            return false;
        }
        catch (LlmHostUnavailableException ex)
        {
            logger.LogWarning(
                ex,
                "Refinement host unavailable or rate-limited during on-demand refinement of recipe {RecipeId}.",
                recipeId
            );
            var msg = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
            await PersistRecipeRefinementStateAsync(
                recipeId,
                RecipeRefinementStatus.Failed,
                msg,
                CancellationToken.None
            );
            workload?.FailWorkload(msg);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "On-demand refinement failed for recipe {RecipeId}.", recipeId);
            var rawMsg = $"Refinement failed: {ex.Message}";
            var msg = rawMsg.Length > 1000 ? rawMsg[..1000] : rawMsg;
            await PersistRecipeRefinementStateAsync(
                recipeId,
                RecipeRefinementStatus.Failed,
                msg,
                CancellationToken.None
            );
            workload?.FailWorkload(msg);
            return false;
        }
    }

    private async Task PersistRecipeRefinementStateAsync(
        Guid recipeId,
        RecipeRefinementStatus status,
        string? statusDetail,
        CancellationToken cancellationToken,
        bool preserveRequestedAt = false
    )
    {
        try
        {
            dbContext.ChangeTracker.Clear();
            var tracked = await dbContext.Recipes.FirstOrDefaultAsync(
                r => r.Id == recipeId,
                cancellationToken
            );
            if (tracked is not null)
            {
                tracked.RefinementStatus = status;
                if (!preserveRequestedAt)
                {
                    tracked.RefinementRequestedAt = null;
                }
                tracked.RefinementStatusDetail = statusDetail;
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to persist refinement status '{Status}' for recipe {RecipeId}.",
                status,
                recipeId
            );
        }
    }

    private async Task<bool> ProcessJobRefinementInternalAsync(
        Guid candidateId,
        bool isManualRunNow,
        CancellationToken cancellationToken
    )
    {
        var config = await llmSettingsService.GetEffectiveConfigAsync(cancellationToken);
        if (!config.HasRefinementModelConfigured)
        {
            return false;
        }

        var refineConfig = config.ForRefinement();
        using var executionLock = await queueSignal.AcquireJobExecutionAsync(
            refineConfig.EffectiveMaxConcurrentRequests,
            cancellationToken
        );

        // Re-verify config and queue state after acquiring execution lock
        config = await llmSettingsService.GetEffectiveConfigAsync(cancellationToken);
        if (!config.HasRefinementModelConfigured)
        {
            return false;
        }

        if (
            !isManualRunNow
            && (
                !config.EnableIngestion
                || !config.RefineDuringIdle
                || await HasActiveOrRunnableIngestionJobsAsync(cancellationToken)
            )
        )
        {
            return false;
        }

        var maxRetries = Math.Max(0, config.MaxIngestionRetries);
        var job = await dbContext.IngestionJobs.FirstOrDefaultAsync(
            j => j.Id == candidateId,
            cancellationToken
        );

        if (
            job is null
            || (
                job.Status != IngestionJobStatus.ReadyForReview
                && job.Status != IngestionJobStatus.Published
            )
            || (!isManualRunNow && job.RefinedAt != null)
            || (isManualRunNow && !job.RefinementRunNowRequested)
            || string.IsNullOrWhiteSpace(job.ExtractedDraftJson)
        )
        {
            return false;
        }

        IngestionExtractedDraftDto? draft;
        try
        {
            draft = JsonSerializer.Deserialize<IngestionExtractedDraftDto>(
                job.ExtractedDraftJson,
                JsonOptions
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Could not deserialize ExtractedDraftJson for refinement of job {JobId}; skipping.",
                job.Id
            );
            job.RefinementRunNowRequested = false;
            job.RefinementAttemptCount = maxRetries + 1;
            if (isManualRunNow)
            {
                job.StatusDetail = "Refinement failed: extracted draft could not be parsed.";
            }
            await dbContext.SaveChangesAsync(cancellationToken);
            return false;
        }

        if (draft is null)
        {
            job.RefinementRunNowRequested = false;
            job.RefinementAttemptCount = maxRetries + 1;
            if (isManualRunNow)
            {
                job.StatusDetail = "Refinement failed: extracted draft was empty.";
            }
            await dbContext.SaveChangesAsync(cancellationToken);
            return false;
        }

        using var activeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var activeRegistration = isManualRunNow
            ? queueSignal.RegisterActiveJobExecution(job.Id, activeCts)
            : queueSignal.RegisterPreemptibleExecution(job.Id, activeCts);
        var jobCt = activeCts.Token;

        var originalDraftJson = job.ExtractedDraftJson;
        var previousStatusDetail = job.StatusDetail;
        var refineModelLabel =
            refineConfig.TextModel?.Trim() ?? config.RefineModel?.Trim() ?? "refinement model";

        using var workload = liveTracker?.BeginWorkload(
            kind: LlmWorkloadKind.JobRefinement,
            title: job.DisplayTitle,
            segments: BuildRefinementSegments("Load Draft", "Save & Materialize"),
            jobId: job.Id,
            recipeId: job.ResultingRecipeId,
            channel: job.Channel,
            subtitle: isManualRunNow ? "Manual refinement" : "Idle refinement",
            thumbnailUrl: job.DownloadedThumbnailImagePath,
            activeModel: refineModelLabel,
            statusDetail: $"Refining recipe via {refineModelLabel}..."
        );

        try
        {
            workload?.ActivateSegment("prepare", "Preparing draft");
            job.RefinementRunNowRequested = false;
            job.StatusDetail = $"Refining recipe via {refineModelLabel}...";
            await dbContext.SaveChangesAsync(jobCt);
            await NotifyQueueChangedSafeAsync("status-changed", job.Id, cancellationToken: jobCt);

            var sourceText = ResolveSourceTextForIdleRefinement(job, draft);

            workload?.CompleteSegment("prepare");
            workload?.ActivateSegment(
                "stage6",
                subStatus: refineModelLabel,
                statusDetail: job.StatusDetail,
                activeModel: refineModelLabel
            );

            var refineResult = await recipeRefiner.RefineDraftAsync(
                new IngestionRefineDraftRequest(
                    SourceText: sourceText,
                    Draft: draft,
                    Config: config,
                    ExcludeRecipeId: job.ResultingRecipeId
                ),
                jobCt
            );

            workload?.CompleteSegment("stage6");
            workload?.ActivateSegment("finalize", "Saving refined recipe");

            // Reload fresh job to detect concurrent edits or discard while LLM call was in flight
            dbContext.ChangeTracker.Clear();
            job = await dbContext.IngestionJobs.FirstOrDefaultAsync(
                j => j.Id == candidateId,
                jobCt
            );

            if (
                job is null
                || (
                    job.Status != IngestionJobStatus.ReadyForReview
                    && job.Status != IngestionJobStatus.Published
                )
                || (!isManualRunNow && job.RefinedAt != null)
                || !string.Equals(
                    job.ExtractedDraftJson,
                    originalDraftJson,
                    StringComparison.Ordinal
                )
            )
            {
                return false;
            }

            var refinedDraft = refineResult.Draft with
            {
                ExtractedModel = job.ExtractedModel ?? refineResult.Draft.ExtractedModel,
                RefinedModel = refineResult.RefinedModel,
            };

            job.RefinedModel = refineResult.RefinedModel;
            job.RefinedAt = DateTimeOffset.UtcNow;
            job.RefinementRunNowRequested = false;
            job.RefinementAttemptCount = 0;
            job.RefinementNextRetryAt = null;

            if (job.Status == IngestionJobStatus.Published)
            {
                var previousFoodItemIds = new HashSet<Guid>();
                Recipe? liveRecipe = null;
                if (job.ResultingRecipeId.HasValue)
                {
                    liveRecipe = await dbContext
                        .Recipes.AsNoTracking()
                        .Include(r => r.Ingredients)
                            .ThenInclude(i => i.Replacements)
                        .Include(r => r.RecipeCollections)
                        .FirstOrDefaultAsync(r => r.Id == job.ResultingRecipeId.Value, jobCt);
                }

                RecipeDetailDto? updatedRecipe = null;
                if (liveRecipe is not null)
                {
                    foreach (var ing in liveRecipe.Ingredients)
                    {
                        if (ing.FoodItemId.HasValue)
                        {
                            previousFoodItemIds.Add(ing.FoodItemId.Value);
                        }
                        foreach (var rep in ing.Replacements)
                        {
                            if (rep.FoodItemId.HasValue)
                            {
                                previousFoodItemIds.Add(rep.FoodItemId.Value);
                            }
                        }
                    }

                    var preservedCollections = liveRecipe
                        .RecipeCollections.Select(c => c.CollectionId)
                        .Distinct()
                        .ToList();
                    refinedDraft = refinedDraft with
                    {
                        SuggestedCollectionIds = preservedCollections,
                        CoverImagePath = liveRecipe.CoverImagePath ?? refinedDraft.CoverImagePath,
                        ThumbnailImagePath =
                            liveRecipe.ThumbnailImagePath ?? refinedDraft.ThumbnailImagePath,
                    };

                    updatedRecipe = await PublishDraftInternalAsync(job, refinedDraft, jobCt);

                    await IngestionCatalogMaterializer.BacklinkUnresolvedRecipeReferencesAsync(
                        dbContext,
                        updatedRecipe.Id,
                        updatedRecipe.Title,
                        jobCt
                    );

                    // Clean up any FoodItems that were detached from this recipe and are now completely orphaned
                    if (previousFoodItemIds.Count > 0)
                    {
                        var currentFoodItemIds = updatedRecipe
                            .Ingredients.Where(i => i.FoodItemId.HasValue)
                            .Select(i => i.FoodItemId!.Value)
                            .Concat(
                                updatedRecipe
                                    .Ingredients.SelectMany(i => i.Replacements ?? [])
                                    .Where(r => r.FoodItemId.HasValue)
                                    .Select(r => r.FoodItemId!.Value)
                            )
                            .ToHashSet();

                        var detachedFoodItemIds = previousFoodItemIds
                            .Where(id => !currentFoodItemIds.Contains(id))
                            .ToList();

                        if (detachedFoodItemIds.Count > 0)
                        {
                            var deletedOrphans =
                                await IngestionCatalogMaterializer.CleanupOrphanedFoodItemsAsync(
                                    dbContext,
                                    detachedFoodItemIds,
                                    job.Id,
                                    jobCt
                                );
                            if (deletedOrphans > 0)
                            {
                                logger.LogInformation(
                                    "Idle refinement of published job {JobId} removed {Count} orphaned food item(s).",
                                    job.Id,
                                    deletedOrphans
                                );
                            }
                        }
                    }

                    // Refresh job entity after recipe update & cleanup saves
                    job =
                        await dbContext.IngestionJobs.FirstOrDefaultAsync(
                            j => j.Id == candidateId,
                            jobCt
                        ) ?? job;
                    job.ResultingRecipeId = updatedRecipe.Id;
                }

                job.RefinedModel = refineResult.RefinedModel;
                job.RefinedAt = DateTimeOffset.UtcNow;
                job.RefinementAttemptCount = 0;
                job.RefinementNextRetryAt = null;
                job.ExtractedDraftJson = JsonSerializer.Serialize(refinedDraft, JsonOptions);
                job.ConfidenceScore = refinedDraft.ConfidenceScore;
                job.DisplayTitle = refinedDraft.Title;
                job.StatusDetail = !string.IsNullOrWhiteSpace(job.ExtractedModel)
                    ? $"Published ({job.ExtractedModel}, refined by {refineResult.RefinedModel})."
                    : $"Published (refined by {refineResult.RefinedModel}).";

                await dbContext.SaveChangesAsync(jobCt);

                if (updatedRecipe is not null && config.EnableIngestion)
                {
                    var publishedRecipeIds = new List<Guid> { updatedRecipe.Id };
                    await IngestionCatalogMaterializer.PromoteWaitingRecipeReferenceJobsAsync(
                        dbContext,
                        recipeService,
                        mediaStorageService,
                        globalAutoPublish: config.AutoPublishHighConfidenceText,
                        jobCt,
                        promotedRecipeIds: publishedRecipeIds
                    );
                    await TryEmbedPublishedRecipesAsync(config, publishedRecipeIds, jobCt);
                }

                workload?.UpdateMetadata(title: job.DisplayTitle);
                workload?.CompleteSegment("finalize");
                workload?.CompleteWorkload(job.StatusDetail);
                await NotifyQueueChangedSafeAsync("refined", job.Id, cancellationToken: jobCt);

                return true;
            }

            // ReadyForReview job: update draft in-place, and auto-publish if now clean and enabled
            var hasUnresolvedRecipeReferences =
                refinedDraft.Ingredients?.Any(i =>
                    i.IsRecipeReference && !i.MatchedRecipeId.HasValue
                ) == true;
            var isPlausible =
                !string.IsNullOrWhiteSpace(refinedDraft.Title)
                && !refinedDraft.Title.Equals("Untitled Recipe", StringComparison.Ordinal)
                && (refinedDraft.Ingredients?.Count ?? 0) >= 1
                && (refinedDraft.Steps?.Count ?? 0) >= 1;

            var canAutoPublish =
                isPlausible
                && (refinedDraft.ValidationIssues?.Count ?? 0) == 0
                && (refinedDraft.QualityWarnings?.Count ?? 0) == 0
                && !hasUnresolvedRecipeReferences
                && (config.AutoPublishHighConfidenceText || job.AutoPublish)
                && (
                    job.Channel == IngestionChannel.WebUrl
                    || job.Channel == IngestionChannel.RawText
                )
                && refinedDraft.ConfidenceScore >= (job.AutoPublish ? 0.60m : 0.85m);

            var publishedSuccessfully = false;
            var remainingIssues = new List<string>(refinedDraft.ValidationIssues ?? []);

            if (canAutoPublish)
            {
                try
                {
                    var createdRecipe = await PublishDraftInternalAsync(job, refinedDraft, jobCt);

                    await IngestionCatalogMaterializer.BacklinkUnresolvedRecipeReferencesAsync(
                        dbContext,
                        createdRecipe.Id,
                        createdRecipe.Title,
                        jobCt
                    );

                    job.Status = IngestionJobStatus.Published;
                    job.ResultingRecipeId = createdRecipe.Id;
                    job.ExtractedDraftJson = JsonSerializer.Serialize(refinedDraft, JsonOptions);
                    job.ConfidenceScore = refinedDraft.ConfidenceScore;
                    job.DisplayTitle = refinedDraft.Title;
                    job.ProcessedAt = DateTimeOffset.UtcNow;
                    job.StatusDetail = !string.IsNullOrWhiteSpace(job.ExtractedModel)
                        ? $"Auto-published after idle refinement ({job.ExtractedModel}, refined by {refineResult.RefinedModel})."
                        : $"Auto-published after idle refinement ({refineResult.RefinedModel}).";
                    publishedSuccessfully = true;
                }
                catch (DomainValidationException ex)
                {
                    logger.LogWarning(
                        ex,
                        "Auto-publish after idle refinement failed validation for job {JobId}; keeping ReadyForReview.",
                        job.Id
                    );
                    var combinedIssues = (refinedDraft.ValidationIssues ?? [])
                        .Append(ex.Message)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    refinedDraft = refinedDraft with
                    {
                        ValidationIssues = combinedIssues,
                        ConfidenceScore = Math.Min(refinedDraft.ConfidenceScore, 0.70m),
                    };
                    remainingIssues = combinedIssues;

                    var preservedModel = job.ExtractedModel;
                    var preservedRefinedModel = job.RefinedModel;
                    var preservedRefinedAt = job.RefinedAt;
                    dbContext.ChangeTracker.Clear();
                    job =
                        await dbContext.IngestionJobs.FirstOrDefaultAsync(
                            j => j.Id == candidateId,
                            jobCt
                        ) ?? job;
                    job.ExtractedModel = preservedModel;
                    job.RefinedModel = preservedRefinedModel;
                    job.RefinedAt = preservedRefinedAt;
                    job.RefinementAttemptCount = 0;
                    job.RefinementNextRetryAt = null;
                }
            }

            if (!publishedSuccessfully)
            {
                job.ExtractedDraftJson = JsonSerializer.Serialize(refinedDraft, JsonOptions);
                job.ConfidenceScore = refinedDraft.ConfidenceScore;
                job.DisplayTitle = refinedDraft.Title;
                job.StatusDetail = !string.IsNullOrWhiteSpace(job.ExtractedModel)
                    ? $"Ready for side-by-side review ({job.ExtractedModel}, refined by {refineResult.RefinedModel})."
                    : $"Ready for side-by-side review (refined by {refineResult.RefinedModel}).";
                if (remainingIssues.Count > 0)
                {
                    var detail =
                        $"{job.StatusDetail} Validation: {string.Join(" ", remainingIssues.Distinct(StringComparer.Ordinal))}";
                    job.StatusDetail = detail[..Math.Min(detail.Length, 2000)];
                }
            }

            await dbContext.SaveChangesAsync(jobCt);

            if (publishedSuccessfully && config.EnableIngestion)
            {
                var publishedRecipeIds = new List<Guid>();
                if (job.ResultingRecipeId.HasValue)
                {
                    publishedRecipeIds.Add(job.ResultingRecipeId.Value);
                }
                await IngestionCatalogMaterializer.PromoteWaitingRecipeReferenceJobsAsync(
                    dbContext,
                    recipeService,
                    mediaStorageService,
                    globalAutoPublish: config.AutoPublishHighConfidenceText,
                    jobCt,
                    promotedRecipeIds: publishedRecipeIds
                );
                await TryEmbedPublishedRecipesAsync(config, publishedRecipeIds, jobCt);
            }

            workload?.UpdateMetadata(title: job.DisplayTitle);
            workload?.CompleteSegment("finalize");
            workload?.CompleteWorkload(job.StatusDetail);
            await NotifyQueueChangedSafeAsync("refined", job.Id, cancellationToken: jobCt);

            return true;
        }
        catch (OperationCanceledException)
        {
            // Preempted by a newly queued import, AI kitchen helper, settings change, or shutdown.
            // Do not fail or increment retry count; restore StatusDetail cleanly.
            try
            {
                dbContext.ChangeTracker.Clear();
                var trackedJob = await dbContext.IngestionJobs.FirstOrDefaultAsync(
                    j => j.Id == candidateId,
                    CancellationToken.None
                );
                if (trackedJob is not null)
                {
                    if (isManualRunNow && cancellationToken.IsCancellationRequested)
                    {
                        trackedJob.RefinementRunNowRequested = true;
                    }
                    else if (
                        trackedJob.StatusDetail?.StartsWith("Refining ", StringComparison.Ordinal)
                        == true
                    )
                    {
                        trackedJob.StatusDetail = previousStatusDetail;
                    }
                    await dbContext.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(
                    ex,
                    "Failed to restore StatusDetail after canceled refinement for job {JobId}.",
                    candidateId
                );
            }

            workload?.CancelWorkload("Refinement preempted or canceled.");
            await NotifyQueueChangedSafeAsync("status-changed", candidateId);
            return false;
        }
        catch (LlmHostUnavailableException ex)
        {
            logger.LogWarning(
                ex,
                "Refinement host unavailable or rate-limited during refinement of job {JobId}.",
                candidateId
            );

            try
            {
                dbContext.ChangeTracker.Clear();
                var trackedJob = await dbContext.IngestionJobs.FirstOrDefaultAsync(
                    j => j.Id == candidateId,
                    CancellationToken.None
                );
                if (trackedJob is not null)
                {
                    trackedJob.RefinementRunNowRequested = false;

                    if (isManualRunNow)
                    {
                        var detail = $"Refinement failed: {ex.Message}";
                        trackedJob.StatusDetail = detail[..Math.Min(detail.Length, 2000)];
                    }
                    else
                    {
                        var safeguardsEnabled = ex.ExhaustedQuotaScope.HasValue
                            ? ex.SafeguardsEnabled
                            : refineConfig.EnableRateLimitSafeguards;

                        if (ex.IsQuotaExhausted && safeguardsEnabled)
                        {
                            var resetTimezone = !string.IsNullOrWhiteSpace(ex.QuotaResetTimezone)
                                ? ex.QuotaResetTimezone
                                : refineConfig.EffectiveQuotaResetTimezone;
                            var resetTime = !string.IsNullOrWhiteSpace(ex.QuotaResetTime)
                                ? ex.QuotaResetTime
                                : refineConfig.EffectiveQuotaResetTime;
                            var nextReset = LlmQuotaSchedule.GetNextResetUtc(
                                DateTimeOffset.UtcNow,
                                resetTimezone,
                                resetTime
                            );
                            trackedJob.RefinementNextRetryAt = nextReset;
                        }
                        else
                        {
                            trackedJob.RefinementAttemptCount++;
                            var delaySeconds = Math.Min(
                                300,
                                15 * (1 << Math.Min(trackedJob.RefinementAttemptCount - 1, 5))
                            );
                            trackedJob.RefinementNextRetryAt = DateTimeOffset.UtcNow.AddSeconds(
                                delaySeconds
                            );
                        }

                        if (
                            trackedJob.StatusDetail?.StartsWith(
                                "Refining ",
                                StringComparison.Ordinal
                            ) == true
                        )
                        {
                            trackedJob.StatusDetail = previousStatusDetail;
                        }
                    }

                    await dbContext.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch (Exception saveEx)
            {
                logger.LogError(
                    saveEx,
                    "Failed to persist refinement backoff for job {JobId}.",
                    candidateId
                );
            }

            workload?.FailWorkload(ex.Message);
            await NotifyQueueChangedSafeAsync("status-changed", candidateId);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Refinement failed for job {JobId}.", candidateId);

            try
            {
                dbContext.ChangeTracker.Clear();
                var trackedJob = await dbContext.IngestionJobs.FirstOrDefaultAsync(
                    j => j.Id == candidateId,
                    CancellationToken.None
                );
                if (trackedJob is not null)
                {
                    trackedJob.RefinementRunNowRequested = false;

                    if (isManualRunNow)
                    {
                        var detail = $"Refinement failed: {ex.Message}";
                        trackedJob.StatusDetail = detail[..Math.Min(detail.Length, 2000)];
                    }
                    else
                    {
                        trackedJob.RefinementAttemptCount++;
                        var delaySeconds = Math.Min(
                            300,
                            15 * (1 << Math.Min(trackedJob.RefinementAttemptCount - 1, 5))
                        );
                        trackedJob.RefinementNextRetryAt = DateTimeOffset.UtcNow.AddSeconds(
                            delaySeconds
                        );
                        if (
                            trackedJob.StatusDetail?.StartsWith(
                                "Refining ",
                                StringComparison.Ordinal
                            ) == true
                        )
                        {
                            trackedJob.StatusDetail = previousStatusDetail;
                        }
                    }

                    await dbContext.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch (Exception saveEx)
            {
                logger.LogError(
                    saveEx,
                    "Failed to persist refinement error state for job {JobId}.",
                    candidateId
                );
            }

            workload?.FailWorkload(ex.Message);
            await NotifyQueueChangedSafeAsync("status-changed", candidateId);
            return false;
        }
    }

    private async Task<bool> HasActiveOrRunnableIngestionJobsAsync(
        CancellationToken cancellationToken
    )
    {
        var now = DateTimeOffset.UtcNow;
        var hasJobs = await dbContext
            .IngestionJobs.AsNoTracking()
            .AnyAsync(
                j =>
                    j.Status == IngestionJobStatus.Preprocessing
                    || j.Status == IngestionJobStatus.Extracting
                    || j.RefinementRunNowRequested
                    || (
                        j.Status == IngestionJobStatus.Queued
                        && (j.RunNowRequested || j.NextRetryAt == null || j.NextRetryAt <= now)
                    ),
                cancellationToken
            );

        if (hasJobs)
        {
            return true;
        }

        return await dbContext
            .Recipes.AsNoTracking()
            .AnyAsync(
                r =>
                    r.RefinementStatus == RecipeRefinementStatus.Queued
                    || r.RefinementStatus == RecipeRefinementStatus.Running,
                cancellationToken
            );
    }

    public static IngestionExtractedDraftDto BuildDraftFromRecipe(Recipe recipe)
    {
        var ingredients = recipe
            .Ingredients.OrderBy(i => i.SortOrder)
            .Select(ing =>
            {
                var isRecipeRef =
                    ing.LinkedRecipeId.HasValue
                    || !string.IsNullOrWhiteSpace(ing.UnresolvedRecipeTitle);
                var isNewFood = !isRecipeRef && !ing.FoodItemId.HasValue;
                var validFoodName = isNewFood && FoodNameSanitizer.IsValidFoodName(ing.DisplayName);

                var replacements =
                    ing.Replacements.Count > 0
                        ? ing
                            .Replacements.OrderBy(r => r.SortOrder)
                            .Select(rep =>
                            {
                                var repIsNew = !rep.FoodItemId.HasValue;
                                var repValidName =
                                    repIsNew && FoodNameSanitizer.IsValidFoodName(rep.DisplayName);
                                return new IngestionDraftReplacementDto(
                                    DisplayName: rep.DisplayName,
                                    Quantity: rep.Quantity,
                                    MaxQuantity: rep.MaxQuantity,
                                    Unit: rep.Unit,
                                    CustomUnitName: rep.CustomUnitName,
                                    PreparationNote: rep.PreparationNote,
                                    MatchedFoodItemId: rep.FoodItemId,
                                    MatchedFoodItemName: rep.FoodItem?.Name,
                                    IsNewFoodItem: repIsNew,
                                    CreateNewFoodItem: repValidName,
                                    ProposedFoodItemName: repValidName ? rep.DisplayName : null,
                                    SuggestedCategory: rep.FoodItem?.Category,
                                    SuggestedIsPantryStaple: rep.FoodItem?.IsPantryStaple ?? false
                                );
                            })
                            .ToList()
                        : null;

                return new IngestionDraftIngredientDto(
                    SectionName: ing.SectionName,
                    Quantity: ing.Quantity,
                    MaxQuantity: ing.MaxQuantity,
                    Unit: ing.Unit,
                    CustomUnitName: ing.CustomUnitName,
                    MatchedFoodItemId: isRecipeRef ? null : ing.FoodItemId,
                    MatchedFoodItemName: isRecipeRef ? null : ing.FoodItem?.Name,
                    IsNewFoodItem: isNewFood,
                    ProposedFoodItemName: validFoodName ? ing.DisplayName : null,
                    SuggestedIsPantryStaple: ing.FoodItem?.IsPantryStaple ?? false,
                    CreateNewFoodItem: validFoodName,
                    DisplayName: ing.DisplayName,
                    PreparationNote: ing.PreparationNote,
                    IsOptional: ing.IsOptional,
                    OriginalRawText: ing.OriginalRawText,
                    AlternativeMeasurement: ing.AlternativeQuantity.HasValue
                    && ing.AlternativeUnit.HasValue
                        ? new RecipeMeasurement(
                            ing.AlternativeQuantity,
                            ing.AlternativeUnit,
                            ing.AlternativeMaxQuantity,
                            ing.AlternativeCustomUnitName
                        )
                        : null,
                    PreferAlternativeMeasurement: ing.PreferAlternativeMeasurement,
                    SuggestedCategory: ing.FoodItem?.Category,
                    IsRecipeReference: isRecipeRef,
                    ReferencedRecipeTitle: isRecipeRef
                        ? (ing.LinkedRecipe?.Title ?? ing.UnresolvedRecipeTitle)
                        : null,
                    MatchedRecipeId: ing.LinkedRecipeId,
                    MatchedRecipeTitle: ing.LinkedRecipe?.Title,
                    MatchedRecipeSlug: ing.LinkedRecipe?.Slug,
                    Replacements: replacements
                );
            })
            .ToList();

        var steps = recipe
            .Steps.OrderBy(s => s.StepNumber)
            .Select(s => new IngestionDraftStepDto(
                SectionName: s.SectionName,
                Instruction: s.Instruction,
                Timers: s.Timers.OrderBy(t => t.SortOrder)
                    .Select(t => new RecipeStepTimerInput(t.DurationSeconds, t.Label))
                    .ToList(),
                ImagePath: s.ImagePath
            ))
            .ToList();

        return new IngestionExtractedDraftDto(
            Title: recipe.Title,
            Description: recipe.Description,
            BaseServings: recipe.BaseServings,
            YieldDisplay: recipe.YieldDisplay,
            PrepTimeMinutes: recipe.PrepTimeMinutes,
            CookTimeMinutes: recipe.CookTimeMinutes,
            TotalTimeMinutes: recipe.TotalTimeMinutes,
            Difficulty: recipe.Difficulty,
            SourceUrl: recipe.SourceUrl,
            SourceName: recipe.SourceName,
            SourceAuthor: recipe.SourceAuthor,
            CoverImagePath: recipe.CoverImagePath,
            ThumbnailImagePath: recipe.ThumbnailImagePath,
            SuggestedCollectionIds: recipe
                .RecipeCollections.Select(c => c.CollectionId)
                .Distinct()
                .ToList(),
            SuggestedTagIds: recipe.RecipeTags.Select(t => t.TagId).Distinct().ToList(),
            Ingredients: ingredients,
            Steps: steps,
            ConfidenceScore: 0.95m,
            SourceBookTitle: recipe.SourceBookTitle,
            SourceBookPage: recipe.SourceBookPage,
            RetainSourceAttachments: true,
            ExtractedModel: recipe.ExtractedModel,
            RefinedModel: recipe.RefinedModel
        );
    }

    private static string ResolveSourceTextForRecipeRefinement(
        IngestionJob? linkedJob,
        IngestionExtractedDraftDto draft
    ) =>
        linkedJob is not null
            ? ResolveSourceTextForIdleRefinement(linkedJob, draft)
            : BuildFallbackSourceTextFromDraft(draft);

    private static string ResolveSourceTextForIdleRefinement(
        IngestionJob job,
        IngestionExtractedDraftDto draft
    )
    {
        if (!string.IsNullOrWhiteSpace(job.NormalizedSourceText))
        {
            return job.NormalizedSourceText;
        }

        if (!string.IsNullOrWhiteSpace(job.RawText))
        {
            return job.RawText;
        }

        if (!string.IsNullOrWhiteSpace(job.ScrapedPageJson))
        {
            try
            {
                var payload = JsonSerializer.Deserialize<ScrapedWebRecipePayload>(
                    job.ScrapedPageJson,
                    JsonOptions
                );
                if (payload is not null)
                {
                    var text = BuildNormalizedWebText(payload);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }
            catch
            {
                // Fall back to reconstructed draft text
            }
        }

        return BuildFallbackSourceTextFromDraft(draft);
    }

    public static string BuildFallbackSourceTextFromDraft(IngestionExtractedDraftDto draft)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(draft.Title);
        if (!string.IsNullOrWhiteSpace(draft.Description))
        {
            sb.AppendLine(draft.Description);
        }

        if (!string.IsNullOrWhiteSpace(draft.YieldDisplay))
        {
            sb.AppendLine($"Yield: {draft.YieldDisplay}");
        }
        else if (draft.BaseServings > 0)
        {
            sb.AppendLine($"Servings: {draft.BaseServings:0.##}");
        }

        if (draft.PrepTimeMinutes.HasValue)
        {
            sb.AppendLine($"Prep Time: {draft.PrepTimeMinutes} minutes");
        }
        if (draft.CookTimeMinutes.HasValue)
        {
            sb.AppendLine($"Cook Time: {draft.CookTimeMinutes} minutes");
        }
        if (draft.TotalTimeMinutes.HasValue)
        {
            sb.AppendLine($"Total Time: {draft.TotalTimeMinutes} minutes");
        }

        sb.AppendLine();
        sb.AppendLine("Ingredients:");
        string? currentIngSection = null;
        foreach (var ing in draft.Ingredients ?? [])
        {
            var sec = string.IsNullOrWhiteSpace(ing.SectionName) ? null : ing.SectionName.Trim();
            if (
                sec is not null
                && !string.Equals(sec, currentIngSection, StringComparison.OrdinalIgnoreCase)
            )
            {
                sb.AppendLine($"{sec}:");
                currentIngSection = sec;
            }

            if (!string.IsNullOrWhiteSpace(ing.OriginalRawText))
            {
                sb.AppendLine($"- {ing.OriginalRawText}");
            }
            else
            {
                sb.AppendLine($"- {FormatDraftIngredientLine(ing)}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Instructions:");
        string? currentStepSection = null;
        var stepNum = 1;
        foreach (var step in draft.Steps ?? [])
        {
            var sec = string.IsNullOrWhiteSpace(step.SectionName) ? null : step.SectionName.Trim();
            if (
                sec is not null
                && !string.Equals(sec, currentStepSection, StringComparison.OrdinalIgnoreCase)
            )
            {
                sb.AppendLine($"{sec}:");
                currentStepSection = sec;
            }

            sb.AppendLine($"{stepNum++}. {StepInstructionParser.ToPlainText(step.Instruction)}");
        }
        return sb.ToString();
    }

    private static string FormatDraftIngredientLine(IngestionDraftIngredientDto ing)
    {
        var parts = new List<string>();
        var measure = FormatMeasurementText(
            ing.Quantity,
            ing.MaxQuantity,
            ing.Unit,
            ing.CustomUnitName
        );
        if (!string.IsNullOrWhiteSpace(measure))
        {
            parts.Add(measure);
        }

        if (ing.AlternativeMeasurement is { } alt)
        {
            var altMeasure = FormatMeasurementText(
                alt.Quantity,
                alt.MaxQuantity,
                alt.Unit,
                alt.CustomUnitName
            );
            if (!string.IsNullOrWhiteSpace(altMeasure))
            {
                parts.Add($"({altMeasure})");
            }
        }

        if (!string.IsNullOrWhiteSpace(ing.DisplayName))
        {
            parts.Add(ing.DisplayName.Trim());
        }

        var line = string.Join(' ', parts);
        if (!string.IsNullOrWhiteSpace(ing.PreparationNote))
        {
            line = $"{line}, {ing.PreparationNote.Trim()}";
        }
        if (ing.IsOptional)
        {
            line = $"{line} (optional)";
        }

        if (ing.Replacements is { Count: > 0 })
        {
            foreach (var rep in ing.Replacements)
            {
                var repParts = new List<string>();
                var repMeasure = FormatMeasurementText(
                    rep.Quantity,
                    rep.MaxQuantity,
                    rep.Unit,
                    rep.CustomUnitName
                );
                if (!string.IsNullOrWhiteSpace(repMeasure))
                {
                    repParts.Add(repMeasure);
                }
                if (!string.IsNullOrWhiteSpace(rep.DisplayName))
                {
                    repParts.Add(rep.DisplayName.Trim());
                }
                var repText = string.Join(' ', repParts);
                if (!string.IsNullOrWhiteSpace(rep.PreparationNote))
                {
                    repText = $"{repText}, {rep.PreparationNote.Trim()}";
                }
                if (!string.IsNullOrWhiteSpace(repText))
                {
                    line = $"{line} or {repText}";
                }
            }
        }

        return line;
    }

    private static string FormatMeasurementText(
        decimal? quantity,
        decimal? maxQuantity,
        MeasurementUnit? unit,
        string? customUnitName
    )
    {
        var qtyStr = quantity.HasValue
            ? (
                maxQuantity.HasValue && maxQuantity.Value > quantity.Value
                    ? $"{quantity.Value:0.##}-{maxQuantity.Value:0.##}"
                    : $"{quantity.Value:0.##}"
            )
            : string.Empty;

        var unitStr = unit switch
        {
            null or MeasurementUnit.Whole => string.Empty,
            MeasurementUnit.Custom => customUnitName?.Trim() ?? string.Empty,
            MeasurementUnit.Tsp => "tsp",
            MeasurementUnit.Tbsp => "tbsp",
            MeasurementUnit.FlOz => "fl oz",
            MeasurementUnit.Cup => "cup",
            MeasurementUnit.Pt => "pt",
            MeasurementUnit.Qt => "qt",
            MeasurementUnit.Gal => "gal",
            MeasurementUnit.Ml => "ml",
            MeasurementUnit.L => "l",
            MeasurementUnit.Oz => "oz",
            MeasurementUnit.Lb => "lb",
            MeasurementUnit.G => "g",
            MeasurementUnit.Kg => "kg",
            _ => unit.Value.ToString().ToLowerInvariant(),
        };

        return string.Join(
            ' ',
            new[] { qtyStr, unitStr }.Where(s => !string.IsNullOrWhiteSpace(s))
        );
    }

    public async Task<int> SyncPendingRecipeEmbeddingsAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (recipeEmbeddingService is null)
        {
            return 0;
        }

        var config = await llmSettingsService.GetEffectiveConfigAsync(cancellationToken);
        if (!config.EnableIngestion || config.ForEmbedding() is null)
        {
            return 0;
        }

        return await recipeEmbeddingService.SyncPendingEmbeddingsAsync(
            config,
            maxBatchCount: 64,
            cancellationToken
        );
    }

    private async Task TryEmbedPublishedRecipesAsync(
        EffectiveLlmConnectionConfig config,
        IReadOnlyCollection<Guid> recipeIds,
        CancellationToken cancellationToken
    )
    {
        if (recipeEmbeddingService is null || recipeIds.Count == 0)
        {
            return;
        }

        var embConfig = config.ForEmbedding();
        if (embConfig is null || string.IsNullOrWhiteSpace(embConfig.TextModel))
        {
            return;
        }

        try
        {
            await recipeEmbeddingService.UpsertRecipeEmbeddingsAsync(
                embConfig,
                recipeIds,
                cancellationToken
            );
        }
        catch (Exception ex)
            when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning(
                ex,
                "Post-publish recipe embedding failed for {Count} recipe(s) on model '{Model}'; background sync will retry later.",
                recipeIds.Count,
                embConfig.TextModel
            );
        }
    }

    private Task<RecipeDetailDto> PublishDraftInternalAsync(
        IngestionJob job,
        IngestionExtractedDraftDto draft,
        CancellationToken cancellationToken
    ) =>
        IngestionCatalogMaterializer.PublishDraftAsync(
            dbContext,
            recipeService,
            mediaStorageService,
            job,
            draft,
            cancellationToken
        );

    public static string BuildNormalizedWebText(ScrapedWebRecipePayload payload)
    {
        var sb = new System.Text.StringBuilder();

        if (!string.IsNullOrWhiteSpace(payload.PageTitle))
            sb.AppendLine($"Title: {payload.PageTitle}");

        if (!string.IsNullOrWhiteSpace(payload.SiteName))
            sb.AppendLine($"Site: {payload.SiteName}");

        if (!string.IsNullOrWhiteSpace(payload.Author))
            sb.AppendLine($"Author: {payload.Author}");

        if (!string.IsNullOrWhiteSpace(payload.Description))
            sb.AppendLine($"Description: {payload.Description}");

        if (!string.IsNullOrWhiteSpace(payload.RecipeYieldText))
            sb.AppendLine($"Yield: {payload.RecipeYieldText}");

        if (payload.PrepTimeMinutes.HasValue)
            sb.AppendLine($"Prep Time: {payload.PrepTimeMinutes} minutes");

        if (payload.CookTimeMinutes.HasValue)
            sb.AppendLine($"Cook Time: {payload.CookTimeMinutes} minutes");

        if (payload.TotalTimeMinutes.HasValue)
            sb.AppendLine($"Total Time: {payload.TotalTimeMinutes} minutes");

        if (payload.RawIngredientLines.Count > 0)
        {
            sb.AppendLine();

            sb.AppendLine("Ingredients:");

            foreach (var ing in payload.RawIngredientLines)
            {
                sb.AppendLine($"- {ing}");
            }
        }

        if (payload.RawInstructionLines.Count > 0)
        {
            sb.AppendLine();

            sb.AppendLine("Instructions:");

            for (var i = 0; i < payload.RawInstructionLines.Count; i++)
            {
                sb.AppendLine($"{i + 1}. {payload.RawInstructionLines[i]}");
            }
        }

        var hasCompleteStructuredRecipe =
            payload.HadRecipeJsonLd
            && payload.RawIngredientLines.Count > 0
            && payload.RawInstructionLines.Count > 0;

        if (!hasCompleteStructuredRecipe && !string.IsNullOrWhiteSpace(payload.CleanedBodyText))
        {
            sb.AppendLine();

            sb.AppendLine("Cleaned Page Text:");

            sb.AppendLine(payload.CleanedBodyText);
        }

        return sb.ToString();
    }

    internal static IReadOnlyList<string> FilterBenignSubRecipeQualityWarnings(
        IReadOnlyList<string>? qualityWarnings,
        IReadOnlyList<IngestionDraftIngredientDto>? ingredients
    )
    {
        if (qualityWarnings is null or { Count: 0 })
        {
            return [];
        }

        var refs = (ingredients ?? []).Where(i => i.IsRecipeReference).ToList();
        var allReferencedSubRecipesResolved =
            refs.Count > 0 && refs.All(i => i.MatchedRecipeId.HasValue);
        if (!allReferencedSubRecipesResolved)
        {
            return qualityWarnings;
        }

        return qualityWarnings
            .Where(w =>
                !System.Text.RegularExpressions.Regex.IsMatch(
                    w,
                    @"(?:relies\s+on|requires|uses|references?)\s+(?:a\s+|the\s+)?(?:referenced\s+)?sub[- ]?recipe|sub[- ]?recipe\b.*\b(?:not\s+provided|missing|omitted|unavailable|incomplete)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                        | System.Text.RegularExpressions.RegexOptions.CultureInvariant
                )
            )
            .ToList();
    }

    private static IReadOnlyList<LlmWorkflowSegmentDto> BuildIngestionSegments(
        bool includeVision,
        bool includeStage6,
        bool preprocessCompleted
    )
    {
        var segments = new List<LlmWorkflowSegmentDto>
        {
            new(
                "preprocess",
                "Preprocess",
                preprocessCompleted
                    ? LlmWorkflowSegmentState.Completed
                    : LlmWorkflowSegmentState.Pending
            ),
        };

        if (includeVision)
        {
            segments.Add(new("vision", "Vision OCR", LlmWorkflowSegmentState.Pending));
        }

        segments.Add(new("stage1", "1. Extract", LlmWorkflowSegmentState.Pending));
        segments.Add(new("stage2", "2. Condense", LlmWorkflowSegmentState.Pending));
        segments.Add(new("stage3", "3. Match", LlmWorkflowSegmentState.Pending));
        segments.Add(new("stage4", "4. Tokenize", LlmWorkflowSegmentState.Pending));
        segments.Add(new("stage5", "5. Polish", LlmWorkflowSegmentState.Pending));

        if (includeStage6)
        {
            segments.Add(new("stage6", "6. Refine", LlmWorkflowSegmentState.Pending));
        }

        segments.Add(new("finalize", "Save", LlmWorkflowSegmentState.Pending));
        return segments;
    }

    private static IReadOnlyList<LlmWorkflowSegmentDto> BuildRefinementSegments(
        string prepareLabel,
        string finalizeLabel
    ) =>
        [
            new("prepare", prepareLabel, LlmWorkflowSegmentState.Pending),
            new("stage6", "Refine Recipe", LlmWorkflowSegmentState.Pending),
            new("finalize", finalizeLabel, LlmWorkflowSegmentState.Pending),
        ];

    private async Task NotifyQueueChangedSafeAsync(
        string reason,
        Guid? jobId = null,
        bool? enableIngestion = null,
        DateTimeOffset? pausedUntilUtc = null,
        string? pauseReason = null,
        CancellationToken cancellationToken = default
    )
    {
        if (realtimeNotifier is null)
        {
            return;
        }

        try
        {
            var counts = await dbContext
                .IngestionJobs.AsNoTracking()
                .Where(j => j.Status != IngestionJobStatus.Discarded)
                .GroupBy(j => j.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken);

            var ready = 0;
            var active = 0;
            var waiting = 0;
            var failed = 0;
            var published = 0;
            var all = 0;

            foreach (var row in counts)
            {
                all += row.Count;
                switch (row.Status)
                {
                    case IngestionJobStatus.ReadyForReview:
                        ready += row.Count;
                        break;
                    case IngestionJobStatus.Queued:
                    case IngestionJobStatus.Preprocessing:
                    case IngestionJobStatus.Extracting:
                        active += row.Count;
                        break;
                    case IngestionJobStatus.WaitingForLlmHost:
                        waiting += row.Count;
                        break;
                    case IngestionJobStatus.Failed:
                        failed += row.Count;
                        break;
                    case IngestionJobStatus.Published:
                        published += row.Count;
                        break;
                }
            }

            var summary = new IngestionQueueSummaryDto(
                ReadyForReviewCount: ready,
                ActiveProcessingCount: active,
                WaitingForLlmHostCount: waiting,
                FailedCount: failed,
                AllCount: all,
                PublishedCount: published
            );

            await realtimeNotifier.NotifyQueueChangedAsync(
                new IngestionQueueChangedEventDto(
                    Reason: reason,
                    JobId: jobId,
                    Summary: summary,
                    EnableIngestion: enableIngestion,
                    PausedUntilUtc: pausedUntilUtc,
                    PauseReason: pauseReason
                ),
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Non-fatal error broadcasting ingestion queue update.");
        }
    }
}

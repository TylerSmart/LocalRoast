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
    IWebRecipeScraper scraper,
    IRecipeService recipeService,
    IMediaStorageService mediaStorageService,
    IPdfIngestionInspector pdfInspector,
    IIngestionQueueSignal queueSignal,
    ILogger<IngestionProcessor> logger
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
        var activeId = queueSignal.ActiveJobId;

        var interrupted = await dbContext
            .IngestionJobs.Where(j =>
                (
                    j.Status == IngestionJobStatus.Preprocessing
                    || j.Status == IngestionJobStatus.Extracting
                ) && (activeId == null || j.Id != activeId)
            )
            .ToListAsync(cancellationToken);

        if (interrupted.Count == 0)
        {
            return 0;
        }

        foreach (var job in interrupted)
        {
            job.Status = IngestionJobStatus.Queued;

            job.NextRetryAt = null;

            job.StatusDetail = "Recovered from interrupted processing.";
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Recovered {Count} interrupted ingestion job(s) back to Queued state.",
            interrupted.Count
        );

        return interrupted.Count;
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
            initialConfig.MaxConcurrentRequests,
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

        try
        {
            // Step 1: Preprocessing

            job.Status = IngestionJobStatus.Preprocessing;

            job.StatusDetail = "Preprocessing source content...";

            // A run-now request covers this attempt only; later retries follow the pause setting.
            job.RunNowRequested = false;

            await dbContext.SaveChangesAsync(jobCt);

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
                return false;
            }

            // Step 2: Extracting

            job.Status = IngestionJobStatus.Extracting;

            job.StatusDetail = "Extracting structured recipe via local LLM...";

            await dbContext.SaveChangesAsync(jobCt);

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
                sourceText =
                    await llmClient.TranscribeRecipeImagesAsync(config, visionContext, jobCt)
                    ?? throw new DomainValidationException(
                        "No recipe text could be read from the images."
                    );
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
                var warningCount = filteredWarnings.Count;
                draft = resolvedDraft with
                {
                    QualityWarnings = filteredWarnings,
                    ValidationIssues = distinctIssues,
                    ConfidenceScore = Math.Clamp(
                        0.96m - (distinctIssues.Count * 0.15m) - (warningCount * 0.10m),
                        0.10m,
                        0.99m
                    ),
                };
            }

            if (allValidationIssues.Count > 0)
            {
                logger.LogWarning(
                    "Recipe extraction for job {JobId} requires review: {Issues}",
                    job.Id,
                    string.Join(" ", allValidationIssues)
                );
            }

            job.ExtractedModel = modelUsed;

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

            // Stage 5: Auto-Publish vs ReadyForReview

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

                    job.StatusDetail = $"Auto-published high-confidence extraction ({modelUsed}).";
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
                    dbContext.ChangeTracker.Clear();
                    job =
                        await dbContext.IngestionJobs.FirstOrDefaultAsync(
                            j => j.Id == job.Id,
                            jobCt
                        ) ?? job;
                    job.ExtractedModel = modelUsed;
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

                job.StatusDetail = $"Ready for side-by-side review ({modelUsed}).";
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
                await IngestionCatalogMaterializer.PromoteWaitingRecipeReferenceJobsAsync(
                    dbContext,
                    recipeService,
                    mediaStorageService,
                    globalAutoPublish: config.AutoPublishHighConfidenceText,
                    jobCt
                );
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            if (await IsJobDiscardedInDbAsync(job.Id, CancellationToken.None))
            {
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

            return false;
        }
        catch (LlmHostUnavailableException ex)
        {
            if (await IsJobDiscardedInDbAsync(job.Id, CancellationToken.None))
            {
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
                    if (ex.IsQuotaExhausted && initialConfig.EnableRateLimitSafeguards)
                    {
                        var nextReset = initialConfig.AutoResumeQueue
                            ? LlmQuotaSchedule.GetNextResetUtc(
                                DateTimeOffset.UtcNow,
                                initialConfig.DailyQuotaResetTimezone,
                                initialConfig.DailyQuotaResetTime
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
                        var tzLabel = !string.IsNullOrWhiteSpace(
                            initialConfig.DailyQuotaResetTimezone
                        )
                            ? $" ({initialConfig.DailyQuotaResetTimezone})"
                            : string.Empty;
                        trackedJob.StatusDetail = nextReset.HasValue
                            ? $"Paused: {reason}. Ingestion will automatically resume at {nextReset.Value:u}{tzLabel}."
                            : $"Paused: {reason}. Ingestion was auto-paused until manually resumed.";
#if false
                        await llmSettingsService.SetIngestionEnabledAsync(
                            false,
                            CancellationToken.None
                        );
                        trackedJob.Status = IngestionJobStatus.Queued;
                        trackedJob.NextRetryAt = null;
                        trackedJob.StatusDetail =
                            $"Paused: Provider API quota exhausted ({ex.Message}). Ingestion was auto-paused to prevent repeated requests until quota resets.";
#endif
                        await dbContext.SaveChangesAsync(CancellationToken.None);
                        logger.LogWarning(
                            "Auto-paused ingestion for job {JobId} because provider quota is exhausted.",
                            job.Id
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

            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (await IsJobDiscardedInDbAsync(job.Id, CancellationToken.None))
            {
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
}

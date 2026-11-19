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

        if (interrupted.Count == 0 && interruptedRefining.Count == 0)
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

        await dbContext.SaveChangesAsync(cancellationToken);

        if (interrupted.Count > 0)
        {
            logger.LogInformation(
                "Recovered {Count} interrupted ingestion job(s) back to Queued state.",
                interrupted.Count
            );
        }

        return interrupted.Count + interruptedRefining.Count;
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
                }
                catch (LlmHostUnavailableException ex)
                    when (ex.IsQuotaExhausted
                        && !IngestionRecipePipeline.QuotaExhaustionAffectsPrimaryTextWorkflow(
                            ex,
                            config
                        )
                    )
                {
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

        var refineConfig = config.ForRefinement();
        using var executionLock = await queueSignal.AcquireJobExecutionAsync(
            refineConfig.EffectiveMaxConcurrentRequests,
            cancellationToken
        );

        // Re-verify config and queue state after acquiring execution lock
        config = await llmSettingsService.GetEffectiveConfigAsync(cancellationToken);
        if (
            !config.EnableIngestion
            || !config.HasRefinementModelConfigured
            || !config.RefineDuringIdle
            || await HasActiveOrRunnableIngestionJobsAsync(cancellationToken)
        )
        {
            return false;
        }

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
            || job.RefinedAt != null
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
                "Could not deserialize ExtractedDraftJson for idle refinement of job {JobId}; skipping.",
                job.Id
            );
            job.RefinementAttemptCount = maxRetries + 1;
            await dbContext.SaveChangesAsync(cancellationToken);
            return false;
        }

        if (draft is null)
        {
            job.RefinementAttemptCount = maxRetries + 1;
            await dbContext.SaveChangesAsync(cancellationToken);
            return false;
        }

        using var activeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var activeRegistration = queueSignal.RegisterPreemptibleExecution(job.Id, activeCts);
        var jobCt = activeCts.Token;

        var originalDraftJson = job.ExtractedDraftJson;
        var previousStatusDetail = job.StatusDetail;
        var refineModelLabel =
            refineConfig.TextModel?.Trim() ?? config.RefineModel?.Trim() ?? "refinement model";

        try
        {
            job.StatusDetail = $"Refining recipe via {refineModelLabel}...";
            await dbContext.SaveChangesAsync(jobCt);

            var sourceText = ResolveSourceTextForIdleRefinement(job, draft);

            var refineResult = await recipeRefiner.RefineDraftAsync(
                new IngestionRefineDraftRequest(
                    SourceText: sourceText,
                    Draft: draft,
                    Config: config,
                    ExcludeRecipeId: job.ResultingRecipeId
                ),
                jobCt
            );

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
                || job.RefinedAt != null
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

                if (updatedRecipe is not null)
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
            // Preempted by a newly queued import, AI kitchen helper, settings change, or shutdown.
            // Do not fail or increment retry count; restore StatusDetail cleanly.
            try
            {
                dbContext.ChangeTracker.Clear();
                var trackedJob = await dbContext.IngestionJobs.FirstOrDefaultAsync(
                    j => j.Id == candidateId,
                    CancellationToken.None
                );
                if (
                    trackedJob is not null
                    && trackedJob.StatusDetail?.StartsWith("Refining ", StringComparison.Ordinal)
                        == true
                )
                {
                    trackedJob.StatusDetail = previousStatusDetail;
                    await dbContext.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(
                    ex,
                    "Failed to restore StatusDetail after preempted idle refinement for job {JobId}.",
                    candidateId
                );
            }

            return false;
        }
        catch (LlmHostUnavailableException ex)
        {
            logger.LogWarning(
                ex,
                "Refinement host unavailable or rate-limited during idle refinement of job {JobId}.",
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
                        trackedJob.StatusDetail?.StartsWith("Refining ", StringComparison.Ordinal)
                        == true
                    )
                    {
                        trackedJob.StatusDetail = previousStatusDetail;
                    }
                    await dbContext.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch (Exception saveEx)
            {
                logger.LogError(
                    saveEx,
                    "Failed to persist idle refinement backoff for job {JobId}.",
                    candidateId
                );
            }

            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Idle refinement failed for job {JobId}; scheduling backoff without changing job status.",
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
                    trackedJob.RefinementAttemptCount++;
                    var delaySeconds = Math.Min(
                        300,
                        15 * (1 << Math.Min(trackedJob.RefinementAttemptCount - 1, 5))
                    );
                    trackedJob.RefinementNextRetryAt = DateTimeOffset.UtcNow.AddSeconds(
                        delaySeconds
                    );
                    if (
                        trackedJob.StatusDetail?.StartsWith("Refining ", StringComparison.Ordinal)
                        == true
                    )
                    {
                        trackedJob.StatusDetail = previousStatusDetail;
                    }
                    await dbContext.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch (Exception saveEx)
            {
                logger.LogError(
                    saveEx,
                    "Failed to persist idle refinement error backoff for job {JobId}.",
                    candidateId
                );
            }

            return false;
        }
    }

    private Task<bool> HasActiveOrRunnableIngestionJobsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return dbContext
            .IngestionJobs.AsNoTracking()
            .AnyAsync(
                j =>
                    j.Status == IngestionJobStatus.Preprocessing
                    || j.Status == IngestionJobStatus.Extracting
                    || (
                        j.Status == IngestionJobStatus.Queued
                        && (j.RunNowRequested || j.NextRetryAt == null || j.NextRetryAt <= now)
                    ),
                cancellationToken
            );
    }

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

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(draft.Title);
        if (!string.IsNullOrWhiteSpace(draft.Description))
        {
            sb.AppendLine(draft.Description);
        }
        sb.AppendLine();
        sb.AppendLine("Ingredients:");
        foreach (var ing in draft.Ingredients ?? [])
        {
            sb.AppendLine(
                !string.IsNullOrWhiteSpace(ing.OriginalRawText)
                    ? $"- {ing.OriginalRawText}"
                    : $"- {ing.DisplayName}"
            );
        }
        sb.AppendLine();
        sb.AppendLine("Instructions:");
        var stepNum = 1;
        foreach (var step in draft.Steps ?? [])
        {
            sb.AppendLine($"{stepNum++}. {StepInstructionParser.ToPlainText(step.Instruction)}");
        }
        return sb.ToString();
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

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

        var interruptedRefinements = await dbContext
            .IngestionDraftRefinements.Where(item =>
                item.Status == IngestionRefinementStatus.Running
                && (activeId == null || item.IngestionJobId != activeId)
            )
            .ToListAsync(cancellationToken);
        foreach (var refinement in interruptedRefinements)
        {
            refinement.Status = IngestionRefinementStatus.Pending;
            refinement.Detail = "Resuming after an interrupted refine pass.";
        }

        if (interrupted.Count == 0)
        {
            if (interruptedRefinements.Count > 0)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
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

            // A refine pass of an earlier draft no longer applies once the job is reprocessed.
            var staleRefinements = await dbContext
                .IngestionDraftRefinements.Where(item => item.IngestionJobId == job.Id)
                .ToListAsync(jobCt);
            dbContext.IngestionDraftRefinements.RemoveRange(staleRefinements);

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

            var tags = await dbContext
                .Tags.AsNoTracking()
                .Select(t => new CatalogTagLookupItem(t.Id, t.Name, t.NormalizedName, t.Slug))
                .ToListAsync(jobCt);

            var catalogFoods = await dbContext
                .FoodItems.AsNoTracking()
                .Select(f => new CatalogFoodLookupItem(
                    f.Id,
                    f.Name,
                    f.NormalizedName,
                    f.IsPantryStaple,
                    f.Aliases.Select(a => a.NormalizedAlias).ToList(),
                    f.Category
                ))
                .ToListAsync(jobCt);

            var tagNames = tags.Select(t => t.Name).ToList();

            var availableFoodNames = await dbContext
                .FoodItems.AsNoTracking()
                .OrderByDescending(f => f.RecipeIngredients.Count)
                .ThenBy(f => f.Name)
                .Select(f => f.Name)
                .Take(500)
                .ToListAsync(jobCt);

            var pendingDraftJsons = await dbContext
                .IngestionJobs.AsNoTracking()
                .Where(j =>
                    j.Status == IngestionJobStatus.ReadyForReview && j.ExtractedDraftJson != null
                )
                .OrderByDescending(j => j.ProcessedAt)
                .Take(100)
                .Select(j => j.ExtractedDraftJson!)
                .ToListAsync(jobCt);

            foreach (var dj in pendingDraftJsons)
            {
                try
                {
                    var pd = JsonSerializer.Deserialize<IngestionExtractedDraftDto>(
                        dj,
                        JsonOptions
                    );

                    if (pd?.ProposedTagNames is { Count: > 0 })
                    {
                        foreach (var pt in pd.ProposedTagNames)
                        {
                            if (
                                !string.IsNullOrWhiteSpace(pt)
                                && !tagNames.Contains(pt, StringComparer.OrdinalIgnoreCase)
                            )
                                tagNames.Add(pt);
                        }
                    }

                    if (pd?.Ingredients is { Count: > 0 })
                    {
                        foreach (var ing in pd.Ingredients)
                        {
                            if (
                                !string.IsNullOrWhiteSpace(ing.ProposedFoodItemName)
                                && availableFoodNames.Count < 500
                                && !availableFoodNames.Contains(
                                    ing.ProposedFoodItemName,
                                    StringComparer.OrdinalIgnoreCase
                                )
                            )
                                availableFoodNames.Add(ing.ProposedFoodItemName);
                        }
                    }
                }
                catch { }
            }

            // Determine if Vision or Text pipeline is needed

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

            var diagnosticsSource = job.NormalizedSourceText ?? job.RawText;

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
                    SupplementalHintText: hintText,
                    AvailableTagNames: tagNames,
                    AvailableFoodNames: availableFoodNames
                );

                job.StatusDetail = "Reading recipe text from images via local LLM...";
                await dbContext.SaveChangesAsync(jobCt);
                job.StructuredSourceText =
                    await llmClient.TranscribeRecipeImagesAsync(config, visionContext, jobCt)
                    ?? throw new DomainValidationException(
                        "No recipe text could be read from the images."
                    );
                diagnosticsSource = job.StructuredSourceText;
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

                var sourceText = job.NormalizedSourceText ?? job.RawText ?? "";
                job.StructuredSourceText = null;
                if (!RecipeSourceSectioner.HasCanonicalSections(sourceText))
                {
                    job.StatusDetail = "Organizing source text via local LLM...";
                    await dbContext.SaveChangesAsync(jobCt);
                    try
                    {
                        job.StructuredSourceText = await llmClient.StructureRecipeSourceAsync(
                            config,
                            sourceText,
                            jobCt
                        );
                    }
                    catch (Exception ex)
                        when ((ex is DomainValidationException or LlmHostUnavailableException)
                            && !jobCt.IsCancellationRequested
                        )
                    {
                        // Without sections there is no method to extract, so a host outage must
                        // surface as one instead of as a missing Instructions section.
                        if (
                            ex is LlmHostUnavailableException
                            && !sourceText.Contains(
                                "Instructions:",
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        {
                            throw;
                        }
                        // Sectioning is optional; if the host is really down, extraction below fails the same way.
                        logger.LogWarning(
                            ex,
                            "Source sectioning failed for job {JobId}; extracting from the unstructured text.",
                            job.Id
                        );
                    }
                    job.StatusDetail = "Extracting structured recipe via local LLM...";
                    await dbContext.SaveChangesAsync(jobCt);
                }

                diagnosticsSource = job.StructuredSourceText ?? sourceText;
            }

            var extractionContext = new LlmExtractionContext(
                SourceContent: diagnosticsSource,
                AvailableTagNames: tagNames,
                AvailableFoodNames: availableFoodNames
            );

            var rawExtracted = await llmClient.ExtractRecipeFromTextAsync(
                config,
                extractionContext,
                jobCt
            );

            if (await IsJobDiscardedInDbAsync(job.Id, jobCt))
            {
                return false;
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

            var extractionIssues = RecipeExtractionDiagnostics.GetReviewIssues(
                rawExtracted,
                diagnosticsSource
            );
            var qualityWarnings = RecipeExtractionDiagnostics.GetQualityWarnings(rawExtracted);
            var unresolvedIssues = new List<string>(extractionIssues);

            if (extractionIssues.Count > 0)
            {
                job.StatusDetail = "Auto-repairing recipe issues via local LLM...";
                await dbContext.SaveChangesAsync(jobCt);
                try
                {
                    var repairResult = await llmClient.TriageAndRepairRecipeAsync(
                        config,
                        new LlmAutoRepairAndTriageContext(
                            SourceContent: diagnosticsSource,
                            Draft: rawExtracted,
                            ValidationIssues: extractionIssues,
                            QualityWarnings: qualityWarnings,
                            AvailableTagNames: tagNames,
                            AvailableFoodNames: availableFoodNames
                        ),
                        jobCt
                    );

                    var postRepairIssues = RecipeExtractionDiagnostics.GetReviewIssues(
                        repairResult.Draft,
                        diagnosticsSource
                    );

                    var remainingHumanReviewIssues = postRepairIssues
                        .Where(issue =>
                        {
                            var decision = repairResult.TriageDecisions.FirstOrDefault(d =>
                                string.Equals(
                                    d.Issue?.Trim(),
                                    issue.Trim(),
                                    StringComparison.OrdinalIgnoreCase
                                )
                                || issue.Contains(
                                    d.Issue?.Trim() ?? "",
                                    StringComparison.OrdinalIgnoreCase
                                )
                            );
                            return decision is null
                                || decision.Resolution
                                    == IngestionIssueTriageResolution.NeedsHumanReview;
                        })
                        .ToList();

                    if (remainingHumanReviewIssues.Count < extractionIssues.Count)
                    {
                        rawExtracted = repairResult.Draft;
                        unresolvedIssues = remainingHumanReviewIssues;
                        qualityWarnings = RecipeExtractionDiagnostics.GetQualityWarnings(
                            rawExtracted
                        );
                        logger.LogInformation(
                            "Auto-repair improved job {JobId}: issues reduced from {Initial} to {Remaining}",
                            job.Id,
                            extractionIssues.Count,
                            unresolvedIssues.Count
                        );
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(
                        ex,
                        "Auto-repair pass failed for job {JobId}; continuing with initial extraction.",
                        job.Id
                    );
                }
            }

            var draft = RecipeDraftNormalizer.Normalize(
                raw: rawExtracted,
                catalogFoods: catalogFoods,
                tags: tags,
                sourceUrl: job.SourceUrl,
                fallbackSourceName: fallbackSourceName,
                fallbackSourceAuthor: fallbackSourceAuthor,
                coverImagePath: job.DownloadedCoverImagePath,
                thumbnailImagePath: job.DownloadedThumbnailImagePath,
                fallbackSourceBookTitle: job.SourceBookTitle,
                fallbackSourceBookPage: job.SourceBookPage,
                retainSourceAttachments: job.Channel
                    is IngestionChannel.Images
                        or IngestionChannel.Pdf
                        or IngestionChannel.RawText,
                extractedModel: modelUsed,
                fallbackCollectionIds: fallbackCollectionIds,
                rawSourceText: job.NormalizedSourceText ?? job.RawText
            );

            draft = draft with
            {
                ConfidenceScore = RecipeDraftNormalizer.CalculateConfidenceScore(
                    rawExtracted.ConfidenceScore,
                    draft.Ingredients?.Count ?? 0,
                    draft.Steps?.Count ?? 0,
                    validationIssueCount: unresolvedIssues.Count,
                    measurementWarningCount: draft.MeasurementWarnings?.Count ?? 0,
                    qualityWarningCount: qualityWarnings.Count
                ),
            };

            if (
                draft.Ingredients is { Count: > 0 }
                && draft.Ingredients.Any(i => i.IsRecipeReference)
            )
            {
                var updatedIngredients = new List<IngestionDraftIngredientDto>();
                foreach (var ing in draft.Ingredients)
                {
                    if (
                        ing.IsRecipeReference
                        && !string.IsNullOrWhiteSpace(ing.ReferencedRecipeTitle)
                    )
                    {
                        var cleanTitle =
                            RecipeDraftNormalizer.NormalizeRecipeReferenceTitle(
                                ing.ReferencedRecipeTitle
                            ) ?? ing.ReferencedRecipeTitle.Trim();
                        var normalizedTitle = cleanTitle.ToUpperInvariant();
                        var matchedRecipe = await dbContext
                            .Recipes.AsNoTracking()
                            .Where(r =>
                                r.Title.ToUpper() == normalizedTitle
                                && (
                                    !job.ResultingRecipeId.HasValue
                                    || r.Id != job.ResultingRecipeId.Value
                                )
                            )
                            .Select(r => new
                            {
                                r.Id,
                                r.Title,
                                r.Slug,
                            })
                            .FirstOrDefaultAsync(jobCt);

                        if (matchedRecipe is not null)
                        {
                            updatedIngredients.Add(
                                ing with
                                {
                                    MatchedRecipeId = matchedRecipe.Id,
                                    MatchedRecipeTitle = matchedRecipe.Title,
                                    MatchedRecipeSlug = matchedRecipe.Slug,
                                }
                            );
                            continue;
                        }
                    }

                    updatedIngredients.Add(ing);
                }

                draft = draft with { Ingredients = updatedIngredients };
            }

            if (unresolvedIssues.Count > 0)
            {
                logger.LogWarning(
                    "Recipe extraction for job {JobId} requires review: {Issues}",
                    job.Id,
                    string.Join(" ", unresolvedIssues)
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

            // Auto-Publish check

            var canAutoPublish =
                unresolvedIssues.Count == 0
                && (config.AutoPublishHighConfidenceText || job.AutoPublish)
                && (
                    job.Channel == IngestionChannel.WebUrl
                    || job.Channel == IngestionChannel.RawText
                )
                && !string.IsNullOrWhiteSpace(draft.Title)
                && (draft.Ingredients?.Count ?? 0) >= 1
                && (draft.Steps?.Count ?? 0) >= 1
                && draft.ConfidenceScore >= (job.AutoPublish ? 0.60m : 0.85m);

            if (canAutoPublish)
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
            }
            else
            {
                job.Status = IngestionJobStatus.ReadyForReview;

                job.ExtractedDraftJson = JsonSerializer.Serialize(draft, JsonOptions);

                job.ConfidenceScore = draft.ConfidenceScore;

                job.DisplayTitle = draft.Title;

                job.ProcessedAt = DateTimeOffset.UtcNow;

                job.StatusDetail = $"Ready for side-by-side review ({modelUsed}).";
                if (unresolvedIssues.Count > 0)
                {
                    var detail =
                        $"{job.StatusDetail} Validation: {string.Join(" ", unresolvedIssues)}";
                    job.StatusDetail = detail[..Math.Min(detail.Length, 2000)];
                }

                var refineModel = config.RefineModel?.Trim();
                if (
                    !string.IsNullOrWhiteSpace(refineModel)
                    && !string.Equals(
                        refineModel,
                        config.TextModel?.Trim(),
                        StringComparison.OrdinalIgnoreCase
                    )
                    && IngestionReviewService.Diagnose(draft, IngestionReviewService.GetSource(job))
                        is { } savedDiagnostics
                    && (
                        savedDiagnostics.ValidationIssues.Count > 0
                        || savedDiagnostics.QualityWarnings.Count > 0
                    )
                )
                {
                    var refinement = new IngestionDraftRefinement { IngestionJobId = job.Id };
                    IngestionReviewService.Requeue(refinement, refineModel, DateTimeOffset.UtcNow);
                    dbContext.IngestionDraftRefinements.Add(refinement);
                }
            }

            await dbContext.SaveChangesAsync(jobCt);

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

                var canPromote = isVisionJob
                    ? !string.IsNullOrWhiteSpace(config.VisionModel)
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

    private async Task<RecipeDetailDto> PublishDraftInternalAsync(
        IngestionJob job,
        IngestionExtractedDraftDto draft,
        CancellationToken cancellationToken
    )
    {
        // 1. Create missing food items

        var ingredientsInput = new List<RecipeIngredientInput>();

        if (draft.Ingredients is not null)
        {
            foreach (var ing in draft.Ingredients)
            {
                var recipeTitle = ing.IsRecipeReference ? ing.ReferencedRecipeTitle?.Trim() : null;
                if (ing.IsRecipeReference && string.IsNullOrWhiteSpace(recipeTitle))
                {
                    throw new DomainValidationException(
                        "A title is required for a recipe component reference."
                    );
                }

                var foodItemId = ing.IsRecipeReference ? null : ing.MatchedFoodItemId;
                Guid? linkedRecipeId = null;

                if (!string.IsNullOrWhiteSpace(recipeTitle))
                {
                    linkedRecipeId =
                        ing.MatchedRecipeId
                        ?? await IngestionCatalogMaterializer.ResolveRecipeReferenceAsync(
                            dbContext,
                            recipeTitle,
                            job.ResultingRecipeId,
                            cancellationToken
                        );
                }

                if (
                    foodItemId == null
                    && !ing.IsRecipeReference
                    && ing.CreateNewFoodItem
                    && !string.IsNullOrWhiteSpace(ing.ProposedFoodItemName)
                )
                {
                    foodItemId = await IngestionCatalogMaterializer.ResolveOrCreateFoodItemAsync(
                        dbContext,
                        ing.ProposedFoodItemName,
                        ing.SuggestedIsPantryStaple,
                        ing.SuggestedCategory,
                        cancellationToken
                    );
                }

                var (cleanQty, cleanMaxQty) = RecipeDraftNormalizer.NormalizeQuantities(
                    ing.Quantity,
                    ing.MaxQuantity
                );

                var cleanUnit =
                    ing.Unit == MeasurementUnit.Custom
                    && (
                        string.IsNullOrWhiteSpace(ing.CustomUnitName)
                        || RecipeDraftNormalizer.IsDisallowedUnitPlaceholder(ing.CustomUnitName)
                    )
                        ? null
                        : ing.Unit;

                if (
                    (cleanUnit is MeasurementUnit.Pinch or MeasurementUnit.Dash)
                    && (cleanQty is null or <= 0.1m)
                )
                {
                    cleanQty = 1m;
                }

                ingredientsInput.Add(
                    new RecipeIngredientInput(
                        SectionName: ing.SectionName,
                        Quantity: cleanQty,
                        MaxQuantity: cleanMaxQty,
                        Unit: cleanUnit,
                        CustomUnitName: cleanUnit == MeasurementUnit.Custom
                            ? ing.CustomUnitName
                            : null,
                        FoodItemId: foodItemId,
                        LinkedRecipeId: linkedRecipeId,
                        DisplayName: string.IsNullOrWhiteSpace(recipeTitle)
                            ? ing.DisplayName
                            : recipeTitle,
                        PreparationNote: ing.PreparationNote,
                        IsOptional: ing.IsOptional,
                        OriginalRawText: ing.OriginalRawText,
                        AlternativeMeasurement: ing.AlternativeMeasurement,
                        PreferAlternativeMeasurement: ing.PreferAlternativeMeasurement,
                        UnresolvedRecipeTitle: linkedRecipeId.HasValue ? null : recipeTitle
                    )
                );
            }
        }

        // 2. Map steps

        var stepsInput = new List<RecipeStepInput>();

        if (draft.Steps is not null)
        {
            foreach (var s in draft.Steps)
            {
                stepsInput.Add(
                    new RecipeStepInput(
                        SectionName: s.SectionName,
                        Instruction: s.Instruction,
                        IngredientIndices: s.IngredientIndices,
                        Timers: s.Timers,
                        ImagePath: s.ImagePath,
                        IngredientAmounts: s.IngredientAmounts
                    )
                );
            }
        }

        // 3. Resolve & auto-create any proposed tags

        var resolvedTagIds = await IngestionCatalogMaterializer.ResolveAndMaterializeTagsAsync(
            dbContext,
            draft.SuggestedTagIds,
            draft.ProposedTagNames,
            cancellationToken
        );

        // 4. Create published recipe

        var upsertRequest = new UpsertRecipeRequest(
            Title: draft.Title,
            Description: draft.Description != null && draft.Description.Length > 2000
                ? draft.Description[..2000].TrimEnd()
                : draft.Description,
            Status: RecipeStatus.Published,
            BaseServings: draft.BaseServings,
            YieldDisplay: draft.YieldDisplay,
            PrepTimeMinutes: draft.PrepTimeMinutes,
            CookTimeMinutes: draft.CookTimeMinutes,
            TotalTimeMinutes: draft.TotalTimeMinutes,
            Difficulty: draft.Difficulty,
            SourceUrl: draft.SourceUrl,
            SourceName: draft.SourceName,
            SourceAuthor: draft.SourceAuthor,
            SourceBookTitle: draft.SourceBookTitle,
            SourceBookPage: draft.SourceBookPage,
            Ingredients: ingredientsInput,
            Steps: stepsInput,
            CoverImagePath: draft.CoverImagePath,
            ThumbnailImagePath: draft.ThumbnailImagePath,
            CollectionIds: draft.SuggestedCollectionIds,
            TagIds: resolvedTagIds,
            ExtractedModel: job.ExtractedModel
        );

        RecipeDetailDto createdRecipe;

        if (job.ResultingRecipeId.HasValue)
        {
            var existingRecipe = await dbContext.Recipes.FirstOrDefaultAsync(
                r => r.Id == job.ResultingRecipeId.Value,
                cancellationToken
            );

            if (existingRecipe is not null)
            {
                var updated = await recipeService.UpdateRecipeAsync(
                    existingRecipe.Id,
                    upsertRequest,
                    cancellationToken
                );

                if (updated is not null)
                {
                    createdRecipe = updated;
                }
                else
                {
                    createdRecipe = await recipeService.CreateRecipeAsync(
                        upsertRequest,
                        cancellationToken
                    );
                }
            }
            else
            {
                createdRecipe = await recipeService.CreateRecipeAsync(
                    upsertRequest,
                    cancellationToken
                );
            }
        }
        else
        {
            createdRecipe = await recipeService.CreateRecipeAsync(upsertRequest, cancellationToken);
        }

        await IngestionCatalogMaterializer.PromoteOrAttachSourceFilesAsync(
            dbContext,
            mediaStorageService,
            job,
            draft,
            createdRecipe.Id,
            cancellationToken
        );

        return createdRecipe;
    }

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
}

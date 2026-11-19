using System.Text.Json;
using System.Text.RegularExpressions;
using App.Core.Common;
using App.Core.Exceptions;
using App.Core.FoodCatalog;
using App.Core.Ingestion;
using App.Core.Media;
using App.Core.Recipes;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace App.Data.Services;

public sealed class IngestionService(
    AppDbContext dbContext,
    IRecipeService recipeService,
    IMediaStorageService mediaStorageService,
    IIngestionQueueSignal queueSignal,
    ILlmSettingsService? llmSettingsService = null
) : IIngestionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static bool CanEditOrReview(IngestionJobStatus status) =>
        status
            is IngestionJobStatus.Queued
                or IngestionJobStatus.ReadyForReview
                or IngestionJobStatus.Published;

    public async Task<IngestionQueueSummaryDto> GetSummaryAsync(
        CancellationToken cancellationToken = default
    )
    {
        var readyForReviewCount = await dbContext.IngestionJobs.CountAsync(
            j => j.Status == IngestionJobStatus.ReadyForReview,
            cancellationToken
        );

        var activeProcessingCount = await dbContext.IngestionJobs.CountAsync(
            j =>
                j.Status == IngestionJobStatus.Queued
                || j.Status == IngestionJobStatus.Preprocessing
                || j.Status == IngestionJobStatus.Extracting,
            cancellationToken
        );

        var waitingForLlmHostCount = await dbContext.IngestionJobs.CountAsync(
            j => j.Status == IngestionJobStatus.WaitingForLlmHost,
            cancellationToken
        );

        var failedCount = await dbContext.IngestionJobs.CountAsync(
            j => j.Status == IngestionJobStatus.Failed,
            cancellationToken
        );

        var allCount = await dbContext.IngestionJobs.CountAsync(
            j => j.Status != IngestionJobStatus.Discarded,
            cancellationToken
        );

        var publishedCount = await dbContext.IngestionJobs.CountAsync(
            j => j.Status == IngestionJobStatus.Published,
            cancellationToken
        );

        return new IngestionQueueSummaryDto(
            ReadyForReviewCount: readyForReviewCount,
            ActiveProcessingCount: activeProcessingCount,
            WaitingForLlmHostCount: waitingForLlmHostCount,
            FailedCount: failedCount,
            AllCount: allCount,
            PublishedCount: publishedCount
        );
    }

    public async Task<PagedResultDto<IngestionJobSummaryDto>> GetJobsPagedAsync(
        IngestionJobQueryParameters parameters,
        CancellationToken cancellationToken = default
    )
    {
        var query = dbContext.IngestionJobs.AsNoTracking().AsQueryable();

        if (parameters.ActiveWaitingOnly)
        {
            query = query.Where(j =>
                j.Status == IngestionJobStatus.Queued
                || j.Status == IngestionJobStatus.Preprocessing
                || j.Status == IngestionJobStatus.Extracting
                || j.Status == IngestionJobStatus.WaitingForLlmHost
            );
        }
        else if (parameters.Status.HasValue)
        {
            query = query.Where(j => j.Status == parameters.Status.Value);
        }
        else
        {
            query = query.Where(j => j.Status != IngestionJobStatus.Discarded);
        }

        if (!string.IsNullOrWhiteSpace(parameters.Search))
        {
            var term = parameters.Search.Trim();
            query = query.Where(j =>
                j.DisplayTitle.Contains(term)
                || (j.SourceUrl != null && j.SourceUrl.Contains(term))
                || (j.StatusDetail != null && j.StatusDetail.Contains(term))
                || (j.ExtractedModel != null && j.ExtractedModel.Contains(term))
                || (j.RefinedModel != null && j.RefinedModel.Contains(term))
                || (j.ResultingRecipe != null && j.ResultingRecipe.Title.Contains(term))
            );
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var page = Math.Max(1, parameters.PageNumber);
        var pageSize = Math.Clamp(parameters.PageSize, 1, 200);

        IOrderedQueryable<IngestionJob> orderedQuery = (
            parameters.SortBy,
            parameters.SortDirection
        ) switch
        {
            (IngestionJobSortBy.Name, IngestionJobSortDirection.Asc) => query
                .OrderBy(j => j.DisplayTitle)
                .ThenByDescending(j => j.CreatedAt)
                .ThenBy(j => j.Id),
            (IngestionJobSortBy.Name, IngestionJobSortDirection.Desc) => query
                .OrderByDescending(j => j.DisplayTitle)
                .ThenByDescending(j => j.CreatedAt)
                .ThenBy(j => j.Id),
            (IngestionJobSortBy.ModifiedAt, IngestionJobSortDirection.Asc) => query
                .OrderBy(j => j.ModifiedAt)
                .ThenBy(j => j.CreatedAt)
                .ThenBy(j => j.Id),
            (IngestionJobSortBy.ModifiedAt, IngestionJobSortDirection.Desc) => query
                .OrderByDescending(j => j.ModifiedAt)
                .ThenByDescending(j => j.CreatedAt)
                .ThenBy(j => j.Id),
            (IngestionJobSortBy.CreatedAt, IngestionJobSortDirection.Asc) => query
                .OrderBy(j => j.CreatedAt)
                .ThenBy(j => j.Id),
            _ => query.OrderByDescending(j => j.CreatedAt).ThenBy(j => j.Id),
        };

        var jobs = await orderedQuery
            .Include(j => j.ResultingRecipe)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var userIds = jobs.Select(j => Guid.TryParse(j.CreatedBy, out var uid) ? uid : (Guid?)null)
            .Where(uid => uid.HasValue)
            .Select(uid => uid!.Value)
            .Distinct()
            .ToList();

        var usernames =
            userIds.Count > 0
                ? await dbContext
                    .Users.AsNoTracking()
                    .Where(u => userIds.Contains(u.Id))
                    .ToDictionaryAsync(u => u.Id.ToString(), u => u.Username, cancellationToken)
                : new Dictionary<string, string>();

        var items = jobs.Select(j =>
            {
                var fileCount = 0;
                if (!string.IsNullOrWhiteSpace(j.SourceFilesJson))
                {
                    try
                    {
                        var files = JsonSerializer.Deserialize<List<IngestionSourceFileDto>>(
                            j.SourceFilesJson,
                            JsonOptions
                        );
                        fileCount = files?.Count ?? 0;
                    }
                    catch
                    {
                        // ignore
                    }
                }

                return new IngestionJobSummaryDto(
                    Id: j.Id,
                    Channel: j.Channel,
                    Status: j.Status,
                    DisplayTitle: j.DisplayTitle,
                    SourceUrl: j.SourceUrl,
                    ThumbnailImagePath: j.DownloadedThumbnailImagePath,
                    ConfidenceScore: j.ConfidenceScore,
                    StatusDetail: j.StatusDetail,
                    AttemptCount: j.AttemptCount,
                    NextRetryAt: j.NextRetryAt,
                    ResultingRecipeId: j.ResultingRecipeId,
                    ResultingRecipeSlug: j.ResultingRecipe?.Slug,
                    SubmittedByUsername: usernames.TryGetValue(j.CreatedBy, out var name)
                        ? name
                        : null,
                    CreatedAt: j.CreatedAt,
                    ModifiedAt: j.ModifiedAt,
                    SourceFileCount: fileCount,
                    ExtractedModel: j.ExtractedModel,
                    RefinedModel: j.RefinedModel,
                    RefinedAt: j.RefinedAt
                );
            })
            .ToList();

        return new PagedResultDto<IngestionJobSummaryDto>(items, totalCount, page, pageSize);
    }

    public async Task<IReadOnlyList<IngestionJobSummaryDto>> GetJobsAsync(
        IngestionJobStatus? status = null,
        CancellationToken cancellationToken = default
    )
    {
        var query = dbContext.IngestionJobs.AsNoTracking().AsQueryable();

        if (status.HasValue)
        {
            query = query.Where(j => j.Status == status.Value);
        }
        else
        {
            query = query.Where(j => j.Status != IngestionJobStatus.Discarded);
        }

        var jobs = await query
            .Include(j => j.ResultingRecipe)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync(cancellationToken);

        var userIds = jobs.Select(j => Guid.TryParse(j.CreatedBy, out var uid) ? uid : (Guid?)null)
            .Where(uid => uid.HasValue)
            .Select(uid => uid!.Value)
            .Distinct()
            .ToList();

        var usernames =
            userIds.Count > 0
                ? await dbContext
                    .Users.AsNoTracking()
                    .Where(u => userIds.Contains(u.Id))
                    .ToDictionaryAsync(u => u.Id.ToString(), u => u.Username, cancellationToken)
                : new Dictionary<string, string>();

        return jobs.Select(j =>
            {
                var fileCount = 0;

                if (!string.IsNullOrWhiteSpace(j.SourceFilesJson))
                {
                    try
                    {
                        var files = JsonSerializer.Deserialize<List<IngestionSourceFileDto>>(
                            j.SourceFilesJson,
                            JsonOptions
                        );

                        fileCount = files?.Count ?? 0;
                    }
                    catch
                    {
                        // ignore
                    }
                }

                return new IngestionJobSummaryDto(
                    Id: j.Id,
                    Channel: j.Channel,
                    Status: j.Status,
                    DisplayTitle: j.DisplayTitle,
                    SourceUrl: j.SourceUrl,
                    ThumbnailImagePath: j.DownloadedThumbnailImagePath,
                    ConfidenceScore: j.ConfidenceScore,
                    StatusDetail: j.StatusDetail,
                    AttemptCount: j.AttemptCount,
                    NextRetryAt: j.NextRetryAt,
                    ResultingRecipeId: j.ResultingRecipeId,
                    ResultingRecipeSlug: j.ResultingRecipe?.Slug,
                    SubmittedByUsername: usernames.TryGetValue(j.CreatedBy, out var name)
                        ? name
                        : null,
                    CreatedAt: j.CreatedAt,
                    ModifiedAt: j.ModifiedAt,
                    SourceFileCount: fileCount,
                    ExtractedModel: j.ExtractedModel,
                    RefinedModel: j.RefinedModel,
                    RefinedAt: j.RefinedAt
                );
            })
            .ToList();
    }

    public async Task<IngestionJobDetailDto?> GetJobByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        var job = await dbContext
            .IngestionJobs.AsNoTracking()
            .Include(j => j.ResultingRecipe)
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job is null)
        {
            return null;
        }

        ScrapedWebRecipePayload? scraped = null;

        if (!string.IsNullOrWhiteSpace(job.ScrapedPageJson))
        {
            try
            {
                scraped = JsonSerializer.Deserialize<ScrapedWebRecipePayload>(
                    job.ScrapedPageJson,
                    JsonOptions
                );
            }
            catch
            {
                // ignore
            }
        }

        IngestionExtractedDraftDto? draft = null;

        if (!string.IsNullOrWhiteSpace(job.ExtractedDraftJson))
        {
            try
            {
                draft = JsonSerializer.Deserialize<IngestionExtractedDraftDto>(
                    job.ExtractedDraftJson,
                    JsonOptions
                );
            }
            catch
            {
                // ignore
            }
        }

        if (draft?.Ingredients is { Count: > 0 } && draft.Ingredients.Any(i => i.IsRecipeReference))
        {
            var (resolvedDraft, _) =
                await IngestionCatalogMaterializer.ResolveDraftRecipeReferencesAsync(
                    dbContext,
                    draft,
                    job.ResultingRecipeId,
                    cancellationToken
                );
            draft = resolvedDraft;
        }

        if (draft != null)
        {
            if (draft.Ingredients is { Count: > 0 })
            {
                var cleanedIngs = draft
                    .Ingredients.Select(i =>
                        RecipeUnitAndSectionHelpers.IsGenericTopLevelSection(i.SectionName)
                            ? i with
                            {
                                SectionName = null,
                            }
                            : i
                    )
                    .ToList();
                draft = draft with { Ingredients = cleanedIngs };
            }

            if (draft.Steps is { Count: > 0 })
            {
                var cleanedSteps = draft
                    .Steps.Select(s =>
                        RecipeUnitAndSectionHelpers.IsGenericTopLevelSection(s.SectionName)
                            ? s with
                            {
                                SectionName = null,
                            }
                            : s
                    )
                    .ToList();
                draft = draft with { Steps = cleanedSteps };
            }
        }

        string? username = null;

        if (Guid.TryParse(job.CreatedBy, out var uid))
        {
            username = await dbContext
                .Users.AsNoTracking()
                .Where(u => u.Id == uid)
                .Select(u => u.Username)
                .FirstOrDefaultAsync(cancellationToken);
        }

        IReadOnlyList<IngestionSourceFileDto>? sourceFiles = null;

        if (!string.IsNullOrWhiteSpace(job.SourceFilesJson))
        {
            try
            {
                sourceFiles = JsonSerializer.Deserialize<List<IngestionSourceFileDto>>(
                    job.SourceFilesJson,
                    JsonOptions
                );
            }
            catch
            {
                // ignore
            }
        }

        PdfInspectionSummaryDto? pdfInspection = null;

        if (!string.IsNullOrWhiteSpace(job.PdfInspectionJson))
        {
            try
            {
                pdfInspection = JsonSerializer.Deserialize<PdfInspectionSummaryDto>(
                    job.PdfInspectionJson,
                    JsonOptions
                );
            }
            catch
            {
                // ignore
            }
        }

        return new IngestionJobDetailDto(
            Id: job.Id,
            Channel: job.Channel,
            Status: job.Status,
            DisplayTitle: job.DisplayTitle,
            SourceUrl: job.SourceUrl,
            RawText: job.RawText,
            ScrapedPage: scraped,
            NormalizedSourceText: job.NormalizedSourceText,
            DownloadedCoverImagePath: job.DownloadedCoverImagePath,
            DownloadedThumbnailImagePath: job.DownloadedThumbnailImagePath,
            ExtractedDraft: draft,
            ConfidenceScore: job.ConfidenceScore,
            StatusDetail: job.StatusDetail,
            AttemptCount: job.AttemptCount,
            NextRetryAt: job.NextRetryAt,
            ProcessedAt: job.ProcessedAt,
            ResultingRecipeId: job.ResultingRecipeId,
            ResultingRecipeSlug: job.ResultingRecipe?.Slug,
            SubmittedByUsername: username,
            CreatedAt: job.CreatedAt,
            ModifiedAt: job.ModifiedAt,
            SourceFiles: sourceFiles,
            PdfInspection: pdfInspection,
            SourceBookTitle: job.SourceBookTitle,
            SourceBookPage: job.SourceBookPage,
            ExtractedModel: job.ExtractedModel,
            RefinedModel: job.RefinedModel,
            RefinedAt: job.RefinedAt
        );
    }

    public async Task<IReadOnlyList<IngestionJobSummaryDto>> EnqueueWebUrlsAsync(
        EnqueueWebUrlIngestionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (request.Urls is null || request.Urls.Count == 0)
        {
            throw new DomainValidationException("At least one URL must be provided.");
        }

        if (request.Urls.Count > 20)
        {
            throw new DomainValidationException("A maximum of 20 URLs can be queued per batch.");
        }

        var newJobs = new List<IngestionJob>();

        foreach (var urlStr in request.Urls)
        {
            if (string.IsNullOrWhiteSpace(urlStr))
            {
                continue;
            }

            var trimmed = urlStr.Trim();

            if (
                !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            )
            {
                throw new DomainValidationException(
                    $"Invalid URL: '{trimmed}'. URLs must begin with http:// or https://."
                );
            }

            var displayTitle = (uri.Host + uri.AbsolutePath).TrimEnd('/');

            if (displayTitle.Length > 200)
            {
                displayTitle = displayTitle[..200];
            }

            var job = new IngestionJob
            {
                Channel = IngestionChannel.WebUrl,

                Status = IngestionJobStatus.Queued,

                SourceUrl = trimmed,

                DisplayTitle = displayTitle,
            };

            dbContext.IngestionJobs.Add(job);

            newJobs.Add(job);
        }

        if (newJobs.Count == 0)
        {
            throw new DomainValidationException("No valid non-empty URLs found in request.");
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        queueSignal.NotifyWorkAvailable();

        return newJobs
            .Select(j => new IngestionJobSummaryDto(
                Id: j.Id,
                Channel: j.Channel,
                Status: j.Status,
                DisplayTitle: j.DisplayTitle,
                SourceUrl: j.SourceUrl,
                ThumbnailImagePath: null,
                ConfidenceScore: null,
                StatusDetail: "Queued for extraction.",
                AttemptCount: 0,
                NextRetryAt: null,
                ResultingRecipeId: null,
                ResultingRecipeSlug: null,
                SubmittedByUsername: null,
                CreatedAt: j.CreatedAt,
                ModifiedAt: j.ModifiedAt
            ))
            .ToList();
    }

    public async Task<IngestionJobSummaryDto> EnqueueImagesAsync(
        IReadOnlyList<IngestionUploadFileStream> files,
        EnqueueImageIngestionMetadata metadata,
        CancellationToken cancellationToken = default
    )
    {
        if (files is null || files.Count == 0 || files.Count > 6)
        {
            throw new DomainValidationException("You must provide between 1 and 6 image files.");
        }

        var jobId = Guid.NewGuid();

        var jobToken = jobId.ToString("N");

        var savedFiles = new List<SavedIngestionImageDto>();

        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];

            var saved = await mediaStorageService.SaveIngestionPageImageAsync(
                f.Content,
                f.OriginalFileName,
                f.ContentLength,
                jobToken,
                i + 1,
                i == 0,
                cancellationToken
            );

            savedFiles.Add(saved);
        }

        var sourceFiles = new List<IngestionSourceFileDto>();

        for (var i = 0; i < savedFiles.Count; i++)
        {
            var s = savedFiles[i];

            sourceFiles.Add(
                new IngestionSourceFileDto(
                    PageNumber: i + 1,
                    OriginalFileName: files[i].OriginalFileName,
                    UrlPath: s.UrlPath,
                    ContentType: "image/webp",
                    FileSizeBytes: s.FileSizeBytes,
                    Width: s.Width,
                    Height: s.Height
                )
            );
        }

        string displayTitle;

        if (!string.IsNullOrWhiteSpace(metadata.SourceBookTitle))
        {
            var cleanTitle = metadata.SourceBookTitle.Trim();

            displayTitle = metadata.SourceBookPage.HasValue
                ? $"Photo: {cleanTitle} (p. {metadata.SourceBookPage.Value})"
                : $"Photo: {cleanTitle}";
        }
        else
        {
            displayTitle =
                files.Count == 1 ? "Photo Scan (1 page)" : $"Photo Scan ({files.Count} pages)";
        }

        var job = new IngestionJob
        {
            Id = jobId,

            Channel = IngestionChannel.Images,

            Status = IngestionJobStatus.Queued,

            DisplayTitle = displayTitle,

            SourceBookTitle = string.IsNullOrWhiteSpace(metadata.SourceBookTitle)
                ? null
                : metadata.SourceBookTitle.Trim(),

            SourceBookPage = metadata.SourceBookPage,

            SourceFilesJson = JsonSerializer.Serialize(sourceFiles, JsonOptions),

            DownloadedCoverImagePath = savedFiles[0].UrlPath,

            DownloadedThumbnailImagePath = savedFiles[0].ThumbnailUrlPath ?? savedFiles[0].UrlPath,

            StatusDetail = "Queued for vision extraction.",
        };

        dbContext.IngestionJobs.Add(job);

        await dbContext.SaveChangesAsync(cancellationToken);

        queueSignal.NotifyWorkAvailable();

        return new IngestionJobSummaryDto(
            Id: job.Id,
            Channel: job.Channel,
            Status: job.Status,
            DisplayTitle: job.DisplayTitle,
            SourceUrl: null,
            ThumbnailImagePath: job.DownloadedThumbnailImagePath,
            ConfidenceScore: null,
            StatusDetail: job.StatusDetail,
            AttemptCount: 0,
            NextRetryAt: null,
            ResultingRecipeId: null,
            ResultingRecipeSlug: null,
            SubmittedByUsername: null,
            CreatedAt: job.CreatedAt,
            ModifiedAt: job.ModifiedAt,
            SourceFileCount: sourceFiles.Count
        );
    }

    public async Task<IngestionJobSummaryDto> EnqueuePdfAsync(
        IngestionUploadFileStream file,
        EnqueuePdfIngestionMetadata metadata,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(file);

        var jobId = Guid.NewGuid();

        var jobToken = jobId.ToString("N");

        var savedPdf = await mediaStorageService.SaveIngestionPdfAsync(
            file.Content,
            file.OriginalFileName,
            file.ContentLength,
            jobToken,
            cancellationToken
        );

        var sourceFiles = new List<IngestionSourceFileDto>
        {
            new(
                PageNumber: 1,
                OriginalFileName: file.OriginalFileName,
                UrlPath: savedPdf.UrlPath,
                ContentType: "application/pdf",
                FileSizeBytes: savedPdf.FileSizeBytes
            ),
        };

        string displayTitle;

        if (!string.IsNullOrWhiteSpace(metadata.SourceBookTitle))
        {
            var cleanTitle = metadata.SourceBookTitle.Trim();

            displayTitle = metadata.SourceBookPage.HasValue
                ? $"PDF: {cleanTitle} (p. {metadata.SourceBookPage.Value})"
                : $"PDF: {cleanTitle}";
        }
        else
        {
            var rawName = Path.GetFileNameWithoutExtension(file.OriginalFileName);

            displayTitle = string.IsNullOrWhiteSpace(rawName) ? "PDF Document" : $"PDF: {rawName}";
        }

        var job = new IngestionJob
        {
            Id = jobId,

            Channel = IngestionChannel.Pdf,

            Status = IngestionJobStatus.Queued,

            DisplayTitle = displayTitle,

            SourceBookTitle = string.IsNullOrWhiteSpace(metadata.SourceBookTitle)
                ? null
                : metadata.SourceBookTitle.Trim(),

            SourceBookPage = metadata.SourceBookPage,

            SourceFilesJson = JsonSerializer.Serialize(sourceFiles, JsonOptions),

            StatusDetail = "Queued for PDF inspection and processing.",
        };

        dbContext.IngestionJobs.Add(job);

        await dbContext.SaveChangesAsync(cancellationToken);

        queueSignal.NotifyWorkAvailable();

        return new IngestionJobSummaryDto(
            Id: job.Id,
            Channel: job.Channel,
            Status: job.Status,
            DisplayTitle: job.DisplayTitle,
            SourceUrl: null,
            ThumbnailImagePath: null,
            ConfidenceScore: null,
            StatusDetail: job.StatusDetail,
            AttemptCount: 0,
            NextRetryAt: null,
            ResultingRecipeId: null,
            ResultingRecipeSlug: null,
            SubmittedByUsername: null,
            CreatedAt: job.CreatedAt,
            ModifiedAt: job.ModifiedAt,
            SourceFileCount: 1
        );
    }

    public async Task<IReadOnlyList<IngestionJobSummaryDto>> EnqueuePdfBatchAsync(
        IReadOnlyList<IngestionUploadFileStream> files,
        EnqueuePdfBatchMetadata metadata,
        CancellationToken cancellationToken = default
    )
    {
        if (files is null || files.Count == 0)
        {
            throw new DomainValidationException("At least one PDF file must be provided.");
        }

        if (files.Count > 50)
        {
            throw new DomainValidationException(
                "A maximum of 50 PDF files can be queued per batch."
            );
        }

        var newJobs = new List<IngestionJob>();

        var cleanBookTitle = string.IsNullOrWhiteSpace(metadata.SourceBookTitle)
            ? null
            : metadata.SourceBookTitle.Trim();

        for (var fileIdx = 0; fileIdx < files.Count; fileIdx++)
        {
            var file = files[fileIdx];

            if (file.ContentLength <= 0)
            {
                continue;
            }

            var ext = Path.GetExtension(file.OriginalFileName);

            if (
                !string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase)
                && !file.ContentType.Contains("pdf", StringComparison.OrdinalIgnoreCase)
            )
            {
                throw new DomainValidationException(
                    $"File '{file.OriginalFileName}' is not a valid PDF document."
                );
            }

            var jobId = Guid.NewGuid();

            var jobToken = jobId.ToString("N");

            var savedPdf = await mediaStorageService.SaveIngestionPdfAsync(
                file.Content,
                file.OriginalFileName,
                file.ContentLength,
                jobToken,
                cancellationToken
            );

            var rawName = Path.GetFileNameWithoutExtension(file.OriginalFileName);

            string displayTitle;

            if (!string.IsNullOrWhiteSpace(cleanBookTitle))
            {
                displayTitle = string.IsNullOrWhiteSpace(rawName)
                    ? $"PDF: {cleanBookTitle} (#{fileIdx + 1})"
                    : $"PDF: {cleanBookTitle} - {rawName}";
            }
            else
            {
                displayTitle = string.IsNullOrWhiteSpace(rawName)
                    ? "PDF Document"
                    : $"PDF: {rawName}";
            }

            if (displayTitle.Length > 200)
            {
                displayTitle = displayTitle[..200];
            }

            var sourceFiles = new List<IngestionSourceFileDto>
            {
                new(
                    PageNumber: 1,
                    OriginalFileName: file.OriginalFileName,
                    UrlPath: savedPdf.UrlPath,
                    ContentType: "application/pdf",
                    FileSizeBytes: savedPdf.FileSizeBytes
                ),
            };

            var job = new IngestionJob
            {
                Id = jobId,

                Channel = IngestionChannel.Pdf,

                Status = IngestionJobStatus.Queued,

                DisplayTitle = displayTitle,

                SourceBookTitle = cleanBookTitle,

                SourceBookPage = null,

                SourceFilesJson = JsonSerializer.Serialize(sourceFiles, JsonOptions),

                StatusDetail = "Queued for PDF inspection and processing.",
            };

            dbContext.IngestionJobs.Add(job);

            newJobs.Add(job);
        }

        if (newJobs.Count == 0)
        {
            throw new DomainValidationException(
                "No valid non-empty PDF files were found in the request."
            );
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        queueSignal.NotifyWorkAvailable();

        return newJobs
            .Select(job => new IngestionJobSummaryDto(
                Id: job.Id,
                Channel: job.Channel,
                Status: job.Status,
                DisplayTitle: job.DisplayTitle,
                SourceUrl: null,
                ThumbnailImagePath: null,
                ConfidenceScore: null,
                StatusDetail: job.StatusDetail,
                AttemptCount: 0,
                NextRetryAt: null,
                ResultingRecipeId: null,
                ResultingRecipeSlug: null,
                SubmittedByUsername: null,
                CreatedAt: job.CreatedAt,
                ModifiedAt: job.ModifiedAt,
                SourceFileCount: 1
            ))
            .ToList();
    }

    public async Task<IngestionJobDetailDto?> PromoteSourcePageToCoverAsync(
        Guid id,
        int pageNumber,
        CancellationToken cancellationToken = default
    )
    {
        var job = await dbContext.IngestionJobs.FirstOrDefaultAsync(
            j => j.Id == id,
            cancellationToken
        );

        if (job is null)
        {
            return null;
        }

        if (!CanEditOrReview(job.Status))
        {
            throw new ConflictException(
                "Cannot modify cover image for a job not awaiting review or already published."
            );
        }

        string? targetImageUrl = null;

        if (
            job.Channel == IngestionChannel.Images
            && !string.IsNullOrWhiteSpace(job.SourceFilesJson)
        )
        {
            try
            {
                var files = JsonSerializer.Deserialize<List<IngestionSourceFileDto>>(
                    job.SourceFilesJson,
                    JsonOptions
                );

                if (files is { Count: > 0 })
                {
                    var match =
                        files.FirstOrDefault(f => f.PageNumber == pageNumber)
                        ?? (
                            pageNumber >= 1 && pageNumber <= files.Count
                                ? files[pageNumber - 1]
                                : null
                        );

                    if (match is not null)
                    {
                        targetImageUrl = match.UrlPath;
                    }
                }
            }
            catch
            {
                // ignore
            }
        }
        else if (
            job.Channel == IngestionChannel.Pdf
            && !string.IsNullOrWhiteSpace(job.PdfInspectionJson)
        )
        {
            try
            {
                var inspection = JsonSerializer.Deserialize<PdfInspectionSummaryDto>(
                    job.PdfInspectionJson,
                    JsonOptions
                );

                if (inspection?.RenderedPageImages is { Count: > 0 })
                {
                    var match =
                        inspection.RenderedPageImages.FirstOrDefault(p =>
                            p.PageNumber == pageNumber
                        )
                        ?? (
                            pageNumber >= 1 && pageNumber <= inspection.RenderedPageImages.Count
                                ? inspection.RenderedPageImages[pageNumber - 1]
                                : null
                        );

                    if (match is not null)
                    {
                        targetImageUrl = match.UrlPath;
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        if (string.IsNullOrWhiteSpace(targetImageUrl))
        {
            throw new DomainValidationException(
                $"Page {pageNumber} is not a valid renderable source image page."
            );
        }

        var promotedCover = await mediaStorageService.PromoteIngestionImageToRecipeCoverAsync(
            targetImageUrl,
            cancellationToken
        );

        job.DownloadedCoverImagePath = promotedCover.CoverImagePath;

        job.DownloadedThumbnailImagePath = promotedCover.ThumbnailImagePath;

        if (job.ResultingRecipeId.HasValue)
        {
            var linkedRecipe = await dbContext.Recipes.FirstOrDefaultAsync(
                r => r.Id == job.ResultingRecipeId.Value,
                cancellationToken
            );
            if (linkedRecipe is not null)
            {
                linkedRecipe.CoverImagePath = promotedCover.CoverImagePath;
                linkedRecipe.ThumbnailImagePath = promotedCover.ThumbnailImagePath;
            }
        }

        if (!string.IsNullOrWhiteSpace(job.ExtractedDraftJson))
        {
            try
            {
                var draft = JsonSerializer.Deserialize<IngestionExtractedDraftDto>(
                    job.ExtractedDraftJson,
                    JsonOptions
                );

                if (draft is not null)
                {
                    draft = draft with
                    {
                        CoverImagePath = promotedCover.CoverImagePath,

                        ThumbnailImagePath = promotedCover.ThumbnailImagePath,
                    };

                    job.ExtractedDraftJson = JsonSerializer.Serialize(draft, JsonOptions);
                }
            }
            catch
            {
                // ignore
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return await GetJobByIdAsync(id, cancellationToken);
    }

    public async Task<IngestionJobSummaryDto> EnqueueRawTextAsync(
        EnqueueRawTextIngestionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(request.RawText))
        {
            throw new DomainValidationException("Raw text content cannot be empty.");
        }

        var trimmed = request.RawText.Trim();

        if (trimmed.Length > 100000)
        {
            throw new DomainValidationException(
                "Raw text exceeds maximum allowed size of 100,000 characters."
            );
        }

        string displayTitle;

        if (!string.IsNullOrWhiteSpace(request.TitleHint))
        {
            displayTitle = request.TitleHint.Trim();
        }
        else
        {
            var firstLine =
                trimmed
                    .Split(
                        '\n',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    )
                    .FirstOrDefault()
                ?? "Raw Text Recipe";

            displayTitle = firstLine.TrimStart('#', '*', ' ', '-');
        }

        if (displayTitle.Length > 200)
        {
            displayTitle = displayTitle[..200];
        }

        var coverPath = IngestionCatalogMaterializer.ValidateOptionalMediaPath(
            request.CoverImagePath
        );

        var thumbPath =
            IngestionCatalogMaterializer.ValidateOptionalMediaPath(request.ThumbnailImagePath)
            ?? coverPath;

        string? initialCollectionsJson = null;

        if (request.CollectionIds is { Count: > 0 })
        {
            var distinctCollections = request.CollectionIds.Distinct().ToList();

            initialCollectionsJson = JsonSerializer.Serialize(distinctCollections, JsonOptions);
        }

        var jobToken = Guid.NewGuid();

        var jobTokenStr = jobToken.ToString("N");

        var cleanFileName = string.Join("_", displayTitle.Split(Path.GetInvalidFileNameChars()))
            .Trim();

        if (cleanFileName.Length > 80)
            cleanFileName = cleanFileName[..80].Trim();

        var fileName = $"{cleanFileName}.txt";

        var savedText = await mediaStorageService.SaveIngestionSourceTextAsync(
            trimmed,
            fileName,
            jobTokenStr,
            cancellationToken
        );

        var sourceFiles = new List<IngestionSourceFileDto>
        {
            new(
                PageNumber: 1,
                OriginalFileName: fileName,
                UrlPath: savedText.MediaUrlPath,
                ContentType: "text/plain",
                FileSizeBytes: savedText.FileSizeBytes
            ),
        };

        var job = new IngestionJob
        {
            Id = jobToken,

            Channel = IngestionChannel.RawText,

            Status = IngestionJobStatus.Queued,

            RawText = trimmed,

            NormalizedSourceText = trimmed,

            SourceFilesJson = JsonSerializer.Serialize(sourceFiles, JsonOptions),

            DisplayTitle = displayTitle,

            SourceName = string.IsNullOrWhiteSpace(request.SourceName)
                ? null
                : request.SourceName.Trim(),

            SourceAuthor = string.IsNullOrWhiteSpace(request.SourceAuthor)
                ? null
                : request.SourceAuthor.Trim(),

            SourceBookTitle = string.IsNullOrWhiteSpace(request.SourceBookTitle)
                ? null
                : request.SourceBookTitle.Trim(),

            SourceBookPage = request.SourceBookPage,

            DownloadedCoverImagePath = coverPath,

            DownloadedThumbnailImagePath = thumbPath,

            InitialCollectionIdsJson = initialCollectionsJson,

            AutoPublish = request.AutoPublish,
        };

        dbContext.IngestionJobs.Add(job);

        await dbContext.SaveChangesAsync(cancellationToken);

        queueSignal.NotifyWorkAvailable();

        return new IngestionJobSummaryDto(
            Id: job.Id,
            Channel: job.Channel,
            Status: job.Status,
            DisplayTitle: job.DisplayTitle,
            SourceUrl: null,
            ThumbnailImagePath: job.DownloadedThumbnailImagePath,
            ConfidenceScore: null,
            StatusDetail: "Queued for extraction.",
            AttemptCount: 0,
            NextRetryAt: null,
            ResultingRecipeId: null,
            ResultingRecipeSlug: null,
            SubmittedByUsername: null,
            CreatedAt: job.CreatedAt,
            ModifiedAt: job.ModifiedAt,
            SourceFileCount: sourceFiles.Count,
            ExtractedModel: job.ExtractedModel
        );
    }

    public async Task<IngestionJobDetailDto?> SaveDraftAsync(
        Guid id,
        IngestionExtractedDraftDto draft,
        CancellationToken cancellationToken = default
    )
    {
        var job = await dbContext.IngestionJobs.FirstOrDefaultAsync(
            j => j.Id == id,
            cancellationToken
        );

        if (job is null)
        {
            return null;
        }

        if (!CanEditOrReview(job.Status))
        {
            throw new ConflictException(
                "Only an ingestion job awaiting review or already published can be edited."
            );
        }

        job.ExtractedDraftJson = JsonSerializer.Serialize(draft, JsonOptions);

        if (!string.IsNullOrWhiteSpace(draft.Title))
        {
            job.DisplayTitle = draft.Title.Trim();
        }

        job.ConfidenceScore = draft.ConfidenceScore;

        job.DownloadedCoverImagePath = draft.CoverImagePath;

        job.DownloadedThumbnailImagePath = draft.ThumbnailImagePath;

        if (!string.IsNullOrWhiteSpace(draft.ExtractedModel))
        {
            job.ExtractedModel = draft.ExtractedModel;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return await GetJobByIdAsync(id, cancellationToken);
    }

    public async Task<RecipeDetailDto> ApproveAndPublishAsync(
        Guid id,
        IngestionExtractedDraftDto draft,
        CancellationToken cancellationToken = default
    )
    {
        var job = await dbContext.IngestionJobs.FirstOrDefaultAsync(
            j => j.Id == id,
            cancellationToken
        );

        if (job is null)
        {
            throw new NotFoundException("Ingestion job not found.");
        }

        if (!CanEditOrReview(job.Status))
        {
            throw new ConflictException(
                "Only an ingestion job awaiting review or already published can be approved."
            );
        }

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

                var (cleanQty, cleanMaxQty) = RecipeUnitAndSectionHelpers.NormalizeQuantities(
                    ing.Quantity,
                    ing.MaxQuantity
                );

                var cleanUnit =
                    ing.Unit == MeasurementUnit.Custom
                    && (
                        string.IsNullOrWhiteSpace(ing.CustomUnitName)
                        || RecipeUnitAndSectionHelpers.IsDisallowedUnitPlaceholder(
                            ing.CustomUnitName
                        )
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

                var replacementsInput =
                    await IngestionCatalogMaterializer.MaterializeReplacementsAsync(
                        dbContext,
                        ing.Replacements,
                        cancellationToken
                    );

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
                        UnresolvedRecipeTitle: linkedRecipeId.HasValue ? null : recipeTitle,
                        Replacements: replacementsInput
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
                        Timers: s.Timers,
                        ImagePath: s.ImagePath
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
            Description: draft.Description,
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
            ExtractedModel: job.ExtractedModel,
            RefinedModel: job.RefinedModel ?? draft.RefinedModel ?? string.Empty
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

                createdRecipe =
                    updated ?? throw new InvalidOperationException("Failed to update recipe.");
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

        job.Status = IngestionJobStatus.Published;

        job.ResultingRecipeId = createdRecipe.Id;

        job.ExtractedDraftJson = JsonSerializer.Serialize(draft, JsonOptions);

        job.StatusDetail = "Approved and published to cookbook.";

        job.ProcessedAt = DateTimeOffset.UtcNow;

        job.DisplayTitle = draft.Title;

        await dbContext.SaveChangesAsync(cancellationToken);

        var globalAutoPublish =
            llmSettingsService is not null
            && (
                await llmSettingsService.GetEffectiveConfigAsync(cancellationToken)
            ).AutoPublishHighConfidenceText;
        await IngestionCatalogMaterializer.PromoteWaitingRecipeReferenceJobsAsync(
            dbContext,
            recipeService,
            mediaStorageService,
            globalAutoPublish,
            cancellationToken
        );

        return (await recipeService.GetRecipeByIdAsync(createdRecipe.Id, cancellationToken))
            ?? createdRecipe;
    }

    public async Task<IngestionJobSummaryDto?> RetryJobAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        var job = await dbContext.IngestionJobs.FirstOrDefaultAsync(
            j => j.Id == id,
            cancellationToken
        );

        if (job is null)
        {
            return null;
        }

        if (job.Status is IngestionJobStatus.Discarded)
        {
            throw new ConflictException("Cannot retry a discarded ingestion job.");
        }

        if (job.Status is IngestionJobStatus.Preprocessing or IngestionJobStatus.Extracting)
        {
            if (queueSignal.ActiveJobId == job.Id)
            {
                throw new ConflictException("Ingestion job is currently being processed.");
            }
        }

        job.Status = IngestionJobStatus.Queued;

        job.NextRetryAt = null;

        job.AttemptCount = 0;

        job.RunNowRequested = true;

        job.RefinedModel = null;

        job.RefinedAt = null;

        job.RefinementAttemptCount = 0;

        job.RefinementNextRetryAt = null;

        job.StatusDetail = "Queued for retry.";

        await dbContext.SaveChangesAsync(cancellationToken);

        queueSignal.NotifyWorkAvailable();

        return ToSummary(job);
    }

    public async Task<IngestionJobSummaryDto?> RunJobNowAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        var job = await dbContext.IngestionJobs.FirstOrDefaultAsync(
            j => j.Id == id,
            cancellationToken
        );

        if (job is null)
        {
            return null;
        }

        if (
            job.Status
            is not (
                IngestionJobStatus.Queued
                or IngestionJobStatus.WaitingForLlmHost
                or IngestionJobStatus.Failed
                or IngestionJobStatus.Preprocessing
                or IngestionJobStatus.Extracting
            )
        )
        {
            throw new ConflictException(
                "Only a queued, waiting, failed, or interrupted ingestion job can be run now."
            );
        }

        if (job.Status is IngestionJobStatus.Preprocessing or IngestionJobStatus.Extracting)
        {
            if (queueSignal.ActiveJobId == job.Id)
            {
                throw new ConflictException("Ingestion job is currently being processed.");
            }
        }

        if (job.Status is not IngestionJobStatus.Queued)
        {
            job.Status = IngestionJobStatus.Queued;
            job.AttemptCount = 0;
            job.RefinedModel = null;
            job.RefinedAt = null;
            job.RefinementAttemptCount = 0;
            job.RefinementNextRetryAt = null;
        }

        job.NextRetryAt = null;
        job.RunNowRequested = true;
        job.StatusDetail = "Queued to run next, even while ingestion is paused.";

        await dbContext.SaveChangesAsync(cancellationToken);

        queueSignal.NotifyWorkAvailable();

        return ToSummary(job);
    }

    private static IngestionJobSummaryDto ToSummary(IngestionJob job) =>
        new(
            Id: job.Id,
            Channel: job.Channel,
            Status: job.Status,
            DisplayTitle: job.DisplayTitle,
            SourceUrl: job.SourceUrl,
            ThumbnailImagePath: job.DownloadedThumbnailImagePath,
            ConfidenceScore: job.ConfidenceScore,
            StatusDetail: job.StatusDetail,
            AttemptCount: job.AttemptCount,
            NextRetryAt: job.NextRetryAt,
            ResultingRecipeId: job.ResultingRecipeId,
            ResultingRecipeSlug: null,
            SubmittedByUsername: null,
            CreatedAt: job.CreatedAt,
            ModifiedAt: job.ModifiedAt,
            ExtractedModel: job.ExtractedModel,
            RefinedModel: job.RefinedModel,
            RefinedAt: job.RefinedAt
        );

    public async Task<RetryWaitingJobsResultDto> RetryAllWaitingJobsAsync(
        CancellationToken cancellationToken = default
    )
    {
        var waitingJobs = await dbContext
            .IngestionJobs.Where(j => j.Status == IngestionJobStatus.WaitingForLlmHost)
            .ToListAsync(cancellationToken);

        if (waitingJobs.Count == 0)
        {
            return new RetryWaitingJobsResultDto(0);
        }

        foreach (var job in waitingJobs)
        {
            job.Status = IngestionJobStatus.Queued;

            job.NextRetryAt = null;

            job.StatusDetail = "Queued for retry.";
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        queueSignal.NotifyWorkAvailable();

        return new RetryWaitingJobsResultDto(waitingJobs.Count);
    }

    public Task<RetryBatchJobsResultDto> RetryBatchJobsAsync(
        IReadOnlyList<Guid>? jobIds,
        string? filter = null,
        CancellationToken cancellationToken = default
    ) => RetryBatchJobsAsync(new RetryBatchJobsRequest(jobIds, filter), cancellationToken);

    public async Task<RetryBatchJobsResultDto> RetryBatchJobsAsync(
        RetryBatchJobsRequest request,
        CancellationToken cancellationToken = default
    )
    {
        IQueryable<IngestionJob> query = dbContext.IngestionJobs.Where(j =>
            j.Status != IngestionJobStatus.Discarded
        );

        var activeId = queueSignal.ActiveJobId;
        if (activeId.HasValue)
        {
            query = query.Where(j => j.Id != activeId.Value);
        }

        if (request.JobIds is { Count: > 0 })
        {
            query = query.Where(j => request.JobIds.Contains(j.Id));
        }
        else if (
            !string.IsNullOrWhiteSpace(request.Filter)
            && !request.Filter.Equals("All", StringComparison.OrdinalIgnoreCase)
        )
        {
            query = request.Filter.Trim().ToLowerInvariant() switch
            {
                "readyforreview" or "review" => query.Where(j =>
                    j.Status == IngestionJobStatus.ReadyForReview
                ),
                "activewaiting" or "active" => query.Where(j =>
                    j.Status == IngestionJobStatus.Queued
                    || j.Status == IngestionJobStatus.Preprocessing
                    || j.Status == IngestionJobStatus.Extracting
                    || j.Status == IngestionJobStatus.WaitingForLlmHost
                ),
                "waiting" => query.Where(j => j.Status == IngestionJobStatus.WaitingForLlmHost),
                "published" => query.Where(j => j.Status == IngestionJobStatus.Published),
                "failed" => query.Where(j => j.Status == IngestionJobStatus.Failed),
                _ => query,
            };
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(j =>
                j.DisplayTitle.Contains(term)
                || (j.SourceUrl != null && j.SourceUrl.Contains(term))
                || (j.StatusDetail != null && j.StatusDetail.Contains(term))
                || (j.ExtractedModel != null && j.ExtractedModel.Contains(term))
                || (j.RefinedModel != null && j.RefinedModel.Contains(term))
                || (j.ResultingRecipe != null && j.ResultingRecipe.Title.Contains(term))
            );
        }

        var jobs = await query.ToListAsync(cancellationToken);

        if (jobs.Count == 0)
        {
            return new RetryBatchJobsResultDto(0);
        }

        foreach (var job in jobs)
        {
            job.Status = IngestionJobStatus.Queued;

            job.NextRetryAt = null;

            job.AttemptCount = 0;

            job.RefinedModel = null;

            job.RefinedAt = null;

            job.RefinementAttemptCount = 0;

            job.RefinementNextRetryAt = null;

            job.StatusDetail = "Queued for retry.";
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        queueSignal.NotifyWorkAvailable();

        return new RetryBatchJobsResultDto(jobs.Count);
    }

    public async Task<bool> DiscardJobAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await dbContext.IngestionJobs.FirstOrDefaultAsync(
            j => j.Id == id,
            cancellationToken
        );

        if (job is null)
        {
            return false;
        }

        if (queueSignal.IsJobActive(job.Id))
        {
            queueSignal.CancelActiveExecution(job.Id);
        }

        job.Status = IngestionJobStatus.Discarded;

        job.StatusDetail = "Job discarded by user.";

        var oldCover = job.DownloadedCoverImagePath;

        var oldThumb = job.DownloadedThumbnailImagePath;

        if (job.ResultingRecipeId == null)
        {
            job.DownloadedCoverImagePath = null;

            job.DownloadedThumbnailImagePath = null;
        }

        if (job.ResultingRecipeId == null)
        {
            if (!string.IsNullOrWhiteSpace(oldCover))
            {
                await mediaStorageService.DeleteMediaFileAsync(oldCover, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(oldThumb))
            {
                await mediaStorageService.DeleteMediaFileAsync(oldThumb, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(job.SourceFilesJson))
            {
                try
                {
                    var sourceFiles = JsonSerializer.Deserialize<List<IngestionSourceFileDto>>(
                        job.SourceFilesJson,
                        JsonOptions
                    );

                    if (sourceFiles is { Count: > 0 })
                    {
                        foreach (var sf in sourceFiles)
                        {
                            await mediaStorageService.DeleteMediaFileAsync(
                                sf.UrlPath,
                                cancellationToken
                            );
                        }
                    }
                }
                catch
                {
                    // ignore
                }
            }

            if (!string.IsNullOrWhiteSpace(job.PdfInspectionJson))
            {
                try
                {
                    var inspection = JsonSerializer.Deserialize<PdfInspectionSummaryDto>(
                        job.PdfInspectionJson,
                        JsonOptions
                    );

                    if (inspection?.RenderedPageImages is { Count: > 0 })
                    {
                        foreach (var rendered in inspection.RenderedPageImages)
                        {
                            await mediaStorageService.DeleteMediaFileAsync(
                                rendered.UrlPath,
                                cancellationToken
                            );
                        }
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<IngestionReviewDiagnosticsDto?> GetReviewDiagnosticsAsync(
        Guid id,
        IngestionExtractedDraftDto draft,
        CancellationToken cancellationToken = default
    )
    {
        var jobExists = await dbContext
            .IngestionJobs.AsNoTracking()
            .AnyAsync(j => j.Id == id, cancellationToken);
        if (!jobExists)
        {
            return null;
        }

        var validationIssues = new List<string>();
        var qualityWarnings = new List<string>();
        var targeted = new List<IngestionDiagnosticTargetDto>();

        if (string.IsNullOrWhiteSpace(draft.Title))
        {
            const string msg = "Recipe title is missing.";
            validationIssues.Add(msg);
            targeted.Add(new IngestionDiagnosticTargetDto(msg, "error"));
        }

        var ingredients = draft.Ingredients ?? [];
        var steps = draft.Steps ?? [];

        if (
            ingredients.Any(i =>
                i is null || (i.Replacements is not null && i.Replacements.Any(r => r is null))
            ) || steps.Any(s => s is null || (s.Timers is not null && s.Timers.Any(t => t is null)))
        )
        {
            throw new DomainValidationException("Draft contains malformed null entries.");
        }

        if (ingredients.Count == 0)
        {
            const string msg = "Recipe has no ingredients.";
            validationIssues.Add(msg);
            targeted.Add(new IngestionDiagnosticTargetDto(msg, "error"));
        }

        if (steps.Count == 0)
        {
            const string msg = "Recipe has no preparation steps.";
            validationIssues.Add(msg);
            targeted.Add(new IngestionDiagnosticTargetDto(msg, "error"));
        }

        for (var i = 0; i < ingredients.Count; i++)
        {
            var ing = ingredients[i];
            if (ing.IsRecipeReference && !ing.MatchedRecipeId.HasValue)
            {
                var refTitle = !string.IsNullOrWhiteSpace(ing.ReferencedRecipeTitle)
                    ? ing.ReferencedRecipeTitle
                    : ing.DisplayName;
                var msg =
                    $"Ingredient #{i + 1} ('{ing.DisplayName}') references sub-recipe '{refTitle}' which could not be matched yet.";
                validationIssues.Add(msg);
                targeted.Add(new IngestionDiagnosticTargetDto(msg, "error", IngredientIndex: i));
            }

            if (
                (ing.Quantity.HasValue && ing.Quantity.Value <= 0m)
                || (ing.MaxQuantity.HasValue && ing.MaxQuantity.Value <= 0m)
            )
            {
                var msg = $"Ingredient #{i + 1} ('{ing.DisplayName}') has a non-positive quantity.";
                validationIssues.Add(msg);
                targeted.Add(new IngestionDiagnosticTargetDto(msg, "error", IngredientIndex: i));
            }

            if (!ing.IsRecipeReference)
            {
                var candidateFoodName = !string.IsNullOrWhiteSpace(ing.ProposedFoodItemName)
                    ? ing.ProposedFoodItemName
                    : ing.DisplayName;
                if (!FoodNameSanitizer.IsValidFoodName(candidateFoodName))
                {
                    var msg =
                        $"Ingredient #{i + 1} ('{ing.DisplayName}') does not have a meaningful food name.";
                    validationIssues.Add(msg);
                    targeted.Add(
                        new IngestionDiagnosticTargetDto(msg, "error", IngredientIndex: i)
                    );
                }
            }

            foreach (var rep in ing.Replacements ?? [])
            {
                if (
                    (rep.Quantity.HasValue && rep.Quantity.Value <= 0m)
                    || (rep.MaxQuantity.HasValue && rep.MaxQuantity.Value <= 0m)
                )
                {
                    var msg =
                        $"Ingredient #{i + 1} replacement ('{rep.DisplayName}') has a non-positive quantity.";
                    validationIssues.Add(msg);
                    targeted.Add(
                        new IngestionDiagnosticTargetDto(msg, "error", IngredientIndex: i)
                    );
                }

                var candidateRepFoodName = !string.IsNullOrWhiteSpace(rep.ProposedFoodItemName)
                    ? rep.ProposedFoodItemName
                    : rep.DisplayName;
                if (!FoodNameSanitizer.IsValidFoodName(candidateRepFoodName))
                {
                    var msg =
                        $"Ingredient #{i + 1} replacement ('{rep.DisplayName}') does not have a meaningful food name.";
                    validationIssues.Add(msg);
                    targeted.Add(
                        new IngestionDiagnosticTargetDto(msg, "error", IngredientIndex: i)
                    );
                }
            }
        }

        foreach (var existingIssue in draft.ValidationIssues ?? [])
        {
            if (
                !string.IsNullOrWhiteSpace(existingIssue)
                && !validationIssues.Contains(existingIssue, StringComparer.Ordinal)
            )
            {
                validationIssues.Add(existingIssue);
            }
        }

        for (var sIdx = 0; sIdx < steps.Count; sIdx++)
        {
            var step = steps[sIdx];

            if (string.IsNullOrWhiteSpace(step.Instruction))
            {
                var msg = $"Step {sIdx + 1}: Instruction text is empty.";
                validationIssues.Add(msg);
                targeted.Add(new IngestionDiagnosticTargetDto(msg, "error", StepNumber: sIdx + 1));
            }
            else
            {
                var extractedTokens = StepInstructionParser.ExtractTokens(step.Instruction);
                foreach (var token in extractedTokens)
                {
                    if (token is StepIngredientToken ingTok)
                    {
                        if (
                            string.IsNullOrWhiteSpace(ingTok.Ingredient)
                            || (ingTok.Amount.HasValue && ingTok.Amount.Value <= 0m)
                        )
                        {
                            var msg =
                                $"Step {sIdx + 1}: Step ingredient token has a missing name or non-positive amount.";
                            validationIssues.Add(msg);
                            targeted.Add(
                                new IngestionDiagnosticTargetDto(msg, "error", StepNumber: sIdx + 1)
                            );
                        }
                    }
                    else if (token is StepTimerToken timerTok && timerTok.Seconds <= 0)
                    {
                        var msg = $"Step {sIdx + 1}: Timer token has a non-positive duration.";
                        validationIssues.Add(msg);
                        targeted.Add(
                            new IngestionDiagnosticTargetDto(msg, "error", StepNumber: sIdx + 1)
                        );
                    }
                    else if (
                        token is StepTemperatureToken tempTok
                        && !tempTok.Fahrenheit.HasValue
                        && !tempTok.Celsius.HasValue
                    )
                    {
                        var msg = $"Step {sIdx + 1}: Temperature token has no numeric value.";
                        validationIssues.Add(msg);
                        targeted.Add(
                            new IngestionDiagnosticTargetDto(msg, "error", StepNumber: sIdx + 1)
                        );
                    }
                    else if (token is StepDimensionToken dimTok && dimTok.Value <= 0m)
                    {
                        var msg = $"Step {sIdx + 1}: Dimension token has a non-positive value.";
                        validationIssues.Add(msg);
                        targeted.Add(
                            new IngestionDiagnosticTargetDto(msg, "error", StepNumber: sIdx + 1)
                        );
                    }
                }
            }

            foreach (var timer in step.Timers ?? [])
            {
                var trimmedLabel = timer.Label?.Trim() ?? string.Empty;
                if (
                    string.IsNullOrWhiteSpace(trimmedLabel)
                    || trimmedLabel.Equals("Cook", StringComparison.OrdinalIgnoreCase)
                    || trimmedLabel.Equals("Timer", StringComparison.OrdinalIgnoreCase)
                )
                {
                    var msg =
                        $"Step {sIdx + 1}: Timer '{timer.Label}' has a vague label; describe the action instead.";
                    qualityWarnings.Add(msg);
                    targeted.Add(
                        new IngestionDiagnosticTargetDto(msg, "warning", StepNumber: sIdx + 1)
                    );
                }
            }
        }

        foreach (var existingWarning in draft.QualityWarnings ?? [])
        {
            if (
                !string.IsNullOrWhiteSpace(existingWarning)
                && !qualityWarnings.Contains(existingWarning, StringComparer.Ordinal)
            )
            {
                qualityWarnings.Add(existingWarning);
            }
        }

        return new IngestionReviewDiagnosticsDto(validationIssues, qualityWarnings, targeted);
    }
}

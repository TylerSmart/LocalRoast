using System.Text.Json;
using App.Core.Exceptions;
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
    IIngestionQueueSignal queueSignal
) : IIngestionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

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

        var waitingForHostCount = await dbContext.IngestionJobs.CountAsync(
            j => j.Status == IngestionJobStatus.WaitingForLlmHost,
            cancellationToken
        );

        var failedCount = await dbContext.IngestionJobs.CountAsync(
            j => j.Status == IngestionJobStatus.Failed,
            cancellationToken
        );

        return new IngestionQueueSummaryDto(
            ReadyForReviewCount: readyForReviewCount,
            ActiveProcessingCount: activeProcessingCount,
            WaitingForLlmHostCount: waitingForHostCount,
            FailedCount: failedCount
        );
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

        return jobs.Select(j => new IngestionJobSummaryDto(
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
                SubmittedByUsername: usernames.TryGetValue(j.CreatedBy, out var name) ? name : null,
                CreatedAt: j.CreatedAt,
                ModifiedAt: j.ModifiedAt
            ))
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

        string? username = null;
        if (Guid.TryParse(job.CreatedBy, out var uid))
        {
            username = await dbContext
                .Users.AsNoTracking()
                .Where(u => u.Id == uid)
                .Select(u => u.Username)
                .FirstOrDefaultAsync(cancellationToken);
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
            ModifiedAt: job.ModifiedAt
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

        var job = new IngestionJob
        {
            Channel = IngestionChannel.RawText,
            Status = IngestionJobStatus.Queued,
            RawText = trimmed,
            DisplayTitle = displayTitle,
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
            StatusDetail: "Queued for extraction.",
            AttemptCount: 0,
            NextRetryAt: null,
            ResultingRecipeId: null,
            ResultingRecipeSlug: null,
            SubmittedByUsername: null,
            CreatedAt: job.CreatedAt,
            ModifiedAt: job.ModifiedAt
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

        if (job.Status is IngestionJobStatus.Published or IngestionJobStatus.Discarded)
        {
            throw new ConflictException(
                "Cannot update draft for a published or discarded ingestion job."
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

        if (job.Status == IngestionJobStatus.Published)
        {
            throw new ConflictException("Ingestion job has already been published.");
        }

        if (job.Status == IngestionJobStatus.Discarded)
        {
            throw new ConflictException("Cannot approve a discarded ingestion job.");
        }

        // 1. Create missing food items
        var ingredientsInput = new List<RecipeIngredientInput>();
        if (draft.Ingredients is not null)
        {
            foreach (var ing in draft.Ingredients)
            {
                var foodItemId = ing.MatchedFoodItemId;

                if (
                    foodItemId == null
                    && ing.CreateNewFoodItem
                    && !string.IsNullOrWhiteSpace(ing.ProposedFoodItemName)
                )
                {
                    var cleanName = ing.ProposedFoodItemName.Trim();
                    var normName = cleanName.ToLowerInvariant();

                    var existing =
                        await dbContext.FoodItems.FirstOrDefaultAsync(
                            f => f.NormalizedName == normName,
                            cancellationToken
                        )
                        ?? await dbContext
                            .FoodItemAliases.Where(a => a.NormalizedAlias == normName)
                            .Select(a => a.FoodItem)
                            .FirstOrDefaultAsync(cancellationToken);

                    if (existing is not null)
                    {
                        foodItemId = existing.Id;
                    }
                    else
                    {
                        if (ing.SuggestedStoreAisleId.HasValue)
                        {
                            var aisleExists = await dbContext.StoreAisles.AnyAsync(
                                a => a.Id == ing.SuggestedStoreAisleId.Value,
                                cancellationToken
                            );
                            if (!aisleExists)
                            {
                                throw new DomainValidationException(
                                    $"Suggested store aisle '{ing.SuggestedStoreAisleId}' does not exist."
                                );
                            }
                        }

                        var newFoodItem = new FoodItem
                        {
                            Name = cleanName,
                            NormalizedName = normName,
                            DefaultStoreAisleId = ing.SuggestedStoreAisleId,
                            IsPantryStaple = ing.SuggestedIsPantryStaple,
                        };
                        dbContext.FoodItems.Add(newFoodItem);
                        await dbContext.SaveChangesAsync(cancellationToken);
                        foodItemId = newFoodItem.Id;
                    }
                }

                ingredientsInput.Add(
                    new RecipeIngredientInput(
                        SectionName: ing.SectionName,
                        Quantity: ing.Quantity,
                        MaxQuantity: ing.MaxQuantity,
                        Unit: ing.Unit,
                        CustomUnitName: ing.CustomUnitName,
                        FoodItemId: foodItemId,
                        LinkedRecipeId: null,
                        DisplayName: ing.DisplayName,
                        PreparationNote: ing.PreparationNote,
                        IsOptional: ing.IsOptional,
                        OriginalRawText: ing.OriginalRawText
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
                        ImagePath: s.ImagePath
                    )
                );
            }
        }

        // 3. Create published recipe
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
            SourceBookTitle: null,
            SourceBookPage: null,
            Ingredients: ingredientsInput,
            Steps: stepsInput,
            CoverImagePath: draft.CoverImagePath,
            ThumbnailImagePath: draft.ThumbnailImagePath,
            CollectionIds: draft.SuggestedCollectionIds,
            TagIds: draft.SuggestedTagIds
        );

        var createdRecipe = await recipeService.CreateRecipeAsync(upsertRequest, cancellationToken);

        job.Status = IngestionJobStatus.Published;
        job.ResultingRecipeId = createdRecipe.Id;
        job.ExtractedDraftJson = JsonSerializer.Serialize(draft, JsonOptions);
        job.StatusDetail = "Approved and published to cookbook.";
        job.ProcessedAt = DateTimeOffset.UtcNow;
        job.DisplayTitle = draft.Title;

        await dbContext.SaveChangesAsync(cancellationToken);
        return createdRecipe;
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

        if (job.Status is IngestionJobStatus.Published or IngestionJobStatus.Discarded)
        {
            throw new ConflictException("Cannot retry a published or discarded ingestion job.");
        }

        job.Status = IngestionJobStatus.Queued;
        job.NextRetryAt = null;
        job.StatusDetail = "Queued for retry.";

        await dbContext.SaveChangesAsync(cancellationToken);
        queueSignal.NotifyWorkAvailable();

        return new IngestionJobSummaryDto(
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
            ModifiedAt: job.ModifiedAt
        );
    }

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

        if (job.Status == IngestionJobStatus.Published)
        {
            throw new ConflictException("Cannot discard a published ingestion job.");
        }

        job.Status = IngestionJobStatus.Discarded;
        job.StatusDetail = "Job discarded by user.";

        if (job.ResultingRecipeId == null)
        {
            if (!string.IsNullOrWhiteSpace(job.DownloadedCoverImagePath))
            {
                await mediaStorageService.DeleteMediaFileAsync(
                    job.DownloadedCoverImagePath,
                    cancellationToken
                );
            }
            if (!string.IsNullOrWhiteSpace(job.DownloadedThumbnailImagePath))
            {
                await mediaStorageService.DeleteMediaFileAsync(
                    job.DownloadedThumbnailImagePath,
                    cancellationToken
                );
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}

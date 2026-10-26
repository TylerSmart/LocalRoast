using System.Text.Json;
using App.Core.Exceptions;
using App.Core.FoodCatalog;
using App.Core.Ingestion;
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
        var interrupted = await dbContext
            .IngestionJobs.Where(j =>
                j.Status == IngestionJobStatus.Preprocessing
                || j.Status == IngestionJobStatus.Extracting
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
        var now = DateTimeOffset.UtcNow;
        var nextJob = await dbContext
            .IngestionJobs.Where(j =>
                j.Status == IngestionJobStatus.Queued
                && (j.NextRetryAt == null || j.NextRetryAt <= now)
            )
            .OrderBy(j => j.CreatedAt)
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
        var job = await dbContext.IngestionJobs.FirstOrDefaultAsync(
            j => j.Id == jobId,
            cancellationToken
        );

        if (
            job is null
            || job.Status is IngestionJobStatus.Published or IngestionJobStatus.Discarded
        )
        {
            return false;
        }

        try
        {
            // Step 1: Preprocessing
            job.Status = IngestionJobStatus.Preprocessing;
            job.StatusDetail = "Preprocessing source content...";
            await dbContext.SaveChangesAsync(cancellationToken);

            if (job.Channel == IngestionChannel.WebUrl)
            {
                if (string.IsNullOrWhiteSpace(job.ScrapedPageJson))
                {
                    if (string.IsNullOrWhiteSpace(job.SourceUrl))
                    {
                        throw new DomainValidationException("WebUrl job has no SourceUrl.");
                    }

                    var scrapeResult = await scraper.ScrapeRecipePageAsync(
                        job.SourceUrl,
                        cancellationToken
                    );
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
                            || job.DisplayTitle.StartsWith(
                                "http",
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                    )
                    {
                        job.DisplayTitle = payload.PageTitle.Trim();
                    }

                    await dbContext.SaveChangesAsync(cancellationToken);
                }
            }
            else if (job.Channel == IngestionChannel.RawText)
            {
                if (string.IsNullOrWhiteSpace(job.NormalizedSourceText))
                {
                    job.NormalizedSourceText = job.RawText;
                    await dbContext.SaveChangesAsync(cancellationToken);
                }
            }

            // Step 2: Extracting
            job.Status = IngestionJobStatus.Extracting;
            job.StatusDetail = "Extracting structured recipe via local LLM...";
            await dbContext.SaveChangesAsync(cancellationToken);

            var config = await llmSettingsService.GetEffectiveConfigAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(config.TextModel))
            {
                throw new LlmHostUnavailableException(
                    "No Text LLM model is configured in LLM Settings. Please select a Text Model and retry."
                );
            }

            var aisles = await dbContext
                .StoreAisles.AsNoTracking()
                .OrderBy(a => a.SortOrder)
                .Select(a => new CatalogAisleLookupItem(a.Id, a.Name, a.NormalizedName))
                .ToListAsync(cancellationToken);

            var tags = await dbContext
                .Tags.AsNoTracking()
                .Select(t => new CatalogTagLookupItem(t.Id, t.Name, t.NormalizedName, t.Slug))
                .ToListAsync(cancellationToken);

            var catalogFoods = await dbContext
                .FoodItems.AsNoTracking()
                .Select(f => new CatalogFoodLookupItem(
                    f.Id,
                    f.Name,
                    f.NormalizedName,
                    f.DefaultStoreAisleId,
                    f.IsPantryStaple,
                    f.Aliases.Select(a => a.NormalizedAlias).ToList()
                ))
                .ToListAsync(cancellationToken);

            var extractionContext = new LlmExtractionContext(
                SourceContent: job.NormalizedSourceText ?? job.RawText ?? "",
                AvailableAisleNames: aisles.Select(a => a.Name).ToList(),
                AvailableTagNames: tags.Select(t => t.Name).ToList()
            );

            var rawExtracted = await llmClient.ExtractRecipeFromTextAsync(
                config,
                extractionContext,
                cancellationToken
            );

            string? fallbackSourceName = null;
            string? fallbackSourceAuthor = null;
            if (!string.IsNullOrWhiteSpace(job.ScrapedPageJson))
            {
                try
                {
                    var page = JsonSerializer.Deserialize<ScrapedWebRecipePayload>(
                        job.ScrapedPageJson,
                        JsonOptions
                    );
                    fallbackSourceName = page?.SiteName;
                    fallbackSourceAuthor = page?.Author;
                }
                catch
                {
                    // Ignore parse error
                }
            }

            var draft = RecipeDraftNormalizer.Normalize(
                raw: rawExtracted,
                catalogFoods: catalogFoods,
                aisles: aisles,
                tags: tags,
                sourceUrl: job.SourceUrl,
                fallbackSourceName: fallbackSourceName,
                fallbackSourceAuthor: fallbackSourceAuthor,
                coverImagePath: job.DownloadedCoverImagePath,
                thumbnailImagePath: job.DownloadedThumbnailImagePath
            );

            // Auto-Publish check
            var canAutoPublish =
                config.AutoPublishHighConfidenceText
                && (
                    job.Channel == IngestionChannel.WebUrl
                    || job.Channel == IngestionChannel.RawText
                )
                && !string.IsNullOrWhiteSpace(draft.Title)
                && (draft.Ingredients?.Count ?? 0) >= 2
                && (draft.Steps?.Count ?? 0) >= 1
                && draft.ConfidenceScore >= 0.85m;

            if (canAutoPublish)
            {
                var createdRecipe = await PublishDraftInternalAsync(job, draft, cancellationToken);
                job.Status = IngestionJobStatus.Published;
                job.ResultingRecipeId = createdRecipe.Id;
                job.ExtractedDraftJson = JsonSerializer.Serialize(draft, JsonOptions);
                job.ConfidenceScore = draft.ConfidenceScore;
                job.DisplayTitle = draft.Title;
                job.ProcessedAt = DateTimeOffset.UtcNow;
                job.StatusDetail = "Auto-published high-confidence extraction.";
            }
            else
            {
                job.Status = IngestionJobStatus.ReadyForReview;
                job.ExtractedDraftJson = JsonSerializer.Serialize(draft, JsonOptions);
                job.ConfidenceScore = draft.ConfidenceScore;
                job.DisplayTitle = draft.Title;
                job.ProcessedAt = DateTimeOffset.UtcNow;
                job.StatusDetail = "Ready for side-by-side review.";
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (LlmHostUnavailableException ex)
        {
            logger.LogWarning(
                ex,
                "LLM host unavailable while processing job {JobId}. Transitioning to WaitingForLlmHost.",
                job.Id
            );
            job.Status = IngestionJobStatus.WaitingForLlmHost;
            job.AttemptCount++;
            var delaySeconds = Math.Min(300, 15 * (1 << Math.Min(job.AttemptCount - 1, 5)));
            job.NextRetryAt = DateTimeOffset.UtcNow.AddSeconds(delaySeconds);
            job.StatusDetail = ex.Message;
            await dbContext.SaveChangesAsync(cancellationToken);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "Fatal error processing job {JobId}. Transitioning to Failed.",
                job.Id
            );
            job.Status = IngestionJobStatus.Failed;
            job.AttemptCount++;
            job.StatusDetail = ex.Message;
            await dbContext.SaveChangesAsync(cancellationToken);
            return false;
        }
    }

    public async Task<int> ProbeAndPromoteWaitingJobsAsync(
        CancellationToken cancellationToken = default
    )
    {
        var now = DateTimeOffset.UtcNow;
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
            foreach (var job in waitingJobs)
            {
                job.Status = IngestionJobStatus.Queued;
                job.NextRetryAt = null;
                job.StatusDetail = "LLM host is online; queued for extraction.";
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            queueSignal.NotifyWorkAvailable();
            logger.LogInformation(
                "LLM host is back online; promoted {Count} waiting job(s) to Queued.",
                waitingJobs.Count
            );
            return waitingJobs.Count;
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

        return await recipeService.CreateRecipeAsync(upsertRequest, cancellationToken);
    }

    private static string BuildNormalizedWebText(ScrapedWebRecipePayload payload)
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

        if (!string.IsNullOrWhiteSpace(payload.CleanedBodyText))
        {
            sb.AppendLine();
            sb.AppendLine("Cleaned Page Text:");
            sb.AppendLine(payload.CleanedBodyText);
        }

        return sb.ToString();
    }
}

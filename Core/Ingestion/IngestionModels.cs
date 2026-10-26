using System.ComponentModel.DataAnnotations;
using App.Core.Recipes;

namespace App.Core.Ingestion;

public record ScrapedWebRecipePayload(
    string CanonicalUrl,
    string? PageTitle,
    string? SiteName,
    string? Author,
    string? Description,
    string? PrepTimeIso,
    int? PrepTimeMinutes,
    string? CookTimeIso,
    int? CookTimeMinutes,
    string? TotalTimeIso,
    int? TotalTimeMinutes,
    string? RecipeYieldText,
    string? PrimaryImageUrl,
    IReadOnlyList<string> RawIngredientLines,
    IReadOnlyList<string> RawInstructionLines,
    string CleanedBodyText,
    bool HadRecipeJsonLd
);

public record WebScrapeResult(
    ScrapedWebRecipePayload Payload,
    string? DownloadedCoverImagePath,
    string? DownloadedThumbnailImagePath
);

// Raw JSON contract returned by Ollama / OpenAI-compatible Text LLM
public record LlmExtractedTimerItem(int DurationSeconds, string Label);

public record LlmExtractedIngredientItem(
    string? SectionName,
    decimal? Quantity,
    decimal? MaxQuantity,
    string? Unit,
    string? CanonicalFoodName,
    string DisplayName,
    string? PreparationNote,
    bool IsOptional,
    string? OriginalRawText,
    string? SuggestedAisleName,
    bool SuggestedIsPantryStaple = false
);

public record LlmExtractedStepItem(
    string? SectionName,
    string Instruction,
    IReadOnlyList<int>? IngredientIndices,
    IReadOnlyList<LlmExtractedTimerItem>? Timers
);

public record LlmExtractedRecipePayload(
    string Title,
    string? Description,
    decimal? BaseServings,
    string? YieldDisplay,
    int? PrepTimeMinutes,
    int? CookTimeMinutes,
    int? TotalTimeMinutes,
    string? Difficulty,
    string? SourceName,
    string? SourceAuthor,
    decimal? ConfidenceScore,
    IReadOnlyList<string>? SuggestedTagNames,
    IReadOnlyList<LlmExtractedIngredientItem>? Ingredients,
    IReadOnlyList<LlmExtractedStepItem>? Steps
);

// Normalized structured draft stored in IngestionJob.ExtractedDraftJson & edited in Review Inbox
public record IngestionDraftIngredientDto(
    string? SectionName = null,
    decimal? Quantity = null,
    decimal? MaxQuantity = null,
    MeasurementUnit? Unit = null,
    string? CustomUnitName = null,
    Guid? MatchedFoodItemId = null,
    string? MatchedFoodItemName = null,
    bool IsNewFoodItem = false,
    [MaxLength(150)] string? ProposedFoodItemName = null,
    Guid? SuggestedStoreAisleId = null,
    bool SuggestedIsPantryStaple = false,
    bool CreateNewFoodItem = true,
    [Required, MaxLength(200)] string DisplayName = "",
    [MaxLength(200)] string? PreparationNote = null,
    bool IsOptional = false,
    [MaxLength(500)] string? OriginalRawText = null
);

public record IngestionDraftStepDto(
    [MaxLength(150)] string? SectionName = null,
    [Required, MaxLength(4000)] string Instruction = "",
    IReadOnlyList<int>? IngredientIndices = null,
    IReadOnlyList<RecipeStepTimerInput>? Timers = null,
    [MaxLength(500)] string? ImagePath = null
);

public record IngestionExtractedDraftDto(
    [Required, MaxLength(200)] string Title,
    [MaxLength(2000)] string? Description = null,
    [Range(typeof(decimal), "0.25", "999")] decimal BaseServings = 4m,
    [MaxLength(100)] string? YieldDisplay = null,
    [Range(0, 10080)] int? PrepTimeMinutes = null,
    [Range(0, 10080)] int? CookTimeMinutes = null,
    [Range(0, 10080)] int? TotalTimeMinutes = null,
    RecipeDifficulty? Difficulty = null,
    [MaxLength(2048)] string? SourceUrl = null,
    [MaxLength(200)] string? SourceName = null,
    [MaxLength(200)] string? SourceAuthor = null,
    [MaxLength(500)] string? CoverImagePath = null,
    [MaxLength(500)] string? ThumbnailImagePath = null,
    IReadOnlyList<Guid>? SuggestedCollectionIds = null,
    IReadOnlyList<Guid>? SuggestedTagIds = null,
    IReadOnlyList<IngestionDraftIngredientDto>? Ingredients = null,
    IReadOnlyList<IngestionDraftStepDto>? Steps = null,
    decimal ConfidenceScore = 0.8m
);

public record EnqueueWebUrlIngestionRequest([Required] IReadOnlyList<string> Urls);

public record EnqueueRawTextIngestionRequest(
    [Required, MaxLength(100000)] string RawText,
    [MaxLength(200)] string? TitleHint = null
);

public record IngestionQueueSummaryDto(
    int ReadyForReviewCount,
    int ActiveProcessingCount,
    int WaitingForLlmHostCount,
    int FailedCount
);

public record IngestionJobSummaryDto(
    Guid Id,
    IngestionChannel Channel,
    IngestionJobStatus Status,
    string DisplayTitle,
    string? SourceUrl,
    string? ThumbnailImagePath,
    decimal? ConfidenceScore,
    string? StatusDetail,
    int AttemptCount,
    DateTimeOffset? NextRetryAt,
    Guid? ResultingRecipeId,
    string? ResultingRecipeSlug,
    string? SubmittedByUsername,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt
);

public record IngestionJobDetailDto(
    Guid Id,
    IngestionChannel Channel,
    IngestionJobStatus Status,
    string DisplayTitle,
    string? SourceUrl,
    string? RawText,
    ScrapedWebRecipePayload? ScrapedPage,
    string? NormalizedSourceText,
    string? DownloadedCoverImagePath,
    string? DownloadedThumbnailImagePath,
    IngestionExtractedDraftDto? ExtractedDraft,
    decimal? ConfidenceScore,
    string? StatusDetail,
    int AttemptCount,
    DateTimeOffset? NextRetryAt,
    DateTimeOffset? ProcessedAt,
    Guid? ResultingRecipeId,
    string? ResultingRecipeSlug,
    string? SubmittedByUsername,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt
);

public record RetryWaitingJobsResultDto(int RequeuedCount);

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using App.Core.FoodCatalog;
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

// Raw JSON contract returned by Ollama / OpenAI-compatible Text/Vision LLM
public record LlmExtractedTimerItem(
    [property: JsonConverter(typeof(FlexibleIntJsonConverter))] int DurationSeconds,
    string? Label = null
);

public record LlmExtractedIngredientItem(
    string? SectionName,
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))] decimal? Quantity,
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))] decimal? MaxQuantity,
    string? Unit,
    string? CanonicalFoodName,
    string? DisplayName = null,
    string? PreparationNote = null,
    bool IsOptional = false,
    string? OriginalRawText = null,
    bool SuggestedIsPantryStaple = false,
    LlmExtractedMeasurementItem? AlternativeMeasurement = null,
    string? SuggestedCategory = null,
    bool IsRecipeReference = false,
    string? ReferencedRecipeTitle = null,
    string? CustomUnitName = null
);

public record LlmExtractedMeasurementItem(
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))] decimal? Quantity,
    string? Unit,
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))]
        decimal? MaxQuantity = null
);

public record LlmExtractedStepIngredientAmountItem(
    [property: JsonConverter(typeof(FlexibleNullableIntJsonConverter))] int? IngredientIndex,
    LlmExtractedMeasurementItem Measurement
);

public record LlmExtractedStepItem(
    string? SectionName,
    string? Instruction = null,
    IReadOnlyList<int>? IngredientIndices = null,
    IReadOnlyList<LlmExtractedTimerItem>? Timers = null,
    IReadOnlyList<LlmExtractedStepIngredientAmountItem>? IngredientAmounts = null,
    string? OriginalRawText = null
);

public record LlmExtractedRecipePayload(
    string? Title = null,
    string? Description = null,
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))]
        decimal? BaseServings = null,
    string? YieldDisplay = null,
    [property: JsonConverter(typeof(FlexibleNullableIntJsonConverter))] int? PrepTimeMinutes = null,
    [property: JsonConverter(typeof(FlexibleNullableIntJsonConverter))] int? CookTimeMinutes = null,
    [property: JsonConverter(typeof(FlexibleNullableIntJsonConverter))]
        int? TotalTimeMinutes = null,
    string? Difficulty = null,
    string? SourceName = null,
    string? SourceAuthor = null,
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))]
        decimal? ConfidenceScore = null,
    IReadOnlyList<string>? SuggestedTagNames = null,
    IReadOnlyList<LlmExtractedIngredientItem>? Ingredients = null,
    IReadOnlyList<LlmExtractedStepItem>? Steps = null,
    string? SourceBookTitle = null,
    [property: JsonConverter(typeof(FlexibleNullableIntJsonConverter))] int? SourceBookPage = null,
    string? ExtractedModel = null
)
{
    /// <summary>Reconciliation issues found by the tagged pipeline; never part of a JSON schema.</summary>
    [JsonIgnore]
    public IReadOnlyList<string>? TaggerIssues { get; init; }

    /// <summary>Indicates whether this draft was produced by an automated repair pass.</summary>
    [JsonIgnore]
    public bool IsRepaired { get; init; }
}

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
    bool SuggestedIsPantryStaple = false,
    bool CreateNewFoodItem = true,
    [Required, MaxLength(200)] string DisplayName = "",
    [MaxLength(200)] string? PreparationNote = null,
    bool IsOptional = false,
    [MaxLength(500)] string? OriginalRawText = null,
    RecipeMeasurement? AlternativeMeasurement = null,
    bool PreferAlternativeMeasurement = false,
    FoodCategory? SuggestedCategory = null,
    bool IsRecipeReference = false,
    [MaxLength(200)] string? ReferencedRecipeTitle = null,
    Guid? MatchedRecipeId = null,
    [MaxLength(200)] string? MatchedRecipeTitle = null,
    [MaxLength(250)] string? MatchedRecipeSlug = null
);

public record IngestionDraftStepDto(
    [MaxLength(150)] string? SectionName = null,
    [Required, MaxLength(4000)] string Instruction = "",
    IReadOnlyList<int>? IngredientIndices = null,
    IReadOnlyList<RecipeStepTimerInput>? Timers = null,
    [MaxLength(500)] string? ImagePath = null,
    IReadOnlyList<RecipeStepIngredientAmountInput>? IngredientAmounts = null
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
    decimal ConfidenceScore = 0.8m,
    [MaxLength(200)] string? SourceBookTitle = null,
    [Range(1, 10000)] int? SourceBookPage = null,
    bool RetainSourceAttachments = true,
    [MaxLength(200)] string? ExtractedModel = null,
    IReadOnlyList<string>? ProposedTagNames = null,
    IReadOnlyList<string>? MeasurementWarnings = null
);

public record EnqueueWebUrlIngestionRequest([Required] IReadOnlyList<string> Urls);

public record EnqueueRawTextIngestionRequest(
    [Required, MaxLength(100000)] string RawText,
    [MaxLength(200)] string? TitleHint = null,
    [MaxLength(200)] string? SourceName = null,
    [MaxLength(200)] string? SourceAuthor = null,
    [MaxLength(200)] string? SourceBookTitle = null,
    [Range(1, 10000)] int? SourceBookPage = null,
    [MaxLength(500)] string? CoverImagePath = null,
    [MaxLength(500)] string? ThumbnailImagePath = null,
    IReadOnlyList<Guid>? CollectionIds = null,
    bool AutoPublish = false
);

public sealed record IngestionUploadFileStream(
    Stream Content,
    string OriginalFileName,
    string ContentType,
    long ContentLength
);

public record EnqueueImageIngestionMetadata(
    [MaxLength(200)] string? TitleHint = null,
    [MaxLength(200)] string? SourceBookTitle = null,
    [Range(1, 10000)] int? SourceBookPage = null
);

public record EnqueuePdfIngestionMetadata(
    [MaxLength(200)] string? TitleHint = null,
    [MaxLength(200)] string? SourceBookTitle = null,
    [Range(1, 10000)] int? SourceBookPage = null
);

public record EnqueuePdfBatchMetadata(
    [MaxLength(200)] string? SourceBookTitle = null,
    bool SplitMultiPage = false
);

public record PromotePageToCoverRequest([Range(1, 50)] int PageNumber = 1);

public record IngestionSourceFileDto(
    int PageNumber,
    string OriginalFileName,
    string UrlPath,
    string ContentType,
    long FileSizeBytes,
    int? Width = null,
    int? Height = null
);

public record PdfInspectionSummaryDto(
    int TotalPageCount,
    int ProcessedPageCount,
    int TotalExtractedCharacters,
    double AverageCharactersPerPage,
    PdfExtractionMode ExtractionMode,
    IReadOnlyList<IngestionSourceFileDto> RenderedPageImages
);

public record IngestionQueueSummaryDto(
    int ReadyForReviewCount,
    int ActiveProcessingCount,
    int WaitingForLlmHostCount,
    int FailedCount,
    int AllCount = 0,
    int PublishedCount = 0
);

public record IngestionJobQueryParameters(
    IngestionJobStatus? Status = null,
    [MaxLength(200)] string? Search = null,
    int PageNumber = 1,
    int PageSize = 25,
    bool ActiveWaitingOnly = false
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
    DateTimeOffset ModifiedAt,
    int SourceFileCount = 0,
    string? ExtractedModel = null
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
    DateTimeOffset ModifiedAt,
    IReadOnlyList<IngestionSourceFileDto>? SourceFiles = null,
    PdfInspectionSummaryDto? PdfInspection = null,
    string? SourceBookTitle = null,
    int? SourceBookPage = null,
    string? ExtractedModel = null
)
{
    public IReadOnlyList<IngestionSourceFileDto> SourceFiles { get; init; } = SourceFiles ?? [];
}

public record RetryWaitingJobsResultDto(int RequeuedCount);

public sealed record RetryBatchJobsRequest(
    IReadOnlyList<Guid>? JobIds = null,
    [MaxLength(50)] string? Filter = null,
    [MaxLength(200)] string? Search = null
);

public sealed record RetryBatchJobsResultDto(int RequeuedCount);

using App.Core.FoodCatalog;
using App.Core.Ingestion;
using App.Core.MealPlans;
using App.Core.Recipes;
using App.Core.Shopping;

namespace App.Data.Services;

internal sealed record AppDataBackupManifest(
    int FormatVersion,
    DateTimeOffset ExportedAt,
    string Application,
    bool IncludesMedia,
    AppDataBackupCounts Counts,
    int MediaFilesCount
);

internal sealed record AppDataBackupCounts(
    int Tags,
    int Collections,
    int FoodItems,
    int FoodItemAliases,
    int CatalogEmbeddings,
    int Recipes,
    int RecipeIngredients,
    int RecipeIngredientReplacements,
    int RecipeSteps,
    int RecipeStepTimers,
    int RecipeTags,
    int RecipeCollections,
    int RecipeAttachments,
    int RecipeEmbeddings,
    int ShareLinks,
    int IngestionJobs,
    int MealPlanTemplates,
    int MealPlanTemplateItems,
    int MealPlanEntries,
    int ShoppingLists,
    int ShoppingListItems,
    int ShoppingListItemSources,
    int CookLogs
);

internal sealed record AppDataBackupSnapshot
{
    public IReadOnlyList<TagSnapshot> Tags { get; init; } = [];
    public IReadOnlyList<CollectionSnapshot> Collections { get; init; } = [];
    public IReadOnlyList<FoodItemSnapshot> FoodItems { get; init; } = [];
    public IReadOnlyList<FoodItemAliasSnapshot> FoodItemAliases { get; init; } = [];
    public IReadOnlyList<CatalogEmbeddingSnapshot> CatalogEmbeddings { get; init; } = [];
    public IReadOnlyList<RecipeSnapshot> Recipes { get; init; } = [];
    public IReadOnlyList<RecipeIngredientSnapshot> RecipeIngredients { get; init; } = [];
    public IReadOnlyList<RecipeIngredientReplacementSnapshot> RecipeIngredientReplacements { get; init; } =
    [];
    public IReadOnlyList<RecipeStepSnapshot> RecipeSteps { get; init; } = [];
    public IReadOnlyList<RecipeStepTimerSnapshot> RecipeStepTimers { get; init; } = [];
    public IReadOnlyList<RecipeTagSnapshot> RecipeTags { get; init; } = [];
    public IReadOnlyList<RecipeCollectionSnapshot> RecipeCollections { get; init; } = [];
    public IReadOnlyList<RecipeAttachmentSnapshot> RecipeAttachments { get; init; } = [];
    public IReadOnlyList<RecipeEmbeddingSnapshot> RecipeEmbeddings { get; init; } = [];
    public IReadOnlyList<ShareLinkSnapshot> ShareLinks { get; init; } = [];
    public IReadOnlyList<IngestionJobSnapshot> IngestionJobs { get; init; } = [];
    public IReadOnlyList<MealPlanTemplateSnapshot> MealPlanTemplates { get; init; } = [];
    public IReadOnlyList<MealPlanTemplateItemSnapshot> MealPlanTemplateItems { get; init; } = [];
    public IReadOnlyList<MealPlanEntrySnapshot> MealPlanEntries { get; init; } = [];
    public IReadOnlyList<ShoppingListSnapshot> ShoppingLists { get; init; } = [];
    public IReadOnlyList<ShoppingListItemSnapshot> ShoppingListItems { get; init; } = [];
    public IReadOnlyList<ShoppingListItemSourceSnapshot> ShoppingListItemSources { get; init; } =
    [];
    public IReadOnlyList<CookLogSnapshot> CookLogs { get; init; } = [];
}

internal sealed record TagSnapshot(
    Guid Id,
    string Name,
    string NormalizedName,
    string Slug,
    TagCategory Category,
    int SortOrder,
    bool IsSystemSeeded,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset ModifiedAt,
    string ModifiedBy
);

internal sealed record CollectionSnapshot(
    Guid Id,
    string Name,
    string NormalizedName,
    string Slug,
    string? Description,
    int SortOrder,
    bool IsSystemSeeded,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset ModifiedAt,
    string ModifiedBy
);

internal sealed record FoodItemSnapshot(
    Guid Id,
    string Name,
    string NormalizedName,
    bool IsPantryStaple,
    FoodCategory? Category,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset ModifiedAt,
    string ModifiedBy
);

internal sealed record FoodItemAliasSnapshot(
    Guid Id,
    Guid FoodItemId,
    string Alias,
    string NormalizedAlias,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset ModifiedAt,
    string ModifiedBy
);

internal sealed record CatalogEmbeddingSnapshot(
    Guid Id,
    string ModelTag,
    string NormalizedText,
    int Dimensions,
    byte[] VectorBytes,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset ModifiedAt,
    string ModifiedBy
);

internal sealed record RecipeSnapshot(
    Guid Id,
    string Title,
    string Slug,
    string? Description,
    RecipeStatus Status,
    decimal BaseServings,
    string? YieldDisplay,
    int? PrepTimeMinutes,
    int? CookTimeMinutes,
    int? TotalTimeMinutes,
    RecipeDifficulty? Difficulty,
    string? SourceUrl,
    string? SourceName,
    string? SourceAuthor,
    string? SourceBookTitle,
    int? SourceBookPage,
    string? CoverImagePath,
    string? ThumbnailImagePath,
    string? ExtractedModel,
    string? RefinedModel,
    RecipeRefinementStatus RefinementStatus,
    DateTimeOffset? RefinementRequestedAt,
    string? RefinementStatusDetail,
    int TimesCooked,
    DateTimeOffset? LastCookedAt,
    decimal? AverageRating,
    int RatingCount,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset ModifiedAt,
    string ModifiedBy
);

internal sealed record RecipeIngredientSnapshot(
    Guid Id,
    Guid RecipeId,
    int SortOrder,
    string? SectionName,
    decimal? Quantity,
    decimal? MaxQuantity,
    MeasurementUnit? Unit,
    string? CustomUnitName,
    decimal? AlternativeQuantity,
    decimal? AlternativeMaxQuantity,
    MeasurementUnit? AlternativeUnit,
    string? AlternativeCustomUnitName,
    bool PreferAlternativeMeasurement,
    Guid? FoodItemId,
    Guid? LinkedRecipeId,
    string? UnresolvedRecipeTitle,
    string DisplayName,
    string? PreparationNote,
    bool IsOptional,
    string? OriginalRawText,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset ModifiedAt,
    string ModifiedBy
);

internal sealed record RecipeIngredientReplacementSnapshot(
    Guid Id,
    Guid RecipeIngredientId,
    int SortOrder,
    string DisplayName,
    decimal? Quantity,
    decimal? MaxQuantity,
    MeasurementUnit? Unit,
    string? CustomUnitName,
    Guid? FoodItemId,
    string? PreparationNote,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset ModifiedAt,
    string ModifiedBy
);

internal sealed record RecipeStepSnapshot(
    Guid Id,
    Guid RecipeId,
    int StepNumber,
    string? SectionName,
    string Instruction,
    string? ImagePath,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset ModifiedAt,
    string ModifiedBy
);

internal sealed record RecipeStepTimerSnapshot(
    Guid Id,
    Guid RecipeStepId,
    int SortOrder,
    int DurationSeconds,
    string Label,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset ModifiedAt,
    string ModifiedBy
);

internal sealed record RecipeTagSnapshot(
    Guid Id,
    Guid RecipeId,
    Guid TagId,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset ModifiedAt,
    string ModifiedBy
);

internal sealed record RecipeCollectionSnapshot(
    Guid Id,
    Guid RecipeId,
    Guid CollectionId,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset ModifiedAt,
    string ModifiedBy
);

internal sealed record RecipeAttachmentSnapshot(
    Guid Id,
    Guid RecipeId,
    int SortOrder,
    string FileName,
    string MediaUrlPath,
    string ContentType,
    long FileSizeBytes,
    string? Label,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset ModifiedAt,
    string ModifiedBy
);

internal sealed record RecipeEmbeddingSnapshot(
    Guid Id,
    Guid RecipeId,
    string ModelTag,
    string ContentHash,
    string EmbeddingText,
    int Dimensions,
    byte[] VectorBytes,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset ModifiedAt,
    string ModifiedBy
);

internal sealed record ShareLinkSnapshot(
    Guid Id,
    Guid RecipeId,
    string Token,
    string? Label,
    bool IncludeCookLogs,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    int ViewCount,
    DateTimeOffset? LastAccessedAt,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset ModifiedAt,
    string ModifiedBy
);

internal sealed record IngestionJobSnapshot(
    Guid Id,
    IngestionChannel Channel,
    IngestionJobStatus Status,
    string DisplayTitle,
    string? SourceUrl,
    string? RawText,
    string? ScrapedPageJson,
    string? NormalizedSourceText,
    string? DownloadedCoverImagePath,
    string? DownloadedThumbnailImagePath,
    string? SourceFilesJson,
    string? PdfInspectionJson,
    string? SourceName,
    string? SourceAuthor,
    string? SourceBookTitle,
    int? SourceBookPage,
    string? InitialCollectionIdsJson,
    bool AutoPublish,
    string? ExtractedDraftJson,
    string? ExtractedModel,
    string? RefinedModel,
    DateTimeOffset? RefinedAt,
    int RefinementAttemptCount,
    DateTimeOffset? RefinementNextRetryAt,
    decimal? ConfidenceScore,
    string? StatusDetail,
    int AttemptCount,
    DateTimeOffset? NextRetryAt,
    bool RunNowRequested,
    bool RefinementRunNowRequested,
    DateTimeOffset? ProcessedAt,
    Guid? ResultingRecipeId,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset ModifiedAt,
    string ModifiedBy
);

internal sealed record MealPlanTemplateSnapshot(
    Guid Id,
    string Name,
    string NormalizedName,
    string? Description,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset ModifiedAt,
    string ModifiedBy
);

internal sealed record MealPlanTemplateItemSnapshot(
    Guid Id,
    Guid TemplateId,
    bool IsOnDeck,
    int? DayOffset,
    MealSlot? Slot,
    int SortOrder,
    MealPlanEntryType EntryType,
    Guid? RecipeId,
    decimal? TargetServings,
    bool IsLeftovers,
    string? CustomTitle,
    string? Notes,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset ModifiedAt,
    string ModifiedBy
);

internal sealed record MealPlanEntrySnapshot(
    Guid Id,
    DateOnly WeekStartDate,
    bool IsOnDeck,
    DateOnly? ScheduledDate,
    MealSlot? Slot,
    int SortOrder,
    MealPlanEntryType EntryType,
    Guid? RecipeId,
    decimal? TargetServings,
    bool IsLeftovers,
    Guid? SourceMealPlanEntryId,
    string? CustomTitle,
    string? Notes,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset ModifiedAt,
    string ModifiedBy
);

internal sealed record ShoppingListSnapshot(
    Guid Id,
    string Name,
    string NormalizedName,
    bool IsPinned,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset ModifiedAt,
    string ModifiedBy
);

internal sealed record ShoppingListItemSnapshot(
    Guid Id,
    Guid ShoppingListId,
    Guid? FoodItemId,
    string DisplayName,
    decimal? Quantity,
    decimal? MaxQuantity,
    MeasurementUnit? Unit,
    string? CustomUnitName,
    MeasurementDimension Dimension,
    string? Notes,
    bool IsPantryStapleExcluded,
    bool IsChecked,
    DateTimeOffset? CheckedAt,
    Guid? CheckedByUserId,
    string? CheckedByDisplayName,
    int SortOrder,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset ModifiedAt,
    string ModifiedBy
);

internal sealed record ShoppingListItemSourceSnapshot(
    Guid Id,
    Guid ShoppingListItemId,
    ShoppingListSourceKind SourceKind,
    Guid? MealPlanEntryId,
    Guid? RecipeId,
    string SourceLabel,
    DateOnly? ScheduledDate,
    decimal? ContributedQuantity,
    decimal? ContributedMaxQuantity,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset ModifiedAt,
    string ModifiedBy
);

internal sealed record CookLogSnapshot(
    Guid Id,
    Guid RecipeId,
    Guid? CookedByUserId,
    string CookedByDisplayName,
    DateTimeOffset CookedAt,
    decimal? ServingsMade,
    int? Rating,
    string? Notes,
    string? PhotoImagePath,
    string? PhotoThumbnailPath,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset ModifiedAt,
    string ModifiedBy
);

using System.ComponentModel.DataAnnotations;

namespace App.Core.Recipes;

public record RecipeCollectionBadgeDto(Guid Id, string Name, string Slug);

public record RecipeTagBadgeDto(Guid Id, string Name, string Slug, TagCategory Category);

public record RecipeSummaryDto(
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
    string? SourceName,
    string? ThumbnailImagePath,
    int IngredientCount,
    int StepCount,
    int TimesCooked,
    decimal? AverageRating,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt,
    IReadOnlyList<RecipeCollectionBadgeDto>? Collections = null,
    IReadOnlyList<RecipeTagBadgeDto>? Tags = null,
    DateTimeOffset? LastCookedAt = null,
    int RatingCount = 0,
    string? LatestCookNote = null
)
{
    public IReadOnlyList<RecipeCollectionBadgeDto> Collections { get; init; } = Collections ?? [];
    public IReadOnlyList<RecipeTagBadgeDto> Tags { get; init; } = Tags ?? [];
}

public record SubRecipePreviewDto(
    Guid Id,
    string Title,
    string Slug,
    decimal BaseServings,
    string? YieldDisplay,
    IReadOnlyList<RecipeIngredientDto> Ingredients,
    IReadOnlyList<RecipeStepDto> Steps
);

public record RecipeIngredientDto(
    Guid Id,
    int SortOrder,
    string? SectionName,
    decimal? Quantity,
    decimal? MaxQuantity,
    MeasurementUnit? Unit,
    string? CustomUnitName,
    Guid? FoodItemId,
    string? FoodItemName,
    bool IsPantryStaple,
    Guid? LinkedRecipeId,
    string? LinkedRecipeTitle,
    string? LinkedRecipeSlug,
    string DisplayName,
    string? PreparationNote,
    bool IsOptional,
    string? OriginalRawText,
    RecipeMeasurement? AlternativeMeasurement = null,
    bool PreferAlternativeMeasurement = false,
    string? UnresolvedRecipeTitle = null
);

public record RecipeStepTimerDto(Guid Id, int SortOrder, int DurationSeconds, string Label);

public record RecipeStepDto(
    Guid Id,
    int StepNumber,
    string? SectionName,
    string Instruction,
    string? ImagePath,
    IReadOnlyList<Guid> IngredientIds,
    IReadOnlyList<RecipeStepTimerDto> Timers,
    IReadOnlyList<RecipeStepIngredientAmountDto>? IngredientAmounts = null
);

public record DetectedStepAmountDto(
    int Start,
    int Length,
    string Text,
    RecipeMeasurement Measurement,
    RecipeMeasurement? AlternativeMeasurement
);

public record DetectStepAmountsRequest([Required, MaxLength(4000)] string Instruction);

public record RecipeAttachmentDto(
    Guid Id,
    int SortOrder,
    string FileName,
    string MediaUrlPath,
    string ContentType,
    long FileSizeBytes,
    string? Label,
    DateTimeOffset CreatedAt
);

public record RecipeDetailDto(
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
    int TimesCooked,
    DateTimeOffset? LastCookedAt,
    decimal? AverageRating,
    IReadOnlyList<RecipeIngredientDto> Ingredients,
    IReadOnlyList<RecipeStepDto> Steps,
    IReadOnlyList<SubRecipePreviewDto> LinkedSubRecipes,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset ModifiedAt,
    string ModifiedBy,
    IReadOnlyList<RecipeCollectionBadgeDto>? Collections = null,
    IReadOnlyList<RecipeTagBadgeDto>? Tags = null,
    IReadOnlyList<RecipeAttachmentDto>? Attachments = null,
    int RatingCount = 0,
    IReadOnlyList<CookLogDto>? CookLogs = null,
    string? ExtractedModel = null,
    Guid? IngestionJobId = null
)
{
    public IReadOnlyList<RecipeCollectionBadgeDto> Collections { get; init; } = Collections ?? [];
    public IReadOnlyList<RecipeTagBadgeDto> Tags { get; init; } = Tags ?? [];
    public IReadOnlyList<RecipeAttachmentDto> Attachments { get; init; } = Attachments ?? [];
    public IReadOnlyList<CookLogDto> CookLogs { get; init; } = CookLogs ?? [];
}

public record RecipeQueryParameters(
    string? Search = null,
    RecipeStatus? Status = null,
    RecipeDifficulty? Difficulty = null,
    int? MaxTotalTimeMinutes = null,
    Guid? FoodItemId = null,
    Guid? CollectionId = null,
    Guid[]? TagIds = null,
    decimal? MinRating = null,
    bool ForgottenFavoritesOnly = false,
    int ForgottenDaysThreshold = 60,
    int? Page = null,
    int? PageSize = null,
    bool IncludeTotalCount = true,
    bool FuzzySearch = false
);

public record RecipePagedResultDto(
    IReadOnlyList<RecipeSummaryDto> Items,
    int? TotalCount,
    int PageNumber,
    int PageSize,
    bool HasMore,
    bool IsFuzzySearch = false
)
{
    public int? TotalPages =>
        TotalCount.HasValue && PageSize > 0
            ? (int)Math.Ceiling((double)TotalCount.Value / PageSize)
            : null;

    public bool HasPreviousPage => PageNumber > 1;

    public bool HasNextPage => HasMore;
}

public record RecipeIngredientInput(
    [MaxLength(150)] string? SectionName = null,
    decimal? Quantity = null,
    decimal? MaxQuantity = null,
    MeasurementUnit? Unit = null,
    [MaxLength(50)] string? CustomUnitName = null,
    Guid? FoodItemId = null,
    Guid? LinkedRecipeId = null,
    [Required, MaxLength(200)] string DisplayName = "",
    [MaxLength(200)] string? PreparationNote = null,
    bool IsOptional = false,
    [MaxLength(500)] string? OriginalRawText = null,
    RecipeMeasurement? AlternativeMeasurement = null,
    bool PreferAlternativeMeasurement = false,
    [MaxLength(200)] string? UnresolvedRecipeTitle = null
);

public record RecipeStepTimerInput(
    [Range(1, 864000)] int DurationSeconds = 60,
    [Required, MaxLength(150)] string Label = ""
);

public record RecipeStepInput(
    [MaxLength(150)] string? SectionName = null,
    [Required, MaxLength(4000)] string Instruction = "",
    IReadOnlyList<int>? IngredientIndices = null,
    IReadOnlyList<RecipeStepTimerInput>? Timers = null,
    [MaxLength(500)] string? ImagePath = null,
    IReadOnlyList<RecipeStepIngredientAmountInput>? IngredientAmounts = null
);

public record UpsertRecipeRequest(
    [Required, MaxLength(200)] string Title,
    [MaxLength(2000)] string? Description = null,
    RecipeStatus Status = RecipeStatus.Published,
    [Range(typeof(decimal), "0.25", "999")] decimal BaseServings = 4m,
    [MaxLength(100)] string? YieldDisplay = null,
    [Range(0, 10080)] int? PrepTimeMinutes = null,
    [Range(0, 10080)] int? CookTimeMinutes = null,
    [Range(0, 10080)] int? TotalTimeMinutes = null,
    RecipeDifficulty? Difficulty = null,
    [MaxLength(2048)] string? SourceUrl = null,
    [MaxLength(200)] string? SourceName = null,
    [MaxLength(200)] string? SourceAuthor = null,
    [MaxLength(200)] string? SourceBookTitle = null,
    [Range(1, 10000)] int? SourceBookPage = null,
    IReadOnlyList<RecipeIngredientInput>? Ingredients = null,
    IReadOnlyList<RecipeStepInput>? Steps = null,
    [MaxLength(500)] string? CoverImagePath = null,
    [MaxLength(500)] string? ThumbnailImagePath = null,
    IReadOnlyList<Guid>? CollectionIds = null,
    IReadOnlyList<Guid>? TagIds = null,
    [MaxLength(200)] string? ExtractedModel = null
);

public record UpdateRecipeOrganizationRequest(
    IReadOnlyList<Guid>? CollectionIds = null,
    IReadOnlyList<Guid>? TagIds = null
);

public record IngredientMatchRequest([Required] IReadOnlyList<Guid> FoodItemIds);

public record IngredientMatchResultDto(
    RecipeSummaryDto Recipe,
    int MatchedCount,
    int SelectedOnHandCount,
    int AdditionalNonStapleCount,
    int PantryStaplesUsedCount,
    IReadOnlyList<string> MatchedIngredientNames,
    IReadOnlyList<string> MissingNonStapleNames
);

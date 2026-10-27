using System.ComponentModel.DataAnnotations;
using App.Core.FoodCatalog;
using App.Core.Recipes;

namespace App.Core.Mcp;

public sealed record McpRecipeSearchRequest(
    string? Query = null,
    string? Tag = null,
    string? Collection = null,
    int? MaxTotalTimeMinutes = null,
    decimal? MinRating = null,
    RecipeDifficulty? Difficulty = null,
    bool ForgottenFavoritesOnly = false,
    int ForgottenDaysThreshold = 60,
    bool IncludeDrafts = false,
    int Limit = 25
);

public sealed record McpCookHistoryNoteDto(
    DateTimeOffset CookedAt,
    string CookedBy,
    int? Rating,
    string? Notes
);

public sealed record McpStepIngredientCalloutDto(
    Guid IngredientId,
    string DisplayName,
    decimal? Quantity,
    decimal? MaxQuantity,
    MeasurementUnit? Unit,
    string? CustomUnitName,
    string? PreparationNote
);

public sealed record McpRecipeStepDetailDto(
    int StepNumber,
    string? SectionName,
    string Instruction,
    IReadOnlyList<McpStepIngredientCalloutDto> StepIngredients,
    IReadOnlyList<RecipeStepTimerDto> Timers
);

public sealed record McpRecipeDetailDto(
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
    IReadOnlyList<McpRecipeStepDetailDto> Steps,
    IReadOnlyList<SubRecipePreviewDto> LinkedSubRecipes,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset ModifiedAt,
    string ModifiedBy,
    IReadOnlyList<RecipeCollectionBadgeDto> Collections,
    IReadOnlyList<RecipeTagBadgeDto> Tags,
    IReadOnlyList<RecipeAttachmentDto> Attachments,
    IReadOnlyList<McpCookHistoryNoteDto> CookHistoryNotes
);

public sealed record McpResolvedFoodItemDto(
    string InputName,
    Guid FoodItemId,
    string CanonicalName,
    bool IsPantryStaple
);

public sealed record McpIngredientMatchResponseDto(
    IReadOnlyList<McpResolvedFoodItemDto> ResolvedIngredients,
    IReadOnlyList<string> UnresolvedIngredientNames,
    IReadOnlyList<IngredientMatchResultDto> Matches
);

public sealed record McpHouseholdTaxonomyDto(
    IReadOnlyList<CollectionDto> Collections,
    IReadOnlyList<TagDto> Tags,
    IReadOnlyList<StoreAisleDto> StoreAisles,
    IReadOnlyList<string> SupportedUnits
);

public sealed record McpEnqueueIngestionRequest(
    string? Url = null,
    string? RawText = null,
    string? TitleHint = null
);

public sealed record McpStructuredIngredientInput(
    [Required, MaxLength(200)] string DisplayName,
    [MaxLength(150)] string? CanonicalFoodName = null,
    [MaxLength(150)] string? SectionName = null,
    decimal? Quantity = null,
    decimal? MaxQuantity = null,
    [MaxLength(50)] string? Unit = null,
    [MaxLength(200)] string? PreparationNote = null,
    bool IsOptional = false,
    [MaxLength(500)] string? OriginalRawText = null,
    [MaxLength(100)] string? SuggestedAisleName = null,
    bool IsPantryStaple = false
);

public sealed record McpStructuredStepInput(
    [Required, MaxLength(4000)] string Instruction,
    [MaxLength(150)] string? SectionName = null,
    IReadOnlyList<int>? IngredientIndices = null,
    IReadOnlyList<RecipeStepTimerInput>? Timers = null
);

public sealed record McpSaveStructuredRecipeRequest(
    [Required, MaxLength(200)] string Title,
    Guid? RecipeId = null,
    [MaxLength(2000)] string? Description = null,
    bool PublishImmediately = false,
    [Range(typeof(decimal), "0.25", "999")] decimal BaseServings = 4m,
    [MaxLength(100)] string? YieldDisplay = null,
    [Range(0, 10080)] int? PrepTimeMinutes = null,
    [Range(0, 10080)] int? CookTimeMinutes = null,
    [Range(0, 10080)] int? TotalTimeMinutes = null,
    [MaxLength(50)] string? Difficulty = null,
    [MaxLength(2048)] string? SourceUrl = null,
    [MaxLength(200)] string? SourceName = null,
    [MaxLength(200)] string? SourceAuthor = null,
    [MaxLength(200)] string? SourceBookTitle = null,
    [Range(1, 10000)] int? SourceBookPage = null,
    IReadOnlyList<McpStructuredIngredientInput>? Ingredients = null,
    IReadOnlyList<McpStructuredStepInput>? Steps = null,
    IReadOnlyList<string>? Collections = null,
    IReadOnlyList<string>? Tags = null
);

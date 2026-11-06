using System.ComponentModel.DataAnnotations;
using App.Core.MealPlans;
using App.Core.Recipes;
using App.Core.Shopping;

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
    bool IsPantryStaple = false,
    RecipeMeasurement? AlternativeMeasurement = null,
    bool PreferAlternativeMeasurement = false
);

public sealed record McpStructuredStepInput(
    [Required, MaxLength(4000)] string Instruction,
    [MaxLength(150)] string? SectionName = null,
    IReadOnlyList<int>? IngredientIndices = null,
    IReadOnlyList<RecipeStepTimerInput>? Timers = null,
    IReadOnlyList<RecipeStepIngredientAmountInput>? IngredientAmounts = null
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

public sealed record McpGetMealPlanRequest(
    string? StartDate = null,
    string? EndDate = null,
    bool IncludeOnDeck = true
);

public sealed record McpPlanMealItemInput(
    string? EntryType = "Recipe",
    bool IsOnDeck = false,
    string? ScheduledDate = null,
    string? WeekStartDate = null,
    string? Slot = null,
    Guid? RecipeId = null,
    [MaxLength(220)] string? RecipeSlug = null,
    [MaxLength(200)] string? RecipeTitle = null,
    [Range(typeof(decimal), "0.25", "999")] decimal? TargetServings = null,
    Guid? SourceMealPlanEntryId = null,
    [MaxLength(200)] string? CustomTitle = null,
    [MaxLength(500)] string? Notes = null
);

public sealed record McpPlanMealsRequest(IReadOnlyList<McpPlanMealItemInput> Entries);

public sealed record McpPlanMealsResultDto(
    int PlannedCount,
    IReadOnlyList<MealPlanEntryDto> Entries
);

public sealed record McpRemoveMealPlanEntryRequest(
    Guid EntryId,
    string? RescheduleToDate = null,
    string? RescheduleToSlot = null,
    bool MoveToOnDeck = false,
    string? WeekStartDate = null
);

public sealed record McpRemoveOrRescheduleMealResultDto(
    string Action,
    Guid EntryId,
    bool Removed,
    MealPlanEntryDto? RescheduledEntry
);

public sealed record McpGetShoppingListsRequest(
    Guid? ShoppingListId = null,
    [MaxLength(150)] string? ListName = null,
    bool IncludeItems = true
);

public sealed record McpShoppingListsResponseDto(
    IReadOnlyList<ShoppingListSummaryDto> Summaries,
    IReadOnlyList<ShoppingListDetailDto> Lists
);

public sealed record McpGenerateShoppingListRequest(
    [Required] string StartDate,
    [Required] string EndDate,
    Guid? ShoppingListId = null,
    [MaxLength(150)] string? ListName = null,
    bool CreateListIfMissing = true,
    bool IncludeOnDeck = true,
    bool IncludeSubRecipes = true,
    bool IncludeOptionalIngredients = true,
    string DuplicatePolicy = "SkipAlreadyImported",
    IReadOnlyList<Guid>? PromotePantryFoodItemIds = null
);

public sealed record McpShoppingListItemUpsertInput(
    [Required, MaxLength(200)] string DisplayName,
    Guid? ItemId = null,
    decimal? Quantity = null,
    decimal? MaxQuantity = null,
    [MaxLength(60)] string? Unit = null,
    Guid? FoodItemId = null,
    [MaxLength(300)] string? Notes = null,
    bool ConsolidateIfCompatible = true
);

public sealed record McpUpsertShoppingListItemsRequest(
    IReadOnlyList<McpShoppingListItemUpsertInput> Items,
    Guid? ShoppingListId = null,
    [MaxLength(150)] string? ListName = null,
    bool CreateListIfMissing = true
);

public sealed record McpUpsertShoppingItemsResultDto(
    ShoppingListDetailDto ShoppingList,
    IReadOnlyList<ShoppingListItemDto> UpsertedItems
);

public sealed record McpSetShoppingItemCheckedRequest(
    bool IsChecked = true,
    Guid? ShoppingListId = null,
    [MaxLength(150)] string? ListName = null,
    Guid? ItemId = null,
    [MaxLength(200)] string? ItemName = null
);

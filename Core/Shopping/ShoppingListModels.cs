using System.ComponentModel.DataAnnotations;
using App.Core.FoodCatalog;
using App.Core.Recipes;

namespace App.Core.Shopping;

public record ShoppingListSummaryDto(
    Guid Id,
    string Name,
    bool IsPinned,
    int ActiveUncheckedCount,
    int CheckedCount,
    int ExcludedPantryCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt
);

public record ShoppingListItemSourceDto(
    Guid Id,
    ShoppingListSourceKind SourceKind,
    Guid? MealPlanEntryId,
    Guid? RecipeId,
    string SourceLabel,
    DateOnly? ScheduledDate,
    decimal? ContributedQuantity,
    decimal? ContributedMaxQuantity
);

public record ShoppingListItemDto(
    Guid Id,
    Guid ShoppingListId,
    Guid? FoodItemId,
    string? FoodItemName,
    string DisplayName,
    decimal? Quantity,
    decimal? MaxQuantity,
    MeasurementUnit? Unit,
    string? CustomUnitName,
    string UnitLabel,
    string FormattedAmount,
    MeasurementDimension Dimension,
    string? Notes,
    bool IsPantryStapleExcluded,
    bool IsChecked,
    DateTimeOffset? CheckedAt,
    Guid? CheckedByUserId,
    string? CheckedByDisplayName,
    int SortOrder,
    IReadOnlyList<ShoppingListItemSourceDto> Sources,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt,
    FoodCategory Category = FoodCategory.Other
);

public record ShoppingListDetailDto(
    Guid Id,
    string Name,
    bool IsPinned,
    IReadOnlyList<ShoppingListItemDto> ActiveItems,
    IReadOnlyList<ShoppingListItemDto> CheckedItems,
    IReadOnlyList<ShoppingListItemDto> ExcludedPantryItems,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt
);

public record CreateShoppingListRequest(
    [Required, MaxLength(150)] string Name,
    bool IsPinned = false
);

public record UpdateShoppingListRequest(
    [Required, MaxLength(150)] string Name,
    bool IsPinned = false
);

public record CreateShoppingListItemRequest(
    [Required, MaxLength(200)] string DisplayName,
    decimal? Quantity = null,
    decimal? MaxQuantity = null,
    MeasurementUnit? Unit = null,
    [MaxLength(60)] string? CustomUnitName = null,
    Guid? FoodItemId = null,
    [MaxLength(300)] string? Notes = null,
    bool ConsolidateIfCompatible = true
);

public record UpdateShoppingListItemRequest(
    [Required, MaxLength(200)] string DisplayName,
    decimal? Quantity = null,
    decimal? MaxQuantity = null,
    MeasurementUnit? Unit = null,
    [MaxLength(60)] string? CustomUnitName = null,
    [MaxLength(300)] string? Notes = null,
    bool IsPantryStapleExcluded = false
);

public record SetShoppingListItemCheckedRequest(bool IsChecked);

public record ClearCheckedItemsResultDto(int RemovedCount);

public record AlreadyImportedMealDto(
    Guid MealPlanEntryId,
    string DisplayTitle,
    DateOnly? ScheduledDate,
    bool IsOnDeck
);

public record GenerateFromMealPlanRequest(
    DateOnly StartDate,
    DateOnly EndDate,
    bool IncludeOnDeck = true,
    bool IncludeSubRecipes = true,
    bool IncludeOptionalIngredients = true,
    DuplicateMealPolicy DuplicatePolicy = DuplicateMealPolicy.SkipAlreadyImported,
    IReadOnlyList<Guid>? PromotePantryFoodItemIds = null
);

public record ShoppingListCandidateItemDto(
    Guid? FoodItemId,
    string DisplayName,
    bool IsPantryStaple,
    decimal? Quantity,
    decimal? MaxQuantity,
    MeasurementUnit? Unit,
    string? CustomUnitName,
    string UnitLabel,
    string FormattedAmount,
    MeasurementDimension Dimension,
    string? Notes,
    IReadOnlyList<string> SourceLabels
);

public record ShoppingListGenerationPreviewDto(
    DateOnly StartDate,
    DateOnly EndDate,
    int EligibleMealsCount,
    int NewMealsToImportCount,
    int IgnoredLeftoversCount,
    int IgnoredNotesCount,
    IReadOnlyList<AlreadyImportedMealDto> AlreadyImportedMeals,
    IReadOnlyList<ShoppingListCandidateItemDto> ActiveCandidateItems,
    IReadOnlyList<ShoppingListCandidateItemDto> ExcludedPantryCandidateItems
);

public record AddRecipeToShoppingListRequest(
    Guid RecipeId,
    decimal? TargetServings = null,
    bool IncludeSubRecipes = true,
    bool IncludeOptionalIngredients = true,
    IReadOnlyList<Guid>? PromotePantryFoodItemIds = null
);

public record ShoppingListGenerationResultDto(
    ShoppingListDetailDto ShoppingList,
    int ImportedMealsCount,
    int SkippedDuplicateMealsCount,
    int IgnoredLeftoversCount,
    int IgnoredNotesCount,
    int ActiveItemsAffectedCount,
    int ExcludedPantryItemsCount
);

public record ShoppingListRealtimeEventDto(
    ShoppingRealtimeEventType EventType,
    Guid ShoppingListId,
    ShoppingListDetailDto? ShoppingList,
    ShoppingListItemDto? ChangedItem,
    Guid? DeletedItemId,
    Guid? ActorUserId,
    string? ActorDisplayName,
    DateTimeOffset OccurredAt
);

public record ShoppingListsCatalogChangedDto(
    ShoppingRealtimeEventType EventType,
    Guid AffectedShoppingListId,
    IReadOnlyList<ShoppingListSummaryDto> Lists,
    DateTimeOffset OccurredAt
);

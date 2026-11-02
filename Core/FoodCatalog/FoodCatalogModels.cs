using System.ComponentModel.DataAnnotations;

namespace App.Core.FoodCatalog;

public record StoreAisleDto(Guid Id, string Name, int SortOrder, bool IsSystemSeeded);

public record FoodItemDto(
    Guid Id,
    string Name,
    Guid? DefaultStoreAisleId,
    string? DefaultStoreAisleName,
    int? DefaultStoreAisleSortOrder,
    bool IsPantryStaple,
    IReadOnlyList<string> Aliases,
    int ReferencedRecipeCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt,
    int ReferencedShoppingListItemCount = 0
);

public record FoodItemQueryParameters(
    string? Search = null,
    Guid? StoreAisleId = null,
    bool? IsPantryStaple = null
);

public record CreateFoodItemRequest(
    [Required, MaxLength(150)] string Name,
    Guid? DefaultStoreAisleId = null,
    bool IsPantryStaple = false,
    IReadOnlyList<string>? Aliases = null
);

public record UpdateFoodItemRequest(
    [Required, MaxLength(150)] string Name,
    Guid? DefaultStoreAisleId = null,
    bool IsPantryStaple = false,
    IReadOnlyList<string>? Aliases = null
);

public record MergeFoodItemsRequest(
    Guid TargetFoodItemId,
    [Required, MinLength(1)] IReadOnlyList<Guid> SourceFoodItemIds,
    bool KeepSourceNamesAsAliases = true
);

public record MergeFoodItemsResultDto(
    FoodItemDto TargetFoodItem,
    int MergedSourceCount,
    int RepointedRecipeIngredientCount,
    int RepointedShoppingListItemCount,
    int ConsolidatedShoppingListItemCount,
    IReadOnlyList<string> AddedAliases
);

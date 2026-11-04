using System.ComponentModel.DataAnnotations;

namespace App.Core.FoodCatalog;

public record FoodItemDto(
    Guid Id,
    string Name,
    bool IsPantryStaple,
    IReadOnlyList<string> Aliases,
    int ReferencedRecipeCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt,
    int ReferencedShoppingListItemCount = 0
);

public record FoodItemQueryParameters(string? Search = null, bool? IsPantryStaple = null);

public record CreateFoodItemRequest(
    [Required, MaxLength(150)] string Name,
    bool IsPantryStaple = false,
    IReadOnlyList<string>? Aliases = null
);

public record UpdateFoodItemRequest(
    [Required, MaxLength(150)] string Name,
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

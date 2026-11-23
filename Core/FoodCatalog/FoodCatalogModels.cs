using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using App.Core.Recipes;

namespace App.Core.FoodCatalog;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FoodItemSortBy
{
    Name = 0,
    RecipeCount = 1,
    CreatedAt = 2,
    ModifiedAt = 3,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FoodItemSortDirection
{
    Asc = 0,
    Desc = 1,
}

public record FoodItemDto(
    Guid Id,
    string Name,
    bool IsPantryStaple,
    IReadOnlyList<string> Aliases,
    int ReferencedRecipeCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt,
    int ReferencedShoppingListItemCount = 0,
    FoodCategory Category = FoodCategory.Other
);

public record FoodItemQueryParameters(
    string? Search = null,
    bool? IsPantryStaple = null,
    int? Page = null,
    int? PageSize = null,
    FoodCategory? Category = null,
    FoodItemSortBy? SortBy = null,
    FoodItemSortDirection? SortDirection = null
);

public record CreateFoodItemRequest(
    [Required, MaxLength(150)] string Name,
    bool IsPantryStaple = false,
    IReadOnlyList<string>? Aliases = null,
    FoodCategory? Category = null
);

public record UpdateFoodItemRequest(
    [Required, MaxLength(150)] string Name,
    bool IsPantryStaple = false,
    IReadOnlyList<string>? Aliases = null,
    FoodCategory? Category = null
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

public record FoodItemRecipeUsageDto(
    Guid RecipeId,
    string RecipeTitle,
    string RecipeSlug,
    string? ThumbnailImagePath,
    RecipeStatus Status,
    IReadOnlyList<string> IngredientDisplayNames
);

public record UnlinkFoodItemRecipesResultDto(
    Guid FoodItemId,
    int UnlinkedRecipeCount,
    int UnlinkedIngredientCount
);

public record BulkDeleteFoodItemsRequest(
    [Required, MinLength(1), MaxLength(200)] IReadOnlyList<Guid> FoodItemIds,
    bool UnlinkFromRecipes = false
);

public record BulkDeleteFoodItemsResultDto(
    int DeletedCount,
    int UnlinkedRecipeCount,
    int UnlinkedIngredientCount,
    int UnlinkedShoppingListItemCount
);

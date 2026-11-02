namespace App.Core.Shopping;

public interface IShoppingListService
{
    Task<IReadOnlyList<ShoppingListSummaryDto>> GetShoppingListsAsync(
        CancellationToken cancellationToken = default
    );

    Task<ShoppingListDetailDto?> GetShoppingListByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default
    );

    Task<ShoppingListDetailDto> CreateShoppingListAsync(
        CreateShoppingListRequest request,
        CancellationToken cancellationToken = default
    );

    Task<ShoppingListDetailDto?> UpdateShoppingListAsync(
        Guid id,
        UpdateShoppingListRequest request,
        CancellationToken cancellationToken = default
    );

    Task<bool> DeleteShoppingListAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ShoppingListItemDto?> AddItemAsync(
        Guid shoppingListId,
        CreateShoppingListItemRequest request,
        CancellationToken cancellationToken = default
    );

    Task<ShoppingListItemDto?> UpdateItemAsync(
        Guid shoppingListId,
        Guid itemId,
        UpdateShoppingListItemRequest request,
        CancellationToken cancellationToken = default
    );

    Task<ShoppingListItemDto?> SetItemCheckedAsync(
        Guid shoppingListId,
        Guid itemId,
        SetShoppingListItemCheckedRequest request,
        CancellationToken cancellationToken = default
    );

    Task<ShoppingListItemDto?> PromotePantryStapleItemAsync(
        Guid shoppingListId,
        Guid itemId,
        CancellationToken cancellationToken = default
    );

    Task<bool> DeleteItemAsync(
        Guid shoppingListId,
        Guid itemId,
        CancellationToken cancellationToken = default
    );

    Task<ClearCheckedItemsResultDto?> ClearCheckedItemsAsync(
        Guid shoppingListId,
        CancellationToken cancellationToken = default
    );

    Task<ShoppingListGenerationPreviewDto?> PreviewGenerateFromMealPlanAsync(
        Guid shoppingListId,
        GenerateFromMealPlanRequest request,
        CancellationToken cancellationToken = default
    );

    Task<ShoppingListGenerationResultDto?> GenerateFromMealPlanAsync(
        Guid shoppingListId,
        GenerateFromMealPlanRequest request,
        CancellationToken cancellationToken = default
    );

    Task<ShoppingListGenerationResultDto?> AddRecipeIngredientsAsync(
        Guid shoppingListId,
        AddRecipeToShoppingListRequest request,
        CancellationToken cancellationToken = default
    );
}

using App.Core.Common;

namespace App.Core.FoodCatalog;

public interface IFoodCatalogService
{
    Task<IReadOnlyList<FoodItemDto>> GetFoodItemsAsync(
        FoodItemQueryParameters query,
        CancellationToken cancellationToken = default
    );

    Task<PagedResultDto<FoodItemDto>> GetFoodItemsPagedAsync(
        FoodItemQueryParameters query,
        CancellationToken cancellationToken = default
    );

    Task<FoodItemDto?> GetFoodItemByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<FoodItemDto> CreateFoodItemAsync(
        CreateFoodItemRequest request,
        CancellationToken cancellationToken = default
    );

    Task<FoodItemDto?> UpdateFoodItemAsync(
        Guid id,
        UpdateFoodItemRequest request,
        CancellationToken cancellationToken = default
    );

    Task<bool> DeleteFoodItemAsync(
        Guid id,
        bool unlinkFromRecipes = false,
        CancellationToken cancellationToken = default
    );

    Task<MergeFoodItemsResultDto> MergeFoodItemsAsync(
        MergeFoodItemsRequest request,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<FoodItemRecipeUsageDto>> GetFoodItemRecipesAsync(
        Guid foodItemId,
        CancellationToken cancellationToken = default
    );

    Task<PagedResultDto<FoodItemRecipeUsageDto>> GetFoodItemRecipesPagedAsync(
        Guid foodItemId,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default
    );

    Task<UnlinkFoodItemRecipesResultDto> UnlinkFoodItemRecipesAsync(
        Guid foodItemId,
        CancellationToken cancellationToken = default
    );
}

namespace App.Core.FoodCatalog;

public interface IFoodCatalogService
{
    Task<IReadOnlyList<FoodItemDto>> GetFoodItemsAsync(
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

    Task<bool> DeleteFoodItemAsync(Guid id, CancellationToken cancellationToken = default);

    Task<MergeFoodItemsResultDto> MergeFoodItemsAsync(
        MergeFoodItemsRequest request,
        CancellationToken cancellationToken = default
    );
}

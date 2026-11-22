using App.Core.Common;
using App.Core.FoodCatalog;

namespace App.Core.Mcp;

public interface IMcpFoodCatalogService
{
    Task<PagedResultDto<FoodItemDto>> ListFoodItemsAsync(
        McpListFoodItemsRequest request,
        CancellationToken cancellationToken = default
    );

    Task<FoodItemDto> CreateFoodItemAsync(
        string name,
        bool isPantryStaple = false,
        string? category = null,
        IReadOnlyList<string>? aliases = null,
        CancellationToken cancellationToken = default
    );

    Task<McpUpdateFoodItemsResultDto> UpdateFoodItemsAsync(
        McpUpdateFoodItemsRequest request,
        CancellationToken cancellationToken = default
    );

    Task<MergeFoodItemsResultDto> MergeFoodItemsAsync(
        MergeFoodItemsRequest request,
        CancellationToken cancellationToken = default
    );
}

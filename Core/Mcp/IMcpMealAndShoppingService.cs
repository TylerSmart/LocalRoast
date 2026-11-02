using App.Core.MealPlans;
using App.Core.Shopping;

namespace App.Core.Mcp;

public interface IMcpMealAndShoppingService
{
    Task<MealPlanRangeResponseDto> GetMealPlanAsync(
        McpGetMealPlanRequest request,
        CancellationToken cancellationToken = default
    );

    Task<McpPlanMealsResultDto> PlanMealsAsync(
        McpPlanMealsRequest request,
        CancellationToken cancellationToken = default
    );

    Task<McpRemoveOrRescheduleMealResultDto> RemoveOrRescheduleMealPlanEntryAsync(
        McpRemoveMealPlanEntryRequest request,
        CancellationToken cancellationToken = default
    );

    Task<McpShoppingListsResponseDto> GetShoppingListsAsync(
        McpGetShoppingListsRequest request,
        CancellationToken cancellationToken = default
    );

    Task<ShoppingListGenerationResultDto> GenerateShoppingListFromMealPlanAsync(
        McpGenerateShoppingListRequest request,
        CancellationToken cancellationToken = default
    );

    Task<McpUpsertShoppingItemsResultDto> UpsertShoppingListItemsAsync(
        McpUpsertShoppingListItemsRequest request,
        CancellationToken cancellationToken = default
    );

    Task<ShoppingListItemDto> SetShoppingListItemCheckedAsync(
        McpSetShoppingItemCheckedRequest request,
        CancellationToken cancellationToken = default
    );
}

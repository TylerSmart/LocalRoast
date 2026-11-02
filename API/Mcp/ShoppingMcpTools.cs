using System.ComponentModel;
using System.Security.Claims;
using App.Core.Mcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace App.API.Mcp;

[McpServerToolType]
public sealed class ShoppingMcpTools(IMcpMealAndShoppingService mcpMealAndShoppingService)
{
    [McpServerTool(Name = "get_shopping_lists", ReadOnly = true)]
    [Description(
        "Get household shopping lists with active checklist items, checked off items, and excluded pantry staples."
    )]
    public Task<CallToolResult> GetShoppingListsAsync(
        [Description("Optional specific shopping list ID (Guid)")] Guid? shoppingListId = null,
        [Description("Optional specific shopping list name (e.g. 'Costco', 'Weekly Groceries')")]
            string? listName = null,
        [Description("Include all checklist items (default true)")] bool includeItems = true,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default
    )
    {
        return McpToolResultHelper.ExecuteAsync(
            user,
            async () =>
            {
                var request = new McpGetShoppingListsRequest(
                    ShoppingListId: shoppingListId,
                    ListName: listName,
                    IncludeItems: includeItems
                );

                var result = await mcpMealAndShoppingService.GetShoppingListsAsync(
                    request,
                    cancellationToken
                );
                return result;
            }
        );
    }

    [McpServerTool(Name = "generate_shopping_list_from_meal_plan")]
    [Description(
        "Generate consolidated shopping list items from scheduled meal plan recipes for a date range (excluding leftovers/notes and isolating pantry staples)."
    )]
    public Task<CallToolResult> GenerateShoppingListFromMealPlanAsync(
        [Description("Start date in ISO format YYYY-MM-DD (e.g. '2026-11-02')")] string startDate,
        [Description("End date in ISO format YYYY-MM-DD (e.g. '2026-11-08')")] string endDate,
        [Description("Optional target shopping list ID (Guid)")] Guid? shoppingListId = null,
        [Description("Optional target shopping list name (default 'Weekly Groceries')")]
            string? listName = null,
        [Description("Automatically create target list if missing (default true)")]
            bool createListIfMissing = true,
        [Description("Include on-deck weekly meals (default true)")] bool includeOnDeck = true,
        [Description("Include ingredients from linked sub-recipes (default true)")]
            bool includeSubRecipes = true,
        [Description("Include optional recipe ingredients (default true)")]
            bool includeOptionalIngredients = true,
        [Description("Duplicate meal policy: 'SkipAlreadyImported' or 'ReplaceAlreadyImported'")]
            string duplicatePolicy = "SkipAlreadyImported",
        [Description(
            "Optional list of pantry staple FoodItem IDs to promote directly to active list"
        )]
            Guid[]? promotePantryFoodItemIds = null,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default
    )
    {
        return McpToolResultHelper.ExecuteAsync(
            user,
            async () =>
            {
                var request = new McpGenerateShoppingListRequest(
                    StartDate: startDate,
                    EndDate: endDate,
                    ShoppingListId: shoppingListId,
                    ListName: listName,
                    CreateListIfMissing: createListIfMissing,
                    IncludeOnDeck: includeOnDeck,
                    IncludeSubRecipes: includeSubRecipes,
                    IncludeOptionalIngredients: includeOptionalIngredients,
                    DuplicatePolicy: duplicatePolicy,
                    PromotePantryFoodItemIds: promotePantryFoodItemIds
                );

                var result = await mcpMealAndShoppingService.GenerateShoppingListFromMealPlanAsync(
                    request,
                    cancellationToken
                );
                return result;
            }
        );
    }

    [McpServerTool(Name = "upsert_shopping_list_items")]
    [Description(
        "Add or update items on a shopping list (e.g. 'Paper Towels', '2 gallons Milk') with natural unit normalization and automatic food catalog matching."
    )]
    public Task<CallToolResult> UpsertShoppingListItemsAsync(
        [Description("List of items to add or update")] McpShoppingListItemUpsertInput[] items,
        [Description("Optional target shopping list ID (Guid)")] Guid? shoppingListId = null,
        [Description("Optional target shopping list name (e.g. 'Costco')")] string? listName = null,
        [Description("Automatically create list if missing (default true)")]
            bool createListIfMissing = true,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default
    )
    {
        return McpToolResultHelper.ExecuteAsync(
            user,
            async () =>
            {
                var request = new McpUpsertShoppingListItemsRequest(
                    Items: items ?? [],
                    ShoppingListId: shoppingListId,
                    ListName: listName,
                    CreateListIfMissing: createListIfMissing
                );

                var result = await mcpMealAndShoppingService.UpsertShoppingListItemsAsync(
                    request,
                    cancellationToken
                );
                return result;
            }
        );
    }

    [McpServerTool(Name = "set_shopping_list_item_checked")]
    [Description("Check or uncheck an item on a shopping list by item ID or display name.")]
    public Task<CallToolResult> SetShoppingListItemCheckedAsync(
        [Description("Set item to checked (true) or unchecked (false, default true)")]
            bool isChecked = true,
        [Description("Optional target shopping list ID (Guid)")] Guid? shoppingListId = null,
        [Description("Optional target shopping list name (e.g. 'Weekly Groceries')")]
            string? listName = null,
        [Description("Optional specific item ID (Guid) to check/uncheck")] Guid? itemId = null,
        [Description("Optional item name to match on the list (e.g. 'Paper Towels')")]
            string? itemName = null,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default
    )
    {
        return McpToolResultHelper.ExecuteAsync(
            user,
            async () =>
            {
                var request = new McpSetShoppingItemCheckedRequest(
                    IsChecked: isChecked,
                    ShoppingListId: shoppingListId,
                    ListName: listName,
                    ItemId: itemId,
                    ItemName: itemName
                );

                var result = await mcpMealAndShoppingService.SetShoppingListItemCheckedAsync(
                    request,
                    cancellationToken
                );
                return result;
            }
        );
    }
}

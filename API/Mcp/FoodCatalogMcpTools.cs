using System.ComponentModel;
using System.Security.Claims;
using App.Core.FoodCatalog;
using App.Core.Mcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace App.API.Mcp;

[McpServerToolType]
public sealed class FoodCatalogMcpTools(IMcpFoodCatalogService mcpFoodCatalogService)
{
    [McpServerTool(Name = "list_food_items", ReadOnly = true)]
    [Description(
        "List canonical household food catalog items with optional search text, pantry staple filter, category filter, and pagination."
    )]
    public Task<CallToolResult> ListFoodItemsAsync(
        [Description("Optional search query matching canonical name or aliases")]
            string? search = null,
        [Description("Optional filter for pantry staple flag (true or false)")]
            bool? isPantryStaple = null,
        [Description(
            "Optional shopping category filter (e.g. Produce, SpicesAndSeasonings, Baking)"
        )]
            string? category = null,
        [Description("1-based page number (default 1)")] int page = 1,
        [Description("Page size (1-200, default 50)")] int pageSize = 50,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default
    )
    {
        return McpToolResultHelper.ExecuteAsync(
            user,
            async () =>
            {
                var request = new McpListFoodItemsRequest(
                    Search: search,
                    IsPantryStaple: isPantryStaple,
                    Category: category,
                    Page: page,
                    PageSize: pageSize
                );
                return await mcpFoodCatalogService.ListFoodItemsAsync(request, cancellationToken);
            }
        );
    }

    [McpServerTool(Name = "create_food_item")]
    [Description("Create a new canonical food item in the household catalog.")]
    public Task<CallToolResult> CreateFoodItemAsync(
        [Description("Canonical food item name (max 150 chars)")] string name,
        [Description("Whether the food item is an always-stocked pantry staple (default false)")]
            bool isPantryStaple = false,
        [Description("Optional shopping category (auto-detected if omitted)")]
            string? category = null,
        [Description("Optional list of alternate names / aliases")] string[]? aliases = null,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default
    )
    {
        return McpToolResultHelper.ExecuteAsync(
            user,
            async () =>
                await mcpFoodCatalogService.CreateFoodItemAsync(
                    name: name,
                    isPantryStaple: isPantryStaple,
                    category: category,
                    aliases: aliases,
                    cancellationToken: cancellationToken
                )
        );
    }

    [McpServerTool(Name = "update_food_items")]
    [Description(
        "Update one or more canonical food items (up to 500 per call). Only supplied fields (name, isPantryStaple, category, aliases) are modified; omitted fields are preserved."
    )]
    public Task<CallToolResult> UpdateFoodItemsAsync(
        [Description("List of food item updates matched by foodItemId or currentName")]
            McpFoodItemUpdateInput[] items,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default
    )
    {
        return McpToolResultHelper.ExecuteAsync(
            user,
            async () =>
                await mcpFoodCatalogService.UpdateFoodItemsAsync(
                    new McpUpdateFoodItemsRequest(items ?? []),
                    cancellationToken
                )
        );
    }

    [McpServerTool(Name = "merge_food_items")]
    [Description(
        "Merge one or more duplicate source food items into a target canonical food item, repointing recipes and shopping list items."
    )]
    public Task<CallToolResult> MergeFoodItemsAsync(
        [Description("Target canonical food item ID (Guid) to keep")] Guid targetFoodItemId,
        [Description("Source food item IDs (Guids) to merge into the target")]
            Guid[] sourceFoodItemIds,
        [Description(
            "Preserve merged source names/aliases as aliases on the target (default true)"
        )]
            bool keepSourceNamesAsAliases = true,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default
    )
    {
        return McpToolResultHelper.ExecuteAsync(
            user,
            async () =>
                await mcpFoodCatalogService.MergeFoodItemsAsync(
                    new MergeFoodItemsRequest(
                        TargetFoodItemId: targetFoodItemId,
                        SourceFoodItemIds: sourceFoodItemIds ?? [],
                        KeepSourceNamesAsAliases: keepSourceNamesAsAliases
                    ),
                    cancellationToken
                )
        );
    }
}

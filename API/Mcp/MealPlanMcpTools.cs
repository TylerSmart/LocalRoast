using System.ComponentModel;
using System.Security.Claims;
using App.Core.Mcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace App.API.Mcp;

[McpServerToolType]
public sealed class MealPlanMcpTools(IMcpMealAndShoppingService mcpMealAndShoppingService)
{
    [McpServerTool(Name = "get_meal_plan", ReadOnly = true)]
    [Description(
        "Get scheduled household meal plan entries and weekly on-deck tray for a date range (defaults to current week Monday-Sunday when omitted)."
    )]
    public Task<CallToolResult> GetMealPlanAsync(
        [Description("Start date in ISO format YYYY-MM-DD (e.g. '2026-11-02')")]
            string? startDate = null,
        [Description("End date in ISO format YYYY-MM-DD (e.g. '2026-11-08')")]
            string? endDate = null,
        [Description("Include weekly on-deck entries (default true)")] bool includeOnDeck = true,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default
    )
    {
        return McpToolResultHelper.ExecuteAsync(
            user,
            async () =>
            {
                var request = new McpGetMealPlanRequest(
                    StartDate: startDate,
                    EndDate: endDate,
                    IncludeOnDeck: includeOnDeck
                );

                var result = await mcpMealAndShoppingService.GetMealPlanAsync(
                    request,
                    cancellationToken
                );
                return result;
            }
        );
    }

    [McpServerTool(Name = "plan_meals")]
    [Description(
        "Schedule one or more recipes (by ID, slug, or title), leftovers, or freeform notes onto meal plan calendar slots or weekly on-deck tray."
    )]
    public Task<CallToolResult> PlanMealsAsync(
        [Description("List of meal entries to schedule onto the calendar or on-deck tray")]
            McpPlanMealItemInput[] entries,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default
    )
    {
        return McpToolResultHelper.ExecuteAsync(
            user,
            async () =>
            {
                var request = new McpPlanMealsRequest(entries ?? []);
                var result = await mcpMealAndShoppingService.PlanMealsAsync(
                    request,
                    cancellationToken
                );
                return result;
            }
        );
    }

    [McpServerTool(Name = "remove_meal_plan_entry")]
    [Description(
        "Delete or reschedule a meal plan entry (move to a new date, new slot, or weekly on-deck tray)."
    )]
    public Task<CallToolResult> RemoveMealPlanEntryAsync(
        [Description("Unique ID (Guid) of the meal plan entry to remove or reschedule")]
            Guid entryId,
        [Description("Optional target date YYYY-MM-DD to reschedule this entry to")]
            string? rescheduleToDate = null,
        [Description("Optional target slot (Breakfast, Lunch, Dinner, SnackPrep)")]
            string? rescheduleToSlot = null,
        [Description("Move entry to the weekly on-deck tray (default false)")]
            bool moveToOnDeck = false,
        [Description("Optional target Monday weekStartDate YYYY-MM-DD when moving to on-deck")]
            string? weekStartDate = null,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default
    )
    {
        return McpToolResultHelper.ExecuteAsync(
            user,
            async () =>
            {
                var request = new McpRemoveMealPlanEntryRequest(
                    EntryId: entryId,
                    RescheduleToDate: rescheduleToDate,
                    RescheduleToSlot: rescheduleToSlot,
                    MoveToOnDeck: moveToOnDeck,
                    WeekStartDate: weekStartDate
                );

                var result = await mcpMealAndShoppingService.RemoveOrRescheduleMealPlanEntryAsync(
                    request,
                    cancellationToken
                );
                return result;
            }
        );
    }
}

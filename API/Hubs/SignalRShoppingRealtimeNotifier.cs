using App.Core.Shopping;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace App.API.Hubs;

public sealed class SignalRShoppingRealtimeNotifier(
    IHubContext<ShoppingHub, IShoppingClient> hubContext,
    ILogger<SignalRShoppingRealtimeNotifier> logger
) : IShoppingRealtimeNotifier
{
    public async Task NotifyListChangedAsync(
        ShoppingListRealtimeEventDto listEvent,
        ShoppingListsCatalogChangedDto catalogEvent,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var listGroupName = ShoppingHubGroups.ForList(listEvent.ShoppingListId);

            // 1. Broadcast list-specific event to clients watching this shopping list
            await hubContext.Clients.Group(listGroupName).ShoppingListUpdated(listEvent);

            // 2. Broadcast catalog event to all clients on the workspace overview
            await hubContext
                .Clients.Group(ShoppingHubGroups.WorkspaceGroup)
                .ShoppingListsCatalogChanged(catalogEvent);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to broadcast SignalR shopping realtime event for list {ShoppingListId}",
                listEvent.ShoppingListId
            );
        }
    }
}

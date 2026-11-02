using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace App.API.Hubs;

[Authorize]
public sealed class ShoppingHub : Hub<IShoppingClient>
{
    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, ShoppingHubGroups.WorkspaceGroup);
        await base.OnConnectedAsync();
    }

    public async Task JoinShoppingList(Guid shoppingListId)
    {
        if (shoppingListId != Guid.Empty)
        {
            await Groups.AddToGroupAsync(
                Context.ConnectionId,
                ShoppingHubGroups.ForList(shoppingListId)
            );
        }
    }

    public async Task LeaveShoppingList(Guid shoppingListId)
    {
        if (shoppingListId != Guid.Empty)
        {
            await Groups.RemoveFromGroupAsync(
                Context.ConnectionId,
                ShoppingHubGroups.ForList(shoppingListId)
            );
        }
    }
}

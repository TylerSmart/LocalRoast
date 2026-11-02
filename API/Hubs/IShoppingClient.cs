using App.Core.Shopping;

namespace App.API.Hubs;

public interface IShoppingClient
{
    Task ShoppingListUpdated(ShoppingListRealtimeEventDto listEvent);
    Task ShoppingListsCatalogChanged(ShoppingListsCatalogChangedDto catalogEvent);
}

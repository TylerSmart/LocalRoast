namespace App.Core.Shopping;

public interface IShoppingRealtimeNotifier
{
    Task NotifyListChangedAsync(
        ShoppingListRealtimeEventDto listEvent,
        ShoppingListsCatalogChangedDto catalogEvent,
        CancellationToken cancellationToken = default
    );
}

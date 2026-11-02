namespace App.API.Hubs;

public static class ShoppingHubGroups
{
    public const string WorkspaceGroup = "shopping-workspace";

    public static string ForList(Guid shoppingListId) => $"shopping-list:{shoppingListId:D}";
}

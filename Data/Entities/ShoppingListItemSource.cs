using App.Core.Shopping;

namespace App.Data.Entities;

public sealed class ShoppingListItemSource : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ShoppingListItemId { get; set; }

    public ShoppingListSourceKind SourceKind { get; set; } = ShoppingListSourceKind.Manual;

    public Guid? MealPlanEntryId { get; set; }

    public Guid? RecipeId { get; set; }

    public string SourceLabel { get; set; } = string.Empty;

    public DateOnly? ScheduledDate { get; set; }

    public decimal? ContributedQuantity { get; set; }

    public decimal? ContributedMaxQuantity { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "System";

    public DateTimeOffset ModifiedAt { get; set; }

    public string ModifiedBy { get; set; } = "System";

    public ShoppingListItem ShoppingListItem { get; set; } = null!;
}

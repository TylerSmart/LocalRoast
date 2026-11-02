using App.Core.Recipes;
using App.Core.Shopping;

namespace App.Data.Entities;

public sealed class ShoppingListItem : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ShoppingListId { get; set; }

    public Guid? FoodItemId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public decimal? Quantity { get; set; }

    public decimal? MaxQuantity { get; set; }

    public MeasurementUnit? Unit { get; set; }

    public string? CustomUnitName { get; set; }

    public MeasurementDimension Dimension { get; set; } = MeasurementDimension.Unspecified;

    public string? Notes { get; set; }

    public bool IsPantryStapleExcluded { get; set; }

    public bool IsChecked { get; set; }

    public DateTimeOffset? CheckedAt { get; set; }

    public Guid? CheckedByUserId { get; set; }

    public string? CheckedByDisplayName { get; set; }

    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "System";

    public DateTimeOffset ModifiedAt { get; set; }

    public string ModifiedBy { get; set; } = "System";

    public ShoppingList ShoppingList { get; set; } = null!;

    public FoodItem? FoodItem { get; set; }

    public ICollection<ShoppingListItemSource> Sources { get; set; } =
        new List<ShoppingListItemSource>();
}

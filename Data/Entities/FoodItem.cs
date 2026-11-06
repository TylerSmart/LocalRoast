using App.Core.FoodCatalog;

namespace App.Data.Entities;

public sealed class FoodItem : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string NormalizedName { get; set; } = string.Empty;

    public bool IsPantryStaple { get; set; }

    public FoodCategory? Category { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "System";

    public DateTimeOffset ModifiedAt { get; set; }

    public string ModifiedBy { get; set; } = "System";

    public ICollection<FoodItemAlias> Aliases { get; set; } = new List<FoodItemAlias>();

    public ICollection<RecipeIngredient> RecipeIngredients { get; set; } =
        new List<RecipeIngredient>();

    public ICollection<ShoppingListItem> ShoppingListItems { get; set; } =
        new List<ShoppingListItem>();
}

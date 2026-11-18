using App.Core.Recipes;

namespace App.Data.Entities;

public sealed class RecipeIngredientReplacement : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RecipeIngredientId { get; set; }

    public int SortOrder { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public decimal? Quantity { get; set; }

    public decimal? MaxQuantity { get; set; }

    public MeasurementUnit? Unit { get; set; }

    public string? CustomUnitName { get; set; }

    public Guid? FoodItemId { get; set; }

    public string? PreparationNote { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "System";

    public DateTimeOffset ModifiedAt { get; set; }

    public string ModifiedBy { get; set; } = "System";

    public RecipeIngredient RecipeIngredient { get; set; } = null!;

    public FoodItem? FoodItem { get; set; }
}

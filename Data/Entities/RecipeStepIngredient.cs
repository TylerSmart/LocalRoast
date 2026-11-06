using App.Core.Recipes;

namespace App.Data.Entities;

public sealed class RecipeStepIngredient : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RecipeStepId { get; set; }

    public Guid RecipeIngredientId { get; set; }

    public decimal? Quantity { get; set; }

    public decimal? MaxQuantity { get; set; }

    public MeasurementUnit? Unit { get; set; }

    public string? CustomUnitName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "System";

    public DateTimeOffset ModifiedAt { get; set; }

    public string ModifiedBy { get; set; } = "System";

    public RecipeStep RecipeStep { get; set; } = null!;

    public RecipeIngredient RecipeIngredient { get; set; } = null!;
}

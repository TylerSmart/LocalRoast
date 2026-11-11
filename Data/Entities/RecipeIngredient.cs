using App.Core.Recipes;

namespace App.Data.Entities;

public sealed class RecipeIngredient : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RecipeId { get; set; }

    public int SortOrder { get; set; }

    public string? SectionName { get; set; }

    public decimal? Quantity { get; set; }

    public decimal? MaxQuantity { get; set; }

    public MeasurementUnit? Unit { get; set; }

    public string? CustomUnitName { get; set; }

    public decimal? AlternativeQuantity { get; set; }

    public decimal? AlternativeMaxQuantity { get; set; }

    public MeasurementUnit? AlternativeUnit { get; set; }

    public string? AlternativeCustomUnitName { get; set; }

    public bool PreferAlternativeMeasurement { get; set; }

    public RecipeMeasurement GetPreferredMeasurement() =>
        CulinaryMath.PreferredMeasurement(
            new RecipeMeasurement(Quantity, Unit, MaxQuantity, CustomUnitName),
            AlternativeQuantity.HasValue && AlternativeUnit.HasValue
                ? new RecipeMeasurement(
                    AlternativeQuantity,
                    AlternativeUnit,
                    AlternativeMaxQuantity,
                    AlternativeCustomUnitName
                )
                : null,
            PreferAlternativeMeasurement
        );

    public Guid? FoodItemId { get; set; }

    public Guid? LinkedRecipeId { get; set; }

    public string? UnresolvedRecipeTitle { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string? PreparationNote { get; set; }

    public bool IsOptional { get; set; }

    public string? OriginalRawText { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "System";

    public DateTimeOffset ModifiedAt { get; set; }

    public string ModifiedBy { get; set; } = "System";

    public Recipe Recipe { get; set; } = null!;

    public FoodItem? FoodItem { get; set; }

    public Recipe? LinkedRecipe { get; set; }

    public ICollection<RecipeStepIngredient> StepReferences { get; set; } =
        new List<RecipeStepIngredient>();
}

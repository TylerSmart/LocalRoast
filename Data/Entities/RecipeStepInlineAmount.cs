using App.Core.Recipes;

namespace App.Data.Entities;

/// <summary>An amount inside a step's instruction, rendered as a scalable chip.</summary>
public sealed class RecipeStepInlineAmount : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RecipeStepId { get; set; }

    public int SortOrder { get; set; }

    /// <summary>Character offset into the trimmed <see cref="RecipeStep.Instruction"/>.</summary>
    public int Start { get; set; }

    public int Length { get; set; }

    /// <summary>Optional character offset into the trimmed <see cref="RecipeStep.Instruction"/> for the associated ingredient name.</summary>
    public int? IngredientSpanStart { get; set; }

    public int? IngredientSpanLength { get; set; }

    public InlineStepAmountKind Kind { get; set; }

    /// <summary>Set if and only if <see cref="Kind"/> is <see cref="InlineStepAmountKind.Ingredient"/>.</summary>
    public Guid? RecipeIngredientId { get; set; }

    public decimal? Quantity { get; set; }

    public decimal? MaxQuantity { get; set; }

    public MeasurementUnit? Unit { get; set; }

    public string? CustomUnitName { get; set; }

    public decimal? AlternativeQuantity { get; set; }

    public decimal? AlternativeMaxQuantity { get; set; }

    public MeasurementUnit? AlternativeUnit { get; set; }

    public string? AlternativeCustomUnitName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "System";

    public DateTimeOffset ModifiedAt { get; set; }

    public string ModifiedBy { get; set; } = "System";

    public RecipeStep RecipeStep { get; set; } = null!;

    public RecipeIngredient? RecipeIngredient { get; set; }
}

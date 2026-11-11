namespace App.Data.Entities;

public sealed class RecipeStep : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RecipeId { get; set; }

    public int StepNumber { get; set; }

    public string? SectionName { get; set; }

    public string Instruction { get; set; } = string.Empty;

    public string? ImagePath { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "System";

    public DateTimeOffset ModifiedAt { get; set; }

    public string ModifiedBy { get; set; } = "System";

    public Recipe Recipe { get; set; } = null!;

    public ICollection<RecipeStepIngredient> IngredientReferences { get; set; } =
        new List<RecipeStepIngredient>();

    public ICollection<RecipeStepTimer> Timers { get; set; } = new List<RecipeStepTimer>();
}

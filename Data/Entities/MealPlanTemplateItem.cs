using App.Core.MealPlans;

namespace App.Data.Entities;

public sealed class MealPlanTemplateItem : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TemplateId { get; set; }

    public bool IsOnDeck { get; set; }

    public int? DayOffset { get; set; }

    public MealSlot? Slot { get; set; }

    public int SortOrder { get; set; }

    public MealPlanEntryType EntryType { get; set; } = MealPlanEntryType.Recipe;

    public Guid? RecipeId { get; set; }

    public decimal? TargetServings { get; set; }

    public bool IsLeftovers { get; set; }

    public string? CustomTitle { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "System";

    public DateTimeOffset ModifiedAt { get; set; }

    public string ModifiedBy { get; set; } = "System";

    public MealPlanTemplate Template { get; set; } = null!;

    public Recipe? Recipe { get; set; }
}

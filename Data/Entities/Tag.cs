using App.Core.Recipes;

namespace App.Data.Entities;

public sealed class Tag : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string NormalizedName { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public TagCategory Category { get; set; } = TagCategory.Custom;

    public int SortOrder { get; set; }

    public bool IsSystemSeeded { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "System";

    public DateTimeOffset ModifiedAt { get; set; }

    public string ModifiedBy { get; set; } = "System";

    public ICollection<RecipeTag> RecipeTags { get; set; } = new List<RecipeTag>();
}

namespace App.Data.Entities;

public sealed class RecipeCollection : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RecipeId { get; set; }

    public Guid CollectionId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "System";

    public DateTimeOffset ModifiedAt { get; set; }

    public string ModifiedBy { get; set; } = "System";

    public Recipe Recipe { get; set; } = null!;

    public Collection Collection { get; set; } = null!;
}

namespace App.Data.Entities;

public sealed class RecipeEmbedding : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RecipeId { get; set; }

    public Recipe Recipe { get; set; } = null!;

    public string ModelTag { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;

    public string EmbeddingText { get; set; } = string.Empty;

    public int Dimensions { get; set; }

    public byte[] VectorBytes { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "System";

    public DateTimeOffset ModifiedAt { get; set; }

    public string ModifiedBy { get; set; } = "System";
}

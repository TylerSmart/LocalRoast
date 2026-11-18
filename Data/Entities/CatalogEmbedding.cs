namespace App.Data.Entities;

public sealed class CatalogEmbedding : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string ModelTag { get; set; } = string.Empty;

    public string NormalizedText { get; set; } = string.Empty;

    public int Dimensions { get; set; }

    public byte[] VectorBytes { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "System";

    public DateTimeOffset ModifiedAt { get; set; }

    public string ModifiedBy { get; set; } = "System";
}

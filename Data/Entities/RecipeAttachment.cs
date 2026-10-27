namespace App.Data.Entities;

public sealed class RecipeAttachment : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RecipeId { get; set; }
    public int SortOrder { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string MediaUrlPath { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string? Label { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "System";
    public DateTimeOffset ModifiedAt { get; set; }
    public string ModifiedBy { get; set; } = "System";

    public Recipe Recipe { get; set; } = null!;
}

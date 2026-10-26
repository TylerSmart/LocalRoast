namespace App.Data.Entities;

public sealed class ShareLink : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RecipeId { get; set; }

    public string Token { get; set; } = string.Empty;

    public string? Label { get; set; }

    public bool IncludeCookLogs { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public int ViewCount { get; set; }

    public DateTimeOffset? LastAccessedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "System";

    public DateTimeOffset ModifiedAt { get; set; }

    public string ModifiedBy { get; set; } = "System";

    public Recipe Recipe { get; set; } = null!;
}

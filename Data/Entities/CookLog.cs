namespace App.Data.Entities;

public sealed class CookLog : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RecipeId { get; set; }

    public Guid? CookedByUserId { get; set; }

    public string CookedByDisplayName { get; set; } = "Household Member";

    public DateTimeOffset CookedAt { get; set; }

    public decimal? ServingsMade { get; set; }

    public int? Rating { get; set; }

    public string? Notes { get; set; }

    public string? PhotoImagePath { get; set; }

    public string? PhotoThumbnailPath { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "System";

    public DateTimeOffset ModifiedAt { get; set; }

    public string ModifiedBy { get; set; } = "System";

    public Recipe Recipe { get; set; } = null!;

    public User? CookedByUser { get; set; }
}

namespace App.Data.Entities;

/// <summary>A unique saved model on a connection with its hardware placement and optional per-model rate limits.</summary>
public sealed class LlmModelConfiguration : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public Guid ConnectionId { get; set; }

    public LlmConnection? Connection { get; set; }

    public string Model { get; set; } = string.Empty;

    public int? OllamaGpuLayers { get; set; }

    public int? MaxConcurrentRequests { get; set; }

    public int? RequestsPerMinute { get; set; }

    public int? DailyRequestQuota { get; set; }

    public string? DailyQuotaResetTimezone { get; set; }

    public string? DailyQuotaResetTime { get; set; }

    public ICollection<LlmModelProfile> Profiles { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "System";

    public DateTimeOffset ModifiedAt { get; set; }

    public string ModifiedBy { get; set; } = "System";
}

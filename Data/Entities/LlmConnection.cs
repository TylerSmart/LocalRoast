using App.Core.Ingestion;

namespace App.Data.Entities;

/// <summary>A saved LLM host (machine or hosted provider) with its endpoint, credentials and request limits.</summary>
public sealed class LlmConnection : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public LlmProvider Provider { get; set; } = LlmProvider.Ollama;

    public string BaseUrl { get; set; } = "http://localhost:11434";

    public string? ApiKey { get; set; }

    public string OllamaKeepAlive { get; set; } = "5m";

    public bool OllamaCpuOnly { get; set; }

    public bool EnableRateLimitSafeguards { get; set; } = true;

    public int MaxConcurrentRequests { get; set; } = 1;

    public int? RequestsPerMinute { get; set; }

    public int? DailyRequestQuota { get; set; }

    public string DailyQuotaResetTimezone { get; set; } = "UTC";

    public string DailyQuotaResetTime { get; set; } = "00:00";

    public ICollection<LlmModelConfiguration> ModelConfigurations { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "System";

    public DateTimeOffset ModifiedAt { get; set; }

    public string ModifiedBy { get; set; } = "System";
}

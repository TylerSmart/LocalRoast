using App.Core.Ingestion;

namespace App.Data.Entities;

public sealed class HouseholdLlmSettings : IAuditable
{
    public const string DefaultSingletonKey = "Default";

    public Guid Id { get; set; } = Guid.NewGuid();

    public string SingletonKey { get; set; } = DefaultSingletonKey;

    public LlmProvider Provider { get; set; } = LlmProvider.Ollama;

    public string BaseUrl { get; set; } = "http://localhost:11434";

    public string? ApiKey { get; set; }

    public string? TextModel { get; set; }

    public string? VisionModel { get; set; }

    public string OllamaKeepAlive { get; set; } = "5m";

    public int ContextWindowTokens { get; set; } = 8192;

    public int RequestTimeoutSeconds { get; set; } = 120;

    public bool AutoPublishHighConfidenceText { get; set; }

    public bool EnableThinking { get; set; }

    public bool OllamaCpuOnly { get; set; }

    public double? Temperature { get; set; }

    public double? TopP { get; set; }

    public int? TopK { get; set; }

    public string? RefineModel { get; set; }

    public bool RefineEnableThinking { get; set; } = true;

    public double? RefineTemperature { get; set; }

    public double? RefineTopP { get; set; }

    public int? RefineTopK { get; set; }

    public int RefineRequestTimeoutSeconds { get; set; } = 1200;

    public int MaxIngestionRetries { get; set; } = 3;

    public bool EnableIngestion { get; set; } = true;

    public bool EnableRateLimitSafeguards { get; set; } = true;

    public int MaxConcurrentRequests { get; set; } = 1;

    public int? RequestsPerMinute { get; set; }

    public int? DailyRequestQuota { get; set; }

    public string DailyQuotaResetTimezone { get; set; } = "UTC";

    public string DailyQuotaResetTime { get; set; } = "00:00";

    public bool AutoResumeQueue { get; set; } = true;

    public DateTimeOffset? PausedUntilUtc { get; set; }

    public string? PauseReason { get; set; }

    public bool ModelProfilesSeeded { get; set; }

    public Guid? TextModelConfigurationId { get; set; }

    public LlmModelConfiguration? TextModelConfiguration { get; set; }

    public Guid? VisionModelConfigurationId { get; set; }

    public LlmModelConfiguration? VisionModelConfiguration { get; set; }

    public Guid? RefineModelConfigurationId { get; set; }

    public LlmModelConfiguration? RefineModelConfiguration { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "System";

    public DateTimeOffset ModifiedAt { get; set; }

    public string ModifiedBy { get; set; } = "System";
}

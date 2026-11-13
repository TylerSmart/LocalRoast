using System.ComponentModel.DataAnnotations;

namespace App.Core.Ingestion;

public sealed class LlmDefaultsOptions
{
    public const string SectionName = "Llm";

    public LlmProvider Provider { get; set; } = LlmProvider.Ollama;

    public string BaseUrl { get; set; } = "http://localhost:11434";

    public string? ApiKey { get; set; }

    public string? TextModel { get; set; }

    public string? VisionModel { get; set; }

    public string OllamaKeepAlive { get; set; } = "5m";

    public int ContextWindowTokens { get; set; } = 8192;

    public int RequestTimeoutSeconds { get; set; } = 120;

    public bool AutoPublishHighConfidenceText { get; set; }

    public bool EnableThinking { get; set; } = false;

    public bool OllamaCpuOnly { get; set; }

    /// <summary>Null keeps LocalRoast's per-request defaults (0 for recipe extraction, 0.2 for AI helpers).</summary>
    public double? Temperature { get; set; }

    /// <summary>Null omits top_p so the model's own default applies.</summary>
    public double? TopP { get; set; }

    /// <summary>Null omits top_k so the model's own default applies.</summary>
    public int? TopK { get; set; }

    /// <summary>Optional larger model that refines drafts still flagged after the Text Model's extraction.</summary>
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

    public bool EnableBackgroundWorker { get; set; } = true;

    public int WorkerPollIntervalSeconds { get; set; } = 3;

    public int OfflineProbeMaxBackoffSeconds { get; set; } = 300;
}

public record LlmSettingsDto(
    LlmProvider Provider,
    string BaseUrl,
    bool HasApiKey,
    string? ApiKeyMasked,
    string? TextModel,
    string? VisionModel,
    string OllamaKeepAlive,
    int RequestTimeoutSeconds,
    bool AutoPublishHighConfidenceText,
    bool EnableThinking,
    int MaxIngestionRetries,
    bool EnableIngestion,
    int ContextWindowTokens,
    DateTimeOffset? ModifiedAt,
    bool OllamaCpuOnly = false,
    double? Temperature = null,
    double? TopP = null,
    int? TopK = null,
    string? RefineModel = null,
    bool RefineEnableThinking = true,
    double? RefineTemperature = null,
    double? RefineTopP = null,
    int? RefineTopK = null,
    int RefineRequestTimeoutSeconds = 1200,
    bool EnableRateLimitSafeguards = true,
    int MaxConcurrentRequests = 1,
    int? RequestsPerMinute = null,
    int? DailyRequestQuota = null,
    string DailyQuotaResetTimezone = "UTC",
    string DailyQuotaResetTime = "00:00",
    bool AutoResumeQueue = true,
    DateTimeOffset? PausedUntilUtc = null,
    string? PauseReason = null,
    Guid? TextModelConfigurationId = null,
    Guid? VisionModelConfigurationId = null,
    Guid? RefineModelConfigurationId = null
);

public record EffectiveLlmConnectionConfig(
    LlmProvider Provider,
    string BaseUrl,
    string? ApiKey,
    string? TextModel,
    string? VisionModel,
    string OllamaKeepAlive,
    int RequestTimeoutSeconds,
    bool AutoPublishHighConfidenceText,
    bool EnableThinking = false,
    int MaxIngestionRetries = 3,
    bool EnableIngestion = true,
    int ContextWindowTokens = 8192,
    bool OllamaCpuOnly = false,
    double? Temperature = null,
    double? TopP = null,
    int? TopK = null,
    string? RefineModel = null,
    bool RefineEnableThinking = true,
    double? RefineTemperature = null,
    double? RefineTopP = null,
    int? RefineTopK = null,
    int RefineRequestTimeoutSeconds = 1200,
    bool EnableRateLimitSafeguards = true,
    int MaxConcurrentRequests = 1,
    int? RequestsPerMinute = null,
    int? DailyRequestQuota = null,
    string DailyQuotaResetTimezone = "UTC",
    string DailyQuotaResetTime = "00:00",
    bool AutoResumeQueue = true,
    DateTimeOffset? PausedUntilUtc = null,
    string? PauseReason = null,
    string? ConnectionKey = null,
    EffectiveLlmConnectionConfig? VisionConfig = null,
    EffectiveLlmConnectionConfig? RefineConfig = null,
    int? OllamaGpuLayers = null
)
{
    /// <summary>
    /// The connection settings for vision requests. A vision model configuration on its own
    /// connection supplies its host, credentials, limits and tuning.
    /// </summary>
    public EffectiveLlmConnectionConfig ForVision() => VisionConfig ?? this;

    /// <summary>The connection settings for refine requests: the Refine Model with its own thinking, sampling and timeout.</summary>
    public EffectiveLlmConnectionConfig ForRefinement() =>
        RefineConfig
        ?? this with
        {
            TextModel = RefineModel?.Trim(),
            VisionModel = RefineModel?.Trim(),
            EnableThinking = RefineEnableThinking,
            Temperature = RefineTemperature,
            TopP = RefineTopP,
            TopK = RefineTopK,
            RequestTimeoutSeconds = RefineRequestTimeoutSeconds,
        };

    /// <summary>True when a refine model is set that is not the text model on the same host.</summary>
    public bool HasDistinctRefinement()
    {
        var refineModel = RefineModel?.Trim();
        if (string.IsNullOrWhiteSpace(refineModel))
        {
            return false;
        }

        var refine = ForRefinement();
        return !string.Equals(refineModel, TextModel?.Trim(), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(refine.BaseUrl, BaseUrl, StringComparison.OrdinalIgnoreCase)
            || refine.Provider != Provider;
    }
}

public record UpdateLlmSettingsRequest(
    LlmProvider Provider = LlmProvider.Ollama,
    [Required, MaxLength(500)] string BaseUrl = "http://localhost:11434",
    [MaxLength(500)] string? ApiKey = null,
    bool ClearApiKey = false,
    [MaxLength(200)] string? TextModel = null,
    [MaxLength(200)] string? VisionModel = null,
    [MaxLength(32)] string OllamaKeepAlive = "5m",
    [Range(5, 1800)] int RequestTimeoutSeconds = 120,
    bool AutoPublishHighConfidenceText = false,
    bool EnableThinking = false,
    [Range(0, 10)] int MaxIngestionRetries = 3,
    bool EnableIngestion = true,
    [Range(2048, 65536)] int ContextWindowTokens = 8192,
    bool OllamaCpuOnly = false,
    [Range(0.0, 2.0)] double? Temperature = null,
    [Range(0.0, 1.0)] double? TopP = null,
    [Range(1, 1000)] int? TopK = null,
    [MaxLength(200)] string? RefineModel = null,
    bool RefineEnableThinking = true,
    [Range(0.0, 2.0)] double? RefineTemperature = null,
    [Range(0.0, 1.0)] double? RefineTopP = null,
    [Range(1, 1000)] int? RefineTopK = null,
    [Range(5, 1800)] int RefineRequestTimeoutSeconds = 1200,
    bool EnableRateLimitSafeguards = true,
    [Range(1, 5)] int MaxConcurrentRequests = 1,
    [Range(1, 1000)] int? RequestsPerMinute = null,
    [Range(1, 100000)] int? DailyRequestQuota = null,
    [MaxLength(64)] string DailyQuotaResetTimezone = "UTC",
    [MaxLength(10)] string DailyQuotaResetTime = "00:00",
    bool AutoResumeQueue = true
);

public record TestLlmConnectionRequest(
    LlmProvider? Provider = null,
    [MaxLength(500)] string? BaseUrl = null,
    [MaxLength(500)] string? ApiKey = null,
    bool ClearApiKey = false,
    [Range(5, 120)] int? TimeoutSeconds = 15,
    Guid? ConnectionId = null
);

public record UpsertLlmModelProfileRequest(
    [Required, MaxLength(150)] string DisplayName,
    [Required, MaxLength(200)] string ModelTag,
    IReadOnlyList<string>? MatchPrefixes = null,
    [MaxLength(500)] string? Summary = null,
    bool EnableThinking = false,
    [Range(0.0, 2.0)] double? Temperature = null,
    [Range(0.0, 1.0)] double? TopP = null,
    [Range(1, 1000)] int? TopK = null,
    [Range(2048, 65536)] int ContextWindowTokens = 32768,
    [Range(5, 1800)] int RequestTimeoutSeconds = 600,
    bool RecommendedAsRefineModel = false,
    IReadOnlyList<string>? Notes = null
);

public record LlmConnectionTestResultDto(
    bool IsReachable,
    LlmProvider Provider,
    string NormalizedBaseUrl,
    int LatencyMs,
    IReadOnlyList<string> Models,
    string? ErrorMessage
);

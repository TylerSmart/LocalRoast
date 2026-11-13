using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace App.Core.Ingestion;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LlmWorkflow
{
    Text = 0,
    Vision = 1,
    Refinement = 2,
}

public sealed record LlmConnectionDto(
    Guid Id,
    string Name,
    LlmProvider Provider,
    string BaseUrl,
    bool HasApiKey,
    string? ApiKeyMasked,
    string OllamaKeepAlive,
    bool OllamaCpuOnly,
    bool EnableRateLimitSafeguards,
    int MaxConcurrentRequests,
    int? RequestsPerMinute,
    int? DailyRequestQuota,
    string DailyQuotaResetTimezone,
    string DailyQuotaResetTime,
    int ModelConfigurationCount,
    DateTimeOffset ModifiedAt
);

public sealed record UpsertLlmConnectionRequest(
    [Required, MaxLength(100)] string Name,
    LlmProvider Provider = LlmProvider.Ollama,
    [Required, MaxLength(500)] string BaseUrl = "http://localhost:11434",
    [MaxLength(500)] string? ApiKey = null,
    bool ClearApiKey = false,
    [MaxLength(32)] string OllamaKeepAlive = "5m",
    bool OllamaCpuOnly = false,
    bool EnableRateLimitSafeguards = true,
    [Range(1, 5)] int MaxConcurrentRequests = 1,
    [Range(1, 1000)] int? RequestsPerMinute = null,
    [Range(1, 100000)] int? DailyRequestQuota = null,
    [MaxLength(64)] string DailyQuotaResetTimezone = "UTC",
    [MaxLength(10)] string DailyQuotaResetTime = "00:00"
);

public sealed record LlmModelConfigurationDto(
    Guid Id,
    string Name,
    Guid ConnectionId,
    string ConnectionName,
    LlmProvider Provider,
    string Model,
    string? ProfileId,
    bool EnableThinking,
    double? Temperature,
    double? TopP,
    int? TopK,
    int ContextWindowTokens,
    int RequestTimeoutSeconds,
    IReadOnlyList<LlmWorkflow> AssignedWorkflows,
    DateTimeOffset ModifiedAt,
    int? OllamaGpuLayers = null
);

public sealed record UpsertLlmModelConfigurationRequest(
    [Required, MaxLength(150)] string Name,
    Guid ConnectionId,
    [Required, MaxLength(200)] string Model,
    [MaxLength(100)] string? ProfileId = null,
    bool EnableThinking = false,
    [Range(0.0, 2.0)] double? Temperature = null,
    [Range(0.0, 1.0)] double? TopP = null,
    [Range(1, 1000)] int? TopK = null,
    [Range(2048, 65536)] int ContextWindowTokens = 8192,
    [Range(5, 1800)] int RequestTimeoutSeconds = 120,
    [Range(0, int.MaxValue)] int? OllamaGpuLayers = null
);

/// <summary>Selects the saved model configuration for each workflow and the queue policies.</summary>
public sealed record UpdateLlmWorkflowsRequest(
    Guid? TextModelConfigurationId = null,
    Guid? VisionModelConfigurationId = null,
    Guid? RefineModelConfigurationId = null,
    bool AutoPublishHighConfidenceText = false,
    [Range(0, 10)] int MaxIngestionRetries = 3,
    bool EnableIngestion = true,
    bool AutoResumeQueue = true
);

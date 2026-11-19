using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace App.Core.Ingestion;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LlmWorkflow
{
    Text = 0,
    Vision = 1,
    Refinement = 2,
    Embedding = 3,
}

public static class LlmProfileDefaults
{
    public const string DefaultDisplayName = "Defaults";
    public const int DefaultContextWindowTokens = 8192;
    public const int DefaultRequestTimeoutSeconds = 120;
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
    Guid DefaultProfileId,
    int ProfileCount,
    IReadOnlyList<LlmWorkflow> AssignedWorkflows,
    DateTimeOffset ModifiedAt,
    int? OllamaGpuLayers = null,
    int? MaxConcurrentRequests = null,
    int? RequestsPerMinute = null,
    int? DailyRequestQuota = null,
    string? DailyQuotaResetTimezone = null,
    string? DailyQuotaResetTime = null
);

public sealed record UpsertLlmModelConfigurationRequest(
    [Required, MaxLength(150)] string Name,
    Guid ConnectionId,
    [Required, MaxLength(200)] string Model,
    [Range(0, int.MaxValue)] int? OllamaGpuLayers = null,
    [Range(1, 5)] int? MaxConcurrentRequests = null,
    [Range(1, 1000)] int? RequestsPerMinute = null,
    [Range(1, 100000)] int? DailyRequestQuota = null,
    [MaxLength(64)] string? DailyQuotaResetTimezone = null,
    [MaxLength(10)] string? DailyQuotaResetTime = null
);

public sealed record LlmModelProfileDto(
    Guid Id,
    Guid ModelConfigurationId,
    string ModelConfigurationName,
    Guid ConnectionId,
    string ConnectionName,
    LlmProvider Provider,
    string Model,
    string DisplayName,
    string Summary,
    bool EnableThinking,
    double? Temperature,
    double? TopP,
    int? TopK,
    int ContextWindowTokens,
    int RequestTimeoutSeconds,
    IReadOnlyList<string> Notes,
    bool IsDefault,
    IReadOnlyList<LlmWorkflow> AssignedWorkflows,
    DateTimeOffset ModifiedAt
);

public sealed record UpsertLlmModelProfileRequest(
    Guid ModelConfigurationId,
    [Required, MaxLength(150)] string DisplayName,
    [MaxLength(500)] string? Summary = null,
    bool EnableThinking = false,
    [Range(0.0, 2.0)] double? Temperature = null,
    [Range(0.0, 1.0)] double? TopP = null,
    [Range(1, 1000)] int? TopK = null,
    [Range(2048, 65536)] int ContextWindowTokens = LlmProfileDefaults.DefaultContextWindowTokens,
    [Range(5, 1800)] int RequestTimeoutSeconds = LlmProfileDefaults.DefaultRequestTimeoutSeconds,
    IReadOnlyList<string>? Notes = null
);

/// <summary>Selects the saved model profile for each workflow and the queue policies.</summary>
public sealed record UpdateLlmWorkflowsRequest(
    Guid? TextModelProfileId = null,
    Guid? VisionModelProfileId = null,
    Guid? RefineModelProfileId = null,
    bool AutoPublishHighConfidenceText = false,
    [Range(0, 10)] int MaxIngestionRetries = 3,
    bool EnableIngestion = true,
    bool AutoResumeQueue = true,
    Guid? EmbeddingModelProfileId = null
);

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

    public int RequestTimeoutSeconds { get; set; } = 120;

    public bool AutoPublishHighConfidenceText { get; set; }

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
    DateTimeOffset? ModifiedAt
);

public record EffectiveLlmConnectionConfig(
    LlmProvider Provider,
    string BaseUrl,
    string? ApiKey,
    string? TextModel,
    string? VisionModel,
    string OllamaKeepAlive,
    int RequestTimeoutSeconds,
    bool AutoPublishHighConfidenceText
);

public record UpdateLlmSettingsRequest(
    LlmProvider Provider = LlmProvider.Ollama,
    [Required, MaxLength(500)] string BaseUrl = "http://localhost:11434",
    [MaxLength(500)] string? ApiKey = null,
    bool ClearApiKey = false,
    [MaxLength(200)] string? TextModel = null,
    [MaxLength(200)] string? VisionModel = null,
    [MaxLength(32)] string OllamaKeepAlive = "5m",
    [Range(5, 600)] int RequestTimeoutSeconds = 120,
    bool AutoPublishHighConfidenceText = false
);

public record TestLlmConnectionRequest(
    LlmProvider? Provider = null,
    [MaxLength(500)] string? BaseUrl = null,
    [MaxLength(500)] string? ApiKey = null,
    bool ClearApiKey = false,
    [Range(5, 120)] int? TimeoutSeconds = 15
);

public record LlmConnectionTestResultDto(
    bool IsReachable,
    LlmProvider Provider,
    string NormalizedBaseUrl,
    int LatencyMs,
    IReadOnlyList<string> Models,
    string? ErrorMessage
);

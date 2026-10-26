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

    public int RequestTimeoutSeconds { get; set; } = 120;

    public bool AutoPublishHighConfidenceText { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "System";

    public DateTimeOffset ModifiedAt { get; set; }

    public string ModifiedBy { get; set; } = "System";
}

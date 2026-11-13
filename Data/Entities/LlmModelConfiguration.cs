namespace App.Data.Entities;

/// <summary>A saved model on a connection with its own tuning; workflows select these.</summary>
public sealed class LlmModelConfiguration : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public Guid ConnectionId { get; set; }

    public LlmConnection? Connection { get; set; }

    public string Model { get; set; } = string.Empty;

    /// <summary>Slug of the profile last applied; informational only, settings are copied.</summary>
    public string? ProfileSlug { get; set; }

    public bool EnableThinking { get; set; }

    public double? Temperature { get; set; }

    public double? TopP { get; set; }

    public int? TopK { get; set; }

    public int ContextWindowTokens { get; set; } = 8192;

    public int RequestTimeoutSeconds { get; set; } = 120;

    public int? OllamaGpuLayers { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "System";

    public DateTimeOffset ModifiedAt { get; set; }

    public string ModifiedBy { get; set; } = "System";
}

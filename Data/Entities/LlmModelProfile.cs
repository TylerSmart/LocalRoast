using App.Core.Ingestion;

namespace App.Data.Entities;

/// <summary>A saved tuning profile bound directly to one saved LLM model configuration.</summary>
public sealed class LlmModelProfile : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ModelConfigurationId { get; set; }

    public LlmModelConfiguration? ModelConfiguration { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public bool EnableThinking { get; set; }

    public double? Temperature { get; set; }

    public double? TopP { get; set; }

    public int? TopK { get; set; }

    public int ContextWindowTokens { get; set; } = LlmProfileDefaults.DefaultContextWindowTokens;

    public int RequestTimeoutSeconds { get; set; } =
        LlmProfileDefaults.DefaultRequestTimeoutSeconds;

    public string NotesJson { get; set; } = "[]";

    /// <summary>True for the auto-created, undeletable Defaults profile owned by a saved model.</summary>
    public bool IsDefault { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "System";

    public DateTimeOffset ModifiedAt { get; set; }

    public string ModifiedBy { get; set; } = "System";
}

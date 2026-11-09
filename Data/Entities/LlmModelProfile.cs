namespace App.Data.Entities;

public sealed class LlmModelProfile : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Slug { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ModelTag { get; set; } = string.Empty;
    public string MatchPrefixesJson { get; set; } = "[]";
    public string Summary { get; set; } = string.Empty;
    public bool EnableThinking { get; set; }
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public int? TopK { get; set; }
    public int ContextWindowTokens { get; set; } = 32768;
    public int RequestTimeoutSeconds { get; set; } = 600;
    public bool RecommendedAsRefineModel { get; set; }
    public string NotesJson { get; set; } = "[]";
    public int SortOrder { get; set; }
    public bool IsBuiltInDefault { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "System";
    public DateTimeOffset ModifiedAt { get; set; }
    public string ModifiedBy { get; set; } = "System";
}

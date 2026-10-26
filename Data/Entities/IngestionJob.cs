using App.Core.Ingestion;

namespace App.Data.Entities;

public sealed class IngestionJob : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public IngestionChannel Channel { get; set; }

    public IngestionJobStatus Status { get; set; } = IngestionJobStatus.Queued;

    public string DisplayTitle { get; set; } = string.Empty;

    public string? SourceUrl { get; set; }

    public string? RawText { get; set; }

    public string? ScrapedPageJson { get; set; }

    public string? NormalizedSourceText { get; set; }

    public string? DownloadedCoverImagePath { get; set; }

    public string? DownloadedThumbnailImagePath { get; set; }

    public string? ExtractedDraftJson { get; set; }

    public decimal? ConfidenceScore { get; set; }

    public string? StatusDetail { get; set; }

    public int AttemptCount { get; set; }

    public DateTimeOffset? NextRetryAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public Guid? ResultingRecipeId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "System";

    public DateTimeOffset ModifiedAt { get; set; }

    public string ModifiedBy { get; set; } = "System";

    public Recipe? ResultingRecipe { get; set; }
}

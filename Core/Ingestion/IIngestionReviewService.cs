using System.ComponentModel.DataAnnotations;

namespace App.Core.Ingestion;

public sealed record IngestionReviewDiagnosticsDto(
    IReadOnlyList<string> ValidationIssues,
    IReadOnlyList<string> QualityWarnings
);

public sealed record IngestionDraftDiagnosticsRequest([Required] IngestionExtractedDraftDto Draft);

public sealed record IngestionRepairPreviewRequest(
    [Required] IngestionExtractedDraftDto Draft,
    [MaxLength(2000)] string? Hint = null
);

public sealed record IngestionRepairPreviewDto(
    IngestionExtractedDraftDto Draft,
    IngestionReviewDiagnosticsDto Before,
    IngestionReviewDiagnosticsDto After,
    IReadOnlyList<string> Notices
);

public interface IIngestionReviewService
{
    Task<IngestionReviewDiagnosticsDto> GetDiagnosticsAsync(
        Guid id,
        IngestionExtractedDraftDto draft,
        CancellationToken cancellationToken = default
    );

    Task<IngestionRepairPreviewDto> PreviewRepairAsync(
        Guid id,
        IngestionRepairPreviewRequest request,
        CancellationToken cancellationToken = default
    );
}

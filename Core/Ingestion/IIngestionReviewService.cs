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

/// <summary>
/// The state of a background refine pass of a review draft by the Refine Model.
/// IssuesBefore and IssuesAfter count validation issues plus quality warnings.
/// </summary>
public sealed record IngestionRefinementDto(
    IngestionRefinementStatus Status,
    string Model,
    string? Detail,
    int? IssuesBefore,
    int? IssuesAfter,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    bool HasProposal,
    bool BasedOnEarlierDraft
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

    Task<IngestionRefinementDto?> GetRefinementAsync(
        Guid id,
        CancellationToken cancellationToken = default
    );

    /// <summary>Queues a refine pass of the saved review draft with the configured Refine Model.</summary>
    Task<IngestionRefinementDto> RequestRefinementAsync(
        Guid id,
        CancellationToken cancellationToken = default
    );

    Task DismissRefinementAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Merges the stored refine proposal onto the given editor draft for side-by-side review.</summary>
    Task<IngestionRepairPreviewDto> PreviewRefinementAsync(
        Guid id,
        IngestionExtractedDraftDto draft,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Runs the oldest pending refine pass while no import is queued. Returns false when there was
    /// nothing to do. New imports pause a running pass, which is resumed later.
    /// </summary>
    Task<bool> RefineNextPendingDraftAsync(CancellationToken cancellationToken = default);
}

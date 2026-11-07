using App.Core.Ingestion;

namespace App.Data.Entities;

/// <summary>
/// A background refine pass of a review draft by the Refine Model. Kept apart from the job so
/// refine progress never changes the job's ModifiedAt, which the review screen uses to detect edits.
/// </summary>
public sealed class IngestionDraftRefinement : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid IngestionJobId { get; set; }

    public IngestionRefinementStatus Status { get; set; } = IngestionRefinementStatus.Pending;

    public string Model { get; set; } = string.Empty;

    public string? Detail { get; set; }

    /// <summary>The job's draft JSON that the refine pass started from.</summary>
    public string? BaseDraftJson { get; set; }

    /// <summary>The Refine Model's corrected recipe payload, kept only when it lowered the issue count.</summary>
    public string? ProposalJson { get; set; }

    public int? IssuesBefore { get; set; }

    public int? IssuesAfter { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "System";

    public DateTimeOffset ModifiedAt { get; set; }

    public string ModifiedBy { get; set; } = "System";

    public IngestionJob? IngestionJob { get; set; }
}

namespace App.Core.Ingestion;

public interface IIngestionProcessor
{
    Task<int> RecoverInterruptedJobsAsync(CancellationToken cancellationToken = default);

    Task<bool> ProcessNextQueuedJobAsync(CancellationToken cancellationToken = default);

    Task<bool> ProcessJobByIdAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task<int> ProbeAndPromoteWaitingJobsAsync(CancellationToken cancellationToken = default);

    Task<bool> ProcessNextIdleRefinementAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Processes the next explicitly requested ("run now") refinement for either a Recipe
    /// or an IngestionJob, even when background ingestion is paused or idle refinement is disabled.
    /// </summary>
    Task<bool> ProcessNextRequestedRefinementAsync(CancellationToken cancellationToken = default);

    Task<int> SyncPendingRecipeEmbeddingsAsync(CancellationToken cancellationToken = default);
}

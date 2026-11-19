namespace App.Core.Ingestion;

public interface IIngestionProcessor
{
    Task<int> RecoverInterruptedJobsAsync(CancellationToken cancellationToken = default);

    Task<bool> ProcessNextQueuedJobAsync(CancellationToken cancellationToken = default);

    Task<bool> ProcessJobByIdAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task<int> ProbeAndPromoteWaitingJobsAsync(CancellationToken cancellationToken = default);

    Task<bool> ProcessNextIdleRefinementAsync(CancellationToken cancellationToken = default);

    Task<int> SyncPendingRecipeEmbeddingsAsync(CancellationToken cancellationToken = default);
}

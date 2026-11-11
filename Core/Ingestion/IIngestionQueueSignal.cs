namespace App.Core.Ingestion;

public interface IIngestionQueueSignal
{
    void NotifyWorkAvailable();

    ValueTask<bool> WaitForWorkAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default
    );

    IDisposable RegisterActiveJobExecution(Guid jobId, CancellationTokenSource cts);

    /// <summary>
    /// Registers low-priority background work, such as a refine pass. <see cref="NotifyWorkAvailable"/>
    /// cancels it so newly queued imports run first.
    /// </summary>
    IDisposable RegisterPreemptibleExecution(Guid jobId, CancellationTokenSource cts);

    /// <summary>Wakes the worker without cancelling running background work.</summary>
    void NotifyBackgroundWorkAvailable();

    /// <summary>
    /// Cancels running preemptible background work so an interactive LLM request does not wait
    /// behind it for the shared request gate. The background work is requeued, not failed.
    /// </summary>
    void PreemptBackgroundWork();

    Task<IDisposable> AcquireJobExecutionAsync(CancellationToken cancellationToken = default);

    Task<IDisposable> AcquireJobExecutionAsync(
        int maxConcurrency,
        CancellationToken cancellationToken = default
    );

    void CancelActiveExecution(Guid? jobId = null);

    Guid? ActiveJobId { get; }

    IReadOnlyCollection<Guid> ActiveJobIds { get; }

    bool IsJobActive(Guid jobId);
}

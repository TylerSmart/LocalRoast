namespace App.Core.Ingestion;

public interface IIngestionQueueSignal
{
    void NotifyWorkAvailable();

    ValueTask<bool> WaitForWorkAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default
    );

    IDisposable RegisterActiveJobExecution(Guid jobId, CancellationTokenSource cts);

    void CancelActiveExecution();

    Guid? ActiveJobId { get; }
}

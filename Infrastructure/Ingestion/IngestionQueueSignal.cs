using System.Threading.Channels;
using App.Core.Ingestion;

namespace App.Infrastructure.Ingestion;

public sealed class IngestionQueueSignal : IIngestionQueueSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false,
        }
    );

    private readonly object _lock = new();
    private Guid? _activeJobId;
    private CancellationTokenSource? _activeCts;

    public Guid? ActiveJobId
    {
        get
        {
            lock (_lock)
            {
                return _activeJobId;
            }
        }
    }

    public void NotifyWorkAvailable()
    {
        _channel.Writer.TryWrite(true);
    }

    public async ValueTask<bool> WaitForWorkAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            if (await _channel.Reader.WaitToReadAsync(cts.Token))
            {
                while (_channel.Reader.TryRead(out _))
                {
                    // drain pending signals
                }
                return true;
            }

            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // timeout reached
            return false;
        }
    }

    public IDisposable RegisterActiveJobExecution(Guid jobId, CancellationTokenSource cts)
    {
        lock (_lock)
        {
            _activeJobId = jobId;
            _activeCts = cts;
        }

        return new ActiveJobScope(this, jobId, cts);
    }

    public void CancelActiveExecution()
    {
        CancellationTokenSource? ctsToCancel;
        lock (_lock)
        {
            ctsToCancel = _activeCts;
        }

        try
        {
            ctsToCancel?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // ignore
        }
    }

    private void Unregister(Guid jobId, CancellationTokenSource cts)
    {
        lock (_lock)
        {
            if (_activeJobId == jobId && ReferenceEquals(_activeCts, cts))
            {
                _activeJobId = null;
                _activeCts = null;
            }
        }
    }

    private sealed class ActiveJobScope(
        IngestionQueueSignal signal,
        Guid jobId,
        CancellationTokenSource cts
    ) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                signal.Unregister(jobId, cts);
            }
        }
    }
}

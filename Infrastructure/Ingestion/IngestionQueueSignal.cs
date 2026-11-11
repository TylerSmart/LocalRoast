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
    private readonly Dictionary<Guid, (CancellationTokenSource Cts, bool Preemptible)> _activeJobs =
        new();

    private int _activeExecutionCount;
    private readonly Queue<(TaskCompletionSource<IDisposable> Tcs, int MaxConcurrency)> _waiters =
        new();

    public Guid? ActiveJobId
    {
        get
        {
            lock (_lock)
            {
                return _activeJobs.Count > 0 ? _activeJobs.Keys.First() : null;
            }
        }
    }

    public IReadOnlyCollection<Guid> ActiveJobIds
    {
        get
        {
            lock (_lock)
            {
                return _activeJobs.Keys.ToArray();
            }
        }
    }

    public bool IsJobActive(Guid jobId)
    {
        lock (_lock)
        {
            return _activeJobs.ContainsKey(jobId);
        }
    }

    public void NotifyWorkAvailable()
    {
        _channel.Writer.TryWrite(true);
        PreemptBackgroundWork();
    }

    public void PreemptBackgroundWork()
    {
        List<CancellationTokenSource> toCancel = [];
        lock (_lock)
        {
            foreach (var (_, (cts, preemptible)) in _activeJobs)
            {
                if (preemptible)
                {
                    toCancel.Add(cts);
                }
            }
        }

        foreach (var cts in toCancel)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException) { }
        }
    }

    public void NotifyBackgroundWorkAvailable()
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
            return false;
        }
    }

    public IDisposable RegisterActiveJobExecution(Guid jobId, CancellationTokenSource cts) =>
        Register(jobId, cts, preemptible: false);

    public IDisposable RegisterPreemptibleExecution(Guid jobId, CancellationTokenSource cts) =>
        Register(jobId, cts, preemptible: true);

    private ActiveJobScope Register(Guid jobId, CancellationTokenSource cts, bool preemptible)
    {
        lock (_lock)
        {
            _activeJobs[jobId] = (cts, preemptible);
        }

        return new ActiveJobScope(this, jobId, cts);
    }

    public Task<IDisposable> AcquireJobExecutionAsync(
        CancellationToken cancellationToken = default
    ) => AcquireJobExecutionAsync(1, cancellationToken);

    public Task<IDisposable> AcquireJobExecutionAsync(
        int maxConcurrency,
        CancellationToken cancellationToken = default
    )
    {
        var limit = Math.Max(1, maxConcurrency);

        lock (_lock)
        {
            if (_activeExecutionCount < limit)
            {
                _activeExecutionCount++;
                return Task.FromResult<IDisposable>(new JobExecutionScope(this));
            }

            var tcs = new TaskCompletionSource<IDisposable>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );

            if (cancellationToken.CanBeCanceled)
            {
                var reg = cancellationToken.Register(() =>
                {
                    lock (_lock)
                    {
                        tcs.TrySetCanceled(cancellationToken);
                    }
                });

                tcs.Task.ContinueWith(_ => reg.Dispose(), TaskScheduler.Default);
            }

            _waiters.Enqueue((tcs, limit));
            return tcs.Task;
        }
    }

    public void CancelActiveExecution(Guid? jobId = null)
    {
        List<CancellationTokenSource> toCancel = [];
        lock (_lock)
        {
            if (jobId.HasValue)
            {
                if (_activeJobs.TryGetValue(jobId.Value, out var entry))
                {
                    toCancel.Add(entry.Cts);
                }
            }
            else
            {
                foreach (var (_, (cts, _)) in _activeJobs)
                {
                    toCancel.Add(cts);
                }
            }
        }

        foreach (var cts in toCancel)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException) { }
        }
    }

    private void ReleaseExecution()
    {
        lock (_lock)
        {
            _activeExecutionCount--;

            while (_waiters.Count > 0)
            {
                var (waiter, limit) = _waiters.Dequeue();
                if (waiter.Task.IsCompleted)
                {
                    continue;
                }

                if (_activeExecutionCount < limit)
                {
                    _activeExecutionCount++;
                    waiter.TrySetResult(new JobExecutionScope(this));
                    return;
                }
                else
                {
                    // Re-enqueue if cannot satisfy
                    _waiters.Enqueue((waiter, limit));
                    break;
                }
            }
        }
    }

    private void Unregister(Guid jobId, CancellationTokenSource cts)
    {
        lock (_lock)
        {
            if (_activeJobs.TryGetValue(jobId, out var entry) && ReferenceEquals(entry.Cts, cts))
            {
                _activeJobs.Remove(jobId);
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

    private sealed class JobExecutionScope(IngestionQueueSignal signal) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                signal.ReleaseExecution();
            }
        }
    }
}

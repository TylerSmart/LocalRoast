using App.Core.Exceptions;
using App.Core.Ingestion;

namespace App.Infrastructure.Ingestion;

public interface ILlmRequestGate
{
    Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default);

    Task<IDisposable> AcquireAsync(
        EffectiveLlmConnectionConfig? config,
        CancellationToken cancellationToken = default
    );
}

public sealed class LlmRequestGate : ILlmRequestGate
{
    private readonly object _lock = new();
    private int _activeCount;
    private readonly Queue<(TaskCompletionSource<IDisposable> Tcs, int MaxConcurrency)> _waiters =
        new();

    // RPM sliding window tracking
    private readonly Queue<DateTimeOffset> _recentRequestTimestamps = new();

    // RPD tracking
    private DateOnly _currentQuotaDay = DateOnly.FromDateTime(DateTime.UtcNow);
    private int _requestsToday;

    public Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default) =>
        AcquireAsync(null, cancellationToken);

    public async Task<IDisposable> AcquireAsync(
        EffectiveLlmConnectionConfig? config,
        CancellationToken cancellationToken = default
    )
    {
        // 1. Daily Quota Check (RPD)
        if (config?.DailyRequestQuota is int rpd && rpd > 0)
        {
            lock (_lock)
            {
                var today = LlmQuotaSchedule.GetCurrentQuotaDate(
                    DateTimeOffset.UtcNow,
                    config.DailyQuotaResetTimezone,
                    config.DailyQuotaResetTime
                );

                if (today != _currentQuotaDay)
                {
                    _currentQuotaDay = today;
                    _requestsToday = 0;
                }

                if (_requestsToday >= rpd)
                {
                    throw new LlmHostUnavailableException(
                        $"Daily request limit reached ({_requestsToday} / {rpd} requests today).",
                        isQuotaExhausted: true
                    );
                }

                _requestsToday++;
            }
        }

        // 2. RPM Pacing (Sliding Window)
        if (config?.RequestsPerMinute is int rpm && rpm > 0)
        {
            TimeSpan delay = TimeSpan.Zero;
            lock (_lock)
            {
                var now = DateTimeOffset.UtcNow;
                var windowStart = now.AddSeconds(-60);

                while (
                    _recentRequestTimestamps.Count > 0
                    && _recentRequestTimestamps.Peek() < windowStart
                )
                {
                    _recentRequestTimestamps.Dequeue();
                }

                if (_recentRequestTimestamps.Count >= rpm)
                {
                    var oldest = _recentRequestTimestamps.Peek();
                    var requiredTime = oldest.AddSeconds(60);
                    if (requiredTime > now)
                    {
                        delay = requiredTime - now;
                    }
                }
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            lock (_lock)
            {
                _recentRequestTimestamps.Enqueue(DateTimeOffset.UtcNow);
            }
        }

        // 3. Concurrency Semaphore (MaxConcurrentRequests)
        var limit = Math.Clamp(config?.MaxConcurrentRequests ?? 1, 1, 5);

        Task<IDisposable> waitTask;
        lock (_lock)
        {
            if (_activeCount < limit)
            {
                _activeCount++;
                return new GateReleaser(this);
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
            waitTask = tcs.Task;
        }

        return await waitTask;
    }

    private void Release()
    {
        lock (_lock)
        {
            _activeCount--;

            while (_waiters.Count > 0)
            {
                var (waiter, limit) = _waiters.Dequeue();
                if (waiter.Task.IsCompleted)
                {
                    continue;
                }

                if (_activeCount < limit)
                {
                    _activeCount++;
                    waiter.TrySetResult(new GateReleaser(this));
                    return;
                }
                else
                {
                    _waiters.Enqueue((waiter, limit));
                    break;
                }
            }
        }
    }

    private sealed class GateReleaser(LlmRequestGate gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}

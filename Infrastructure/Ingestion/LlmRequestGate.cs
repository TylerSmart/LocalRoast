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
    private const string DefaultConnectionKey = "default";

    private readonly object _lock = new();

    // Each saved connection (machine/provider) gets its own concurrency, pacing and quota state
    // so a busy or rate-limited host never blocks requests bound for another one.
    private readonly Dictionary<string, ConnectionState> _states = new(StringComparer.Ordinal);

    public Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default) =>
        AcquireAsync(null, cancellationToken);

    public async Task<IDisposable> AcquireAsync(
        EffectiveLlmConnectionConfig? config,
        CancellationToken cancellationToken = default
    )
    {
        ConnectionState state;
        lock (_lock)
        {
            var key = config?.ConnectionKey is { Length: > 0 } connectionKey
                ? connectionKey
                : DefaultConnectionKey;
            if (!_states.TryGetValue(key, out state!))
            {
                state = new ConnectionState();
                _states[key] = state;
            }
        }

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

                if (today != state.CurrentQuotaDay)
                {
                    state.CurrentQuotaDay = today;
                    state.RequestsToday = 0;
                }

                if (state.RequestsToday >= rpd)
                {
                    throw new LlmHostUnavailableException(
                        $"Daily request limit reached ({state.RequestsToday} / {rpd} requests today).",
                        isQuotaExhausted: true
                    );
                }

                state.RequestsToday++;
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
                    state.RecentRequestTimestamps.Count > 0
                    && state.RecentRequestTimestamps.Peek() < windowStart
                )
                {
                    state.RecentRequestTimestamps.Dequeue();
                }

                if (state.RecentRequestTimestamps.Count >= rpm)
                {
                    var oldest = state.RecentRequestTimestamps.Peek();
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
                state.RecentRequestTimestamps.Enqueue(DateTimeOffset.UtcNow);
            }
        }

        // 3. Concurrency Semaphore (MaxConcurrentRequests)
        var limit = Math.Clamp(config?.MaxConcurrentRequests ?? 1, 1, 5);

        Task<IDisposable> waitTask;
        lock (_lock)
        {
            if (state.ActiveCount < limit)
            {
                state.ActiveCount++;
                return new GateReleaser(this, state);
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

            state.Waiters.Enqueue((tcs, limit));
            waitTask = tcs.Task;
        }

        return await waitTask;
    }

    private void Release(ConnectionState state)
    {
        lock (_lock)
        {
            state.ActiveCount--;

            while (state.Waiters.Count > 0)
            {
                var (waiter, limit) = state.Waiters.Dequeue();
                if (waiter.Task.IsCompleted)
                {
                    continue;
                }

                if (state.ActiveCount < limit)
                {
                    state.ActiveCount++;
                    waiter.TrySetResult(new GateReleaser(this, state));
                    return;
                }
                else
                {
                    state.Waiters.Enqueue((waiter, limit));
                    break;
                }
            }
        }
    }

    private sealed class ConnectionState
    {
        public int ActiveCount;
        public readonly Queue<(TaskCompletionSource<IDisposable> Tcs, int MaxConcurrency)> Waiters =
            new();

        // RPM sliding window tracking
        public readonly Queue<DateTimeOffset> RecentRequestTimestamps = new();

        // RPD tracking
        public DateOnly CurrentQuotaDay = DateOnly.FromDateTime(DateTime.UtcNow);
        public int RequestsToday;
    }

    private sealed class GateReleaser(LlmRequestGate gate, ConnectionState state) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                gate.Release(state);
            }
        }
    }
}

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

    // Independent concurrency, pacing and daily quota state per connection ("conn:<id>")
    // and per saved (connection, model) ("model:<connectionId>:<model>").
    private readonly Dictionary<string, GateBucketState> _states = new(StringComparer.Ordinal);

    public Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default) =>
        AcquireAsync(null, cancellationToken);

    public async Task<IDisposable> AcquireAsync(
        EffectiveLlmConnectionConfig? config,
        CancellationToken cancellationToken = default
    )
    {
        var connectionKey = config?.ConnectionKey is { Length: > 0 } ck ? ck : DefaultConnectionKey;
        var modelKey = config?.ModelKey is { Length: > 0 } mk ? mk : null;

        GateBucketState connectionState;
        GateBucketState? modelState = null;
        lock (_lock)
        {
            connectionState = GetOrCreateBucket($"conn:{connectionKey}");
            if (modelKey is not null)
            {
                modelState = GetOrCreateBucket($"model:{modelKey}");
            }
        }

        // 1. Atomic Dual Daily Quota Check (Connection RPD + Model RPD)
        var connectionRpd = config?.DailyRequestQuota is int cRpd && cRpd > 0 ? cRpd : (int?)null;
        var modelRpd =
            modelState is not null && config?.ModelDailyRequestQuota is int mRpd && mRpd > 0
                ? mRpd
                : (int?)null;

        DateOnly? connIncrementedDay = null;
        DateOnly? modelIncrementedDay = null;

        if (connectionRpd.HasValue || modelRpd.HasValue)
        {
            lock (_lock)
            {
                var nowUtc = DateTimeOffset.UtcNow;

                DateOnly connToday = default;
                if (connectionRpd.HasValue)
                {
                    connToday = LlmQuotaSchedule.GetCurrentQuotaDate(
                        nowUtc,
                        config!.DailyQuotaResetTimezone,
                        config.DailyQuotaResetTime
                    );
                    if (connToday != connectionState.CurrentQuotaDay)
                    {
                        connectionState.CurrentQuotaDay = connToday;
                        connectionState.RequestsToday = 0;
                    }
                }

                DateOnly modelToday = default;
                if (modelRpd.HasValue)
                {
                    modelToday = LlmQuotaSchedule.GetCurrentQuotaDate(
                        nowUtc,
                        config!.EffectiveQuotaResetTimezone,
                        config.EffectiveQuotaResetTime
                    );
                    if (modelToday != modelState!.CurrentQuotaDay)
                    {
                        modelState.CurrentQuotaDay = modelToday;
                        modelState.RequestsToday = 0;
                    }
                }

                // Validate both tiers BEFORE incrementing either counter so a blocked model
                // request never consumes connection quota (and vice versa).
                if (connectionRpd.HasValue && connectionState.RequestsToday >= connectionRpd.Value)
                {
                    throw new LlmHostUnavailableException(
                        $"Daily request limit reached ({connectionState.RequestsToday} / {connectionRpd.Value} requests today).",
                        isQuotaExhausted: true
                    )
                    {
                        ExhaustedQuotaScope = LlmQuotaScope.Connection,
                        ExhaustedGateKey = connectionKey,
                        QuotaResetTimezone = config!.DailyQuotaResetTimezone,
                        QuotaResetTime = config.DailyQuotaResetTime,
                        SafeguardsEnabled = config.EnableRateLimitSafeguards,
                    };
                }

                if (modelRpd.HasValue && modelState!.RequestsToday >= modelRpd.Value)
                {
                    var modelLabel = config!.TextModel ?? config.VisionModel ?? modelKey;
                    throw new LlmHostUnavailableException(
                        $"Daily request limit reached for model '{modelLabel}' ({modelState.RequestsToday} / {modelRpd.Value} requests today).",
                        isQuotaExhausted: true
                    )
                    {
                        ExhaustedQuotaScope = LlmQuotaScope.Model,
                        ExhaustedGateKey = modelKey,
                        QuotaResetTimezone = config.EffectiveQuotaResetTimezone,
                        QuotaResetTime = config.EffectiveQuotaResetTime,
                        SafeguardsEnabled = config.EnableRateLimitSafeguards,
                    };
                }

                if (connectionRpd.HasValue)
                {
                    connectionState.RequestsToday++;
                    connIncrementedDay = connToday;
                }

                if (modelRpd.HasValue)
                {
                    modelState!.RequestsToday++;
                    modelIncrementedDay = modelToday;
                }
            }
        }

        try
        {
            // 2. Dual Sliding-Window RPM Pacing (Connection RPM + Model RPM)
            var connectionRpm =
                config?.RequestsPerMinute is int cRpm && cRpm > 0 ? cRpm : (int?)null;
            var modelRpm =
                modelState is not null && config?.ModelRequestsPerMinute is int mRpm && mRpm > 0
                    ? mRpm
                    : (int?)null;

            if (connectionRpm.HasValue || modelRpm.HasValue)
            {
                while (true)
                {
                    TimeSpan delay;
                    lock (_lock)
                    {
                        var now = DateTimeOffset.UtcNow;
                        var connDelay = connectionRpm.HasValue
                            ? ComputeRpmDelayLocked(connectionState, connectionRpm.Value, now)
                            : TimeSpan.Zero;
                        var modelDelay = modelRpm.HasValue
                            ? ComputeRpmDelayLocked(modelState!, modelRpm.Value, now)
                            : TimeSpan.Zero;

                        delay = connDelay >= modelDelay ? connDelay : modelDelay;
                        if (delay <= TimeSpan.Zero)
                        {
                            if (connectionRpm.HasValue)
                            {
                                connectionState.RecentRequestTimestamps.Enqueue(now);
                            }
                            if (modelRpm.HasValue)
                            {
                                modelState!.RecentRequestTimestamps.Enqueue(now);
                            }
                            break;
                        }
                    }

                    await Task.Delay(delay, cancellationToken);
                }
            }

            // 3. Hierarchical Concurrency Semaphores (always Connection first, then Model)
            var connectionLimit = Math.Clamp(config?.MaxConcurrentRequests ?? 1, 1, 5);
            await AcquireConcurrencySlotAsync(connectionState, connectionLimit, cancellationToken);

            var acquiredModelSlot = false;
            if (modelState is not null && config?.ModelMaxConcurrentRequests is int mConc)
            {
                var modelLimit = Math.Clamp(mConc, 1, 5);
                try
                {
                    await AcquireConcurrencySlotAsync(modelState, modelLimit, cancellationToken);
                    acquiredModelSlot = true;
                }
                catch
                {
                    ReleaseBucket(connectionState);
                    throw;
                }
            }

            return new GateReleaser(this, connectionState, acquiredModelSlot ? modelState : null);
        }
        catch
        {
            // Roll back reserved daily quota counts if the caller was canceled before acquiring a slot.
            if (connIncrementedDay.HasValue || modelIncrementedDay.HasValue)
            {
                lock (_lock)
                {
                    if (
                        connIncrementedDay.HasValue
                        && connectionState.CurrentQuotaDay == connIncrementedDay.Value
                        && connectionState.RequestsToday > 0
                    )
                    {
                        connectionState.RequestsToday--;
                    }

                    if (
                        modelIncrementedDay.HasValue
                        && modelState is not null
                        && modelState.CurrentQuotaDay == modelIncrementedDay.Value
                        && modelState.RequestsToday > 0
                    )
                    {
                        modelState.RequestsToday--;
                    }
                }
            }

            throw;
        }
    }

    private GateBucketState GetOrCreateBucket(string key)
    {
        if (!_states.TryGetValue(key, out var state))
        {
            state = new GateBucketState();
            _states[key] = state;
        }
        return state;
    }

    private static TimeSpan ComputeRpmDelayLocked(
        GateBucketState state,
        int rpm,
        DateTimeOffset now
    )
    {
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
                return requiredTime - now;
            }
        }

        return TimeSpan.Zero;
    }

    private Task AcquireConcurrencySlotAsync(
        GateBucketState state,
        int limit,
        CancellationToken cancellationToken
    )
    {
        lock (_lock)
        {
            if (state.ActiveCount < limit)
            {
                state.ActiveCount++;
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

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
            return tcs.Task;
        }
    }

    private void Release(GateBucketState connectionState, GateBucketState? modelState)
    {
        lock (_lock)
        {
            if (modelState is not null)
            {
                ReleaseBucketLocked(modelState);
            }
            ReleaseBucketLocked(connectionState);
        }
    }

    private void ReleaseBucket(GateBucketState state)
    {
        lock (_lock)
        {
            ReleaseBucketLocked(state);
        }
    }

    private static void ReleaseBucketLocked(GateBucketState state)
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
                waiter.TrySetResult();
                return;
            }

            state.Waiters.Enqueue((waiter, limit));
            break;
        }
    }

    private sealed class GateBucketState
    {
        public int ActiveCount;
        public readonly Queue<(TaskCompletionSource Tcs, int MaxConcurrency)> Waiters = new();

        // RPM sliding window tracking
        public readonly Queue<DateTimeOffset> RecentRequestTimestamps = new();

        // RPD tracking
        public DateOnly CurrentQuotaDay = DateOnly.FromDateTime(DateTime.UtcNow);
        public int RequestsToday;
    }

    private sealed class GateReleaser(
        LlmRequestGate gate,
        GateBucketState connectionState,
        GateBucketState? modelState
    ) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                gate.Release(connectionState, modelState);
            }
        }
    }
}

using App.Core.Ingestion;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace App.API.Hubs;

public sealed class SignalRIngestionRealtimeNotifier(
    IHubContext<IngestionHub, IIngestionClient> hubContext,
    ILogger<SignalRIngestionRealtimeNotifier> logger
) : IIngestionRealtimeNotifier
{
    private int _subscriberCount;

    public bool HasSubscribers => Volatile.Read(ref _subscriberCount) > 0;

    public void IncrementSubscribers() => Interlocked.Increment(ref _subscriberCount);

    public void DecrementSubscribers()
    {
        while (true)
        {
            var current = Volatile.Read(ref _subscriberCount);
            if (current <= 0)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _subscriberCount, current - 1, current) == current)
            {
                return;
            }
        }
    }

    public async Task NotifyWorkloadUpsertedAsync(
        LlmActiveWorkloadDto workload,
        CancellationToken cancellationToken = default
    )
    {
        if (!HasSubscribers)
        {
            return;
        }

        try
        {
            await hubContext
                .Clients.Group(IngestionHubGroups.MonitorsGroup)
                .WorkloadUpserted(workload);
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Failed to broadcast SignalR WorkloadUpserted for workload {WorkloadId}.",
                workload.WorkloadId
            );
        }
    }

    public async Task NotifyWorkloadMetaUpdatedAsync(
        LlmWorkloadMetaUpdatedDto update,
        CancellationToken cancellationToken = default
    )
    {
        if (!HasSubscribers)
        {
            return;
        }

        try
        {
            await hubContext
                .Clients.Group(IngestionHubGroups.MonitorsGroup)
                .WorkloadMetaUpdated(update);
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Failed to broadcast SignalR WorkloadMetaUpdated for workload {WorkloadId}.",
                update.WorkloadId
            );
        }
    }

    public async Task NotifyCallStartedAsync(
        LlmCallStartedDto started,
        CancellationToken cancellationToken = default
    )
    {
        if (!HasSubscribers)
        {
            return;
        }

        try
        {
            await hubContext.Clients.Group(IngestionHubGroups.MonitorsGroup).CallStarted(started);
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Failed to broadcast SignalR CallStarted for call {CallId}.",
                started.Call.CallId
            );
        }
    }

    public async Task NotifyCallDeltaAsync(
        LlmCallStreamDeltaDto delta,
        CancellationToken cancellationToken = default
    )
    {
        if (!HasSubscribers)
        {
            return;
        }

        try
        {
            await hubContext.Clients.Group(IngestionHubGroups.MonitorsGroup).CallStreamDelta(delta);
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Failed to broadcast SignalR CallStreamDelta for call {CallId}.",
                delta.CallId
            );
        }
    }

    public async Task NotifyCallCompletedAsync(
        LlmCallCompletedDto completed,
        CancellationToken cancellationToken = default
    )
    {
        if (!HasSubscribers)
        {
            return;
        }

        try
        {
            await hubContext
                .Clients.Group(IngestionHubGroups.MonitorsGroup)
                .CallCompleted(completed);
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Failed to broadcast SignalR CallCompleted for call {CallId}.",
                completed.CallId
            );
        }
    }

    public async Task NotifyQueueChangedAsync(
        IngestionQueueChangedEventDto queueEvent,
        CancellationToken cancellationToken = default
    )
    {
        if (!HasSubscribers)
        {
            return;
        }

        try
        {
            await hubContext
                .Clients.Group(IngestionHubGroups.MonitorsGroup)
                .QueueChanged(queueEvent);
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Failed to broadcast SignalR QueueChanged event ({Reason}).",
                queueEvent.Reason
            );
        }
    }
}

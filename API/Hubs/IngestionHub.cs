using App.Core.Ingestion;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace App.API.Hubs;

[Authorize]
public sealed class IngestionHub(
    IIngestionLiveTracker liveTracker,
    SignalRIngestionRealtimeNotifier notifier
) : Hub<IIngestionClient>
{
    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, IngestionHubGroups.MonitorsGroup);
        notifier.IncrementSubscribers();
        await Clients.Caller.LiveSnapshot(liveTracker.GetSnapshot());
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        notifier.DecrementSubscribers();
        await base.OnDisconnectedAsync(exception);
    }

    public Task<IngestionLiveSnapshotDto> RequestSnapshot() =>
        Task.FromResult(liveTracker.GetSnapshot());
}

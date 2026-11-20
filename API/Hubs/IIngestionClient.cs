using App.Core.Ingestion;

namespace App.API.Hubs;

public interface IIngestionClient
{
    Task LiveSnapshot(IngestionLiveSnapshotDto snapshot);

    Task WorkloadUpserted(LlmActiveWorkloadDto workload);

    Task WorkloadMetaUpdated(LlmWorkloadMetaUpdatedDto update);

    Task CallStarted(LlmCallStartedDto started);

    Task CallStreamDelta(LlmCallStreamDeltaDto delta);

    Task CallCompleted(LlmCallCompletedDto completed);

    Task QueueChanged(IngestionQueueChangedEventDto queueEvent);
}

namespace App.Core.Ingestion;

public interface IIngestionRealtimeNotifier
{
    bool HasSubscribers { get; }

    Task NotifyWorkloadUpsertedAsync(
        LlmActiveWorkloadDto workload,
        CancellationToken cancellationToken = default
    );

    Task NotifyWorkloadMetaUpdatedAsync(
        LlmWorkloadMetaUpdatedDto update,
        CancellationToken cancellationToken = default
    );

    Task NotifyCallStartedAsync(
        LlmCallStartedDto started,
        CancellationToken cancellationToken = default
    );

    Task NotifyCallDeltaAsync(
        LlmCallStreamDeltaDto delta,
        CancellationToken cancellationToken = default
    );

    Task NotifyCallCompletedAsync(
        LlmCallCompletedDto completed,
        CancellationToken cancellationToken = default
    );

    Task NotifyQueueChangedAsync(
        IngestionQueueChangedEventDto queueEvent,
        CancellationToken cancellationToken = default
    );
}

public interface ILlmWorkloadScope : IDisposable
{
    Guid WorkloadId { get; }

    void UpdateMetadata(
        string? title = null,
        string? subtitle = null,
        string? thumbnailUrl = null,
        string? activeModel = null,
        string? statusDetail = null
    );

    void SetSegments(IReadOnlyList<LlmWorkflowSegmentDto> segments);

    void ActivateSegment(
        string segmentKey,
        string? subStatus = null,
        string? statusDetail = null,
        string? activeModel = null
    );

    void UpdateSegmentSubStatus(string segmentKey, string? subStatus, string? statusDetail = null);

    void CompleteSegment(string segmentKey);

    void SkipSegment(string segmentKey);

    void FailSegment(string segmentKey, string? error = null);

    void CompleteWorkload(string? statusDetail = null);

    void FailWorkload(string? errorDetail = null);

    void CancelWorkload(string? statusDetail = null);
}

public interface ILlmCallScope : IDisposable
{
    Guid WorkloadId { get; }
    Guid CallId { get; }

    void AppendThinking(string text);

    void AppendOutput(string text);

    void UpdateProgress(int? evalCount = null, double? tokensPerSecond = null);

    void Complete(int? evalCount = null, double? tokensPerSecond = null);

    void Fail(string errorMessage);
}

public interface IIngestionLiveTracker
{
    ILlmWorkloadScope? CurrentWorkload { get; }

    IngestionLiveSnapshotDto GetSnapshot();

    ILlmWorkloadScope BeginWorkload(
        LlmWorkloadKind kind,
        string title,
        IReadOnlyList<LlmWorkflowSegmentDto> segments,
        Guid? jobId = null,
        Guid? recipeId = null,
        IngestionChannel? channel = null,
        string? subtitle = null,
        string? thumbnailUrl = null,
        string? activeModel = null,
        string? statusDetail = null
    );

    ILlmCallScope BeginLlmCall(
        string stageKey,
        string stageLabel,
        string model,
        bool thinkingEnabled,
        string? fallbackWorkloadTitle = null,
        LlmWorkloadKind fallbackKind = LlmWorkloadKind.KitchenHelper
    );
}

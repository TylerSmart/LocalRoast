using System.Text.Json.Serialization;

namespace App.Core.Ingestion;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LlmWorkloadKind
{
    RecipeIngestion = 0,
    JobRefinement = 1,
    RecipeRefinement = 2,
    EmbeddingSync = 3,
    MealPlanDraft = 4,
    KitchenHelper = 5,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LlmWorkloadStatus
{
    Running = 0,
    Completed = 1,
    Failed = 2,
    Canceled = 3,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LlmWorkflowSegmentState
{
    Pending = 0,
    Active = 1,
    Completed = 2,
    Skipped = 3,
    Failed = 4,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LlmCallPhase
{
    WaitingForModel = 0,
    Thinking = 1,
    GeneratingOutput = 2,
    Completed = 3,
    Failed = 4,
}

public sealed record LlmWorkflowSegmentDto(
    string Key,
    string Label,
    LlmWorkflowSegmentState State,
    string? SubStatus = null,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? CompletedAtUtc = null
);

public sealed record LlmCallTraceDto(
    Guid CallId,
    string StageKey,
    string StageLabel,
    string Model,
    bool ThinkingEnabled,
    LlmCallPhase Phase,
    string ThinkingText,
    string OutputText,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? ThinkingCompletedAtUtc = null,
    DateTimeOffset? CompletedAtUtc = null,
    int? EvalCount = null,
    double? TokensPerSecond = null,
    string? ErrorMessage = null
);

public sealed record LlmActiveWorkloadDto(
    Guid WorkloadId,
    LlmWorkloadKind Kind,
    LlmWorkloadStatus Status,
    string Title,
    DateTimeOffset StartedAtUtc,
    Guid? JobId = null,
    Guid? RecipeId = null,
    IngestionChannel? Channel = null,
    string? Subtitle = null,
    string? ThumbnailUrl = null,
    string? ActiveModel = null,
    string? StatusDetail = null,
    DateTimeOffset? CompletedAtUtc = null,
    IReadOnlyList<LlmWorkflowSegmentDto>? Segments = null,
    IReadOnlyList<LlmCallTraceDto>? Calls = null
)
{
    public IReadOnlyList<LlmWorkflowSegmentDto> Segments { get; init; } = Segments ?? [];
    public IReadOnlyList<LlmCallTraceDto> Calls { get; init; } = Calls ?? [];
}

public sealed record IngestionLiveSnapshotDto(
    IReadOnlyList<LlmActiveWorkloadDto> ActiveWorkloads,
    IReadOnlyList<LlmActiveWorkloadDto> RecentWorkloads
);

public sealed record LlmWorkloadMetaUpdatedDto(
    Guid WorkloadId,
    LlmWorkloadStatus Status,
    string Title,
    string? Subtitle,
    string? ThumbnailUrl,
    string? ActiveModel,
    string? StatusDetail,
    DateTimeOffset? CompletedAtUtc,
    IReadOnlyList<LlmWorkflowSegmentDto> Segments
);

public sealed record LlmCallStartedDto(Guid WorkloadId, LlmCallTraceDto Call);

public sealed record LlmCallStreamDeltaDto(
    Guid WorkloadId,
    Guid CallId,
    LlmCallPhase Phase,
    string? ThinkingDelta,
    string? OutputDelta,
    DateTimeOffset? ThinkingCompletedAtUtc,
    int? EvalCount,
    double? TokensPerSecond
);

public sealed record LlmCallCompletedDto(
    Guid WorkloadId,
    Guid CallId,
    LlmCallPhase Phase,
    DateTimeOffset? ThinkingCompletedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int? EvalCount,
    double? TokensPerSecond,
    string? ErrorMessage
);

public sealed record IngestionQueueChangedEventDto(
    string Reason,
    Guid? JobId = null,
    IngestionQueueSummaryDto? Summary = null,
    bool? EnableIngestion = null,
    DateTimeOffset? PausedUntilUtc = null,
    string? PauseReason = null
);

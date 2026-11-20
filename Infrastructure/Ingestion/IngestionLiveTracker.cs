using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using App.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace App.Infrastructure.Ingestion;

public sealed class IngestionLiveTracker : IIngestionLiveTracker, IDisposable
{
    public const int MaxThinkingCharsPerCall = 32_768;
    public const int MaxOutputCharsPerCall = 16_384;
    public const int MaxRecentWorkloads = 3;
    private const string TruncationNotice = "\n… [truncated in live viewer]";

    private static readonly TimeSpan DefaultDeltaFlushInterval = TimeSpan.FromMilliseconds(75);

    private readonly IIngestionRealtimeNotifier? _notifier;
    private readonly ILogger<IngestionLiveTracker>? _logger;
    private readonly TimeSpan _deltaFlushInterval;
    private readonly TimeProvider _timeProvider;

    private readonly object _stateLock = new();
    private readonly Dictionary<Guid, WorkloadSession> _activeSessions = new();
    private readonly List<WorkloadSession> _activeOrder = [];
    private readonly LinkedList<LlmActiveWorkloadDto> _recentWorkloads = new();

    private readonly AsyncLocal<WorkloadScope?> _currentScope = new();

    private readonly Channel<Func<Task>> _broadcastChannel = Channel.CreateUnbounded<Func<Task>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false }
    );
    private readonly Task _broadcastPumpTask;
    private int _disposed;

    public IngestionLiveTracker(
        IIngestionRealtimeNotifier? notifier = null,
        ILogger<IngestionLiveTracker>? logger = null,
        TimeSpan? deltaFlushInterval = null,
        TimeProvider? timeProvider = null
    )
    {
        _notifier = notifier;
        _logger = logger;
        _deltaFlushInterval = deltaFlushInterval ?? DefaultDeltaFlushInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _broadcastPumpTask = Task.Run(PumpBroadcastsAsync);
    }

    public ILlmWorkloadScope? CurrentWorkload => _currentScope.Value;

    public IngestionLiveSnapshotDto GetSnapshot()
    {
        lock (_stateLock)
        {
            var active = _activeOrder.Select(s => s.ToDto()).ToList();
            var recent = _recentWorkloads.ToList();
            return new IngestionLiveSnapshotDto(active, recent);
        }
    }

    public ILlmWorkloadScope BeginWorkload(
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
    )
    {
        var now = _timeProvider.GetUtcNow();
        var session = new WorkloadSession(
            tracker: this,
            workloadId: Guid.NewGuid(),
            kind: kind,
            title: string.IsNullOrWhiteSpace(title) ? "LLM Task" : title.Trim(),
            startedAtUtc: now,
            jobId: jobId,
            recipeId: recipeId,
            channel: channel,
            subtitle: subtitle,
            thumbnailUrl: thumbnailUrl,
            activeModel: activeModel,
            statusDetail: statusDetail,
            segments: segments
        );

        LlmActiveWorkloadDto dto;
        lock (_stateLock)
        {
            _activeSessions[session.WorkloadId] = session;
            _activeOrder.Add(session);
            dto = session.ToDto();
        }

        EnqueueBroadcast(() =>
            _notifier is not null ? _notifier.NotifyWorkloadUpsertedAsync(dto) : Task.CompletedTask
        );

        var previous = _currentScope.Value;
        var scope = new WorkloadScope(this, session, previous);
        _currentScope.Value = scope;
        return scope;
    }

    public ILlmCallScope BeginLlmCall(
        string stageKey,
        string stageLabel,
        string model,
        bool thinkingEnabled,
        string? fallbackWorkloadTitle = null,
        LlmWorkloadKind fallbackKind = LlmWorkloadKind.KitchenHelper
    )
    {
        var currentScope = _currentScope.Value;
        WorkloadScope? ownedWorkloadScope = null;
        WorkloadSession session;

        if (currentScope is not null && !currentScope.IsDisposed)
        {
            session = currentScope.Session;
        }
        else
        {
            var title = string.IsNullOrWhiteSpace(fallbackWorkloadTitle)
                ? stageLabel
                : fallbackWorkloadTitle.Trim();
            var now = _timeProvider.GetUtcNow();
            ownedWorkloadScope = (WorkloadScope)BeginWorkload(
                kind: fallbackKind,
                title: title,
                segments:
                [
                    new LlmWorkflowSegmentDto(
                        Key: stageKey,
                        Label: stageLabel,
                        State: LlmWorkflowSegmentState.Active,
                        StartedAtUtc: now
                    ),
                ],
                activeModel: model,
                statusDetail: $"{stageLabel} via {model}..."
            );
            session = ownedWorkloadScope.Session;
        }

        var nowUtc = _timeProvider.GetUtcNow();
        var effectiveStageKey = stageKey;
        var effectiveStageLabel = stageLabel;
        LlmCallTraceDto startedDto;
        LlmWorkloadMetaUpdatedDto? metaUpdate = null;

        CallSession callSession;
        lock (_stateLock)
        {
            var activeSegment = session.GetActiveSegmentUnlocked();
            if (activeSegment is not null)
            {
                if (string.IsNullOrWhiteSpace(effectiveStageKey))
                {
                    effectiveStageKey = activeSegment.Key;
                }

                if (
                    !string.IsNullOrWhiteSpace(activeSegment.SubStatus)
                    && !effectiveStageLabel.Contains(
                        activeSegment.SubStatus,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    effectiveStageLabel = $"{effectiveStageLabel} · {activeSegment.SubStatus}";
                }
            }

            callSession = new CallSession(
                tracker: this,
                workloadId: session.WorkloadId,
                callId: Guid.NewGuid(),
                stageKey: effectiveStageKey,
                stageLabel: effectiveStageLabel,
                model: string.IsNullOrWhiteSpace(model) ? "unknown" : model.Trim(),
                thinkingEnabled: thinkingEnabled,
                startedAtUtc: nowUtc
            );

            session.AddCallUnlocked(callSession);
            if (!string.Equals(session.ActiveModel, callSession.Model, StringComparison.Ordinal))
            {
                session.ActiveModel = callSession.Model;
                metaUpdate = session.ToMetaUpdatedDtoUnlocked();
            }

            startedDto = callSession.ToDto();
        }

        if (metaUpdate is not null)
        {
            var update = metaUpdate;
            EnqueueBroadcast(() =>
                _notifier is not null
                    ? _notifier.NotifyWorkloadMetaUpdatedAsync(update)
                    : Task.CompletedTask
            );
        }

        var startedEvent = new LlmCallStartedDto(session.WorkloadId, startedDto);
        EnqueueBroadcast(() =>
            _notifier is not null
                ? _notifier.NotifyCallStartedAsync(startedEvent)
                : Task.CompletedTask
        );

        return new CallScope(callSession, ownedWorkloadScope);
    }

    public async Task DrainBroadcastsAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (
            _broadcastChannel.Writer.TryWrite(() =>
            {
                tcs.TrySetResult();
                return Task.CompletedTask;
            })
        )
        {
            await tcs.Task;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _broadcastChannel.Writer.TryComplete();
        }
    }

    private void EnqueueBroadcast(Func<Task> action)
    {
        if (_notifier is null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _broadcastChannel.Writer.TryWrite(action);
    }

    private async Task PumpBroadcastsAsync()
    {
        try
        {
            await foreach (var work in _broadcastChannel.Reader.ReadAllAsync())
            {
                try
                {
                    await work();
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Failed to dispatch ingestion live broadcast.");
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Ingestion live broadcast pump terminated.");
        }
    }

    private void FinalizeSession(
        WorkloadSession session,
        LlmWorkloadStatus finalStatus,
        string? statusDetail
    )
    {
        LlmWorkloadMetaUpdatedDto? metaUpdate = null;

        lock (_stateLock)
        {
            if (session.IsFinalized)
            {
                return;
            }

            session.FinalizeUnlocked(finalStatus, statusDetail, _timeProvider.GetUtcNow());
            _activeSessions.Remove(session.WorkloadId);
            _activeOrder.Remove(session);

            var snapshot = session.ToDto();
            _recentWorkloads.AddFirst(snapshot);
            while (_recentWorkloads.Count > MaxRecentWorkloads)
            {
                _recentWorkloads.RemoveLast();
            }

            metaUpdate = session.ToMetaUpdatedDtoUnlocked();
        }

        if (metaUpdate is not null)
        {
            var update = metaUpdate;
            EnqueueBroadcast(() =>
                _notifier is not null
                    ? _notifier.NotifyWorkloadMetaUpdatedAsync(update)
                    : Task.CompletedTask
            );
        }
    }

    private void BroadcastMetaUpdate(WorkloadSession session)
    {
        LlmWorkloadMetaUpdatedDto update;
        lock (_stateLock)
        {
            update = session.ToMetaUpdatedDtoUnlocked();
        }

        EnqueueBroadcast(() =>
            _notifier is not null
                ? _notifier.NotifyWorkloadMetaUpdatedAsync(update)
                : Task.CompletedTask
        );
    }

    private sealed class WorkloadScope(
        IngestionLiveTracker tracker,
        WorkloadSession session,
        WorkloadScope? previous
    ) : ILlmWorkloadScope
    {
        private int _disposed;

        public Guid WorkloadId => session.WorkloadId;
        public WorkloadSession Session => session;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void UpdateMetadata(
            string? title = null,
            string? subtitle = null,
            string? thumbnailUrl = null,
            string? activeModel = null,
            string? statusDetail = null
        )
        {
            lock (tracker._stateLock)
            {
                if (session.IsFinalized)
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(title))
                {
                    session.Title = title.Trim();
                }

                if (subtitle is not null)
                {
                    session.Subtitle = subtitle;
                }

                if (thumbnailUrl is not null)
                {
                    session.ThumbnailUrl = thumbnailUrl;
                }

                if (!string.IsNullOrWhiteSpace(activeModel))
                {
                    session.ActiveModel = activeModel.Trim();
                }

                if (statusDetail is not null)
                {
                    session.StatusDetail = statusDetail;
                }
            }

            tracker.BroadcastMetaUpdate(session);
        }

        public void SetSegments(IReadOnlyList<LlmWorkflowSegmentDto> segments)
        {
            lock (tracker._stateLock)
            {
                if (session.IsFinalized)
                {
                    return;
                }

                session.ReplaceSegmentsUnlocked(segments);
            }

            tracker.BroadcastMetaUpdate(session);
        }

        public void ActivateSegment(
            string segmentKey,
            string? subStatus = null,
            string? statusDetail = null,
            string? activeModel = null
        )
        {
            lock (tracker._stateLock)
            {
                if (session.IsFinalized)
                {
                    return;
                }

                session.ActivateSegmentUnlocked(
                    segmentKey,
                    subStatus,
                    statusDetail,
                    activeModel,
                    tracker._timeProvider.GetUtcNow()
                );
            }

            tracker.BroadcastMetaUpdate(session);
        }

        public void UpdateSegmentSubStatus(
            string segmentKey,
            string? subStatus,
            string? statusDetail = null
        )
        {
            lock (tracker._stateLock)
            {
                if (session.IsFinalized)
                {
                    return;
                }

                session.UpdateSegmentSubStatusUnlocked(segmentKey, subStatus, statusDetail);
            }

            tracker.BroadcastMetaUpdate(session);
        }

        public void CompleteSegment(string segmentKey)
        {
            lock (tracker._stateLock)
            {
                if (session.IsFinalized)
                {
                    return;
                }

                session.SetSegmentStateUnlocked(
                    segmentKey,
                    LlmWorkflowSegmentState.Completed,
                    subStatus: null,
                    tracker._timeProvider.GetUtcNow()
                );
            }

            tracker.BroadcastMetaUpdate(session);
        }

        public void SkipSegment(string segmentKey)
        {
            lock (tracker._stateLock)
            {
                if (session.IsFinalized)
                {
                    return;
                }

                session.SetSegmentStateUnlocked(
                    segmentKey,
                    LlmWorkflowSegmentState.Skipped,
                    subStatus: null,
                    tracker._timeProvider.GetUtcNow()
                );
            }

            tracker.BroadcastMetaUpdate(session);
        }

        public void FailSegment(string segmentKey, string? error = null)
        {
            lock (tracker._stateLock)
            {
                if (session.IsFinalized)
                {
                    return;
                }

                session.SetSegmentStateUnlocked(
                    segmentKey,
                    LlmWorkflowSegmentState.Failed,
                    subStatus: error,
                    tracker._timeProvider.GetUtcNow()
                );
            }

            tracker.BroadcastMetaUpdate(session);
        }

        public void CompleteWorkload(string? statusDetail = null) =>
            tracker.FinalizeSession(session, LlmWorkloadStatus.Completed, statusDetail);

        public void FailWorkload(string? errorDetail = null) =>
            tracker.FinalizeSession(session, LlmWorkloadStatus.Failed, errorDetail);

        public void CancelWorkload(string? statusDetail = null) =>
            tracker.FinalizeSession(session, LlmWorkloadStatus.Canceled, statusDetail);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                if (ReferenceEquals(tracker._currentScope.Value, this))
                {
                    tracker._currentScope.Value = previous;
                }

                if (!session.IsFinalized)
                {
                    tracker.FinalizeSession(session, LlmWorkloadStatus.Canceled, "Stopped.");
                }
            }
        }
    }

    private sealed class WorkloadSession
    {
        private readonly List<LlmWorkflowSegmentDto> _segments;
        private readonly List<CallSession> _calls = [];

        public WorkloadSession(
            IngestionLiveTracker tracker,
            Guid workloadId,
            LlmWorkloadKind kind,
            string title,
            DateTimeOffset startedAtUtc,
            Guid? jobId,
            Guid? recipeId,
            IngestionChannel? channel,
            string? subtitle,
            string? thumbnailUrl,
            string? activeModel,
            string? statusDetail,
            IReadOnlyList<LlmWorkflowSegmentDto> segments
        )
        {
            _ = tracker;
            WorkloadId = workloadId;
            Kind = kind;
            Title = title;
            StartedAtUtc = startedAtUtc;
            JobId = jobId;
            RecipeId = recipeId;
            Channel = channel;
            Subtitle = subtitle;
            ThumbnailUrl = thumbnailUrl;
            ActiveModel = activeModel;
            StatusDetail = statusDetail;
            _segments = [.. segments];
        }

        public Guid WorkloadId { get; }
        public LlmWorkloadKind Kind { get; }
        public LlmWorkloadStatus Status { get; private set; } = LlmWorkloadStatus.Running;
        public string Title { get; set; }
        public Guid? JobId { get; }
        public Guid? RecipeId { get; }
        public IngestionChannel? Channel { get; }
        public string? Subtitle { get; set; }
        public string? ThumbnailUrl { get; set; }
        public string? ActiveModel { get; set; }
        public string? StatusDetail { get; set; }
        public DateTimeOffset StartedAtUtc { get; }
        public DateTimeOffset? CompletedAtUtc { get; private set; }
        public bool IsFinalized => Status != LlmWorkloadStatus.Running;

        public void AddCallUnlocked(CallSession call) => _calls.Add(call);

        public LlmWorkflowSegmentDto? GetActiveSegmentUnlocked() =>
            _segments.FirstOrDefault(s => s.State == LlmWorkflowSegmentState.Active);

        public void ReplaceSegmentsUnlocked(IReadOnlyList<LlmWorkflowSegmentDto> segments)
        {
            _segments.Clear();
            _segments.AddRange(segments);
        }

        public void ActivateSegmentUnlocked(
            string segmentKey,
            string? subStatus,
            string? statusDetail,
            string? activeModel,
            DateTimeOffset now
        )
        {
            for (var i = 0; i < _segments.Count; i++)
            {
                var seg = _segments[i];
                if (string.Equals(seg.Key, segmentKey, StringComparison.OrdinalIgnoreCase))
                {
                    _segments[i] = seg with
                    {
                        State = LlmWorkflowSegmentState.Active,
                        SubStatus = subStatus,
                        StartedAtUtc = seg.StartedAtUtc ?? now,
                        CompletedAtUtc = null,
                    };
                }
                else if (seg.State == LlmWorkflowSegmentState.Active)
                {
                    _segments[i] = seg with
                    {
                        State = LlmWorkflowSegmentState.Completed,
                        SubStatus = null,
                        CompletedAtUtc = now,
                    };
                }
            }

            if (statusDetail is not null)
            {
                StatusDetail = statusDetail;
            }

            if (!string.IsNullOrWhiteSpace(activeModel))
            {
                ActiveModel = activeModel.Trim();
            }
        }

        public void UpdateSegmentSubStatusUnlocked(
            string segmentKey,
            string? subStatus,
            string? statusDetail
        )
        {
            for (var i = 0; i < _segments.Count; i++)
            {
                if (string.Equals(_segments[i].Key, segmentKey, StringComparison.OrdinalIgnoreCase))
                {
                    _segments[i] = _segments[i] with { SubStatus = subStatus };
                    break;
                }
            }

            if (statusDetail is not null)
            {
                StatusDetail = statusDetail;
            }
        }

        public void SetSegmentStateUnlocked(
            string segmentKey,
            LlmWorkflowSegmentState state,
            string? subStatus,
            DateTimeOffset now
        )
        {
            for (var i = 0; i < _segments.Count; i++)
            {
                if (string.Equals(_segments[i].Key, segmentKey, StringComparison.OrdinalIgnoreCase))
                {
                    _segments[i] = _segments[i] with
                    {
                        State = state,
                        SubStatus =
                            subStatus
                            ?? (
                                state == LlmWorkflowSegmentState.Completed
                                    ? null
                                    : _segments[i].SubStatus
                            ),
                        CompletedAtUtc = state
                            is LlmWorkflowSegmentState.Completed
                                or LlmWorkflowSegmentState.Skipped
                                or LlmWorkflowSegmentState.Failed
                            ? now
                            : _segments[i].CompletedAtUtc,
                    };
                    break;
                }
            }
        }

        public void FinalizeUnlocked(
            LlmWorkloadStatus finalStatus,
            string? statusDetail,
            DateTimeOffset now
        )
        {
            Status = finalStatus;
            CompletedAtUtc = now;
            if (statusDetail is not null)
            {
                StatusDetail = statusDetail;
            }

            for (var i = 0; i < _segments.Count; i++)
            {
                var seg = _segments[i];
                if (seg.State == LlmWorkflowSegmentState.Active)
                {
                    _segments[i] = seg with
                    {
                        State = finalStatus switch
                        {
                            LlmWorkloadStatus.Completed => LlmWorkflowSegmentState.Completed,
                            LlmWorkloadStatus.Failed => LlmWorkflowSegmentState.Failed,
                            _ => LlmWorkflowSegmentState.Pending,
                        },
                        CompletedAtUtc = now,
                    };
                }
            }
        }

        public LlmActiveWorkloadDto ToDto() =>
            new(
                WorkloadId: WorkloadId,
                Kind: Kind,
                Status: Status,
                Title: Title,
                StartedAtUtc: StartedAtUtc,
                JobId: JobId,
                RecipeId: RecipeId,
                Channel: Channel,
                Subtitle: Subtitle,
                ThumbnailUrl: ThumbnailUrl,
                ActiveModel: ActiveModel,
                StatusDetail: StatusDetail,
                CompletedAtUtc: CompletedAtUtc,
                Segments: _segments.ToList(),
                Calls: _calls.Select(c => c.ToDto()).ToList()
            );

        public LlmWorkloadMetaUpdatedDto ToMetaUpdatedDtoUnlocked() =>
            new(
                WorkloadId: WorkloadId,
                Status: Status,
                Title: Title,
                Subtitle: Subtitle,
                ThumbnailUrl: ThumbnailUrl,
                ActiveModel: ActiveModel,
                StatusDetail: StatusDetail,
                CompletedAtUtc: CompletedAtUtc,
                Segments: _segments.ToList()
            );
    }

    private sealed class CallSession
    {
        private readonly IngestionLiveTracker _tracker;
        private readonly object _callLock = new();
        private readonly StringBuilder _thinkingTotal = new();
        private readonly StringBuilder _outputTotal = new();
        private readonly StringBuilder _pendingThinking = new();
        private readonly StringBuilder _pendingOutput = new();
        private readonly Stopwatch _generationStopwatch = new();

        private bool _thinkingTruncated;
        private bool _outputTruncated;
        private bool _flushScheduled;
        private bool _phaseDirty;
        private bool _completed;
        private int _chunkCount;

        public CallSession(
            IngestionLiveTracker tracker,
            Guid workloadId,
            Guid callId,
            string stageKey,
            string stageLabel,
            string model,
            bool thinkingEnabled,
            DateTimeOffset startedAtUtc
        )
        {
            _tracker = tracker;
            WorkloadId = workloadId;
            CallId = callId;
            StageKey = stageKey;
            StageLabel = stageLabel;
            Model = model;
            ThinkingEnabled = thinkingEnabled;
            StartedAtUtc = startedAtUtc;
        }

        public Guid WorkloadId { get; }
        public Guid CallId { get; }
        public string StageKey { get; }
        public string StageLabel { get; }
        public string Model { get; }
        public bool ThinkingEnabled { get; }
        public LlmCallPhase Phase { get; private set; } = LlmCallPhase.WaitingForModel;
        public DateTimeOffset StartedAtUtc { get; }
        public DateTimeOffset? ThinkingCompletedAtUtc { get; private set; }
        public DateTimeOffset? CompletedAtUtc { get; private set; }
        public int? EvalCount { get; private set; }
        public double? TokensPerSecond { get; private set; }
        public string? ErrorMessage { get; private set; }

        public void AppendThinking(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var scheduleFlush = false;
            lock (_callLock)
            {
                if (_completed)
                {
                    return;
                }

                if (!_generationStopwatch.IsRunning)
                {
                    _generationStopwatch.Start();
                }

                if (Phase != LlmCallPhase.Thinking)
                {
                    Phase = LlmCallPhase.Thinking;
                    _phaseDirty = true;
                }

                _chunkCount++;
                UpdateLiveThroughputUnlocked();

                var appended = AppendBounded(
                    _thinkingTotal,
                    text,
                    MaxThinkingCharsPerCall,
                    ref _thinkingTruncated
                );
                if (!string.IsNullOrEmpty(appended) && _tracker._notifier?.HasSubscribers == true)
                {
                    _pendingThinking.Append(appended);
                }

                scheduleFlush = ShouldScheduleFlushUnlocked();
            }

            if (scheduleFlush)
            {
                _ = FlushAfterDelayAsync();
            }
        }

        public void AppendOutput(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var scheduleFlush = false;
            lock (_callLock)
            {
                if (_completed)
                {
                    return;
                }

                if (!_generationStopwatch.IsRunning)
                {
                    _generationStopwatch.Start();
                }

                if (Phase == LlmCallPhase.Thinking && !ThinkingCompletedAtUtc.HasValue)
                {
                    ThinkingCompletedAtUtc = _tracker._timeProvider.GetUtcNow();
                }

                if (Phase != LlmCallPhase.GeneratingOutput)
                {
                    Phase = LlmCallPhase.GeneratingOutput;
                    _phaseDirty = true;
                }

                _chunkCount++;
                UpdateLiveThroughputUnlocked();

                var appended = AppendBounded(
                    _outputTotal,
                    text,
                    MaxOutputCharsPerCall,
                    ref _outputTruncated
                );
                if (!string.IsNullOrEmpty(appended) && _tracker._notifier?.HasSubscribers == true)
                {
                    _pendingOutput.Append(appended);
                }

                scheduleFlush = ShouldScheduleFlushUnlocked();
            }

            if (scheduleFlush)
            {
                _ = FlushAfterDelayAsync();
            }
        }

        public void UpdateProgress(int? evalCount, double? tokensPerSecond)
        {
            var scheduleFlush = false;
            lock (_callLock)
            {
                if (_completed)
                {
                    return;
                }

                if (evalCount.HasValue)
                {
                    EvalCount = evalCount;
                }

                if (tokensPerSecond.HasValue)
                {
                    TokensPerSecond = Math.Round(tokensPerSecond.Value, 1);
                }

                _phaseDirty = true;
                scheduleFlush = ShouldScheduleFlushUnlocked();
            }

            if (scheduleFlush)
            {
                _ = FlushAfterDelayAsync();
            }
        }

        public void Complete(int? evalCount, double? tokensPerSecond)
        {
            LlmCallStreamDeltaDto? finalDelta;
            LlmCallCompletedDto completedDto;

            lock (_callLock)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
                _generationStopwatch.Stop();
                var now = _tracker._timeProvider.GetUtcNow();
                if (Phase == LlmCallPhase.Thinking && !ThinkingCompletedAtUtc.HasValue)
                {
                    ThinkingCompletedAtUtc = now;
                }

                if (evalCount.HasValue)
                {
                    EvalCount = evalCount;
                }

                if (tokensPerSecond.HasValue)
                {
                    TokensPerSecond = Math.Round(tokensPerSecond.Value, 1);
                }

                finalDelta = DrainPendingDeltaUnlocked();
                Phase = LlmCallPhase.Completed;
                CompletedAtUtc = now;

                completedDto = new LlmCallCompletedDto(
                    WorkloadId: WorkloadId,
                    CallId: CallId,
                    Phase: Phase,
                    ThinkingCompletedAtUtc: ThinkingCompletedAtUtc,
                    CompletedAtUtc: now,
                    EvalCount: EvalCount,
                    TokensPerSecond: TokensPerSecond,
                    ErrorMessage: null
                );
            }

            DispatchDeltaIfSubscribers(finalDelta);
            _tracker.EnqueueBroadcast(() =>
                _tracker._notifier is not null
                    ? _tracker._notifier.NotifyCallCompletedAsync(completedDto)
                    : Task.CompletedTask
            );
        }

        public void Fail(string errorMessage)
        {
            LlmCallStreamDeltaDto? finalDelta;
            LlmCallCompletedDto completedDto;

            lock (_callLock)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
                _generationStopwatch.Stop();
                var now = _tracker._timeProvider.GetUtcNow();
                if (Phase == LlmCallPhase.Thinking && !ThinkingCompletedAtUtc.HasValue)
                {
                    ThinkingCompletedAtUtc = now;
                }

                finalDelta = DrainPendingDeltaUnlocked();
                Phase = LlmCallPhase.Failed;
                CompletedAtUtc = now;
                ErrorMessage = string.IsNullOrWhiteSpace(errorMessage)
                    ? "LLM call failed."
                    : errorMessage.Trim();

                completedDto = new LlmCallCompletedDto(
                    WorkloadId: WorkloadId,
                    CallId: CallId,
                    Phase: Phase,
                    ThinkingCompletedAtUtc: ThinkingCompletedAtUtc,
                    CompletedAtUtc: now,
                    EvalCount: EvalCount,
                    TokensPerSecond: TokensPerSecond,
                    ErrorMessage: ErrorMessage
                );
            }

            DispatchDeltaIfSubscribers(finalDelta);
            _tracker.EnqueueBroadcast(() =>
                _tracker._notifier is not null
                    ? _tracker._notifier.NotifyCallCompletedAsync(completedDto)
                    : Task.CompletedTask
            );
        }

        public bool IsCompleted
        {
            get
            {
                lock (_callLock)
                {
                    return _completed;
                }
            }
        }

        public LlmCallTraceDto ToDto()
        {
            lock (_callLock)
            {
                return new LlmCallTraceDto(
                    CallId: CallId,
                    StageKey: StageKey,
                    StageLabel: StageLabel,
                    Model: Model,
                    ThinkingEnabled: ThinkingEnabled,
                    Phase: Phase,
                    ThinkingText: _thinkingTotal.ToString(),
                    OutputText: _outputTotal.ToString(),
                    StartedAtUtc: StartedAtUtc,
                    ThinkingCompletedAtUtc: ThinkingCompletedAtUtc,
                    CompletedAtUtc: CompletedAtUtc,
                    EvalCount: EvalCount,
                    TokensPerSecond: TokensPerSecond,
                    ErrorMessage: ErrorMessage
                );
            }
        }

        private void UpdateLiveThroughputUnlocked()
        {
            EvalCount = _chunkCount;
            var elapsedSec = _generationStopwatch.Elapsed.TotalSeconds;
            if (elapsedSec >= 0.25 && _chunkCount >= 4)
            {
                TokensPerSecond = Math.Round(_chunkCount / elapsedSec, 1);
            }
        }

        private bool ShouldScheduleFlushUnlocked()
        {
            if (_flushScheduled || _tracker._notifier?.HasSubscribers != true)
            {
                return false;
            }

            _flushScheduled = true;
            return true;
        }

        private async Task FlushAfterDelayAsync()
        {
            try
            {
                if (_tracker._deltaFlushInterval > TimeSpan.Zero)
                {
                    await Task.Delay(_tracker._deltaFlushInterval);
                }

                LlmCallStreamDeltaDto? delta;
                lock (_callLock)
                {
                    _flushScheduled = false;
                    if (_completed)
                    {
                        return;
                    }

                    delta = DrainPendingDeltaUnlocked();
                }

                DispatchDeltaIfSubscribers(delta);
            }
            catch (Exception ex)
            {
                _tracker._logger?.LogDebug(ex, "Failed to flush call stream delta.");
            }
        }

        private LlmCallStreamDeltaDto? DrainPendingDeltaUnlocked()
        {
            if (_pendingThinking.Length == 0 && _pendingOutput.Length == 0 && !_phaseDirty)
            {
                return null;
            }

            var thinkingDelta = _pendingThinking.Length > 0 ? _pendingThinking.ToString() : null;
            var outputDelta = _pendingOutput.Length > 0 ? _pendingOutput.ToString() : null;
            _pendingThinking.Clear();
            _pendingOutput.Clear();
            _phaseDirty = false;

            return new LlmCallStreamDeltaDto(
                WorkloadId: WorkloadId,
                CallId: CallId,
                Phase: Phase,
                ThinkingDelta: thinkingDelta,
                OutputDelta: outputDelta,
                ThinkingCompletedAtUtc: ThinkingCompletedAtUtc,
                EvalCount: EvalCount,
                TokensPerSecond: TokensPerSecond
            );
        }

        private void DispatchDeltaIfSubscribers(LlmCallStreamDeltaDto? delta)
        {
            if (delta is null || _tracker._notifier?.HasSubscribers != true)
            {
                return;
            }

            _tracker.EnqueueBroadcast(() =>
                _tracker._notifier is not null
                    ? _tracker._notifier.NotifyCallDeltaAsync(delta)
                    : Task.CompletedTask
            );
        }

        private static string AppendBounded(
            StringBuilder target,
            string incoming,
            int maxChars,
            ref bool truncated
        )
        {
            if (truncated)
            {
                return string.Empty;
            }

            var remaining = maxChars - target.Length;
            if (remaining <= 0)
            {
                truncated = true;
                target.Append(TruncationNotice);
                return TruncationNotice;
            }

            if (incoming.Length <= remaining)
            {
                target.Append(incoming);
                return incoming;
            }

            var slice = incoming[..remaining] + TruncationNotice;
            target.Append(slice);
            truncated = true;
            return slice;
        }
    }

    private sealed class CallScope(CallSession call, WorkloadScope? ownedWorkloadScope)
        : ILlmCallScope
    {
        private int _disposed;

        public Guid WorkloadId => call.WorkloadId;
        public Guid CallId => call.CallId;

        public void AppendThinking(string text) => call.AppendThinking(text);

        public void AppendOutput(string text) => call.AppendOutput(text);

        public void UpdateProgress(int? evalCount = null, double? tokensPerSecond = null) =>
            call.UpdateProgress(evalCount, tokensPerSecond);

        public void Complete(int? evalCount = null, double? tokensPerSecond = null)
        {
            call.Complete(evalCount, tokensPerSecond);
            ownedWorkloadScope?.CompleteWorkload();
        }

        public void Fail(string errorMessage)
        {
            call.Fail(errorMessage);
            ownedWorkloadScope?.FailWorkload(errorMessage);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                if (!call.IsCompleted)
                {
                    call.Fail("Canceled.");
                }

                ownedWorkloadScope?.Dispose();
            }
        }
    }
}

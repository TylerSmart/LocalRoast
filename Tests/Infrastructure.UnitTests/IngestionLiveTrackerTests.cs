using App.Core.Ingestion;
using App.Infrastructure.Ingestion;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Infrastructure.UnitTests;

[TestClass]
public sealed class IngestionLiveTrackerTests
{
    [TestMethod]
    public async Task ParallelWorkloads_IsolateLlmCallsViaAsyncLocal()
    {
        var notifier = new RecordingRealtimeNotifier { HasSubscribers = true };
        using var tracker = new IngestionLiveTracker(
            notifier,
            deltaFlushInterval: TimeSpan.FromMilliseconds(10)
        );

        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;

        async Task RunJobAsync(string title, string stageThought)
        {
            using var workload = tracker.BeginWorkload(
                LlmWorkloadKind.RecipeIngestion,
                title,
                [new LlmWorkflowSegmentDto("stage1", "1. Extract", LlmWorkflowSegmentState.Active)]
            );

            using var call = tracker.BeginLlmCall(
                "stage1",
                "Stage 1 · Extract",
                "gemma4:e4b",
                thinkingEnabled: true
            );

            call.AppendThinking(stageThought);
            if (Interlocked.Increment(ref startedCount) == 2)
            {
                barrier.TrySetResult();
            }

            await barrier.Task;
            call.AppendOutput("{\"ok\":true}");
            call.Complete(evalCount: 15, tokensPerSecond: 60.0);
            workload.CompleteWorkload("Done");
        }

        await Task.WhenAll(
            RunJobAsync("Recipe Alpha", "Alpha thinking"),
            RunJobAsync("Recipe Beta", "Beta thinking")
        );

        await tracker.DrainBroadcastsAsync();

        var snapshot = tracker.GetSnapshot();
        Assert.IsEmpty(snapshot.ActiveWorkloads);
        Assert.HasCount(2, snapshot.RecentWorkloads);

        var alpha = snapshot.RecentWorkloads.Single(w => w.Title == "Recipe Alpha");
        var beta = snapshot.RecentWorkloads.Single(w => w.Title == "Recipe Beta");
        Assert.HasCount(1, alpha.Calls);
        Assert.HasCount(1, beta.Calls);
        Assert.AreEqual("Alpha thinking", alpha.Calls[0].ThinkingText);
        Assert.AreEqual("Beta thinking", beta.Calls[0].ThinkingText);
    }

    [TestMethod]
    public async Task SegmentTransitionsAndRecentRingBuffer_CapAtMaxRecent()
    {
        using var tracker = new IngestionLiveTracker();

        for (var i = 1; i <= 5; i++)
        {
            using var workload = tracker.BeginWorkload(
                LlmWorkloadKind.RecipeIngestion,
                $"Recipe {i}",
                [
                    new LlmWorkflowSegmentDto(
                        "stage1",
                        "1. Extract",
                        LlmWorkflowSegmentState.Pending
                    ),
                    new LlmWorkflowSegmentDto(
                        "stage2",
                        "2. Condense",
                        LlmWorkflowSegmentState.Pending
                    ),
                ]
            );
            workload.ActivateSegment("stage1", "Attempt 1/3");
            workload.CompleteSegment("stage1");
            workload.SkipSegment("stage2");
            workload.CompleteWorkload("Completed");
        }

        var snapshot = tracker.GetSnapshot();
        Assert.IsEmpty(snapshot.ActiveWorkloads);
        Assert.HasCount(IngestionLiveTracker.MaxRecentWorkloads, snapshot.RecentWorkloads);
        Assert.AreEqual("Recipe 5", snapshot.RecentWorkloads[0].Title);
        Assert.AreEqual("Recipe 3", snapshot.RecentWorkloads[2].Title);
        Assert.AreEqual(
            LlmWorkflowSegmentState.Completed,
            snapshot.RecentWorkloads[0].Segments[0].State
        );
        Assert.AreEqual(
            LlmWorkflowSegmentState.Skipped,
            snapshot.RecentWorkloads[0].Segments[1].State
        );
    }

    [TestMethod]
    public async Task CallStreaming_CoalescesDeltasAndPreservesStrictBroadcastOrder()
    {
        var notifier = new RecordingRealtimeNotifier { HasSubscribers = true };
        using var tracker = new IngestionLiveTracker(
            notifier,
            deltaFlushInterval: TimeSpan.FromMilliseconds(25)
        );

        using (
            var workload = tracker.BeginWorkload(
                LlmWorkloadKind.JobRefinement,
                "Blueberry Pie",
                [new LlmWorkflowSegmentDto("stage6", "Refine", LlmWorkflowSegmentState.Active)]
            )
        )
        {
            using var call = tracker.BeginLlmCall(
                "stage6",
                "Stage 6 · Refine",
                "gemma4:26b-a4b",
                thinkingEnabled: true
            );

            call.AppendThinking("Checking ");
            call.AppendThinking("filling ");
            call.AppendThinking("ratios.");
            await Task.Delay(50);
            call.AppendOutput("{\"title\":\"Blueberry Pie\"}");
            call.Complete(evalCount: 20, tokensPerSecond: 42.5);
            workload.CompleteWorkload("Refined");
        }

        await tracker.DrainBroadcastsAsync();

        Assert.IsNotEmpty(notifier.EventOrder);
        Assert.AreEqual("Upserted", notifier.EventOrder[0]);
        var callStartedIdx = notifier.EventOrder.IndexOf("CallStarted");
        var firstDeltaIdx = notifier.EventOrder.IndexOf("CallDelta");
        var callCompletedIdx = notifier.EventOrder.IndexOf("CallCompleted");
        Assert.IsGreaterThanOrEqualTo(0, callStartedIdx);
        Assert.IsGreaterThan(callStartedIdx, firstDeltaIdx);
        Assert.IsGreaterThan(firstDeltaIdx, callCompletedIdx);

        var combinedThinking = string.Concat(notifier.Deltas.Select(d => d.ThinkingDelta ?? ""));
        var combinedOutput = string.Concat(notifier.Deltas.Select(d => d.OutputDelta ?? ""));
        Assert.AreEqual("Checking filling ratios.", combinedThinking);
        Assert.AreEqual("{\"title\":\"Blueberry Pie\"}", combinedOutput);
    }

    [TestMethod]
    public void BeginLlmCall_WithoutActiveWorkload_AutoCreatesFallbackWorkload()
    {
        using var tracker = new IngestionLiveTracker();

        using (
            var call = tracker.BeginLlmCall(
                "kitchen_helper",
                "Kitchen AI · Cooking Question",
                "gemma4:e4b",
                thinkingEnabled: false,
                fallbackWorkloadTitle: "Kitchen AI Question",
                fallbackKind: LlmWorkloadKind.KitchenHelper
            )
        )
        {
            call.AppendOutput("{\"answer\":\"Use kosher salt.\"}");
            var activeSnapshot = tracker.GetSnapshot();
            Assert.HasCount(1, activeSnapshot.ActiveWorkloads);
            Assert.AreEqual(LlmWorkloadKind.KitchenHelper, activeSnapshot.ActiveWorkloads[0].Kind);
            call.Complete();
        }

        var doneSnapshot = tracker.GetSnapshot();
        Assert.IsEmpty(doneSnapshot.ActiveWorkloads);
        Assert.HasCount(1, doneSnapshot.RecentWorkloads);
        Assert.AreEqual(LlmWorkloadStatus.Completed, doneSnapshot.RecentWorkloads[0].Status);
    }

    private sealed class RecordingRealtimeNotifier : IIngestionRealtimeNotifier
    {
        private readonly object _lock = new();
        public bool HasSubscribers { get; set; }
        public List<string> EventOrder { get; } = [];
        public List<LlmCallStreamDeltaDto> Deltas { get; } = [];

        public Task NotifyWorkloadUpsertedAsync(
            LlmActiveWorkloadDto workload,
            CancellationToken cancellationToken = default
        )
        {
            lock (_lock)
            {
                EventOrder.Add("Upserted");
            }
            return Task.CompletedTask;
        }

        public Task NotifyWorkloadMetaUpdatedAsync(
            LlmWorkloadMetaUpdatedDto update,
            CancellationToken cancellationToken = default
        )
        {
            lock (_lock)
            {
                EventOrder.Add("MetaUpdated");
            }
            return Task.CompletedTask;
        }

        public Task NotifyCallStartedAsync(
            LlmCallStartedDto started,
            CancellationToken cancellationToken = default
        )
        {
            lock (_lock)
            {
                EventOrder.Add("CallStarted");
            }
            return Task.CompletedTask;
        }

        public Task NotifyCallDeltaAsync(
            LlmCallStreamDeltaDto delta,
            CancellationToken cancellationToken = default
        )
        {
            lock (_lock)
            {
                EventOrder.Add("CallDelta");
                Deltas.Add(delta);
            }
            return Task.CompletedTask;
        }

        public Task NotifyCallCompletedAsync(
            LlmCallCompletedDto completed,
            CancellationToken cancellationToken = default
        )
        {
            lock (_lock)
            {
                EventOrder.Add("CallCompleted");
            }
            return Task.CompletedTask;
        }

        public Task NotifyQueueChangedAsync(
            IngestionQueueChangedEventDto queueEvent,
            CancellationToken cancellationToken = default
        )
        {
            lock (_lock)
            {
                EventOrder.Add("QueueChanged");
            }
            return Task.CompletedTask;
        }
    }
}

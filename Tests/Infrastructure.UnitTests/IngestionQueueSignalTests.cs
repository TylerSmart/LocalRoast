using App.Infrastructure.Ingestion;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Infrastructure.UnitTests;

[TestClass]
public sealed class IngestionQueueSignalTests
{
    [TestMethod]
    public async Task NotifyWorkAvailable_PreemptsBackgroundWorkButNotImports()
    {
        var signal = new IngestionQueueSignal();
        using var importCts = new CancellationTokenSource();
        using (signal.RegisterActiveJobExecution(Guid.NewGuid(), importCts))
        {
            signal.NotifyWorkAvailable();
            Assert.IsFalse(importCts.IsCancellationRequested);
        }

        using var refineCts = new CancellationTokenSource();
        var refineJobId = Guid.NewGuid();
        using (signal.RegisterPreemptibleExecution(refineJobId, refineCts))
        {
            Assert.AreEqual(refineJobId, signal.ActiveJobId);
            signal.NotifyBackgroundWorkAvailable();
            Assert.IsFalse(refineCts.IsCancellationRequested);
            signal.NotifyWorkAvailable();
            Assert.IsTrue(refineCts.IsCancellationRequested);
        }
        Assert.IsNull(signal.ActiveJobId);
        Assert.IsTrue(await signal.WaitForWorkAsync(TimeSpan.FromSeconds(1)));

        using var laterImportCts = new CancellationTokenSource();
        using (signal.RegisterActiveJobExecution(Guid.NewGuid(), laterImportCts))
        {
            signal.NotifyWorkAvailable();
            Assert.IsFalse(laterImportCts.IsCancellationRequested);
        }
    }

    [TestMethod]
    public async Task PreemptBackgroundWork_CancelsOnlyBackgroundWorkWithoutWakingTheWorker()
    {
        var signal = new IngestionQueueSignal();
        using var importCts = new CancellationTokenSource();
        using (signal.RegisterActiveJobExecution(Guid.NewGuid(), importCts))
        {
            signal.PreemptBackgroundWork();
            Assert.IsFalse(importCts.IsCancellationRequested);
        }

        using var refineCts = new CancellationTokenSource();
        using (signal.RegisterPreemptibleExecution(Guid.NewGuid(), refineCts))
        {
            signal.PreemptBackgroundWork();
            Assert.IsTrue(refineCts.IsCancellationRequested);
        }

        Assert.IsFalse(await signal.WaitForWorkAsync(TimeSpan.FromMilliseconds(50)));
    }

    [TestMethod]
    public async Task AcquireJobExecutionAsync_SupportsConfiguredConcurrency()
    {
        var signal = new IngestionQueueSignal();

        var lease1 = await signal.AcquireJobExecutionAsync(maxConcurrency: 2);
        var lease2 = await signal.AcquireJobExecutionAsync(maxConcurrency: 2);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await signal.AcquireJobExecutionAsync(maxConcurrency: 2, cts.Token);
        });

        lease1.Dispose();

        var lease3 = await signal.AcquireJobExecutionAsync(maxConcurrency: 2);
        Assert.IsNotNull(lease3);

        lease2.Dispose();
        lease3.Dispose();
    }

    [TestMethod]
    public void ActiveJobIds_TracksMultipleConcurrentJobs()
    {
        var signal = new IngestionQueueSignal();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        using var cts1 = new CancellationTokenSource();
        using var cts2 = new CancellationTokenSource();

        using (signal.RegisterActiveJobExecution(id1, cts1))
        using (signal.RegisterActiveJobExecution(id2, cts2))
        {
            Assert.IsTrue(signal.IsJobActive(id1));
            Assert.IsTrue(signal.IsJobActive(id2));
            Assert.AreEqual(2, signal.ActiveJobIds.Count);
        }

        Assert.IsFalse(signal.IsJobActive(id1));
        Assert.IsFalse(signal.IsJobActive(id2));
        Assert.AreEqual(0, signal.ActiveJobIds.Count);
    }
}

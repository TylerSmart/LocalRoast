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
}

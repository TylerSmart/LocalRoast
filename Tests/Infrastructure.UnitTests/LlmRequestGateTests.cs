using System.Net;
using App.Core.Exceptions;
using App.Core.Ingestion;
using App.Infrastructure.Ingestion;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Infrastructure.UnitTests;

[TestClass]
public sealed class LlmRequestGateTests
{
    private static EffectiveLlmConnectionConfig CreateConfig(
        int maxConcurrent = 1,
        int? rpm = null,
        int? rpd = null
    ) =>
        new(
            Provider: LlmProvider.Ollama,
            BaseUrl: "http://localhost:11434",
            ApiKey: null,
            TextModel: "test-model",
            VisionModel: null,
            OllamaKeepAlive: "5m",
            RequestTimeoutSeconds: 60,
            AutoPublishHighConfidenceText: false,
            MaxConcurrentRequests: maxConcurrent,
            RequestsPerMinute: rpm,
            DailyRequestQuota: rpd
        );

    [TestMethod]
    public async Task AcquireAsync_AllowsConfiguredConcurrentRequests()
    {
        var gate = new LlmRequestGate();
        var config = CreateConfig(maxConcurrent: 2);

        using var lease1 = await gate.AcquireAsync(config);
        using var lease2 = await gate.AcquireAsync(config);

        // Third should wait
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            using var lease3 = await gate.AcquireAsync(config, cts.Token);
        });
    }

    [TestMethod]
    public async Task AcquireAsync_ReleasesSlotForNextCaller()
    {
        var gate = new LlmRequestGate();
        var config = CreateConfig(maxConcurrent: 1);

        var lease1 = await gate.AcquireAsync(config);
        var lease2Task = gate.AcquireAsync(config);

        Assert.IsFalse(lease2Task.IsCompleted);

        lease1.Dispose();

        var lease2 = await lease2Task;
        Assert.IsNotNull(lease2);
        lease2.Dispose();
    }

    [TestMethod]
    public async Task AcquireAsync_DailyQuotaExhaustion_ThrowsLlmHostUnavailableException()
    {
        var gate = new LlmRequestGate();
        var config = CreateConfig(maxConcurrent: 5, rpd: 2);

        using (await gate.AcquireAsync(config)) { }
        using (await gate.AcquireAsync(config)) { }

        var ex = await Assert.ThrowsAsync<LlmHostUnavailableException>(async () =>
        {
            using var lease = await gate.AcquireAsync(config);
        });

        Assert.IsTrue(ex.IsQuotaExhausted);
        Assert.IsTrue(ex.Message.Contains("Daily request limit reached"));
    }

    [TestMethod]
    public async Task AcquireAsync_SeparateConnections_DoNotShareSlotsOrQuota()
    {
        var gate = new LlmRequestGate();
        var machineA = CreateConfig(maxConcurrent: 1, rpd: 1) with { ConnectionKey = "machine-a" };
        var machineB = CreateConfig(maxConcurrent: 1, rpd: 1) with { ConnectionKey = "machine-b" };

        using var leaseA = await gate.AcquireAsync(machineA);
        var leaseBTask = gate.AcquireAsync(machineB);

        Assert.IsTrue(leaseBTask.IsCompleted, "A busy connection must not block another one.");
        using var leaseB = await leaseBTask;
        await Assert.ThrowsAsync<LlmHostUnavailableException>(async () =>
        {
            using var lease = await gate.AcquireAsync(machineA);
        });
    }

    [TestMethod]
    public async Task AcquireAsync_PerModelDailyQuota_IsIndependentAndDoesNotBurnConnectionQuota()
    {
        var gate = new LlmRequestGate();
        var flash = CreateConfig(maxConcurrent: 5, rpd: 5) with
        {
            ConnectionKey = "google-ai",
            TextModel = "gemini-2.5-flash",
            ModelKey = "google-ai:gemini-2.5-flash",
            ModelDailyRequestQuota = 1,
            ModelDailyQuotaResetTimezone = "America/Los_Angeles",
            ModelDailyQuotaResetTime = "00:00",
        };
        var flashLite = CreateConfig(maxConcurrent: 5, rpd: 5) with
        {
            ConnectionKey = "google-ai",
            TextModel = "gemini-2.5-flash-lite",
            ModelKey = "google-ai:gemini-2.5-flash-lite",
            ModelDailyRequestQuota = 4,
        };

        // 1 request on flash hits flash's model quota (1/1) and uses 1/5 of connection quota.
        using (await gate.AcquireAsync(flash)) { }

        var ex = await Assert.ThrowsAsync<LlmHostUnavailableException>(async () =>
        {
            using var lease = await gate.AcquireAsync(flash);
        });
        Assert.IsTrue(ex.IsQuotaExhausted);
        Assert.AreEqual(LlmQuotaScope.Model, ex.ExhaustedQuotaScope);
        Assert.AreEqual("google-ai:gemini-2.5-flash", ex.ExhaustedGateKey);
        Assert.AreEqual("America/Los_Angeles", ex.QuotaResetTimezone);
        Assert.AreEqual("00:00", ex.QuotaResetTime);

        // Repeated blocked attempts on flash MUST NOT consume connection quota, so flashLite
        // still gets all 4 remaining connection requests (total 1 + 4 = 5).
        for (var i = 0; i < 4; i++)
        {
            using (await gate.AcquireAsync(flashLite)) { }
        }

        // The 6th total request on the connection now trips the connection RPD (5/5).
        var connEx = await Assert.ThrowsAsync<LlmHostUnavailableException>(async () =>
        {
            using var lease = await gate.AcquireAsync(flashLite);
        });
        Assert.IsTrue(connEx.IsQuotaExhausted);
        Assert.AreEqual(LlmQuotaScope.Connection, connEx.ExhaustedQuotaScope);
        Assert.AreEqual("google-ai", connEx.ExhaustedGateKey);
    }

    [TestMethod]
    public async Task AcquireAsync_HierarchicalConcurrency_EnforcesBothConnectionAndModelCaps()
    {
        var gate = new LlmRequestGate();
        var modelA = CreateConfig(maxConcurrent: 2) with
        {
            ConnectionKey = "shared-host",
            ModelKey = "shared-host:model-a",
            ModelMaxConcurrentRequests = 1,
        };
        var modelB = CreateConfig(maxConcurrent: 2) with
        {
            ConnectionKey = "shared-host",
            ModelKey = "shared-host:model-b",
            ModelMaxConcurrentRequests = 2,
        };

        // Hold 1 slot on modelA -> modelA is at its model limit (1), so a 2nd modelA waits.
        using var leaseA1 = await gate.AcquireAsync(modelA);
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                using var leaseA2 = await gate.AcquireAsync(modelA, cts.Token);
            });
        }

        // Connection limit is 2, so modelB can acquire the 2nd connection slot concurrently.
        using var leaseB1 = await gate.AcquireAsync(modelB);

        // Now connection is at 2/2, so a 2nd modelB must wait even though modelB allows 2.
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                using var leaseB2 = await gate.AcquireAsync(modelB, cts.Token);
            });
        }
    }
}

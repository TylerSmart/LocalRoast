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
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using App.Core.Authentication;
using App.Core.Ingestion;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.API.IntegrationTests;

[TestClass]
public sealed class IngestionHubIntegrationTests
{
    private AppWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    [TestInitialize]
    public void Setup()
    {
        _factory = new AppWebApplicationFactory();
        _client = _factory.CreateClient();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<AuthResponse> RegisterAndAuthorizeAsync(
        string email,
        string username,
        string password
    )
    {
        var response = await _client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(email, username, password)
        );
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.IsNotNull(auth);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            auth.Token
        );
        return auth;
    }

    private HubConnection CreateHubConnection(string? token) =>
        new HubConnectionBuilder()
            .WithUrl(
                new Uri(_client.BaseAddress!, "/api/hubs/ingestion"),
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                    }
                }
            )
            .Build();

    [TestMethod]
    public async Task IngestionHub_WhenUnauthenticated_FailsToConnect()
    {
        await using var hubConnection = CreateHubConnection(null);
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await hubConnection.StartAsync()
        );
    }

    [TestMethod]
    public async Task IngestionHub_WhenUsingPat_FailsToConnect()
    {
        await RegisterAndAuthorizeAsync("ingpat@example.com", "ingpat", "Password123!");
        var patRes = await _client.PostAsJsonAsync(
            "/api/account/tokens",
            new CreatePersonalAccessTokenRequest("Ingestion PAT")
        );
        var createdPat = await patRes.Content.ReadFromJsonAsync<CreatedPersonalAccessTokenDto>();
        Assert.IsNotNull(createdPat);

        await using var hubConnection = CreateHubConnection(createdPat.PlaintextToken);
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await hubConnection.StartAsync()
        );
    }

    [TestMethod]
    public async Task IngestionHub_WhenAuthenticated_ReceivesLateJoinSnapshotAndLiveStreams()
    {
        var auth = await RegisterAndAuthorizeAsync(
            "inglive@example.com",
            "inglive",
            "Password123!"
        );

        var tracker = _factory.Services.GetRequiredService<IIngestionLiveTracker>();
        using var activeWorkload = tracker.BeginWorkload(
            LlmWorkloadKind.RecipeIngestion,
            "Sourdough Focaccia",
            [
                new LlmWorkflowSegmentDto(
                    "preprocess",
                    "Preprocess",
                    LlmWorkflowSegmentState.Completed
                ),
                new LlmWorkflowSegmentDto("stage1", "1. Extract", LlmWorkflowSegmentState.Active),
            ],
            activeModel: "gemma4:e4b"
        );
        using var activeCall = tracker.BeginLlmCall(
            "stage1",
            "Stage 1 · Extract Recipe Structure",
            "gemma4:e4b",
            thinkingEnabled: true
        );
        activeCall.AppendThinking("Pre-connect reasoning history.");

        var snapshotTcs = new TaskCompletionSource<IngestionLiveSnapshotDto>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var deltaTcs = new TaskCompletionSource<LlmCallStreamDeltaDto>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var queueChangedTcs = new TaskCompletionSource<IngestionQueueChangedEventDto>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        await using var connection = CreateHubConnection(auth.Token);
        connection.On<IngestionLiveSnapshotDto>("LiveSnapshot", s => snapshotTcs.TrySetResult(s));
        connection.On<LlmCallStreamDeltaDto>(
            "CallStreamDelta",
            d =>
            {
                if (!string.IsNullOrEmpty(d.ThinkingDelta))
                {
                    deltaTcs.TrySetResult(d);
                }
            }
        );
        connection.On<IngestionQueueChangedEventDto>(
            "QueueChanged",
            q =>
            {
                if (q.Reason == "enqueued")
                {
                    queueChangedTcs.TrySetResult(q);
                }
            }
        );

        await connection.StartAsync();

        // 1. Verify immediate late-join snapshot delivers pre-connect thinking history
        var snapshot = await snapshotTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.HasCount(1, snapshot.ActiveWorkloads);
        Assert.AreEqual("Sourdough Focaccia", snapshot.ActiveWorkloads[0].Title);
        Assert.HasCount(1, snapshot.ActiveWorkloads[0].Calls);
        Assert.AreEqual(
            "Pre-connect reasoning history.",
            snapshot.ActiveWorkloads[0].Calls[0].ThinkingText
        );

        // 2. Verify live streaming delta arrives after connect
        activeCall.AppendThinking(" Live delta chunk.");
        var delta = await deltaTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(activeWorkload.WorkloadId, delta.WorkloadId);
        Assert.Contains("Live delta chunk.", delta.ThinkingDelta ?? "");

        activeCall.Complete();
        activeWorkload.CompleteWorkload("Done");

        // 3. Verify REST enqueue broadcasts QueueChanged with updated summary
        var enqueueRes = await _client.PostAsJsonAsync(
            "/api/ingestion/jobs/text",
            new EnqueueRawTextIngestionRequest("Garlic Pasta\nIngredients:\n- 1 lb pasta")
        );
        Assert.AreEqual(HttpStatusCode.Accepted, enqueueRes.StatusCode);

        var queueEvent = await queueChangedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsNotNull(queueEvent.Summary);
        Assert.IsGreaterThanOrEqualTo(1, queueEvent.Summary.ActiveProcessingCount);
    }
}

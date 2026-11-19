using System.Text.Json.Nodes;
using App.Core.Ingestion;
using App.Data;
using App.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class LlmConnectionCascadeDeleteTests
{
    private AppDbContext _dbContext = null!;
    private FakeQueueSignal _queueSignal = null!;
    private LlmConnectionService _connectionService = null!;
    private LlmSettingsService _settingsService = null!;

    private sealed class FakeQueueSignal : IIngestionQueueSignal
    {
        public int PreemptCount { get; private set; }
        public Guid? ActiveJobId => null;
        public IReadOnlyCollection<Guid> ActiveJobIds => [];

        public void NotifyWorkAvailable() { }

        public void NotifyBackgroundWorkAvailable() { }

        public void PreemptBackgroundWork() => PreemptCount++;

        public ValueTask<bool> WaitForWorkAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(false);

        public IDisposable RegisterActiveJobExecution(Guid jobId, CancellationTokenSource cts) =>
            throw new NotImplementedException();

        public IDisposable RegisterPreemptibleExecution(Guid jobId, CancellationTokenSource cts) =>
            throw new NotImplementedException();

        public Task<IDisposable> AcquireJobExecutionAsync(
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<IDisposable> AcquireJobExecutionAsync(
            int maxConcurrency,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public void CancelActiveExecution(Guid? jobId = null) { }

        public bool IsJobActive(Guid jobId) => false;
    }

    private sealed class NoopLlmClient : ILlmExtractionClient
    {
        public Task<LlmConnectionTestResultDto> ProbeAndListModelsAsync(
            EffectiveLlmConnectionConfig config,
            int timeoutSeconds = 15,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<LlmStructuredStageResult<T>> ExecuteStructuredStageAsync<T>(
            EffectiveLlmConnectionConfig config,
            string stageName,
            IReadOnlyList<LlmStageChatMessage> messages,
            JsonNode? jsonSchema,
            bool? overrideThinking = null,
            int maxTokens = 16384,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<float[][]> GenerateEmbeddingsAsync(
            EffectiveLlmConnectionConfig config,
            IReadOnlyList<string> inputs,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<string?> TranscribeRecipeImagesAsync(
            EffectiveLlmConnectionConfig config,
            LlmVisionExtractionContext context,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<LlmSubstitutionPayload> SuggestIngredientSubstitutionsAsync(
            EffectiveLlmConnectionConfig config,
            LlmSubstitutionContext context,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<string> AnswerKitchenQuestionAsync(
            EffectiveLlmConnectionConfig config,
            LlmKitchenQuestionContext context,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<LlmDraftMealPlanPayload> DraftWeeklyMealPlanAsync(
            EffectiveLlmConnectionConfig config,
            LlmDraftMealPlanContext context,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();
    }

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"LlmCascadeDelete_{Guid.NewGuid():N}")
            .Options;
        _dbContext = new AppDbContext(options);
        _queueSignal = new FakeQueueSignal();
        var client = new NoopLlmClient();
        _connectionService = new LlmConnectionService(_dbContext, client, _queueSignal);
        _settingsService = new LlmSettingsService(
            _dbContext,
            Options.Create(new LlmDefaultsOptions()),
            client,
            _queueSignal
        );
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dbContext.Dispose();
    }

    [TestMethod]
    public async Task DeleteConnectionAsync_PreemptsBackgroundWork_WhenIdleRefinementOrEmbeddingUnassigned()
    {
        var conn = await _connectionService.CreateConnectionAsync(
            new UpsertLlmConnectionRequest(
                "Google AI Studio",
                LlmProvider.OpenAiCompatible,
                "https://generativelanguage.googleapis.com/v1beta/openai"
            )
        );
        var model = await _connectionService.CreateModelConfigurationAsync(
            new UpsertLlmModelConfigurationRequest("gemini-flash", conn.Id, "gemini-flash")
        );

        await _settingsService.UpdateWorkflowsAsync(
            new UpdateLlmWorkflowsRequest(
                RefineModelProfileId: model.DefaultProfileId,
                RefineDuringIdle: true
            )
        );
        Assert.AreEqual(0, _queueSignal.PreemptCount);

        await _connectionService.DeleteConnectionAsync(conn.Id);

        Assert.AreEqual(1, _queueSignal.PreemptCount);
        Assert.AreEqual(0, await _dbContext.LlmConnections.CountAsync());
        Assert.AreEqual(0, await _dbContext.LlmModelConfigurations.CountAsync());
        Assert.AreEqual(0, await _dbContext.LlmModelProfiles.CountAsync());

        var settings = await _dbContext.HouseholdLlmSettings.SingleAsync();
        Assert.IsNull(settings.RefineModelProfileId);
        Assert.IsNull(settings.RefineModel);
    }

    [TestMethod]
    public async Task DeleteConnectionAsync_DoesNotPreemptBackgroundWork_WhenOnlyTextWorkflowUnassigned()
    {
        var conn = await _connectionService.CreateConnectionAsync(
            new UpsertLlmConnectionRequest(
                "Local Ollama",
                LlmProvider.Ollama,
                "http://localhost:11434"
            )
        );
        var model = await _connectionService.CreateModelConfigurationAsync(
            new UpsertLlmModelConfigurationRequest("gemma4:e4b", conn.Id, "gemma4:e4b")
        );

        await _settingsService.UpdateWorkflowsAsync(
            new UpdateLlmWorkflowsRequest(TextModelProfileId: model.DefaultProfileId)
        );

        await _connectionService.DeleteConnectionAsync(conn.Id);

        Assert.AreEqual(0, _queueSignal.PreemptCount);
        var settings = await _dbContext.HouseholdLlmSettings.SingleAsync();
        Assert.IsNull(settings.TextModelProfileId);
        Assert.IsNull(settings.TextModel);
    }

    [TestMethod]
    public async Task DeleteModelProfileAsync_PreemptsBackgroundWork_WhenEmbeddingProfileDeleted()
    {
        var conn = await _connectionService.CreateConnectionAsync(
            new UpsertLlmConnectionRequest(
                "Local Ollama",
                LlmProvider.Ollama,
                "http://localhost:11434"
            )
        );
        var model = await _connectionService.CreateModelConfigurationAsync(
            new UpsertLlmModelConfigurationRequest("embeddinggemma", conn.Id, "embeddinggemma")
        );
        var customProfile = await _settingsService.CreateModelProfileAsync(
            new UpsertLlmModelProfileRequest(model.Id, "Custom Embed")
        );

        await _settingsService.UpdateWorkflowsAsync(
            new UpdateLlmWorkflowsRequest(EmbeddingModelProfileId: customProfile.Id)
        );

        await _settingsService.DeleteModelProfileAsync(customProfile.Id);

        Assert.AreEqual(1, _queueSignal.PreemptCount);
        var settings = await _dbContext.HouseholdLlmSettings.SingleAsync();
        Assert.IsNull(settings.EmbeddingModelProfileId);
        Assert.IsNull(settings.EmbeddingModel);
    }
}

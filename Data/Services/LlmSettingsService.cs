using System.Text.Json;
using App.Core.Exceptions;
using App.Core.Ingestion;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace App.Data.Services;

public sealed class LlmSettingsService(
    AppDbContext dbContext,
    IOptions<LlmDefaultsOptions> defaultOptions,
    ILlmExtractionClient llmClient,
    IIngestionQueueSignal queueSignal
) : ILlmSettingsService
{
    public async Task<LlmSettingsDto> GetSettingsAsync(
        CancellationToken cancellationToken = default
    )
    {
        var entity = await QuerySettingsWithAssignments()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.SingletonKey == HouseholdLlmSettings.DefaultSingletonKey,
                cancellationToken
            );

        if (entity is not null)
        {
            // Connection, model and tuning fields report what each workflow actually uses.
            var effective = ApplyWorkflowAssignments(BuildLegacyConfig(entity), entity);
            var refine = effective.ForRefinement();
            return new LlmSettingsDto(
                Provider: effective.Provider,
                BaseUrl: effective.BaseUrl,
                HasApiKey: !string.IsNullOrWhiteSpace(effective.ApiKey),
                ApiKeyMasked: MaskApiKey(effective.ApiKey),
                TextModel: effective.TextModel,
                VisionModel: effective.VisionModel,
                OllamaKeepAlive: effective.OllamaKeepAlive,
                RequestTimeoutSeconds: effective.RequestTimeoutSeconds,
                AutoPublishHighConfidenceText: entity.AutoPublishHighConfidenceText,
                EnableThinking: effective.EnableThinking,
                MaxIngestionRetries: entity.MaxIngestionRetries,
                EnableIngestion: entity.EnableIngestion,
                ContextWindowTokens: effective.ContextWindowTokens,
                ModifiedAt: entity.ModifiedAt,
                OllamaCpuOnly: effective.OllamaCpuOnly,
                Temperature: effective.Temperature,
                TopP: effective.TopP,
                TopK: effective.TopK,
                RefineModel: effective.RefineModel,
                RefineEnableThinking: refine.EnableThinking,
                RefineTemperature: refine.Temperature,
                RefineTopP: refine.TopP,
                RefineTopK: refine.TopK,
                RefineRequestTimeoutSeconds: refine.RequestTimeoutSeconds,
                EnableRateLimitSafeguards: effective.EnableRateLimitSafeguards,
                MaxConcurrentRequests: effective.MaxConcurrentRequests,
                RequestsPerMinute: effective.RequestsPerMinute,
                DailyRequestQuota: effective.DailyRequestQuota,
                DailyQuotaResetTimezone: effective.DailyQuotaResetTimezone,
                DailyQuotaResetTime: effective.DailyQuotaResetTime,
                AutoResumeQueue: entity.AutoResumeQueue,
                PausedUntilUtc: entity.PausedUntilUtc,
                PauseReason: entity.PauseReason,
                EmbeddingModel: effective.EmbeddingModel,
                TextModelProfileId: entity.TextModelProfileId,
                VisionModelProfileId: entity.VisionModelProfileId,
                RefineModelProfileId: entity.RefineModelProfileId,
                EmbeddingModelProfileId: entity.EmbeddingModelProfileId,
                RefineDuringIngestion: entity.RefineDuringIngestion,
                RefineDuringIdle: entity.RefineDuringIdle
            );
        }

        var opt = defaultOptions.Value;
        return new LlmSettingsDto(
            Provider: opt.Provider,
            BaseUrl: opt.BaseUrl,
            HasApiKey: !string.IsNullOrWhiteSpace(opt.ApiKey),
            ApiKeyMasked: MaskApiKey(opt.ApiKey),
            TextModel: opt.TextModel,
            VisionModel: opt.VisionModel,
            OllamaKeepAlive: opt.OllamaKeepAlive,
            RequestTimeoutSeconds: opt.RequestTimeoutSeconds,
            AutoPublishHighConfidenceText: opt.AutoPublishHighConfidenceText,
            EnableThinking: opt.EnableThinking,
            MaxIngestionRetries: opt.MaxIngestionRetries,
            EnableIngestion: opt.EnableIngestion,
            ContextWindowTokens: opt.ContextWindowTokens,
            ModifiedAt: null,
            OllamaCpuOnly: opt.OllamaCpuOnly,
            Temperature: opt.Temperature,
            TopP: opt.TopP,
            TopK: opt.TopK,
            RefineModel: opt.RefineModel,
            RefineEnableThinking: opt.RefineEnableThinking,
            RefineTemperature: opt.RefineTemperature,
            RefineTopP: opt.RefineTopP,
            RefineTopK: opt.RefineTopK,
            RefineRequestTimeoutSeconds: opt.RefineRequestTimeoutSeconds,
            EnableRateLimitSafeguards: opt.EnableRateLimitSafeguards,
            MaxConcurrentRequests: opt.MaxConcurrentRequests,
            RequestsPerMinute: opt.RequestsPerMinute,
            DailyRequestQuota: opt.DailyRequestQuota,
            DailyQuotaResetTimezone: opt.DailyQuotaResetTimezone ?? "UTC",
            DailyQuotaResetTime: opt.DailyQuotaResetTime ?? "00:00",
            AutoResumeQueue: opt.AutoResumeQueue,
            PausedUntilUtc: opt.PausedUntilUtc,
            PauseReason: opt.PauseReason,
            EmbeddingModel: opt.EmbeddingModel,
            RefineDuringIngestion: opt.RefineDuringIngestion,
            RefineDuringIdle: opt.RefineDuringIdle
        );
    }

    public async Task<EffectiveLlmConnectionConfig> GetEffectiveConfigAsync(
        CancellationToken cancellationToken = default
    )
    {
        var entity = await QuerySettingsWithAssignments()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.SingletonKey == HouseholdLlmSettings.DefaultSingletonKey,
                cancellationToken
            );

        if (entity is not null)
        {
            return ApplyWorkflowAssignments(BuildLegacyConfig(entity), entity);
        }

        var opt = defaultOptions.Value;
        return new EffectiveLlmConnectionConfig(
            Provider: opt.Provider,
            BaseUrl: opt.BaseUrl,
            ApiKey: opt.ApiKey,
            TextModel: opt.TextModel,
            VisionModel: opt.VisionModel,
            OllamaKeepAlive: opt.OllamaKeepAlive,
            RequestTimeoutSeconds: opt.RequestTimeoutSeconds,
            AutoPublishHighConfidenceText: opt.AutoPublishHighConfidenceText,
            EnableThinking: opt.EnableThinking,
            MaxIngestionRetries: opt.MaxIngestionRetries,
            EnableIngestion: opt.EnableIngestion,
            ContextWindowTokens: opt.ContextWindowTokens,
            OllamaCpuOnly: opt.OllamaCpuOnly,
            Temperature: opt.Temperature,
            TopP: opt.TopP,
            TopK: opt.TopK,
            RefineModel: opt.RefineModel,
            RefineEnableThinking: opt.RefineEnableThinking,
            RefineTemperature: opt.RefineTemperature,
            RefineTopP: opt.RefineTopP,
            RefineTopK: opt.RefineTopK,
            RefineRequestTimeoutSeconds: opt.RefineRequestTimeoutSeconds,
            EnableRateLimitSafeguards: opt.EnableRateLimitSafeguards,
            MaxConcurrentRequests: opt.MaxConcurrentRequests,
            RequestsPerMinute: opt.RequestsPerMinute,
            DailyRequestQuota: opt.DailyRequestQuota,
            DailyQuotaResetTimezone: opt.DailyQuotaResetTimezone ?? "UTC",
            DailyQuotaResetTime: opt.DailyQuotaResetTime ?? "00:00",
            AutoResumeQueue: opt.AutoResumeQueue,
            PausedUntilUtc: opt.PausedUntilUtc,
            PauseReason: opt.PauseReason,
            EmbeddingModel: opt.EmbeddingModel,
            RefineDuringIngestion: opt.RefineDuringIngestion,
            RefineDuringIdle: opt.RefineDuringIdle
        );
    }

    public async Task<LlmSettingsDto> UpdateSettingsAsync(
        UpdateLlmSettingsRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ValidateUrl(request.BaseUrl);

        var entity = await dbContext.HouseholdLlmSettings.FirstOrDefaultAsync(
            s => s.SingletonKey == HouseholdLlmSettings.DefaultSingletonKey,
            cancellationToken
        );

        if (entity is null)
        {
            entity = new HouseholdLlmSettings
            {
                SingletonKey = HouseholdLlmSettings.DefaultSingletonKey,
            };
            dbContext.HouseholdLlmSettings.Add(entity);
        }

        entity.Provider = request.Provider;
        entity.BaseUrl = request.BaseUrl.Trim();

        if (request.ClearApiKey)
        {
            entity.ApiKey = null;
        }
        else if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            entity.ApiKey = request.ApiKey.Trim();
        }

        entity.TextModel = string.IsNullOrWhiteSpace(request.TextModel)
            ? null
            : request.TextModel.Trim();
        entity.VisionModel = string.IsNullOrWhiteSpace(request.VisionModel)
            ? null
            : request.VisionModel.Trim();
        entity.EmbeddingModel = string.IsNullOrWhiteSpace(request.EmbeddingModel)
            ? null
            : request.EmbeddingModel.Trim();
        entity.OllamaKeepAlive = string.IsNullOrWhiteSpace(request.OllamaKeepAlive)
            ? "5m"
            : request.OllamaKeepAlive.Trim();
        entity.RequestTimeoutSeconds = request.RequestTimeoutSeconds;
        entity.AutoPublishHighConfidenceText = request.AutoPublishHighConfidenceText;
        entity.EnableThinking = request.EnableThinking;
        entity.OllamaCpuOnly = request.OllamaCpuOnly;
        var wasIngestionEnabled = entity.EnableIngestion;
        entity.MaxIngestionRetries = request.MaxIngestionRetries;
        entity.EnableIngestion = request.EnableIngestion;
        entity.ContextWindowTokens = request.ContextWindowTokens;
        entity.Temperature = request.Temperature;
        entity.TopP = request.TopP;
        entity.TopK = request.TopK;
        var wasRefineDuringIdle = entity.RefineDuringIdle;
        var previousRefineModel = entity.RefineModel;
        entity.RefineModel = string.IsNullOrWhiteSpace(request.RefineModel)
            ? null
            : request.RefineModel.Trim();
        entity.RefineDuringIngestion = request.RefineDuringIngestion;
        entity.RefineDuringIdle = request.RefineDuringIdle;
        entity.RefineEnableThinking = request.RefineEnableThinking;
        entity.RefineTemperature = request.RefineTemperature;
        entity.RefineTopP = request.RefineTopP;
        entity.RefineTopK = request.RefineTopK;
        entity.RefineRequestTimeoutSeconds = request.RefineRequestTimeoutSeconds;
        entity.EnableRateLimitSafeguards = request.EnableRateLimitSafeguards;
        entity.MaxConcurrentRequests = request.MaxConcurrentRequests;
        entity.RequestsPerMinute = request.RequestsPerMinute;
        entity.DailyRequestQuota = request.DailyRequestQuota;
        entity.DailyQuotaResetTimezone = string.IsNullOrWhiteSpace(request.DailyQuotaResetTimezone)
            ? "UTC"
            : request.DailyQuotaResetTimezone.Trim();
        entity.DailyQuotaResetTime = string.IsNullOrWhiteSpace(request.DailyQuotaResetTime)
            ? "00:00"
            : request.DailyQuotaResetTime.Trim();
        entity.AutoResumeQueue = request.AutoResumeQueue;
        if (request.EnableIngestion)
        {
            entity.PausedUntilUtc = null;
            entity.PauseReason = null;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var hasRefineModel =
            !string.IsNullOrWhiteSpace(entity.RefineModel)
            || entity.RefineModelProfileId is not null;
        if (wasRefineDuringIdle && (!entity.RefineDuringIdle || !hasRefineModel))
        {
            queueSignal.PreemptBackgroundWork();
        }

        await ApplyQueueSideEffectsAsync(
            wasIngestionEnabled,
            request.EnableIngestion,
            wakeWaitingJobs: !string.IsNullOrWhiteSpace(entity.TextModel)
                || entity.TextModelProfileId is not null,
            wakeIdleRefinement: entity.EnableIngestion
                && entity.RefineDuringIdle
                && hasRefineModel
                && (
                    !wasRefineDuringIdle
                    || !string.Equals(
                        previousRefineModel,
                        entity.RefineModel,
                        StringComparison.OrdinalIgnoreCase
                    )
                ),
            cancellationToken
        );

        if (
            entity.EnableIngestion
            && (
                !string.IsNullOrWhiteSpace(entity.EmbeddingModel)
                || entity.EmbeddingModelProfileId is not null
            )
        )
        {
            queueSignal.NotifyBackgroundWorkAvailable();
        }

        return await GetSettingsAsync(cancellationToken);
    }

    public async Task<LlmSettingsDto> UpdateWorkflowsAsync(
        UpdateLlmWorkflowsRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var requestedIds = new[]
        {
            request.TextModelProfileId,
            request.VisionModelProfileId,
            request.RefineModelProfileId,
            request.EmbeddingModelProfileId,
        }
            .OfType<Guid>()
            .Distinct()
            .ToList();
        var existingIds = await dbContext
            .LlmModelProfiles.Where(p => requestedIds.Contains(p.Id))
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);
        var missing = requestedIds.Except(existingIds).ToList();
        if (missing.Count > 0)
        {
            throw new DomainValidationException($"Model profile '{missing[0]}' does not exist.");
        }

        var entity = await dbContext.HouseholdLlmSettings.FirstOrDefaultAsync(
            s => s.SingletonKey == HouseholdLlmSettings.DefaultSingletonKey,
            cancellationToken
        );
        if (entity is null)
        {
            entity = CreateFromDefaults();
            dbContext.HouseholdLlmSettings.Add(entity);
        }

        var wasRefineDuringIdle = entity.RefineDuringIdle;
        var previousRefineProfileId = entity.RefineModelProfileId;

        entity.TextModelProfileId = request.TextModelProfileId;
        entity.VisionModelProfileId = request.VisionModelProfileId;
        entity.RefineModelProfileId = request.RefineModelProfileId;
        entity.EmbeddingModelProfileId = request.EmbeddingModelProfileId;

        // An unassigned workflow is off; never fall back to the old single-host models.
        if (request.TextModelProfileId is null)
        {
            entity.TextModel = null;
        }
        if (request.VisionModelProfileId is null)
        {
            entity.VisionModel = null;
        }
        if (request.RefineModelProfileId is null)
        {
            entity.RefineModel = null;
        }
        if (request.EmbeddingModelProfileId is null)
        {
            entity.EmbeddingModel = null;
        }

        var wasIngestionEnabled = entity.EnableIngestion;
        entity.AutoPublishHighConfidenceText = request.AutoPublishHighConfidenceText;
        entity.MaxIngestionRetries = request.MaxIngestionRetries;
        entity.EnableIngestion = request.EnableIngestion;
        entity.AutoResumeQueue = request.AutoResumeQueue;
        entity.RefineDuringIngestion = request.RefineDuringIngestion;
        entity.RefineDuringIdle = request.RefineDuringIdle;
        if (request.EnableIngestion)
        {
            entity.PausedUntilUtc = null;
            entity.PauseReason = null;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        if (
            wasRefineDuringIdle && (!entity.RefineDuringIdle || entity.RefineModelProfileId is null)
        )
        {
            queueSignal.PreemptBackgroundWork();
        }

        await ApplyQueueSideEffectsAsync(
            wasIngestionEnabled,
            request.EnableIngestion,
            wakeWaitingJobs: request.TextModelProfileId is not null,
            wakeIdleRefinement: entity.EnableIngestion
                && entity.RefineDuringIdle
                && entity.RefineModelProfileId is not null
                && (!wasRefineDuringIdle || previousRefineProfileId != entity.RefineModelProfileId),
            cancellationToken
        );

        if (entity.EnableIngestion && request.EmbeddingModelProfileId is not null)
        {
            queueSignal.NotifyBackgroundWorkAvailable();
        }

        return await GetSettingsAsync(cancellationToken);
    }

    private async Task ApplyQueueSideEffectsAsync(
        bool wasIngestionEnabled,
        bool enableIngestion,
        bool wakeWaitingJobs,
        bool wakeIdleRefinement,
        CancellationToken cancellationToken
    )
    {
        if (!enableIngestion && wasIngestionEnabled)
        {
            queueSignal.CancelActiveExecution();
            var workingJobs = await dbContext
                .IngestionJobs.Where(j =>
                    j.Status == IngestionJobStatus.Preprocessing
                    || j.Status == IngestionJobStatus.Extracting
                    || j.RunNowRequested
                )
                .ToListAsync(cancellationToken);

            foreach (var job in workingJobs)
            {
                job.Status = IngestionJobStatus.Queued;
                job.NextRetryAt = null;
                job.StatusDetail = "Paused. Queued to resume when ingestion is enabled.";
                job.RunNowRequested = false;
            }

            if (workingJobs.Count > 0)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
        else if (enableIngestion && !wasIngestionEnabled)
        {
            queueSignal.NotifyWorkAvailable();
        }

        if (wakeWaitingJobs)
        {
            await WakeWaitingJobsAsync(dbContext, queueSignal, cancellationToken);
        }

        if (wakeIdleRefinement)
        {
            await WakeIdleRefinementJobsAsync(dbContext, queueSignal, cancellationToken);
        }
    }

    /// <summary>Resets backoff on unrefined jobs and wakes the idle worker when refinement settings change.</summary>
    internal static async Task WakeIdleRefinementJobsAsync(
        AppDbContext dbContext,
        IIngestionQueueSignal queueSignal,
        CancellationToken cancellationToken
    )
    {
        var backedOffJobs = await dbContext
            .IngestionJobs.Where(j =>
                (
                    j.Status == IngestionJobStatus.ReadyForReview
                    || j.Status == IngestionJobStatus.Published
                )
                && j.RefinedAt == null
                && (j.RefinementNextRetryAt != null || j.RefinementAttemptCount > 0)
            )
            .ToListAsync(cancellationToken);

        if (backedOffJobs.Count > 0)
        {
            foreach (var job in backedOffJobs)
            {
                job.RefinementNextRetryAt = null;
                job.RefinementAttemptCount = 0;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        queueSignal.NotifyBackgroundWorkAvailable();
    }

    /// <summary>Retries jobs parked while the LLM host was offline after the LLM setup changes.</summary>
    internal static async Task WakeWaitingJobsAsync(
        AppDbContext dbContext,
        IIngestionQueueSignal queueSignal,
        CancellationToken cancellationToken
    )
    {
        var waitingJobs = await dbContext
            .IngestionJobs.Where(j => j.Status == IngestionJobStatus.WaitingForLlmHost)
            .ToListAsync(cancellationToken);

        if (waitingJobs.Count > 0)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var job in waitingJobs)
            {
                job.NextRetryAt = now;
            }
            await dbContext.SaveChangesAsync(cancellationToken);
            queueSignal.NotifyWorkAvailable();
        }
    }

    private IQueryable<HouseholdLlmSettings> QuerySettingsWithAssignments() =>
        dbContext
            .HouseholdLlmSettings.Include(s => s.TextModelProfile)
                .ThenInclude(p => p!.ModelConfiguration)
                    .ThenInclude(c => c!.Connection)
            .Include(s => s.VisionModelProfile)
                .ThenInclude(p => p!.ModelConfiguration)
                    .ThenInclude(c => c!.Connection)
            .Include(s => s.RefineModelProfile)
                .ThenInclude(p => p!.ModelConfiguration)
                    .ThenInclude(c => c!.Connection)
            .Include(s => s.EmbeddingModelProfile)
                .ThenInclude(p => p!.ModelConfiguration)
                    .ThenInclude(c => c!.Connection);

    private static EffectiveLlmConnectionConfig BuildLegacyConfig(HouseholdLlmSettings entity) =>
        new(
            Provider: entity.Provider,
            BaseUrl: entity.BaseUrl,
            ApiKey: entity.ApiKey,
            TextModel: entity.TextModel,
            VisionModel: entity.VisionModel,
            OllamaKeepAlive: entity.OllamaKeepAlive,
            RequestTimeoutSeconds: entity.RequestTimeoutSeconds,
            AutoPublishHighConfidenceText: entity.AutoPublishHighConfidenceText,
            EnableThinking: entity.EnableThinking,
            MaxIngestionRetries: entity.MaxIngestionRetries,
            EnableIngestion: entity.EnableIngestion,
            ContextWindowTokens: entity.ContextWindowTokens,
            OllamaCpuOnly: entity.OllamaCpuOnly,
            Temperature: entity.Temperature,
            TopP: entity.TopP,
            TopK: entity.TopK,
            RefineModel: entity.RefineModel,
            RefineEnableThinking: entity.RefineEnableThinking,
            RefineTemperature: entity.RefineTemperature,
            RefineTopP: entity.RefineTopP,
            RefineTopK: entity.RefineTopK,
            RefineRequestTimeoutSeconds: entity.RefineRequestTimeoutSeconds,
            EnableRateLimitSafeguards: entity.EnableRateLimitSafeguards,
            MaxConcurrentRequests: entity.MaxConcurrentRequests,
            RequestsPerMinute: entity.RequestsPerMinute,
            DailyRequestQuota: entity.DailyRequestQuota,
            DailyQuotaResetTimezone: entity.DailyQuotaResetTimezone ?? "UTC",
            DailyQuotaResetTime: entity.DailyQuotaResetTime ?? "00:00",
            AutoResumeQueue: entity.AutoResumeQueue,
            PausedUntilUtc: entity.PausedUntilUtc,
            PauseReason: entity.PauseReason,
            EmbeddingModel: entity.EmbeddingModel,
            RefineDuringIngestion: entity.RefineDuringIngestion,
            RefineDuringIdle: entity.RefineDuringIdle
        );

    /// <summary>
    /// Replaces the old single-host values with each workflow's assigned model profile so
    /// text, vision, refinement, and embedding resolve their exact connection, model, rate limits,
    /// and tuning from their selected profile.
    /// </summary>
    private static EffectiveLlmConnectionConfig ApplyWorkflowAssignments(
        EffectiveLlmConnectionConfig legacy,
        HouseholdLlmSettings entity
    )
    {
        var text = entity.TextModelProfile
            is { ModelConfiguration.Connection: not null } textProfile
            ? WithModelProfile(legacy, textProfile)
            : legacy;

        var vision = entity.VisionModelProfile
            is { ModelConfiguration.Connection: not null } visionProfile
            ? WithModelProfile(legacy, visionProfile) with
            {
                TextModel = visionProfile.ModelConfiguration.Model,
                VisionModel = visionProfile.ModelConfiguration.Model,
                RefineModel = null,
            }
            : null;

        var refine = entity.RefineModelProfile
            is { ModelConfiguration.Connection: not null } refineProfile
            ? WithModelProfile(legacy, refineProfile) with
            {
                TextModel = refineProfile.ModelConfiguration.Model,
                VisionModel = refineProfile.ModelConfiguration.Model,
                RefineModel = refineProfile.ModelConfiguration.Model,
            }
            : null;

        var embedding = entity.EmbeddingModelProfile
            is { ModelConfiguration.Connection: not null } embeddingProfile
            ? WithModelProfile(legacy, embeddingProfile) with
            {
                TextModel = embeddingProfile.ModelConfiguration.Model,
                VisionModel = null,
                RefineModel = null,
                EmbeddingModel = embeddingProfile.ModelConfiguration.Model,
            }
            : null;

        return text with
        {
            TextModel = entity.TextModelProfile?.ModelConfiguration?.Model ?? legacy.TextModel,
            VisionModel = vision?.VisionModel ?? legacy.VisionModel,
            RefineModel = refine?.RefineModel ?? legacy.RefineModel,
            EmbeddingModel = embedding?.EmbeddingModel ?? legacy.EmbeddingModel,
            VisionConfig = vision,
            RefineConfig = refine,
            EmbeddingConfig = embedding,
        };
    }

    internal static EffectiveLlmConnectionConfig WithConnection(
        EffectiveLlmConnectionConfig baseConfig,
        LlmConnection connection
    ) =>
        baseConfig with
        {
            Provider = connection.Provider,
            BaseUrl = connection.BaseUrl,
            ApiKey = connection.ApiKey,
            OllamaKeepAlive = connection.OllamaKeepAlive,
            OllamaCpuOnly = connection.OllamaCpuOnly,
            EnableRateLimitSafeguards = connection.EnableRateLimitSafeguards,
            MaxConcurrentRequests = connection.MaxConcurrentRequests,
            RequestsPerMinute = connection.RequestsPerMinute,
            DailyRequestQuota = connection.DailyRequestQuota,
            DailyQuotaResetTimezone = connection.DailyQuotaResetTimezone,
            DailyQuotaResetTime = connection.DailyQuotaResetTime,
            ConnectionKey = connection.Id.ToString(),
        };

    private static EffectiveLlmConnectionConfig WithModelProfile(
        EffectiveLlmConnectionConfig baseConfig,
        LlmModelProfile profile
    )
    {
        var configuration = profile.ModelConfiguration!;
        return WithConnection(baseConfig, configuration.Connection!) with
        {
            TextModel = configuration.Model,
            EnableThinking = profile.EnableThinking,
            Temperature = profile.Temperature,
            TopP = profile.TopP,
            TopK = profile.TopK,
            ContextWindowTokens = profile.ContextWindowTokens,
            RequestTimeoutSeconds = profile.RequestTimeoutSeconds,
            OllamaGpuLayers = configuration.OllamaGpuLayers,
            ModelKey =
                $"{configuration.ConnectionId}:{configuration.Model.Trim().ToLowerInvariant()}",
            ModelMaxConcurrentRequests = configuration.MaxConcurrentRequests,
            ModelRequestsPerMinute = configuration.RequestsPerMinute,
            ModelDailyRequestQuota = configuration.DailyRequestQuota,
            ModelDailyQuotaResetTimezone = configuration.DailyQuotaResetTimezone,
            ModelDailyQuotaResetTime = configuration.DailyQuotaResetTime,
            VisionConfig = null,
            RefineConfig = null,
            EmbeddingConfig = null,
        };
    }

    public async Task<LlmConnectionTestResultDto> TestConnectionAsync(
        TestLlmConnectionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (!string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            ValidateUrl(request.BaseUrl);
        }

        var effective = await GetEffectiveConfigAsync(cancellationToken);
        if (request.ConnectionId is { } connectionId)
        {
            var connection =
                await dbContext
                    .LlmConnections.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == connectionId, cancellationToken)
                ?? throw new NotFoundException($"LLM connection '{connectionId}' not found.");
            effective = WithConnection(effective, connection);
        }

        var provider = request.Provider ?? effective.Provider;
        var baseUrl = !string.IsNullOrWhiteSpace(request.BaseUrl)
            ? request.BaseUrl.Trim()
            : effective.BaseUrl;
        var apiKey = request.ClearApiKey
            ? null
            : (
                !string.IsNullOrWhiteSpace(request.ApiKey)
                    ? request.ApiKey.Trim()
                    : effective.ApiKey
            );
        var timeout = request.TimeoutSeconds ?? 15;

        var testConfig = new EffectiveLlmConnectionConfig(
            Provider: provider,
            BaseUrl: baseUrl,
            ApiKey: apiKey,
            TextModel: effective.TextModel,
            VisionModel: effective.VisionModel,
            OllamaKeepAlive: effective.OllamaKeepAlive,
            RequestTimeoutSeconds: timeout,
            AutoPublishHighConfidenceText: effective.AutoPublishHighConfidenceText,
            EnableThinking: effective.EnableThinking,
            MaxIngestionRetries: effective.MaxIngestionRetries,
            EnableIngestion: effective.EnableIngestion
        );

        return await llmClient.ProbeAndListModelsAsync(testConfig, timeout, cancellationToken);
    }

    public Task<LlmSettingsDto> SetIngestionEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default
    ) => SetIngestionEnabledAsync(enabled, null, null, cancellationToken);

    public async Task<LlmSettingsDto> SetIngestionEnabledAsync(
        bool enabled,
        DateTimeOffset? autoResumeAt,
        string? pauseReason,
        CancellationToken cancellationToken = default
    )
    {
        var entity = await dbContext.HouseholdLlmSettings.FirstOrDefaultAsync(
            s => s.SingletonKey == HouseholdLlmSettings.DefaultSingletonKey,
            cancellationToken
        );

        if (entity is null)
        {
            entity = CreateFromDefaults();
            dbContext.HouseholdLlmSettings.Add(entity);
        }

        var wasEnabled = entity.EnableIngestion;
        entity.EnableIngestion = enabled;
        if (enabled)
        {
            entity.PausedUntilUtc = null;
            entity.PauseReason = null;
        }
        else
        {
            entity.PausedUntilUtc = autoResumeAt;
            entity.PauseReason = pauseReason;
        }
        await dbContext.SaveChangesAsync(cancellationToken);

        if (!enabled)
        {
            queueSignal.CancelActiveExecution();

            var workingJobs = await dbContext
                .IngestionJobs.Where(j =>
                    j.Status == IngestionJobStatus.Preprocessing
                    || j.Status == IngestionJobStatus.Extracting
                    || j.RunNowRequested
                )
                .ToListAsync(cancellationToken);

            foreach (var job in workingJobs)
            {
                job.Status = IngestionJobStatus.Queued;
                job.NextRetryAt = null;
                job.StatusDetail = "Paused. Queued to resume when ingestion is enabled.";
                job.RunNowRequested = false;
            }

            if (workingJobs.Count > 0)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
        else if (!wasEnabled)
        {
            queueSignal.NotifyWorkAvailable();
        }

        return await GetSettingsAsync(cancellationToken);
    }

    // Toggling pause before any settings were saved must keep the configured defaults.
    private HouseholdLlmSettings CreateFromDefaults()
    {
        var opt = defaultOptions.Value;
        return new HouseholdLlmSettings
        {
            SingletonKey = HouseholdLlmSettings.DefaultSingletonKey,
            Provider = opt.Provider,
            BaseUrl = opt.BaseUrl,
            ApiKey = opt.ApiKey,
            TextModel = opt.TextModel,
            VisionModel = opt.VisionModel,
            EmbeddingModel = opt.EmbeddingModel,
            OllamaKeepAlive = opt.OllamaKeepAlive,
            RequestTimeoutSeconds = opt.RequestTimeoutSeconds,
            AutoPublishHighConfidenceText = opt.AutoPublishHighConfidenceText,
            EnableThinking = opt.EnableThinking,
            MaxIngestionRetries = opt.MaxIngestionRetries,
            EnableIngestion = opt.EnableIngestion,
            ContextWindowTokens = opt.ContextWindowTokens,
            OllamaCpuOnly = opt.OllamaCpuOnly,
            Temperature = opt.Temperature,
            TopP = opt.TopP,
            TopK = opt.TopK,
            RefineModel = opt.RefineModel,
            RefineDuringIngestion = opt.RefineDuringIngestion,
            RefineDuringIdle = opt.RefineDuringIdle,
            RefineEnableThinking = opt.RefineEnableThinking,
            RefineTemperature = opt.RefineTemperature,
            RefineTopP = opt.RefineTopP,
            RefineTopK = opt.RefineTopK,
            RefineRequestTimeoutSeconds = opt.RefineRequestTimeoutSeconds,
            EnableRateLimitSafeguards = opt.EnableRateLimitSafeguards,
            MaxConcurrentRequests = opt.MaxConcurrentRequests,
            RequestsPerMinute = opt.RequestsPerMinute,
            DailyRequestQuota = opt.DailyRequestQuota,
            AutoResumeQueue = opt.AutoResumeQueue,
            PausedUntilUtc = opt.PausedUntilUtc,
            PauseReason = opt.PauseReason,
        };
    }

    internal static void ValidateUrl(string? url)
    {
        if (
            string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        )
        {
            throw new DomainValidationException("BaseUrl must be a valid http or https URL.");
        }
    }

    internal static string? MaskApiKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var trimmed = apiKey.Trim();
        if (trimmed.Length <= 4)
        {
            return "••••••••";
        }

        return "••••" + trimmed[^4..];
    }

    public async Task<IReadOnlyList<LlmModelProfileDto>> GetModelProfilesAsync(
        CancellationToken cancellationToken = default
    )
    {
        var entities = await dbContext
            .LlmModelProfiles.AsNoTracking()
            .Include(p => p.ModelConfiguration)
                .ThenInclude(c => c!.Connection)
            .OrderBy(p => p.ModelConfiguration!.Connection!.Name)
            .ThenBy(p => p.ModelConfiguration!.Name)
            .ThenByDescending(p => p.IsDefault)
            .ThenBy(p => p.DisplayName)
            .ToListAsync(cancellationToken);

        var settings = await dbContext
            .HouseholdLlmSettings.AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.SingletonKey == HouseholdLlmSettings.DefaultSingletonKey,
                cancellationToken
            );

        return entities.Select(p => ToDto(p, settings)).ToList();
    }

    public async Task<LlmModelProfileDto> CreateModelProfileAsync(
        UpsertLlmModelProfileRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ValidateProfileRequest(request);

        var modelConfig =
            await dbContext
                .LlmModelConfigurations.Include(c => c.Connection)
                .FirstOrDefaultAsync(c => c.Id == request.ModelConfigurationId, cancellationToken)
            ?? throw new DomainValidationException("Choose a saved model for this profile.");

        await EnsureUniqueProfileNameAsync(
            request.ModelConfigurationId,
            request.DisplayName,
            modelConfig.Name,
            excludeId: null,
            cancellationToken
        );

        var entity = new LlmModelProfile
        {
            Id = Guid.NewGuid(),
            ModelConfigurationId = modelConfig.Id,
            ModelConfiguration = modelConfig,
            DisplayName = request.DisplayName.Trim(),
            Summary = request.Summary?.Trim() ?? string.Empty,
            EnableThinking = request.EnableThinking,
            Temperature = request.Temperature,
            TopP = request.TopP,
            TopK = request.TopK,
            ContextWindowTokens = request.ContextWindowTokens,
            RequestTimeoutSeconds = request.RequestTimeoutSeconds,
            NotesJson = JsonSerializer.Serialize(request.Notes ?? []),
            IsDefault = false,
            CreatedBy = "System",
            ModifiedBy = "System",
        };

        dbContext.LlmModelProfiles.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        var settings = await dbContext
            .HouseholdLlmSettings.AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.SingletonKey == HouseholdLlmSettings.DefaultSingletonKey,
                cancellationToken
            );

        return ToDto(entity, settings);
    }

    public async Task<LlmModelProfileDto> UpdateModelProfileAsync(
        Guid id,
        UpsertLlmModelProfileRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ValidateProfileRequest(request);

        var entity =
            await dbContext
                .LlmModelProfiles.Include(p => p.ModelConfiguration)
                    .ThenInclude(c => c!.Connection)
                .FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new NotFoundException($"Model profile '{id}' not found.");

        if (entity.IsDefault)
        {
            if (request.ModelConfigurationId != entity.ModelConfigurationId)
            {
                throw new DomainValidationException(
                    "The Defaults profile cannot be moved to another model."
                );
            }

            if (
                !string.Equals(
                    request.DisplayName.Trim(),
                    LlmProfileDefaults.DefaultDisplayName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                throw new DomainValidationException("The Defaults profile cannot be renamed.");
            }

            entity.DisplayName = LlmProfileDefaults.DefaultDisplayName;
        }
        else
        {
            var modelConfig =
                await dbContext
                    .LlmModelConfigurations.Include(c => c.Connection)
                    .FirstOrDefaultAsync(
                        c => c.Id == request.ModelConfigurationId,
                        cancellationToken
                    )
                ?? throw new DomainValidationException("Choose a saved model for this profile.");

            await EnsureUniqueProfileNameAsync(
                request.ModelConfigurationId,
                request.DisplayName,
                modelConfig.Name,
                excludeId: id,
                cancellationToken
            );

            entity.ModelConfigurationId = modelConfig.Id;
            entity.ModelConfiguration = modelConfig;
            entity.DisplayName = request.DisplayName.Trim();
        }

        entity.Summary = request.Summary?.Trim() ?? string.Empty;
        entity.EnableThinking = request.EnableThinking;
        entity.Temperature = request.Temperature;
        entity.TopP = request.TopP;
        entity.TopK = request.TopK;
        entity.ContextWindowTokens = request.ContextWindowTokens;
        entity.RequestTimeoutSeconds = request.RequestTimeoutSeconds;
        entity.NotesJson = JsonSerializer.Serialize(request.Notes ?? []);

        await dbContext.SaveChangesAsync(cancellationToken);

        var settings = await dbContext
            .HouseholdLlmSettings.AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.SingletonKey == HouseholdLlmSettings.DefaultSingletonKey,
                cancellationToken
            );
        var dto = ToDto(entity, settings);
        if (dto.AssignedWorkflows.Count > 0)
        {
            await WakeWaitingJobsAsync(dbContext, queueSignal, cancellationToken);
        }

        return dto;
    }

    public async Task DeleteModelProfileAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        var entity =
            await dbContext.LlmModelProfiles.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new NotFoundException($"Model profile '{id}' not found.");

        if (entity.IsDefault)
        {
            throw new ConflictException(
                $"The '{entity.DisplayName}' profile is the standard default for this model and cannot be deleted.",
                "Profile is undeletable"
            );
        }

        var settings = await dbContext
            .HouseholdLlmSettings.AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.SingletonKey == HouseholdLlmSettings.DefaultSingletonKey,
                cancellationToken
            );
        var assignedWorkflows = GetAssignedWorkflows(entity.Id, settings);
        if (assignedWorkflows.Count > 0)
        {
            throw new ConflictException(
                $"'{entity.DisplayName}' is selected for the {string.Join(", ", assignedWorkflows)} workflow(s). Choose another profile there first.",
                "Profile in use"
            );
        }

        dbContext.LlmModelProfiles.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureUniqueProfileNameAsync(
        Guid modelConfigurationId,
        string displayName,
        string modelConfigurationName,
        Guid? excludeId,
        CancellationToken cancellationToken
    )
    {
        var normalized = displayName.Trim().ToLower();
        if (
            await dbContext.LlmModelProfiles.AnyAsync(
                p =>
                    p.ModelConfigurationId == modelConfigurationId
                    && p.DisplayName.ToLower() == normalized
                    && p.Id != excludeId,
                cancellationToken
            )
        )
        {
            throw new ConflictException(
                $"A profile named '{displayName.Trim()}' already exists for '{modelConfigurationName}'.",
                "Duplicate profile name"
            );
        }
    }

    private static List<LlmWorkflow> GetAssignedWorkflows(
        Guid profileId,
        HouseholdLlmSettings? settings
    )
    {
        var workflows = new List<LlmWorkflow>();
        if (settings is null)
        {
            return workflows;
        }
        if (settings.TextModelProfileId == profileId)
        {
            workflows.Add(LlmWorkflow.Text);
        }
        if (settings.VisionModelProfileId == profileId)
        {
            workflows.Add(LlmWorkflow.Vision);
        }
        if (settings.RefineModelProfileId == profileId)
        {
            workflows.Add(LlmWorkflow.Refinement);
        }
        if (settings.EmbeddingModelProfileId == profileId)
        {
            workflows.Add(LlmWorkflow.Embedding);
        }
        return workflows;
    }

    private static void ValidateProfileRequest(UpsertLlmModelProfileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            throw new DomainValidationException("DisplayName is required.");
        }

        if (request.ContextWindowTokens < 2048 || request.ContextWindowTokens > 65536)
        {
            throw new DomainValidationException(
                "ContextWindowTokens must be between 2048 and 65536."
            );
        }

        if (request.RequestTimeoutSeconds < 5 || request.RequestTimeoutSeconds > 1800)
        {
            throw new DomainValidationException(
                "RequestTimeoutSeconds must be between 5 and 1800."
            );
        }

        if (request.Temperature is < 0.0 or > 2.0)
        {
            throw new DomainValidationException("Temperature must be between 0.0 and 2.0.");
        }

        if (request.TopP is < 0.0 or > 1.0)
        {
            throw new DomainValidationException("TopP must be between 0.0 and 1.0.");
        }

        if (request.TopK is < 1 or > 1000)
        {
            throw new DomainValidationException("TopK must be between 1 and 1000.");
        }
    }

    private static LlmModelProfileDto ToDto(LlmModelProfile p, HouseholdLlmSettings? settings)
    {
        IReadOnlyList<string> notes;
        try
        {
            notes = JsonSerializer.Deserialize<List<string>>(p.NotesJson) ?? [];
        }
        catch
        {
            notes = [];
        }

        var modelConfig = p.ModelConfiguration;
        var connection = modelConfig?.Connection;

        return new LlmModelProfileDto(
            Id: p.Id,
            ModelConfigurationId: modelConfig?.Id ?? p.ModelConfigurationId,
            ModelConfigurationName: modelConfig?.Name ?? string.Empty,
            ConnectionId: connection?.Id ?? modelConfig?.ConnectionId ?? Guid.Empty,
            ConnectionName: connection?.Name ?? string.Empty,
            Provider: connection?.Provider ?? LlmProvider.Ollama,
            Model: modelConfig?.Model ?? string.Empty,
            DisplayName: p.DisplayName,
            Summary: p.Summary,
            EnableThinking: p.EnableThinking,
            Temperature: p.Temperature,
            TopP: p.TopP,
            TopK: p.TopK,
            ContextWindowTokens: p.ContextWindowTokens,
            RequestTimeoutSeconds: p.RequestTimeoutSeconds,
            Notes: notes,
            IsDefault: p.IsDefault,
            AssignedWorkflows: GetAssignedWorkflows(p.Id, settings),
            ModifiedAt: p.ModifiedAt
        );
    }
}

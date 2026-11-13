using System.Text.Json;
using System.Text.RegularExpressions;
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
            // Connection and model fields report what each workflow actually uses.
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
                TextModelConfigurationId: entity.TextModelConfigurationId,
                VisionModelConfigurationId: entity.VisionModelConfigurationId,
                RefineModelConfigurationId: entity.RefineModelConfigurationId
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
            PauseReason: opt.PauseReason
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
            PauseReason: opt.PauseReason
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
        entity.RefineModel = string.IsNullOrWhiteSpace(request.RefineModel)
            ? null
            : request.RefineModel.Trim();
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

        await ApplyQueueSideEffectsAsync(
            wasIngestionEnabled,
            request.EnableIngestion,
            wakeWaitingJobs: !string.IsNullOrWhiteSpace(entity.TextModel)
                || entity.TextModelConfigurationId is not null,
            cancellationToken
        );

        return await GetSettingsAsync(cancellationToken);
    }

    public async Task<LlmSettingsDto> UpdateWorkflowsAsync(
        UpdateLlmWorkflowsRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var requestedIds = new[]
        {
            request.TextModelConfigurationId,
            request.VisionModelConfigurationId,
            request.RefineModelConfigurationId,
        }
            .OfType<Guid>()
            .Distinct()
            .ToList();
        var existingIds = await dbContext
            .LlmModelConfigurations.Where(c => requestedIds.Contains(c.Id))
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
        var missing = requestedIds.Except(existingIds).ToList();
        if (missing.Count > 0)
        {
            throw new DomainValidationException(
                $"Model configuration '{missing[0]}' does not exist."
            );
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

        entity.TextModelConfigurationId = request.TextModelConfigurationId;
        entity.VisionModelConfigurationId = request.VisionModelConfigurationId;
        entity.RefineModelConfigurationId = request.RefineModelConfigurationId;

        // An unassigned workflow is off; never fall back to the old single-host models.
        if (request.TextModelConfigurationId is null)
        {
            entity.TextModel = null;
        }
        if (request.VisionModelConfigurationId is null)
        {
            entity.VisionModel = null;
        }
        if (request.RefineModelConfigurationId is null)
        {
            entity.RefineModel = null;
        }

        var wasIngestionEnabled = entity.EnableIngestion;
        entity.AutoPublishHighConfidenceText = request.AutoPublishHighConfidenceText;
        entity.MaxIngestionRetries = request.MaxIngestionRetries;
        entity.EnableIngestion = request.EnableIngestion;
        entity.AutoResumeQueue = request.AutoResumeQueue;
        if (request.EnableIngestion)
        {
            entity.PausedUntilUtc = null;
            entity.PauseReason = null;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await ApplyQueueSideEffectsAsync(
            wasIngestionEnabled,
            request.EnableIngestion,
            wakeWaitingJobs: request.TextModelConfigurationId is not null,
            cancellationToken
        );

        return await GetSettingsAsync(cancellationToken);
    }

    private async Task ApplyQueueSideEffectsAsync(
        bool wasIngestionEnabled,
        bool enableIngestion,
        bool wakeWaitingJobs,
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
            .HouseholdLlmSettings.Include(s => s.TextModelConfiguration)
                .ThenInclude(c => c!.Connection)
            .Include(s => s.VisionModelConfiguration)
                .ThenInclude(c => c!.Connection)
            .Include(s => s.RefineModelConfiguration)
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
            PauseReason: entity.PauseReason
        );

    /// <summary>
    /// Replaces the old single-host values with each workflow's assigned model configuration so
    /// text, vision and refinement can run on different machines or providers.
    /// </summary>
    private static EffectiveLlmConnectionConfig ApplyWorkflowAssignments(
        EffectiveLlmConnectionConfig legacy,
        HouseholdLlmSettings entity
    )
    {
        var text = entity.TextModelConfiguration is { Connection: not null } textConfiguration
            ? WithModelConfiguration(legacy, textConfiguration)
            : legacy;

        var vision = entity.VisionModelConfiguration is { Connection: not null } visionConfiguration
            ? WithModelConfiguration(legacy, visionConfiguration) with
            {
                TextModel = visionConfiguration.Model,
                VisionModel = visionConfiguration.Model,
                RefineModel = null,
            }
            : null;

        var refine = entity.RefineModelConfiguration is { Connection: not null } refineConfiguration
            ? WithModelConfiguration(legacy, refineConfiguration) with
            {
                TextModel = refineConfiguration.Model,
                VisionModel = refineConfiguration.Model,
                RefineModel = refineConfiguration.Model,
            }
            : null;

        return text with
        {
            TextModel = entity.TextModelConfiguration?.Model ?? legacy.TextModel,
            VisionModel = vision?.VisionModel ?? legacy.VisionModel,
            RefineModel = refine?.RefineModel ?? legacy.RefineModel,
            VisionConfig = vision,
            RefineConfig = refine,
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

    private static EffectiveLlmConnectionConfig WithModelConfiguration(
        EffectiveLlmConnectionConfig baseConfig,
        LlmModelConfiguration configuration
    ) =>
        WithConnection(baseConfig, configuration.Connection!) with
        {
            TextModel = configuration.Model,
            EnableThinking = configuration.EnableThinking,
            Temperature = configuration.Temperature,
            TopP = configuration.TopP,
            TopK = configuration.TopK,
            ContextWindowTokens = configuration.ContextWindowTokens,
            RequestTimeoutSeconds = configuration.RequestTimeoutSeconds,
            OllamaGpuLayers = configuration.OllamaGpuLayers,
            VisionConfig = null,
            RefineConfig = null,
        };

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
        await EnsureModelProfilesSeededAsync(cancellationToken);

        var entities = await dbContext
            .LlmModelProfiles.AsNoTracking()
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.DisplayName)
            .ToListAsync(cancellationToken);

        return entities.Select(ToDto).ToList();
    }

    public async Task<LlmModelProfileDto> CreateModelProfileAsync(
        UpsertLlmModelProfileRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ValidateProfileRequest(request);
        await EnsureModelProfilesSeededAsync(cancellationToken);

        var slug = await GenerateSlugAsync(request.DisplayName, cancellationToken);
        var maxSortOrder =
            await dbContext.LlmModelProfiles.MaxAsync(p => (int?)p.SortOrder, cancellationToken)
            ?? -1;

        var entity = new LlmModelProfile
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            DisplayName = request.DisplayName.Trim(),
            ModelTag = request.ModelTag.Trim(),
            MatchPrefixesJson = JsonSerializer.Serialize(request.MatchPrefixes ?? []),
            Summary = request.Summary?.Trim() ?? string.Empty,
            EnableThinking = request.EnableThinking,
            Temperature = request.Temperature,
            TopP = request.TopP,
            TopK = request.TopK,
            ContextWindowTokens = request.ContextWindowTokens,
            RequestTimeoutSeconds = request.RequestTimeoutSeconds,
            RecommendedAsRefineModel = request.RecommendedAsRefineModel,
            NotesJson = JsonSerializer.Serialize(request.Notes ?? []),
            SortOrder = maxSortOrder + 1,
            IsBuiltInDefault = false,
            CreatedBy = "System",
            ModifiedBy = "System",
        };

        dbContext.LlmModelProfiles.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        return ToDto(entity);
    }

    public async Task<LlmModelProfileDto> UpdateModelProfileAsync(
        string id,
        UpsertLlmModelProfileRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ValidateProfileRequest(request);
        await EnsureModelProfilesSeededAsync(cancellationToken);

        var entity = await FindProfileAsync(id, cancellationToken);
        if (entity is null)
        {
            throw new NotFoundException($"Model profile '{id}' not found.");
        }

        entity.DisplayName = request.DisplayName.Trim();
        entity.ModelTag = request.ModelTag.Trim();
        entity.MatchPrefixesJson = JsonSerializer.Serialize(request.MatchPrefixes ?? []);
        entity.Summary = request.Summary?.Trim() ?? string.Empty;
        entity.EnableThinking = request.EnableThinking;
        entity.Temperature = request.Temperature;
        entity.TopP = request.TopP;
        entity.TopK = request.TopK;
        entity.ContextWindowTokens = request.ContextWindowTokens;
        entity.RequestTimeoutSeconds = request.RequestTimeoutSeconds;
        entity.RecommendedAsRefineModel = request.RecommendedAsRefineModel;
        entity.NotesJson = JsonSerializer.Serialize(request.Notes ?? []);

        await dbContext.SaveChangesAsync(cancellationToken);

        return ToDto(entity);
    }

    public async Task DeleteModelProfileAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        await EnsureModelProfilesSeededAsync(cancellationToken);

        var entity = await FindProfileAsync(id, cancellationToken);
        if (entity is null)
        {
            throw new NotFoundException($"Model profile '{id}' not found.");
        }

        dbContext.LlmModelProfiles.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LlmModelProfileDto>> RestoreDefaultModelProfilesAsync(
        CancellationToken cancellationToken = default
    )
    {
        await EnsureModelProfilesSeededAsync(cancellationToken);

        for (var i = 0; i < LlmModelProfiles.All.Count; i++)
        {
            var def = LlmModelProfiles.All[i];
            var existing = await dbContext.LlmModelProfiles.FirstOrDefaultAsync(
                p => p.Slug.ToLower() == def.Id.ToLower(),
                cancellationToken
            );

            if (existing is not null)
            {
                existing.DisplayName = def.DisplayName;
                existing.ModelTag = def.ModelTag;
                existing.MatchPrefixesJson = JsonSerializer.Serialize(def.MatchPrefixes);
                existing.Summary = def.Summary;
                existing.EnableThinking = def.EnableThinking;
                existing.Temperature = def.Temperature;
                existing.TopP = def.TopP;
                existing.TopK = def.TopK;
                existing.ContextWindowTokens = def.ContextWindowTokens;
                existing.RequestTimeoutSeconds = def.RequestTimeoutSeconds;
                existing.RecommendedAsRefineModel = def.RecommendedAsRefineModel;
                existing.NotesJson = JsonSerializer.Serialize(def.Notes);
                existing.SortOrder = i;
                existing.IsBuiltInDefault = true;
            }
            else
            {
                dbContext.LlmModelProfiles.Add(
                    new LlmModelProfile
                    {
                        Id = Guid.NewGuid(),
                        Slug = def.Id,
                        DisplayName = def.DisplayName,
                        ModelTag = def.ModelTag,
                        MatchPrefixesJson = JsonSerializer.Serialize(def.MatchPrefixes),
                        Summary = def.Summary,
                        EnableThinking = def.EnableThinking,
                        Temperature = def.Temperature,
                        TopP = def.TopP,
                        TopK = def.TopK,
                        ContextWindowTokens = def.ContextWindowTokens,
                        RequestTimeoutSeconds = def.RequestTimeoutSeconds,
                        RecommendedAsRefineModel = def.RecommendedAsRefineModel,
                        NotesJson = JsonSerializer.Serialize(def.Notes),
                        SortOrder = i,
                        IsBuiltInDefault = true,
                        CreatedBy = "System",
                        ModifiedBy = "System",
                    }
                );
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return await GetModelProfilesAsync(cancellationToken);
    }

    private async Task EnsureModelProfilesSeededAsync(CancellationToken cancellationToken)
    {
        var settings = await dbContext.HouseholdLlmSettings.FirstOrDefaultAsync(
            s => s.SingletonKey == HouseholdLlmSettings.DefaultSingletonKey,
            cancellationToken
        );

        if (settings is null)
        {
            settings = CreateFromDefaults();
            dbContext.HouseholdLlmSettings.Add(settings);
        }

        await SeedBuiltInProfilesAsync(settings, cancellationToken);
    }

    private async Task SeedBuiltInProfilesAsync(
        HouseholdLlmSettings settings,
        CancellationToken cancellationToken
    )
    {
        if (settings.ModelProfilesSeeded)
        {
            return;
        }

        var existingSlugs = await dbContext
            .LlmModelProfiles.Select(p => p.Slug.ToLower())
            .ToListAsync(cancellationToken);

        var addedAny = false;
        var maxSortOrder =
            await dbContext.LlmModelProfiles.MaxAsync(p => (int?)p.SortOrder, cancellationToken)
            ?? -1;

        for (var i = 0; i < LlmModelProfiles.All.Count; i++)
        {
            var p = LlmModelProfiles.All[i];
            if (!existingSlugs.Contains(p.Id.ToLower()))
            {
                dbContext.LlmModelProfiles.Add(
                    new LlmModelProfile
                    {
                        Id = Guid.NewGuid(),
                        Slug = p.Id,
                        DisplayName = p.DisplayName,
                        ModelTag = p.ModelTag,
                        MatchPrefixesJson = JsonSerializer.Serialize(p.MatchPrefixes),
                        Summary = p.Summary,
                        EnableThinking = p.EnableThinking,
                        Temperature = p.Temperature,
                        TopP = p.TopP,
                        TopK = p.TopK,
                        ContextWindowTokens = p.ContextWindowTokens,
                        RequestTimeoutSeconds = p.RequestTimeoutSeconds,
                        RecommendedAsRefineModel = p.RecommendedAsRefineModel,
                        NotesJson = JsonSerializer.Serialize(p.Notes),
                        SortOrder = maxSortOrder + 1 + i,
                        IsBuiltInDefault = true,
                        CreatedBy = "System",
                        ModifiedBy = "System",
                    }
                );
                addedAny = true;
            }
        }

        if (!settings.ModelProfilesSeeded || addedAny)
        {
            settings.ModelProfilesSeeded = true;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<LlmModelProfile?> FindProfileAsync(
        string id,
        CancellationToken cancellationToken
    )
    {
        var trimmed = id.Trim();
        if (Guid.TryParse(trimmed, out var guid))
        {
            var byGuid = await dbContext.LlmModelProfiles.FirstOrDefaultAsync(
                p => p.Id == guid,
                cancellationToken
            );
            if (byGuid is not null)
            {
                return byGuid;
            }
        }

        return await dbContext.LlmModelProfiles.FirstOrDefaultAsync(
            p => p.Slug.ToLower() == trimmed.ToLower(),
            cancellationToken
        );
    }

    private async Task<string> GenerateSlugAsync(
        string displayName,
        CancellationToken cancellationToken
    )
    {
        var raw = displayName.Trim().ToLowerInvariant();
        var slug = Regex.Replace(raw, @"[^a-z0-9]+", "-").Trim('-');
        if (string.IsNullOrEmpty(slug))
        {
            slug = "custom-profile";
        }

        var candidate = slug;
        var counter = 2;
        while (
            await dbContext.LlmModelProfiles.AnyAsync(p => p.Slug == candidate, cancellationToken)
        )
        {
            candidate = $"{slug}-{counter}";
            counter++;
        }

        return candidate;
    }

    private static void ValidateProfileRequest(UpsertLlmModelProfileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            throw new DomainValidationException("DisplayName is required.");
        }

        if (string.IsNullOrWhiteSpace(request.ModelTag))
        {
            throw new DomainValidationException("ModelTag is required.");
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

    private static LlmModelProfileDto ToDto(LlmModelProfile p)
    {
        IReadOnlyList<string> matchPrefixes;
        try
        {
            matchPrefixes = JsonSerializer.Deserialize<List<string>>(p.MatchPrefixesJson) ?? [];
        }
        catch
        {
            matchPrefixes = [];
        }

        IReadOnlyList<string> notes;
        try
        {
            notes = JsonSerializer.Deserialize<List<string>>(p.NotesJson) ?? [];
        }
        catch
        {
            notes = [];
        }

        var builtIn = LlmModelProfiles.All.FirstOrDefault(b => b.Id == p.Slug);

        return new LlmModelProfileDto(
            Id: p.Slug,
            DisplayName: p.DisplayName,
            ModelTag: p.ModelTag,
            MatchPrefixes: matchPrefixes,
            Summary: p.Summary,
            EnableThinking: p.EnableThinking,
            Temperature: p.Temperature,
            TopP: p.TopP,
            TopK: p.TopK,
            ContextWindowTokens: p.ContextWindowTokens,
            RequestTimeoutSeconds: p.RequestTimeoutSeconds,
            RecommendedAsRefineModel: p.RecommendedAsRefineModel,
            Notes: notes,
            IsBuiltInDefault: p.IsBuiltInDefault,
            DailyQuotaResetTimezone: builtIn?.DailyQuotaResetTimezone,
            DailyQuotaResetTime: builtIn?.DailyQuotaResetTime
        );
    }
}

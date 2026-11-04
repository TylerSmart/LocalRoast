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
        var entity = await dbContext
            .HouseholdLlmSettings.AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.SingletonKey == HouseholdLlmSettings.DefaultSingletonKey,
                cancellationToken
            );

        if (entity is not null)
        {
            return new LlmSettingsDto(
                Provider: entity.Provider,
                BaseUrl: entity.BaseUrl,
                HasApiKey: !string.IsNullOrWhiteSpace(entity.ApiKey),
                ApiKeyMasked: MaskApiKey(entity.ApiKey),
                TextModel: entity.TextModel,
                VisionModel: entity.VisionModel,
                OllamaKeepAlive: entity.OllamaKeepAlive,
                RequestTimeoutSeconds: entity.RequestTimeoutSeconds,
                AutoPublishHighConfidenceText: entity.AutoPublishHighConfidenceText,
                EnableThinking: entity.EnableThinking,
                MaxIngestionRetries: entity.MaxIngestionRetries,
                EnableIngestion: entity.EnableIngestion,
                ModifiedAt: entity.ModifiedAt
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
            ModifiedAt: null
        );
    }

    public async Task<EffectiveLlmConnectionConfig> GetEffectiveConfigAsync(
        CancellationToken cancellationToken = default
    )
    {
        var entity = await dbContext
            .HouseholdLlmSettings.AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.SingletonKey == HouseholdLlmSettings.DefaultSingletonKey,
                cancellationToken
            );

        if (entity is not null)
        {
            return new EffectiveLlmConnectionConfig(
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
                EnableIngestion: entity.EnableIngestion
            );
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
            EnableIngestion: opt.EnableIngestion
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
        var wasIngestionEnabled = entity.EnableIngestion;
        entity.MaxIngestionRetries = request.MaxIngestionRetries;
        entity.EnableIngestion = request.EnableIngestion;

        await dbContext.SaveChangesAsync(cancellationToken);

        if (!request.EnableIngestion && wasIngestionEnabled)
        {
            queueSignal.CancelActiveExecution();
            var workingJobs = await dbContext
                .IngestionJobs.Where(j =>
                    j.Status == IngestionJobStatus.Preprocessing
                    || j.Status == IngestionJobStatus.Extracting
                )
                .ToListAsync(cancellationToken);

            foreach (var job in workingJobs)
            {
                job.Status = IngestionJobStatus.Queued;
                job.NextRetryAt = null;
                job.StatusDetail = "Paused. Queued to resume when ingestion is enabled.";
            }

            if (workingJobs.Count > 0)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
        else if (request.EnableIngestion && !wasIngestionEnabled)
        {
            queueSignal.NotifyWorkAvailable();
        }

        // If a model is configured, wake any waiting jobs
        if (!string.IsNullOrWhiteSpace(entity.TextModel))
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

        return await GetSettingsAsync(cancellationToken);
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

    public async Task<LlmSettingsDto> SetIngestionEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default
    )
    {
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

        var wasEnabled = entity.EnableIngestion;
        entity.EnableIngestion = enabled;
        await dbContext.SaveChangesAsync(cancellationToken);

        if (!enabled)
        {
            queueSignal.CancelActiveExecution();

            var workingJobs = await dbContext
                .IngestionJobs.Where(j =>
                    j.Status == IngestionJobStatus.Preprocessing
                    || j.Status == IngestionJobStatus.Extracting
                )
                .ToListAsync(cancellationToken);

            foreach (var job in workingJobs)
            {
                job.Status = IngestionJobStatus.Queued;
                job.NextRetryAt = null;
                job.StatusDetail = "Paused. Queued to resume when ingestion is enabled.";
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

    private static void ValidateUrl(string? url)
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

    private static string? MaskApiKey(string? apiKey)
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
}

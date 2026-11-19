using App.Core.Exceptions;
using App.Core.Ingestion;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace App.Data.Services;

public sealed class LlmConnectionService(
    AppDbContext dbContext,
    ILlmExtractionClient llmClient,
    IIngestionQueueSignal queueSignal
) : ILlmConnectionService
{
    private const int ProbeTimeoutSeconds = 15;

    public async Task<IReadOnlyList<LlmConnectionDto>> GetConnectionsAsync(
        CancellationToken cancellationToken = default
    )
    {
        var connections = await dbContext
            .LlmConnections.AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new { Connection = c, Count = c.ModelConfigurations.Count })
            .ToListAsync(cancellationToken);

        return connections.Select(c => ToDto(c.Connection, c.Count)).ToList();
    }

    public async Task<LlmConnectionDto> CreateConnectionAsync(
        UpsertLlmConnectionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ValidateConnectionRequest(request);
        await EnsureUniqueConnectionNameAsync(request.Name, null, cancellationToken);

        var entity = new LlmConnection();
        ApplyConnectionRequest(entity, request);
        dbContext.LlmConnections.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        return ToDto(entity, 0);
    }

    public async Task<LlmConnectionDto> UpdateConnectionAsync(
        Guid id,
        UpsertLlmConnectionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ValidateConnectionRequest(request);
        var entity =
            await dbContext.LlmConnections.FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new NotFoundException($"LLM connection '{id}' not found.");
        await EnsureUniqueConnectionNameAsync(request.Name, id, cancellationToken);

        ApplyConnectionRequest(entity, request);
        await dbContext.SaveChangesAsync(cancellationToken);

        var count = await dbContext.LlmModelConfigurations.CountAsync(
            c => c.ConnectionId == id,
            cancellationToken
        );
        if (count > 0)
        {
            await LlmSettingsService.WakeWaitingJobsAsync(
                dbContext,
                queueSignal,
                cancellationToken
            );
        }

        return ToDto(entity, count);
    }

    public async Task DeleteConnectionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity =
            await dbContext
                .LlmConnections.Include(c => c.ModelConfigurations)
                    .ThenInclude(m => m.Profiles)
                .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new NotFoundException($"LLM connection '{id}' not found.");

        var profiles = entity.ModelConfigurations.SelectMany(m => m.Profiles).ToList();
        var preemptBackground = await LlmSettingsService.UnassignWorkflowsForProfilesAsync(
            dbContext,
            profiles.Select(p => p.Id).ToList(),
            cancellationToken
        );

        if (profiles.Count > 0)
        {
            dbContext.LlmModelProfiles.RemoveRange(profiles);
        }
        if (entity.ModelConfigurations.Count > 0)
        {
            dbContext.LlmModelConfigurations.RemoveRange(entity.ModelConfigurations);
        }
        dbContext.LlmConnections.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (preemptBackground)
        {
            queueSignal.PreemptBackgroundWork();
        }
    }

    public async Task<LlmConnectionTestResultDto> TestConnectionAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        var entity =
            await dbContext
                .LlmConnections.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new NotFoundException($"LLM connection '{id}' not found.");

        var config = LlmSettingsService.WithConnection(
            new EffectiveLlmConnectionConfig(
                Provider: entity.Provider,
                BaseUrl: entity.BaseUrl,
                ApiKey: entity.ApiKey,
                TextModel: null,
                VisionModel: null,
                OllamaKeepAlive: entity.OllamaKeepAlive,
                RequestTimeoutSeconds: ProbeTimeoutSeconds,
                AutoPublishHighConfidenceText: false
            ),
            entity
        );

        return await llmClient.ProbeAndListModelsAsync(
            config,
            ProbeTimeoutSeconds,
            cancellationToken
        );
    }

    public async Task<IReadOnlyList<LlmModelConfigurationDto>> GetModelConfigurationsAsync(
        CancellationToken cancellationToken = default
    )
    {
        var configurations = await dbContext
            .LlmModelConfigurations.AsNoTracking()
            .Include(c => c.Connection)
            .Include(c => c.Profiles)
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken);
        var settings = await GetSettingsAssignmentsAsync(cancellationToken);

        return configurations.Select(c => ToDto(c, settings)).ToList();
    }

    public async Task<LlmModelConfigurationDto> CreateModelConfigurationAsync(
        UpsertLlmModelConfigurationRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var connection = await ValidateModelConfigurationRequestAsync(
            request,
            null,
            cancellationToken
        );

        var entity = new LlmModelConfiguration { Connection = connection };
        ApplyModelConfigurationRequest(entity, request);

        var defaultProfile = new LlmModelProfile
        {
            Id = Guid.NewGuid(),
            ModelConfigurationId = entity.Id,
            ModelConfiguration = entity,
            DisplayName = LlmProfileDefaults.DefaultDisplayName,
            Summary = string.Empty,
            EnableThinking = false,
            Temperature = null,
            TopP = null,
            TopK = null,
            ContextWindowTokens = LlmProfileDefaults.DefaultContextWindowTokens,
            RequestTimeoutSeconds = LlmProfileDefaults.DefaultRequestTimeoutSeconds,
            NotesJson = "[]",
            IsDefault = true,
            CreatedBy = "System",
            ModifiedBy = "System",
        };
        entity.Profiles.Add(defaultProfile);

        dbContext.LlmModelConfigurations.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        return ToDto(entity, await GetSettingsAssignmentsAsync(cancellationToken));
    }

    public async Task<LlmModelConfigurationDto> UpdateModelConfigurationAsync(
        Guid id,
        UpsertLlmModelConfigurationRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var entity =
            await dbContext
                .LlmModelConfigurations.Include(c => c.Profiles)
                .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new NotFoundException($"Model configuration '{id}' not found.");
        var connection = await ValidateModelConfigurationRequestAsync(
            request,
            id,
            cancellationToken
        );

        entity.Connection = connection;
        entity.ConnectionId = connection.Id;
        ApplyModelConfigurationRequest(entity, request);
        await dbContext.SaveChangesAsync(cancellationToken);

        var settings = await GetSettingsAssignmentsAsync(cancellationToken);
        var dto = ToDto(entity, settings);
        if (dto.AssignedWorkflows.Count > 0)
        {
            await LlmSettingsService.WakeWaitingJobsAsync(
                dbContext,
                queueSignal,
                cancellationToken
            );
        }

        return dto;
    }

    public async Task DeleteModelConfigurationAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        var entity =
            await dbContext
                .LlmModelConfigurations.Include(c => c.Profiles)
                .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new NotFoundException($"Model configuration '{id}' not found.");

        var preemptBackground = await LlmSettingsService.UnassignWorkflowsForProfilesAsync(
            dbContext,
            entity.Profiles.Select(p => p.Id).ToList(),
            cancellationToken
        );

        if (entity.Profiles.Count > 0)
        {
            dbContext.LlmModelProfiles.RemoveRange(entity.Profiles);
        }
        dbContext.LlmModelConfigurations.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (preemptBackground)
        {
            queueSignal.PreemptBackgroundWork();
        }
    }

    private static void ValidateConnectionRequest(UpsertLlmConnectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new DomainValidationException("Name is required.");
        }

        LlmSettingsService.ValidateUrl(request.BaseUrl);

        if (request.MaxConcurrentRequests is < 1 or > 5)
        {
            throw new DomainValidationException("MaxConcurrentRequests must be between 1 and 5.");
        }

        if (
            !string.IsNullOrWhiteSpace(request.DailyQuotaResetTime)
            && !TimeOnly.TryParseExact(request.DailyQuotaResetTime.Trim(), "HH:mm", out _)
        )
        {
            throw new DomainValidationException("DailyQuotaResetTime must use HH:mm format.");
        }
    }

    private async Task EnsureUniqueConnectionNameAsync(
        string name,
        Guid? excludeId,
        CancellationToken cancellationToken
    )
    {
        var normalized = name.Trim().ToLower();
        if (
            await dbContext.LlmConnections.AnyAsync(
                c => c.Name.ToLower() == normalized && c.Id != excludeId,
                cancellationToken
            )
        )
        {
            throw new ConflictException($"A connection named '{name.Trim()}' already exists.");
        }
    }

    private static void ApplyConnectionRequest(
        LlmConnection entity,
        UpsertLlmConnectionRequest request
    )
    {
        entity.Name = request.Name.Trim();
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
        entity.OllamaKeepAlive = string.IsNullOrWhiteSpace(request.OllamaKeepAlive)
            ? "5m"
            : request.OllamaKeepAlive.Trim();
        entity.OllamaCpuOnly = request.OllamaCpuOnly;
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
    }

    private async Task<LlmConnection> ValidateModelConfigurationRequestAsync(
        UpsertLlmModelConfigurationRequest request,
        Guid? excludeId,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new DomainValidationException("Name is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Model))
        {
            throw new DomainValidationException("Model is required.");
        }

        if (request.OllamaGpuLayers is < 0)
        {
            throw new DomainValidationException(
                "OllamaGpuLayers must be a non-negative whole number."
            );
        }

        if (request.MaxConcurrentRequests is < 1 or > 5)
        {
            throw new DomainValidationException("MaxConcurrentRequests must be between 1 and 5.");
        }

        if (request.RequestsPerMinute is < 1 or > 1000)
        {
            throw new DomainValidationException("RequestsPerMinute must be between 1 and 1000.");
        }

        if (request.DailyRequestQuota is < 1 or > 100000)
        {
            throw new DomainValidationException("DailyRequestQuota must be between 1 and 100000.");
        }

        if (
            !string.IsNullOrWhiteSpace(request.DailyQuotaResetTime)
            && !TimeOnly.TryParseExact(request.DailyQuotaResetTime.Trim(), "HH:mm", out _)
        )
        {
            throw new DomainValidationException("DailyQuotaResetTime must use HH:mm format.");
        }

        var connection =
            await dbContext.LlmConnections.FirstOrDefaultAsync(
                c => c.Id == request.ConnectionId,
                cancellationToken
            ) ?? throw new DomainValidationException("Choose a saved connection for this model.");

        var normalizedName = request.Name.Trim().ToLower();
        if (
            await dbContext.LlmModelConfigurations.AnyAsync(
                c => c.Name.ToLower() == normalizedName && c.Id != excludeId,
                cancellationToken
            )
        )
        {
            throw new ConflictException(
                $"A model configuration named '{request.Name.Trim()}' already exists."
            );
        }

        var normalizedModel = request.Model.Trim().ToLower();
        if (
            await dbContext.LlmModelConfigurations.AnyAsync(
                c =>
                    c.ConnectionId == request.ConnectionId
                    && c.Model.ToLower() == normalizedModel
                    && c.Id != excludeId,
                cancellationToken
            )
        )
        {
            throw new ConflictException(
                $"Model '{request.Model.Trim()}' is already configured on '{connection.Name}'. Create additional profiles for this model instead of duplicating it.",
                "Duplicate model on connection"
            );
        }

        return connection;
    }

    private static void ApplyModelConfigurationRequest(
        LlmModelConfiguration entity,
        UpsertLlmModelConfigurationRequest request
    )
    {
        entity.Name = request.Name.Trim();
        entity.Model = request.Model.Trim();
        entity.OllamaGpuLayers = request.OllamaGpuLayers;
        entity.MaxConcurrentRequests = request.MaxConcurrentRequests;
        entity.RequestsPerMinute = request.RequestsPerMinute;
        entity.DailyRequestQuota = request.DailyRequestQuota;
        entity.DailyQuotaResetTimezone = string.IsNullOrWhiteSpace(request.DailyQuotaResetTimezone)
            ? null
            : request.DailyQuotaResetTimezone.Trim();
        entity.DailyQuotaResetTime = string.IsNullOrWhiteSpace(request.DailyQuotaResetTime)
            ? null
            : request.DailyQuotaResetTime.Trim();
    }

    private Task<HouseholdLlmSettings?> GetSettingsAssignmentsAsync(
        CancellationToken cancellationToken
    ) =>
        dbContext
            .HouseholdLlmSettings.AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.SingletonKey == HouseholdLlmSettings.DefaultSingletonKey,
                cancellationToken
            );

    private static List<LlmWorkflow> GetAssignedWorkflows(
        IEnumerable<Guid> profileIds,
        HouseholdLlmSettings? settings
    )
    {
        var workflows = new List<LlmWorkflow>();
        if (settings is null)
        {
            return workflows;
        }
        var idSet = profileIds.ToHashSet();
        if (settings.TextModelProfileId is { } textId && idSet.Contains(textId))
        {
            workflows.Add(LlmWorkflow.Text);
        }
        if (settings.VisionModelProfileId is { } visionId && idSet.Contains(visionId))
        {
            workflows.Add(LlmWorkflow.Vision);
        }
        if (settings.RefineModelProfileId is { } refineId && idSet.Contains(refineId))
        {
            workflows.Add(LlmWorkflow.Refinement);
        }
        if (settings.EmbeddingModelProfileId is { } embedId && idSet.Contains(embedId))
        {
            workflows.Add(LlmWorkflow.Embedding);
        }
        return workflows;
    }

    private static LlmConnectionDto ToDto(LlmConnection c, int modelConfigurationCount) =>
        new(
            Id: c.Id,
            Name: c.Name,
            Provider: c.Provider,
            BaseUrl: c.BaseUrl,
            HasApiKey: !string.IsNullOrWhiteSpace(c.ApiKey),
            ApiKeyMasked: LlmSettingsService.MaskApiKey(c.ApiKey),
            OllamaKeepAlive: c.OllamaKeepAlive,
            OllamaCpuOnly: c.OllamaCpuOnly,
            EnableRateLimitSafeguards: c.EnableRateLimitSafeguards,
            MaxConcurrentRequests: c.MaxConcurrentRequests,
            RequestsPerMinute: c.RequestsPerMinute,
            DailyRequestQuota: c.DailyRequestQuota,
            DailyQuotaResetTimezone: c.DailyQuotaResetTimezone,
            DailyQuotaResetTime: c.DailyQuotaResetTime,
            ModelConfigurationCount: modelConfigurationCount,
            ModifiedAt: c.ModifiedAt
        );

    private static LlmModelConfigurationDto ToDto(
        LlmModelConfiguration c,
        HouseholdLlmSettings? settings
    )
    {
        var defaultProfileId =
            c.Profiles.FirstOrDefault(p => p.IsDefault)?.Id
            ?? c.Profiles.FirstOrDefault()?.Id
            ?? Guid.Empty;

        return new LlmModelConfigurationDto(
            Id: c.Id,
            Name: c.Name,
            ConnectionId: c.Connection?.Id ?? c.ConnectionId,
            ConnectionName: c.Connection?.Name ?? string.Empty,
            Provider: c.Connection?.Provider ?? LlmProvider.Ollama,
            Model: c.Model,
            DefaultProfileId: defaultProfileId,
            ProfileCount: c.Profiles.Count,
            AssignedWorkflows: GetAssignedWorkflows(c.Profiles.Select(p => p.Id), settings),
            ModifiedAt: c.ModifiedAt,
            OllamaGpuLayers: c.OllamaGpuLayers,
            MaxConcurrentRequests: c.MaxConcurrentRequests,
            RequestsPerMinute: c.RequestsPerMinute,
            DailyRequestQuota: c.DailyRequestQuota,
            DailyQuotaResetTimezone: c.DailyQuotaResetTimezone,
            DailyQuotaResetTime: c.DailyQuotaResetTime
        );
    }
}

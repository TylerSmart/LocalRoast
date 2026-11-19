namespace App.Core.Ingestion;

public interface ILlmSettingsService
{
    Task<LlmSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task<EffectiveLlmConnectionConfig> GetEffectiveConfigAsync(
        CancellationToken cancellationToken = default
    );

    Task<LlmSettingsDto> UpdateSettingsAsync(
        UpdateLlmSettingsRequest request,
        CancellationToken cancellationToken = default
    );

    /// <summary>Assigns saved model profiles to the text, vision, refinement, and embedding workflows.</summary>
    Task<LlmSettingsDto> UpdateWorkflowsAsync(
        UpdateLlmWorkflowsRequest request,
        CancellationToken cancellationToken = default
    );

    Task<LlmConnectionTestResultDto> TestConnectionAsync(
        TestLlmConnectionRequest request,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<LlmModelProfileDto>> GetModelProfilesAsync(
        CancellationToken cancellationToken = default
    );

    Task<LlmModelProfileDto> CreateModelProfileAsync(
        UpsertLlmModelProfileRequest request,
        CancellationToken cancellationToken = default
    );

    Task<LlmModelProfileDto> UpdateModelProfileAsync(
        Guid id,
        UpsertLlmModelProfileRequest request,
        CancellationToken cancellationToken = default
    );

    Task DeleteModelProfileAsync(Guid id, CancellationToken cancellationToken = default);

    Task<LlmSettingsDto> SetIngestionEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default
    );

    Task<LlmSettingsDto> SetIngestionEnabledAsync(
        bool enabled,
        DateTimeOffset? autoResumeAt,
        string? pauseReason,
        CancellationToken cancellationToken = default
    );
}

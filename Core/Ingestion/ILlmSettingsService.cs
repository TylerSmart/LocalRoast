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
        string id,
        UpsertLlmModelProfileRequest request,
        CancellationToken cancellationToken = default
    );

    Task DeleteModelProfileAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LlmModelProfileDto>> RestoreDefaultModelProfilesAsync(
        CancellationToken cancellationToken = default
    );

    Task<LlmSettingsDto> SetIngestionEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default
    );
}

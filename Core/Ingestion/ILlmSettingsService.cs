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
}

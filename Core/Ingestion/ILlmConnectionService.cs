namespace App.Core.Ingestion;

public interface ILlmConnectionService
{
    Task<IReadOnlyList<LlmConnectionDto>> GetConnectionsAsync(
        CancellationToken cancellationToken = default
    );

    Task<LlmConnectionDto> CreateConnectionAsync(
        UpsertLlmConnectionRequest request,
        CancellationToken cancellationToken = default
    );

    Task<LlmConnectionDto> UpdateConnectionAsync(
        Guid id,
        UpsertLlmConnectionRequest request,
        CancellationToken cancellationToken = default
    );

    Task DeleteConnectionAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Probes the saved connection and lists the models it serves.</summary>
    Task<LlmConnectionTestResultDto> TestConnectionAsync(
        Guid id,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<LlmModelConfigurationDto>> GetModelConfigurationsAsync(
        CancellationToken cancellationToken = default
    );

    Task<LlmModelConfigurationDto> CreateModelConfigurationAsync(
        UpsertLlmModelConfigurationRequest request,
        CancellationToken cancellationToken = default
    );

    Task<LlmModelConfigurationDto> UpdateModelConfigurationAsync(
        Guid id,
        UpsertLlmModelConfigurationRequest request,
        CancellationToken cancellationToken = default
    );

    Task DeleteModelConfigurationAsync(Guid id, CancellationToken cancellationToken = default);
}

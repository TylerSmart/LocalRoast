namespace App.Core.Ingestion;

public record LlmExtractionContext(
    string SourceContent,
    IReadOnlyList<string> AvailableAisleNames,
    IReadOnlyList<string> AvailableTagNames
);

public record LlmVisionImageInput(int PageNumber, string MimeType, string Base64Data);

public record LlmVisionExtractionContext(
    IReadOnlyList<LlmVisionImageInput> Images,
    string? SupplementalHintText,
    IReadOnlyList<string> AvailableAisleNames,
    IReadOnlyList<string> AvailableTagNames
);

public interface ILlmExtractionClient
{
    Task<LlmConnectionTestResultDto> ProbeAndListModelsAsync(
        EffectiveLlmConnectionConfig config,
        int timeoutSeconds = 15,
        CancellationToken cancellationToken = default
    );

    Task<LlmExtractedRecipePayload> ExtractRecipeFromTextAsync(
        EffectiveLlmConnectionConfig config,
        LlmExtractionContext context,
        CancellationToken cancellationToken = default
    );

    Task<LlmExtractedRecipePayload> ExtractRecipeFromImagesAsync(
        EffectiveLlmConnectionConfig config,
        LlmVisionExtractionContext context,
        CancellationToken cancellationToken = default
    );
}

namespace App.Core.Ingestion;

public sealed record IngestionRefineDraftRequest(
    string SourceText,
    IngestionExtractedDraftDto Draft,
    EffectiveLlmConnectionConfig Config,
    Guid? ExcludeRecipeId = null
);

public sealed record IngestionRefineDraftResult(
    IngestionExtractedDraftDto Draft,
    string RefinedModel,
    IReadOnlyList<string> RefinementNotes,
    bool MadeChanges
);

public interface IIngestionRecipeRefiner
{
    Task<IngestionRefineDraftResult> RefineDraftAsync(
        IngestionRefineDraftRequest request,
        CancellationToken cancellationToken = default
    );
}

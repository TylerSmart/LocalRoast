using App.Core.Recipes;

namespace App.Core.Ingestion;

public interface IIngestionService
{
    Task<IngestionQueueSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IngestionJobSummaryDto>> GetJobsAsync(
        IngestionJobStatus? status = null,
        CancellationToken cancellationToken = default
    );

    Task<IngestionJobDetailDto?> GetJobByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<IngestionJobSummaryDto>> EnqueueWebUrlsAsync(
        EnqueueWebUrlIngestionRequest request,
        CancellationToken cancellationToken = default
    );

    Task<IngestionJobSummaryDto> EnqueueRawTextAsync(
        EnqueueRawTextIngestionRequest request,
        CancellationToken cancellationToken = default
    );

    Task<IngestionJobSummaryDto> EnqueueImagesAsync(
        IReadOnlyList<IngestionUploadFileStream> files,
        EnqueueImageIngestionMetadata metadata,
        CancellationToken cancellationToken = default
    );

    Task<IngestionJobSummaryDto> EnqueuePdfAsync(
        IngestionUploadFileStream file,
        EnqueuePdfIngestionMetadata metadata,
        CancellationToken cancellationToken = default
    );

    Task<IngestionJobDetailDto?> PromoteSourcePageToCoverAsync(
        Guid id,
        int pageNumber,
        CancellationToken cancellationToken = default
    );

    Task<IngestionJobDetailDto?> SaveDraftAsync(
        Guid id,
        IngestionExtractedDraftDto draft,
        CancellationToken cancellationToken = default
    );

    Task<RecipeDetailDto> ApproveAndPublishAsync(
        Guid id,
        IngestionExtractedDraftDto draft,
        CancellationToken cancellationToken = default
    );

    Task<IngestionJobSummaryDto?> RetryJobAsync(
        Guid id,
        CancellationToken cancellationToken = default
    );

    Task<RetryWaitingJobsResultDto> RetryAllWaitingJobsAsync(
        CancellationToken cancellationToken = default
    );

    Task<bool> DiscardJobAsync(Guid id, CancellationToken cancellationToken = default);
}

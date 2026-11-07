using System.Text.Json;
using App.Core.Exceptions;
using App.Core.Ingestion;
using App.Core.Media;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace App.Data.Services;

public sealed class IngestionReviewService(
    AppDbContext dbContext,
    ILlmSettingsService llmSettingsService,
    ILlmExtractionClient llmClient,
    IMediaStorageService mediaStorageService
) : IIngestionReviewService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<IngestionReviewDiagnosticsDto> GetDiagnosticsAsync(
        Guid id,
        IngestionExtractedDraftDto draft,
        CancellationToken cancellationToken = default
    )
    {
        var job = await FindJobAsync(id, cancellationToken);
        return Diagnose(draft, GetSource(job));
    }

    public async Task<IngestionRepairPreviewDto> PreviewRepairAsync(
        Guid id,
        IngestionRepairPreviewRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var job = await FindJobAsync(id, cancellationToken);
        if (job.Status != IngestionJobStatus.ReadyForReview)
        {
            throw new ConflictException("Only a recipe awaiting review can be repaired.");
        }

        var source = GetSource(job);
        var before = Diagnose(request.Draft, source);
        var images = await GetSourceImagesAsync(job, cancellationToken);
        var config = await llmSettingsService.GetEffectiveConfigAsync(cancellationToken);
        var result = await llmClient.RepairRecipeDraftAsync(
            config,
            new LlmRecipeRepairContext(
                source,
                images,
                IngestionDraftRepairMapper.ToPayload(request.Draft),
                request.Hint
            ),
            cancellationToken
        );

        var currentJob = await dbContext
            .IngestionJobs.AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new { item.Status, item.ModifiedAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (
            currentJob is null
            || currentJob.Status != IngestionJobStatus.ReadyForReview
            || currentJob.ModifiedAt != job.ModifiedAt
        )
        {
            throw new ConflictException(
                "The ingestion job changed while the AI was working. Reload it before repairing."
            );
        }

        var foods = await dbContext
            .FoodItems.AsNoTracking()
            .Select(food => new CatalogFoodLookupItem(
                food.Id,
                food.Name,
                food.NormalizedName,
                food.IsPantryStaple,
                food.Aliases.Select(alias => alias.NormalizedAlias).ToList(),
                food.Category
            ))
            .ToListAsync(cancellationToken);
        var proposed = IngestionDraftRepairMapper.Merge(request.Draft, result.Draft, foods, source);
        return new IngestionRepairPreviewDto(
            proposed,
            before,
            Diagnose(proposed, source),
            result.Notices
        );
    }

    private async Task<IngestionJob> FindJobAsync(Guid id, CancellationToken cancellationToken) =>
        await dbContext
            .IngestionJobs.AsNoTracking()
            .FirstOrDefaultAsync(job => job.Id == id, cancellationToken)
        ?? throw new NotFoundException("Ingestion job not found.");

    private static string? GetSource(IngestionJob job) =>
        !string.IsNullOrWhiteSpace(job.StructuredSourceText) ? job.StructuredSourceText
        : !string.IsNullOrWhiteSpace(job.NormalizedSourceText) ? job.NormalizedSourceText
        : job.RawText;

    private static IngestionReviewDiagnosticsDto Diagnose(
        IngestionExtractedDraftDto draft,
        string? source
    )
    {
        var payload = IngestionDraftRepairMapper.ToPayload(draft);
        return new IngestionReviewDiagnosticsDto(
            RecipeExtractionDiagnostics.GetReviewIssues(payload, source),
            RecipeExtractionDiagnostics.GetQualityWarnings(payload)
        );
    }

    private async Task<IReadOnlyList<LlmVisionImageInput>> GetSourceImagesAsync(
        IngestionJob job,
        CancellationToken cancellationToken
    )
    {
        IReadOnlyList<IngestionSourceFileDto> sourceFiles = [];
        var requiresImages = job.Channel == IngestionChannel.Images;
        try
        {
            if (job.Channel == IngestionChannel.Images)
            {
                sourceFiles = string.IsNullOrWhiteSpace(job.SourceFilesJson)
                    ? []
                    : JsonSerializer.Deserialize<List<IngestionSourceFileDto>>(
                        job.SourceFilesJson,
                        JsonOptions
                    ) ?? [];
            }
            else if (
                job.Channel == IngestionChannel.Pdf
                && !string.IsNullOrWhiteSpace(job.PdfInspectionJson)
            )
            {
                var inspection = JsonSerializer.Deserialize<PdfInspectionSummaryDto>(
                    job.PdfInspectionJson,
                    JsonOptions
                );
                if (inspection?.ExtractionMode == PdfExtractionMode.VisionRaster)
                {
                    requiresImages = true;
                    sourceFiles = inspection.RenderedPageImages ?? [];
                }
            }
        }
        catch (JsonException)
        {
            throw new DomainValidationException(
                "The stored source images could not be read. The review draft has not been changed."
            );
        }

        if (
            (requiresImages && sourceFiles.Count == 0)
            || (
                job.Channel == IngestionChannel.Pdf
                && sourceFiles.Count == 0
                && string.IsNullOrWhiteSpace(GetSource(job))
            )
            || sourceFiles.Any(file => file is null || string.IsNullOrWhiteSpace(file.UrlPath))
        )
        {
            throw new DomainValidationException(
                "The original source images are unavailable. The review draft has not been changed."
            );
        }
        var images = new List<LlmVisionImageInput>();
        foreach (var file in sourceFiles)
        {
            byte[] jpeg;
            try
            {
                jpeg = await mediaStorageService.EncodeMediaImageAsVisionJpegAsync(
                    file.UrlPath,
                    1568,
                    cancellationToken
                );
            }
            catch (NotFoundException)
            {
                throw new DomainValidationException(
                    "An original source image is missing. The review draft has not been changed."
                );
            }
            images.Add(
                new LlmVisionImageInput(file.PageNumber, "image/jpeg", Convert.ToBase64String(jpeg))
            );
        }
        return images;
    }
}

namespace App.Core.Media;

public interface IMediaStorageService
{
    Task<UploadedRecipeCoverDto> SaveRecipeCoverAsync(
        Stream content,
        string originalFileName,
        long contentLength,
        CancellationToken cancellationToken = default
    );

    Task<UploadedMediaDto> SaveStepImageAsync(
        Stream content,
        string originalFileName,
        long contentLength,
        CancellationToken cancellationToken = default
    );

    Task DeleteMediaFileAsync(string? mediaUrlPath, CancellationToken cancellationToken = default);

    Task<SavedIngestionImageDto> SaveIngestionPageImageAsync(
        Stream content,
        string originalFileName,
        long contentLength,
        string jobToken,
        int pageNumber,
        bool generateThumbnail,
        CancellationToken cancellationToken = default
    );

    Task<SavedIngestionPdfDto> SaveIngestionPdfAsync(
        Stream content,
        string originalFileName,
        long contentLength,
        string jobToken,
        CancellationToken cancellationToken = default
    );

    Task<Stream> OpenMediaReadStreamAsync(
        string mediaUrlPath,
        CancellationToken cancellationToken = default
    );

    Task<byte[]> EncodeMediaImageAsVisionJpegAsync(
        string mediaUrlPath,
        int maxDimensionPx = 1568,
        CancellationToken cancellationToken = default
    );

    Task<UploadedRecipeCoverDto> PromoteIngestionImageToRecipeCoverAsync(
        string sourceMediaUrlPath,
        CancellationToken cancellationToken = default
    );

    Task<SavedRecipeAttachmentFileDto> PromoteIngestionFileToAttachmentAsync(
        string sourceMediaUrlPath,
        CancellationToken cancellationToken = default
    );

    Task<SavedRecipeAttachmentFileDto> SaveDirectRecipeAttachmentAsync(
        Stream content,
        string originalFileName,
        string? contentType,
        long contentLength,
        CancellationToken cancellationToken = default
    );
}

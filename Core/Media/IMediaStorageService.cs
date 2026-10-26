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
}

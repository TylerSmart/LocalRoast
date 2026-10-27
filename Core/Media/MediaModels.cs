namespace App.Core.Media;

public record UploadedRecipeCoverDto(string CoverImagePath, string ThumbnailImagePath);

public record UploadedMediaDto(string ImagePath);

public record SavedIngestionImageDto(
    string UrlPath,
    string? ThumbnailUrlPath,
    int Width,
    int Height,
    long FileSizeBytes
);

public record SavedIngestionPdfDto(string UrlPath, long FileSizeBytes);

public record SavedRecipeAttachmentFileDto(
    string MediaUrlPath,
    string ContentType,
    long FileSizeBytes
);

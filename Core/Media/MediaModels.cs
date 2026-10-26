namespace App.Core.Media;

public record UploadedRecipeCoverDto(string CoverImagePath, string ThumbnailImagePath);

public record UploadedMediaDto(string ImagePath);

using App.Core.Exceptions;
using App.Core.Media;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace App.Infrastructure.Media;

public sealed class LocalFileMediaStorageService : IMediaStorageService
{
    private readonly MediaStorageOptions _options;
    private readonly string _uploadsRoot;
    private static readonly WebpEncoder WebpLossyEncoder = new()
    {
        Quality = 82,
        FileFormat = WebpFileFormatType.Lossy,
    };
    private static readonly WebpEncoder WebpScanEncoder = new()
    {
        Quality = 85,
        FileFormat = WebpFileFormatType.Lossy,
    };

    public LocalFileMediaStorageService(
        IOptions<MediaStorageOptions> options,
        IHostEnvironment hostEnvironment
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(hostEnvironment);

        _options = options.Value;
        _uploadsRoot = MediaStorageOptions.ResolveUploadsRoot(
            _options.UploadsPath,
            hostEnvironment.ContentRootPath
        );
        Directory.CreateDirectory(_uploadsRoot);
    }

    public async Task<UploadedRecipeCoverDto> SaveRecipeCoverAsync(
        Stream content,
        string originalFileName,
        long contentLength,
        CancellationToken cancellationToken = default
    )
    {
        ValidateImageContentLength(contentLength);

        using var image = await LoadAndNormalizeImageAsync(content, cancellationToken);

        var token = Guid.NewGuid().ToString("N");
        var recipesDir = Path.Combine(_uploadsRoot, "recipes");
        Directory.CreateDirectory(recipesDir);

        // Generate thumbnail first by cloning before modifying original image
        var thumbPath = Path.Combine(recipesDir, $"{token}-thumb.webp");
        using (
            var thumb = image.Clone(ctx =>
            {
                if (image.Width > 600 || image.Height > 450)
                {
                    ctx.Resize(
                        new ResizeOptions { Size = new Size(600, 450), Mode = ResizeMode.Max }
                    );
                }
            })
        )
        {
            await thumb.SaveAsync(thumbPath, WebpLossyEncoder, cancellationToken);
        }

        // Generate cover variant
        var coverPath = Path.Combine(recipesDir, $"{token}-cover.webp");
        if (image.Width > 1600 || image.Height > 1200)
        {
            image.Mutate(ctx =>
                ctx.Resize(new ResizeOptions { Size = new Size(1600, 1200), Mode = ResizeMode.Max })
            );
        }
        await image.SaveAsync(coverPath, WebpLossyEncoder, cancellationToken);

        return new UploadedRecipeCoverDto(
            $"/api/uploads/recipes/{token}-cover.webp",
            $"/api/uploads/recipes/{token}-thumb.webp"
        );
    }

    public async Task<UploadedMediaDto> SaveStepImageAsync(
        Stream content,
        string originalFileName,
        long contentLength,
        CancellationToken cancellationToken = default
    )
    {
        ValidateImageContentLength(contentLength);

        using var image = await LoadAndNormalizeImageAsync(content, cancellationToken);

        var token = Guid.NewGuid().ToString("N");
        var stepsDir = Path.Combine(_uploadsRoot, "steps");
        Directory.CreateDirectory(stepsDir);

        var stepPath = Path.Combine(stepsDir, $"{token}.webp");
        if (image.Width > 1200 || image.Height > 900)
        {
            image.Mutate(ctx =>
                ctx.Resize(new ResizeOptions { Size = new Size(1200, 900), Mode = ResizeMode.Max })
            );
        }
        await image.SaveAsync(stepPath, WebpLossyEncoder, cancellationToken);

        return new UploadedMediaDto($"/api/uploads/steps/{token}.webp");
    }

    public async Task<SavedIngestionImageDto> SaveIngestionPageImageAsync(
        Stream content,
        string originalFileName,
        long contentLength,
        string jobToken,
        int pageNumber,
        bool generateThumbnail,
        CancellationToken cancellationToken = default
    )
    {
        ValidateImageContentLength(contentLength);

        using var image = await LoadAndNormalizeImageAsync(content, cancellationToken);

        var ingestionDir = Path.Combine(_uploadsRoot, "ingestion");
        Directory.CreateDirectory(ingestionDir);

        string? thumbUrl = null;
        if (generateThumbnail)
        {
            var thumbPath = Path.Combine(ingestionDir, $"{jobToken}-thumb.webp");
            using var thumb = image.Clone(ctx =>
            {
                if (image.Width > 600 || image.Height > 450)
                {
                    ctx.Resize(
                        new ResizeOptions { Size = new Size(600, 450), Mode = ResizeMode.Max }
                    );
                }
            });
            await thumb.SaveAsync(thumbPath, WebpLossyEncoder, cancellationToken);
            thumbUrl = $"/api/uploads/ingestion/{jobToken}-thumb.webp";
        }

        if (image.Width > 2000 || image.Height > 2400)
        {
            image.Mutate(ctx =>
                ctx.Resize(new ResizeOptions { Size = new Size(2000, 2400), Mode = ResizeMode.Max })
            );
        }

        var pagePath = Path.Combine(ingestionDir, $"{jobToken}-p{pageNumber}.webp");
        await image.SaveAsync(pagePath, WebpScanEncoder, cancellationToken);

        var fileInfo = new FileInfo(pagePath);
        return new SavedIngestionImageDto(
            $"/api/uploads/ingestion/{jobToken}-p{pageNumber}.webp",
            thumbUrl,
            image.Width,
            image.Height,
            fileInfo.Length
        );
    }

    public async Task<SavedIngestionPdfDto> SaveIngestionPdfAsync(
        Stream content,
        string originalFileName,
        long contentLength,
        string jobToken,
        CancellationToken cancellationToken = default
    )
    {
        if (contentLength <= 0)
        {
            throw new DomainValidationException("Uploaded file cannot be empty.");
        }

        if (contentLength > _options.MaxPdfUploadBytes)
        {
            var maxMb = _options.MaxPdfUploadBytes / (1024 * 1024);
            throw new DomainValidationException(
                $"Uploaded PDF exceeds the maximum allowed size of {maxMb} MB."
            );
        }

        var ingestionDir = Path.Combine(_uploadsRoot, "ingestion");
        Directory.CreateDirectory(ingestionDir);

        var pdfPath = Path.Combine(ingestionDir, $"{jobToken}.pdf");

        // Validate PDF magic header: %PDF-
        await using (
            var fs = new FileStream(
                pdfPath,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None,
                4096,
                useAsync: true
            )
        )
        {
            await content.CopyToAsync(fs, cancellationToken);

            if (fs.Length < 5)
            {
                throw new DomainValidationException(
                    "Uploaded file is too small to be a valid PDF."
                );
            }

            fs.Position = 0;
            var header = new byte[5];
            var bytesRead = await fs.ReadAsync(header.AsMemory(0, 5), cancellationToken);
            if (
                bytesRead < 5
                || header[0] != 0x25
                || header[1] != 0x50
                || header[2] != 0x44
                || header[3] != 0x46
                || header[4] != 0x2D
            )
            {
                throw new DomainValidationException(
                    "Uploaded file is not a valid PDF document (missing %PDF- header)."
                );
            }
        }

        var fileInfo = new FileInfo(pdfPath);
        return new SavedIngestionPdfDto($"/api/uploads/ingestion/{jobToken}.pdf", fileInfo.Length);
    }

    public Task<Stream> OpenMediaReadStreamAsync(
        string mediaUrlPath,
        CancellationToken cancellationToken = default
    )
    {
        var fullPath = ResolvePhysicalPath(mediaUrlPath);
        if (!File.Exists(fullPath))
        {
            throw new NotFoundException($"Media file '{mediaUrlPath}' was not found on disk.");
        }

        Stream fs = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            useAsync: true
        );
        return Task.FromResult(fs);
    }

    public async Task<byte[]> EncodeMediaImageAsVisionJpegAsync(
        string mediaUrlPath,
        int maxDimensionPx = 1568,
        CancellationToken cancellationToken = default
    )
    {
        var fullPath = ResolvePhysicalPath(mediaUrlPath);
        if (!File.Exists(fullPath))
        {
            throw new NotFoundException($"Media file '{mediaUrlPath}' was not found on disk.");
        }

        using var image = await Image.LoadAsync(fullPath, cancellationToken);
        if (image.Width > maxDimensionPx || image.Height > maxDimensionPx)
        {
            image.Mutate(ctx =>
                ctx.Resize(
                    new ResizeOptions
                    {
                        Size = new Size(maxDimensionPx, maxDimensionPx),
                        Mode = ResizeMode.Max,
                    }
                )
            );
        }

        using var ms = new MemoryStream();
        await image.SaveAsJpegAsync(ms, new JpegEncoder { Quality = 85 }, cancellationToken);
        return ms.ToArray();
    }

    public async Task<UploadedRecipeCoverDto> PromoteIngestionImageToRecipeCoverAsync(
        string sourceMediaUrlPath,
        CancellationToken cancellationToken = default
    )
    {
        var fullPath = ResolvePhysicalPath(sourceMediaUrlPath);
        if (!File.Exists(fullPath))
        {
            throw new NotFoundException(
                $"Source image '{sourceMediaUrlPath}' was not found on disk."
            );
        }

        using var image = await Image.LoadAsync(fullPath, cancellationToken);

        var token = Guid.NewGuid().ToString("N");
        var recipesDir = Path.Combine(_uploadsRoot, "recipes");
        Directory.CreateDirectory(recipesDir);

        var thumbPath = Path.Combine(recipesDir, $"{token}-thumb.webp");
        using (
            var thumb = image.Clone(ctx =>
            {
                if (image.Width > 600 || image.Height > 450)
                {
                    ctx.Resize(
                        new ResizeOptions { Size = new Size(600, 450), Mode = ResizeMode.Max }
                    );
                }
            })
        )
        {
            await thumb.SaveAsync(thumbPath, WebpLossyEncoder, cancellationToken);
        }

        var coverPath = Path.Combine(recipesDir, $"{token}-cover.webp");
        if (image.Width > 1600 || image.Height > 1200)
        {
            image.Mutate(ctx =>
                ctx.Resize(new ResizeOptions { Size = new Size(1600, 1200), Mode = ResizeMode.Max })
            );
        }
        await image.SaveAsync(coverPath, WebpLossyEncoder, cancellationToken);

        return new UploadedRecipeCoverDto(
            $"/api/uploads/recipes/{token}-cover.webp",
            $"/api/uploads/recipes/{token}-thumb.webp"
        );
    }

    public Task<SavedRecipeAttachmentFileDto> PromoteIngestionFileToAttachmentAsync(
        string sourceMediaUrlPath,
        CancellationToken cancellationToken = default
    )
    {
        var fullSourcePath = ResolvePhysicalPath(sourceMediaUrlPath);
        if (!File.Exists(fullSourcePath))
        {
            throw new NotFoundException(
                $"Source file '{sourceMediaUrlPath}' was not found on disk."
            );
        }

        var attachmentsDir = Path.Combine(_uploadsRoot, "attachments");
        Directory.CreateDirectory(attachmentsDir);

        var ext = Path.GetExtension(fullSourcePath).ToLowerInvariant();
        var token = Guid.NewGuid().ToString("N");
        var destFileName = $"{token}{ext}";
        var destPath = Path.Combine(attachmentsDir, destFileName);

        File.Copy(fullSourcePath, destPath, overwrite: true);

        var contentType = ext == ".pdf" ? "application/pdf" : "image/webp";
        var fileInfo = new FileInfo(destPath);

        return Task.FromResult(
            new SavedRecipeAttachmentFileDto(
                $"/api/uploads/attachments/{destFileName}",
                contentType,
                fileInfo.Length
            )
        );
    }

    public async Task<SavedRecipeAttachmentFileDto> SaveDirectRecipeAttachmentAsync(
        Stream content,
        string originalFileName,
        string? contentType,
        long contentLength,
        CancellationToken cancellationToken = default
    )
    {
        var isPdf =
            string.Equals(
                Path.GetExtension(originalFileName),
                ".pdf",
                StringComparison.OrdinalIgnoreCase
            ) || string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase);

        var attachmentsDir = Path.Combine(_uploadsRoot, "attachments");
        Directory.CreateDirectory(attachmentsDir);

        var token = Guid.NewGuid().ToString("N");

        if (isPdf)
        {
            if (contentLength <= 0)
            {
                throw new DomainValidationException("Uploaded file cannot be empty.");
            }
            if (contentLength > _options.MaxPdfUploadBytes)
            {
                var maxMb = _options.MaxPdfUploadBytes / (1024 * 1024);
                throw new DomainValidationException(
                    $"Uploaded PDF exceeds the maximum allowed size of {maxMb} MB."
                );
            }

            var destPath = Path.Combine(attachmentsDir, $"{token}.pdf");
            await using (
                var fs = new FileStream(
                    destPath,
                    FileMode.Create,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    4096,
                    useAsync: true
                )
            )
            {
                await content.CopyToAsync(fs, cancellationToken);
                fs.Position = 0;
                var header = new byte[5];
                var read = await fs.ReadAsync(header.AsMemory(0, 5), cancellationToken);
                if (
                    read < 5
                    || header[0] != 0x25
                    || header[1] != 0x50
                    || header[2] != 0x44
                    || header[3] != 0x46
                    || header[4] != 0x2D
                )
                {
                    throw new DomainValidationException(
                        "Uploaded file is not a valid PDF document (missing %PDF- header)."
                    );
                }
            }

            var fileInfo = new FileInfo(destPath);
            return new SavedRecipeAttachmentFileDto(
                $"/api/uploads/attachments/{token}.pdf",
                "application/pdf",
                fileInfo.Length
            );
        }
        else
        {
            ValidateImageContentLength(contentLength);
            using var image = await LoadAndNormalizeImageAsync(content, cancellationToken);

            var destPath = Path.Combine(attachmentsDir, $"{token}.webp");
            if (image.Width > 2000 || image.Height > 2400)
            {
                image.Mutate(ctx =>
                    ctx.Resize(
                        new ResizeOptions { Size = new Size(2000, 2400), Mode = ResizeMode.Max }
                    )
                );
            }
            await image.SaveAsync(destPath, WebpScanEncoder, cancellationToken);

            var fileInfo = new FileInfo(destPath);
            return new SavedRecipeAttachmentFileDto(
                $"/api/uploads/attachments/{token}.webp",
                "image/webp",
                fileInfo.Length
            );
        }
    }

    public Task DeleteMediaFileAsync(
        string? mediaUrlPath,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(mediaUrlPath))
        {
            return Task.CompletedTask;
        }

        const string prefix = "/api/uploads/";
        if (!mediaUrlPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        var relative = mediaUrlPath[prefix.Length..].TrimStart('/', '\\');
        var fullPath = Path.GetFullPath(Path.Combine(_uploadsRoot, relative));

        var rootWithSep = _uploadsRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _uploadsRoot
            : _uploadsRoot + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        try
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch
        {
            // Best-effort cleanup
        }

        return Task.CompletedTask;
    }

    private string ResolvePhysicalPath(string mediaUrlPath)
    {
        if (string.IsNullOrWhiteSpace(mediaUrlPath))
        {
            throw new DomainValidationException("Media URL path cannot be empty.");
        }

        const string prefix = "/api/uploads/";
        if (!mediaUrlPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainValidationException(
                "Invalid media path. Uploaded media paths must start with '/api/uploads/'."
            );
        }

        var relative = mediaUrlPath[prefix.Length..].TrimStart('/', '\\');
        var fullPath = Path.GetFullPath(Path.Combine(_uploadsRoot, relative));

        var rootWithSep = _uploadsRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _uploadsRoot
            : _uploadsRoot + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainValidationException("Invalid path traversal in media URL.");
        }

        return fullPath;
    }

    private void ValidateImageContentLength(long contentLength)
    {
        if (contentLength <= 0)
        {
            throw new DomainValidationException("Uploaded file cannot be empty.");
        }

        if (contentLength > _options.MaxUploadBytes)
        {
            var maxMb = _options.MaxUploadBytes / (1024 * 1024);
            throw new DomainValidationException(
                $"Uploaded file exceeds the maximum allowed size of {maxMb} MB."
            );
        }
    }

    private static async Task<Image> LoadAndNormalizeImageAsync(
        Stream content,
        CancellationToken cancellationToken
    )
    {
        MemoryStream? msToDispose = null;
        MemoryStream seekableStream;

        if (content is MemoryStream memStream && memStream.CanSeek)
        {
            seekableStream = memStream;
        }
        else
        {
            msToDispose = new MemoryStream();
            await content.CopyToAsync(msToDispose, cancellationToken);
            msToDispose.Position = 0;
            seekableStream = msToDispose;
        }

        try
        {
            Image image;
            var startPos = seekableStream.Position;
            try
            {
                image = await Image.LoadAsync(seekableStream, cancellationToken);
            }
            catch (Exception ex)
                when (ex is UnknownImageFormatException or InvalidImageContentException)
            {
                // Fall back to Magick.NET for HEIC / HEIF
                seekableStream.Position = startPos;
                try
                {
                    using var magick = new ImageMagick.MagickImage(seekableStream);
                    magick.AutoOrient();
                    using var pngMs = new MemoryStream();
                    await magick.WriteAsync(pngMs, ImageMagick.MagickFormat.Png, cancellationToken);
                    pngMs.Position = 0;
                    image = await Image.LoadAsync(pngMs, cancellationToken);
                }
                catch (Exception magickEx) when (magickEx is not DomainValidationException)
                {
                    throw new DomainValidationException(
                        "Unsupported or corrupted image file. Upload a valid JPEG, PNG, WebP, GIF, or HEIC image."
                    );
                }
            }

            // Apply EXIF orientation and strip metadata
            image.Mutate(ctx => ctx.AutoOrient());
            image.Metadata.ExifProfile = null;
            image.Metadata.IptcProfile = null;
            image.Metadata.XmpProfile = null;

            return image;
        }
        finally
        {
            if (msToDispose is not null)
            {
                await msToDispose.DisposeAsync();
            }
        }
    }
}

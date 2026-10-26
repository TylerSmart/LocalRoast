using App.Core.Exceptions;
using App.Core.Media;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
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
        ValidateContentLength(contentLength);

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
        ValidateContentLength(contentLength);

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

    private void ValidateContentLength(long contentLength)
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
        Image image;
        try
        {
            image = await Image.LoadAsync(content, cancellationToken);
        }
        catch (Exception ex)
            when (ex is UnknownImageFormatException or InvalidImageContentException)
        {
            throw new DomainValidationException(
                "Unsupported or corrupted image file. Upload a valid JPEG, PNG, WebP, or GIF image."
            );
        }

        // Apply EXIF orientation and strip metadata
        image.Mutate(ctx => ctx.AutoOrient());
        image.Metadata.ExifProfile = null;
        image.Metadata.IptcProfile = null;
        image.Metadata.XmpProfile = null;

        return image;
    }
}

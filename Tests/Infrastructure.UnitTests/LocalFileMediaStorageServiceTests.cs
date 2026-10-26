using App.Core.Exceptions;
using App.Infrastructure.Media;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace App.Infrastructure.UnitTests;

[TestClass]
public sealed class LocalFileMediaStorageServiceTests
{
    private string _tempDirectory = null!;
    private LocalFileMediaStorageService _service = null!;
    private MediaStorageOptions _options = null!;

    private sealed class DummyHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "App.Infrastructure.UnitTests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            null!;
    }

    [TestInitialize]
    public void Setup()
    {
        _tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "LocalRoast_MediaTest_" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_tempDirectory);

        _options = new MediaStorageOptions
        {
            UploadsPath = _tempDirectory,
            MaxUploadBytes = 5 * 1024 * 1024, // 5 MB
        };

        var hostEnv = new DummyHostEnvironment(_tempDirectory);
        _service = new LocalFileMediaStorageService(Options.Create(_options), hostEnv);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    private static MemoryStream CreateTestImageStream(
        int width = 800,
        int height = 600,
        ushort? exifOrientation = null
    )
    {
        using var image = new Image<Rgba32>(width, height);
        if (exifOrientation.HasValue)
        {
            image.Metadata.ExifProfile ??= new ExifProfile();
            image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, exifOrientation.Value);
        }

        var ms = new MemoryStream();
        image.SaveAsPng(ms);
        ms.Position = 0;
        return ms;
    }

    [TestMethod]
    public async Task SaveRecipeCoverAsync_ValidImage_CreatesCoverAndThumbnailWebpFiles()
    {
        using var stream = CreateTestImageStream(1800, 1400);
        var result = await _service.SaveRecipeCoverAsync(stream, "photo.png", stream.Length);

        Assert.IsNotNull(result);
        StringAssert.StartsWith(result.CoverImagePath, "/api/uploads/recipes/");
        StringAssert.EndsWith(result.CoverImagePath, "-cover.webp");
        StringAssert.StartsWith(result.ThumbnailImagePath, "/api/uploads/recipes/");
        StringAssert.EndsWith(result.ThumbnailImagePath, "-thumb.webp");

        var coverDiskPath = Path.Combine(
            _tempDirectory,
            "recipes",
            Path.GetFileName(result.CoverImagePath)
        );
        var thumbDiskPath = Path.Combine(
            _tempDirectory,
            "recipes",
            Path.GetFileName(result.ThumbnailImagePath)
        );

        Assert.IsTrue(File.Exists(coverDiskPath), "Cover file should exist on disk.");
        Assert.IsTrue(File.Exists(thumbDiskPath), "Thumb file should exist on disk.");

        // Verify dimensions were constrained
        using var savedCover = await Image.LoadAsync(coverDiskPath);
        Assert.IsTrue(savedCover.Width <= 1600 && savedCover.Height <= 1200);

        using var savedThumb = await Image.LoadAsync(thumbDiskPath);
        Assert.IsTrue(savedThumb.Width <= 600 && savedThumb.Height <= 450);
    }

    [TestMethod]
    public async Task SaveStepImageAsync_ValidImage_CreatesStepWebpFile()
    {
        using var stream = CreateTestImageStream(1400, 1000);
        var result = await _service.SaveStepImageAsync(stream, "step.png", stream.Length);

        Assert.IsNotNull(result);
        StringAssert.StartsWith(result.ImagePath, "/api/uploads/steps/");
        StringAssert.EndsWith(result.ImagePath, ".webp");

        var stepDiskPath = Path.Combine(
            _tempDirectory,
            "steps",
            Path.GetFileName(result.ImagePath)
        );
        Assert.IsTrue(File.Exists(stepDiskPath));

        using var savedStep = await Image.LoadAsync(stepDiskPath);
        Assert.IsTrue(savedStep.Width <= 1200 && savedStep.Height <= 900);
    }

    [TestMethod]
    public async Task SaveRecipeCoverAsync_ExifOrientation_AutoOrientsAndStripsExif()
    {
        // Orientation 6 = Rotate 90 CW (so width and height swap)
        using var stream = CreateTestImageStream(800, 400, exifOrientation: 6);
        var result = await _service.SaveRecipeCoverAsync(stream, "portrait.jpg", stream.Length);

        var coverDiskPath = Path.Combine(
            _tempDirectory,
            "recipes",
            Path.GetFileName(result.CoverImagePath)
        );
        using var savedCover = await Image.LoadAsync(coverDiskPath);

        // EXIF orientation 6 rotates 90 degrees CW so 800x400 becomes 400x800
        Assert.AreEqual(400, savedCover.Width);
        Assert.AreEqual(800, savedCover.Height);
        Assert.IsNull(savedCover.Metadata.ExifProfile);
    }

    [TestMethod]
    public async Task SaveRecipeCoverAsync_ZeroOrNegativeLength_ThrowsDomainValidationException()
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            _service.SaveRecipeCoverAsync(stream, "empty.png", 0)
        );
    }

    [TestMethod]
    public async Task SaveRecipeCoverAsync_ExceedsMaxSize_ThrowsDomainValidationException()
    {
        using var stream = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            _service.SaveRecipeCoverAsync(stream, "big.png", _options.MaxUploadBytes + 1)
        );
    }

    [TestMethod]
    public async Task SaveRecipeCoverAsync_InvalidImageBytes_ThrowsDomainValidationException()
    {
        using var stream = new MemoryStream("not an image at all"u8.ToArray());
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            _service.SaveRecipeCoverAsync(stream, "fake.png", stream.Length)
        );
    }

    [TestMethod]
    public async Task DeleteMediaFileAsync_ExistingFile_DeletesFile()
    {
        using var stream = CreateTestImageStream(200, 200);
        var result = await _service.SaveStepImageAsync(stream, "step.png", stream.Length);

        var stepDiskPath = Path.Combine(
            _tempDirectory,
            "steps",
            Path.GetFileName(result.ImagePath)
        );
        Assert.IsTrue(File.Exists(stepDiskPath));

        await _service.DeleteMediaFileAsync(result.ImagePath);
        Assert.IsFalse(File.Exists(stepDiskPath));
    }

    [TestMethod]
    public async Task DeleteMediaFileAsync_PathTraversal_DoesNotDeleteOutsideUploadsRoot()
    {
        var outsideFile = Path.Combine(
            Path.GetTempPath(),
            "LocalRoast_Outside_" + Guid.NewGuid().ToString("N") + ".txt"
        );
        await File.WriteAllTextAsync(outsideFile, "important data");

        try
        {
            // Attempt traversal
            await _service.DeleteMediaFileAsync(
                "/api/uploads/../../" + Path.GetFileName(outsideFile)
            );
            Assert.IsTrue(File.Exists(outsideFile));
        }
        finally
        {
            if (File.Exists(outsideFile))
            {
                File.Delete(outsideFile);
            }
        }
    }
}

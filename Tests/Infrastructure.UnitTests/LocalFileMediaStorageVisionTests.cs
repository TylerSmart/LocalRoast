using App.Core.Exceptions;
using App.Core.Media;
using App.Infrastructure.Media;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace App.Infrastructure.UnitTests;

[TestClass]
public sealed class LocalFileMediaStorageVisionTests
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
            "LocalRoast_VisionMediaTest_" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_tempDirectory);

        _options = new MediaStorageOptions
        {
            UploadsPath = _tempDirectory,
            MaxUploadBytes = 10 * 1024 * 1024,
            MaxPdfUploadBytes = 25 * 1024 * 1024,
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
            // Best-effort
        }
    }

    private static MemoryStream CreateTestImageStream(int width = 800, int height = 600)
    {
        using var image = new Image<Rgba32>(width, height);
        var ms = new MemoryStream();
        image.SaveAsPng(ms);
        ms.Position = 0;
        return ms;
    }

    private static MemoryStream CreateDummyPdfStream(string content = "Sample PDF text")
    {
        var raw = "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" + content;
        var bytes = System.Text.Encoding.ASCII.GetBytes(raw);
        return new MemoryStream(bytes);
    }

    [TestMethod]
    public async Task SaveIngestionPageImageAsync_ValidPng_SavesWebpAndThumbnail()
    {
        using var stream = CreateTestImageStream(1600, 1200);
        var result = await _service.SaveIngestionPageImageAsync(
            stream,
            "scan.png",
            stream.Length,
            "job123",
            1,
            generateThumbnail: true
        );

        Assert.IsNotNull(result);
        StringAssert.StartsWith(result.UrlPath, "/api/uploads/ingestion/job123-p1");
        StringAssert.EndsWith(result.UrlPath, ".webp");
        Assert.IsNotNull(result.ThumbnailUrlPath);
        StringAssert.EndsWith(result.ThumbnailUrlPath, "-thumb.webp");
        Assert.IsTrue(result.Width <= 2000);
        Assert.IsTrue(result.Height <= 2400);
    }

    [TestMethod]
    public async Task SaveIngestionPdfAsync_ValidHeader_SavesPdf()
    {
        using var stream = CreateDummyPdfStream();
        var result = await _service.SaveIngestionPdfAsync(
            stream,
            "recipe.pdf",
            stream.Length,
            "job456"
        );

        Assert.IsNotNull(result);
        Assert.AreEqual("/api/uploads/ingestion/job456.pdf", result.UrlPath);
        StringAssert.EndsWith(result.UrlPath, ".pdf");
    }

    [TestMethod]
    public async Task SaveIngestionPdfAsync_InvalidHeader_ThrowsDomainValidationException()
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes("NOT A VALID PDF FILE");
        using var stream = new MemoryStream(bytes);

        await Assert.ThrowsAsync<DomainValidationException>(async () =>
        {
            await _service.SaveIngestionPdfAsync(stream, "bad.pdf", stream.Length, "job789");
        });
    }

    [TestMethod]
    public async Task EncodeMediaImageAsVisionJpegAsync_ExistingWebp_ReturnsValidJpegBytes()
    {
        using var stream = CreateTestImageStream(1000, 800);
        var saved = await _service.SaveIngestionPageImageAsync(
            stream,
            "page.png",
            stream.Length,
            "jobV1",
            1,
            generateThumbnail: false
        );

        var jpegBytes = await _service.EncodeMediaImageAsVisionJpegAsync(
            saved.UrlPath,
            maxDimensionPx: 600
        );

        Assert.IsNotNull(jpegBytes);
        Assert.IsTrue(jpegBytes.Length > 0);
        Assert.AreEqual(0xFF, jpegBytes[0]);
        Assert.AreEqual(0xD8, jpegBytes[1]); // JPEG SOI marker

        using var decoded = Image.Load(jpegBytes);
        Assert.IsTrue(decoded.Width <= 600);
        Assert.IsTrue(decoded.Height <= 600);
    }

    [TestMethod]
    public async Task PromoteIngestionImageToRecipeCoverAsync_ValidSource_CreatesCoverAndThumbnail()
    {
        using var stream = CreateTestImageStream(1200, 900);
        var saved = await _service.SaveIngestionPageImageAsync(
            stream,
            "cover.png",
            stream.Length,
            "jobPromote",
            1,
            generateThumbnail: false
        );

        var cover = await _service.PromoteIngestionImageToRecipeCoverAsync(saved.UrlPath);

        Assert.IsNotNull(cover);
        StringAssert.StartsWith(cover.CoverImagePath, "/api/uploads/recipes/");
        StringAssert.EndsWith(cover.CoverImagePath, "-cover.webp");
        StringAssert.StartsWith(cover.ThumbnailImagePath, "/api/uploads/recipes/");
        StringAssert.EndsWith(cover.ThumbnailImagePath, "-thumb.webp");
    }

    [TestMethod]
    public async Task PromoteIngestionFileToAttachmentAsync_ValidFile_CopiesToAttachmentsDirectory()
    {
        using var stream = CreateDummyPdfStream();
        var savedPdf = await _service.SaveIngestionPdfAsync(
            stream,
            "source.pdf",
            stream.Length,
            "jobAttach"
        );

        var attachment = await _service.PromoteIngestionFileToAttachmentAsync(savedPdf.UrlPath);

        Assert.IsNotNull(attachment);
        StringAssert.StartsWith(attachment.MediaUrlPath, "/api/uploads/attachments/");
        StringAssert.EndsWith(attachment.MediaUrlPath, ".pdf");
        Assert.AreEqual("application/pdf", attachment.ContentType);
    }

    [TestMethod]
    public async Task SaveDirectRecipeAttachmentAsync_ValidPdf_SavesAttachment()
    {
        using var stream = CreateDummyPdfStream();
        var attachment = await _service.SaveDirectRecipeAttachmentAsync(
            stream,
            "my-notes.pdf",
            "application/pdf",
            stream.Length
        );

        Assert.IsNotNull(attachment);
        StringAssert.StartsWith(attachment.MediaUrlPath, "/api/uploads/attachments/");
        StringAssert.EndsWith(attachment.MediaUrlPath, ".pdf");
        Assert.AreEqual("application/pdf", attachment.ContentType);
    }

    [TestMethod]
    public async Task SaveDirectRecipeAttachmentAsync_ValidImage_SavesWebp()
    {
        using var stream = CreateTestImageStream(800, 600);
        var attachment = await _service.SaveDirectRecipeAttachmentAsync(
            stream,
            "photo.jpg",
            "image/jpeg",
            stream.Length
        );

        Assert.IsNotNull(attachment);
        StringAssert.StartsWith(attachment.MediaUrlPath, "/api/uploads/attachments/");
        StringAssert.EndsWith(attachment.MediaUrlPath, ".webp");
        Assert.AreEqual("image/webp", attachment.ContentType);
    }
}

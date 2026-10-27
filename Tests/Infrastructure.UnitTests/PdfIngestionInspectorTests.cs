using App.Core.Ingestion;
using App.Infrastructure.Ingestion;
using App.Infrastructure.Media;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace App.Infrastructure.UnitTests;

[TestClass]
public sealed class PdfIngestionInspectorTests
{
    private string _tempDirectory = null!;
    private LocalFileMediaStorageService _mediaStorage = null!;
    private PdfIngestionInspector _inspector = null!;

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
            "LocalRoast_PdfInspectorTest_" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_tempDirectory);

        var options = new MediaStorageOptions
        {
            UploadsPath = _tempDirectory,
            MaxUploadBytes = 10 * 1024 * 1024,
            MaxPdfUploadBytes = 25 * 1024 * 1024,
        };

        var hostEnv = new DummyHostEnvironment(_tempDirectory);
        _mediaStorage = new LocalFileMediaStorageService(Options.Create(options), hostEnv);
        _inspector = new PdfIngestionInspector(_mediaStorage);
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

    private static byte[] CreateDigitalTextPdfBytes()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        // Add enough characters to comfortably exceed the density threshold (>= 120 chars)
        var recipeText =
            "Chocolate Chip Cookies Ingredients: 2 cups all-purpose flour, 1 cup unsalted butter, 3/4 cup brown sugar, 1 tsp vanilla extract, 2 large eggs, 1 cup dark chocolate chips. Instructions: Cream the butter and sugar until light and fluffy. Beat in the eggs one at a time, then stir in vanilla. Gradually blend in the dry ingredients, then fold in chocolate chips. Bake at 375 degrees Fahrenheit for 10 to 12 minutes until golden brown.";
        page.AddText(recipeText, 12, new PdfPoint(50, 700), font);

        return builder.Build();
    }

    private static byte[] CreateEmptyPagePdfBytes()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.A4);
        return builder.Build();
    }

    [TestMethod]
    public async Task InspectAndPrepareAsync_DigitalTextPdf_RoutesToDigitalTextMode()
    {
        var pdfBytes = CreateDigitalTextPdfBytes();
        using var stream = new MemoryStream(pdfBytes);

        var savedPdf = await _mediaStorage.SaveIngestionPdfAsync(
            stream,
            "digital.pdf",
            stream.Length,
            "jobDigital"
        );

        var result = await _inspector.InspectAndPrepareAsync(savedPdf.UrlPath, "jobDigital");

        Assert.IsNotNull(result);
        Assert.AreEqual(PdfExtractionMode.DigitalText, result.Summary.ExtractionMode);
        Assert.AreEqual(1, result.Summary.TotalPageCount);
        Assert.IsTrue(result.Summary.TotalExtractedCharacters > 120);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.NormalizedDigitalText));
        StringAssert.Contains(result.NormalizedDigitalText!, "Chocolate Chip Cookies");
        Assert.AreEqual(1, result.Summary.RenderedPageImages.Count);
    }

    [TestMethod]
    public async Task InspectAndPrepareAsync_EmptyScannedPdf_RoutesToVisionRasterMode()
    {
        var pdfBytes = CreateEmptyPagePdfBytes();
        using var stream = new MemoryStream(pdfBytes);

        var savedPdf = await _mediaStorage.SaveIngestionPdfAsync(
            stream,
            "scanned.pdf",
            stream.Length,
            "jobScanned"
        );

        var result = await _inspector.InspectAndPrepareAsync(savedPdf.UrlPath, "jobScanned");

        Assert.IsNotNull(result);
        Assert.AreEqual(PdfExtractionMode.VisionRaster, result.Summary.ExtractionMode);
        Assert.AreEqual(1, result.Summary.TotalPageCount);
        Assert.AreEqual(0, result.Summary.TotalExtractedCharacters);
        Assert.IsNull(result.NormalizedDigitalText);
        Assert.AreEqual(1, result.Summary.RenderedPageImages.Count);
        Assert.AreEqual(1, result.Summary.RenderedPageImages[0].PageNumber);
        StringAssert.EndsWith(result.Summary.RenderedPageImages[0].UrlPath, ".webp");
    }
}

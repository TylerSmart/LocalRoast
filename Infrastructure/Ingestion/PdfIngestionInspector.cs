using System.Text;
using App.Core.Exceptions;
using App.Core.Ingestion;
using App.Core.Media;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace App.Infrastructure.Ingestion;

public sealed class PdfIngestionInspector(IMediaStorageService mediaStorageService)
    : IPdfIngestionInspector
{
    public async Task<PdfInspectionResult> InspectAndPrepareAsync(
        string pdfMediaUrlPath,
        string jobToken,
        CancellationToken cancellationToken = default
    )
    {
        await using var pdfStream = await mediaStorageService.OpenMediaReadStreamAsync(
            pdfMediaUrlPath,
            cancellationToken
        );

        using var ms = new MemoryStream();
        await pdfStream.CopyToAsync(ms, cancellationToken);
        var pdfBytes = ms.ToArray();

        PdfDocument document;
        try
        {
            document = PdfDocument.Open(pdfBytes);
        }
        catch (Exception ex) when (ex is not DomainValidationException)
        {
            throw new DomainValidationException(
                "Uploaded PDF could not be read or is password-protected."
            );
        }

        using (document)
        {
            var totalPageCount = document.NumberOfPages;
            if (totalPageCount <= 0)
            {
                throw new DomainValidationException("Uploaded PDF contains no pages.");
            }

            var maxTextPages = Math.Min(totalPageCount, 10);
            var maxVisionPages = Math.Min(totalPageCount, 6);

            var sbText = new StringBuilder();
            var totalChars = 0;

            for (var i = 1; i <= maxTextPages; i++)
            {
                try
                {
                    var page = document.GetPage(i);
                    var text = ContentOrderTextExtractor.GetText(page);
                    var clean = text?.Trim() ?? "";
                    if (!string.IsNullOrWhiteSpace(clean))
                    {
                        sbText.AppendLine($"--- Page {i} ---");
                        sbText.AppendLine(clean);
                        sbText.AppendLine();
                        totalChars += clean.Count(char.IsLetterOrDigit);
                    }
                }
                catch
                {
                    // Ignore per-page text extraction error
                }
            }

            var avgCharsPerPage = maxTextPages > 0 ? (double)totalChars / maxTextPages : 0.0;
            var isDigitalText =
                (maxTextPages == 1 && totalChars >= 150)
                || (maxTextPages > 1 && avgCharsPerPage >= 120.0);
            var extractionMode = isDigitalText
                ? PdfExtractionMode.DigitalText
                : PdfExtractionMode.VisionRaster;

            var renderedImages = new List<IngestionSourceFileDto>();
            string? thumbPath = null;
            var dpi = isDigitalText ? 150 : 180;

            for (var p = 0; p < maxVisionPages; p++)
            {
                try
                {
                    using var pagePngMs = new MemoryStream();
                    PDFtoImage.Conversion.SavePng(
                        pagePngMs,
                        pdfBytes,
                        page: p,
                        options: new PDFtoImage.RenderOptions(Dpi: dpi)
                    );
                    pagePngMs.Position = 0;

                    var saved = await mediaStorageService.SaveIngestionPageImageAsync(
                        pagePngMs,
                        $"page_{p + 1}.png",
                        pagePngMs.Length,
                        jobToken,
                        p + 1,
                        generateThumbnail: p == 0,
                        cancellationToken
                    );

                    if (p == 0)
                    {
                        thumbPath = saved.ThumbnailUrlPath;
                    }

                    renderedImages.Add(
                        new IngestionSourceFileDto(
                            PageNumber: p + 1,
                            OriginalFileName: $"Page {p + 1}",
                            UrlPath: saved.UrlPath,
                            ContentType: "image/webp",
                            FileSizeBytes: saved.FileSizeBytes,
                            Width: saved.Width,
                            Height: saved.Height
                        )
                    );
                }
                catch
                {
                    // Fall back to extracting embedded images via PdfPig
                    try
                    {
                        var page = document.GetPage(p + 1);
                        var images = page.GetImages().ToList();
                        if (images.Count > 0)
                        {
                            var largest = images
                                .OrderByDescending(img => img.WidthInSamples * img.HeightInSamples)
                                .First();

                            if (largest.TryGetPng(out var pngBytes))
                            {
                                using var imgMs = new MemoryStream(pngBytes);
                                var saved = await mediaStorageService.SaveIngestionPageImageAsync(
                                    imgMs,
                                    $"page_{p + 1}.png",
                                    pngBytes.Length,
                                    jobToken,
                                    p + 1,
                                    generateThumbnail: p == 0,
                                    cancellationToken
                                );

                                if (p == 0)
                                {
                                    thumbPath = saved.ThumbnailUrlPath;
                                }

                                renderedImages.Add(
                                    new IngestionSourceFileDto(
                                        PageNumber: p + 1,
                                        OriginalFileName: $"Page {p + 1}",
                                        UrlPath: saved.UrlPath,
                                        ContentType: "image/webp",
                                        FileSizeBytes: saved.FileSizeBytes,
                                        Width: saved.Width,
                                        Height: saved.Height
                                    )
                                );
                            }
                        }
                    }
                    catch
                    {
                        // Ignore page render fallback error
                    }
                }
            }

            if (extractionMode == PdfExtractionMode.VisionRaster && renderedImages.Count == 0)
            {
                throw new DomainValidationException(
                    "Scanned PDF contained no renderable page images."
                );
            }

            var summary = new PdfInspectionSummaryDto(
                TotalPageCount: totalPageCount,
                ProcessedPageCount: renderedImages.Count,
                TotalExtractedCharacters: totalChars,
                AverageCharactersPerPage: Math.Round(avgCharsPerPage, 1),
                ExtractionMode: extractionMode,
                RenderedPageImages: renderedImages
            );

            return new PdfInspectionResult(
                Summary: summary,
                NormalizedDigitalText: isDigitalText && sbText.Length > 0
                    ? sbText.ToString()
                    : null,
                GeneratedThumbnailImagePath: thumbPath
            );
        }
    }
}

namespace App.Core.Ingestion;

public record PdfInspectionResult(
    PdfInspectionSummaryDto Summary,
    string? NormalizedDigitalText,
    string? GeneratedThumbnailImagePath
);

public interface IPdfIngestionInspector
{
    Task<PdfInspectionResult> InspectAndPrepareAsync(
        string pdfMediaUrlPath,
        string jobToken,
        CancellationToken cancellationToken = default
    );
}

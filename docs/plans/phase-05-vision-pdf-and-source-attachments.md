# Phase 05: Multi-Image / Camera Scan Ingestion, PDF Text & Vision Pipeline, Zoomable Source Viewer & Recipe Source Attachments

> **Branch:** `feature/phase-05-vision-pdf-and-source-attachments`
> **Status:** `[x]` Completed (`[ ]` Not Started | `[~]` In Progress | `[x]` Completed)
> **PRD Reference:** `docs/prd.md` — Sections 1, 2, 3, 4.1.1, 4.2.1–4.2.4, 5, and 6 (Milestone 2, Slice 2 of 3)
> **Target Execution:** Run `/execute-phase` to execute this plan step-by-step.

---

## 1. Scope & Objectives

This phase delivers **Slice 2 of Milestone 2**, completing all four multimodal recipe ingestion channels by adding **Multi-Page Camera / Cookbook Photo Scans (`PNG`, `JPG`, `WEBP`, `HEIC`)**, **Hybrid PDF Document Ingestion** (page-by-page text-density inspection routing digital PDFs to the **Text LLM** and rasterized scanned/image-only PDFs to the **Vision LLM**), an interactive **Zoom & Pan Source Viewer** in the Side-by-Side Review Inbox, and optional **Original Source Scan / PDF Attachment Retention** on published recipes:

1. **Multi-Image & Mobile Camera Cookbook Scans (`IngestionChannel.Images`, `PNG`/`JPG`/`WEBP`/`HEIC`):**
   - Accept `1–6` uploaded images per job (`POST /api/ingestion/jobs/images`, `multipart/form-data`, up to `15 MB` per image / `60 MB` request total) plus optional `titleHint`, `sourceBookTitle`, and `sourceBookPage` hints.
   - Decode standard formats via `SixLabors.ImageSharp` and decode Apple `.heic`/`.heif` uploads (`ftypheic`, `ftypheix`, `ftyphevc`, `ftypmif1`, `ftypmsf1`) via `Magick.NET-Q8-AnyCPU` fallback, auto-orienting EXIF rotation, stripping metadata, and persisting crisp WebP page scans (`/api/uploads/ingestion/{jobToken}-p{n}.webp`) plus a Page 1 queue thumbnail (`/api/uploads/ingestion/{jobToken}-thumb.webp`).
   - Route all page images together in order as a single multimodal prompt to the configured **Vision LLM** (`config.VisionModel`) across both **Ollama** (`/api/chat` `messages[].images` raw base64 JPEG array) and **OpenAI-Compatible `/v1`** (`/v1/chat/completions` `image_url` `data:image/jpeg;base64,...` content blocks), transitioning safely to `WaitingForLlmHost` without losing uploaded files if `VisionModel` is unset or the GPU workstation is asleep/offline.

2. **Hybrid PDF Inspection & Rasterization Pipeline (`IngestionChannel.Pdf`, `IPdfIngestionInspector`, `PdfIngestionInspector`):**
   - Accept single PDF uploads (`POST /api/ingestion/jobs/pdf`, `multipart/form-data`, `%PDF-` magic header check, up to `25 MB`), saving the original PDF immediately to `/api/uploads/ingestion/{jobToken}.pdf` and returning `202 Accepted` in `< 200ms`.
   - During background `Preprocessing`, inspect up to `10` pages with **`PdfPig`** (`UglyToad.PdfPig`) and compute per-page alphanumeric character density:
     - **Digital Text PDF (`PdfExtractionMode.DigitalText`):** When average non-whitespace characters per page `>= 120` (or total `>= 150` on a 1-page PDF), build structured page-delimited `NormalizedSourceText` for fast **Text LLM** extraction (`config.TextModel`), while also rendering up to `6` WebP page preview images (`150 DPI` via `PDFtoImage` with fallback to embedded `PdfPig` page images) for smooth mobile/desktop visual inspection.
     - **Scanned / Image-Only PDF (`PdfExtractionMode.VisionRaster`):** When digital text density is below threshold, rasterize up to `6` pages at `180 DPI` via **`PDFtoImage`** (with fallback to `PdfPig` `page.GetImages()` if native PDFium rendering fails) into `/api/uploads/ingestion/{jobToken}-p{n}.webp` and route the page images to the **Vision LLM** (`config.VisionModel`).

3. **Zoomable Multi-Page Source Viewer & Cover Promotion (`SourceViewerComponent`):**
   - Build a dedicated reusable `SourceViewerComponent` (`Client/src/app/features/ingestion/source-viewer/`) rendered in the Left Pane of `IngestionReviewComponent`:
     - Interactive zoom (`100%`, `150%`, `200%`, `300%`), `Rotate 90°`, drag/scroll pan container, `Page X of N` navigation buttons, and clickable page thumbnail filmstrip.
     - **1-Click "Use Page as Recipe Cover" (`POST /api/ingestion/jobs/{id}/cover-from-page`):** Lets cooks who photographed both a cookbook's recipe text page and its finished dish photo page promote any page scan directly into a `/api/uploads/recipes/{token}-cover.webp` + `-thumb.webp` recipe cover.
     - **PDF Mode Switcher:** Toggle seamlessly between `Page Previews` (zoomable WebP pages — works identically on `360px` mobile and desktop), `Embedded PDF` (same-origin `<iframe [src]="safePdfUrl()">`), and `Extracted Text` (when digital text was extracted), alongside a `"Download / Open PDF"` link.

4. **Cookbook Provenance & Retained Recipe Source Attachments (`RecipeAttachment`, `RecipeDetailComponent`, `RecipeAttachmentViewerDialogComponent`):**
   - Extract and edit `SourceBookTitle` and `SourceBookPage` end-to-end from Vision/Text LLM output $\rightarrow$ `IngestionReviewComponent` $\rightarrow$ `Recipe`.
   - Persist retained original scans (`image/webp`) and original uploaded PDFs (`application/pdf`) as `RecipeAttachment` rows copied into `/api/uploads/attachments/{token}.{ext}` when `RetainSourceAttachments` is checked on `Approve & Publish` (default `true` for `Images` and `Pdf`; if unchecked, temporary `/api/uploads/ingestion/` scans/PDFs are cleaned up on publish).
   - Support viewing retained scans/PDFs in a dedicated zoomable modal (`RecipeAttachmentViewerDialogComponent`), attaching additional scans/PDFs to any existing recipe (`POST /api/recipes/{id}/attachments`), and deleting individual attachments (`DELETE /api/recipes/{id}/attachments/{attachmentId}`), while keeping public `/share/<token>` links strictly isolated from household attachments.

### Explicitly Out of Scope (Deferred to Phase 06 & Milestones 3–4)
- Personal Access Tokens (`lr_pat_...`) and the embedded `/api/mcp` Streamable HTTP server (**Phase 06**).
- Meal Planning Calendar, Smart Shopping Lists, and SignalR live co-shopping (**Milestone 3**).
- Wake-locked Cook Mode, `CookLog` ratings/history, `FoodItem` duplicate merge UI, and 1-click AI Kitchen Helpers (**Milestone 4**).

### 1.1 File Inventory (Modify & Create)

#### NuGet & Container Updates
- `Infrastructure/App.Infrastructure.csproj` — Add NuGet packages `PdfPig` (`0.1.14`), `PDFtoImage` (`5.2.1` or latest stable `5.x`), and `Magick.NET-Q8-AnyCPU` (`15.0.0` or latest stable).
- `Tests/Infrastructure.UnitTests/App.Infrastructure.UnitTests.csproj` — Reference `App.Infrastructure` (already referenced, inherits `PdfPig` + `Magick.NET` for generating test PDFs and HEICs in memory).
- `Tests/API.IntegrationTests/App.API.IntegrationTests.csproj` & `Tests/Client.E2ETests/App.Client.E2ETests.csproj` — Uses transitive `PdfPig` / `SixLabors.ImageSharp` to construct valid multi-page PNGs, Digital Text PDFs, and Scanned Image PDFs in memory without binary fixtures.
- `API/Dockerfile` — In the `runtime` stage (`mcr.microsoft.com/dotnet/aspnet:10.0`), install lightweight native runtime libraries `libfontconfig1`, `libheif1`, and `libde265-0` for Linux container PDF rasterization and HEIC decoding.

#### Existing Backend & Backend Test Files to Modify
- `Core/Media/MediaModels.cs` — Add `SavedIngestionImageDto`, `SavedIngestionPdfDto`, and `SavedRecipeAttachmentFileDto`.
- `Core/Media/IMediaStorageService.cs` — Add methods for saving/opening/transcoding ingestion images & PDFs, promoting a page scan to a recipe cover, and promoting/saving recipe attachments.
- `Core/Ingestion/IngestionEnums.cs` — Add `PdfExtractionMode` (`DigitalText`, `VisionRaster`).
- `Core/Ingestion/IngestionModels.cs` — Add `IngestionUploadFileStream`, `EnqueueImageIngestionMetadata`, `EnqueuePdfIngestionMetadata`, `PromotePageToCoverRequest`, `IngestionSourceFileDto`, `PdfInspectionSummaryDto`, and backwards-compatible optional properties on `LlmExtractedRecipePayload`, `IngestionExtractedDraftDto`, `IngestionJobSummaryDto`, and `IngestionJobDetailDto`.
- `Core/Ingestion/ILlmExtractionClient.cs` — Add `LlmVisionImageInput`, `LlmVisionExtractionContext`, and `ExtractRecipeFromImagesAsync(...)`.
- `Core/Ingestion/IIngestionService.cs` — Add `EnqueueImagesAsync(...)`, `EnqueuePdfAsync(...)`, and `PromoteSourcePageToCoverAsync(...)`.
- `Core/Ingestion/RecipeDraftNormalizer.cs` — Map `SourceBookTitle`, `SourceBookPage`, and `RetainSourceAttachments` with backwards-compatible defaulted parameters.
- `Core/Recipes/RecipeModels.cs` — Add `RecipeAttachmentDto` and optional `Attachments` collection on `RecipeDetailDto`.
- `Core/Recipes/IRecipeService.cs` — Add `AddRecipeAttachmentAsync(...)` and `DeleteRecipeAttachmentAsync(...)`.
- `Data/Entities/IngestionJob.cs` — Add `SourceFilesJson`, `PdfInspectionJson`, `SourceBookTitle`, and `SourceBookPage`.
- `Data/Configurations/IngestionJobConfiguration.cs` — Configure max lengths for `SourceFilesJson`, `PdfInspectionJson`, and `SourceBookTitle`.
- `Data/Entities/Recipe.cs` — Add `public ICollection<RecipeAttachment> Attachments { get; set; } = new List<RecipeAttachment>();`.
- `Data/AppDbContext.cs` — Add `public DbSet<RecipeAttachment> RecipeAttachments => Set<RecipeAttachment>();`.
- `Data/Services/RecipeService.cs` — Include `Attachments` in `GetRecipeByIdAsync`, implement `AddRecipeAttachmentAsync` & `DeleteRecipeAttachmentAsync`, and delete physical attachment files on `DeleteRecipeAsync`.
- `Data/Services/IngestionService.cs` — Implement `EnqueueImagesAsync`, `EnqueuePdfAsync`, `PromoteSourcePageToCoverAsync`, attachment promotion/cleanup in `ApproveAndPublishAsync`, source file cleanup in `DiscardJobAsync`, and DTO mapping for `SourceFiles` / `PdfInspection` / `SourceBookTitle` / `SourceBookPage`.
- `Data/Services/IngestionProcessor.cs` — Add `Images` and `Pdf` preprocessing + Vision/Text LLM routing, keep `Images`/`Pdf` jobs in `ReadyForReview` (never auto-publish scans/PDFs per PRD 4.2.3), and make `ProbeAndPromoteWaitingJobsAsync` respect per-job `TextModel` vs `VisionModel` readiness.
- `Infrastructure/Media/MediaStorageOptions.cs` — Add `MaxPdfUploadBytes` (`25 MB`).
- `Infrastructure/Media/LocalFileMediaStorageService.cs` — Implement HEIC-capable image loading (`SixLabors.ImageSharp` + `Magick.NET` fallback), ingestion scan/PDF persistence, JPEG base64 vision encoding (`<= 1568px` longest side), cover promotion, and attachment storage.
- `Infrastructure/Ingestion/LlmExtractionClient.cs` — Implement `ExtractRecipeFromImagesAsync` for Ollama (`/api/chat` `images` array) and OpenAI-Compatible (`/v1/chat/completions` `image_url` blocks), plus `sourceBookTitle` & `sourceBookPage` in the JSON schema and prompt.
- `Infrastructure/InfrastructureServiceCollectionExtensions.cs` — Register `services.AddScoped<IPdfIngestionInspector, PdfIngestionInspector>();`.
- `API/Controllers/IngestionJobsController.cs` — Add `POST /api/ingestion/jobs/images`, `POST /api/ingestion/jobs/pdf`, and `POST /api/ingestion/jobs/{id:guid}/cover-from-page`.
- `API/Controllers/RecipesController.cs` — Add `POST /api/recipes/{id:guid}/attachments` and `DELETE /api/recipes/{id:guid}/attachments/{attachmentId:guid}`.
- `Tests/API.IntegrationTests/OpenApiIntegrationTests.cs` — Assert `/ingestion/jobs/images`, `/ingestion/jobs/pdf`, and `/recipes/{id}/attachments` appear in `/api/openapi/v1.json`.

#### New Backend & Backend Test Files to Create
- `Core/Ingestion/IPdfIngestionInspector.cs`
- `Data/Entities/RecipeAttachment.cs`
- `Data/Configurations/RecipeAttachmentConfiguration.cs`
- `Data/Migrations/<Timestamp>_AddVisionPdfAndRecipeAttachments.cs` (+ `.Designer.cs` & snapshot)
- `Infrastructure/Ingestion/PdfIngestionInspector.cs`
- `Tests/Infrastructure.UnitTests/PdfIngestionInspectorTests.cs`
- `Tests/Infrastructure.UnitTests/LocalFileMediaStorageVisionTests.cs`
- `Tests/API.IntegrationTests/VisionAndPdfIngestionIntegrationTests.cs`

#### Existing Angular & E2E Files to Modify
- `Client/src/app/core/models/ingestion.models.ts` — Add `PdfExtractionMode`, `IngestionSourceFileDto`, `PdfInspectionSummaryDto`, `EnqueueImageIngestionMetadata`, `EnqueuePdfIngestionMetadata`, and new fields on `IngestionExtractedDraftDto`, `IngestionJobSummaryDto`, and `IngestionJobDetailDto`.
- `Client/src/app/core/models/recipe.models.ts` — Add `RecipeAttachmentDto` and `attachments: RecipeAttachmentDto[]` on `RecipeDetailDto`.
- `Client/src/app/core/services/ingestion/ingestion.service.ts` (+ `.spec.ts`) — Add `enqueueImages(...)`, `enqueuePdf(...)`, and `promotePageToCover(...)`.
- `Client/src/app/core/services/recipe/recipe.service.ts` (+ `.spec.ts` if present, or create `recipe.service.spec.ts`) — Add `uploadRecipeAttachment(...)` and `deleteRecipeAttachment(...)`.
- `Client/src/app/features/ingestion/ingestion-hub/ingestion-hub.component.ts` (+ `.html`, `.scss`, `.spec.ts`) — Expand quick-capture tabs to 4 channels (`Web URLs`, `Raw Text`, `Photos / Camera`, `PDF Document`) with multi-image preview/reorder/remove, mobile `capture="environment"` camera button, PDF file dropzone, and optional cookbook provenance hints.
- `Client/src/app/features/ingestion/ingestion-review/ingestion-review.component.ts` (+ `.html`, `.scss`, `.spec.ts`) — Embed `<app-source-viewer>` in the Left Pane for `Images` and `Pdf` jobs; add `sourceBookTitle`, `sourceBookPage`, and `retainSourceAttachments` controls in the Right Pane.
- `Client/src/app/features/recipes/recipe-detail/recipe-detail.component.ts` (+ `.html`, `.scss`, `.spec.ts`) — Fix cookbook provenance pill visibility when `sourceName` is null, render the **Original Source Attachments** card (`no-print`), open `RecipeAttachmentViewerDialogComponent`, upload new scan/PDF attachments, and remove attachments with confirmation.

#### New Angular Components & E2E Test Files to Create
- `Client/src/app/features/ingestion/source-viewer/source-viewer.component.ts`
- `Client/src/app/features/ingestion/source-viewer/source-viewer.component.html`
- `Client/src/app/features/ingestion/source-viewer/source-viewer.component.scss`
- `Client/src/app/features/ingestion/source-viewer/source-viewer.component.spec.ts`
- `Client/src/app/features/recipes/recipe-attachment-viewer-dialog/recipe-attachment-viewer-dialog.component.ts`
- `Client/src/app/features/recipes/recipe-attachment-viewer-dialog/recipe-attachment-viewer-dialog.component.html`
- `Client/src/app/features/recipes/recipe-attachment-viewer-dialog/recipe-attachment-viewer-dialog.component.scss`
- `Client/src/app/features/recipes/recipe-attachment-viewer-dialog/recipe-attachment-viewer-dialog.component.spec.ts`
- `Tests/Client.E2ETests/VisionAndPdfIngestionE2ETests.cs`

---

## 2. Domain & Database Design (`Core/`, `Data/` & `Infrastructure/`)

### 2.1 Enums (`Core/Ingestion/IngestionEnums.cs`)

Add `PdfExtractionMode` alongside existing `LlmProvider`, `IngestionChannel`, and `IngestionJobStatus`:

```csharp
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PdfExtractionMode
{
    DigitalText = 0,
    VisionRaster = 1,
}
```

### 2.2 Entities (`Data/Entities/`)

1. **New Entity — `Data/Entities/RecipeAttachment.cs` (`IAuditable`):**
   ```csharp
   namespace App.Data.Entities;

   public sealed class RecipeAttachment : IAuditable
   {
       public Guid Id { get; set; } = Guid.NewGuid();
       public Guid RecipeId { get; set; }
       public int SortOrder { get; set; }
       public string FileName { get; set; } = string.Empty;
       public string MediaUrlPath { get; set; } = string.Empty;
       public string ContentType { get; set; } = string.Empty;
       public long FileSizeBytes { get; set; }
       public string? Label { get; set; }
       public DateTimeOffset CreatedAt { get; set; }
       public string CreatedBy { get; set; } = "System";
       public DateTimeOffset ModifiedAt { get; set; }
       public string ModifiedBy { get; set; } = "System";

       public Recipe Recipe { get; set; } = null!;
   }
   ```

2. **Modify `Data/Entities/Recipe.cs`:**
   - Add navigation collection:
     `public ICollection<RecipeAttachment> Attachments { get; set; } = new List<RecipeAttachment>();`

3. **Modify `Data/Entities/IngestionJob.cs`:**
   - Add properties:
     ```csharp
     public string? SourceFilesJson { get; set; }
     public string? PdfInspectionJson { get; set; }
     public string? SourceBookTitle { get; set; }
     public int? SourceBookPage { get; set; }
     ```

### 2.3 EF Core Fluent Configurations (`Data/Configurations/`)

1. **New Configuration — `Data/Configurations/RecipeAttachmentConfiguration.cs`:**
   - Table: `"RecipeAttachments"`, PK: `a => a.Id`.
   - `FileName`: required, `HasMaxLength(255)`.
   - `MediaUrlPath`: required, `HasMaxLength(500)`.
   - `ContentType`: required, `HasMaxLength(100)`.
   - `Label`: optional, `HasMaxLength(150)`.
   - `CreatedBy` / `ModifiedBy`: required, `HasMaxLength(256)`.
   - Index: `builder.HasIndex(a => new { a.RecipeId, a.SortOrder });`
   - Relationship (`DeleteBehavior.Cascade` — **SQL Server safe:** `RecipeAttachment` has only one FK to `Recipes`, creating zero multiple-cascade-path conflicts):
     ```csharp
     builder
         .HasOne(a => a.Recipe)
         .WithMany(r => r.Attachments)
         .HasForeignKey(a => a.RecipeId)
         .OnDelete(DeleteBehavior.Cascade);
     ```

2. **Update `Data/Configurations/IngestionJobConfiguration.cs`:**
   - Add:
     ```csharp
     builder.Property(j => j.SourceFilesJson).HasMaxLength(200000);
     builder.Property(j => j.PdfInspectionJson).HasMaxLength(200000);
     builder.Property(j => j.SourceBookTitle).HasMaxLength(200);
     ```

3. **EF Core Migration Name:**
   `AddVisionPdfAndRecipeAttachments` (`dotnet ef migrations add AddVisionPdfAndRecipeAttachments --project Data/App.Data.csproj`)

### 2.4 DTOs, Requests & Service Contracts (`Core/`)

> [!IMPORTANT]
> **`App.Core` Zero-HTTP Rule:** Never reference `IFormFile` in `App.Core`. Controllers convert uploaded `IFormFile` items into `IngestionUploadFileStream` records before calling `IIngestionService` or `IRecipeService`. Also append new optional record parameters at the **end** with default values (`= null` / `= 0` / `= true`) so all Phase 01–04 tests compile unchanged.

1. **`Core/Media/MediaModels.cs` & `Core/Media/IMediaStorageService.cs`:**
   ```csharp
   public record SavedIngestionImageDto(
       string UrlPath,
       string? ThumbnailUrlPath,
       int Width,
       int Height,
       long FileSizeBytes
   );

   public record SavedIngestionPdfDto(
       string UrlPath,
       long FileSizeBytes
   );

   public record SavedRecipeAttachmentFileDto(
       string MediaUrlPath,
       string ContentType,
       long FileSizeBytes
   );
   ```
   Add to `IMediaStorageService`:
   - `Task<SavedIngestionImageDto> SaveIngestionPageImageAsync(Stream content, string originalFileName, long contentLength, string jobToken, int pageNumber, bool generateThumbnail, CancellationToken cancellationToken = default);`
   - `Task<SavedIngestionPdfDto> SaveIngestionPdfAsync(Stream content, string originalFileName, long contentLength, string jobToken, CancellationToken cancellationToken = default);`
   - `Task<Stream> OpenMediaReadStreamAsync(string mediaUrlPath, CancellationToken cancellationToken = default);`
   - `Task<byte[]> EncodeMediaImageAsVisionJpegAsync(string mediaUrlPath, int maxDimensionPx = 1568, CancellationToken cancellationToken = default);`
   - `Task<UploadedRecipeCoverDto> PromoteIngestionImageToRecipeCoverAsync(string sourceMediaUrlPath, CancellationToken cancellationToken = default);`
   - `Task<SavedRecipeAttachmentFileDto> PromoteIngestionFileToAttachmentAsync(string sourceMediaUrlPath, CancellationToken cancellationToken = default);`
   - `Task<SavedRecipeAttachmentFileDto> SaveDirectRecipeAttachmentAsync(Stream content, string originalFileName, string? contentType, long contentLength, CancellationToken cancellationToken = default);`

2. **`Core/Ingestion/IngestionModels.cs`:**
   ```csharp
   public sealed record IngestionUploadFileStream(
       Stream Content,
       string OriginalFileName,
       string ContentType,
       long ContentLength
   );

   public record EnqueueImageIngestionMetadata(
       [MaxLength(200)] string? TitleHint = null,
       [MaxLength(200)] string? SourceBookTitle = null,
       [Range(1, 10000)] int? SourceBookPage = null
   );

   public record EnqueuePdfIngestionMetadata(
       [MaxLength(200)] string? TitleHint = null,
       [MaxLength(200)] string? SourceBookTitle = null,
       [Range(1, 10000)] int? SourceBookPage = null
   );

   public record PromotePageToCoverRequest([Range(1, 50)] int PageNumber = 1);

   public record IngestionSourceFileDto(
       int PageNumber,
       string OriginalFileName,
       string UrlPath,
       string ContentType,
       long FileSizeBytes,
       int? Width = null,
       int? Height = null
   );

   public record PdfInspectionSummaryDto(
       int TotalPageCount,
       int ProcessedPageCount,
       int TotalExtractedCharacters,
       double AverageCharactersPerPage,
       PdfExtractionMode ExtractionMode,
       IReadOnlyList<IngestionSourceFileDto> RenderedPageImages
   );
   ```
   Update existing records in `Core/Ingestion/IngestionModels.cs` (appending optional defaulted parameters at the end):
   - `LlmExtractedRecipePayload`: append `string? SourceBookTitle = null, int? SourceBookPage = null`
   - `IngestionExtractedDraftDto`: append `[MaxLength(200)] string? SourceBookTitle = null, [Range(1, 10000)] int? SourceBookPage = null, bool RetainSourceAttachments = true`
   - `IngestionJobSummaryDto`: append `int SourceFileCount = 0`
   - `IngestionJobDetailDto`: append `IReadOnlyList<IngestionSourceFileDto>? SourceFiles = null, PdfInspectionSummaryDto? PdfInspection = null, string? SourceBookTitle = null, int? SourceBookPage = null` (with `public IReadOnlyList<IngestionSourceFileDto> SourceFiles { get; init; } = SourceFiles ?? [];`)

3. **`Core/Ingestion/IPdfIngestionInspector.cs`:**
   ```csharp
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
   ```

4. **`Core/Ingestion/ILlmExtractionClient.cs`:**
   ```csharp
   public record LlmVisionImageInput(int PageNumber, string MimeType, string Base64Data);

   public record LlmVisionExtractionContext(
       IReadOnlyList<LlmVisionImageInput> Images,
       string? SupplementalHintText,
       IReadOnlyList<string> AvailableAisleNames,
       IReadOnlyList<string> AvailableTagNames
   );
   ```
   Add to `ILlmExtractionClient`:
   ```csharp
   Task<LlmExtractedRecipePayload> ExtractRecipeFromImagesAsync(
       EffectiveLlmConnectionConfig config,
       LlmVisionExtractionContext context,
       CancellationToken cancellationToken = default
   );
   ```

5. **`Core/Ingestion/IIngestionService.cs`:**
   Add:
   - `Task<IngestionJobSummaryDto> EnqueueImagesAsync(IReadOnlyList<IngestionUploadFileStream> files, EnqueueImageIngestionMetadata metadata, CancellationToken cancellationToken = default);`
   - `Task<IngestionJobSummaryDto> EnqueuePdfAsync(IngestionUploadFileStream file, EnqueuePdfIngestionMetadata metadata, CancellationToken cancellationToken = default);`
   - `Task<IngestionJobDetailDto?> PromoteSourcePageToCoverAsync(Guid id, int pageNumber, CancellationToken cancellationToken = default);`

6. **`Core/Recipes/RecipeModels.cs` & `Core/Recipes/IRecipeService.cs`:**
   ```csharp
   public record RecipeAttachmentDto(
       Guid Id,
       int SortOrder,
       string FileName,
       string MediaUrlPath,
       string ContentType,
       long FileSizeBytes,
       string? Label,
       DateTimeOffset CreatedAt
   );
   ```
   - On `RecipeDetailDto`, append `IReadOnlyList<RecipeAttachmentDto>? Attachments = null` with `public IReadOnlyList<RecipeAttachmentDto> Attachments { get; init; } = Attachments ?? [];`.
   - Do **NOT** add attachments to `PublicSharedRecipeDto` (`Core/Recipes/ShareLinkModels.cs`) so public `/share/<token>` links remain strictly scoped to recipe content per `.agents/rules/domain.md`.
   - On `IRecipeService`, add:
     - `Task<RecipeAttachmentDto?> AddRecipeAttachmentAsync(Guid recipeId, Stream content, string originalFileName, string? contentType, long contentLength, string? label = null, CancellationToken cancellationToken = default);`
     - `Task<bool> DeleteRecipeAttachmentAsync(Guid recipeId, Guid attachmentId, CancellationToken cancellationToken = default);`

### 2.5 Service & Infrastructure Business Logic (`Data/Services/` & `Infrastructure/`)

1. **HEIC + Standard Image Decoding (`LocalFileMediaStorageService`):**
   - Inspect magic bytes or extension: if the stream contains an ISO BMFF `ftyp` header (`heic`, `heix`, `hevc`, `hevx`, `mif1`, `msf1`) or `ImageSharp` throws `UnknownImageFormatException` on `.heic`/`.heif`, load via `ImageMagick.MagickImage`, call `magick.AutoOrient()`, write PNG bytes to a `MemoryStream`, and load into `SixLabors.ImageSharp.Image`. Catch `MagickException` and convert to `DomainValidationException` (`400 Bad Request`).
   - `SaveIngestionPageImageAsync`: Validates `0 < contentLength <= MaxUploadBytes` (`15 MB`), resizes max `2000x2400` (`ResizeMode.Max`), saves `/api/uploads/ingestion/{jobToken}-p{pageNumber}.webp` (`Quality = 85`), and if `generateThumbnail` is `true`, clones & saves `/api/uploads/ingestion/{jobToken}-thumb.webp` (`600x450`).
   - `SaveIngestionPdfAsync`: Validates `0 < contentLength <= MaxPdfUploadBytes` (`25 MB`) and verifies the first 5 bytes equal ASCII `%PDF-` (throwing `DomainValidationException` otherwise). Writes `/api/uploads/ingestion/{jobToken}.pdf`.
   - `EncodeMediaImageAsVisionJpegAsync`: Loads the local WebP image under `_uploadsRoot`, resizes if longest edge `> maxDimensionPx` (`1568px`), encodes in-memory via `SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 85 }`, and returns JPEG `byte[]` (universal across Ollama and OpenAI-compatible vision servers).
   - `SaveDirectRecipeAttachmentAsync`: Accepts either a PDF (`%PDF-`, `<= 25 MB`, saved to `/api/uploads/attachments/{token}.pdf`) or an image (`PNG`/`JPG`/`WEBP`/`GIF`/`HEIC`, `<= 15 MB`, normalized and saved to `/api/uploads/attachments/{token}.webp`).

2. **PDF Text-Density Inspection & Page Rasterization (`PdfIngestionInspector`):**
   - Opens the saved PDF via `PdfDocument.Open(pdfBytes)` (catching PdfPig parse/encryption errors as `DomainValidationException("Uploaded PDF could not be read or is password-protected.")`).
   - Rejects empty PDFs (`TotalPageCount == 0`). Caps text extraction at `maxTextPages = 10` and image rasterization at `maxVisionPages = 6`.
   - Extracts per-page text with `ContentOrderTextExtractor.GetText(page)`. Counts non-whitespace alphanumeric characters (`char.IsLetterOrDigit`).
   - **Routing Threshold:**
     - `isDigitalText = (processedPages == 1 && totalAlphaNumericChars >= 150) || (processedPages > 1 && avgAlphaNumericCharsPerPage >= 120)`.
     - Regardless of mode, attempts to render up to `Math.Min(totalPageCount, 6)` page preview images (`150 DPI` for `DigitalText`, `180 DPI` for `VisionRaster`) using `PDFtoImage.Conversion.SavePng(pngStream, pdfBytes, page: pageIndex, options: new RenderOptions(Dpi: dpi))`, falling back to extracting the largest embedded `IPdfImage` on `page.GetImages()` via `PdfPig` if PDFium fails or is unavailable, saving each rendered page via `SaveIngestionPageImageAsync(..., generateThumbnail: pageIndex == 0)`.
     - If `ExtractionMode == PdfExtractionMode.VisionRaster` and `RenderedPageImages.Count == 0`, throw `DomainValidationException("Scanned PDF contained no renderable page images.")`.

3. **Multimodal Vision Extraction (`LlmExtractionClient.ExtractRecipeFromImagesAsync`):**
   - If `string.IsNullOrWhiteSpace(config.VisionModel)`, throw `LlmHostUnavailableException("No Vision LLM model is configured in LLM Settings. Please select a Vision Model and retry.")`.
   - Build a culinary vision prompt instructing the model to transcribe multi-page cookbook spreads or handwritten recipe cards accurately in page order (`Page 1..N`), extract `sourceBookTitle` & `sourceBookPage` if visible in page headers/footers, normalize ingredients/units/aisles, and extract step timers.
   - **Ollama (`/api/chat`):** `model = config.VisionModel.Trim()`, `format = GetRecipeExtractionJsonSchema()`, user message `{ role = "user", content = userPrompt, images = context.Images.Select(i => i.Base64Data).ToArray() }` (raw base64 without `data:` prefix).
   - **OpenAI-Compatible (`/v1/chat/completions`):** `model = config.VisionModel.Trim()`, `response_format = new { type = "json_object" }`, user message `content` array starting with `{ type = "text", text = userPrompt }` followed by `{ type = "image_url", image_url = new { url = $"data:{img.MimeType};base64,{img.Base64Data}" } }` for each image.
   - Transient network/timeout/`408`/`429`/`502`/`503`/`504` errors throw `LlmHostUnavailableException` so `IngestionProcessor` transitions the job to `WaitingForLlmHost` with exponential backoff while preserving all uploaded files on disk.

4. **Queue Processing & Promotion Rules (`IngestionProcessor` & `IngestionService`):**
   - `EnqueueImagesAsync`: Validates `1 <= files.Count <= 6`, saves all images with a shared `jobToken = Guid.NewGuid().ToString("N")`, sets `DisplayTitle = metadata.TitleHint ?? Path.GetFileNameWithoutExtension(files[0].OriginalFileName)`, stores `SourceFilesJson`, `SourceBookTitle`, `SourceBookPage`, and `DownloadedThumbnailImagePath` (Page 1 thumb), persists `Queued` job, and signals `queueSignal.NotifyWorkAvailable()`.
   - `EnqueuePdfAsync`: Validates single PDF stream, saves `/api/uploads/ingestion/{jobToken}.pdf`, stores `SourceFilesJson` (1 entry with `ContentType = "application/pdf"`), persists `Queued` job, and signals worker.
   - `IngestionProcessor.ProcessJobByIdAsync`:
     - `Channel == IngestionChannel.Images`: Reads `SourceFilesJson`, encodes each page via `EncodeMediaImageAsVisionJpegAsync`, calls `llmClient.ExtractRecipeFromImagesAsync`, normalizes via `RecipeDraftNormalizer.Normalize(..., fallbackSourceBookTitle: job.SourceBookTitle, fallbackSourceBookPage: job.SourceBookPage, retainSourceAttachments: true)`, and sets `Status = IngestionJobStatus.ReadyForReview` (vision jobs **never** auto-publish).
     - `Channel == IngestionChannel.Pdf`: Runs `pdfInspector.InspectAndPrepareAsync` once (persisting `PdfInspectionJson`, `NormalizedSourceText`, and `DownloadedThumbnailImagePath` so retries do not re-rasterize!). Routes `DigitalText` to `ExtractRecipeFromTextAsync` and `VisionRaster` to `ExtractRecipeFromImagesAsync` (using `RenderedPageImages`). Always lands in `ReadyForReview` so the user can review page previews and attachment retention.
   - `IngestionProcessor.ProbeAndPromoteWaitingJobsAsync`: Filters `waitingJobs` to those whose required model (`TextModel` for `WebUrl`/`RawText`/`Pdf:DigitalText`; `VisionModel` for `Images`/`Pdf:VisionRaster`; either model for un-inspected `Pdf`) is non-empty before probing and promoting, preventing infinite `Queued` $\leftrightarrow$ `WaitingForLlmHost` loops when only `TextModel` is configured.
   - `ApproveAndPublishAsync`:
     - Passes `draft.SourceBookTitle` and `draft.SourceBookPage` into `UpsertRecipeRequest`.
     - If `draft.RetainSourceAttachments` is `true` and the job has `SourceFiles`: promotes each original source file (`Images`: each uploaded scan `.webp`; `Pdf`: the original `.pdf` document) via `PromoteIngestionFileToAttachmentAsync` and saves `RecipeAttachment` rows (`Label = "Page N Scan"` or `"Original PDF"`).
     - If `draft.RetainSourceAttachments` is `false`: deletes temporary `/api/uploads/ingestion/` scan/PDF files (except any file actively referenced if not already promoted).
   - `DiscardJobAsync`: Also deletes all `SourceFiles` and `PdfInspection.RenderedPageImages` via `DeleteMediaFileAsync`.

---

## 3. API Endpoints (`API/Controllers/`)

### 3.1 `IngestionJobsController` (`[Route("ingestion/jobs")]`, `[Authorize]`)

| Verb | Route (`/api/...`) | Request Body / Form | Response | Error Cases |
| :--- | :--- | :--- | :--- | :--- |
| `POST` | `/ingestion/jobs/images` | `multipart/form-data`: `files` (`IFormFileCollection`, 1–6 images), `titleHint?`, `sourceBookTitle?`, `sourceBookPage?` (`[RequestSizeLimit(67_108_864)]`) | `202 Accepted` (`IngestionJobSummaryDto`) | `400` (`0` or `>6` files, `>15 MB` file, unsupported/corrupt image) |
| `POST` | `/ingestion/jobs/pdf` | `multipart/form-data`: `file` (`IFormFile`, 1 PDF), `titleHint?`, `sourceBookTitle?`, `sourceBookPage?` (`[RequestSizeLimit(28_311_552)]`) | `202 Accepted` (`IngestionJobSummaryDto`) | `400` (missing/empty file, `>25 MB`, invalid `%PDF-` header) |
| `POST` | `/ingestion/jobs/{id:guid}/cover-from-page` | JSON `PromotePageToCoverRequest` (`{ pageNumber }`) | `200 OK` (`IngestionJobDetailDto`) | `400` (page out of range), `404`, `409` (job published/discarded) |

### 3.2 `RecipesController` (`[Route("recipes")]`, `[Authorize]`)

| Verb | Route (`/api/...`) | Request Body / Form | Response | Error Cases |
| :--- | :--- | :--- | :--- | :--- |
| `POST` | `/recipes/{id:guid}/attachments` | `multipart/form-data`: `file` (`IFormFile`), `label?` (`string`) (`[RequestSizeLimit(28_311_552)]`) | `201 Created` (`RecipeAttachmentDto`) | `400` (invalid image/PDF or size limit), `404` (recipe not found) |
| `DELETE` | `/recipes/{id:guid}/attachments/{attachmentId:guid}` | — | `204 No Content` | `404` (recipe or attachment not found) |

---

## 4. Angular Frontend Design (`Client/`)

### 4.1 Models & Services (`src/app/core/`)

1. **`src/app/core/models/ingestion.models.ts`:**
   - Add `export type PdfExtractionMode = 'DigitalText' | 'VisionRaster';`
   - Add `IngestionSourceFileDto`, `PdfInspectionSummaryDto`, `EnqueueImageIngestionMetadata`, `EnqueuePdfIngestionMetadata`, and `PromotePageToCoverRequest`.
   - Extend `IngestionExtractedDraftDto` with `sourceBookTitle?: string | null; sourceBookPage?: number | null; retainSourceAttachments?: boolean;`.
   - Extend `IngestionJobSummaryDto` with `sourceFileCount?: number;`.
   - Extend `IngestionJobDetailDto` with `sourceFiles: IngestionSourceFileDto[]; pdfInspection: PdfInspectionSummaryDto | null; sourceBookTitle: string | null; sourceBookPage: number | null;`.

2. **`src/app/core/models/recipe.models.ts`:**
   - Add `RecipeAttachmentDto` (`id`, `sortOrder`, `fileName`, `mediaUrlPath`, `contentType`, `fileSizeBytes`, `label`, `createdAt`).
   - Extend `RecipeDetailDto` with `attachments: RecipeAttachmentDto[];`.

3. **`src/app/core/services/ingestion/ingestion.service.ts`:**
   - `enqueueImages(files: File[], metadata?: EnqueueImageIngestionMetadata): Observable<IngestionJobSummaryDto>` (`FormData` appending each file under `'files'` + optional metadata fields, `POST /ingestion/jobs/images`, taps `refreshSummary()`).
   - `enqueuePdf(file: File, metadata?: EnqueuePdfIngestionMetadata): Observable<IngestionJobSummaryDto>` (`FormData` with `'file'` + optional metadata fields, `POST /ingestion/jobs/pdf`, taps `refreshSummary()`).
   - `promotePageToCover(id: string, pageNumber: number): Observable<IngestionJobDetailDto>` (`POST /ingestion/jobs/${id}/cover-from-page`).

4. **`src/app/core/services/recipe/recipe.service.ts`:**
   - `uploadRecipeAttachment(recipeId: string, file: File, label?: string): Observable<RecipeAttachmentDto>` (`POST /recipes/${recipeId}/attachments` via `FormData`).
   - `deleteRecipeAttachment(recipeId: string, attachmentId: string): Observable<void>` (`DELETE /recipes/${recipeId}/attachments/${attachmentId}`).

### 4.2 Feature Components (`src/app/features/` — Strict Per-Component Folders & External Files)

1. **`features/ingestion/ingestion-hub/` (`IngestionHubComponent`):**
   - Expand `activeTab` signal to `'urls' | 'text' | 'images' | 'pdf'`:
     - **`Photos / Camera` Tab (`photo_camera` icon):**
       - Supports selecting/dropping `1–6` image files (`accept="image/png,image/jpeg,image/webp,image/heic,image/heif,.heic,.heif"`, `multiple`) **plus** a dedicated mobile-friendly **"Snap Cookbook Page"** button (`<input type="file" accept="image/*" capture="environment">`) that appends consecutive camera snaps up to 6 pages!
       - Renders selected page cards with client-side preview thumbnails (showing a clean `HEIC` placeholder icon when browser `URL.createObjectURL` cannot preview raw `.heic` natively), `Move Left` / `Move Right` page-order buttons, `Remove` button, and optional `Title Hint`, `Cookbook Title`, and `Starting Page #` inputs.
     - **`PDF Document` Tab (`picture_as_pdf` icon):**
       - Drag-and-drop / file picker for a `.pdf` document (`accept="application/pdf,.pdf"`), displaying selected filename + file size badge, clear button, and optional `Title Hint` / `Cookbook Title` / `Page #` inputs, explaining automatic Digital Text vs. Vision OCR routing.
   - Queue summary cards display a page/file count badge (e.g., `2 pages`) when `job.sourceFileCount > 1`.
   - At `360px` mobile width, channel tabs wrap into a clean `2x2` thumb-friendly grid without horizontal page overflow.

2. **New Component — `features/ingestion/source-viewer/` (`SourceViewerComponent`):**
   - Inputs (`input()`):
     - `channel = input.required<IngestionChannel>()`
     - `sourceFiles = input<IngestionSourceFileDto[]>([])`
     - `pdfInspection = input<PdfInspectionSummaryDto | null>(null)`
     - `normalizedSourceText = input<string | null>(null)`
     - `allowSetCover = input<boolean>(false)`
     - `isPromotingCover = input<boolean>(false)`
   - Outputs (`output()`):
     - `promotePageToCover = output<number>()`
   - State signals: `activePageIndex = signal(0)`, `zoomPercent = signal<100 | 150 | 200 | 300>(100)`, `rotationDeg = signal<0 | 90 | 180 | 270>(0)`, `pdfViewMode = signal<'pages' | 'embed' | 'text'>('pages')`.
   - Renders:
     - Top toolbar with `Page X of N` prev/next controls, zoom step buttons (`zoom_out`, `100%..300%`, `zoom_in`, `fit_screen`), `rotate_right` (`90°`), and optional **"Use Page as Cover"** (`add_photo_alternate`) action button.
     - Scrollable/pannable high-contrast stage (`overflow: auto`) scaling the active image smoothly via `[style.width.%]="zoomPercent()"` and `[style.transform]="'rotate(' + rotationDeg() + 'deg)'"`.
     - Clickable thumbnail filmstrip below the stage when `displayPages().length > 1`.
     - For `Pdf`: shows an extraction telemetry badge (`Digital Text PDF · N pages` or `Scanned PDF (Vision OCR) · N pages`), `"Open / Download PDF"` anchor, and `Page Previews` | `Embedded PDF` | `Extracted Text` mode pills (`DomSanitizer.bypassSecurityTrustResourceUrl` strictly guarded to paths starting with `/api/uploads/`).

3. **`features/ingestion/ingestion-review/` (`IngestionReviewComponent`):**
   - Header channel badge handles all 4 channels (`Web Recipe`, `Raw Text`, `Cookbook Scan`, `PDF Document`).
   - **Left Pane:** Renders `<app-source-viewer>` when `job()?.channel === 'Images' || job()?.channel === 'Pdf'` (wired to `(promotePageToCover)="onPromotePageToCover($event)"`, updating `form.patchValue({ coverImagePath, thumbnailImagePath })` and showing a snackbar confirmation), while preserving the existing Web URL / Raw Text inspector for `WebUrl` and `RawText` jobs.
   - **Right Pane (Overview Card):**
     - Adds a **Cookbook & Source Provenance** 2-column row (`sourceBookTitle` and `sourceBookPage`, plus `sourceName` / `sourceAuthor`).
     - When `job()?.sourceFiles.length`, renders a **"Retain original scan / PDF as recipe attachment"** `<mat-checkbox formControlName="retainSourceAttachments">` (default `true`).

4. **New Dialog — `features/recipes/recipe-attachment-viewer-dialog/` (`RecipeAttachmentViewerDialogComponent`):**
   - Opens via `MatDialog` from `RecipeDetailComponent` with `MAT_DIALOG_DATA` `{ title: string; attachments: RecipeAttachmentDto[]; initialIndex: number }`.
   - Converts `RecipeAttachmentDto[]` items into viewer items (rendering zoomable/rotatable image scans or embedded/downloadable PDFs) with full-bleed mobile (`maxWidth: '96vw'`) and spacious desktop (`width: '920px'`) sizing.

5. **`features/recipes/recipe-detail/` (`RecipeDetailComponent`):**
   - Update hero provenance condition from `@if (recipe()!.sourceName)` to `@if (recipe()!.sourceName || recipe()!.sourceBookTitle || recipe()!.sourceAuthor)` so cookbook titles and page numbers always display cleanly.
   - Add an **"Original Source & Attachments"** section (`no-print` class) showing retained scans/PDFs (thumbnail or PDF icon, label/filename, formatted KB/MB size, **View** button opening `RecipeAttachmentViewerDialogComponent`, **Download** link, and **Remove** icon button using `ConfirmDialogComponent`), plus an **"Attach Scan / PDF"** upload button (`accept="image/*,.heic,.heif,application/pdf,.pdf"`) so household cooks can attach source cards to any recipe at any time.

---

## 5. Playwright E2E Test Scenarios (`Tests/Client.E2ETests/VisionAndPdfIngestionE2ETests.cs`)

Create `Tests/Client.E2ETests/VisionAndPdfIngestionE2ETests.cs` inheriting from `E2ETestBase` with an in-process mock LAN Ollama server (`MockMultimodalOllamaServer`) that inspects `/api/chat` requests (`images` array present $\rightarrow$ Vision model response; text-only $\rightarrow$ Text model response):

- `[TestMethod] MultiImageIngestion_UploadTwoPages_ZoomRotatePromoteCoverAndRetainAttachments()`:
  Registers user, configures both `Text Model` (`qwen2.5:14b`) and `Vision Model` (`qwen2.5vl:7b`) in LLM Settings, navigates to `/ingest`, switches to `Photos / Camera`, uploads 2 generated PNG cookbook pages with `Cookbook Title = "The Cozy Kitchen"` and `Starting Page = 142`, waits for `Ready for Review`, opens `/ingest/review/:id`, verifies `<app-source-viewer>` displays `Page 1 of 2`, tests Zoom (`150%`), Rotate (`90°`), switches to `Page 2 of 2`, clicks **"Use Page as Cover"**, verifies `retainSourceAttachments` is checked, clicks **Approve & Publish**, and verifies on `/recipes/:id` that the cover photo, `"The Cozy Kitchen"` `(p. 142)`, and 2 retained source attachments render and open in `RecipeAttachmentViewerDialogComponent` (and tests removing 1 attachment).
- `[TestMethod] PdfIngestion_DigitalTextVsScannedImagePdf_RoutesToTextAndVisionModelsCorrectly()`:
  Programmatically generates (a) a **Digital Text PDF** via `PdfDocumentBuilder` (`AddText` > 200 chars) and (b) a **Scanned Image-Only PDF** (`AddPng` with zero text); uploads both via the `PDF Document` tab on `/ingest`; verifies `MockMultimodalOllamaServer` received a text-only `/api/chat` call on `qwen2.5:14b` for the digital PDF and an `images`-bearing `/api/chat` call on `qwen2.5vl:7b` for the scanned PDF; opens the PDF review screen to verify the `Digital Text PDF` / `Scanned PDF` telemetry chip, `Page Previews` / `Extracted Text` mode toggles, and unchecking `retainSourceAttachments` publishes cleanly with `0` retained attachments.
- `[TestMethod] VisionIngestion_OfflineHost_TransitionsToWaitingPreservingScansAndResumesOnline()`:
  Toggles `MockMultimodalOllamaServer.SimulateOffline = true`, uploads a cookbook scan image, verifies the `WaitingForLlmHost` resilience banner appears on `/ingest` with the Page 1 thumbnail intact, restores `SimulateOffline = false`, clicks **Retry All Waiting Jobs**, and verifies the job transitions to `Ready for Review`.
- `[TestMethod] VisionIngestion_MobileViewport_360px_CameraTabAndSplitReviewSwitchCleanly()`:
  Sets viewport to `360x640`, verifies the 4-channel tab grid on `/ingest` has zero horizontal scroll (`scrollWidth <= clientWidth`), uploads a cookbook photo via mobile capture/upload, navigates to `/ingest/review/:id`, toggles `Original Source` $\leftrightarrow$ `Structured Draft` panes at `360px`, zooms the scan in `Original Source`, and publishes to the cookbook.

---

## 6. Step-by-Step Execution Checklist (For Implementation Agent)

- [x] **Task 1: Backend Core, Data, PDF/Vision Infrastructure & EF Migration**
  - Add `PdfPig`, `PDFtoImage`, and `Magick.NET-Q8-AnyCPU` to `Infrastructure/App.Infrastructure.csproj` and update `API/Dockerfile` runtime packages (`libfontconfig1 libheif1 libde265-0`).
  - Update `Core/Media/`, `Core/Ingestion/`, and `Core/Recipes/` models, enums, interfaces, and `RecipeDraftNormalizer`.
  - Create `RecipeAttachment` + `RecipeAttachmentConfiguration`, update `Recipe`, `IngestionJob`, `IngestionJobConfiguration`, and `AppDbContext`.
  - Implement `LocalFileMediaStorageService` extensions (HEIC + WebP + PDF + Vision JPEG + attachment promotion), `PdfIngestionInspector`, `LlmExtractionClient.ExtractRecipeFromImagesAsync`, `RecipeService` attachment methods, `IngestionService` image/PDF/cover/attachment flows, and `IngestionProcessor` multimodal routing + model-aware `WaitingForLlmHost` probing.
  - Register `IPdfIngestionInspector` in `InfrastructureServiceCollectionExtensions.cs`.
  - Generate, apply, and format EF migration:
    ```powershell
    .\scripts\ensure-local-infra.ps1
    dotnet ef migrations add AddVisionPdfAndRecipeAttachments --project Data/App.Data.csproj
    dotnet ef database update --project Data/App.Data.csproj
    dotnet csharpier format Data/Migrations
    ```
  - Write & run unit tests in `Tests/Core.UnitTests` and `Tests/Infrastructure.UnitTests` (`PdfIngestionInspectorTests.cs`, `LocalFileMediaStorageVisionTests.cs`, and vision tests in `LlmExtractionClientTests.cs`):
    ```powershell
    dotnet test --project Tests/Core.UnitTests/App.Core.UnitTests.csproj
    dotnet test --project Tests/Infrastructure.UnitTests/App.Infrastructure.UnitTests.csproj
    ```

- [x] **Task 2: API Controllers & Integration Tests**
  - Add `POST /api/ingestion/jobs/images`, `POST /api/ingestion/jobs/pdf`, and `POST /api/ingestion/jobs/{id:guid}/cover-from-page` to `API/Controllers/IngestionJobsController.cs`.
  - Add `POST /api/recipes/{id:guid}/attachments` and `DELETE /api/recipes/{id:guid}/attachments/{attachmentId:guid}` to `API/Controllers/RecipesController.cs`.
  - Update `Tests/API.IntegrationTests/OpenApiIntegrationTests.cs` and create `Tests/API.IntegrationTests/VisionAndPdfIngestionIntegrationTests.cs` covering:
    - Auth guard (`401`) on new upload/attachment endpoints
    - Multi-image (`1–6`) validation, `>6` rejection (`400`), corrupt image rejection (`400`), Vision LLM extraction, cover-from-page promotion, and `RetainSourceAttachments` (`true` vs `false`)
    - Digital Text PDF (`TextModel`) vs Scanned Image PDF (`VisionModel`) end-to-end processing
    - Direct recipe attachment upload/delete and public `/share/<token>` isolation (no attachment leakage)
    - Missing `VisionModel` / offline Vision host `WaitingForLlmHost` transition without promoting prematurely when only `TextModel` is configured
  - Run integration tests until 100% green:
    ```powershell
    dotnet test --project Tests/API.IntegrationTests/App.API.IntegrationTests.csproj
    ```

- [x] **Task 3: Angular Client & Unit Tests**
  - Update `ingestion.models.ts`, `recipe.models.ts`, `ingestion.service.ts` (+ `.spec.ts`), and `recipe.service.ts` (+ `.spec.ts`).
  - Create dedicated `SourceViewerComponent` (`features/ingestion/source-viewer/`) and `RecipeAttachmentViewerDialogComponent` (`features/recipes/recipe-attachment-viewer-dialog/`) with strictly separate `.ts`, `.html`, `.scss`, and `.spec.ts` files.
  - Update `IngestionHubComponent`, `IngestionReviewComponent`, and `RecipeDetailComponent` (+ `.spec.ts` tests).
  - Run frontend lint, production build, and unit tests until 100% green:
    ```powershell
    # In Client/:
    npm run lint
    npm run build:production
    npm test -- --watch=false
    ```

- [x] **Task 4: Playwright E2E Verification & Final Formatting**
  - Implement `Tests/Client.E2ETests/VisionAndPdfIngestionE2ETests.cs` covering all 4 scenarios in Section 5.
  - Rebuild Angular production bundle (`npm run build:production` in `Client/`) and run Playwright E2E tests:
    ```powershell
    dotnet test --project Tests/Client.E2ETests/App.Client.E2ETests.csproj
    ```
  - Format all backend and frontend files:
    ```powershell
    dotnet csharpier format .
    # In Client/:
    npm run format
    npm run lint:fix
    ```
  - Verify full solution build & test suite (`dotnet build App.slnx` and `dotnet test App.slnx`).
  - Mark this plan's status as `[x] Completed` and commit onto `feature/phase-05-vision-pdf-and-source-attachments`.

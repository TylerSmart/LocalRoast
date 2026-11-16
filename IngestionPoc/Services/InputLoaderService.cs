using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using App.Core.Ingestion;
using App.Data.Services;
using App.Infrastructure.Ingestion;
using App.Infrastructure.Media;
using IngestionPoc.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace IngestionPoc.Services;

public sealed class InputLoaderService
{
    public static readonly string[] DefaultPresetTitles =
    [
        "Classic Guacamole",
        "Ultracreamy Hummus",
        "Curry Deviled Eggs with Easy-Peel Hard-Cooked Eggs",
        "Lemon Ricotta Pancakes",
        "One-Minute Salsa",
        "Baharat-Spiced Beef Topping for Hummus",
    ];

    private sealed record AtkJsonRecipe(
        string Title,
        string? Description,
        decimal? BaseServings,
        string? YieldDisplay,
        string? SourceName,
        string? SourceAuthor,
        string? SourceBookTitle,
        int? SourceBookPage,
        List<AtkJsonIngredient>? Ingredients,
        List<AtkJsonStep>? Steps,
        [property: JsonPropertyName("_chapter")] string? Chapter
    );

    private sealed record AtkJsonIngredient(
        string? SectionName,
        string DisplayName,
        string? OriginalRawText,
        bool IsOptional
    );

    private sealed record AtkJsonStep(int StepNumber, string Instruction);

    private sealed class PocHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "IngestionPoc";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new PhysicalFileProvider(contentRootPath);
    }

    public static List<RecipeInputItem> LoadAtkRecipes(
        string atkJsonPath,
        string? preset,
        IReadOnlyList<int>? indices,
        IReadOnlyList<string>? titleFilters,
        int offset,
        int? limit,
        string format = "canonical"
    )
    {
        if (!File.Exists(atkJsonPath))
        {
            throw new FileNotFoundException($"ATK JSON file not found: {atkJsonPath}", atkJsonPath);
        }

        var json = File.ReadAllText(atkJsonPath, Encoding.UTF8);
        var all =
            JsonSerializer.Deserialize<List<AtkJsonRecipe>>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            ) ?? [];

        var indexed = all.Select((r, idx) => (Index: idx, Recipe: r)).ToList();
        IEnumerable<(int Index, AtkJsonRecipe Recipe)> selected;

        if (indices is { Count: > 0 })
        {
            var idxSet = indices.ToHashSet();
            selected = indexed.Where(x => idxSet.Contains(x.Index));
        }
        else if (titleFilters is { Count: > 0 })
        {
            selected = indexed.Where(x =>
                titleFilters.Any(f =>
                    x.Recipe.Title.Contains(f.Trim(), StringComparison.OrdinalIgnoreCase)
                )
            );
        }
        else if (string.Equals(preset, "default", StringComparison.OrdinalIgnoreCase))
        {
            var presetOrder = DefaultPresetTitles
                .Select((t, i) => (Title: t, Order: i))
                .ToDictionary(x => x.Title, x => x.Order, StringComparer.OrdinalIgnoreCase);

            selected = indexed
                .Where(x => presetOrder.ContainsKey(x.Recipe.Title))
                .OrderBy(x => presetOrder[x.Recipe.Title]);
        }
        else
        {
            selected = indexed.Skip(Math.Max(0, offset));
        }

        if (limit.HasValue && limit.Value > 0)
        {
            selected = selected.Take(limit.Value);
        }

        var results = new List<RecipeInputItem>();
        foreach (var (idx, recipe) in selected)
        {
            var normalized = string.Equals(format, "plain", StringComparison.OrdinalIgnoreCase)
                ? FormatAtkAsPlainText(recipe)
                : FormatAtkAsCanonicalText(recipe);

            results.Add(
                new RecipeInputItem(
                    Id: $"atk-{idx:0000}",
                    Title: recipe.Title,
                    SourceKind: $"atk-{format}",
                    NormalizedText: normalized,
                    SourceReference: $"{Path.GetFileName(atkJsonPath)}#[{idx}]"
                )
            );
        }

        return results;
    }

    public static async Task<RecipeInputItem> LoadWebUrlAsync(
        string url,
        HttpClient httpClient,
        CancellationToken cancellationToken = default
    )
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "localroast-ingestion-poc");
        Directory.CreateDirectory(tempRoot);
        var mediaStorage = new LocalFileMediaStorageService(
            Options.Create(new MediaStorageOptions { UploadsPath = tempRoot }),
            new PocHostEnvironment(tempRoot)
        );

        var scraper = new WebRecipeScraper(httpClient, mediaStorage);
        var scraped = await scraper.ScrapeRecipePageAsync(url, cancellationToken);
        var normalized = IngestionProcessor.BuildNormalizedWebText(scraped.Payload);
        var title = !string.IsNullOrWhiteSpace(scraped.Payload.PageTitle)
            ? scraped.Payload.PageTitle.Trim()
            : new Uri(url).Host;

        return new RecipeInputItem(
            Id: "web-1",
            Title: title,
            SourceKind: scraped.Payload.HadRecipeJsonLd ? "web-jsonld" : "web-html",
            NormalizedText: normalized,
            SourceReference: scraped.Payload.CanonicalUrl
        );
    }

    public static async Task<RecipeInputItem> LoadPdfAsync(
        string pdfPath,
        PocLlmConfig llmConfig,
        HttpClient httpClient,
        CancellationToken cancellationToken = default
    )
    {
        if (!File.Exists(pdfPath))
        {
            throw new FileNotFoundException($"PDF file not found: {pdfPath}", pdfPath);
        }

        var pdfBytes = await File.ReadAllBytesAsync(pdfPath, cancellationToken);
        using var document = PdfDocument.Open(pdfBytes);

        var maxTextPages = Math.Min(document.NumberOfPages, 10);
        var sb = new StringBuilder();
        var totalChars = 0;

        for (var p = 1; p <= maxTextPages; p++)
        {
            var page = document.GetPage(p);
            var text = ContentOrderTextExtractor.GetText(page)?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.AppendLine($"--- Page {p} ---");
                sb.AppendLine(text);
                sb.AppendLine();
                totalChars += text.Count(char.IsLetterOrDigit);
            }
        }

        var avgChars = maxTextPages > 0 ? (double)totalChars / maxTextPages : 0.0;
        var isDigital =
            (maxTextPages == 1 && totalChars >= 150) || (maxTextPages > 1 && avgChars >= 120.0);

        var fileNameTitle = Path.GetFileNameWithoutExtension(pdfPath);
        if (isDigital)
        {
            return new RecipeInputItem(
                Id: "pdf-digital",
                Title: fileNameTitle,
                SourceKind: "pdf-digital",
                NormalizedText: sb.ToString().Trim(),
                SourceReference: Path.GetFullPath(pdfPath)
            );
        }

        // Scanned / image-based PDF: rasterize pages and run Vision transcription
        var visionModel = !string.IsNullOrWhiteSpace(llmConfig.VisionModel)
            ? llmConfig.VisionModel
            : llmConfig.TextModel;

        var images = new List<LlmVisionImageInput>();
        var maxVisionPages = Math.Min(document.NumberOfPages, 6);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            for (var p = 0; p < maxVisionPages; p++)
            {
                using var ms = new MemoryStream();
                PDFtoImage.Conversion.SavePng(
                    ms,
                    pdfBytes,
                    page: p,
                    options: new PDFtoImage.RenderOptions(Dpi: 180)
                );
                images.Add(
                    new LlmVisionImageInput(
                        p + 1,
                        "image/png",
                        Convert.ToBase64String(ms.ToArray())
                    )
                );
            }
        }

        var effective = new EffectiveLlmConnectionConfig(
            Provider: llmConfig.Provider,
            BaseUrl: llmConfig.BaseUrl,
            ApiKey: llmConfig.ApiKey,
            TextModel: llmConfig.TextModel,
            VisionModel: visionModel,
            OllamaKeepAlive: llmConfig.OllamaKeepAlive,
            RequestTimeoutSeconds: llmConfig.RequestTimeoutSeconds,
            AutoPublishHighConfidenceText: false,
            EnableThinking: llmConfig.EnableThinking,
            ContextWindowTokens: llmConfig.ContextWindowTokens,
            Temperature: llmConfig.Temperature,
            TopP: llmConfig.TopP,
            TopK: llmConfig.TopK,
            OllamaCpuOnly: llmConfig.OllamaCpuOnly,
            OllamaGpuLayers: llmConfig.OllamaGpuLayers
        );

        var visionClient = new LlmExtractionClient(httpClient);
        var transcribed =
            await visionClient.TranscribeRecipeImagesAsync(
                effective,
                new LlmVisionExtractionContext(images, $"Source PDF: {fileNameTitle}"),
                cancellationToken
            )
            ?? throw new InvalidOperationException(
                "Vision transcription of scanned PDF produced no text."
            );

        return new RecipeInputItem(
            Id: "pdf-vision",
            Title: fileNameTitle,
            SourceKind: "pdf-vision",
            NormalizedText: transcribed,
            SourceReference: Path.GetFullPath(pdfPath)
        );
    }

    public static RecipeInputItem LoadTextFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Text file not found: {path}", path);
        }

        var text = File.ReadAllText(path, Encoding.UTF8).Trim();
        return new RecipeInputItem(
            Id: "text-1",
            Title: Path.GetFileNameWithoutExtension(path),
            SourceKind: "text-file",
            NormalizedText: text,
            SourceReference: Path.GetFullPath(path)
        );
    }

    public static async Task<List<RecipeInputItem>> LoadFromDbJobsAsync(
        string connectionString,
        string? statusFilter,
        IReadOnlyList<string>? titleFilters,
        int offset,
        int limit,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = CatalogSearchService.CreateReadOnlyDbContext(connectionString);
        var query = db.IngestionJobs.AsQueryable();

        if (
            !string.IsNullOrWhiteSpace(statusFilter)
            && !string.Equals(statusFilter, "all", StringComparison.OrdinalIgnoreCase)
        )
        {
            if (
                Enum.TryParse<IngestionJobStatus>(statusFilter.Trim(), ignoreCase: true, out var st)
            )
            {
                query = query.Where(j => j.Status == st);
            }
        }

        var rows = await query
            .OrderByDescending(j => j.ProcessedAt.HasValue)
            .ThenBy(j => j.CreatedAt)
            .Select(j => new
            {
                j.Id,
                j.DisplayTitle,
                j.Channel,
                j.Status,
                j.SourceUrl,
                j.RawText,
                j.NormalizedSourceText,
                j.ExtractedDraftJson,
                j.ConfidenceScore,
            })
            .ToListAsync(cancellationToken);

        var filtered = rows.AsEnumerable();
        if (titleFilters is { Count: > 0 })
        {
            filtered = rows.Where(r =>
                !string.IsNullOrWhiteSpace(r.DisplayTitle)
                && titleFilters.Any(f =>
                    r.DisplayTitle.Contains(f.Trim(), StringComparison.OrdinalIgnoreCase)
                )
            );
        }

        filtered = filtered.Skip(Math.Max(0, offset));
        if (limit > 0)
        {
            filtered = filtered.Take(limit);
        }

        var results = new List<RecipeInputItem>();
        foreach (var r in filtered)
        {
            string normalized = !string.IsNullOrWhiteSpace(r.NormalizedSourceText)
                ? r.NormalizedSourceText
                : r.RawText ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            results.Add(
                new RecipeInputItem(
                    Id: $"job-{r.Id.ToString()[..8]}",
                    Title: r.DisplayTitle ?? r.SourceUrl ?? $"Job {r.Id}",
                    SourceKind: $"db-{r.Channel}",
                    NormalizedText: normalized,
                    SourceReference: r.SourceUrl ?? $"IngestionJobs/{r.Id}",
                    ProductionExtractedDraftJson: r.ExtractedDraftJson,
                    ProductionConfidenceScore: r.ConfidenceScore,
                    ProductionJobStatus: r.Status.ToString()
                )
            );
        }

        return results;
    }

    public static async Task<List<RecipeInputItem>> AttachProductionDraftsAsync(
        string connectionString,
        IReadOnlyList<RecipeInputItem> items,
        CancellationToken cancellationToken = default
    )
    {
        var titles = items.Select(i => i.Title).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        await using var db = CatalogSearchService.CreateReadOnlyDbContext(connectionString);
        var jobDrafts = await db
            .IngestionJobs.Where(j =>
                j.DisplayTitle != null
                && titles.Contains(j.DisplayTitle)
                && j.ExtractedDraftJson != null
            )
            .OrderByDescending(j => j.ProcessedAt)
            .Select(j => new
            {
                j.DisplayTitle,
                j.ExtractedDraftJson,
                j.ConfidenceScore,
                Status = j.Status.ToString(),
            })
            .ToListAsync(cancellationToken);

        var byTitle = new Dictionary<string, (string DraftJson, decimal? Conf, string Status)>(
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var j in jobDrafts)
        {
            if (j.DisplayTitle is not null && !byTitle.ContainsKey(j.DisplayTitle))
            {
                byTitle[j.DisplayTitle] = (j.ExtractedDraftJson!, j.ConfidenceScore, j.Status);
            }
        }

        return items
            .Select(item =>
                item.ProductionExtractedDraftJson is null
                && byTitle.TryGetValue(item.Title, out var match)
                    ? item with
                    {
                        ProductionExtractedDraftJson = match.DraftJson,
                        ProductionConfidenceScore = match.Conf,
                        ProductionJobStatus = match.Status,
                    }
                    : item
            )
            .ToList();
    }

    private static string FormatAtkAsCanonicalText(AtkJsonRecipe r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Title: {r.Title}");
        if (!string.IsNullOrWhiteSpace(r.SourceAuthor))
        {
            sb.AppendLine($"Author: {r.SourceAuthor}");
        }
        if (!string.IsNullOrWhiteSpace(r.SourceBookTitle))
        {
            sb.AppendLine($"Cookbook: {r.SourceBookTitle}");
        }
        if (r.SourceBookPage.HasValue)
        {
            sb.AppendLine($"Page: {r.SourceBookPage.Value}");
        }
        if (!string.IsNullOrWhiteSpace(r.Chapter))
        {
            sb.AppendLine($"Chapter: {r.Chapter}");
        }
        if (!string.IsNullOrWhiteSpace(r.YieldDisplay))
        {
            sb.AppendLine(
                r.BaseServings.HasValue
                    ? $"Yield: {r.YieldDisplay} (Base Servings: {r.BaseServings.Value})"
                    : $"Yield: {r.YieldDisplay}"
            );
        }
        else if (r.BaseServings.HasValue)
        {
            sb.AppendLine($"Base Servings: {r.BaseServings.Value}");
        }

        if (!string.IsNullOrWhiteSpace(r.Description))
        {
            sb.AppendLine();
            sb.AppendLine("Description & Kitchen Notes:");
            sb.AppendLine(r.Description.Trim());
        }

        sb.AppendLine();
        sb.AppendLine("Ingredients:");
        string? lastSection = null;
        foreach (var ing in r.Ingredients ?? [])
        {
            if (
                !string.IsNullOrWhiteSpace(ing.SectionName)
                && !string.Equals(ing.SectionName, lastSection, StringComparison.Ordinal)
            )
            {
                sb.AppendLine();
                sb.AppendLine($"[{ing.SectionName.Trim()}]");
                lastSection = ing.SectionName.Trim();
            }

            var line = !string.IsNullOrWhiteSpace(ing.OriginalRawText)
                ? ing.OriginalRawText.Trim()
                : ing.DisplayName.Trim();
            sb.AppendLine($"- {line}");
        }

        sb.AppendLine();
        sb.AppendLine("Instructions:");
        var steps = (r.Steps ?? []).OrderBy(s => s.StepNumber).ToList();
        for (var i = 0; i < steps.Count; i++)
        {
            sb.AppendLine($"{i + 1}. {steps[i].Instruction.Trim()}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatAtkAsPlainText(AtkJsonRecipe r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Title);
        if (!string.IsNullOrWhiteSpace(r.YieldDisplay))
        {
            sb.AppendLine(r.YieldDisplay);
        }
        if (!string.IsNullOrWhiteSpace(r.Description))
        {
            sb.AppendLine();
            sb.AppendLine(r.Description.Trim());
        }
        sb.AppendLine();
        string? lastSection = null;
        foreach (var ing in r.Ingredients ?? [])
        {
            if (
                !string.IsNullOrWhiteSpace(ing.SectionName)
                && !string.Equals(ing.SectionName, lastSection, StringComparison.Ordinal)
            )
            {
                sb.AppendLine(ing.SectionName.Trim());
                lastSection = ing.SectionName.Trim();
            }
            sb.AppendLine(ing.OriginalRawText ?? ing.DisplayName);
        }
        foreach (var step in (r.Steps ?? []).OrderBy(s => s.StepNumber))
        {
            sb.AppendLine();
            sb.AppendLine(step.Instruction.Trim());
        }
        return sb.ToString().TrimEnd();
    }
}

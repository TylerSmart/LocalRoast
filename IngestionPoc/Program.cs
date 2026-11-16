using System.Globalization;
using System.Text.Json;
using App.Core.Ingestion;
using IngestionPoc.Models;
using IngestionPoc.Services;

var cli = CliArgs.Parse(args);
if (cli.ShowHelp)
{
    PrintHelp();
    return 0;
}

var repoRoot = FindRepoRoot();
var pocRoot = Path.Combine(repoRoot, "IngestionPoc");
var promptsDir = cli.PromptsDir ?? Path.Combine(pocRoot, "prompts");
var prompts = PromptSet.LoadFromDirectory(promptsDir);

var connectionString = cli.NoDb
    ? null
    : cli.ConnectionString ?? TryReadDevConnectionString(repoRoot);

PocLlmConfig? dbLlmConfig = null;
if (!string.IsNullOrWhiteSpace(connectionString))
{
    try
    {
        dbLlmConfig = await CatalogSearchService.TryLoadDefaultLlmConfigAsync(
            connectionString,
            cli.Model
        );
    }
    catch (Exception ex)
    {
        Console.WriteLine(
            $"[Warn] Could not read LLM config from SQL Server ({ex.Message}); using defaults/CLI flags."
        );
    }
}

var baseConfig =
    dbLlmConfig
    ?? new PocLlmConfig(
        Provider: LlmProvider.Ollama,
        BaseUrl: "http://localhost:11434",
        ApiKey: null,
        TextModel: "gemma4:e4b",
        VisionModel: null,
        EnableThinking: true,
        AuxThinkingOverride: null,
        Temperature: null,
        TopP: null,
        TopK: null,
        ContextWindowTokens: 32768,
        RequestTimeoutSeconds: 600,
        OllamaGpuLayers: null,
        OllamaCpuOnly: false
    );

var effectiveConfig = baseConfig with
{
    Provider = cli.Provider ?? baseConfig.Provider,
    BaseUrl = cli.BaseUrl ?? baseConfig.BaseUrl,
    ApiKey =
        cli.ApiKey
        ?? Environment.GetEnvironmentVariable("INGESTION_POC_API_KEY")
        ?? baseConfig.ApiKey,
    TextModel = cli.Model ?? baseConfig.TextModel,
    VisionModel = cli.VisionModel ?? baseConfig.VisionModel,
    EnableThinking = cli.Thinking ?? baseConfig.EnableThinking,
    AuxThinkingOverride = cli.AuxThinking ?? baseConfig.AuxThinkingOverride,
    Temperature = cli.Temperature ?? baseConfig.Temperature,
    TopP = cli.TopP ?? baseConfig.TopP,
    TopK = cli.TopK ?? baseConfig.TopK,
    ContextWindowTokens = cli.ContextWindow ?? baseConfig.ContextWindowTokens,
    RequestTimeoutSeconds = cli.TimeoutSeconds ?? baseConfig.RequestTimeoutSeconds,
    OllamaGpuLayers = cli.GpuLayers ?? baseConfig.OllamaGpuLayers,
    OllamaCpuOnly = cli.CpuOnly ?? baseConfig.OllamaCpuOnly,
    EndpointStyle = cli.EndpointStyle ?? baseConfig.EndpointStyle,
    EmbedModel = cli.DisableEmbeddings ? null : (cli.EmbedModel ?? baseConfig.EmbedModel),
};

Console.WriteLine("LocalRoast Ingestion PoC");
Console.WriteLine(
    $"  Server:  {effectiveConfig.Provider} ({effectiveConfig.EndpointStyle}) @ {effectiveConfig.BaseUrl}"
);
Console.WriteLine(
    $"  Model:   {effectiveConfig.TextModel} | Embed: {effectiveConfig.EmbedModel ?? "disabled"} (thinking={effectiveConfig.EnableThinking}, auxThinking={effectiveConfig.AuxThinkingOverride?.ToString() ?? "inherit"}, ctx={effectiveConfig.ContextWindowTokens})"
);
Console.WriteLine(
    $"  Prompts: {promptsDir} (stage3={cli.Stage3Mode}, stage4={cli.Stage4Mode}, polish={cli.PolishMode})"
);

using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var llmClient = new LlmRunnerClient(httpClient);

var catalog = new CatalogSnapshot { LoadedFromDatabase = false };
if (!string.IsNullOrWhiteSpace(connectionString))
{
    try
    {
        catalog = await CatalogSearchService.LoadSnapshotAsync(connectionString);
        Console.WriteLine(
            $"  Catalog: Loaded {catalog.FoodItems.Count} FoodItems and {catalog.Recipes.Count} Recipes from SQL Server (read-only)."
        );
    }
    catch (Exception ex)
    {
        Console.WriteLine(
            $"[Warn] Could not load catalog from SQL Server ({ex.Message}); proceeding with empty DB catalog."
        );
    }
}
else
{
    Console.WriteLine("  Catalog: Offline mode (--no-db)");
}

var inputs = new List<RecipeInputItem>();
if (!string.IsNullOrWhiteSpace(cli.Url))
{
    Console.WriteLine($"  Input:   Scraping URL {cli.Url}...");
    inputs.Add(await InputLoaderService.LoadWebUrlAsync(cli.Url, httpClient));
}
else if (!string.IsNullOrWhiteSpace(cli.PdfPath))
{
    Console.WriteLine($"  Input:   Reading PDF {cli.PdfPath}...");
    inputs.Add(await InputLoaderService.LoadPdfAsync(cli.PdfPath, effectiveConfig, httpClient));
}
else if (!string.IsNullOrWhiteSpace(cli.TextPath))
{
    Console.WriteLine($"  Input:   Reading text file {cli.TextPath}...");
    inputs.Add(InputLoaderService.LoadTextFile(cli.TextPath));
}
else if (!string.IsNullOrWhiteSpace(cli.RawText))
{
    inputs.Add(
        new RecipeInputItem("raw-1", "Inline Recipe", "raw-text", cli.RawText.Trim(), "cli:--raw")
    );
}
else if (!string.IsNullOrWhiteSpace(cli.FromDbJobsStatus))
{
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        Console.Error.WriteLine("--from-db-jobs requires a SQL Server connection.");
        return 1;
    }

    inputs.AddRange(
        await InputLoaderService.LoadFromDbJobsAsync(
            connectionString,
            cli.FromDbJobsStatus,
            cli.Titles,
            cli.Offset,
            cli.Limit ?? 10
        )
    );
    Console.WriteLine(
        $"  Input:   Loaded {inputs.Count} IngestionJob(s) from SQL Server (status={cli.FromDbJobsStatus})"
    );
}
else
{
    var atkPath = cli.AtkPath ?? Path.Combine(repoRoot, "atk_recipes.json");
    var preset =
        cli.Indices is null && cli.Titles is null && !cli.UseSlice
            ? (cli.Preset ?? "default")
            : cli.Preset;

    inputs.AddRange(
        InputLoaderService.LoadAtkRecipes(
            atkPath,
            preset,
            cli.Indices,
            cli.Titles,
            cli.Offset,
            cli.Limit,
            cli.AtkFormat
        )
    );
    Console.WriteLine($"  Input:   Loaded {inputs.Count} ATK recipe(s) ({cli.AtkFormat} format)");

    if (cli.CompareProdDb && !string.IsNullOrWhiteSpace(connectionString))
    {
        inputs = await InputLoaderService.AttachProductionDraftsAsync(connectionString, inputs);
        var matchedCount = inputs.Count(x => x.ProductionExtractedDraftJson is not null);
        Console.WriteLine(
            $"  Compare: Attached {matchedCount}/{inputs.Count} stored production IngestionJob draft(s)"
        );
    }
}

if (inputs.Count == 0)
{
    Console.Error.WriteLine("No input recipes matched your selection.");
    return 1;
}

var modelTag = SanitizeFileName(effectiveConfig.TextModel);
var runDir =
    cli.OutputDir
    ?? Path.Combine(pocRoot, "runs", $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{modelTag}");

var runOptions = new PocRunOptions(
    RunDirectory: runDir,
    MaxDescriptionLength: cli.MaxDescriptionLength,
    SessionCatalog: cli.SessionCatalog,
    SkipSteps: cli.SkipSteps,
    SkipIngredients: cli.SkipIngredients,
    PolishMode: cli.PolishMode,
    Stage3Mode: cli.Stage3Mode,
    Stage4Mode: cli.Stage4Mode,
    CacheDirectory: Path.Combine(pocRoot, "cache")
);

Console.WriteLine($"  Output:  {runDir}");

var runner = new PipelineRunner(llmClient, prompts, effectiveConfig, catalog, runOptions);
var summaries = await runner.RunAllAsync(inputs);

Console.WriteLine("\n=== Run Complete ===");
Console.WriteLine($"Saved results and summary to: {runDir}");
foreach (var s in summaries)
{
    var polishSuffix = string.Empty;
    if (s.TargetedPolish is { } tgt)
    {
        polishSuffix +=
            $" | Targeted: {(tgt.Success ? $"{s.TargetedScore?.TotalScore ?? 0}/100 ({tgt.StepWarningsAfterPolish}w)" : "FAILED")} ({tgt.ElapsedMs / 1000.0:F1}s)";
    }
    if (s.FullPolish is { } full)
    {
        polishSuffix +=
            $" | Full: {(full.Success ? $"{s.FullScore?.TotalScore ?? 0}/100 ({full.StepWarningsAfterPolish}w)" : "FAILED")} ({full.ElapsedMs / 1000.0:F1}s)";
    }
    var prodSuffix = s.ProductionScore is not null
        ? $" | Prod={s.ProductionScore.TotalScore}/100"
        : string.Empty;

    Console.WriteLine(
        $"  - {s.Title}: {(s.Stage1Success ? "OK" : "FAILED")} in {s.TotalElapsedMs / 1000.0:F1}s (S3={s.Stage3ElapsedMs / 1000.0:F1}s, S4={s.Stage4ElapsedMs / 1000.0:F1}s) | "
            + $"Score={s.BaselineScore?.TotalScore ?? 0}/100{prodSuffix} | "
            + $"Ing: {s.ExistingIngredientMatches} db-ing, {s.ExistingRecipeMatches} db-rec, {s.NewIngredientsProposed} new, {s.UnresolvedRecipes} unres-rec | "
            + $"StepWarn: {s.StepValidationWarnings}, ProseOnly: {s.ProseOnlyIngredients.Count}{polishSuffix}"
    );
}

return summaries.Any(s => !s.Stage1Success) ? 1 : 0;

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "App.slnx")))
        {
            return dir.FullName;
        }
        dir = dir.Parent;
    }

    dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "App.slnx")))
        {
            return dir.FullName;
        }
        dir = dir.Parent;
    }

    return Directory.GetCurrentDirectory();
}

static string? TryReadDevConnectionString(string repoRoot)
{
    var devPath = Path.Combine(repoRoot, "API", "appsettings.Development.json");
    if (!File.Exists(devPath))
    {
        return null;
    }

    using var doc = JsonDocument.Parse(File.ReadAllText(devPath));
    if (
        doc.RootElement.TryGetProperty("ConnectionStrings", out var cs)
        && cs.TryGetProperty("DefaultConnection", out var dc)
        && dc.ValueKind == JsonValueKind.String
    )
    {
        return dc.GetString();
    }
    return null;
}

static string SanitizeFileName(string value)
{
    var invalid = Path.GetInvalidFileNameChars().ToHashSet();
    return new string(
        value.Select(c => invalid.Contains(c) || c == '/' || c == ':' ? '-' : c).ToArray()
    );
}

static void PrintHelp()
{
    Console.WriteLine(
        """
        LocalRoast Ingestion PoC — Multi-stage recipe ingestion sandbox

        INPUT OPTIONS (choose one; defaults to ATK curated preset):
          --preset default             Run the curated 6-recipe ATK test subset (default)
          --titles "A,B"               Filter ATK recipes by title substring (comma-separated)
          --indices "0,3,6"            Select ATK recipes by 0-based JSON array index
          --offset <n> --limit <k>     Slice ATK recipes sequentially
          --atk <path>                 Path to atk_recipes.json (default: repo root)
          --atk-format canonical|plain Format ATK recipes as labeled text or plain paste (default: canonical)
          --url <https://...>          Scrape and normalize a live recipe web page
          --pdf <path>                 Read digital text PDF or transcribe scanned PDF via vision model
          --text <path>                Read raw recipe text from a file
          --raw "<text>"               Provide inline raw recipe text
          --from-db-jobs <status>      Load inputs directly from SQL Server IngestionJobs (published|queued|needsreview|failed|all)
          --compare-prod               Attach matching stored production drafts from IngestionJobs for side-by-side scoring

        LLM & PROMPT OPTIONS (auto-loads HouseholdLlmSettings from local SQL Server when available):
          --model <tag>                Override text model (also looks up saved LlmModelConfiguration in DB)
          --embed-model <tag|none>     Embedding model for Stage 3 vector search (default: embeddinggemma-2:270m)
          --vision-model <tag>         Vision model override for scanned PDFs
          --provider Ollama|OpenAiCompatible
          --endpoint-style native|openai Call Ollama native /api/chat (default) or /v1/chat/completions
          --base-url <url>             Override LLM server URL (e.g. http://ThinkStationP3:11434)
          --api-key <key>              API key (or set INGESTION_POC_API_KEY)
          --thinking true|false        Override EnableThinking
          --aux-thinking true|false    Override thinking specifically for Stages 2 and 3 (faster batches!)
          --temperature <num>          Sampling temperature override
          --top-p <num>                Top-P override
          --top-k <int>                Top-K override
          --context-window <int>       Ollama num_ctx override
          --timeout <sec>              Per-request HTTP timeout in seconds
          --gpu-layers <int>           Ollama num_gpu override
          --cpu-only true|false        Force Ollama CPU only
          --prompts-dir <dir>          Directory holding 01..05 prompt markdown files

        PIPELINE & OUTPUT OPTIONS:
          --output <dir>               Custom run output directory (default: IngestionPoc/runs/<timestamp>_<model>)
          --max-description-length <n> Max description chars before Stage 2 runs (default: 2000)
          --session-catalog            Accumulate newly proposed ingredients & recipes in memory across batch
          --stage3-mode vector-batched|vector-single|llm-terms Stage 3 DB matching mode (default: vector-batched)
          --stage4-mode batched|single Stage 4 batched or per-step tokenization (default: batched)
          --polish none|targeted|full|both Stage 5 end-to-end QA polish mode (default: none)
          --skip-ingredients           Skip Stage 3 (DB search & matching)
          --skip-steps                 Skip Stage 4 (step tokenization)
          --no-db                      Do not connect to SQL Server
          --connection-string <cs>     Custom SQL Server connection string
          --help                       Show this help
        """
    );
}

sealed class CliArgs
{
    public bool ShowHelp { get; private set; }
    public string? Preset { get; private set; }
    public List<int>? Indices { get; private set; }
    public List<string>? Titles { get; private set; }
    public int Offset { get; private set; }
    public int? Limit { get; private set; }
    public bool UseSlice { get; private set; }
    public string? AtkPath { get; private set; }
    public string AtkFormat { get; private set; } = "canonical";
    public string? Url { get; private set; }
    public string? PdfPath { get; private set; }
    public string? TextPath { get; private set; }
    public string? RawText { get; private set; }
    public string? FromDbJobsStatus { get; private set; }
    public bool CompareProdDb { get; private set; }

    public string? Model { get; private set; }
    public string? EmbedModel { get; private set; }
    public bool DisableEmbeddings { get; private set; }
    public string? VisionModel { get; private set; }
    public LlmProvider? Provider { get; private set; }
    public EndpointStyle? EndpointStyle { get; private set; }
    public string? BaseUrl { get; private set; }
    public string? ApiKey { get; private set; }
    public bool? Thinking { get; private set; }
    public bool? AuxThinking { get; private set; }
    public double? Temperature { get; private set; }
    public double? TopP { get; private set; }
    public int? TopK { get; private set; }
    public int? ContextWindow { get; private set; }
    public int? TimeoutSeconds { get; private set; }
    public int? GpuLayers { get; private set; }
    public bool? CpuOnly { get; private set; }
    public string? PromptsDir { get; private set; }
    public string? OutputDir { get; private set; }
    public int MaxDescriptionLength { get; private set; } = 2000;
    public bool SessionCatalog { get; private set; }
    public Stage3Mode Stage3Mode { get; private set; } = Stage3Mode.VectorBatched;
    public Stage4Mode Stage4Mode { get; private set; } = Stage4Mode.Batched;
    public PolishMode PolishMode { get; private set; } = PolishMode.None;
    public bool SkipIngredients { get; private set; }
    public bool SkipSteps { get; private set; }
    public bool NoDb { get; private set; }
    public string? ConnectionString { get; private set; }

    public static CliArgs Parse(string[] args)
    {
        var c = new CliArgs();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string Next() =>
                i + 1 < args.Length
                    ? args[++i]
                    : throw new ArgumentException($"Missing value for {arg}");

            switch (arg.ToLowerInvariant())
            {
                case "-h":
                case "--help":
                    c.ShowHelp = true;
                    break;
                case "--preset":
                    c.Preset = Next();
                    break;
                case "--indices":
                    c.Indices = Next()
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => int.Parse(s.Trim(), CultureInfo.InvariantCulture))
                        .ToList();
                    break;
                case "--titles":
                    c.Titles = Next()
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .ToList();
                    break;
                case "--offset":
                    c.Offset = int.Parse(Next(), CultureInfo.InvariantCulture);
                    c.UseSlice = true;
                    break;
                case "--limit":
                    c.Limit = int.Parse(Next(), CultureInfo.InvariantCulture);
                    c.UseSlice = true;
                    break;
                case "--atk":
                    c.AtkPath = Next();
                    break;
                case "--atk-format":
                    c.AtkFormat = Next();
                    break;
                case "--url":
                    c.Url = Next();
                    break;
                case "--pdf":
                    c.PdfPath = Next();
                    break;
                case "--text":
                    c.TextPath = Next();
                    break;
                case "--raw":
                    c.RawText = Next();
                    break;
                case "--from-db-jobs":
                    c.FromDbJobsStatus = Next();
                    break;
                case "--compare-prod":
                    c.CompareProdDb = true;
                    break;
                case "--model":
                    c.Model = Next();
                    break;
                case "--embed-model":
                    var em = Next();
                    if (string.Equals(em, "none", StringComparison.OrdinalIgnoreCase))
                    {
                        c.DisableEmbeddings = true;
                    }
                    else
                    {
                        c.EmbedModel = em;
                    }
                    break;
                case "--vision-model":
                    c.VisionModel = Next();
                    break;
                case "--provider":
                    c.Provider = Enum.Parse<LlmProvider>(Next(), ignoreCase: true);
                    break;
                case "--endpoint-style":
                    c.EndpointStyle = Enum.Parse<EndpointStyle>(Next(), ignoreCase: true);
                    break;
                case "--base-url":
                    c.BaseUrl = Next();
                    break;
                case "--api-key":
                    c.ApiKey = Next();
                    break;
                case "--thinking":
                    c.Thinking = bool.Parse(Next());
                    break;
                case "--aux-thinking":
                    c.AuxThinking = bool.Parse(Next());
                    break;
                case "--temperature":
                    c.Temperature = double.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--top-p":
                    c.TopP = double.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--top-k":
                    c.TopK = int.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--context-window":
                    c.ContextWindow = int.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--timeout":
                    c.TimeoutSeconds = int.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--gpu-layers":
                    c.GpuLayers = int.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--cpu-only":
                    c.CpuOnly = bool.Parse(Next());
                    break;
                case "--prompts-dir":
                    c.PromptsDir = Next();
                    break;
                case "--output":
                    c.OutputDir = Next();
                    break;
                case "--max-description-length":
                    c.MaxDescriptionLength = int.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--session-catalog":
                    c.SessionCatalog = true;
                    break;
                case "--stage3-mode":
                    c.Stage3Mode = Next().Trim().ToLowerInvariant() switch
                    {
                        "vector-batched" or "vectorbatched" => Stage3Mode.VectorBatched,
                        "vector-single" or "vectorsingle" => Stage3Mode.VectorSingle,
                        "llm-terms" or "llmterms" => Stage3Mode.LlmTerms,
                        var unknown => throw new ArgumentException(
                            $"Unknown --stage3-mode: {unknown}"
                        ),
                    };
                    break;
                case "--stage4-mode":
                    c.Stage4Mode = Enum.Parse<Stage4Mode>(Next(), ignoreCase: true);
                    break;
                case "--polish":
                    c.PolishMode = Enum.Parse<PolishMode>(Next(), ignoreCase: true);
                    break;
                case "--skip-ingredients":
                    c.SkipIngredients = true;
                    break;
                case "--skip-steps":
                    c.SkipSteps = true;
                    break;
                case "--no-db":
                    c.NoDb = true;
                    break;
                case "--connection-string":
                    c.ConnectionString = Next();
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }
        return c;
    }
}

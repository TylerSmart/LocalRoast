using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using App.Core.Exceptions;
using App.Core.Ingestion;
using App.Infrastructure.Ingestion;

if (args.Length != 11)
{
    Console.Error.WriteLine(
        "Usage: IngestionEvaluator <input.json> <output-dir> <base-url> <model> <timeout-seconds> <thinking> <ollama-options-json> <temperature|\"\"> <top-p|\"\"> <top-k|\"\"> <context-window-tokens>"
    );
    return 2;
}

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter() },
};
var cases =
    JsonSerializer.Deserialize<EvaluationCase[]>(await File.ReadAllTextAsync(args[0]), jsonOptions)
    ?? throw new InvalidDataException("Expected a recipe case array.");
var outputDirectory = Path.GetFullPath(args[1]);
Directory.CreateDirectory(outputDirectory);
var config = new EffectiveLlmConnectionConfig(
    LlmProvider.Ollama,
    args[2],
    null,
    args[3],
    null,
    "5m",
    int.Parse(args[4]),
    false,
    bool.Parse(args[5]),
    ContextWindowTokens: int.Parse(args[10], CultureInfo.InvariantCulture),
    Temperature: string.IsNullOrEmpty(args[7])
        ? null
        : double.Parse(args[7], CultureInfo.InvariantCulture),
    TopP: string.IsNullOrEmpty(args[8])
        ? null
        : double.Parse(args[8], CultureInfo.InvariantCulture),
    TopK: string.IsNullOrEmpty(args[9]) ? null : int.Parse(args[9], CultureInfo.InvariantCulture)
);
var overrides =
    JsonNode.Parse(args[6]) as JsonObject
    ?? throw new InvalidDataException("Ollama overrides must be a JSON object.");
using var handler = new RecordingHandler(overrides);
using var http = new HttpClient(handler);
var client = new LlmExtractionClient(http);
var failed = false;
foreach (var item in cases)
{
    var path = Path.Combine(outputDirectory, $"{item.Id}.json");
    if (File.Exists(path))
    {
        throw new IOException($"Refusing to overwrite an evaluation result: {path}");
    }

    Console.WriteLine($"{config.TextModel}: {item.Id} {item.Title}");
    handler.Exchanges.Clear();
    var stopwatch = Stopwatch.StartNew();
    LlmExtractedRecipePayload? raw = null;
    IngestionExtractedDraftDto? draft = null;
    string? error = null;
    try
    {
        raw = await client.ExtractRecipeFromTextAsync(
            config,
            new LlmExtractionContext(item.SourceContent, [], item.FoodNames)
        );
        draft = RecipeDraftNormalizer.Normalize(
            raw,
            [],
            [],
            null,
            null,
            null,
            null,
            null,
            extractedModel: config.TextModel,
            rawSourceText: item.SourceContent
        );
    }
    catch (Exception ex)
        when (ex is LlmHostUnavailableException or DomainValidationException or JsonException)
    {
        failed = true;
        error = $"{ex.GetType().Name}: {ex.Message}";
        Console.Error.WriteLine(error);
    }

    stopwatch.Stop();
    await File.WriteAllTextAsync(
        path,
        JsonSerializer.Serialize(
            new
            {
                item.Id,
                item.Title,
                config.TextModel,
                config.EnableThinking,
                config.Temperature,
                config.TopP,
                config.TopK,
                config.ContextWindowTokens,
                Overrides = overrides,
                ElapsedSeconds = stopwatch.Elapsed.TotalSeconds,
                Error = error,
                Raw = raw,
                Draft = draft,
                ValidationIssues = raw is null
                    ? null
                    : RecipeExtractionDiagnostics.GetReviewIssues(raw, item.SourceContent),
                handler.Exchanges,
            },
            jsonOptions
        )
    );
    Console.WriteLine(
        $"  {stopwatch.Elapsed.TotalSeconds:F1}s; {draft?.Ingredients?.Count ?? 0} ingredients; {draft?.Steps?.Count ?? 0} steps"
    );
}

return failed ? 1 : 0;

sealed record EvaluationCase(
    string Id,
    string Title,
    string SourceContent,
    IReadOnlyList<string>? FoodNames = null
);

sealed class RecordingHandler(JsonObject overrides) : DelegatingHandler(new HttpClientHandler())
{
    public List<object> Exchanges { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
        if (overrides.Count > 0)
        {
            var options = body["options"]!.AsObject();
            foreach (var pair in overrides)
            {
                options[pair.Key] = pair.Value?.DeepClone();
            }
            request.Content.Dispose();
            request.Content = new StringContent(
                body.ToJsonString(),
                Encoding.UTF8,
                "application/json"
            );
        }

        using var response = await base.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        Exchanges.Add(
            new
            {
                Request = body,
                Status = (int)response.StatusCode,
                Response = responseText,
            }
        );
        return new HttpResponseMessage(response.StatusCode)
        {
            Content = new StringContent(responseText, Encoding.UTF8, "application/json"),
        };
    }
}

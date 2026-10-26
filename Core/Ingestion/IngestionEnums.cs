using System.Text.Json.Serialization;

namespace App.Core.Ingestion;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LlmProvider
{
    Ollama = 0,
    OpenAiCompatible = 1,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IngestionChannel
{
    WebUrl = 0,
    RawText = 1,
    Images = 2,
    Pdf = 3,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IngestionJobStatus
{
    Queued = 0,
    Preprocessing = 1,
    Extracting = 2,
    WaitingForLlmHost = 3,
    ReadyForReview = 4,
    Published = 5,
    Failed = 6,
    Discarded = 7,
}

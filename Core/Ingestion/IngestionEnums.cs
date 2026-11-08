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

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IngestionRefinementStatus
{
    Pending = 0,
    Running = 1,
    Improved = 2,
    NoImprovement = 3,
    Failed = 4,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PdfExtractionMode
{
    DigitalText = 0,
    VisionRaster = 1,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IngestionIssueTriageResolution
{
    NeedsHumanReview = 0,
    Resolved = 1,
    IgnoreAsFalsePositive = 2,
}

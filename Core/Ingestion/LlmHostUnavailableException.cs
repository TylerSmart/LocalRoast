namespace App.Core.Ingestion;

public sealed class LlmHostUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

namespace App.Core.Ingestion;

public sealed class LlmHostUnavailableException : Exception
{
    public bool IsQuotaExhausted { get; init; }

    public LlmHostUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException) { }

    public LlmHostUnavailableException(
        string message,
        bool isQuotaExhausted,
        Exception? innerException = null
    )
        : base(message, innerException)
    {
        IsQuotaExhausted = isQuotaExhausted;
    }
}

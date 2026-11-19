namespace App.Core.Ingestion;

public enum LlmQuotaScope
{
    Connection = 0,
    Model = 1,
}

public sealed class LlmHostUnavailableException : Exception
{
    public bool IsQuotaExhausted { get; init; }

    public LlmQuotaScope? ExhaustedQuotaScope { get; init; }

    public string? ExhaustedGateKey { get; init; }

    public string? QuotaResetTimezone { get; init; }

    public string? QuotaResetTime { get; init; }

    public bool SafeguardsEnabled { get; init; } = true;

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

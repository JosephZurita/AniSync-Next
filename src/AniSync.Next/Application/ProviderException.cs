namespace AniSync.Next.Application;

public sealed class ProviderException : Exception
{
    public ProviderException(string message, bool isTransient, TimeSpan? retryAfter = null, Exception? innerException = null)
        : base(message, innerException)
    {
        IsTransient = isTransient;
        RetryAfter = retryAfter;
    }

    public bool IsTransient { get; }
    public TimeSpan? RetryAfter { get; }
}

public sealed class StalePreviewException(string message) : Exception(message);


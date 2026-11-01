namespace Convenciones.Ai;

/// <summary>Base type for the failures the model boundary reports to the caller.</summary>
public abstract class AiException : Exception
{
    protected AiException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>The configuration is incomplete or invalid. Fail fast; do not call the model.</summary>
public sealed class AiConfigurationException : AiException
{
    public AiConfigurationException(string message)
        : base(message)
    {
    }
}

/// <summary>The provider could not be reached, or kept returning 429/5xx after the retries.</summary>
public sealed class AiUnavailableException : AiException
{
    public AiUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>The attempt exceeded the per-call timeout.</summary>
public sealed class AiTimeoutException : AiException
{
    public AiTimeoutException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The provider answered, but the answer is not the JSON the schema requires, even after the single
/// repair attempt. Nothing may be persisted from such an answer.
/// </summary>
public sealed class AiInvalidResponseException : AiException
{
    public AiInvalidResponseException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

namespace VNotch.Services;

public sealed class RecoverableAnimationException : Exception
{
    public RecoverableAnimationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class RecoverableMediaException : Exception
{
    public RecoverableMediaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

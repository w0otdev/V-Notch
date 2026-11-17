namespace VNotch.Services.Translation;

// Consulted only while holding the model gate. Retention and retry are independent.
internal sealed class TranslationBackendRecovery(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _failedAt;
    internal static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);
    internal bool HasFailure { get; private set; }
    internal bool RetryReady => HasFailure && _clock.GetElapsedTime(_failedAt) >= Cooldown;

    internal void Failed()
    {
        _failedAt = _clock.GetTimestamp();
        HasFailure = true;
    }

    internal void Reset() => HasFailure = false;
}

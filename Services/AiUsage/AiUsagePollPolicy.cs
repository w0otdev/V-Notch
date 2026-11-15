using VNotch.Models;

namespace VNotch.Services.AiUsage;

// Automatic failures back off without retaining a CLI process while idle.
// Explicit refresh retains its separate 10-second cooldown and can recover
// immediately after installation, login, or an account switch.
internal sealed class AiUsagePollPolicy
{
    private int _failures;
    internal void Reset() => _failures = 0;

    internal TimeSpan After(AiUsageState state)
    {
        if (state == AiUsageState.Ready) { Reset(); return TimeSpan.FromMinutes(2); }
        _failures = Math.Min(_failures + 1, 3);
        return TimeSpan.FromMinutes(state switch
        {
            AiUsageState.NotInstalled => 10,
            AiUsageState.SignedOut or AiUsageState.Unsupported => 5,
            _ => Math.Min(10, 2 << _failures)
        });
    }
}

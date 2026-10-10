using Xunit;

namespace VNotch.Tests;

internal static class DesktopTestMode
{
    internal const string EnvironmentVariable = "VNOTCH_RUN_DESKTOP_TESTS";
    internal const string SkipReason = "Visible desktop/GPU tests require a disposable non-interactive Windows session; they cannot run on the user's desktop.";
    internal static bool Enabled => !Environment.UserInteractive &&
        Environment.GetEnvironmentVariable(EnvironmentVariable) == "1";
}

public sealed class DesktopFactAttribute : FactAttribute
{
    public DesktopFactAttribute()
    {
        if (!DesktopTestMode.Enabled) Skip = DesktopTestMode.SkipReason;
    }
}

public sealed class DesktopTheoryAttribute : TheoryAttribute
{
    public DesktopTheoryAttribute()
    {
        if (!DesktopTestMode.Enabled) Skip = DesktopTestMode.SkipReason;
    }
}

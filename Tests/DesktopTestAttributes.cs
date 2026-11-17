using Xunit;

namespace VNotch.Tests;

internal static class DesktopTestMode
{
    internal const string EnvironmentVariable = "VNOTCH_RUN_DESKTOP_TESTS";
    internal const string SkipReason = "Visible desktop/GPU tests require an opted-in GitHub-hosted Windows runner with an interactive desktop; they cannot run on the user's desktop.";
    internal static bool Enabled => IsEnabled(Environment.UserInteractive,
        Environment.GetEnvironmentVariable(EnvironmentVariable),
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
        Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT"),
        Environment.GetEnvironmentVariable("RUNNER_OS"));

    // UserInteractive describes the window station, not whether a person is using
    // the machine. Hosted runners have an interactive desktop but are disposable.
    // A service/session-0 runner cannot present or capture the desktop under test.
    internal static bool IsEnabled(bool userInteractive, string? optIn, string? githubActions,
        string? runnerEnvironment, string? runnerOs) =>
        userInteractive && optIn == "1" && githubActions == "true" &&
        runnerEnvironment == "github-hosted" && runnerOs == "Windows";
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DesktopFactAttribute : FactAttribute
{
    public DesktopFactAttribute()
    {
        if (!DesktopTestMode.Enabled) Skip = DesktopTestMode.SkipReason;
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DesktopTheoryAttribute : TheoryAttribute
{
    public DesktopTheoryAttribute()
    {
        if (!DesktopTestMode.Enabled) Skip = DesktopTestMode.SkipReason;
    }
}

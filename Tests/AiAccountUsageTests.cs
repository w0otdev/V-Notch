using System.Diagnostics;
using System.IO;
using System.Text.Json;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.AiUsage;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class AiAccountUsageTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1800000000);

    [Fact]
    public void SelectedCodexPathIsAnExistingExecutableAndNeverAShellCommand()
    {
        Assert.Null(CodexAccountUsageReader.FindExecutable("codex.exe"));
        Assert.Null(CodexAccountUsageReader.FindExecutable(@"C:\codex.exe --run-other-program"));
        using var temp = new UsageTestDirectory();
        string path = Path.Combine(temp.Path, "codex.exe");
        File.WriteAllText(path, "test-only");
        var start = CodexAccountUsageReader.FindExecutable(path);
        Assert.NotNull(start);
        Assert.Equal(path, start.FileName);
        Assert.Empty(start.ArgumentList);
        Assert.Empty(start.Arguments);
    }

    [Fact]
    public void MultiBucketCodexLimitsAreNotDuplicatedAndDoNotAssumeWindowDurations()
    {
        using var json = JsonDocument.Parse("""
        {"rateLimits":{"primary":{"usedPercent":99}},"rateLimitsByLimitId":{
          "other":{"primary":{"usedPercent":42,"windowDurationMins":60,"resetsAt":1800003600}},
          "codex":{"primary":{"usedPercent":25,"windowDurationMins":300,"resetsAt":1800007200},
                   "secondary":{"usedPercent":80,"windowDurationMins":10080,"resetsAt":1800600000}}
        }}
        """);
        var usage = AiUsageParsing.Codex(json.RootElement, Now, "plus");
        Assert.Equal(AiUsageState.Ready, usage.State);
        Assert.Equal(3, usage.Windows!.Count);
        Assert.Equal("codex", usage.Windows[0].Bucket);
        Assert.Equal(75, usage.Windows[0].RemainingPercent);
        Assert.Equal(300, usage.Windows[0].DurationMinutes);
        Assert.Equal(60, usage.Windows[2].DurationMinutes);
        Assert.Equal(Now.AddHours(2), usage.Windows[0].ResetsAt);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"42\"")]
    [InlineData("-1")]
    [InlineData("101")]
    [InlineData("1e3000")]
    public void UnknownAndMalformedPercentagesAreNotShownAsUnusedAllowance(string value)
    {
        using var json = JsonDocument.Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":" + value + "}}}");
        var usage = AiUsageParsing.Codex(json.RootElement, Now, null);
        Assert.Equal(AiUsageState.Unavailable, usage.State);
        Assert.Empty(usage.Windows!);
    }

    [Theory]
    [InlineData("null", false)]
    [InlineData("1800000000", true)]
    [InlineData("1800000100", false)]
    public void ResetAndStalenessNeverInventFreshAllowance(string reset, bool expired)
    {
        using var json = JsonDocument.Parse("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":100,\"resets_at\":" + reset + "}}}");
        var usage = AiUsageParsing.Claude(json.RootElement, Now);
        var window = Assert.Single(usage.Windows!);
        Assert.Equal(expired, window.IsExpired(Now));
        Assert.Equal(0, window.RemainingPercent);
        Assert.False(usage.IsStale(Now.AddMinutes(9)));
        Assert.True(usage.IsStale(Now.AddMinutes(11)));
    }

    [Fact]
    public async Task CodexSessionUsesOnlyOfficialReadRpcsAndHandlesNotifications()
    {
        string replies = """
        {"id":1,"result":{}}
        {"method":"account/updated","params":{"authMode":"chatgpt"}}
        {"id":2,"result":{"account":{"type":"chatgpt","email":"user@example.test","planType":"pro"}}}
        {"id":3,"result":{"rateLimits":{"primary":{"usedPercent":12,"windowDurationMins":300}}}}
        """;
        var output = new StringWriter();
        var usage = await CodexAccountUsageReader.ReadSessionAsync(new StringReader(replies), output, Now, default);
        Assert.Equal(AiUsageState.Ready, usage.State);
        Assert.Equal("user@example.test", usage.AccountLabel);
        var methods = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => { using var j = JsonDocument.Parse(s); return j.RootElement.GetProperty("method").GetString(); }).ToArray();
        Assert.Equal(new[] { "initialize", "initialized", "account/read", "account/rateLimits/read" }, methods);
        Assert.DoesNotContain("thread/", output.ToString());
        Assert.DoesNotContain("access_token", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refresh_token", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("null", (int)AiUsageState.SignedOut)]
    [InlineData("{\"type\":\"apiKey\"}", (int)AiUsageState.Unsupported)]
    public async Task CodexCannotReadSubscriptionLimitsWithoutAChatGptAccount(string account, int state)
    {
        var output = new StringWriter();
        var usage = await CodexAccountUsageReader.ReadSessionAsync(new StringReader(
            "{\"id\":1,\"result\":{}}\n{\"id\":2,\"result\":{\"account\":" + account + "}}\n"), output, Now, default);
        Assert.Equal((AiUsageState)state, usage.State);
        Assert.DoesNotContain("rateLimits/read", output.ToString());
    }

    [Fact]
    public async Task CodexRejectsOversizedAndTruncatedResponsesAndDoesNotExposeProviderErrors()
    {
        await Assert.ThrowsAsync<IOException>(() => CodexAccountUsageReader.ReadSessionAsync(
            new StringReader(new string('x', 300000)), new StringWriter(), Now, default));
        await Assert.ThrowsAsync<IOException>(() => CodexAccountUsageReader.ReadSessionAsync(
            new StringReader("{\"id\":1,\"result\":{}}\n"), new StringWriter(), Now, default));
        var ex = await Assert.ThrowsAsync<IOException>(() => CodexAccountUsageReader.ReadSessionAsync(
            new StringReader("{\"id\":1,\"error\":{\"message\":\"PRIVATE-TOKEN\"}}\n"), new StringWriter(), Now, default));
        Assert.DoesNotContain("PRIVATE-TOKEN", ex.ToString());
    }

    [Fact]
    public async Task CodexNeverApprovesServerActions()
    {
        string replies = """
        {"method":"item/commandExecution/requestApproval","id":"approve-1","params":{"command":"private"}}
        {"id":1,"result":{}}
        {"id":2,"result":{"account":null}}
        """;
        var output = new StringWriter();
        await CodexAccountUsageReader.ReadSessionAsync(new StringReader(replies), output, Now, default);
        using var rejection = JsonDocument.Parse(output.ToString().Split('\n')[1]);
        Assert.Equal(-32601, rejection.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.DoesNotContain("private", output.ToString());
    }

    [Fact]
    public async Task MonitoringIsOptInAndPrivacyBlocksOnlyTheOnlineProvider()
    {
        var reader = new FakeCodex();
        int claudeReads = 0;
        await using var monitor = new AiAccountUsageMonitor(reader, () => { Interlocked.Increment(ref claudeReads); return new("Claude", AiUsageState.Waiting); });
        await monitor.RefreshAsync();
        Assert.Equal(0, reader.Reads);
        Assert.Equal(0, claudeReads);
        monitor.ApplySettings(new() { EnableAiUsageMonitoring = true, EnableLocalOnlyMode = true });
        await Eventually(() => Volatile.Read(ref claudeReads) > 0);
        Assert.Equal(0, reader.Reads);
        Assert.Equal(AiUsageState.PrivacyBlocked, monitor.Codex.State);
        Assert.Equal(AiUsageState.Waiting, monitor.Claude.State);
    }

    [Fact]
    public async Task ManualRefreshDoesNotOverlapOrHammerCodexAndDisableCancelsPendingReads()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new FakeCodex(async token => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return new("Codex", AiUsageState.Ready); });
        await using var monitor = new AiAccountUsageMonitor(reader);
        monitor.ApplySettings(new() { EnableAiUsageMonitoring = true, TrackClaudeUsage = false });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await monitor.RefreshAsync();
        Assert.Equal(1, reader.Reads);
        monitor.ApplySettings(new());
        await Eventually(() => reader.Cancelled);
        Assert.Equal(AiUsageState.Disabled, monitor.Codex.State);
        await monitor.RefreshAsync();
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public async Task AppearanceChangesDoNotRestartAccountPolling()
    {
        var original = AiUsageAppearance.Current;
        var reader = new FakeCodex();
        await using var monitor = new AiAccountUsageMonitor(reader);
        try
        {
            var settings = new NotchSettings { EnableAiUsageMonitoring = true, TrackClaudeUsage = false };
            monitor.ApplySettings(settings);
            await Eventually(() => monitor.Codex.State == AiUsageState.Ready);
            settings.AiUsageShowCodex = false;
            settings.AiUsageClaudeColor = "#326BFF";
            settings.AiUsageMotionIntensity = 35;
            monitor.ApplySettings(settings);
            await monitor.RefreshAsync();
            Assert.Equal(1, reader.Reads);
            Assert.Equal(AiUsageState.Ready, monitor.Codex.State);
            Assert.False(AiUsageAppearance.Current.ShowCodex);
            Assert.Equal("#326BFF", AiUsageAppearance.Current.ClaudeColor);
        }
        finally { AiUsageAppearance.Apply(original); }
    }

    [Fact]
    public async Task ExplicitRefreshReadsTheCurrentAccountBeforeTheAutomaticIntervalWithACooldown()
    {
        var now = Now;
        var reader = new FakeCodex();
        await using var monitor = new AiAccountUsageMonitor(reader, utcNow: () => now);
        monitor.ApplySettings(new() { EnableAiUsageMonitoring = true, TrackClaudeUsage = false });
        await Eventually(() => monitor.Codex.State == AiUsageState.Ready);
        await monitor.RefreshAsync(forceCodex: true);
        Assert.Equal(1, reader.Reads);
        now = now.AddSeconds(10);
        await monitor.RefreshAsync();
        Assert.Equal(1, reader.Reads);
        await monitor.RefreshAsync(forceCodex: true);
        Assert.Equal(2, reader.Reads);
        await monitor.RefreshAsync(forceCodex: true);
        Assert.Equal(2, reader.Reads);

        monitor.ApplySettings(new() { EnableAiUsageMonitoring = true, EnableLocalOnlyMode = true });
        now = now.AddMinutes(3);
        await monitor.RefreshAsync(forceCodex: true);
        Assert.Equal(2, reader.Reads);
        Assert.Equal(AiUsageState.PrivacyBlocked, monitor.Codex.State);
    }

    [Fact]
    public async Task RapidSettingsChangesCannotPublishAnOldAccountOrLoseTheMonitorLoop()
    {
        var reader = new FakeCodex();
        await using var monitor = new AiAccountUsageMonitor(reader);
        for (int i = 0; i < 30; i++) monitor.ApplySettings(new() { EnableAiUsageMonitoring = i % 2 == 0 });
        monitor.ApplySettings(new() { EnableAiUsageMonitoring = true });
        await Eventually(() => monitor.Codex.State == AiUsageState.Ready);
        int reads = reader.Reads;
        await monitor.RefreshAsync();
        Assert.Equal(reads, reader.Reads);
    }

    [Fact]
    public async Task SettingsTransferCannotEnablePollingOrImportAnExecutableToRun()
    {
        using var temp = new UsageTestDirectory();
        using var settingsService = new SettingsService(Path.Combine(temp.Path, "settings.json"));
        var local = new NotchSettings { EnableAiUsageMonitoring = true, CodexUsageExecutable = @"C:\Trusted\codex.exe", ExpandedWidget = "aiusage" };
        string exported = settingsService.ExportSettingsToString(local);
        Assert.DoesNotContain("Trusted", exported);
        var (imported, _) = SettingsService.ImportSettingsFromString("""{"EnableAiUsageMonitoring":true,"CodexUsageExecutable":"C:\\untrusted.exe","ExpandedWidget":"aiusage"}""");
        Assert.False(imported.EnableAiUsageMonitoring);
        Assert.Empty(imported.CodexUsageExecutable);
        Assert.Equal("aiusage", imported.ExpandedWidget);
        var (preserved, _) = SettingsService.ImportSettingsFromString(exported, local);
        Assert.True(preserved.EnableAiUsageMonitoring);
        Assert.Equal(local.CodexUsageExecutable, preserved.CodexUsageExecutable);
        await settingsService.SaveAsync(local);
        Assert.Equal(local.CodexUsageExecutable, settingsService.Load().CodexUsageExecutable);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class FakeCodex(Func<CancellationToken, Task<AiAccountUsage>>? action = null) : ICodexAccountUsageReader
    {
        internal int Reads;
        internal bool Cancelled;
        public async Task<AiAccountUsage> ReadAsync(string executable, CancellationToken token)
        {
            Interlocked.Increment(ref Reads);
            try { return action == null ? new("Codex", AiUsageState.Ready) : await action(token); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
        }
    }
}

public sealed class ClaudeUsageBridgeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApiAuthenticationIsExplainedEvenBeforeTheBridgeIsConnected(bool hasCustomStatusLine)
    {
        using var temp = new UsageTestDirectory();
        string settings = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(settings, hasCustomStatusLine
            ? """{"apiKeyHelper":"helper","statusLine":{"type":"command","command":"custom-script"}}"""
            : """{"apiKeyHelper":"helper"}""");
        var setup = new ClaudeUsageBridge(temp.Path, settings).InspectSetupState();
        Assert.True(setup.HasCustomApiEndpoint);
        Assert.Equal(0, VNotchNative.vn_ai_usage_resolve_claude_setup_state((int)AiUsageState.Waiting,
            1, setup.HasStatusLine ? (byte)1 : (byte)0, 0, 1, out byte action, out int prompt));
        Assert.Equal((byte)1, action);
        Assert.Equal(2, prompt);
    }

    [Fact]
    public void InstallPreservesSettingsMakesABackupAndRemoveOnlyRemovesItsOwnCommand()
    {
        using var temp = new UsageTestDirectory();
        string settings = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(settings, "{\"env\":{\"EXISTING\":\"yes\"},\"permissions\":{\"deny\":[\"Bash\"]}}");
        var bridge = new ClaudeUsageBridge(temp.Path, settings);
        Assert.Equal(ClaudeBridgeSetup.Installed, bridge.Install());
        Assert.Equal(ClaudeBridgeSetup.AlreadyInstalled, bridge.Install());
        Assert.Single(Directory.GetFiles(temp.Path, "settings.json.v-notch-backup-*"));
        using (var json = JsonDocument.Parse(File.ReadAllText(settings)))
        {
            Assert.Equal("yes", json.RootElement.GetProperty("env").GetProperty("EXISTING").GetString());
            Assert.Equal(bridge.Command, json.RootElement.GetProperty("statusLine").GetProperty("command").GetString());
        }
        Assert.True(bridge.Remove());
        using var removed = JsonDocument.Parse(File.ReadAllText(settings));
        Assert.False(removed.RootElement.TryGetProperty("statusLine", out _));
        Assert.True(removed.RootElement.TryGetProperty("permissions", out _));
        File.WriteAllText(settings, "{\"statusLine\":{\"type\":\"command\",\"command\":\"custom-script\"}}");
        Assert.Equal(ClaudeBridgeSetup.ExistingStatusLine, bridge.Install());
        Assert.False(bridge.Remove());
        Assert.Contains("custom-script", File.ReadAllText(settings));
        Assert.True(File.Exists(Path.Combine(temp.Path, "claude-statusline.ps1")));
    }

    [Fact]
    public void MalformedAndOversizedFilesArePreservedAndNotOverwritten()
    {
        using var temp = new UsageTestDirectory();
        string settings = Path.Combine(temp.Path, "settings.json");
        var bridge = new ClaudeUsageBridge(temp.Path, settings);
        File.WriteAllText(settings, "{broken");
        Assert.ThrowsAny<JsonException>(() => bridge.Install());
        Assert.Equal("{broken", File.ReadAllText(settings));
        File.WriteAllText(settings, new string('x', 1024 * 1024 + 1));
        Assert.Throws<IOException>(() => bridge.Install());
    }

    [Fact]
    public async Task ActualPowerShellBridgeStoresOnlyWhitelistedLimitFieldsAndReplacesOldSamples()
    {
        using var temp = new UsageTestDirectory();
        var bridge = new ClaudeUsageBridge(temp.Path, Path.Combine(temp.Path, "settings.json"));
        bridge.Install();
        string script = Path.Combine(temp.Path, "claude-statusline.ps1");
        var payload = """
        {"session_id":"private-session","transcript_path":"private-path","access_token":"private-token",
         "rate_limits":{"five_hour":{"used_percentage":23.5,"resets_at":1900000000,"secret":"private-value"},
                        "seven_day":{"used_percentage":91,"resets_at":1900600000}}}
        """;
        string output = await RunBridge(script, temp.Path, payload);
        string data = Path.Combine(temp.Path, "V-Notch", "ai-usage", "claude.json");
        Assert.True(File.Exists(data), output);
        string stored = File.ReadAllText(data);
        Assert.DoesNotContain("private", stored);
        var usage = ClaudeUsageBridge.Read(data);
        Assert.Equal(AiUsageState.Ready, usage.State);
        Assert.Equal(2, usage.Windows!.Count);
        Assert.Equal(76.5, usage.Windows[0].RemainingPercent);
        Assert.Contains("76.5% left", output);
        await RunBridge(script, temp.Path, "{}");
        Assert.Equal(AiUsageState.Waiting, ClaudeUsageBridge.Read(data).State);
    }

    [Fact]
    public void FutureOrMissingObservationTimesAreNotTrusted()
    {
        using var temp = new UsageTestDirectory();
        string path = Path.Combine(temp.Path, "sample.json");
        File.WriteAllText(path, """{"rate_limits":{"five_hour":{"used_percentage":0}}}""");
        Assert.Equal(AiUsageState.Waiting, ClaudeUsageBridge.Read(path).State);
        File.WriteAllText(path, """{"observed_at":"2099-01-01T00:00:00Z","rate_limits":{"five_hour":{"used_percentage":0}}}""");
        Assert.Equal(AiUsageState.Waiting, ClaudeUsageBridge.Read(path).State);
    }

    private static async Task<string> RunBridge(string script, string localAppData, string payload)
    {
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }) start.ArgumentList.Add(arg);
        start.Environment["LOCALAPPDATA"] = localAppData;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(payload);
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        Assert.Equal(0, process.ExitCode);
        Assert.Empty(await error);
        return await output;
    }

    [Fact]
    public async Task EncodedConnectionCommandWorksWithSpacesAndShellMetacharactersInThePath()
    {
        using var temp = new UsageTestDirectory();
        string directory = Path.Combine(temp.Path, "Name's $ bridge & test");
        var bridge = new ClaudeUsageBridge(directory, Path.Combine(temp.Path, "settings.json"));
        bridge.Install();
        string[] command = bridge.Command.Split(' ');
        var start = new ProcessStartInfo(command[0]) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in command.Skip(1)) start.ArgumentList.Add(arg);
        start.Environment["LOCALAPPDATA"] = temp.Path;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":50}}}");
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("50% left", await output);
        // EncodedCommand may use CLIXML for errors; no error should be produced.
        Assert.Empty(await error);
        Assert.True(File.Exists(Path.Combine(temp.Path, "V-Notch", "ai-usage", "claude.json")));
    }

    [Fact]
    public void InspectSetupStateAndNativeClassifierDetectConnectionAndCustomEndpoints()
    {
        using var temp = new UsageTestDirectory();
        string settings = Path.Combine(temp.Path, "settings.json");
        var bridge = new ClaudeUsageBridge(temp.Path, settings);

        var missing = bridge.InspectSetupState();
        Assert.False(missing.HasSettingsFile);
        Assert.Equal(0, VNotchNative.vn_ai_usage_resolve_claude_setup_state((int)AiUsageState.Waiting, 0, 0, 0, 0, out byte actionMissing, out int promptMissing));
        Assert.Equal((byte)1, actionMissing);
        Assert.Equal(1, promptMissing);

        bridge.Install();
        var connected = bridge.InspectSetupState();
        Assert.True(connected.HasSettingsFile);
        Assert.True(connected.HasStatusLine);
        Assert.True(connected.MatchesBridgeCommand);
        Assert.False(connected.HasCustomApiEndpoint);
        Assert.Equal(0, VNotchNative.vn_ai_usage_resolve_claude_setup_state((int)AiUsageState.Waiting, 1, 1, 1, 0, out byte actionReady, out int promptReady));
        Assert.Equal((byte)0, actionReady);
        Assert.Equal(0, promptReady);

        string payload = JsonSerializer.Serialize(new
        {
            statusLine = new { type = "command", command = bridge.Command },
            env = new { ANTHROPIC_BASE_URL = "https://proxy.example/anthropic" }
        });
        File.WriteAllText(settings, payload);
        var customApi = bridge.InspectSetupState();
        Assert.True(customApi.MatchesBridgeCommand);
        Assert.True(customApi.HasCustomApiEndpoint);
        Assert.Equal(0, VNotchNative.vn_ai_usage_resolve_claude_setup_state((int)AiUsageState.Waiting, 1, 1, 1, 1, out byte actionCustom, out int promptCustom));
        Assert.Equal((byte)1, actionCustom);
        Assert.Equal(2, promptCustom);
    }
}

internal sealed class UsageTestDirectory : IDisposable
{
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VNotch_AiUsage_" + Guid.NewGuid().ToString("N"));
    internal UsageTestDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
}

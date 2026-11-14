using VNotch.Models;

namespace VNotch.Services.AiUsage;

internal sealed class AiAccountUsageMonitor : IAsyncDisposable
{
    internal static AiAccountUsageMonitor Shared { get; } = new(new CodexAccountUsageReader());
    private readonly ICodexAccountUsageReader _codex;
    private readonly Func<AiAccountUsage> _claude;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly AiQuotaNotifications _quotaNotifications = new();
    private CancellationTokenSource? _lifetime;
    private Task _loop = Task.CompletedTask;
    private readonly List<Task> _retired = [];
    private (bool Enabled, bool Codex, bool Claude, bool Blocked, string Path) _options;
    private DateTimeOffset _nextCodexRead;
    private DateTimeOffset _nextManualCodexRead;
    private bool _disposed;
    internal AiAccountUsageMonitor(ICodexAccountUsageReader codex, Func<AiAccountUsage>? claude = null,
        Func<DateTimeOffset>? utcNow = null)
    { _codex = codex; _claude = claude ?? (() => ClaudeUsageBridge.Read()); _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow); }

    internal AiAccountUsage Codex { get; private set; } = new("Codex", AiUsageState.Disabled);
    internal AiAccountUsage Claude { get; private set; } = new("Claude", AiUsageState.Disabled);
    internal event EventHandler? Updated;
    internal event Action<IReadOnlyList<AiQuotaAlert>>? QuotaLow;

    internal void ApplySettings(NotchSettings settings)
    {
        AiUsageAppearance.Apply(settings);
        var options = (settings.EnableAiUsageMonitoring, settings.TrackCodexUsage, settings.TrackClaudeUsage,
            settings.EnableLocalOnlyMode || !settings.AllowOnlineAi, settings.CodexUsageExecutable ?? "");
        CancellationTokenSource? previous;
        lock (_gate)
        {
            if (_disposed || options == _options) return;
            _options = options;
            previous = _lifetime;
            _retired.RemoveAll(t => t.IsCompleted);
            _retired.Add(_loop);
            var source = new CancellationTokenSource();
            _lifetime = source;
            CancellationToken lifetime = source.Token;
            _nextCodexRead = default;
            _nextManualCodexRead = default;
            var state = !options.Item1 ? AiUsageState.Disabled : options.Item4 ? AiUsageState.PrivacyBlocked : AiUsageState.Loading;
            Codex = new("Codex", options.Item2 ? state : AiUsageState.Disabled);
            Claude = new("Claude", options.Item3 ? state : AiUsageState.Disabled);
            _loop = options.Item1 && ((options.Item2 && !options.Item4) || options.Item3)
                ? Task.Run(() => RunAsync(lifetime)) : Task.CompletedTask;
        }
        previous?.Cancel();
        previous?.Dispose();
        Updated?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            await RefreshCoreAsync(token, wait: true).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) await RefreshCoreAsync(token, wait: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    internal Task RefreshAsync(CancellationToken token = default, bool forceCodex = false) =>
        RefreshCoreAsync(token, wait: false, forceCodex);

    private async Task RefreshCoreAsync(CancellationToken token, bool wait, bool forceCodex = false)
    {
        CancellationToken lifetime;
        lock (_gate)
        {
            if (_disposed || !_options.Enabled || _lifetime == null) return;
            lifetime = _lifetime.Token;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        if (wait) await _refresh.WaitAsync(linked.Token).ConfigureAwait(false);
        else if (!await _refresh.WaitAsync(0, linked.Token).ConfigureAwait(false)) return;
        try
        {
            (bool Enabled, bool Codex, bool Claude, bool Blocked, string Path) options;
            lock (_gate) options = _options;
            AiAccountUsage? claude = null, codex = null;
            // Claude's bridge is local; it is safe to read even when online requests are blocked.
            if (options.Claude)
            {
                try { claude = _claude(); }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
                { claude = new("Claude", AiUsageState.Unavailable); }
            }
            DateTimeOffset now = _utcNow();
            if (options.Codex && !options.Blocked &&
                (now >= _nextCodexRead || (forceCodex && now >= _nextManualCodexRead)))
            {
                // Manual refresh can see a changed account/allowance without waiting two minutes.
                // Keep a short cooldown and the same lock so repeated clicks cannot overlap reads.
                _nextCodexRead = now.AddMinutes(2);
                _nextManualCodexRead = now.AddSeconds(10);
                try { codex = await _codex.ReadAsync(options.Path, linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!linked.IsCancellationRequested) { codex = new("Codex", AiUsageState.Unavailable); }
                catch (Exception ex) when (ex is System.IO.IOException or System.ComponentModel.Win32Exception or
                    UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or System.Net.Http.HttpRequestException)
                { codex = new("Codex", AiUsageState.Unavailable); }
            }
            List<AiQuotaAlert> alerts = [];
            lock (_gate)
            {
                if (linked.IsCancellationRequested || _disposed || options != _options) return;
                if (claude != null)
                {
                    Claude = claude;
                    _quotaNotifications.Evaluate(claude, _utcNow(), alerts);
                }
                if (codex != null)
                {
                    Codex = codex;
                    _quotaNotifications.Evaluate(codex, _utcNow(), alerts);
                }
            }
            if (alerts.Count > 0) QuotaLow?.Invoke(alerts);
            Updated?.Invoke(this, EventArgs.Empty);
        }
        finally { _refresh.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        CancellationTokenSource? source;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            source = _lifetime;
            tasks = _retired.Append(_loop).ToArray();
        }
        source?.Cancel();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        lock (_gate) _quotaNotifications.Dispose();
        _lifetime?.Dispose();
    }
}

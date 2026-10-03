using System.Security.Cryptography;
using System.Text;
using System.Windows;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Spotlight;

namespace VNotch;

public partial class SpotlightWindow
{
    private AiUsageSnapshot? _usageSnapshot;
    private string? _usageIdentity;
    private string? _balanceIdentity;
    private string? _balanceText;
    private DateTimeOffset _balanceUpdated;
    private bool _balanceLoading;

    private static string UsageIdentity(NotchSettings settings)
    {
        var (key, model) = SpotlightAiService.Configuration(settings, settings.SpotlightAiProvider);
        byte[] bytes = Encoding.UTF8.GetBytes(key);
        try { return settings.SpotlightAiProvider + ":" + model + ":" + Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private void UpdateAiUsage()
    {
        string identity = UsageIdentity(_settings);
        var usage = identity == _usageIdentity ? _usageSnapshot : null;
        var parts = new List<string>();
        if (usage?.Tokens is long count)
            parts.Add(Loc.Get("spotlight.ai.usage.tokens", count.ToString("N0", Loc.GetCulture())));
        if (usage?.Remaining is long remaining)
            parts.Add(Loc.Get("spotlight.ai.usage.quota", remaining.ToString("N0", Loc.GetCulture()) +
                (usage.Limit is long limit ? " / " + limit.ToString("N0", Loc.GetCulture()) : "")));
        if (_settings.SpotlightAiProvider == "DeepSeek" && identity == _balanceIdentity &&
            !string.IsNullOrEmpty(_balanceText) && _balanceText != "—")
            parts.Add(Loc.Get("spotlight.ai.usage.balance", _balanceText));
        AiUsageText.Text = string.Join(" · ", parts);
        var visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (AiUsageRow.Visibility != visibility)
        {
            AiUsageRow.Visibility = visibility;
            ScheduleContentResize();
        }
        AiUsageText.ToolTip = Loc.Get("spotlight.ai.usage.scope") +
            (usage?.Updated is { } updated ? $"\n{updated:HH:mm:ss} · {_settings.SpotlightAiProvider}" : "") +
            (usage?.Reset is { } reset ? "\n" + Loc.Get("spotlight.ai.usage.reset", reset) : "") +
            (identity == _balanceIdentity ? "\n" + Loc.Get("spotlight.ai.usage.balanceUpdated", _balanceUpdated.ToString("T", Loc.GetCulture())) : "");
        bool hasQuota = usage?.Remaining != null && usage.Limit > 0;
        AiQuotaBar.Visibility = hasQuota ? Visibility.Visible : Visibility.Collapsed;
        AiQuotaBar.Value = hasQuota ? Math.Clamp(100d * usage!.Remaining!.Value / usage.Limit!.Value, 0, 100) : 0;
        AiUsageRefresh.Visibility = _settings.SpotlightAiProvider == "DeepSeek" ? Visibility.Visible : Visibility.Collapsed;
        AiUsageRefresh.IsEnabled = !_balanceLoading;
        AiUsageRefresh.ToolTip = Loc.Get("spotlight.ai.usage.refresh");
    }

    private async void AiUsageRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_balanceLoading || _settings.SpotlightAiProvider != "DeepSeek") return;
        if (_settings.EnableLocalOnlyMode || !_settings.AllowOnlineAi) { SetAiStatus("spotlight.ai.privacyBlocked"); return; }
        _balanceLoading = true;
        string identity = UsageIdentity(_settings);
        string key = SpotlightAiService.Configuration(_settings, "DeepSeek").Key;
        UpdateAiUsage();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            string result = await _aiService.GetBalanceAsync(key, timeout.Token);
            _balanceIdentity = identity;
            _balanceText = result;
            _balanceUpdated = DateTimeOffset.Now;
        }
        catch (Exception)
        {
            _balanceIdentity = identity;
            _balanceText = null;
            _balanceUpdated = DateTimeOffset.Now;
        }
        finally { _balanceLoading = false; UpdateAiUsage(); }
    }
}

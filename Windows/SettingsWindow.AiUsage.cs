using System.Windows;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.AiUsage;

namespace VNotch;

public partial class SettingsWindow
{
    private bool _aiUsageSubscribed;
    private bool _aiUsageRefreshing;

    private void AiUsageSettings_Loaded(object sender, RoutedEventArgs e)
    {
        if (_aiUsageSubscribed) return;
        _aiUsageSubscribed = true;
        AiAccountUsageMonitor.Shared.Updated += AiUsageMonitor_Updated;
        UpdateAiUsageStatusSummary();
    }

    private void AiUsageSettings_Unloaded(object sender, RoutedEventArgs e)
    {
        _aiUsageSubscribed = false;
        AiAccountUsageMonitor.Shared.Updated -= AiUsageMonitor_Updated;
    }

    private void AiUsageMonitor_Updated(object? sender, EventArgs e)
    {
        if (!_aiUsageSubscribed || Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => AiUsageMonitor_Updated(sender, e)); return; }
        if (PanelAiUsage.IsVisible) UpdateAiUsageStatusSummary();
    }

    private void LoadAiUsageSettings()
    {
        EnableAiUsageCheck.IsChecked = _settings.EnableAiUsageMonitoring;
        TrackCodexUsageCheck.IsChecked = _settings.TrackCodexUsage;
        TrackClaudeUsageCheck.IsChecked = _settings.TrackClaudeUsage;
        AiUsageCodexBrowseButton.ToolTip = _settings.CodexUsageExecutable;
        LoadAiUsageCustomization(_settings);
        UpdateAiUsageStatusSummary();
    }

    private void ReadAiUsageSettings(NotchSettings snapshot)
    {
        snapshot.EnableAiUsageMonitoring = EnableAiUsageCheck.IsChecked == true;
        snapshot.TrackCodexUsage = TrackCodexUsageCheck.IsChecked == true;
        snapshot.TrackClaudeUsage = TrackClaudeUsageCheck.IsChecked == true;
        ReadAiUsageCustomization(snapshot);
    }

    private void LocalizeAiUsageSettings()
    {
        AiUsageSettingsHint.Text = Loc.Get("aiUsage.hint");
        if (AiUsagePreviewLabel != null)
            AiUsagePreviewLabel.Text = Loc.Get("aiUsage.preview");
        EnableAiUsageCheck.Content = Loc.Get("aiUsage.enable");
        TrackCodexUsageCheck.Content = "Codex";
        TrackClaudeUsageCheck.Content = "Claude Code";
        AiUsageRefreshButton.Content = Loc.Get("aiUsage.refresh");
        AiUsageCodexBrowseButton.Content = Loc.Get("aiUsage.browse");
        AiUsageCodexAutoButton.Content = "Codex · " + Loc.Get("translation.autoDetect");
        AiUsageClaudeConnectButton.Content = Loc.Get("aiUsage.install");
        AiUsageClaudeRemoveButton.Content = Loc.Get("aiUsage.remove");
        AiUsageCodexManageButton.Content = "Codex · " + Loc.Get("aiUsage.manage");
        AiUsageClaudeManageButton.Content = "Claude · " + Loc.Get("aiUsage.manage");
        AiUsageGuideButton.Content = Loc.Get("aiUsage.guide");
        AiUsageAdvancedTitle.Text = Loc.Get("aiUsage.advanced");
        LocalizeAiUsageCustomization();
        AiUsageSettingsPreview.Present();
        UpdateAiUsageStatusSummary();
    }

    private void UpdateAiUsageStatusSummary()
    {
        if (AiUsageSetupStatus == null) return;
        bool hasCodex = CodexAccountUsageReader.FindExecutable(_settings.CodexUsageExecutable) != null;
        PresentAiUsageConnections(AiAccountUsageMonitor.Shared.Codex, AiAccountUsageMonitor.Shared.Claude,
            new ClaudeUsageBridge().InspectSetupState(), hasCodex);
    }

    internal void PresentAiUsageConnections(AiAccountUsage codex, AiAccountUsage claude,
        ClaudeBridgeSetupStatus claudeSetup, bool hasCodex)
    {
        string codexStatus = Loc.Get(hasCodex ? "aiUsage.statusDetected" : "aiUsage.statusMissing");
        if (codex.State == AiUsageState.Ready) codexStatus = FormatAiUsageAccountStatus(codex);
        else if (codex.State != AiUsageState.NotInstalled)
            codexStatus = Loc.Get("aiUsage.state." + codex.State);
        AiUsageCodexStatus.Text = codexStatus;

        AiUsageClaudeStatus.Text = claude.State == AiUsageState.Ready
            ? FormatAiUsageAccountStatus(claude)
            : claude.State is AiUsageState.Disabled or AiUsageState.Loading or AiUsageState.PrivacyBlocked
                ? Loc.Get("aiUsage.state." + claude.State)
                : VNotch.Controls.AiUsageNotchView.ResolveStatusDisplay(claude, claudeSetup, out _);
        AiUsageClaudeConnectButton.Content = Loc.Get(claudeSetup.MatchesBridgeCommand ? "aiUsage.statusConnected" : "aiUsage.install");
        AiUsageClaudeConnectButton.IsEnabled = !claudeSetup.MatchesBridgeCommand;
        AiUsageClaudeConnectButton.Visibility = claudeSetup.MatchesBridgeCommand ? Visibility.Collapsed : Visibility.Visible;
        AiUsageClaudeRemoveButton.IsEnabled = claudeSetup.MatchesBridgeCommand;
        TrackCodexUsageCheck.IsEnabled = TrackClaudeUsageCheck.IsEnabled = EnableAiUsageCheck.IsChecked == true;
        AiUsageQuotaRow.Visibility = EnableAiUsageCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AiUsageRefreshButton.IsEnabled = !_aiUsageRefreshing && EnableAiUsageCheck.IsChecked == true &&
            (TrackCodexUsageCheck.IsChecked == true || TrackClaudeUsageCheck.IsChecked == true);
        AiUsageRefreshButton.Content = Loc.Get(_aiUsageRefreshing ? "aiUsage.state.Loading" : "aiUsage.refresh");
    }

    private static string FormatAiUsageAccountStatus(AiAccountUsage usage)
    {
        string status = string.Join(" · ", new[] { usage.AccountLabel, usage.Plan,
            usage.UpdatedAt is { } updated ? Loc.Get("aiUsage.updated", updated.ToLocalTime().ToString("t", Loc.GetCulture())) : null }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        return string.IsNullOrWhiteSpace(status) ? Loc.Get("aiUsage.statusConnected") : status;
    }

    public void NavigateToAiUsage()
    {
        NavigateToSection(NavSectionAiUsage);
        UpdateAiUsageStatusSummary();
        Dispatcher.BeginInvoke(() => AiUsageCard.BringIntoView(new Rect(0, 0, Math.Max(1, AiUsageCard.ActualWidth), 1)));
    }

    private void AiUsageOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        if (ReferenceEquals(sender, EnableAiUsageCheck) && EnableAiUsageCheck.IsChecked == true)
        {
            string updated = SettingsMigrator.AppendCsvTokenIfMissing(_settings.VisibleNavTabs, "AiUsage");
            if (!string.Equals(updated, _settings.VisibleNavTabs, System.StringComparison.Ordinal))
            {
                _settings.VisibleNavTabs = updated;
                PopulateNavTabsSettings();
                (Application.Current?.MainWindow as MainWindow)?.ApplyNavTabOrderAndVisibility();
            }
        }
        PushLivePreview();
        UpdateAiUsageStatusSummary();
    }

    private async void AiUsageRefresh_Click(object sender, RoutedEventArgs e)
    {
        _aiUsageRefreshing = true;
        UpdateAiUsageStatusSummary();
        try { await AiAccountUsageMonitor.Shared.RefreshAsync(forceCodex: true); }
        catch (OperationCanceledException) { }
        finally
        {
            _aiUsageRefreshing = false;
            AiUsageSettingsPreview.Present();
            UpdateAiUsageStatusSummary();
        }
    }

    private void AiUsageCodexBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Codex executable|codex.exe|Executable files|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        _settings.CodexUsageExecutable = dialog.FileName;
        AiUsageCodexBrowseButton.ToolTip = dialog.FileName;
        PushLivePreview();
        UpdateAiUsageStatusSummary();
    }

    private void AiUsageCodexAuto_Click(object sender, RoutedEventArgs e)
    {
        _settings.CodexUsageExecutable = "";
        AiUsageCodexBrowseButton.ToolTip = null;
        PushLivePreview();
        UpdateAiUsageStatusSummary();
    }

    private void AiUsageClaudeConnect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = new ClaudeUsageBridge().Install();
            _ = AiAccountUsageMonitor.Shared.RefreshAsync();
            AiUsageSettingsPreview.Present();
            ShowAiUsageFeedback(result == ClaudeBridgeSetup.ExistingStatusLine ? "aiUsage.setupConflict" : "aiUsage.setupDone");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        { ShowAiUsageFeedback("aiUsage.error"); }
        UpdateAiUsageStatusSummary();
    }

    private void AiUsageClaudeRemove_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            new ClaudeUsageBridge().Remove();
            _ = AiAccountUsageMonitor.Shared.RefreshAsync();
            AiUsageSettingsPreview.Present();
            ShowAiUsageFeedback("aiUsage.removed");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        { ShowAiUsageFeedback("aiUsage.error"); }
        UpdateAiUsageStatusSummary();
    }

    private void ShowAiUsageFeedback(string key)
    {
        AiUsageSetupStatus.Text = Loc.Get(key);
        AiUsageSetupStatus.Visibility = Visibility.Visible;
    }

    private void AiUsageCodexManage_Click(object sender, RoutedEventArgs e) => SafeLauncher.TryOpenUrl("https://chatgpt.com/codex/settings/usage");
    private void AiUsageClaudeManage_Click(object sender, RoutedEventArgs e) => SafeLauncher.TryOpenUrl("https://claude.ai/settings/usage");
    private void AiUsageGuide_Click(object sender, RoutedEventArgs e) => SafeLauncher.TryOpenUrl("https://github.com/w0otdev/V-Notch/blob/main/docs/AI_ACCOUNT_USAGE.md");
}

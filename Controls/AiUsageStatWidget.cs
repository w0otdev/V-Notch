using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.AiUsage;

namespace VNotch.Controls;

/// <summary>Compact allowance readout with a shared visual hierarchy for both providers.</summary>
public sealed partial class AiUsageStatWidget : UserControl
{
    private const double ColumnWidth = 130;
    private static readonly Brush LabelBrush = UiPalette.SecondaryBrush;
    private static readonly Brush TrackBrush = Frozen(0x1C, 0xFF, 0xFF, 0xFF);
    private static readonly Brush ProviderBrush = UiPalette.PrimaryBrush;
    private readonly StackPanel _root = new() { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly AiUsageMotion _motion;
    private bool _subscribed;
    public bool IsPreview { get; set; }
    public event Action? OpenDetailsRequested;

    public AiUsageStatWidget()
    {
        AiUsageTypography.Apply(this);
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Content = _root;
        _motion = new AiUsageMotion(this);
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;
        Focusable = true;
        SetResourceReference(FocusVisualStyleProperty, SystemParameters.FocusVisualStyleKey);
        KeyboardNavigation.SetIsTabStop(this, true);
        Loaded += (_, _) =>
        {
            if (_subscribed) return;
            _subscribed = true;
            if (!IsPreview) AiAccountUsageMonitor.Shared.Updated += OnUpdated;
            Loc.LanguageChanged += OnLanguageChanged;
            AiUsageAppearance.Changed += OnAppearanceChanged;
            PresentWithEntrance();
        };
        Unloaded += (_, _) =>
        {
            _subscribed = false;
            AiAccountUsageMonitor.Shared.Updated -= OnUpdated;
            Loc.LanguageChanged -= OnLanguageChanged;
            AiUsageAppearance.Changed -= OnAppearanceChanged;
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) PresentWithEntrance(); };
        Present();
    }

    private void OnLanguageChanged() => OnUpdated(this, EventArgs.Empty);
    private void OnAppearanceChanged() => OnUpdated(this, EventArgs.Empty);

    private void OnUpdated(object? sender, EventArgs e)
    {
        if (!_subscribed || Dispatcher.HasShutdownStarted) return;
        if (Dispatcher.CheckAccess()) { if (IsVisible) Present(); }
        else Dispatcher.BeginInvoke(() => { if (_subscribed && IsVisible) Present(); });
    }

    internal void Present()
    {
        if (IsPreview) { PresentExample(); return; }
        Present(AiAccountUsageMonitor.Shared.Codex, AiAccountUsageMonitor.Shared.Claude);
    }
    private void PresentWithEntrance()
    {
        if (IsPreview) { PresentExample(); return; }
        Present(AiAccountUsageMonitor.Shared.Codex, AiAccountUsageMonitor.Shared.Claude, reveal: true);
    }
    internal void PresentExample(bool reveal = false)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Present(new("Codex", AiUsageState.Ready, now,
            [new("aiUsage.primary", 11, 300, now.AddHours(2)), new("aiUsage.secondary", 2, 10080, now.AddDays(2))], "Plus", "codex@example.com"),
            new("Claude", AiUsageState.Ready, now,
            [new("aiUsage.fiveHour", 37, 300, now.AddHours(1)), new("aiUsage.sevenDay", 21, 10080, now.AddDays(3))], "Pro"),
            now, new(true, true, true, false), reveal);
    }

    internal void Present(AiAccountUsage codex, AiAccountUsage claude, DateTimeOffset? observedNow = null,
        ClaudeBridgeSetupStatus? claudeSetup = null, bool reveal = false)
    {
        DateTimeOffset now = observedNow ?? DateTimeOffset.UtcNow;
        var setup = claudeSetup ?? new ClaudeUsageBridge().InspectSetupState();
        _motion.BeginPresentation(reveal);
        _root.Children.Clear();
        var appearance = AiUsageAppearance.Current;
        // Reserve breathing room inside the widget without growing its host column.
        _root.Margin = new Thickness(0, 0, 0, appearance.Compact ? 8 : 10);
        AiAccountUsage first = appearance.ClaudeFirst ? claude : codex;
        AiAccountUsage second = appearance.ClaudeFirst ? codex : claude;
        bool showFirst = ShouldShowAccount(first, setup);
        bool showSecond = ShouldShowAccount(second, setup);
        bool singleAccount = showFirst != showSecond;
        double interBlockSpacing = appearance.Compact ? 4 : 8;

        if (showFirst)
        {
            ResolveAccountWindows(first, out var shortWin, out var longWin);
            _root.Children.Add(CreateBlock(first, shortWin, longWin, setup, now, showSecond ? interBlockSpacing : 0, singleAccount));
        }
        if (showSecond)
        {
            ResolveAccountWindows(second, out var shortWin, out var longWin);
            _root.Children.Add(CreateBlock(second, shortWin, longWin, setup, now, 0, singleAccount));
        }
        if (!showFirst && !showSecond)
        {
            _root.Children.Add(new TextBlock
            {
                Text = Loc.Get("aiUsage.chooseProviders"),
                Width = ColumnWidth,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = LabelBrush
            });
        }
        for (int i = 0; i < _root.Children.Count; i++) _motion.Reveal((FrameworkElement)_root.Children[i], i);
        AutomationProperties.SetName(this, Loc.Get("aiUsage.title"));
        AutomationProperties.SetHelpText(this, Loc.Get("aiUsage.details"));
    }

    private static bool ShouldShowAccount(AiAccountUsage usage, ClaudeBridgeSetupStatus setup)
    {
        if (!AiUsageAppearance.Current.Shows(usage.Provider)) return false;
        if (usage.Provider != "Claude" || usage.State == AiUsageState.Ready) return true;
        // Keep a connected account during refresh or while waiting for activity.
        return setup.MatchesBridgeCommand && !setup.HasCustomApiEndpoint &&
            usage.State is AiUsageState.Waiting or AiUsageState.Loading or AiUsageState.Unavailable or AiUsageState.PrivacyBlocked;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnAiUsageWindowEntry
    {
        public byte IsCodexBucket;
        public byte MatchesFirstBucket;
        public byte HasDuration;
        public byte Reserved;
        public int DurationMinutes;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_ai_usage_resolve_account_windows(
        ReadOnlySpan<VnAiUsageWindowEntry> entries,
        int count,
        out int outShortIdx,
        out int outLongIdx);

    private static void ResolveAccountWindows(AiAccountUsage usage, out AiQuotaWindow? shortWin, out AiQuotaWindow? longWin)
    {
        shortWin = null;
        longWin = null;
        if (usage.State != AiUsageState.Ready || usage.Windows is not { Count: > 0 } windows) return;

        int count = windows.Count;
        string? firstBucket = windows[0].Bucket;
        Span<VnAiUsageWindowEntry> entries = count <= 32
            ? stackalloc VnAiUsageWindowEntry[count]
            : new VnAiUsageWindowEntry[count];

        for (int i = 0; i < count; i++)
        {
            var w = windows[i];
            entries[i] = new VnAiUsageWindowEntry
            {
                IsCodexBucket = w.Bucket == "codex" ? (byte)1 : (byte)0,
                MatchesFirstBucket = w.Bucket == firstBucket ? (byte)1 : (byte)0,
                HasDuration = w.DurationMinutes.HasValue ? (byte)1 : (byte)0,
                DurationMinutes = w.DurationMinutes ?? 0
            };
        }

        if (vn_ai_usage_resolve_account_windows(entries, count, out int shortIdx, out int longIdx) == 0)
        {
            if ((uint)shortIdx < (uint)count) shortWin = windows[shortIdx];
            if ((uint)longIdx < (uint)count) longWin = windows[longIdx];
        }
    }

    private static (string text, Brush fill, double percent) Measure(AiAccountUsage usage, AiQuotaWindow? window, DateTimeOffset now)
    {
        if (window is not { } w) return (usage.State == AiUsageState.Loading ? "…" : "—", UiPalette.TertiaryBrush, 0);
        VNotchNative.vn_ai_usage_compute_window_visual(
            w.UsedPercent, w.DurationMinutes ?? 0,
            w.ResetsAt.HasValue ? (byte)1 : (byte)0, w.ResetsAt?.UtcDateTime.Ticks ?? 0L,
            usage.UpdatedAt.HasValue ? (byte)1 : (byte)0, usage.UpdatedAt?.UtcDateTime.Ticks ?? 0L,
            now.UtcDateTime.Ticks, out var v);
        if (v.IsExpired != 0) return ("—", UiPalette.TertiaryBrush, 0);
        Brush fill = AiUsageAppearance.Current.Fill(usage.Provider, v.ClampedUsedPercent);
        double percent = AiUsageAppearance.Current.ShowUsed ? v.ClampedUsedPercent : v.ClampedRemainingPercent;
        return (percent.ToString("0.#", Loc.GetCulture()) + "%", fill, percent);
    }

    private static string BuildToolTip(AiAccountUsage usage, AiQuotaWindow? shortWin, AiQuotaWindow? longWin, ClaudeBridgeSetupStatus claudeSetup, DateTimeOffset now)
    {
        var appearance = AiUsageAppearance.Current;
        string head = !appearance.ShowPlan || string.IsNullOrWhiteSpace(usage.Plan) ? usage.Provider : usage.Provider + " · " + AiUsageTypography.PlanName(usage.Plan);
        if (appearance.ShowAccount && !string.IsNullOrWhiteSpace(usage.AccountLabel)) head += "\n" + usage.AccountLabel;
        if (usage.State != AiUsageState.Ready)
            return head + "\n" + AiUsageNotchView.ResolveStatusDisplay(usage, claudeSetup, out _);

        var lines = new List<string> { head };
        var culture = Loc.GetCulture();
        AddWindow(shortWin);
        if (appearance.ShowWeekly) AddWindow(longWin);
        if (usage.UpdatedAt is { } updated)
            lines.Add(Loc.Get("aiUsage.updated", updated.ToLocalTime().ToString("g", culture)));
        if (usage.IsStale(now)) lines.Add(Loc.Get("aiUsage.stale"));
        lines.Add(Loc.Get("aiUsage.details"));
        return string.Join("\n", lines);

        void AddWindow(AiQuotaWindow? window)
        {
            if (window == null) return;
            string label = Loc.Get(window.DurationMinutes == 10080 ? "aiUsage.sevenDay" : window.LabelKey);
            if (window.IsExpired(now))
            {
                lines.Add(label + ": " + Loc.Get("aiUsage.expired"));
                return;
            }
            double percent = appearance.Percent(window);
            lines.Add(label + ": " + appearance.PercentText(percent) + " (" +
                Loc.Get(appearance.ShowUsed ? "aiUsage.left" : "aiUsage.used", (100 - percent).ToString("0.#", culture)) + ")");
            if (appearance.ShowResets && window.ResetsAt is { } reset)
                lines.Add(Loc.Get("aiUsage.resets", reset.ToLocalTime().ToString("g", culture)));
        }
    }

    private StackPanel CreateBlock(AiAccountUsage usage, AiQuotaWindow? window, AiQuotaWindow? longWindow, ClaudeBridgeSetupStatus claudeSetup, DateTimeOffset now, double bottom, bool singleAccount)
    {
        bool compact = AiUsageAppearance.Current.Compact;
        bool expanded = singleAccount && usage.State == AiUsageState.Ready && AiUsageAppearance.Current.ShowWeekly &&
            (window != null || longWindow != null);
        var block = new StackPanel
        {
            Name = usage.Provider + "Subscription",
            Orientation = Orientation.Vertical,
            Margin = new Thickness(0, 0, 0, bottom),
            ToolTip = BuildToolTip(usage, window, longWindow, claudeSetup, now)
        };
        var head = new Grid { Width = ColumnWidth };
        head.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (AiUsageAppearance.Current.ShowLogos)
            brand.Children.Add(new Image { Source = AiProviderBrand.For(usage.Provider), Width = compact ? 10 : 12, Height = compact ? 10 : 12,
                Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
        brand.Children.Add(new TextBlock { Text = usage.Provider, FontSize = compact ? 10 : 11, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center, Foreground = SystemParameters.HighContrast ? SystemColors.ControlTextBrush : ProviderBrush });
        head.Children.Add(brand);
        var valueText = expanded
            ? new TextBlock { Name = "SubscriptionPlan", Text = AiUsageAppearance.Current.ShowPlan ? AiUsageTypography.PlanName(usage.Plan) : "",
                FontSize = compact ? 9 : 10, Foreground = LabelBrush, TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 70, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center }
            : CreateAllowance(usage, window, now, "PrimaryAllowance");
        Grid.SetColumn(valueText, 1);
        head.Children.Add(valueText);
        block.Children.Add(head);
        if (expanded)
        {
            block.Children.Add(CreateWindowRow(usage, window, now, weekly: false));
            block.Children.Add(CreateWindowRow(usage, longWindow, now, weekly: true));
        }
        else
        {
            block.Children.Add(CreateTrack(usage, window, now, weekly: false));
            block.Children.Add(CreateSummary(usage, window, longWindow, claudeSetup, now));
        }
        return block;
    }

    private static TextBlock CreateAllowance(AiAccountUsage usage, AiQuotaWindow? window, DateTimeOffset now, string name)
    {
        bool compact = AiUsageAppearance.Current.Compact;
        var (text, _, percent) = Measure(usage, window, now);
        bool available = window != null && !window.IsExpired(now);
        string value = available ? AiUsageAppearance.Current.PercentText(percent) : text;
        var label = new TextBlock { Name = name, Text = value, FontSize = compact ? 8 : 9,
            Margin = new Thickness(4, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = UiPalette.PrimaryBrush, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        if (available) AiUsageTypography.EmphasizePercent(label, value, percent, compact ? 12 : 16);
        else label.FontSize = compact ? 12 : 16;
        return label;
    }

    private StackPanel CreateWindowRow(AiAccountUsage usage, AiQuotaWindow? window, DateTimeOffset now, bool weekly)
    {
        bool compact = AiUsageAppearance.Current.Compact;
        var row = new StackPanel { Name = weekly ? "WeeklyQuota" : "PrimaryQuota", Width = ColumnWidth,
            Margin = new Thickness(0, weekly ? compact ? 6 : 8 : compact ? 4 : 6, 0, 0) };
        var line = new Grid();
        line.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        line.Children.Add(new TextBlock { Name = weekly ? "WeeklyWindowLabel" : "PrimaryWindowLabel",
            Text = weekly ? Loc.Get("aiUsage.compactSevenDay") : WindowDuration(window),
            FontSize = compact ? 9 : 10, Foreground = LabelBrush, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis });
        var value = CreateAllowance(usage, window, now, weekly ? "WeeklyAllowance" : "PrimaryAllowance");
        Grid.SetColumn(value, 1);
        line.Children.Add(value);
        row.Children.Add(line);
        row.Children.Add(CreateTrack(usage, window, now, weekly));
        return row;
    }

    private Border CreateTrack(AiAccountUsage usage, AiQuotaWindow? window, DateTimeOffset now, bool weekly)
    {
        bool compact = AiUsageAppearance.Current.Compact;
        var (_, fill, percent) = Measure(usage, window, now);
        int height = AiUsageAppearance.Current.BarHeight;
        var track = new Border { Name = weekly ? "WeeklyQuotaTrack" : "PrimaryQuotaTrack", Width = ColumnWidth, Height = height,
            CornerRadius = new CornerRadius(height / 2.0), Margin = new Thickness(0, compact ? 2 : 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, Background = TrackBrush, ClipToBounds = true };
        var fillBorder = new Border { Name = weekly ? "WeeklyQuotaFill" : "PrimaryQuotaFill", Width = ColumnWidth, Height = height, CornerRadius = new CornerRadius(height / 2.0),
            HorizontalAlignment = HorizontalAlignment.Left, Background = fill };
        AiUsageAppearance.Current.BindFill(fillBorder, usage.Provider, window?.UsedPercent ?? 0);
        _motion.BindBar(fillBorder, usage.Provider + (weekly ? ":weekly" : ":compact"), percent, window != null && !window.IsExpired(now));
        track.Child = fillBorder;
        return track;
    }

    private static Grid CreateSummary(AiAccountUsage usage, AiQuotaWindow? window, AiQuotaWindow? weekly,
        ClaudeBridgeSetupStatus setup, DateTimeOffset now)
    {
        bool compact = AiUsageAppearance.Current.Compact;
        var summary = new Grid { Name = "SubscriptionSummary", Width = ColumnWidth,
            Margin = new Thickness(0, compact ? 1 : 3, 0, 0) };
        summary.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        summary.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        summary.Children.Add(new TextBlock
        {
            Name = "SubscriptionPlan", Text = Summary(usage, window, setup, now), FontSize = compact ? 8.5 : 9.5,
            Foreground = LabelBrush, TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (AiUsageAppearance.Current.ShowWeekly && usage.State == AiUsageState.Ready)
        {
            var week = new TextBlock { Name = "WeeklySummary", Text = Loc.Get("aiUsage.compactSevenDay") + " " + Measure(usage, weekly, now).text,
                FontSize = compact ? 8.5 : 9.5, Foreground = LabelBrush, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(6, 0, 0, 0) };
            Grid.SetColumn(week, 1);
            summary.Children.Add(week);
        }
        return summary;
    }

    private static string WindowDuration(AiQuotaWindow? window) => window?.DurationMinutes switch
    {
        null or 300 => Loc.Get("aiUsage.compactFiveHour"),
        { } minutes => Loc.Get("aiUsage.minutes", minutes)
    };

    private static string Summary(AiAccountUsage usage, AiQuotaWindow? window,
        ClaudeBridgeSetupStatus setup, DateTimeOffset now)
    {
        if (usage.State == AiUsageState.Ready)
        {
            if (window?.IsExpired(now) == true) return Loc.Get("aiUsage.waitingShort");
            string plan = AiUsageAppearance.Current.ShowPlan && !string.IsNullOrWhiteSpace(usage.Plan) ? AiUsageTypography.PlanName(usage.Plan) + " · " : "";
            if (window == null) return Loc.Get("aiUsage.chooseProviders");
            return plan + WindowDuration(window);
        }
        return Loc.Get(usage.State switch
        {
            AiUsageState.Waiting when setup.HasCustomApiEndpoint => "aiUsage.apiShort",
            AiUsageState.Waiting when !setup.MatchesBridgeCommand => "aiUsage.connectShort",
            AiUsageState.Waiting => "aiUsage.activityShort",
            AiUsageState.SignedOut => "aiUsage.signIn",
            AiUsageState.NotInstalled => "aiUsage.statusMissing",
            AiUsageState.Unsupported => "aiUsage.noPlanShort",
            AiUsageState.Unavailable => "aiUsage.refresh",
            _ => "aiUsage.state." + usage.State
        });
    }

    internal void RequestOpenDetails() => OpenDetailsRequested?.Invoke();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            if (!e.IsRepeat) RequestOpenDetails();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new UsageWidgetPeer(this);

    private sealed class UsageWidgetPeer(AiUsageStatWidget owner) : UserControlAutomationPeer(owner), IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        public override object? GetPattern(PatternInterface pattern) => pattern == PatternInterface.Invoke ? this : base.GetPattern(pattern);
        public void Invoke()
        {
            if (!owner.IsEnabled) throw new ElementNotEnabledException();
            owner.Dispatcher.BeginInvoke(new Action(owner.RequestOpenDetails));
        }
    }

    private static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}

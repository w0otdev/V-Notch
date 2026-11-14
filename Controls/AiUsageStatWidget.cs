using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Documents;
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
    private readonly Dictionary<string, double> _prevPercents = new(StringComparer.Ordinal);
    private bool _subscribed;
    private bool _hasPresentedAppearance;
    private AiUsageAppearance _lastPreviewAppearance = AiUsageAppearance.Current;
    private string _previewTooltipSummary = string.Empty;
    public bool IsPreview { get; set; }
    internal string PreviewTooltipSummary => _previewTooltipSummary;
    public event Action? OpenDetailsRequested;

    private readonly record struct PreviewTransition(
        bool OrderFlipped,
        bool CompactChanged,
        bool MetricChanged,
        bool LogosTurnedOn,
        bool WeeklyTurnedOn,
        bool PlanTurnedOn,
        bool BarHeightChanged,
        int OldBarHeight,
        bool ColorsChanged,
        bool MotionChanged);

    private sealed class PreviewSnapshot
    {
        public readonly Dictionary<string, Rect> BlockBounds = new(StringComparer.Ordinal);
        public readonly Dictionary<string, Rect> LocalBounds = new(StringComparer.Ordinal);
        public readonly Dictionary<string, Color> FillColors = new(StringComparer.Ordinal);
        public readonly Dictionary<string, double> LivePercents = new(StringComparer.Ordinal);
    }

    private sealed class PreviewBuildContext
    {
        public readonly List<TrackAnimTarget> Tracks = [];
        public readonly List<NumberAnimTarget> Numbers = [];
        public TextBlock? EmptyText;
        public string? PlanNameCodex;
        public string? PlanNameClaude;
    }

    private readonly record struct TrackAnimTarget(
        Border Fill, string FillKey, Color TargetColor, Action RestoreBind, int RowIndex, bool IsWeekly, bool IsNewBlock);

    private readonly record struct NumberAnimTarget(
        DependencyObject Target, string Key, double TargetPercent, Func<double, string> Formatter, string FinalText, int DelayMs);

    public AiUsageStatWidget()
    {
        AiUsageTypography.Apply(this);
        SnapsToDevicePixels = false;
        UseLayoutRounding = false;
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
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) PresentWithEntrance();
            else if (IsPreview) _hasPresentedAppearance = false;
        };
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
        var appearance = AiUsageAppearance.Current;
        bool animateChanges = IsPreview && _hasPresentedAppearance && _motion.CanAnimateNow;
        PreviewSnapshot? snapshot = animateChanges && _root.IsLoaded && _root.Children.Count > 0 && _root.IsArrangeValid
            ? CapturePreviewSnapshot()
            : null;

        _motion.BeginPresentation(reveal);
        _root.Children.Clear();
        _previewTooltipSummary = string.Empty;
        // Reserve breathing room inside the widget without growing its host column.
        _root.Margin = new Thickness(0, 0, 0, appearance.Compact ? 8 : 10);
        AiAccountUsage first = appearance.ClaudeFirst ? claude : codex;
        AiAccountUsage second = appearance.ClaudeFirst ? codex : claude;
        bool showFirst = ShouldShowAccount(first, setup);
        bool showSecond = ShouldShowAccount(second, setup);
        bool singleAccount = showFirst != showSecond;
        double interBlockSpacing = appearance.Compact ? 4 : 8;

        PreviewTransition tx = default;
        if (animateChanges)
        {
            var prev = _lastPreviewAppearance;
            tx = new PreviewTransition(
                OrderFlipped: prev.ClaudeFirst != appearance.ClaudeFirst && showFirst && showSecond,
                CompactChanged: prev.Compact != appearance.Compact,
                MetricChanged: prev.ShowUsed != appearance.ShowUsed,
                LogosTurnedOn: !prev.ShowLogos && appearance.ShowLogos,
                WeeklyTurnedOn: !prev.ShowWeekly && appearance.ShowWeekly,
                PlanTurnedOn: !prev.ShowPlan && appearance.ShowPlan,
                BarHeightChanged: prev.BarHeight != appearance.BarHeight,
                OldBarHeight: prev.BarHeight,
                ColorsChanged: prev.StatusColors != appearance.StatusColors ||
                    !string.Equals(prev.CodexColor, appearance.CodexColor, StringComparison.Ordinal) ||
                    !string.Equals(prev.ClaudeColor, appearance.ClaudeColor, StringComparison.Ordinal),
                MotionChanged: prev.MotionIntensity != appearance.MotionIntensity && appearance.MotionIntensity > 0);
        }

        PreviewBuildContext? build = snapshot != null ? new PreviewBuildContext() : null;
        Dictionary<string, double>? nextPercents = IsPreview ? new Dictionary<string, double>(StringComparer.Ordinal) : null;

        if (showFirst)
        {
            ResolveAccountWindows(first, out var shortWin, out var longWin);
            RecordPercents(nextPercents, first, shortWin, longWin, now);
            bool isNew = snapshot != null && !snapshot.BlockBounds.ContainsKey(first.Provider);
            _root.Children.Add(CreateBlock(first, shortWin, longWin, setup, now, showSecond ? interBlockSpacing : 0, singleAccount, isNew, build));
            if (IsPreview) _previewTooltipSummary = BuildSummaryText(first, shortWin, longWin, setup, now, includeWeekly: false, includeFooter: false);
        }
        if (showSecond)
        {
            ResolveAccountWindows(second, out var shortWin, out var longWin);
            RecordPercents(nextPercents, second, shortWin, longWin, now);
            bool isNew = snapshot != null && !snapshot.BlockBounds.ContainsKey(second.Provider);
            _root.Children.Add(CreateBlock(second, shortWin, longWin, setup, now, 0, singleAccount, isNew, build));
            if (IsPreview && string.IsNullOrEmpty(_previewTooltipSummary))
                _previewTooltipSummary = BuildSummaryText(second, shortWin, longWin, setup, now, includeWeekly: false, includeFooter: false);
        }
        if (!showFirst && !showSecond)
        {
            var emptyText = new TextBlock
            {
                Text = Loc.Get("aiUsage.chooseProviders"),
                Width = ColumnWidth,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = LabelBrush
            };
            _root.Children.Add(emptyText);
            if (build != null) build.EmptyText = emptyText;
        }

        if (snapshot != null && build != null)
        {
            _root.Measure(new Size(ColumnWidth, double.PositiveInfinity));
            _root.Arrange(new Rect(_root.DesiredSize));
            ApplyFlipChoreography(snapshot, build, tx, appearance);
        }

        if (IsPreview && IsLoaded && IsVisible)
        {
            _hasPresentedAppearance = true;
            _lastPreviewAppearance = appearance;
            _prevPercents.Clear();
            if (nextPercents != null)
                foreach (var (k, v) in nextPercents) _prevPercents[k] = v;
        }
        for (int i = 0; i < _root.Children.Count; i++) _motion.Reveal((FrameworkElement)_root.Children[i], i);
        AutomationProperties.SetName(this, Loc.Get("aiUsage.title"));
        AutomationProperties.SetHelpText(this, Loc.Get("aiUsage.details"));
    }

    private static void RecordPercents(Dictionary<string, double>? dest, AiAccountUsage usage,
        AiQuotaWindow? shortWin, AiQuotaWindow? longWin, DateTimeOffset now)
    {
        if (dest == null) return;
        if (shortWin != null && !shortWin.IsExpired(now))
            dest[usage.Provider + ":primary"] = Measure(usage, shortWin, now).percent;
        if (longWin != null && !longWin.IsExpired(now))
            dest[usage.Provider + ":weekly"] = Measure(usage, longWin, now).percent;
    }

    private PreviewSnapshot CapturePreviewSnapshot()
    {
        var snapshot = new PreviewSnapshot();
        foreach (UIElement child in _root.Children)
        {
            if (child is not StackPanel block) continue;
            string provider = block.Name == "ClaudeSubscription" ? "Claude" : "Codex";
            if (!TryTransformBounds(block, _root, out Rect blockBounds)) continue;
            snapshot.BlockBounds[provider] = blockBounds;
            if (_prevPercents.TryGetValue(provider + ":primary", out double prevPrimary))
                snapshot.LivePercents[provider + ":primary"] = prevPrimary;
            if (_prevPercents.TryGetValue(provider + ":weekly", out double prevWeekly))
                snapshot.LivePercents[provider + ":weekly"] = prevWeekly;
            WalkBlockElements(block, el =>
            {
                if (TryTransformBounds(el, block, out Rect localBounds))
                    snapshot.LocalBounds[provider + ":" + el.Name] = localBounds;
                if (el is Border { Name: "PrimaryQuotaFill" or "WeeklyQuotaFill", Background: SolidColorBrush brush })
                    snapshot.FillColors[provider + ":" + el.Name] = brush.Color;
                else if (el is TextBlock tb)
                {
                    if (el.Name == "PrimaryAllowance" && FindPercentRun(tb) is { } pRun && TryParsePercentText(pRun.Text, out double pVal))
                        snapshot.LivePercents[provider + ":primary"] = pVal;
                    else if (el.Name == "WeeklyAllowance" && FindPercentRun(tb) is { } wRun && TryParsePercentText(wRun.Text, out double wVal))
                        snapshot.LivePercents[provider + ":weekly"] = wVal;
                    else if (el.Name == "WeeklySummary" && TryParseTrailingPercent(tb.Text, out double wsVal))
                        snapshot.LivePercents[provider + ":weekly"] = wsVal;
                }
            });
        }
        return snapshot;
    }

    private void ApplyFlipChoreography(PreviewSnapshot snapshot, PreviewBuildContext build, PreviewTransition tx, AiUsageAppearance appearance)
    {
        if (build.EmptyText != null)
        {
            _motion.AnimateSpringTransform(build.EmptyText, 0, 10, fromScale: 0.95, fromOpacity: 0.0, durationMs: 260);
            return;
        }

        for (int blockIndex = 0; blockIndex < _root.Children.Count; blockIndex++)
        {
            if (_root.Children[blockIndex] is not StackPanel block) continue;
            string provider = block.Name == "ClaudeSubscription" ? "Claude" : "Codex";
            bool hadPrevBlock = snapshot.BlockBounds.TryGetValue(provider, out Rect oldBlock);
            if (!hadPrevBlock)
            {
                Panel.SetZIndex(block, 2);
                _motion.AnimateSpringTransform(block, 0, 14, fromScale: 0.90, fromOpacity: 0.0, durationMs: 340, delayMs: blockIndex * 40);
                continue;
            }

            if (TryTransformBounds(block, _root, out Rect newBlock))
            {
                double bdx = oldBlock.Left - newBlock.Left;
                double bdy = oldBlock.Top - newBlock.Top;
                if (tx.OrderFlipped)
                {
                    bool rising = bdy > 0;
                    Panel.SetZIndex(block, rising ? 2 : 1);
                    _motion.AnimateSpringTransform(block, bdx, bdy, fromScale: rising ? 1.05 : 0.94,
                        fromOpacity: rising ? 1.0 : 0.86, durationMs: 360);
                }
                else if (Moved(bdx, bdy))
                {
                    Panel.SetZIndex(block, 0);
                    _motion.AnimateSpringTransform(block, bdx, bdy, durationMs: 300);
                }
                else
                {
                    Panel.SetZIndex(block, 0);
                    if (tx.CompactChanged)
                        _motion.AnimateSpringTransform(block, 0, 0, fromScale: appearance.Compact ? 1.025 : 0.975, fromOpacity: 0.90, durationMs: 260);
                }
            }

            bool weeklyRowIsNew = !snapshot.LocalBounds.ContainsKey(provider + ":WeeklyQuota");
            WalkBlockElements(block, el =>
            {
                if (el.Name is "PrimaryQuota" or "SubscriptionSummary" or "PrimaryQuotaFill" or "WeeklyQuotaFill") return;
                if (weeklyRowIsNew && el.Name is "WeeklyWindowLabel" or "WeeklyAllowance" or "WeeklyQuotaTrack") return;
                if (!TryTransformBounds(el, block, out Rect newLocal)) return;

                string fullKey = provider + ":" + el.Name;
                bool hasPrev = snapshot.LocalBounds.TryGetValue(fullKey, out Rect oldLocal);
                double dx = hasPrev ? oldLocal.Left - newLocal.Left : 0;
                double dy = hasPrev ? oldLocal.Top - newLocal.Top : 0;

                switch (el.Name)
                {
                    case "ProviderLogo":
                        if (!hasPrev)
                            _motion.AnimateSpringTransform(el, 0, 0, fromScale: 0.12, fromOpacity: 0.0, fromAngle: -28, durationMs: 340, delayMs: blockIndex * 45);
                        else if (Moved(dx, dy))
                            _motion.AnimateSpringTransform(el, dx, dy, durationMs: 280);
                        break;
                    case "ProviderTitle":
                        if (Moved(dx, dy))
                            _motion.AnimateSpringTransform(el, dx, dy, durationMs: 300, delayMs: tx.LogosTurnedOn ? 15 : 0);
                        break;
                    case "PrimaryQuotaTrack":
                    case "WeeklyQuotaTrack":
                        double scaleY = tx.BarHeightChanged ? (double)tx.OldBarHeight / Math.Max(1, appearance.BarHeight) : 1.0;
                        if (Moved(dx, dy) || Math.Abs(scaleY - 1.0) > 0.01)
                            _motion.AnimateSpringTransform(el, dx, dy, durationMs: 300, scaleFromTop: tx.BarHeightChanged, fromScaleY: scaleY);
                        break;
                    case "WeeklyQuota":
                        if (!hasPrev)
                            _motion.AnimateSpringTransform(el, 0, 10, fromScale: 0.94, fromOpacity: 0.0, durationMs: 320, delayMs: 35);
                        break;
                    case "WeeklySummary":
                        if (!hasPrev)
                            _motion.AnimateSpringTransform(el, 0, 7, fromScale: 0.88, fromOpacity: 0.0, durationMs: 290, delayMs: blockIndex * 45);
                        else if (Moved(dx, dy))
                            _motion.AnimateSpringTransform(el, dx, dy, durationMs: 280);
                        break;
                    case "SubscriptionPlan":
                        string? plan = provider == "Claude" ? build.PlanNameClaude : build.PlanNameCodex;
                        if (tx.PlanTurnedOn && !string.IsNullOrWhiteSpace(plan))
                            _motion.AnimateSpringTransform(el, dx - 8, dy, fromScale: 0.92, fromOpacity: 0.30, durationMs: 280);
                        else if (Moved(dx, dy))
                            _motion.AnimateSpringTransform(el, dx, dy, durationMs: 280);
                        break;
                    case "PrimaryAllowance":
                    case "WeeklyAllowance":
                        if (tx.MetricChanged)
                            _motion.AnimateSpringTransform(el, dx, dy + (appearance.ShowUsed ? -5 : 5), fromOpacity: 0.65, durationMs: 280);
                        else if (Moved(dx, dy))
                            _motion.AnimateSpringTransform(el, dx, dy, durationMs: 280);
                        break;
                    default:
                        if (Moved(dx, dy))
                            _motion.AnimateSpringTransform(el, dx, dy, durationMs: 280);
                        break;
                }
            });
        }

        foreach (var track in build.Tracks)
        {
            if (tx.ColorsChanged && snapshot.FillColors.TryGetValue(track.FillKey, out Color oldColor) && oldColor != track.TargetColor)
                _motion.AnimateColorBrush(track.Fill, oldColor, track.TargetColor, track.RestoreBind, durationMs: 340);

            if (tx.MotionChanged)
                _motion.PulseBar(track.Fill, fromProgress: 0.08, durationMs: 360, delayMs: track.RowIndex * 60);
            else if (track.IsNewBlock || (tx.WeeklyTurnedOn && track.IsWeekly))
                _motion.PulseBar(track.Fill, fromProgress: 0.0, durationMs: 340, delayMs: 45 + track.RowIndex * 50);
            else if (tx.ColorsChanged || tx.BarHeightChanged)
                _motion.PulseBar(track.Fill, fromProgress: null, durationMs: 300, delayMs: track.RowIndex * 40);
        }

        foreach (var num in build.Numbers)
        {
            if (!snapshot.LivePercents.TryGetValue(num.Key, out double prevValue)) continue;
            if (Math.Abs(prevValue - num.TargetPercent) > 0.2)
                _motion.AnimateNumber(num.Target, prevValue, num.TargetPercent, num.Formatter, num.FinalText, durationMs: 340, delayMs: num.DelayMs);
        }
    }

    private static bool Moved(double dx, double dy) => Math.Abs(dx) > 0.4 || Math.Abs(dy) > 0.4;

    private static bool TryTransformBounds(FrameworkElement element, Visual ancestor, out Rect bounds)
    {
        if (!ancestor.IsAncestorOf(element) || (element.RenderSize.Width <= 0 && element.RenderSize.Height <= 0))
        {
            bounds = default;
            return false;
        }
        bounds = element.TransformToAncestor(ancestor).TransformBounds(new Rect(new Point(0, 0), element.RenderSize));
        return true;
    }

    private static void WalkBlockElements(DependencyObject parent, Action<FrameworkElement> visit)
    {
        if (parent is Panel panel)
        {
            foreach (UIElement child in panel.Children)
            {
                if (child is FrameworkElement fe)
                {
                    if (!string.IsNullOrEmpty(fe.Name)) visit(fe);
                    WalkBlockElements(fe, visit);
                }
            }
        }
        else if (parent is Border { Child: FrameworkElement child })
        {
            if (!string.IsNullOrEmpty(child.Name)) visit(child);
            WalkBlockElements(child, visit);
        }
    }

    private static Run? FindPercentRun(TextBlock text)
    {
        foreach (Inline inline in text.Inlines)
            if (inline is Run run && run.FontSize > text.FontSize + 0.5) return run;
        return null;
    }

    private static bool TryParsePercentText(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string trimmed = text.Trim().TrimEnd('%');
        return double.TryParse(trimmed, Loc.GetCulture(), out value);
    }

    private static bool TryParseTrailingPercent(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        int space = text.LastIndexOf(' ');
        return space >= 0 && TryParsePercentText(text[(space + 1)..], out value);
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

    private static string BuildToolTip(AiAccountUsage usage, AiQuotaWindow? shortWin, AiQuotaWindow? longWin, ClaudeBridgeSetupStatus claudeSetup, DateTimeOffset now) =>
        BuildSummaryText(usage, shortWin, longWin, claudeSetup, now, includeWeekly: true, includeFooter: true);

    private static string BuildSummaryText(AiAccountUsage usage, AiQuotaWindow? shortWin, AiQuotaWindow? longWin,
        ClaudeBridgeSetupStatus claudeSetup, DateTimeOffset now, bool includeWeekly, bool includeFooter)
    {
        var appearance = AiUsageAppearance.Current;
        string head = !appearance.ShowPlan || string.IsNullOrWhiteSpace(usage.Plan) ? usage.Provider : usage.Provider + " · " + AiUsageTypography.PlanName(usage.Plan);
        if (appearance.ShowAccount && !string.IsNullOrWhiteSpace(usage.AccountLabel)) head += "\n" + usage.AccountLabel;
        if (usage.State != AiUsageState.Ready)
            return head + "\n" + AiUsageNotchView.ResolveStatusDisplay(usage, claudeSetup, out _);

        var lines = new List<string> { head };
        var culture = Loc.GetCulture();
        AddWindow(shortWin);
        if (includeWeekly && appearance.ShowWeekly) AddWindow(longWin);
        if (includeFooter && usage.UpdatedAt is { } updated)
            lines.Add(Loc.Get("aiUsage.updated", updated.ToLocalTime().ToString("g", culture)));
        if (includeFooter && usage.IsStale(now)) lines.Add(Loc.Get("aiUsage.stale"));
        if (includeFooter) lines.Add(Loc.Get("aiUsage.details"));
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

    private StackPanel CreateBlock(AiAccountUsage usage, AiQuotaWindow? window, AiQuotaWindow? longWindow,
        ClaudeBridgeSetupStatus claudeSetup, DateTimeOffset now, double bottom, bool singleAccount, bool isNewBlock, PreviewBuildContext? build)
    {
        var appearance = AiUsageAppearance.Current;
        bool compact = appearance.Compact;
        bool expanded = singleAccount && usage.State == AiUsageState.Ready && appearance.ShowWeekly &&
            (window != null || longWindow != null);
        var block = new StackPanel
        {
            Name = usage.Provider + "Subscription",
            Orientation = Orientation.Vertical,
            Margin = new Thickness(0, 0, 0, bottom),
            ToolTip = BuildToolTip(usage, window, longWindow, claudeSetup, now)
        };
        if (build != null)
        {
            if (usage.Provider == "Claude") build.PlanNameClaude = usage.Plan;
            else build.PlanNameCodex = usage.Plan;
        }

        var head = new Grid { Width = ColumnWidth };
        head.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (appearance.ShowLogos)
        {
            brand.Children.Add(new Image
            {
                Name = "ProviderLogo",
                Source = AiProviderBrand.For(usage.Provider),
                Width = compact ? 10 : 12,
                Height = compact ? 10 : 12,
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            });
        }
        brand.Children.Add(new TextBlock
        {
            Name = "ProviderTitle",
            Text = usage.Provider,
            FontSize = compact ? 10 : 11,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = SystemParameters.HighContrast ? SystemColors.ControlTextBrush : ProviderBrush
        });
        head.Children.Add(brand);
        var valueText = expanded
            ? new TextBlock { Name = "SubscriptionPlan", Text = appearance.ShowPlan ? AiUsageTypography.PlanName(usage.Plan) : "",
                FontSize = compact ? 9 : 10, Foreground = LabelBrush, TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 70, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center }
            : CreateAllowance(usage, window, now, "PrimaryAllowance", weekly: false, build);
        Grid.SetColumn(valueText, 1);
        head.Children.Add(valueText);
        block.Children.Add(head);
        if (expanded)
        {
            block.Children.Add(CreateWindowRow(usage, window, now, weekly: false, isNewBlock, build));
            block.Children.Add(CreateWindowRow(usage, longWindow, now, weekly: true, isNewBlock, build));
        }
        else
        {
            block.Children.Add(CreateTrack(usage, window, now, weekly: false, isNewBlock, build));
            block.Children.Add(CreateSummary(usage, window, longWindow, claudeSetup, now, build));
        }
        return block;
    }

    private static TextBlock CreateAllowance(AiAccountUsage usage, AiQuotaWindow? window, DateTimeOffset now,
        string name, bool weekly, PreviewBuildContext? build)
    {
        bool compact = AiUsageAppearance.Current.Compact;
        var (text, _, percent) = Measure(usage, window, now);
        bool available = window != null && !window.IsExpired(now);
        string value = available ? AiUsageAppearance.Current.PercentText(percent) : text;
        var label = new TextBlock { Name = name, Text = value, FontSize = compact ? 8 : 9,
            Margin = new Thickness(4, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = UiPalette.PrimaryBrush, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        if (available)
        {
            AiUsageTypography.EmphasizePercent(label, value, percent, compact ? 12 : 16);
            if (build != null && FindPercentRun(label) is { } numberRun)
            {
                var culture = Loc.GetCulture();
                string finalNum = percent.ToString("0.#", culture) + "%";
                build.Numbers.Add(new NumberAnimTarget(
                    numberRun,
                    usage.Provider + (weekly ? ":weekly" : ":primary"),
                    percent,
                    v => Math.Round(v).ToString("0", culture) + "%",
                    finalNum,
                    weekly ? 35 : 0));
            }
        }
        else label.FontSize = compact ? 12 : 16;
        return label;
    }

    private StackPanel CreateWindowRow(AiAccountUsage usage, AiQuotaWindow? window, DateTimeOffset now,
        bool weekly, bool isNewBlock, PreviewBuildContext? build)
    {
        var appearance = AiUsageAppearance.Current;
        bool compact = appearance.Compact;
        var row = new StackPanel { Name = weekly ? "WeeklyQuota" : "PrimaryQuota", Width = ColumnWidth,
            Margin = new Thickness(0, weekly ? compact ? 6 : 8 : compact ? 4 : 6, 0, 0) };
        var line = new Grid();
        line.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        line.Children.Add(new TextBlock { Name = weekly ? "WeeklyWindowLabel" : "PrimaryWindowLabel",
            Text = weekly ? Loc.Get("aiUsage.compactSevenDay") : WindowDuration(window),
            FontSize = compact ? 9 : 10, Foreground = LabelBrush, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis });
        var value = CreateAllowance(usage, window, now, weekly ? "WeeklyAllowance" : "PrimaryAllowance", weekly, build);
        Grid.SetColumn(value, 1);
        line.Children.Add(value);
        row.Children.Add(line);
        row.Children.Add(CreateTrack(usage, window, now, weekly, isNewBlock, build));
        return row;
    }

    private Border CreateTrack(AiAccountUsage usage, AiQuotaWindow? window, DateTimeOffset now,
        bool weekly, bool isNewBlock, PreviewBuildContext? build)
    {
        var appearance = AiUsageAppearance.Current;
        bool compact = appearance.Compact;
        var (_, fill, percent) = Measure(usage, window, now);
        int height = appearance.BarHeight;
        double usedPercent = window?.UsedPercent ?? 0;
        var track = new Border { Name = weekly ? "WeeklyQuotaTrack" : "PrimaryQuotaTrack", Width = ColumnWidth, Height = height,
            CornerRadius = new CornerRadius(height / 2.0), Margin = new Thickness(0, compact ? 2 : 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, Background = TrackBrush, ClipToBounds = true };
        var fillBorder = new Border { Name = weekly ? "WeeklyQuotaFill" : "PrimaryQuotaFill", Width = ColumnWidth, Height = height, CornerRadius = new CornerRadius(height / 2.0),
            HorizontalAlignment = HorizontalAlignment.Left, Background = fill };
        appearance.BindFill(fillBorder, usage.Provider, usedPercent);
        _motion.BindBar(fillBorder, usage.Provider + (weekly ? ":weekly" : ":compact"), percent, window != null && !window.IsExpired(now));
        if (build != null)
        {
            Color targetColor = (fill as SolidColorBrush)?.Color ?? Colors.Transparent;
            string provider = usage.Provider;
            build.Tracks.Add(new TrackAnimTarget(
                fillBorder,
                provider + ":" + fillBorder.Name,
                targetColor,
                () => appearance.BindFill(fillBorder, provider, usedPercent),
                build.Tracks.Count,
                weekly,
                isNewBlock));
        }
        track.Child = fillBorder;
        return track;
    }

    private static Grid CreateSummary(AiAccountUsage usage, AiQuotaWindow? window, AiQuotaWindow? weekly,
        ClaudeBridgeSetupStatus setup, DateTimeOffset now, PreviewBuildContext? build)
    {
        var appearance = AiUsageAppearance.Current;
        bool compact = appearance.Compact;
        var summary = new Grid { Name = "SubscriptionSummary", Width = ColumnWidth,
            Margin = new Thickness(0, compact ? 1 : 3, 0, 0) };
        summary.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        summary.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        summary.Children.Add(new TextBlock
        {
            Name = "SubscriptionPlan", Text = Summary(usage, window, setup, now), FontSize = compact ? 8.5 : 9.5,
            Foreground = LabelBrush, TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (appearance.ShowWeekly && usage.State == AiUsageState.Ready)
        {
            string weeklyPrefix = Loc.Get("aiUsage.compactSevenDay");
            var (weekText, _, weekPercent) = Measure(usage, weekly, now);
            string finalText = weeklyPrefix + " " + weekText;
            var week = new TextBlock { Name = "WeeklySummary", Text = finalText,
                FontSize = compact ? 8.5 : 9.5, Foreground = LabelBrush, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(6, 0, 0, 0) };
            Grid.SetColumn(week, 1);
            summary.Children.Add(week);
            if (build != null && weekly != null && !weekly.IsExpired(now))
            {
                var culture = Loc.GetCulture();
                build.Numbers.Add(new NumberAnimTarget(
                    week,
                    usage.Provider + ":weekly",
                    weekPercent,
                    v => weeklyPrefix + " " + Math.Round(v).ToString("0", culture) + "%",
                    finalText,
                    45));
            }
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

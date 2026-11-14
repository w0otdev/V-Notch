using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.AiUsage;

namespace VNotch.Controls;

public sealed class AiAccountUsageWidget : UserControl
{
    private static readonly Brush LabelBrush = Frozen(0xB0, 0xB0, 0xB0);
    private static readonly Brush ResetBrush = Frozen(0x87, 0x87, 0x87);
    private static readonly Brush TrackBrush = Frozen(0x2D, 0x2D, 0x2D);
    private static readonly Brush GreenBrush = Frozen(0x00, 0xD4, 0x91);
    private static readonly Brush OrangeBrush = Frozen(0xFF, 0x4D, 0x08);
    private static readonly Brush RedBrush = Frozen(0xFF, 0x3B, 0x30);
    private readonly Grid _grid = new();
    private bool _subscribed;
    private bool _compact;

    public bool Compact
    {
        get => _compact;
        set { if (_compact == value) return; _compact = value; Present(); }
    }

    public AiAccountUsageWidget()
    {
        AiUsageTypography.Apply(this);
        Content = _grid;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => { if (IsVisible) Present(); };
        Present();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed) return;
        _subscribed = true;
        AiAccountUsageMonitor.Shared.Updated += OnUpdated;
        Loc.LanguageChanged += OnLanguageChanged;
        Present();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _subscribed = false;
        AiAccountUsageMonitor.Shared.Updated -= OnUpdated;
        Loc.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged() => OnUpdated(this, EventArgs.Empty);
    private void OnUpdated(object? sender, EventArgs e)
    {
        if (!_subscribed || Dispatcher.HasShutdownStarted) return;
        if (Dispatcher.CheckAccess()) { if (IsVisible) Present(); }
        else Dispatcher.BeginInvoke(() => { if (_subscribed && IsVisible) Present(); });
    }

    internal void Present() => Present(AiAccountUsageMonitor.Shared.Codex, AiAccountUsageMonitor.Shared.Claude);

    internal void Present(AiAccountUsage codex, AiAccountUsage claude)
    {
        _grid.Children.Clear();
        _grid.ColumnDefinitions.Clear();
        _grid.RowDefinitions.Clear();
        _grid.MinWidth = Compact ? 120 : 220;
        _grid.MaxWidth = Compact ? 480 : 360;
        if (Compact)
        {
            _grid.ColumnDefinitions.Add(new());
            _grid.ColumnDefinitions.Add(new() { Width = new GridLength(10) });
            _grid.ColumnDefinitions.Add(new());
        }
        else
        {
            _grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            _grid.RowDefinitions.Add(new() { Height = new GridLength(12) });
            _grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        }
        var claudeCard = CreateProviderCard(claude, Compact);
        var codexCard = CreateProviderCard(codex, Compact);
        if (Compact) Grid.SetColumn(codexCard, 2);
        else Grid.SetRow(codexCard, 2);
        _grid.Children.Add(claudeCard);
        _grid.Children.Add(codexCard);
        AutomationProperties.SetName(this, Loc.Get("aiUsage.title"));
    }

    internal static Border CreateProviderCard(AiAccountUsage usage, bool compact = false, DateTimeOffset? observedNow = null)
    {
        DateTimeOffset now = observedNow ?? DateTimeOffset.UtcNow;
        var panel = new StackPanel();
        var card = new Border
        {
            Name = usage.Provider + "UsageCard", CornerRadius = new(compact ? 8 : 26),
            Background = SystemParameters.HighContrast ? SystemColors.ControlBrush : Brushes.Black,
            Padding = new Thickness(compact ? 0 : 16), Child = panel, SnapsToDevicePixels = false, UseLayoutRounding = false
        };
        AiUsageTypography.Apply(card);
        card.SetValue(TextElement.ForegroundProperty, SystemParameters.HighContrast ? SystemColors.ControlTextBrush : Brushes.White);
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, compact ? 5 : 12) };
        var logo = new Image { Source = AiProviderBrand.For(usage.Provider), Width = compact ? 11 : 26, Height = compact ? 11 : 26,
            Margin = new(0, 0, compact ? 3 : 8, 0), Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center,
            Focusable = false, IsHitTestVisible = false, Tag = usage.Provider };
        header.Children.Add(logo);
        header.Children.Add(new TextBlock
        {
            Text = compact ? usage.Provider : Loc.Get("aiUsage.providerUsage", usage.Provider), FontSize = compact ? 10 : 19,
            FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(header);
        var details = new List<string>();
        if (usage.AccountLabel != null) details.Add(usage.AccountLabel);
        if (usage.State != AiUsageState.Ready || usage.Windows is not { Count: > 0 })
        {
            string status = Loc.Get("aiUsage.state." + usage.State);
            panel.Children.Add(new TextBlock { Text = compact ? usage.State == AiUsageState.Loading ? "…" : "—" : status,
                FontSize = compact ? 10 : 12, Foreground = LabelBrush, TextWrapping = TextWrapping.Wrap,
                MinHeight = compact ? 20 : 105, VerticalAlignment = VerticalAlignment.Center });
            details.Add(status);
        }
        else
        {
            for (int i = 0; i < usage.Windows.Count; i++)
            {
                var window = usage.Windows[i];
                string info = DescribeWindow(usage, window, now);
                details.Add(info);
                if (i < 2) panel.Children.Add(CreateQuotaRow(usage, window, now, compact, i == 0, info));
            }
            if (!compact && usage.IsStale(now))
                panel.Children.Add(new TextBlock { Text = Loc.Get("aiUsage.stale"), FontSize = 10, Foreground = ResetBrush,
                    TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) });
        }
        if (usage.UpdatedAt is { } updated) details.Add(Loc.Get("aiUsage.updated", updated.ToLocalTime().ToString("g", Loc.GetCulture())));
        string description = string.Join("\n\n", details);
        AutomationProperties.SetName(card, Loc.Get("aiUsage.providerUsage", usage.Provider) + "\n" + description);
        if (compact)
        {
            var expanded = CreateProviderCard(usage, observedNow: now);
            expanded.Width = 336;
            card.ToolTip = new ToolTip { Content = expanded, Padding = new Thickness(0), BorderThickness = new Thickness(0),
                Background = Brushes.Transparent, Placement = PlacementMode.Bottom, HasDropShadow = false };
            ToolTipService.SetInitialShowDelay(card, 250);
            ToolTipService.SetShowDuration(card, 30000);
        }
        else card.ToolTip = description;
        return card;
    }

    private static StackPanel CreateQuotaRow(AiAccountUsage usage, AiQuotaWindow window, DateTimeOffset now, bool compact, bool first, string info)
    {
        bool expired = window.IsExpired(now);
        var row = new StackPanel { Margin = new(0, first ? 0 : compact ? 5 : 12, 0, 0), ToolTip = info,
            Opacity = expired || usage.IsStale(now) ? 0.55 : 1 };
        string percent = window.RemainingPercent.ToString(compact ? "0" : "0.#", Loc.GetCulture());
        string label = WindowLabel(usage, window, compact);
        if (compact)
            row.Children.Add(new TextBlock { Text = label + " " + (expired ? "—" : percent + "%"), FontSize = 10,
                Foreground = LabelBrush, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis });
        else
        {
            var line = new Grid { Margin = new(0, 0, 0, 7) };
            line.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            line.Children.Add(new TextBlock { Text = label, Foreground = LabelBrush, FontSize = 12, FontWeight = FontWeights.Bold,
                Margin = new(0, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            var reset = new TextBlock { Name = "QuotaReset", Text = FormatReset(window, now), FontSize = 11.5, Foreground = ResetBrush,
                FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 160, ToolTip = info };
            Grid.SetColumn(reset, 1);
            line.Children.Add(reset);
            row.Children.Add(line);
        }
        row.Children.Add(CreateTrack(window, compact, expired));
        if (!compact)
            row.Children.Add(new TextBlock { Name = "QuotaRemaining", Text = expired ? "—" : Loc.Get("aiUsage.left", percent), FontSize = 12,
                FontWeight = FontWeights.Bold, Foreground = LabelBrush, Margin = new(0, 7, 0, 0) });
        AutomationProperties.SetName(row, info);
        return row;
    }

    private static Border CreateTrack(AiQuotaWindow window, bool compact, bool expired)
    {
        double used = expired ? 0 : Math.Clamp(window.UsedPercent, 0, 100);
        double remaining = expired ? 0 : window.RemainingPercent;
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new() { Width = new GridLength(remaining, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new() { Width = new GridLength(100 - remaining, GridUnitType.Star) });
        columns.Children.Add(new Border { Name = "QuotaFill", CornerRadius = new(compact ? 2 : 4),
            Background = SystemParameters.HighContrast ? SystemColors.HighlightBrush : used >= 90 ? RedBrush : used >= 70 ? OrangeBrush : GreenBrush });
        return new Border { Name = "QuotaTrack", Height = compact ? 3 : 7, CornerRadius = new(compact ? 2 : 4),
            Background = SystemParameters.HighContrast ? SystemColors.ControlDarkBrush : TrackBrush,
            Child = columns, Margin = new(0, compact ? 3 : 0, 0, 0) };
    }

    internal static string FormatReset(AiQuotaWindow window, DateTimeOffset now)
    {
        if (window.ResetsAt is not { } reset) return "—";
        if (reset <= now) return Loc.Get("aiUsage.expired");
        var remaining = reset - now;
        if (remaining < TimeSpan.FromHours(24) && window.DurationMinutes is not 10080)
        {
            int minutes = (int)Math.Ceiling(remaining.TotalMinutes);
            string duration = minutes < 60 ? Loc.Get("aiUsage.minutes", minutes) : Loc.Get("aiUsage.hoursMinutes", minutes / 60, minutes % 60);
            return Loc.Get("aiUsage.resetsIn", duration);
        }
        var local = reset.ToLocalTime();
        return Loc.Get("aiUsage.resets", local.ToString("ddd", Loc.GetCulture()) + " " + local.ToString("t", Loc.GetCulture()));
    }

    private static string WindowLabel(AiAccountUsage usage, AiQuotaWindow window, bool compact)
    {
        if (compact) return window.DurationMinutes switch
        {
            300 => Loc.Get("aiUsage.compactFiveHour"), 10080 => Loc.Get("aiUsage.compactSevenDay"),
            _ => Loc.Get(window.LabelKey)
        };
        if (window.LabelKey is "aiUsage.primary" or "aiUsage.fiveHour") return Loc.Get("aiUsage.currentSession");
        if (usage.Provider == "Claude" && window.DurationMinutes == 10080) return Loc.Get("aiUsage.allModels");
        return window.DurationMinutes == 10080 ? Loc.Get("aiUsage.sevenDay") : Loc.Get(window.LabelKey);
    }

    private static string DescribeWindow(AiAccountUsage usage, AiQuotaWindow window, DateTimeOffset now)
    {
        string info = (window.Bucket is { } bucket ? bucket + " · " : "") + WindowLabel(usage, window, false) + " · " +
            (window.IsExpired(now) ? "—" : Loc.Get("aiUsage.left", window.RemainingPercent.ToString("0.#", Loc.GetCulture()))) +
            "\n" + FormatReset(window, now);
        if (usage.IsStale(now)) info += "\n" + Loc.Get("aiUsage.stale");
        return info;
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

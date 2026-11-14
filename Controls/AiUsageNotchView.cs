using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.AiUsage;

namespace VNotch.Controls;

public sealed class AiUsageNotchView : UserControl
{
    private static readonly Brush PrimaryTextBrush = UiPalette.PrimaryBrush;
    private static readonly Brush LabelTextBrush = UiPalette.SecondaryBrush;
    private static readonly Brush MutedTextBrush = UiPalette.SecondaryBrush;
    private static readonly Brush SubtleTextBrush = UiPalette.TertiaryBrush;
    private static readonly Brush DividerBrush = Frozen(0x18, 0xFF, 0xFF, 0xFF);
    private static readonly Brush TrackBrush = Frozen(0x1C, 0xFF, 0xFF, 0xFF);
    private static readonly Brush ActionRestBrush = Brushes.Transparent;
    private static readonly Brush ActionHoverBrush = Frozen(0x1E, 0xFF, 0xFF, 0xFF);
    private static readonly Brush ActionIconBrush = UiPalette.IconBrush;

    private static readonly Geometry RefreshGeometry = CreateRefreshGeometry();
    private static readonly Geometry TuneGeometry = CreateTuneGeometry();

    private readonly Grid _rootGrid = new()
    {
        Margin = new Thickness(16, 26, 16, 16),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch
    };

    private bool _subscribed;
    private bool _isRefreshing;
    private bool _isPreview;
    private readonly AiUsageMotion _motion;

    public bool IsPreview
    {
        get => _isPreview;
        set
        {
            if (_isPreview == value) return;
            _isPreview = value;
            _rootGrid.Margin = value ? new Thickness(0, 4, 0, 4) : new Thickness(10, 26, 10, 12);
            Present();
        }
    }

    public event Action? OpenSettingsRequested;
    public event Action? ContentSizeChanged;

    public AiUsageNotchView()
    {
        AiUsageTypography.Apply(this, new Thickness(0, 1, 0, 2));
        Content = _rootGrid;
        _motion = new AiUsageMotion(this);
        SnapsToDevicePixels = false;
        UseLayoutRounding = false;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
        SizeChanged += (_, _) => LayoutPreviewPanes();
        PresentWithEntrance();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed) return;
        _subscribed = true;
        AiAccountUsageMonitor.Shared.Updated += OnMonitorUpdated;
        Loc.LanguageChanged += OnLanguageChanged;
        AiUsageAppearance.Changed += OnLanguageChanged;
        PresentWithEntrance();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed) return;
        _subscribed = false;
        AiAccountUsageMonitor.Shared.Updated -= OnMonitorUpdated;
        Loc.LanguageChanged -= OnLanguageChanged;
        AiUsageAppearance.Changed -= OnLanguageChanged;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            PresentWithEntrance();
        }
    }

    private void OnLanguageChanged() => OnMonitorUpdated(this, EventArgs.Empty);

    private void OnMonitorUpdated(object? sender, EventArgs e)
    {
        if (!_subscribed || Dispatcher.HasShutdownStarted) return;
        if (Dispatcher.CheckAccess())
        {
            if (IsVisible) Present();
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_subscribed && IsVisible) Present();
        });
    }

    private ClaudeBridgeSetupStatus _claudeSetup;

    internal void Present() => Present(AiAccountUsageMonitor.Shared.Codex, AiAccountUsageMonitor.Shared.Claude);

    internal void PrepareForDisplay()
    {
        // Hidden views skip monitor updates. Build the latest snapshot before
        // the shell measures its destination, rather than after it is revealed.
        if (!IsVisible) Present();
    }

    private void PresentWithEntrance() => Present(AiAccountUsageMonitor.Shared.Codex, AiAccountUsageMonitor.Shared.Claude, reveal: !_isPreview);

    internal void Present(AiAccountUsage codex, AiAccountUsage claude, DateTimeOffset? observedNow = null, bool reveal = false)
    {
        DateTimeOffset now = observedNow ?? DateTimeOffset.UtcNow;
        _claudeSetup = new ClaudeUsageBridge().InspectSetupState();
        _motion.BeginPresentation(reveal && !_isPreview);

        _rootGrid.Children.Clear();
        _rootGrid.ColumnDefinitions.Clear();
        _rootGrid.RowDefinitions.Clear();

        var appearance = AiUsageAppearance.Current;
        AiAccountUsage first = appearance.ClaudeFirst ? claude : codex;
        AiAccountUsage second = appearance.ClaudeFirst ? codex : claude;
        int paneIndex = 0;
        if (appearance.Shows(first.Provider))
        {
            AppendProviderPane(first, now, paneIndex++);
        }
        if (appearance.Shows(second.Provider))
        {
            AppendProviderPane(second, now, paneIndex++);
        }
        if (paneIndex == 0) _rootGrid.Children.Add(CreateEmptyStateBody(Loc.Get("aiUsage.chooseProviders"), 1));
        LayoutPreviewPanes();

        AutomationProperties.SetName(this, Loc.Get("aiUsage.title"));
        ContentSizeChanged?.Invoke();
    }

    private void AppendProviderPane(AiAccountUsage account, DateTimeOffset now, int paneIndex)
    {
        if (paneIndex > 0)
        {
            _rootGrid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var divider = new Border
            {
                Width = 1,
                Margin = new Thickness(16, 4, 16, 4),
                Background = SystemParameters.HighContrast ? SystemColors.ControlDarkBrush : DividerBrush,
                IsHitTestVisible = false
            };
            Grid.SetColumn(divider, _rootGrid.ColumnDefinitions.Count - 1);
            _rootGrid.Children.Add(divider);
        }
        _rootGrid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var pane = CreateProviderPane(account, now);
        Grid.SetColumn(pane, _rootGrid.ColumnDefinitions.Count - 1);
        _rootGrid.Children.Add(pane);
        _motion.Reveal(pane, paneIndex);
    }

    private void LayoutPreviewPanes()
    {
        if (!_isPreview) return;
        bool narrow = ActualWidth > 0 && ActualWidth < 480;
        _rootGrid.ColumnDefinitions.Clear();
        _rootGrid.RowDefinitions.Clear();
        int index = 0;
        foreach (FrameworkElement child in _rootGrid.Children)
        {
            bool divider = child is Border;
            if (narrow)
            {
                _rootGrid.RowDefinitions.Add(new() { Height = GridLength.Auto });
                Grid.SetColumn(child, 0);
                Grid.SetRow(child, index);
            }
            else
            {
                _rootGrid.ColumnDefinitions.Add(new() { Width = divider ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(child, index);
                Grid.SetRow(child, 0);
            }
            if (child is Border line)
            {
                line.Width = narrow ? double.NaN : 1;
                line.Height = narrow ? 1 : double.NaN;
                line.Margin = narrow ? new Thickness(0, 16, 0, 16) : new Thickness(16, 0, 16, 0);
            }
            index++;
        }
    }

    internal Grid CreateProviderPane(AiAccountUsage usage, DateTimeOffset now)
    {
        var windows = FilterVisibleWindows(usage.Windows, AiUsageAppearance.Current.ShowWeekly);
        int windowCount = windows.Length;
        byte hasUpdated = usage.UpdatedAt.HasValue ? (byte)1 : (byte)0;
        long updatedTicks = usage.UpdatedAt?.UtcDateTime.Ticks ?? 0L;
        long nowTicks = now.UtcDateTime.Ticks;

        VNotchNative.vn_ai_usage_compute_card_summary(
            (int)usage.State,
            windowCount,
            hasUpdated,
            updatedTicks,
            nowTicks,
            out var summary);

        string statusText = ResolveStatusDisplay(usage, _claudeSetup, out byte claudeActionKind);
        if (usage.State == AiUsageState.Ready && windowCount == 0) statusText = Loc.Get("aiUsage.chooseProviders");
        byte actionKind = usage.Provider == "Claude" ? claudeActionKind : summary.ActionKind;

        var pane = new Grid
        {
            Name = usage.Provider + "NotchPane",
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent
        };
        pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = CreateHeaderRow(usage, actionKind);
        Grid.SetRow(header, 0);
        pane.Children.Add(header);

        UIElement body = summary.ShowQuotaRows != 0 && windowCount > 0
            ? CreateQuotaBody(usage, windows, summary.VisibleWindowCount, summary.IsStale != 0, now)
            : CreateEmptyStateBody(statusText, actionKind);

        Grid.SetRow(body, 1);
        pane.Children.Add(body);

        AutomationProperties.SetName(pane, Loc.Get("aiUsage.providerUsage", usage.Provider));
        return pane;
    }

    private Grid CreateHeaderRow(AiAccountUsage usage, byte actionKind)
    {
        var header = new Grid { MinHeight = 28, Margin = new Thickness(0, 0, 0, AiUsageAppearance.Current.Compact ? 6 : 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        var logo = new Image
        {
            Source = AiProviderBrand.For(usage.Provider),
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 8, 0),
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Focusable = false,
            Tag = usage.Provider
        };
        titleStack.Children.Add(logo);
        logo.Visibility = AiUsageAppearance.Current.ShowLogos ? Visibility.Visible : Visibility.Collapsed;

        var nameText = new TextBlock
        {
            Text = usage.Provider,
            FontSize = AiUsageAppearance.Current.Compact ? 12 : 14,
            FontWeight = FontWeights.Bold,
            Foreground = SystemParameters.HighContrast ? SystemColors.ControlTextBrush : PrimaryTextBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleStack.Children.Add(nameText);

        if (AiUsageAppearance.Current.ShowPlan && !string.IsNullOrWhiteSpace(usage.Plan))
        {
            titleStack.Children.Add(new TextBlock
            {
                Text = " · " + AiUsageTypography.PlanName(usage.Plan),
                FontSize = 11,
                Foreground = SystemParameters.HighContrast ? SystemColors.GrayTextBrush : MutedTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 84
            });
        }

        if (usage.UpdatedAt is { } headerUpdated)
        {
            titleStack.ToolTip = (!AiUsageAppearance.Current.ShowAccount || string.IsNullOrWhiteSpace(usage.AccountLabel) ? "" : usage.AccountLabel + "\n") +
                Loc.Get("aiUsage.updated", headerUpdated.ToLocalTime().ToString("t", Loc.GetCulture()));
        }

        Grid.SetColumn(titleStack, 0);
        header.Children.Add(titleStack);

        bool opensSettings = actionKind != 0;
        var actionBtn = CreateHeaderActionButton(opensSettings);
        Grid.SetColumn(actionBtn, 1);
        header.Children.Add(actionBtn);

        return header;
    }

    private ActionBorder CreateHeaderActionButton(bool opensSettings)
    {
        string tooltip = opensSettings ? Loc.Get("aiUsage.openSettings") : Loc.Get("aiUsage.refresh");
        var iconPath = new Path
        {
            Data = opensSettings ? TuneGeometry : RefreshGeometry,
            Fill = SystemParameters.HighContrast ? SystemColors.ControlTextBrush : ActionIconBrush,
            Stretch = Stretch.Uniform,
            Width = 12,
            Height = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var btn = new ActionBorder
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(8),
            Background = ActionRestBrush,
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Child = iconPath
        };
        btn.Visibility = _isPreview ? Visibility.Collapsed : Visibility.Visible;
        btn.Focusable = !_isPreview;
        AutomationProperties.SetName(btn, tooltip);

        btn.MouseEnter += (_, _) =>
        {
            if (!SystemParameters.HighContrast) btn.Background = ActionHoverBrush;
        };
        btn.MouseLeave += (_, _) => btn.Background = ActionRestBrush;
        btn.Click += async (_, e) =>
        {
            e.Handled = true;
            if (opensSettings)
            {
                OpenSettingsRequested?.Invoke();
                return;
            }

            await TriggerRefreshAsync(btn);
        };

        return btn;
    }

    private StackPanel CreateQuotaBody(
        AiAccountUsage usage,
        System.Collections.Generic.IReadOnlyList<AiQuotaWindow> windows,
        int visibleCount,
        bool isStale,
        DateTimeOffset now)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Top };

        for (int i = 0; i < visibleCount; i++)
        {
            var row = CreateQuotaRow(usage, windows[i], i == 0, now);
            stack.Children.Add(row);
        }

        if (visibleCount == 1 && usage.UpdatedAt is { } updated)
        {
            stack.Children.Add(new TextBlock
            {
                Text = isStale
                    ? Loc.Get("aiUsage.stale")
                    : Loc.Get("aiUsage.updated", updated.ToLocalTime().ToString("t", Loc.GetCulture())),
                FontSize = 10,
                Foreground = SystemParameters.HighContrast ? SystemColors.GrayTextBrush : SubtleTextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 8, 0, 0)
            });
        }

        return stack;
    }

    private StackPanel CreateQuotaRow(AiAccountUsage usage, AiQuotaWindow window, bool isFirst, DateTimeOffset now)
    {
        VNotchNative.vn_ai_usage_compute_window_visual(
            window.UsedPercent,
            window.DurationMinutes ?? 0,
            window.ResetsAt.HasValue ? (byte)1 : (byte)0,
            window.ResetsAt?.UtcDateTime.Ticks ?? 0L,
            usage.UpdatedAt.HasValue ? (byte)1 : (byte)0,
            usage.UpdatedAt?.UtcDateTime.Ticks ?? 0L,
            now.UtcDateTime.Ticks,
            out var visual);

        bool expired = visual.IsExpired != 0;
        string label = ResolveWindowLabel(window);
        string resetText = FormatResetText(window, visual);
        string percentText = expired
            ? "—"
            : AiUsageAppearance.Current.PercentText(AiUsageAppearance.Current.ShowUsed ? visual.ClampedUsedPercent : visual.ClampedRemainingPercent);
        string allowanceText = percentText;

        Brush fillBrush = AiUsageAppearance.Current.Fill(usage.Provider, visual.ClampedUsedPercent);

        Brush valueBrush = SystemParameters.HighContrast
            ? SystemColors.ControlTextBrush
            : PrimaryTextBrush;

        var row = new StackPanel
        {
            Margin = new Thickness(0, isFirst ? 0 : AiUsageAppearance.Current.Compact ? 8 : 12, 0, 0),
            Opacity = 1,
            ToolTip = label + " · " + allowanceText + (AiUsageAppearance.Current.ShowResets ? "\n" + resetText : "")
        };

        var topLine = new Grid { Margin = new Thickness(0, 0, 0, AiUsageAppearance.Current.Compact ? 4 : 6) };
        topLine.RowDefinitions.Add(new() { Height = GridLength.Auto });
        topLine.RowDefinitions.Add(new() { Height = GridLength.Auto });
        topLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = SystemParameters.HighContrast ? SystemColors.ControlTextBrush : LabelTextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        Grid.SetColumn(labelBlock, 0);
        topLine.Children.Add(labelBlock);

        var resetBlock = new TextBlock
        {
            Name = "QuotaReset",
            Text = resetText,
            FontSize = 10,
            Foreground = SystemParameters.HighContrast ? SystemColors.GrayTextBrush : SubtleTextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0)
        };
        Grid.SetRow(resetBlock, 1);
        Grid.SetColumnSpan(resetBlock, 3);
        topLine.Children.Add(resetBlock);
        resetBlock.Visibility = AiUsageAppearance.Current.ShowResets ? Visibility.Visible : Visibility.Collapsed;

        var percentBlock = new TextBlock
        {
            Name = "QuotaRemaining",
            Text = percentText,
            FontSize = AiUsageAppearance.Current.Compact ? 9 : 10,
            FontWeight = FontWeights.Bold,
            Foreground = valueBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (!expired)
        {
            double displayed = AiUsageAppearance.Current.ShowUsed ? visual.ClampedUsedPercent : visual.ClampedRemainingPercent;
            AiUsageTypography.EmphasizePercent(percentBlock, percentText, displayed,
                isFirst ? AiUsageAppearance.Current.Compact ? 21 : 26 : AiUsageAppearance.Current.Compact ? 14 : 16);
        }
        else percentBlock.FontSize = isFirst ? 26 : 16;
        Grid.SetColumn(percentBlock, 2);
        topLine.Children.Add(percentBlock);

        row.Children.Add(topLine);

        var trackGrid = new Grid();
        double remaining = expired ? 0 : AiUsageAppearance.Current.ShowUsed ? visual.ClampedUsedPercent : visual.ClampedRemainingPercent;
        trackGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(remaining, GridUnitType.Star) });
        trackGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - remaining, GridUnitType.Star) });

        var fillBorder = new Border
        {
            Name = "QuotaFill",
            CornerRadius = new CornerRadius(AiUsageAppearance.Current.BarHeight / 2.0),
            Background = fillBrush
        };
        AiUsageAppearance.Current.BindFill(fillBorder, usage.Provider, visual.ClampedUsedPercent);
        Grid.SetColumn(fillBorder, 0);
        Grid.SetColumnSpan(fillBorder, 2);
        _motion.BindBar(fillBorder, usage.Provider + ":" + window.Bucket + ":" + window.LabelKey + ":" + window.DurationMinutes,
            remaining, !expired);
        trackGrid.Children.Add(fillBorder);

        var trackBorder = new Border
        {
            Name = "QuotaTrack",
            Height = AiUsageAppearance.Current.BarHeight,
            CornerRadius = new CornerRadius(AiUsageAppearance.Current.BarHeight / 2.0),
            Background = SystemParameters.HighContrast ? SystemColors.ControlDarkBrush : TrackBrush,
            ClipToBounds = true,
            Child = trackGrid
        };
        row.Children.Add(trackBorder);

        AutomationProperties.SetName(row, label + " " + allowanceText + (AiUsageAppearance.Current.ShowResets ? " " + resetText : ""));
        return row;
    }

    internal static string ResolveStatusDisplay(AiAccountUsage usage, ClaudeBridgeSetupStatus setup, out byte actionKind)
    {
        if (usage.Provider != "Claude")
        {
            VNotchNative.vn_ai_usage_compute_card_summary((int)usage.State, usage.Windows?.Count ?? 0, 0, 0L, 0L, out var s);
            actionKind = s.ActionKind;
            return Loc.Get("aiUsage.state." + usage.State);
        }

        VNotchNative.vn_ai_usage_resolve_claude_setup_state(
            (int)usage.State,
            setup.HasSettingsFile ? (byte)1 : (byte)0,
            setup.HasStatusLine ? (byte)1 : (byte)0,
            setup.MatchesBridgeCommand ? (byte)1 : (byte)0,
            setup.HasCustomApiEndpoint ? (byte)1 : (byte)0,
            out actionKind,
            out int promptMode);

        string key = promptMode switch
        {
            1 => "aiUsage.claudeNotConnected",
            2 => "aiUsage.claudeCustomEndpoint",
            3 => "aiUsage.claudeExistingStatusLine",
            _ => "aiUsage.state." + usage.State
        };
        return Loc.Get(key);
    }

    private StackPanel CreateEmptyStateBody(string statusText, byte actionKind)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Top };

        stack.Children.Add(new TextBlock
        {
            Text = statusText,
            FontSize = 12,
            Foreground = SystemParameters.HighContrast ? SystemColors.GrayTextBrush : MutedTextBrush,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = 18
        });

        bool opensSettings = actionKind != 0;
        string actionLabel = opensSettings ? Loc.Get("aiUsage.openSettings") : Loc.Get("aiUsage.refresh");

        var actionText = new TextBlock
        {
            Text = actionLabel,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = SystemParameters.HighContrast ? SystemColors.HighlightBrush : PrimaryTextBrush
        };

        var actionRow = new ActionBorder
        {
            MinWidth = 44,
            MinHeight = 30,
            CornerRadius = new CornerRadius(8),
            Background = SystemParameters.HighContrast ? SystemColors.ControlBrush : ActionHoverBrush,
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 10, 0, 0),
            Child = actionText
        };
        actionRow.Visibility = _isPreview ? Visibility.Collapsed : Visibility.Visible;
        actionRow.Focusable = !_isPreview;
        AutomationProperties.SetName(actionRow, actionLabel);

        actionRow.MouseEnter += (_, _) => actionRow.Opacity = 0.78;
        actionRow.MouseLeave += (_, _) => actionRow.Opacity = 1.0;
        actionRow.Click += async (_, e) =>
        {
            e.Handled = true;
            if (opensSettings)
            {
                OpenSettingsRequested?.Invoke();
                return;
            }

            await TriggerRefreshAsync(actionRow);
        };

        stack.Children.Add(actionRow);
        return stack;
    }

    private async System.Threading.Tasks.Task TriggerRefreshAsync(FrameworkElement? source = null)
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        if (source != null) { source.IsEnabled = false; source.Opacity = 0.45; source.ToolTip = Loc.Get("aiUsage.state.Loading"); }
        using var busy = _motion.Busy(source);
        try
        {
            await AiAccountUsageMonitor.Shared.RefreshAsync(forceCodex: true);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _isRefreshing = false;
            if (source != null) { source.IsEnabled = true; source.Opacity = 1.0; source.ToolTip = Loc.Get("aiUsage.refresh"); }
            if (IsVisible) Present();
        }
    }

    private static string ResolveWindowLabel(AiQuotaWindow window)
    {
        return Loc.Get(window.DurationMinutes switch
        {
            300 => "aiUsage.fiveHour",
            10080 => "aiUsage.sevenDay",
            _ => window.LabelKey
        });
    }

    private static string FormatResetText(AiQuotaWindow window, in VnAiQuotaWindowVisualOut visual)
    {
        return visual.ResetMode switch
        {
            1 => Loc.Get("aiUsage.expired"),
            2 => Loc.Get("aiUsage.minutes", visual.ResetMinutes),
            3 => Loc.Get("aiUsage.hoursMinutes", visual.ResetHours, visual.ResetMinutes),
            4 when window.ResetsAt is { } reset =>
                reset.ToLocalTime().ToString("ddd", Loc.GetCulture()) + " " + reset.ToLocalTime().ToString("t", Loc.GetCulture()),
            _ => "—"
        };
    }

    private static AiQuotaWindow[] FilterVisibleWindows(IReadOnlyList<AiQuotaWindow>? source, bool showWeekly)
    {
        if (source is not { Count: > 0 }) return [];

        int count = 0;
        for (int i = 0; i < source.Count; i++)
        {
            if (showWeekly || source[i].DurationMinutes != 10080) count++;
        }
        if (count == 0) return [];

        var result = new AiQuotaWindow[count];
        int write = 0;
        for (int i = 0; i < source.Count; i++)
        {
            var win = source[i];
            if (showWeekly || win.DurationMinutes != 10080)
                result[write++] = win;
        }
        return result;
    }

    private static Geometry CreateRefreshGeometry()
    {
        var pen = new Pen(Brushes.White, 3.2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        var geometry = new GeometryGroup { FillRule = FillRule.Nonzero };
        geometry.Children.Add(Geometry.Parse("M18.3,6.7 A8,8 0 1 0 19.2,15.5").GetWidenedPathGeometry(pen));
        geometry.Children.Add(Geometry.Parse("M20.4,2.7 Q21.5,1.6 21.5,3.2 L21.5,9 Q21.5,10 20.5,10 L14.7,10 Q13.1,10 14.2,8.9 Z"));
        geometry.Freeze();
        return geometry;
    }

    private static Geometry CreateTuneGeometry()
    {
        var geometry = new GeometryGroup { FillRule = FillRule.Nonzero };
        geometry.Children.Add(new RectangleGeometry(new Rect(2, 5, 20, 3), 1.5, 1.5));
        geometry.Children.Add(new RectangleGeometry(new Rect(2, 16, 20, 3), 1.5, 1.5));
        geometry.Children.Add(new RectangleGeometry(new Rect(7, 2, 5, 9), 2.5, 2.5));
        geometry.Children.Add(new RectangleGeometry(new Rect(14, 13, 5, 9), 2.5, 2.5));
        geometry.Freeze();
        return geometry;
    }

    private static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}

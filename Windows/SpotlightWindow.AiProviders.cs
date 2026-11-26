using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Controls;
using VNotch.Services;
using VNotch.Services.Spotlight;

namespace VNotch;

public partial class SpotlightWindow
{
    private static readonly Geometry CheckMarkGeometry = CreateCheckMarkGeometry();
    private ContextMenu? _aiProviderMenu;
    // Keep quick selection for this window's conversations without changing the saved default.
    private string? _aiSelectedProvider;

    private static Geometry CreateCheckMarkGeometry()
    {
        var geometry = Geometry.Parse("M1,5 L4,8 L10,1");
        geometry.Freeze();
        return geometry;
    }

    private void AiProviderButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_aiMode || !IsSpotlightOpen || _isClosing) return;
        if (_aiProviderMenu is { IsOpen: true })
        {
            CloseAiProviderMenu();
            return;
        }

        string current = CurrentAiIdentity().Provider;
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("AiProviderMenuStyle"),
            PlacementTarget = AiProviderButton,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 6,
            FontFamily = (FontFamily)FindResource("SFProDisplay"),
            FontWeight = FontWeights.Bold,
            Language = this.Language
        };
        AutomationProperties.SetName(menu, Loc.Get("spotlight.ai.switchProvider"));
        bool selected = false;
        int generation = _animationGeneration;
        MenuItem? focusItem = null;
        foreach (string provider in SpotlightAiService.Providers)
        {
            var (key, model) = SpotlightAiService.Configuration(_settings, provider);
            bool configured = !string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(model);
            var item = CreateAiProviderMenuItem(provider, model, provider == current, configured);
            if (configured && (focusItem == null || provider == current)) focusItem = item;
            item.Click += (_, args) =>
            {
                args.Handled = true;
                selected = true;
                menu.IsOpen = false;
                TransitionAiPageAsync(() => SelectAiProvider(provider)).SafeFireAndForget("SPOTLIGHT-AI");
            };
            menu.Items.Add(item);
        }

        menu.Opened += (_, _) =>
        {
            BeginAiProviderMenuEntrance(menu);
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (!ReferenceEquals(_aiProviderMenu, menu) || !menu.IsOpen) return;
                menu.Focus();
                focusItem?.Focus();
            }));
        };
        menu.Closed += (_, _) =>
        {
            ResetAiProviderMenuMotion(menu);
            if (!ReferenceEquals(_aiProviderMenu, menu)) return;
            _aiProviderMenu = null;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (generation != _animationGeneration || !IsSpotlightOpen || _isClosing || _allowClose || _aiProviderMenu != null) return;
                if (!IsActive) HideSpotlight();
                else if (!selected && _aiMode) AiProviderButton.Focus();
            }));
        };
        _aiProviderMenu = menu;
        menu.IsOpen = true;
    }

    private MenuItem CreateAiProviderMenuItem(string provider, string model, bool selected, bool configured)
    {
        var sfPro = (FontFamily)FindResource("SFProDisplay");
        var header = new Grid { Width = 246, Opacity = configured ? 1 : 0.45 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        header.Children.Add(CreateAiBrandImage(provider, 18));
        var label = new StackPanel { Margin = new Thickness(4, 0, 10, 0) };
        label.Children.Add(new TextBlock
        {
            Text = provider,
            FontFamily = sfPro,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = SpotlightChatPalette.Primary
        });
        label.Children.Add(new TextBlock
        {
            Text = configured ? model : Loc.Get("spotlight.ai.providerNotConfigured"),
            FontFamily = sfPro,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = SpotlightChatPalette.Secondary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 0)
        });
        Grid.SetColumn(label, 1);
        header.Children.Add(label);
        var check = new System.Windows.Shapes.Path
        {
            Data = CheckMarkGeometry,
            Stroke = SpotlightChatPalette.Primary,
            StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 12,
            Height = 12,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = selected ? Visibility.Visible : Visibility.Hidden
        };
        Grid.SetColumn(check, 2);
        header.Children.Add(check);
        var item = new MenuItem
        {
            Header = header,
            Style = (Style)FindResource("SpotlightMenuItemStyle"),
            IsCheckable = true,
            IsChecked = selected,
            IsEnabled = configured,
            Tag = selected ? "Selected" : null,
            ToolTip = configured ? model : Loc.Get("spotlight.ai.configure")
        };
        AutomationProperties.SetName(item, provider);
        AutomationProperties.SetHelpText(item, configured ? model : Loc.Get("spotlight.ai.configure"));
        ToolTipService.SetShowOnDisabled(item, true);
        return item;
    }

    private void SelectAiProvider(string provider)
    {
        if (!_aiMode || !IsSpotlightOpen || _isClosing || !SpotlightAiService.Providers.Contains(provider)) return;
        var (key, model) = SpotlightAiService.Configuration(_settings, provider);
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(model)) return;
        if (provider == CurrentAiIdentity().Provider)
        {
            SearchBox.Focus();
            return;
        }

        // Finish the old conversation before changing its identity. Cancellation can
        // restore an unfinished prompt; leave the input untouched for the new chat.
        CancelAiRequest();
        SaveAiHistory();
        _aiSelectedProvider = provider;
        _aiConversationProvider = provider;
        _aiConversationModel = model;
        _chatId = Guid.NewGuid().ToString("N");
        _messageOffset = 0;
        _aiHistory.Clear();
        _aiDraftToRestore = null;
        CloseAiHistory();
        RenderAiHistory();
        SetAiStatus("spotlight.ai.hint");
        RefreshAiPanel();
        SearchBox.Focus();
    }

    private void CloseAiProviderMenu()
    {
        if (_aiProviderMenu is { } menu) menu.IsOpen = false;
    }
}

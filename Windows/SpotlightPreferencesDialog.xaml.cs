using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media.Animation;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Spotlight;

namespace VNotch.Windows;

public partial class SpotlightPreferencesDialog : Window
{
    private readonly SpotlightPreferences _preferences;
    private readonly SpotlightSearchItem? _item;

    internal SpotlightPreferencesDialog(SpotlightPreferences preferences, SpotlightSearchItem? item)
    {
        InitializeComponent();
        _preferences = preferences;
        _item = item;
        Title = Loc.Get(item == null ? "spotlight.excludedFolders" : "spotlight.alias").TrimEnd('…', '.');
        Heading.Text = Title;
        Hint.Text = Loc.Get(item == null ? "spotlight.excludedFoldersHint" : "spotlight.aliasHint");
        CancelButton.Content = Loc.Get("dialog.cancel");
        SaveButton.Content = Loc.Get("settings.btn.save");
        CloseButton.ToolTip = Loc.Get("tooltip.close");
        AutomationProperties.SetName(CloseButton, Loc.Get("tooltip.close"));
        AutomationProperties.SetName(Editor, Title);
        AutomationProperties.SetHelpText(Editor, Hint.Text);
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Loc.GetCulture().IetfLanguageTag);
        if (item == null)
        {
            Editor.Text = string.Join(Environment.NewLine, preferences.GetExcludedFolders());
            Editor.AcceptsReturn = true;
            Editor.Height = 168;
            Editor.HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
            Editor.VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
        }
        else
        {
            AppCard.Visibility = Visibility.Visible;
            AppTitle.Text = item.DisplayTitle;
            AppTitle.ToolTip = item.Title;
            AppKind.Text = Loc.Get("spotlight.kind.application");
            AppIcon.Source = item.Icon;
            AppGlyph.Visibility = item.Icon == null ? Visibility.Visible : Visibility.Collapsed;
            Editor.Text = preferences.GetAlias(item.Target);
            Editor.MaxLength = 80;
            SaveButton.IsDefault = true;
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Editor.Focus();
        if (_item != null) Editor.SelectAll();
        if (AnimationConfig.ReduceMotion) return;
        var duration = TimeSpan.FromMilliseconds(140);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Shell.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration)
        { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        EntranceOffset.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(6, 0, duration)
        { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_item == null)
                _preferences.SetExcludedFolders(Editor.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            else if (!_preferences.SetApp(_item, _preferences.IsPinned(_item.Target), Editor.Text))
            {
                ShowError(Loc.Get("spotlight.pinLimit"));
                return;
            }
            DialogResult = true;
        }
        catch (ArgumentException) { ShowError(Loc.Get("spotlight.invalidFolder")); }
        catch (NotSupportedException) { ShowError(Loc.Get("spotlight.invalidFolder")); }
        catch (IOException) { ShowError(Loc.Get("spotlight.invalidFolder")); }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        Editor.Focus();
    }

    private void Editor_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (ErrorText != null) ErrorText.Visibility = Visibility.Collapsed;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Button handles its own mouse-down, so only unhandled header clicks drag.
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}

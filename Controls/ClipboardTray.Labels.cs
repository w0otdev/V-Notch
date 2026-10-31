using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;
using VNotch.Services.Clipboard;
using VNotch.ViewModels;

namespace VNotch.Controls;

public sealed partial class ClipboardTray
{
    private int _labelEditorVersion;
    private bool _labelEditorOpen;
    private void SynchronizeLabels()
    {
        if (_controller == null) return;
        var desired = _controller.Store.GetCategoryOrder().ToList();
        if (_optimisticCategoryOrder != null)
        {
            var known = desired.ToHashSet(StringComparer.Ordinal);
            var preview = _optimisticCategoryOrder.ToHashSet(StringComparer.Ordinal);
            desired = _optimisticCategoryOrder.Where(known.Contains)
                .Concat(desired.Where(id => !preview.Contains(id))).ToList();
        }
        if (_categoryItems.Select(item => item.Name).SequenceEqual(desired))
            return;
        CancelCategoryReorder();
        var existing = _categoryItems.ToDictionary(item => item.Name);
        var labels = _controller.Store.GetLabels().ToDictionary(label => label.Id);
        _categoryItems = desired.Select(name =>
        {
            if (existing.TryGetValue(name, out var category)) return category;
            return ClipboardLabel.IsCustomId(name)
                ? new ClipboardCategoryViewModel(TrayIconKind.Shopping, name, labels[name].Name)
                : new ClipboardCategoryViewModel(Enum.Parse<TrayIconKind>(name));
        }).ToArray();


        CategoryPanel.ItemsSource = _categoryItems;
    }

    private void LocalizeLabelActions()
    {
        AddLabelButton.ToolTip = Loc.Get("clipboard.addLabel");
        SelectAllButton.ToolTip = Loc.Get("clipboard.selectAll");
        System.Windows.Automation.AutomationProperties.SetName(AddLabelButton, Loc.Get("clipboard.addLabel"));
        System.Windows.Automation.AutomationProperties.SetName(SelectAllButton, Loc.Get("clipboard.selectAll"));
        LabelEditorTitle.Text = Loc.Get("clipboard.addLabel");
        LabelEditorHint.Text = Loc.Get("clipboard.labelHint");
        SaveLabelButton.Content = Loc.Get("clipboard.createLabel");
        CancelLabelButton.Content = Loc.Get("clipboard.cancel");
        System.Windows.Automation.AutomationProperties.SetName(LabelNameBox, Loc.Get("clipboard.labelName"));
        UpdateSelectionActions();
    }

    private void Cards_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionActions();

    private void UpdateSelectionActions()
    {
        if (DeleteSelectedButton == null || Cards == null) return;
        DeleteSelectedButton.Visibility = Cards.SelectedItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTrayButton.Visibility = Cards.SelectedItems.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        string label = Loc.Get("clipboard.deleteSelected", Cards.SelectedItems.Count);
        DeleteSelectedButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(DeleteSelectedButton, label);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SelectAllTrayItems(null);
    private async void DeleteSelected_Click(object sender, RoutedEventArgs e) => await SafeAsync(() => ConfirmDeleteAsync(SelectedCards()));

    private void AddLabel_Click(object sender, RoutedEventArgs e)
    {
        if (_controller == null || _disposed) return;
        ++_labelEditorVersion;
        _labelEditorOpen = true;
        EndSelection();
        foreach (var menu in _openMenus.ToArray()) menu.IsOpen = false;
        LabelNameBox.Clear();
        LabelEditorError.Text = "";
        TrayContentGrid.IsHitTestVisible = false;
        TrayMotion.SetBlurRadius(TrayContentGrid, 12.0);
        ShowTransientLayer(LabelEditorPanel);
        AnimateLabelEditorBorder(entering: true);
        LabelNameBox.Focus();
    }

    private void CloseLabelEditor(bool immediately = false)
    {
        bool wasOpen = _labelEditorOpen || LabelEditorPanel.Visibility == Visibility.Visible;
        _labelEditorOpen = false;
        ++_labelEditorVersion;
        if (!_deleteConfirmationOpen)
        {
            TrayContentGrid.IsHitTestVisible = true;
            TrayMotion.SetBlurRadius(TrayContentGrid, 0.0);
        }
        if (!immediately && wasOpen && !_disposed && !_trayLeaving && IsVisible)
            AnimateLabelEditorBorder(entering: false);
        DismissTransientLayer(LabelEditorPanel, immediately: immediately);
        LabelNameBox.Clear();
        if (wasOpen && !_disposed && !_trayLeaving && IsVisible) AddLabelButton.Focus();
    }

    private void LabelEditorBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, LabelEditorPanel))
        {
            CloseLabelEditor();
            e.Handled = true;
        }
    }

    private void AnimateLabelEditorBorder(bool entering)
    {
        if (LabelEditorBorder?.RenderTransform is not TransformGroup group ||
            group.Children.OfType<ScaleTransform>().FirstOrDefault() is not { } scale ||
            group.Children.OfType<TranslateTransform>().FirstOrDefault() is not { } translate) return;

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        translate.BeginAnimation(TranslateTransform.XProperty, null);

        if (AnimationConfig.ReduceMotion)
        {
            scale.ScaleX = scale.ScaleY = 1.0;
            translate.X = translate.Y = 0.0;
            return;
        }

        if (entering)
        {
            scale.ScaleX = scale.ScaleY = 1.0;
            translate.X = translate.Y = 0.0;
            var scaleAnim = new DoubleAnimationUsingKeyFrames
            {
                KeyFrames =
                {
                    new DiscreteDoubleKeyFrame(0.92, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                    new SplineDoubleKeyFrame(1.02, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150)), new KeySpline(0.2, 0.85, 0.35, 1.0)),
                    new SplineDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240)), new KeySpline(0.4, 0.0, 0.2, 1.0))
                }
            };
            Timeline.SetDesiredFrameRate(scaleAnim, AnimationConfig.TargetFps);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);

            var slideAnim = new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = TrayMotion.EaseOut,
                FillBehavior = FillBehavior.Stop
            };
            Timeline.SetDesiredFrameRate(slideAnim, AnimationConfig.TargetFps);
            translate.BeginAnimation(TranslateTransform.YProperty, slideAnim);
        }
        else
        {
            var scaleAnim = new DoubleAnimation(1.0, 0.95, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = TrayMotion.EaseOut,
                FillBehavior = FillBehavior.Stop
            };
            Timeline.SetDesiredFrameRate(scaleAnim, AnimationConfig.TargetFps);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);

            var slideAnim = new DoubleAnimation(0, 6, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = TrayMotion.EaseOut,
                FillBehavior = FillBehavior.Stop
            };
            Timeline.SetDesiredFrameRate(slideAnim, AnimationConfig.TargetFps);
            translate.BeginAnimation(TranslateTransform.YProperty, slideAnim);
        }
    }

    private void ShakeLabelEditor()
    {
        if (AnimationConfig.ReduceMotion || LabelEditorBorder?.RenderTransform is not TransformGroup group ||
            group.Children.OfType<TranslateTransform>().FirstOrDefault() is not { } translate) return;
        var shake = new DoubleAnimationUsingKeyFrames
        {
            FillBehavior = FillBehavior.Stop,
            KeyFrames =
            {
                new SplineDoubleKeyFrame(-7, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(45)), new KeySpline(0.2, 0.8, 0.4, 1)),
                new SplineDoubleKeyFrame(6, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(105)), new KeySpline(0.4, 0, 0.2, 1)),
                new SplineDoubleKeyFrame(-4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(165)), new KeySpline(0.4, 0, 0.2, 1)),
                new SplineDoubleKeyFrame(2, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(215)), new KeySpline(0.4, 0, 0.2, 1)),
                new SplineDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)), new KeySpline(0.2, 0.8, 0.4, 1))
            }
        };
        Timeline.SetDesiredFrameRate(shake, AnimationConfig.TargetFps);
        translate.BeginAnimation(TranslateTransform.XProperty, shake);
    }

    private void CancelLabel_Click(object sender, RoutedEventArgs e) => CloseLabelEditor();
    private async void SaveLabel_Click(object sender, RoutedEventArgs e) => await SaveLabelAsync();
    private async void LabelName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SaveLabelAsync();
    }

    private async Task SaveLabelAsync()
    {
        if (_controller == null || !SaveLabelButton.IsEnabled) return;
        int version = _labelEditorVersion;
        SaveLabelButton.IsEnabled = false;
        try
        {
            var label = await _controller.Store.AddLabelAsync(LabelNameBox.Text);
            if (_disposed || version != _labelEditorVersion || !_labelEditorOpen) return;
            CloseLabelEditor();
            RebuildCategories();
            SelectCategory(label.Id);
            CategoryScroll.ScrollToRightEnd();
        }
        catch (ArgumentException)
        {
            LabelEditorError.Text = Loc.Get("clipboard.invalidLabel");
            ShakeLabelEditor();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("CLIPBOARD", ex.GetType().Name);
            LabelEditorError.Text = Loc.Get("clipboard.error");
            ShakeLabelEditor();
        }
        finally { SaveLabelButton.IsEnabled = true; }
    }

}

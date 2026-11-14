using System.Runtime.InteropServices;
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
    [LibraryImport(NativeDll)]
    private static partial int vn_clipboard_selection_merge_optimistic_order(
        int desiredCount,
        ReadOnlySpan<int> optimisticToDesiredIndex,
        int optimisticCount,
        Span<byte> scratchTakenMask,
        Span<int> outMergedDesiredIndices,
        out int outMergedCount);

    private int _labelEditorVersion;
    private bool _labelEditorOpen;
    private void SynchronizeLabels()
    {
        if (_controller == null) return;
        var storeOrder = _controller.Store.GetCategoryOrder();
        int desiredCount = storeOrder.Count;
        if (desiredCount == 0)
        {
            if (_categoryItems.Length != 0)
            {
                CancelCategoryReorder();
                _categoryItems = [];
                CategoryPanel.ItemsSource = _categoryItems;
            }
            return;
        }

        Span<int> mergedIndices = desiredCount <= 256 ? stackalloc int[desiredCount] : new int[desiredCount];
        int mergedCount;
        if (_optimisticCategoryOrder is { Length: > 0 } optimistic)
        {
            int optCount = optimistic.Length;
            Span<int> optToDesired = optCount <= 256 ? stackalloc int[optCount] : new int[optCount];
            for (int i = 0; i < optCount; i++)
            {
                optToDesired[i] = IndexOfCategoryName(storeOrder, optimistic[i]);
            }
            Span<byte> scratchTaken = desiredCount <= 256 ? stackalloc byte[desiredCount] : new byte[desiredCount];
            _ = vn_clipboard_selection_merge_optimistic_order(
                desiredCount, optToDesired, optCount, scratchTaken, mergedIndices, out mergedCount);
        }
        else
        {
            mergedCount = desiredCount;
            for (int i = 0; i < desiredCount; i++)
                mergedIndices[i] = i;
        }

        if (MatchesCurrentCategoryOrder(storeOrder, mergedIndices[..mergedCount]))
            return;

        CancelCategoryReorder();
        var existingItems = _categoryItems;
        var labels = _controller.Store.GetLabels();
        var nextItems = new ClipboardCategoryViewModel[mergedCount];
        for (int i = 0; i < mergedCount; i++)
        {
            string name = storeOrder[mergedIndices[i]];
            nextItems[i] = FindExistingCategoryItem(existingItems, name) ??
                (ClipboardLabel.IsCustomId(name)
                    ? new ClipboardCategoryViewModel(TrayIconKind.Shopping, name, FindCustomLabelName(labels, name))
                    : new ClipboardCategoryViewModel(Enum.Parse<TrayIconKind>(name)));
        }
        _categoryItems = nextItems;
        CategoryPanel.ItemsSource = _categoryItems;
    }

    private static int IndexOfCategoryName(IReadOnlyList<string> storeOrder, string name)
    {
        for (int i = 0; i < storeOrder.Count; i++)
        {
            if (string.Equals(storeOrder[i], name, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    private bool MatchesCurrentCategoryOrder(IReadOnlyList<string> storeOrder, ReadOnlySpan<int> mergedIndices)
    {
        if (_categoryItems.Length != mergedIndices.Length)
            return false;

        for (int i = 0; i < mergedIndices.Length; i++)
        {
            if (!string.Equals(_categoryItems[i].Name, storeOrder[mergedIndices[i]], StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static ClipboardCategoryViewModel? FindExistingCategoryItem(ClipboardCategoryViewModel[] items, string name)
    {
        for (int i = 0; i < items.Length; i++)
        {
            if (string.Equals(items[i].Name, name, StringComparison.Ordinal))
                return items[i];
        }
        return null;
    }

    private static string FindCustomLabelName(IReadOnlyList<ClipboardLabel> labels, string id)
    {
        for (int i = 0; i < labels.Count; i++)
        {
            if (string.Equals(labels[i].Id, id, StringComparison.Ordinal))
                return labels[i].Name;
        }
        return id;
    }

    private static T? FindTransform<T>(TransformCollection children) where T : Transform
    {
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is T match)
                return match;
        }
        return null;
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
        CloseAllOpenMenus();
        LabelNameBox.Clear();
        LabelEditorError.Text = "";
        TrayContentGrid.IsHitTestVisible = false;
        TrayContentGrid.Effect = null;
        TrayContentGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(0.38, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
        TrayMotion.SetBlurRadius(TrayContentGrid, 0.0);
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
            TrayContentGrid.Effect = null;
            TrayContentGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(immediately ? 0 : 220))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
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
            FindTransform<ScaleTransform>(group.Children) is not { } scale ||
            FindTransform<TranslateTransform>(group.Children) is not { } translate) return;

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
            FindTransform<TranslateTransform>(group.Children) is not { } translate) return;
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

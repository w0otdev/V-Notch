using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;
using VNotch.ViewModels;

namespace VNotch.Controls;

public sealed partial class ClipboardTray
{
    private sealed record CategoryPreview(Transform Original, TranslateTransform Offset, int Index);
    private readonly Dictionary<Button, CategoryPreview> _categoryReorderPreviews = [];
    private bool _categoryReorderVisualsReady;
    private bool _categoryReorderGapPlaced;
    private Button? _categoryLandingButton;
    private int _categoryLandingVersion;

    private Point CategorySlotOrigin(ClipboardCategoryViewModel category) =>
        CategoryPanel.ItemContainerGenerator.ContainerFromItem(category) is ContentPresenter presenter
            ? presenter.TranslatePoint(new Point(), CategoryScroll) : new Point();

    private void BeginCategoryReorderVisuals()
    {
        if (_categoryReorderSource is not { } source || _categoryReorderVisualsReady) return;
        StopCategoryReorderLanding();
        _categoryReorderVisualsReady = true;
        _categoryReorderGapPlaced = false;
        ToolTipService.SetIsEnabled(source, false);
        SetButtonScale(source, 1, 0);
        source.Opacity = 0;
        CategorySelection.Opacity = 0;
        for (int index = 0; index < _categoryItems.Length; index++)
        {
            var category = _categoryItems[index];
            if (CategoryButton(category) is not { } button || button == source) continue;
            var original = button.RenderTransform;
            var offset = new TranslateTransform();
            button.RenderTransform = new TransformGroup { Children = { original, offset } };
            _categoryReorderPreviews[button] = new CategoryPreview(original, offset, index);
        }
        CategoryReorderGhost.DataContext = source.DataContext;
        CategoryReorderGhost.Width = CategoryReorderMarker.Width = source.ActualWidth;
        CategoryReorderGhost.Height = CategoryReorderMarker.Height = source.ActualHeight;
        CategoryReorderOverlay.Visibility = Visibility.Visible;
        CategoryReorderGhostScale.ScaleX = CategoryReorderGhostScale.ScaleY = 1;
        if (!AnimationConfig.ReduceMotion)
        {
            AnimateCategoryReorderValue(CategoryReorderGhostScale, ScaleTransform.ScaleXProperty, 1.04, 130);
            AnimateCategoryReorderValue(CategoryReorderGhostScale, ScaleTransform.ScaleYProperty, 1.04, 130);
        }
    }

    private void UpdateCategoryReorderPreview(ClipboardCategoryViewModel source)
    {
        if (!_categoryReorderVisualsReady || _categoryReorderSource is not { } sourceButton) return;
        var clip = new Rect(0, -8, CategoryScroll.ActualWidth, CategoryScroll.ActualHeight + 16);
        if (CategoryReorderOverlay.Clip is not RectangleGeometry geometry || geometry.Rect != clip)
            CategoryReorderOverlay.Clip = new RectangleGeometry(clip);
        CategoryReorderGhost.Width = CategoryReorderMarker.Width = sourceButton.ActualWidth;
        CategoryReorderGhost.Height = CategoryReorderMarker.Height = sourceButton.ActualHeight;
        int sourceIndex = Array.IndexOf(_categoryItems, source);
        int destination = _categoryReorderBeforeId == null ? _categoryItems.Length - 1 :
            Array.FindIndex(_categoryItems, category => category.Name == _categoryReorderBeforeId);
        if (_categoryReorderBeforeId != null && destination > sourceIndex) destination--;
        double span = sourceButton.ActualWidth + sourceButton.Margin.Left + sourceButton.Margin.Right;
        foreach (var (_, preview) in _categoryReorderPreviews)
        {
            int index = preview.Index;
            double shift = 0;
            if (sourceIndex < destination && index > sourceIndex && index <= destination)
                shift = -span;
            else if (sourceIndex > destination && index >= destination && index < sourceIndex)
                shift = span;

            AnimateCategoryReorderValue(preview.Offset, TranslateTransform.XProperty, shift, 170);
        }

        double x;
        if (_categoryReorderBeforeId != null)
        {
            var before = _categoryItems.First(category => category.Name == _categoryReorderBeforeId);
            x = CategorySlotOrigin(before).X - (Array.IndexOf(_categoryItems, before) > sourceIndex ? span : 0);
        }
        else
        {
            var last = _categoryItems[^1];
            var lastButton = CategoryButton(last)!;
            x = CategorySlotOrigin(last).X + lastButton.ActualWidth + lastButton.Margin.Right - span;
        }
        Canvas.SetTop(CategoryReorderMarker, Math.Max(0, (CategoryScroll.ActualHeight - sourceButton.ActualHeight) / 2));
        AnimateCategoryReorderValue(CategoryReorderGapOffset, TranslateTransform.XProperty, x, _categoryReorderGapPlaced ? 170 : 0);
        _categoryReorderGapPlaced = true;
    }

    private void ResetCategoryReorderVisuals()
    {
        StopCategoryReorderLanding();
        foreach (var (button, preview) in _categoryReorderPreviews)
        {
            preview.Offset.BeginAnimation(TranslateTransform.XProperty, null);
            button.RenderTransform = preview.Original;
        }
        _categoryReorderPreviews.Clear();
        _categoryReorderVisualsReady = false;
        _categoryReorderGapPlaced = false;
        CategoryReorderGapOffset.BeginAnimation(TranslateTransform.XProperty, null);
        CategoryReorderGhostScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CategoryReorderGhostScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CategoryReorderGhostScale.ScaleX = CategoryReorderGhostScale.ScaleY = 1;
    }

    private void BeginCategoryReorderLanding(string id, Point origin)
    {
        if (AnimationConfig.ReduceMotion || _disposed || !IsVisible || _trayLeaving ||
            _categoryItems.FirstOrDefault(category => category.Name == id) is not { } category ||
            CategoryButton(category) is not { } button) return;
        StopCategoryReorderLanding();
        int version = _categoryLandingVersion;
        _categoryLandingButton = button;
        button.Opacity = 0;
        CategoryReorderGhost.DataContext = category;
        CategoryReorderGhost.Width = button.ActualWidth;
        CategoryReorderGhost.Height = button.ActualHeight;
        CategoryReorderGhost.Opacity = 1;
        CategoryReorderGhostScale.ScaleX = CategoryReorderGhostScale.ScaleY = 1.04;
        CategoryReorderMarker.Visibility = Visibility.Collapsed;
        CategoryReorderOverlay.Visibility = Visibility.Visible;
        Canvas.SetLeft(CategoryReorderGhost, origin.X);
        Canvas.SetTop(CategoryReorderGhost, origin.Y);
        var target = button.TranslatePoint(new Point(), CategoryScroll);
        AnimateCategoryReorderValue(CategoryReorderGhost, Canvas.LeftProperty, target.X, 150);
        AnimateCategoryReorderValue(CategoryReorderGhost, Canvas.TopProperty, target.Y, 150);
        AnimateCategoryReorderValue(CategoryReorderGhostScale, ScaleTransform.ScaleXProperty, 1, 150);
        AnimateCategoryReorderValue(CategoryReorderGhostScale, ScaleTransform.ScaleYProperty, 1, 150);
        // A versioned clock owns cleanup. A new press or hide invalidates it.
        var settle = new DoubleAnimation(1, 1, TimeSpan.FromMilliseconds(160));
        Timeline.SetDesiredFrameRate(settle, AnimationConfig.TargetFps);
        settle.Completed += (_, _) => { if (version == _categoryLandingVersion) StopCategoryReorderLanding(); };
        CategoryReorderGhost.BeginAnimation(OpacityProperty, settle);
    }

    private void StopCategoryReorderLanding()
    {
        ++_categoryLandingVersion;
        if (_categoryLandingButton != null) _categoryLandingButton.Opacity = 1;
        _categoryLandingButton = null;
        CategoryReorderGhost.BeginAnimation(Canvas.LeftProperty, null);
        CategoryReorderGhost.BeginAnimation(Canvas.TopProperty, null);
        CategoryReorderGhost.BeginAnimation(OpacityProperty, null);
        CategoryReorderOverlay.Visibility = Visibility.Collapsed;
        CategoryReorderGhost.DataContext = null;
    }

    private static void AnimateCategoryReorderValue(DependencyObject target, DependencyProperty property, double value, int duration)
    {
        // Base values hold destinations, so pointer frames never restart a clock.
        double previous = (double)target.GetValue(property);
        double destination = target is Animatable animatable ? (double)animatable.GetAnimationBaseValue(property) :
            (double)((UIElement)target).GetAnimationBaseValue(property);
        if (Math.Abs(destination - value) < 1e-6 && duration > 0 && !AnimationConfig.ReduceMotion) return;
        DoubleAnimation? animation = duration <= 0 || AnimationConfig.ReduceMotion ? null :
            new DoubleAnimation(previous, value, TimeSpan.FromMilliseconds(duration))
            { EasingFunction = TrayMotion.EaseOut, FillBehavior = FillBehavior.Stop };
        if (animation != null) Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        if (target is Animatable motion)
        {
            motion.BeginAnimation(property, null);
            motion.SetValue(property, value);
            if (animation != null) motion.BeginAnimation(property, animation);
        }
        else if (target is UIElement element)
        {
            element.BeginAnimation(property, null);
            element.SetValue(property, value);
            if (animation != null) element.BeginAnimation(property, animation);
        }
    }
}

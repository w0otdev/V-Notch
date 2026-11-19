using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow
{
    private Panel? _revealingPage;
    private readonly HashSet<UIElement> _revealedComponents = new();
    private readonly List<UIElement> _visibleRevealBatch = new();
    private int _firstBatchDelay;
    private double _nextRevealAbove, _nextRevealBelow;
    private bool _waitingForRevealLayout;

    private void AnimateActivePanel(string section, int baseDelayMs = 0)
    {
        ResetProgressiveBlurElements(isMenuList: false);
        if (!_navPanels.TryGetValue(section, out var page) || !IsLoaded || !page.IsVisible ||
            AnimationConfig.ReduceMotion || !AnimationConfig.ClientAreaAnimation) return;

        _revealingPage = page;
        _firstBatchDelay = baseDelayMs;
        page.SizeChanged += RevealPageResized;
        RevealVisibleComponents();
    }

    private void StopComponentReveal()
    {
        if (_revealingPage != null)
        {
            _revealingPage.SizeChanged -= RevealPageResized;
            _revealingPage.LayoutUpdated -= RevealViewportAfterLayout;
        }
        _waitingForRevealLayout = false;
        _revealingPage = null;
        _revealedComponents.Clear();
    }

    private void RevealViewportAfterLayout(object? sender, EventArgs e) => RevealVisibleComponents();
    private void RevealPageResized(object sender, SizeChangedEventArgs e) => RevealVisibleComponents();

    private void RevealSettingsRows(Panel group)
    {
        if (_revealingPage == null || !group.IsVisible || !_revealingPage.IsAncestorOf(group)) return;
        _revealedComponents.RemoveWhere(item => group.IsAncestorOf(item) && !_activeBlurredElements.ContainsKey(item));
        RevealVisibleComponents();
    }

    private void RevealVisibleComponents()
    {
        if (_revealingPage is not { IsVisible: true } page || !SettingsScrollViewer.IsAncestorOf(page)) return;
        if (!page.IsArrangeValid || !SettingsScrollViewer.IsArrangeValid || SettingsScrollViewer.ViewportHeight <= 0)
        {
            if (!_waitingForRevealLayout) page.LayoutUpdated += RevealViewportAfterLayout;
            _waitingForRevealLayout = true;
            return;
        }
        if (_waitingForRevealLayout) page.LayoutUpdated -= RevealViewportAfterLayout;
        _waitingForRevealLayout = false;

        _visibleRevealBatch.Clear();
        _nextRevealAbove = double.NegativeInfinity;
        _nextRevealBelow = double.PositiveInfinity;
        CollectVisibleComponents(page, new Rect(SettingsScrollViewer.RenderSize));
        if (_visibleRevealBatch.Count == 0) return;
        ApplyProgressiveBlurCascade(_visibleRevealBatch, _firstBatchDelay, direction: -1, isMenuList: false,
            Math.Min(60, AnimationConfig.TargetFps), appendComponents: true);
        _firstBatchDelay = 0;
    }

    private void CollectVisibleComponents(FrameworkElement element, Rect viewport)
    {
        if (!element.IsVisible || element.RenderSize.Width <= 0 || element.RenderSize.Height <= 0 ||
            _revealedComponents.Contains(element)) return;
        var bounds = element.TransformToAncestor(SettingsScrollViewer).TransformBounds(new Rect(element.RenderSize));
        if (!bounds.IntersectsWith(viewport))
        {
            // Between these offsets scrolling cannot expose another component.
            double offset = SettingsScrollViewer.VerticalOffset;
            if (bounds.Top > viewport.Bottom)
                _nextRevealBelow = Math.Min(_nextRevealBelow, offset + bounds.Top - viewport.Bottom);
            else if (bounds.Bottom < viewport.Top)
                _nextRevealAbove = Math.Max(_nextRevealAbove, offset + bounds.Bottom - viewport.Top);
            return;
        }

        // Traverse authored layout, including trees and disabled groups. Widgets keep
        // their own templates so a button, slider or combo box moves as one control.
        if (element is Expander expander)
        {
            if (expander.Template?.FindName("HeaderSite", expander) is FrameworkElement header)
                CollectVisibleComponents(header, viewport);
            else if (expander.Header is FrameworkElement headerContent)
                CollectVisibleComponents(headerContent, viewport);
            if (expander.IsExpanded && expander.Content is FrameworkElement content && expander.IsAncestorOf(content))
                CollectVisibleComponents(content, viewport);
            return;
        }
        if (element is ItemsControl items && items is not ComboBox)
        {
            for (int i = 0; i < items.Items.Count; i++)
                if (items.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement container)
                    CollectVisibleComponents(container, viewport);
            return;
        }
        // Reveal framed images as one visual so the background and the image share a clock.
        if (element is Panel and not Canvas or Border { Child: not null and not Image } or ContentPresenter or ListBoxItem)
        {
            int count = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < count; i++)
                if (VisualTreeHelper.GetChild(element, i) is FrameworkElement child)
                    CollectVisibleComponents(child, viewport);
            return;
        }

        _revealedComponents.Add(element);
        _visibleRevealBatch.Add(element);
    }
}

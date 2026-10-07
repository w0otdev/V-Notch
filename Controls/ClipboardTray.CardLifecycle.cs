using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.ViewModels;

namespace VNotch.Controls;

public partial class ClipboardTray
{
    // Shared realization state belongs to the view lifecycle, independent of selection or thumbnail work.
    private readonly HashSet<FrameworkElement> _realizedCardElements = [];

    private void Card_Loaded(object sender, RoutedEventArgs e)
    {
        if (_disposed || sender is not FrameworkElement element) return;
        if (element is Border border && border.ActualWidth > 0 && border.ActualHeight > 0)
        {
            var bounds = new Rect(0, 0, border.ActualWidth, border.ActualHeight);
            if (border.Clip is not RectangleGeometry existing || existing.Rect != bounds)
            {
                var clip = new RectangleGeometry(bounds, 16, 16);
                clip.Freeze();
                border.Clip = clip;
            }
        }
        _realizedCardElements.Add(element);
        RefreshCardAge(element);
        RefreshCardViewport(element);
        QueueCardContentRefresh();
    }

    private void Card_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        _realizedCardElements.Remove(element);
        CancelThumbnailLoad(element); ForgetCardViewport(element); ResetCardMotion(element);
        if (element.DataContext is ClipboardCardViewModel card)
        {
            card.IsMenuOpen = false;
            ReleaseCardPreview(card);
        }
    }

    private void Card_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        CancelThumbnailLoad(element); ForgetCardViewport(element); ResetCardMotion(element);
        if (ItemsControl.ContainerFromElement(Cards, element) is ListBoxItem container)
        {
            ResetCardMotion(container);
            if (element.IsLoaded && e.NewValue is ClipboardCardViewModel current && _arrivingCards.Remove(current.Entry.Id))
                AnimateCardLifecycle(container, entering: true, fresh: true);
        }
        if (e.OldValue is ClipboardCardViewModel previous)
        {
            previous.IsMenuOpen = false;
            ReleaseCardPreview(previous);
        }
        if (!_realizedCardElements.Contains(element)) return;
        RefreshCardAge(element);
        RefreshCardViewport(element);
        QueueCardContentRefresh();
    }

    private void Card_VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if (e.NewValue is true)
        {
            RefreshCardAge(element);
            RefreshCardViewport(element);
            QueueCardContentRefresh();
        }
        else
        {
            CancelThumbnailLoad(element);
            ForgetCardViewport(element);
            if (element.DataContext is ClipboardCardViewModel card) ReleaseCardPreview(card);
        }
    }
}

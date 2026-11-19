using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Clipboard;
using VNotch.ViewModels;

namespace VNotch.Controls;

public sealed partial class ClipboardTray
{
    internal sealed record ThumbnailLoad(ClipboardCardViewModel Card, CancellationTokenSource Cancellation)
    {
        public Task Completion { get; set; } = Task.CompletedTask;
    }
    internal Dictionary<FrameworkElement, ThumbnailLoad> ThumbnailLoads => _thumbnailLoads;
    private readonly Dictionary<FrameworkElement, ThumbnailLoad> _thumbnailLoads = [];
    // Remember unsuccessful lookups too. Missing icons/corrupt images must not
    // enqueue disk/decode work on every pixel of scrolling.
    private readonly Dictionary<FrameworkElement, ClipboardCardViewModel> _completedContentLoads = [];
    private DispatcherOperation? _cardContentRefresh;
    private UiScheduledTimer? _entranceWorkTimer;

    private bool IsEntranceWorkDeferred => _entranceWorkTimer?.IsEnabled == true;

    private void DeferEntranceWork()
    {
        StopEntranceWork();
        if (AnimationConfig.ReduceMotion) return;
        // Visibility/Loaded can queue work before BeginEntrance. Cancel that work
        // as well so decoding and result reconciliation cannot interrupt the reveal.
        _cardContentRefresh?.Abort();
        _cardContentRefresh = null;
        CancelThumbnailLoads();
        CancelSearch();
        _searchTimer.Stop();
        _entranceWorkTimer ??= CreateEntranceWorkTimer();
        // With no cards on screen only the header is animating (40 + 320 ms).
        // A populated tray also waits for the final card's stagger to finish.
        _entranceWorkTimer.Interval = TimeSpan.FromMilliseconds(_cards.Count == 0 ? 360 : 740);
        _entranceWorkTimer.Start();
    }

    private UiScheduledTimer CreateEntranceWorkTimer()
    {
        var timer = new UiScheduledTimer(DispatcherPriority.Background);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_disposed || _trayLeaving || !IsLoaded || !IsVisible) return;
            QueueCardContentRefresh();
            if (_queryDirty) _ = SafeAsync(RefreshAsync);
        };
        return timer;
    }

    private void StopEntranceWork() => _entranceWorkTimer?.Stop();

    private bool IsCardInViewport(FrameworkElement element)
    {
        if (_disposed || _trayLeaving || !element.IsLoaded || !element.IsVisible ||
            !Cards.IsLoaded || !Cards.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return false;
        if (!TryGetPositionInCards(element, out var origin)) return false;
        return new Rect(origin, element.RenderSize).IntersectsWith(new Rect(Cards.RenderSize));
    }

    private void QueueCardContentRefresh()
    {
        if (_disposed || _trayLeaving || IsEntranceWorkDeferred || _cardContentRefresh?.Status == DispatcherOperationStatus.Pending) return;
        _cardContentRefresh = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _cardContentRefresh = null;
            if (_disposed || _trayLeaving || IsEntranceWorkDeferred || !IsLoaded || !IsVisible) return;
            // Cached containers remain IsVisible outside the clip. Only the
            // viewport should compete for the two decoding slots.
            foreach (var element in _realizedCardElements)
            {
                bool visible = IsCardInViewport(element);
                RefreshCardViewportVisibility(element, visible);
                if (visible && element.DataContext is ClipboardCardViewModel card && NeedsCardContent(element, card))
                    _ = SafeAsync(() => LoadCardContentAsync(element));
                else if (!visible) CancelThumbnailLoad(element);
            }
        }));
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        QueueCardContentRefresh();
    }

    private static ClipboardImagePreview.PixelTarget CardPixelTarget(FrameworkElement element)
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(element);
        return new((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY));
    }

    private static bool NeedsCardContent(FrameworkElement element, ClipboardCardViewModel card) =>
        card.NeedsMaskedPreviewSize ||
        (card.Entry.Kind == ClipboardKind.Image && (card.Image == null || card.PreviewTarget != CardPixelTarget(element))) ||
        (card.Entry.Kind == ClipboardKind.File && card.PreferredFileIcon == null && card.FileIcon == null) ||
        (card.PreferredSourceIcon == null && card.SourceIcon == null && card.Entry.SourceExecutable.Length != 0);

    private Task LoadCardContentAsync(FrameworkElement element)
    {
        if (_disposed || _trayLeaving || IsEntranceWorkDeferred) return Task.CompletedTask;
        if (element.DataContext is not ClipboardCardViewModel card || !_realizedCardElements.Contains(element) ||
            !IsCardInViewport(element)) return Task.CompletedTask;
        var controller = _controller;
        if (controller == null || card.Entry.IsPersonal && !controller.Store.IsPersonalUnlocked) return Task.CompletedTask;
        if (!NeedsCardContent(element, card)) return Task.CompletedTask;
        if (_completedContentLoads.TryGetValue(element, out var completed) && ReferenceEquals(completed, card) &&
            (card.Entry.Kind != ClipboardKind.Image || card.PreviewTarget == CardPixelTarget(element)))
            return Task.CompletedTask;
        if (_thumbnailLoads.TryGetValue(element, out var existing) && ReferenceEquals(existing.Card, card)) return existing.Completion;
        CancelThumbnailLoad(element);
        CancellationTokenSource linkedCts;
        try { linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_contentLifetime.Token); }
        catch (ObjectDisposedException) { return Task.CompletedTask; }
        var load = new ThumbnailLoad(card, linkedCts);
        _thumbnailLoads.Add(element, load);
        load.Completion = LoadThumbnailAsync(element, load, controller);
        return load.Completion;
    }

    private async Task LoadThumbnailAsync(FrameworkElement element, ThumbnailLoad load, ClipboardHistoryController controller)
    {
        var card = load.Card;
        var cancellation = load.Cancellation.Token;
        bool acquired = false;
        ClipboardImagePreview.PixelTarget? attemptedTarget = null;
        bool IsCurrent() => !_disposed && !cancellation.IsCancellationRequested && IsCardInViewport(element)
            && _realizedCardElements.Contains(element) && ReferenceEquals(element.DataContext, card)
            && (!card.Entry.IsPersonal || controller.Store.IsPersonalUnlocked);
        try
        {
            await _thumbnailSlots.WaitAsync(cancellation);
            acquired = true;
            if (!IsCurrent()) return;
            await card.PrepareMaskedPreviewAsync(controller.Store, cancellation);
            if (!IsCurrent()) return;
            if (card.PreferredSourceIcon == null && card.SourceIcon == null && card.Entry.SourceExecutable.Length != 0)
            {
                var icon = await Task.Run(() => FileIconProvider.GetAppIcon(card.Entry.SourceExecutable), cancellation);
                if (!IsCurrent()) return;
                card.SourceIcon = icon;
            }
            if (card.Entry.Kind == ClipboardKind.File && card.PreferredFileIcon == null && card.FileIcon == null && !string.IsNullOrEmpty(card.FileExtension))
            {
                var icon = await Task.Run(() => FileIconProvider.GetExtensionIcon(card.FileExtension, small: false), cancellation);
                if (!IsCurrent()) return;
                if (icon != null) card.FileIcon = icon;
            }
            if (card.Entry.Kind != ClipboardKind.Image || !IsCurrent()) return;
            var target = CardPixelTarget(element);
            if (card.Image != null && card.PreviewTarget == target) return;
            attemptedTarget = target;
            var image = await controller.Store.GetImagePreviewAsync(card.Entry.Id, cancellation, target);
            if (IsCurrent()) card.Image = image;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { /* Intentionally ignored. */ }
        catch (Exception ex) when (IsContentError(ex)) { /* Intentionally ignored. */ }
        finally
        {
            if (IsCurrent())
            {
                _completedContentLoads[element] = card;
                if (attemptedTarget is { } target)
                {
                    card.PreviewTarget = target;
                    if (target != CardPixelTarget(element)) QueueCardContentRefresh();
                }
            }
            if (_thumbnailLoads.TryGetValue(element, out var current) && ReferenceEquals(current, load))
                _thumbnailLoads.Remove(element);
            if (acquired)
            {
                try { _thumbnailSlots.Release(); }
                catch (ObjectDisposedException) { /* Intentionally ignored. */ }
            }
            load.Cancellation.Dispose();
        }
    }

    internal static BitmapSource CreateBitmap(byte[] bytes) =>
        ClipboardImagePreview.Decode(bytes, isPersonal: false, CancellationToken.None);

    private void CancelThumbnailLoad(FrameworkElement element)
    {
        _completedContentLoads.Remove(element);
        if (_thumbnailLoads.Remove(element, out var load))
        {
            try { load.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { /* Intentionally ignored. */ }
        }
        // The task owns disposal until its worker has released the slot and wiped private bytes.
    }

    private void CancelThumbnailLoads(bool personalOnly = false)
    {
        foreach (var pair in _completedContentLoads.ToArray())
            if (!personalOnly || pair.Value.Entry.IsPersonal) _completedContentLoads.Remove(pair.Key);
        foreach (var pair in _thumbnailLoads.ToArray())
            if (!personalOnly || pair.Value.Card.Entry.IsPersonal) CancelThumbnailLoad(pair.Key);
    }

}

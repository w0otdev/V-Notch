using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using VNotch.ViewModels;

namespace VNotch.Controls;

public partial class ClipboardTray
{
    private sealed record ThumbnailLoad(ClipboardCardViewModel Card, CancellationTokenSource Cancellation)
    {
        public Task Completion { get; set; } = Task.CompletedTask;
    }
    private readonly Dictionary<FrameworkElement, ThumbnailLoad> _thumbnailLoads = [];
    // Remember unsuccessful lookups too. Missing icons/corrupt images must not
    // enqueue disk/decode work on every pixel of scrolling.
    private readonly Dictionary<FrameworkElement, ClipboardCardViewModel> _completedContentLoads = [];
    private DispatcherOperation? _cardContentRefresh;
    private DispatcherTimer? _entranceWorkTimer;

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
        _searchCancellation?.Cancel();
        _searchTimer.Stop();
        _entranceWorkTimer ??= CreateEntranceWorkTimer();
        _entranceWorkTimer.Start();
    }

    private DispatcherTimer CreateEntranceWorkTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            // Last stagger: 60ms + 7 * 35ms + 360ms.
            Interval = TimeSpan.FromMilliseconds(665)
        };
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
                if (visible && element.DataContext is ClipboardCardViewModel card && NeedsCardContent(card))
                    _ = SafeAsync(() => LoadCardContentAsync(element));
                else if (!visible) CancelThumbnailLoad(element);
            }
        }));
    }

    private static bool NeedsCardContent(ClipboardCardViewModel card) =>
        (card.Entry.Kind == ClipboardKind.Image && card.Image == null) ||
        (card.Entry.Kind == ClipboardKind.File && card.FileIcon == null) ||
        (card.SourceIcon == null && card.Entry.SourceExecutable.Length != 0);

    private Task LoadCardContentAsync(FrameworkElement element)
    {
        if (IsEntranceWorkDeferred) return Task.CompletedTask;
        if (element.DataContext is not ClipboardCardViewModel card || !_realizedCardElements.Contains(element) ||
            !IsCardInViewport(element)) return Task.CompletedTask;
        var controller = _controller;
        if (controller == null || card.Entry.IsPersonal && !controller.Store.IsPersonalUnlocked) return Task.CompletedTask;
        if (!NeedsCardContent(card)) return Task.CompletedTask;
        if (_completedContentLoads.TryGetValue(element, out var completed) && ReferenceEquals(completed, card))
            return Task.CompletedTask;
        if (_thumbnailLoads.TryGetValue(element, out var existing) && ReferenceEquals(existing.Card, card)) return existing.Completion;
        CancelThumbnailLoad(element);
        var load = new ThumbnailLoad(card, CancellationTokenSource.CreateLinkedTokenSource(_contentLifetime.Token));
        _thumbnailLoads.Add(element, load);
        load.Completion = LoadThumbnailAsync(element, load, controller);
        return load.Completion;
    }

    private async Task LoadThumbnailAsync(FrameworkElement element, ThumbnailLoad load, ClipboardHistoryController controller)
    {
        var card = load.Card;
        var cancellation = load.Cancellation.Token;
        byte[]? bytes = null;
        bool acquired = false;
        bool IsCurrent() => !cancellation.IsCancellationRequested && IsCardInViewport(element)
            && _realizedCardElements.Contains(element) && ReferenceEquals(element.DataContext, card)
            && (!card.Entry.IsPersonal || controller.Store.IsPersonalUnlocked);
        try
        {
            await _thumbnailSlots.WaitAsync(cancellation);
            acquired = true;
            if (!IsCurrent()) return;
            if (card.SourceIcon == null && card.Entry.SourceExecutable.Length != 0)
            {
                var icon = await Task.Run(() => FileIconProvider.GetAppIcon(card.Entry.SourceExecutable), cancellation);
                if (!IsCurrent()) return;
                card.SourceIcon = icon;
            }
            if (card.Entry.Kind == ClipboardKind.File && card.FileIcon == null && !string.IsNullOrEmpty(card.FileExtension))
            {
                var icon = await Task.Run(() => FileIconProvider.GetExtensionIcon(card.FileExtension, small: false), cancellation);
                if (!IsCurrent()) return;
                if (icon != null) card.FileIcon = icon;
            }
            if (card.Entry.Kind != ClipboardKind.Image || card.Image != null || !IsCurrent()) return;
            bytes = await controller.Store.GetImageAsync(card.Entry.Id, cancellation);
            if (bytes == null || !IsCurrent()) return;
            var image = await Task.Run(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                using var stream = new MemoryStream(bytes);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                var frame = decoder.Frames[0];
                bool landscape = frame.PixelWidth >= frame.PixelHeight;
                stream.Position = 0;
                var bitmap = new BitmapImage();
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                // Bound both portrait and landscape previews. Width alone can
                // decode a very tall screenshot into a huge bitmap.
                if (landscape) bitmap.DecodePixelWidth = Math.Min(420, frame.PixelWidth);
                else bitmap.DecodePixelHeight = Math.Min(420, frame.PixelHeight);
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                cancellation.ThrowIfCancellationRequested();
                // Copy only the decoded pixels so a retained preview cannot
                // keep its MemoryStream and the original full-size PNG alive.
                int stride = (bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8;
                var pixels = new byte[stride * bitmap.PixelHeight];
                try
                {
                    bitmap.CopyPixels(pixels, stride, 0);
                    var preview = BitmapSource.Create(bitmap.PixelWidth, bitmap.PixelHeight, bitmap.DpiX, bitmap.DpiY,
                        bitmap.Format, bitmap.Palette, pixels, stride);
                    preview.Freeze();
                    cancellation.ThrowIfCancellationRequested();
                    return preview;
                }
                finally { if (card.Entry.IsPersonal) CryptographicOperations.ZeroMemory(pixels); }
            }, cancellation);
            if (IsCurrent()) card.Image = image;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (IsContentError(ex)) { }
        finally
        {
            if (IsCurrent()) _completedContentLoads[element] = card;
            if (_thumbnailLoads.TryGetValue(element, out var current) && ReferenceEquals(current, load))
                _thumbnailLoads.Remove(element);
            if (bytes != null && card.Entry.IsPersonal) CryptographicOperations.ZeroMemory(bytes);
            if (acquired) _thumbnailSlots.Release();
            load.Cancellation.Dispose();
        }
    }

    private void CancelThumbnailLoad(FrameworkElement element)
    {
        _completedContentLoads.Remove(element);
        if (_thumbnailLoads.Remove(element, out var load)) load.Cancellation.Cancel();
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

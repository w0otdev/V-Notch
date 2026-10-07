using System.Windows;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services.Clipboard;

namespace VNotch;

public partial class MainWindow
{
    private DispatcherTimer? _clipboardDragTimer;
    private bool _clipboardDragAutoExpanded;
    private bool _clipboardDragCanDrop;
    internal void CancelDragDropTimers()
    {
        _clipboardDragTimer?.Stop();
        _clipboardDragTimer = null;
    }
    private void NotchWrapper_DragEnter(object sender, DragEventArgs e)
    {
        if (_clipboardHistory?.IsDragging == true) return;
        _clipboardDragCanDrop = ClipboardDropReader.Supports(e.Data);
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            _clipboardDragCanDrop = e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files
                && files.All(ClipboardImportPaths.IsLocal) && files.Length <= ClipboardHistoryStore.MaxImportItems;
        e.Effects = _clipboardDragCanDrop ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true;
        if (!_clipboardDragCanDrop) return;
        SetClipboardDropHover(true);
        KeepClipboardDropTargetOpen();
        _clipboardDragAutoExpanded |= !_isExpanded;
        _transitionCoordinator.RequestView(NotchView.Secondary, "ClipboardDragEnter");
    }
    private void KeepClipboardDropTargetOpen()
    {
        CancelDragDropTimers();
        _hoverCollapseTimer.Stop();
        _hoverThumbnailDelayTimer.Stop();
    }
    private void NotchWrapper_PreviewDragOver(object sender, DragEventArgs e)
        => KeepClipboardDropTargetOpen();

    private void NotchWrapper_PreviewDrop(object sender, DragEventArgs e)
    {
        // Category buttons handle Drop themselves, so clean up before bubbling.
        KeepClipboardDropTargetOpen();
        _clipboardDragAutoExpanded = false;
        _clipboardDragCanDrop = false;
        SetClipboardDropHover(false);
    }
    private void NotchWrapper_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = _clipboardHistory?.IsDragging != true && _clipboardDragCanDrop
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private void NotchWrapper_DragLeave(object sender, DragEventArgs e)
    {
        CancelDragDropTimers();
        // OLE drag/drop does not reliably update WPF IsMouseOver. Moving between
        // child targets can also raise DragLeave without leaving the notch.
        // Defer until the next target can cancel this callback, then use screen geometry.
        _clipboardDragTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _clipboardDragTimer.Tick += (_, _) =>
        {
            if (_isAnimating) return;
            CompleteClipboardDragLeave(IsCursorInsideNotchVisual());
        };
        _clipboardDragTimer.Start();
    }
    private void CompleteClipboardDragLeave(bool cursorInside)
    {
        CancelDragDropTimers();
        if (cursorInside) return;
        SetClipboardDropHover(false);
        _clipboardDragCanDrop = false;
        bool collapse = _clipboardDragAutoExpanded;
        _clipboardDragAutoExpanded = false;
        if (collapse) CollapseNotch();
    }
    private async void NotchWrapper_DragDrop(object sender, DragEventArgs e)
    {
        CancelDragDropTimers(); _clipboardDragAutoExpanded = false; _clipboardDragCanDrop = false;
        e.Handled = true;
        if (_clipboardHistory == null || _clipboardHistory.IsDragging || !ClipboardDropReader.Supports(e.Data))
        { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Copy;
        _transitionCoordinator.RequestView(NotchView.Secondary, "ClipboardDrop");
        if (ClipboardTrayView != null) await ClipboardTrayView.ImportCurrentCategoryDropAsync(e.Data);
        else await _clipboardHistory.ImportDropAsync(e.Data);
    }
}


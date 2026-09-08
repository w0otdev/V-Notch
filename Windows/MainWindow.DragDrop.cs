using System;
using System.Windows;
using System.Windows.Threading;
using VNotch.Controllers;

namespace VNotch;

public partial class MainWindow
{
    #region Drag-to-Open

    private DispatcherTimer? _dragWaitTimer;
    private DispatcherTimer? _dragCollapseTimer;
    private DispatcherTimer? _dropProcessTimer;
    private DispatcherTimer? _shelfReadyTimer;
    private DispatcherTimer? _collapseWaitTimer;

    internal void CancelDragDropTimers()
    {
        _dragWaitTimer?.Stop();
        _dragWaitTimer = null;
        _dragCollapseTimer?.Stop();
        _dragCollapseTimer = null;
        _dropProcessTimer?.Stop();
        _dropProcessTimer = null;
        _shelfReadyTimer?.Stop();
        _shelfReadyTimer = null;
        _collapseWaitTimer?.Stop();
        _collapseWaitTimer = null;
    }

    private void InitializeDragDropController()
    {
        _dragDropController.ExpandRequested += () => ExpandNotch();
        _dragDropController.SwitchToSecondaryRequested += () => SwitchToSecondaryView();
        _dragDropController.SwitchToPrimaryRequested += () => SwitchToPrimaryView();
        _dragDropController.CollapseRequested += () => CollapseNotch();
        _dragDropController.FilesAccepted += files => _fileShelf.EnqueueFiles(files);
        _dragDropController.UnlockPromptRequested += (files, count) =>
        {
            _pendingUnlockFiles = files;
            ShowShelfUnlockBanner(count);
        };
        _dragDropController.DropRejected += msg => SetShelfDropRejectVisualState(msg);
    }

    private void NotchWrapper_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;

        _dragCollapseTimer?.Stop();
        _dragCollapseTimer = null;

        bool wasExpanded = _isExpanded;

        bool handled = _dragDropController.HandleDragEnter(
            hasFiles: true,
            isExpanded: _isExpanded,
            isAnimating: _isAnimating,
            isSecondaryView: _isSecondaryView);

        if (handled && ((!wasExpanded && _dragDropController.IsDragAutoExpanded) || (_isExpanded && _isAnimating)))
        {
            StartDragWaitForShelf();
        }
    }

    private void NotchWrapper_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void NotchWrapper_DragLeave(object sender, DragEventArgs e)
    {
        if (!_dragDropController.HandleDragLeave()) return;

        _dragCollapseTimer?.Stop();
        _dragCollapseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _dragCollapseTimer.Tick += (s, args) =>
        {
            _dragCollapseTimer?.Stop();
            _dragCollapseTimer = null;
            _dragDropController.AutoCollapseAfterDrag(_isExpanded, _isSecondaryView, _isAnimating);

            if (_isExpanded && !_isSecondaryView && _isAnimating)
            {
                _collapseWaitTimer?.Stop();
                var collapseDeadlineUtc = DateTime.UtcNow.AddMilliseconds(1500);
                _collapseWaitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
                _collapseWaitTimer.Tick += (s2, args2) =>
                {
                    if (!_isAnimating || DateTime.UtcNow >= collapseDeadlineUtc)
                    {
                        _collapseWaitTimer?.Stop();
                        _collapseWaitTimer = null;
                        _dragDropController.CollapseAfterViewSwitch(_isSecondaryView, _isAnimating);
                    }
                };
                _collapseWaitTimer.Start();
            }
        };
        _dragCollapseTimer.Start();
    }

    private void NotchWrapper_DragDrop(object sender, DragEventArgs e)
    {
        CancelDragDropTimers();

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Handled = true;
            _dragDropController.Reset();
            return;
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;

        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (files == null || files.Length == 0)
        {
            return;
        }

        if (!_isExpanded)
        {
            ExpandNotch();
        }

        var dropDeadlineUtc = DateTime.UtcNow.AddMilliseconds(1500);
        _dropProcessTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _dropProcessTimer.Tick += (s, args) =>
        {
            if (!_isAnimating || DateTime.UtcNow >= dropDeadlineUtc)
            {
                _dropProcessTimer?.Stop();
                _dropProcessTimer = null;

                if (!_isSecondaryView)
                {
                    SwitchToSecondaryView();
                    var shelfDeadlineUtc = DateTime.UtcNow.AddMilliseconds(1500);
                    _shelfReadyTimer?.Stop();
                    _shelfReadyTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(40)
                    };
                    _shelfReadyTimer.Tick += (s2, args2) =>
                    {
                        if (!_isAnimating || DateTime.UtcNow >= shelfDeadlineUtc)
                        {
                            _shelfReadyTimer?.Stop();
                            _shelfReadyTimer = null;
                            _dragDropController.HandleDrop(files);
                        }
                    };
                    _shelfReadyTimer.Start();
                }
                else
                {
                    _dragDropController.HandleDrop(files);
                }
            }
        };
        _dropProcessTimer.Start();
    }

    private void StartDragWaitForShelf()
    {
        _dragWaitTimer?.Stop();
        var waitDeadlineUtc = DateTime.UtcNow.AddMilliseconds(1500);
        _dragWaitTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _dragWaitTimer.Tick += (s, args) =>
        {
            if ((_isExpanded && !_isAnimating) || DateTime.UtcNow >= waitDeadlineUtc)
            {
                _dragWaitTimer?.Stop();
                _dragWaitTimer = null;
                _dragDropController.OnAnimationCompleted(_isExpanded, _isSecondaryView);
            }
        };
        _dragWaitTimer.Start();
    }

    #endregion
}

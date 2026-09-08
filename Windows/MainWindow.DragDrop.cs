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
            _dragDropController.AutoCollapseAfterDrag(_isExpanded, _isSecondaryView, _isAnimating);

            if (_isExpanded && !_isSecondaryView && _isAnimating)
            {
                var collapseWait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
                collapseWait.Tick += (s2, args2) =>
                {
                    if (!_isAnimating)
                    {
                        collapseWait.Stop();
                        _dragDropController.CollapseAfterViewSwitch(_isSecondaryView, _isAnimating);
                    }
                };
                collapseWait.Start();
            }
        };
        _dragCollapseTimer.Start();
    }

    private void NotchWrapper_DragDrop(object sender, DragEventArgs e)
    {
        _dragWaitTimer?.Stop();
        _dragCollapseTimer?.Stop();

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

        var dropProcessTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        dropProcessTimer.Tick += (s, args) =>
        {
            if (!_isAnimating)
            {
                dropProcessTimer.Stop();

                if (!_isSecondaryView)
                {
                    SwitchToSecondaryView();
                    var shelfReadyTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(40)
                    };
                    shelfReadyTimer.Tick += (s2, args2) =>
                    {
                        if (!_isAnimating)
                        {
                            shelfReadyTimer.Stop();
                            _dragDropController.HandleDrop(files);
                        }
                    };
                    shelfReadyTimer.Start();
                }
                else
                {
                    _dragDropController.HandleDrop(files);
                }
            }
        };
        dropProcessTimer.Start();
    }

    private void StartDragWaitForShelf()
    {
        _dragWaitTimer?.Stop();
        _dragWaitTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _dragWaitTimer.Tick += (s, args) =>
        {
            if (_isExpanded && !_isAnimating)
            {
                _dragWaitTimer?.Stop();
                _dragDropController.OnAnimationCompleted(_isExpanded, _isSecondaryView);
            }
        };
        _dragWaitTimer.Start();
    }

    #endregion
}

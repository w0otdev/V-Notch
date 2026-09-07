using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private FileShelfController _fileShelf = null!;

    private bool _isSecondaryView
    {
        get => _notchState.IsSecondaryView;
        set
        {
            if (value && !_notchState.IsSecondaryView)
                _notchState.TryTransitionTo(NotchState.SecondaryView);
            else if (!value && _notchState.IsSecondaryView)
                _notchState.TryTransitionTo(NotchState.Expanded);

            if (value) _viewModel.SetView(VNotch.Models.NotchView.Secondary);
            else if (_viewModel.CurrentView == VNotch.Models.NotchView.Secondary)
                _viewModel.SetView(VNotch.Models.NotchView.Media);
        }
    }
    private DateTime _lastViewSwitchUtc = DateTime.MinValue;
    private static readonly TimeSpan ViewSwitchCooldown = TimeSpan.FromMilliseconds(600);
    private bool _isScrollSessionLocked = false;
    private System.Windows.Threading.DispatcherTimer? _scrollSessionResetTimer;

    private static readonly SolidColorBrush _brushShelfItemBg = CreateFrozenBrush(37, 37, 37);
    private static readonly SolidColorBrush _brushShelfItemBorder = CreateFrozenBrush(51, 51, 51);
    private static readonly SolidColorBrush _brushShelfSelectedBg = CreateFrozenBrush(255, 255, 255, 40);
    private static readonly SolidColorBrush _brushShelfSelectedBorder = CreateFrozenBrush(255, 255, 255, 140);

    private Point _selectionStart;
    private bool _isSelecting = false;
    private Point _dragStartPoint;
    private bool _didDragOutFromShelf = false;
    private bool _isSweepSelecting = false;
    private string? _sweepStartFile = null;
    private bool _wasSelectedOnMouseDown = false;
    private HashSet<string> _selectionInitialState = new();

    private bool _isCameraSectionExpanded = false;
    private int _cameraSectionAnimToken = 0;
    private double _cameraSectionCompactWidth = 0;
    private double _cameraSectionCompactHeight = 0;
    private Thickness _cameraSectionCompactMargin = new Thickness(0, 0, 8, 0);

    private string[]? _pendingUnlockFiles = null;

    private const int ShelfVirtualizationOverscanColumns = 1;
    private bool _shelfUsesSmallItems;
    private int _shelfRowCount = 1;
    private double _shelfCellWidth = 66;
    private double _shelfCellHeight = 82;
    private bool _shelfViewportRefreshQueued;

    #region File Shelf Visual State

    private static readonly SolidColorBrush _brushShelfNormalBg = CreateFrozenBrush(26, 26, 26);
    private static readonly SolidColorBrush _brushShelfActiveBg = CreateFrozenBrush(42, 42, 42);
    private static readonly SolidColorBrush _brushShelfErrorBg = CreateFrozenBrush(56, 24, 24);
    private static readonly SolidColorBrush _brushShelfNormalBorder = CreateFrozenBrush(51, 51, 51);
    private static readonly SolidColorBrush _brushShelfActiveBorder = CreateFrozenBrush(153, 255, 255, 255);
    private static readonly SolidColorBrush _brushShelfErrorBorder = CreateFrozenBrush(255, 107, 107);
    private static readonly SolidColorBrush _brushShelfStatusWarning = CreateFrozenBrush(255, 196, 120);
    private static readonly SolidColorBrush _brushShelfStatusError = CreateFrozenBrush(255, 107, 107);
    private static readonly SolidColorBrush _brushShelfUnlockHintBg = CreateFrozenBrush(42, 36, 20);
    private static readonly SolidColorBrush _brushShelfUnlockHintBorder = CreateFrozenBrush(255, 196, 120);

    #endregion

    #region Controller Initialization & Event Wiring

    private void InitializeFileShelfController()
    {
        _fileShelf = new FileShelfController(_settings, _settingsService);

        _fileShelf.FileReadyToAdd += OnShelfFileReadyToAdd;
        _fileShelf.AddQueueDrained += OnShelfAddQueueDrained;
        _fileShelf.CapacityChanged += OnShelfCapacityChanged;
        _fileShelf.LayoutRefreshRequested += OnShelfLayoutRefreshRequested;
        _fileShelf.PinStateChanged += OnShelfPinStateChanged;
        _fileShelf.FileExternallyRemoved += OnShelfFileExternallyRemoved;
        _fileShelf.FileExternallyRenamed += OnShelfFileExternallyRenamed;

        UpdateShelfCapacityIndicator();
    }

    private void OnShelfFileReadyToAdd(string filePath)
    {
        RefreshShelfLayout();

        var lastItem = FindRealizedShelfItem(filePath);
        if (lastItem != null)
        {
            lastItem.Opacity = 0;
            lastItem.RenderTransformOrigin = new Point(0.5, 0.5);
            var st = new ScaleTransform(0.75, 0.75);
            lastItem.RenderTransform = st;

            var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(200))) { EasingFunction = _easeExpOut6 };
            var scaleIn = new DoubleAnimation(0.75, 1, new Duration(TimeSpan.FromMilliseconds(280))) { EasingFunction = _easeMenuSpring };
            Timeline.SetDesiredFrameRate(fadeIn, VNotch.Services.AnimationConfig.TargetFps);
            Timeline.SetDesiredFrameRate(scaleIn, VNotch.Services.AnimationConfig.TargetFps);

            fadeIn.Completed += (s2, e2) =>
            {
                lastItem.Opacity = 1;
                lastItem.BeginAnimation(OpacityProperty, null);
                lastItem.RenderTransform = new ScaleTransform(1, 1);
            };

            lastItem.BeginAnimation(OpacityProperty, fadeIn);
            st.BeginAnimation(ScaleTransform.ScaleXProperty, scaleIn);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, scaleIn);

            AnimateShelfItemProgress(lastItem);
        }

        if (_shelfProcessNextTimer == null)
        {
            _shelfProcessNextTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
            _shelfProcessNextTimer.Tick += (s, args) =>
            {
                _shelfProcessNextTimer.Stop();
                _fileShelf.ProcessNext();
            };
        }
        _shelfProcessNextTimer.Start();
    }

    private void OnShelfCapacityChanged()
    {
        UpdateShelfCapacityIndicator();
    }

    private void OnShelfAddQueueDrained()
    {
        SyncShelfFilesToClipboard();
    }

    private void SyncShelfFilesToClipboard()
    {
        if (!_settings.CopyShelfFilesToClipboard)
            return;

        var files = _fileShelf.Files;
        if (files.Count == 0)
            return;

        var paths = new StringCollection();
        foreach (var file in files)
            paths.Add(file);

        try
        {
            var data = new DataObject();
            data.SetFileDropList(paths);
            Clipboard.SetDataObject(data, true);
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("SHELF-CLIPBOARD", $"Failed to copy shelf files to clipboard: {ex.Message}");
        }
    }

    private void OnShelfLayoutRefreshRequested()
    {
        if (_shelfPinDirty)
        {
            AnimatedPinReorder();
            _shelfPinDirty = false;
        }
        else
        {
            RefreshShelfLayout();
        }
    }

    private bool _shelfPinDirty = false;
    private void OnShelfPinStateChanged()
    {
        _shelfPinDirty = true;
    }

    private string? _lastToggledPinPath = null;

    private void AnimatedPinReorder()
    {
        string? toggledPath = _lastToggledPinPath;
        _lastToggledPinPath = null;

        var oldPositions = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in ShelfItemsContainer.Children)
        {
            if (child is Border b && b.Tag is string path)
            {
                var pos = b.TranslatePoint(new Point(0, 0), ShelfItemsContainer);
                oldPositions[path] = pos;
            }
        }

        RefreshShelfLayout(forceFullRebuild: true);
        ShelfItemsContainer.UpdateLayout();

        var dur = TimeSpan.FromMilliseconds(300);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        foreach (var child in ShelfItemsContainer.Children)
        {
            if (child is Border b && b.Tag is string path && oldPositions.TryGetValue(path, out var oldPos))
            {
                var newPos = b.TranslatePoint(new Point(0, 0), ShelfItemsContainer);
                double dx = oldPos.X - newPos.X;
                double dy = oldPos.Y - newPos.Y;

                if (Math.Abs(dx) < 2 && Math.Abs(dy) < 2) continue;

                var tt = new TranslateTransform(dx, dy);
                b.RenderTransform = tt;

                var animX = new DoubleAnimation(dx, 0, dur) { EasingFunction = ease };
                var animY = new DoubleAnimation(dy, 0, dur) { EasingFunction = ease };
                Timeline.SetDesiredFrameRate(animX, VNotch.Services.AnimationConfig.TargetFps);
                Timeline.SetDesiredFrameRate(animY, VNotch.Services.AnimationConfig.TargetFps);

                animX.Completed += (_, _) =>
                {
                    tt.BeginAnimation(TranslateTransform.XProperty, null);
                    tt.BeginAnimation(TranslateTransform.YProperty, null);
                    b.RenderTransform = new ScaleTransform(1, 1);
                };

                tt.BeginAnimation(TranslateTransform.XProperty, animX);
                tt.BeginAnimation(TranslateTransform.YProperty, animY);
            }
        }
    }

    private void OnShelfFileExternallyRemoved(string filePath)
    {
        Dispatcher.BeginInvoke(() =>
        {
            AnimateFileDeletion(new[] { filePath }, () =>
            {
                _fileShelf.RemoveFile(filePath);
                RefreshShelfLayout();
            });
        });
    }

    private void OnShelfFileExternallyRenamed(string oldPath, string newPath)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _fileShelf.HandleExternalRename(oldPath, newPath);
        });
    }

    #endregion

    #region Drop Visual State

    private void ResetShelfDropVisualState()
    {
        // In the Liquid Glass skin the tray's resting state must keep the
        // translucent glass material (so the refracted backdrop shows through),
        // otherwise it snaps back to a solid dark panel as soon as files land or a
        // drag ends. The active/reject/unlock states below keep their solid
        // feedback colours — they're only shown transiently during a drag.
        if (IsLiquidGlassEnabled)
        {
            FileShelf.Background = _glassPanelBg;
            FileShelf.BorderBrush = _glassPanelBorder;
            FileShelf.BorderThickness = new Thickness(1);
            FileShelfDashedBorder.Stroke = _glassDashStroke;
        }
        else
        {
            FileShelf.Background = _brushShelfNormalBg;
            FileShelf.BorderBrush = null;
            FileShelf.BorderThickness = new Thickness(0);
            FileShelfDashedBorder.Stroke = _brushShelfNormalBorder;
        }
        FileShelfDashedBorder.StrokeDashArray = new DoubleCollection { 4, 3 };
        UpdateShelfCapacityIndicator();
    }

    private void SetShelfDropAcceptVisualState()
    {
        FileShelf.Background = _brushShelfActiveBg;
        FileShelfDashedBorder.Stroke = _brushShelfActiveBorder;
        FileShelfDashedBorder.StrokeDashArray = new DoubleCollection { 2, 1 };
        UpdateShelfCapacityIndicator();
    }

    private void SetShelfDropRejectVisualState(string message)
    {
        FileShelf.Background = _brushShelfErrorBg;
        FileShelfDashedBorder.Stroke = _brushShelfErrorBorder;
        FileShelfDashedBorder.StrokeDashArray = new DoubleCollection { 2, 1 };
        UpdateShelfCapacityIndicator(message, isError: true);
    }

    private void SetShelfDropUnlockHintVisualState()
    {
        FileShelf.Background = _brushShelfUnlockHintBg;
        FileShelfDashedBorder.Stroke = _brushShelfUnlockHintBorder;
        FileShelfDashedBorder.StrokeDashArray = new DoubleCollection { 2, 1 };
        UpdateShelfCapacityIndicator(Loc.Get("shelf.unlockHint"), isError: false);
    }

    #endregion

    #region Capacity Indicator

    private void UpdateShelfCapacityIndicator(string? transientMessage = null, bool isError = false)
    {
        if (ShelfStatusText == null || ShelfPlaceholder == null || ShelfCountText == null || ShelfCountBadge == null)
            return;

        ShelfPlaceholder.Text = Loc.Get("shelf.placeholder");
        var shelfEmpty = _fileShelf.FileCount == 0;
        ShelfPlaceholder.Visibility = shelfEmpty ? Visibility.Visible : Visibility.Collapsed;
        ShelfPlaceholderPanel.Visibility = shelfEmpty ? Visibility.Visible : Visibility.Collapsed;

        ShelfCountText.Text = _fileShelf.GetCountDisplayText();
        ShelfCountText.Foreground = _fileShelf.IsCountWarning ? _brushShelfStatusWarning : Brushes.White;
        ShelfCountBadge.Visibility = _isSecondaryView && ShelfUnlockBanner.Visibility != Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!string.IsNullOrWhiteSpace(transientMessage))
        {
            ShelfStatusText.Text = transientMessage;
            ShelfStatusText.Foreground = isError ? _brushShelfStatusError : _brushShelfStatusWarning;
            ShelfStatusText.Visibility = Visibility.Visible;
            return;
        }

        var statusMsg = _fileShelf.GetStatusMessage();
        if (statusMsg != null)
        {
            ShelfStatusText.Text = statusMsg;
            ShelfStatusText.Foreground = _brushShelfStatusWarning;
            ShelfStatusText.Visibility = Visibility.Visible;
            return;
        }

        ShelfStatusText.Visibility = Visibility.Collapsed;
    }

    #endregion

    #region Drag & Drop Events

    private DateTime _lastDragOverValidation = DateTime.MinValue;
    private FileShelfController.DropValidation? _cachedDragValidation;

    private void FileShelf_DragEnter(object sender, DragEventArgs e)
    {
        var files = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? e.Data.GetData(DataFormats.FileDrop) as string[]
            : null;

        var validation = _fileShelf.ValidateDrop(files);
        _cachedDragValidation = validation;
        _lastDragOverValidation = DateTime.UtcNow;

        ApplyDragValidationVisual(validation, e);
        e.Handled = true;
    }

    private void FileShelf_DragOver(object sender, DragEventArgs e)
    {
        var now = DateTime.UtcNow;
        if (_cachedDragValidation != null && (now - _lastDragOverValidation).TotalMilliseconds < 100)
        {
            ApplyDragValidationVisual(_cachedDragValidation, e);
            e.Handled = true;
            return;
        }

        var files = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? e.Data.GetData(DataFormats.FileDrop) as string[]
            : null;

        var validation = _fileShelf.ValidateDrop(files);
        _cachedDragValidation = validation;
        _lastDragOverValidation = now;

        ApplyDragValidationVisual(validation, e);
        e.Handled = true;
    }

    private void ApplyDragValidationVisual(FileShelfController.DropValidation validation, DragEventArgs e)
    {
        switch (validation.Result)
        {
            case FileShelfController.DropResult.Accept:
                e.Effects = DragDropEffects.Copy;
                SetShelfDropAcceptVisualState();
                break;
            case FileShelfController.DropResult.UnlockPrompt:
                e.Effects = DragDropEffects.Copy;
                SetShelfDropUnlockHintVisualState();
                break;
            default:
                e.Effects = DragDropEffects.None;
                if (!string.IsNullOrEmpty(validation.Message))
                    SetShelfDropRejectVisualState(validation.Message);
                break;
        }
    }

    private void FileShelf_DragLeave(object sender, DragEventArgs e)
    {
        _cachedDragValidation = null;
        ResetShelfDropVisualState();
        e.Handled = true;
    }

    private void FileShelf_Drop(object sender, DragEventArgs e)
    {
        _cachedDragValidation = null;
        ResetShelfDropVisualState();

        var files = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? e.Data.GetData(DataFormats.FileDrop) as string[]
            : null;

        var validation = _fileShelf.ValidateDrop(files);

        switch (validation.Result)
        {
            case FileShelfController.DropResult.Accept:
                _fileShelf.EnqueueFiles(validation.NewFiles);
                break;
            case FileShelfController.DropResult.UnlockPrompt:
                _pendingUnlockFiles = validation.NewFiles;
                ShowShelfUnlockBanner(validation.FileCount);
                break;
            default:
                if (!string.IsNullOrEmpty(validation.Message))
                    SetShelfDropRejectVisualState(validation.Message);
                break;
        }

        e.Handled = true;
    }

    #endregion

    #region Unlock Banner

    private void ShowShelfUnlockBanner(int fileCount)
    {
        ShelfUnlockBanner.Visibility = Visibility.Visible;
        ShelfCountBadge.Visibility = Visibility.Collapsed;
        ShelfPlaceholder.Visibility = Visibility.Collapsed;
        ShelfPlaceholderPanel.Visibility = Visibility.Collapsed;
        ShelfUnlockMessageText.Text = Loc.Get("shelf.unlockMessage", fileCount);
        ShelfUnlockButtonText.Text = Loc.Get("shelf.unlockButton");
        ShelfUnlockDismissText.Text = Loc.Get("shelf.unlockDismiss");
        ShelfUnlockSettingsHint.Text = Loc.Get("shelf.unlockSettingsHint");
        ShelfUnlockSettingsHint.Visibility = Visibility.Visible;

        ShelfUnlockBanner.Opacity = 0;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeIn, VNotch.Services.AnimationConfig.TargetFps);
        ShelfUnlockBanner.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void ShelfUnlockButton_Click(object sender, RoutedEventArgs e)
    {
        _fileShelf.UnlockLimit();
        HideShelfUnlockBanner();

        if (_pendingUnlockFiles != null && _pendingUnlockFiles.Length > 0)
        {
            _fileShelf.EnqueueFiles(_pendingUnlockFiles);
            _pendingUnlockFiles = null;
        }
    }

    private void ShelfUnlockDismiss_Click(object sender, RoutedEventArgs e)
    {
        _pendingUnlockFiles = null;
        HideShelfUnlockBanner();
    }

    private void HideShelfUnlockBanner()
    {
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (s, e) =>
        {
            ShelfUnlockBanner.Visibility = Visibility.Collapsed;
            ShelfCountBadge.Visibility = _isSecondaryView ? Visibility.Visible : Visibility.Collapsed;
            UpdateShelfCapacityIndicator();
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeOut, VNotch.Services.AnimationConfig.TargetFps);
        ShelfUnlockBanner.BeginAnimation(OpacityProperty, fadeOut);
    }

    #endregion

    #region Shelf Layout & Item Creation

    private static readonly SolidColorBrush _brushShelfHoverBg = CreateFrozenBrush(48, 48, 48);
    private static readonly SolidColorBrush _brushShelfHoverBorder = CreateFrozenBrush(85, 85, 85);

    private DispatcherTimer? _shelfProcessNextTimer;

    private void RefreshShelfLayout(bool forceFullRebuild = false)
    {
        var files = _fileShelf.Files;
        int fileCount = files.Count;
        bool useSmallSize = fileCount > 4;

        bool sizeChanged = _shelfUsesSmallItems != useSmallSize;
        _shelfUsesSmallItems = useSmallSize;
        _shelfRowCount = useSmallSize ? 2 : 1;
        _shelfCellWidth = useSmallSize ? 56 : 66;
        _shelfCellHeight = useSmallSize ? 56 : 82;

        int columnCount = fileCount == 0 ? 0 : (fileCount + _shelfRowCount - 1) / _shelfRowCount;
        ShelfItemsContainer.Width = columnCount * _shelfCellWidth;
        ShelfItemsContainer.Height = useSmallSize ? 112 : 82;

        RealizeShelfViewport(forceFullRebuild || sizeChanged);
    }

    private void RealizeShelfViewport(bool forceFullRebuild = false)
    {
        var files = _fileShelf.Files;
        if (forceFullRebuild)
            ShelfItemsContainer.Children.Clear();

        double viewportWidth = ShelfScrollViewer.ViewportWidth;
        if (!double.IsFinite(viewportWidth) || viewportWidth <= 0)
            viewportWidth = ShelfScrollViewer.ActualWidth;

        var range = ShelfViewportVirtualizer.Calculate(
            files.Count,
            _shelfRowCount,
            _shelfCellWidth,
            ShelfScrollViewer.HorizontalOffset,
            viewportWidth,
            ShelfVirtualizationOverscanColumns);

        var desiredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = range.StartIndex; i < range.EndIndexExclusive; i++)
            desiredPaths.Add(files[i]);

        for (int i = ShelfItemsContainer.Children.Count - 1; i >= 0; i--)
        {
            if (ShelfItemsContainer.Children[i] is not Border border ||
                border.Tag is not string path ||
                !desiredPaths.Contains(path))
            {
                ShelfItemsContainer.Children.RemoveAt(i);
            }
        }

        var realized = new Dictionary<string, Border>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in ShelfItemsContainer.Children)
        {
            if (child is Border border && border.Tag is string path)
                realized[path] = border;
        }

        for (int i = range.StartIndex; i < range.EndIndexExclusive; i++)
        {
            string file = files[i];
            if (!realized.TryGetValue(file, out var item))
            {
                item = (Border)CreateShelfItem(file, _shelfUsesSmallItems);
                ShelfItemsContainer.Children.Add(item);
            }

            UpdateShelfItemVisualState(item, _fileShelf.IsSelected(file));
            int column = i / _shelfRowCount;
            int row = i % _shelfRowCount;
            Canvas.SetLeft(item, column * _shelfCellWidth);
            Canvas.SetTop(item, row * _shelfCellHeight);
        }
    }

    private Border? FindRealizedShelfItem(string filePath)
    {
        foreach (var child in ShelfItemsContainer.Children)
        {
            if (child is Border border && border.Tag is string path &&
                string.Equals(path, filePath, StringComparison.OrdinalIgnoreCase))
            {
                return border;
            }
        }

        return null;
    }

    private FrameworkElement CreateShelfItem(string filePath, bool isSmall)
    {
        var fileName = Path.GetFileName(filePath);
        double width = isSmall ? 48 : 58;
        double height = isSmall ? 48 : 74;
        double iconSize = isSmall ? 26 : 34;

        var border = new Border
        {
            Width = width,
            Height = height,
            Margin = new Thickness(4),
            CornerRadius = new CornerRadius(isSmall ? 8 : 12),
            Background = _brushShelfItemBg,
            BorderBrush = _brushShelfItemBorder,
            BorderThickness = new Thickness(1),
            ToolTip = fileName,
            Tag = filePath,
            Cursor = Cursors.Hand,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1)
        };

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var image = new Image
        {
            Source = null,  // loaded asynchronously below
            Width = iconSize,
            Height = iconSize,
            Margin = new Thickness(0, isSmall ? 2 : 6, 0, isSmall ? 2 : 4),
            Stretch = Stretch.UniformToFill,
            Clip = new RectangleGeometry
            {
                RadiusX = isSmall ? 4 : 6,
                RadiusY = isSmall ? 4 : 6,
                Rect = new Rect(0, 0, iconSize, iconSize)
            }
        };

        // Load the shell icon off the UI thread to avoid blocking layout during the
        // camera-open transition, which realizes the entire visible shelf simultaneously
        // and would otherwise spike memory by calling SHCreateItemFromParsingName /
        // SHGetFileInfo synchronously for every file in a single burst.
        var capturedPath = filePath;
        System.Threading.Tasks.Task.Run(() => GetFileIcon(capturedPath))
            .ContinueWith(t =>
            {
                var icon = t.Result;
                if (icon != null)
                    image.Source = icon;
            }, System.Threading.CancellationToken.None,
            System.Threading.Tasks.TaskContinuationOptions.OnlyOnRanToCompletion,
            System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());

        stack.Children.Add(image);

        if (!isSmall)
        {
            var text = new TextBlock
            {
                Text = fileName,
                Style = (Style)FindResource("SmallText"),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(6, 0, 6, 6),
            };
            stack.Children.Add(text);
        }

        if (_fileShelf.IsPinned(filePath))
        {
            var wrapper = new Grid();
            wrapper.Children.Add(stack);

            var pinIcon = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M14.6358 3.90949C15.2888 3.47412 15.6153 3.25643 15.9711 3.29166C16.3269 3.32689 16.6044 3.60439 17.1594 4.15938L19.8406 6.84062C20.3956 7.39561 20.6731 7.67311 20.7083 8.02888C20.7436 8.38465 20.5259 8.71118 20.0905 9.36424L18.4419 11.8372C17.88 12.68 17.5991 13.1013 17.3749 13.5511C17.2086 13.8845 17.0659 14.2292 16.9476 14.5825C16.7882 15.0591 16.6889 15.5557 16.4902 16.5489L16.2992 17.5038C16.2986 17.5072 16.2982 17.5089 16.298 17.5101C16.1556 18.213 15.3414 18.5419 14.7508 18.1351L14.7455 18.1315C14.7322 18.1223 14.7255 18.1177 14.7189 18.1131C11.2692 15.7225 8.27754 12.7308 5.88691 9.28108L5.86851 9.25451C5.86655 9.25169 5.86558 9.25028 5.86486 9.24924C5.45815 8.65858 5.78704 7.84444 6.4899 7.70202L6.49618 7.70076L7.45114 7.50977C8.44433 7.31113 8.94092 7.21182 9.4175 7.05236C9.77083 6.93415 10.1155 6.79139 10.4489 6.62514C10.8987 6.40089 11.32 6.11998 12.1628 5.55815L14.6358 3.90949Z M5 19L9.5 14.5"),
                Fill = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                Stretch = Stretch.Uniform,
                Width = 10,
                Height = 10,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 3, 3, 0)
            };
            wrapper.Children.Add(pinIcon);
            border.Child = wrapper;
        }
        else
        {
            border.Child = stack;
        }

        AttachShelfItemEvents(border, filePath);
        return border;
    }

    #endregion

    #region Shelf Item Events

    private void AttachShelfItemEvents(Border border, string filePath)
    {
        border.MouseEnter += (s, e) =>
        {
            if (!_fileShelf.IsSelected(filePath))
            {
                border.Background = _brushShelfHoverBg;
                border.BorderBrush = _brushShelfHoverBorder;
            }
            if (!_isSweepSelecting && border.RenderTransform is ScaleTransform st)
                AnimateButtonScale(st, 1.05);
        };
        border.MouseLeave += (s, e) =>
        {
            if (!_fileShelf.IsSelected(filePath))
            {
                border.Background = _brushShelfItemBg;
                border.BorderBrush = _brushShelfItemBorder;
            }
            if (!_isSweepSelecting && border.RenderTransform is ScaleTransform st)
                AnimateButtonScale(st, 1.0);
        };

        border.MouseRightButtonUp += (s, e) =>
        {
            _lastToggledPinPath = filePath;
            _fileShelf.TogglePin(filePath);
            e.Handled = true;
        };

        border.MouseLeftButtonDown += (s, e) =>
        {
            _dragStartPoint = e.GetPosition(null);
            _selectionStart = e.GetPosition(FileShelfGrid);
            _didDragOutFromShelf = false;
            _isSweepSelecting = false;
            _sweepStartFile = filePath;

            _wasSelectedOnMouseDown = _fileShelf.IsSelected(filePath);

            border.CaptureMouse();
            e.Handled = true;

            SelectionRect.Width = 0;
            SelectionRect.Height = 0;

            bool isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (isCtrl)
            {
                _fileShelf.ToggleSelection(filePath);
            }
            else
            {
                if (!_wasSelectedOnMouseDown)
                {
                    _fileShelf.Select(filePath);
                }
            }

            foreach (var child in ShelfItemsContainer.Children)
            {
                if (child is Border b && b.Tag is string p)
                    UpdateShelfItemVisualState(b, _fileShelf.IsSelected(p));
            }
        };

        border.MouseLeftButtonUp += (s, e) =>
        {
            if (_didDragOutFromShelf || _isSweepSelecting || _isSelecting)
            {
                _isSelecting = false;
                _isSweepSelecting = false;
                _sweepStartFile = null;
                SelectionCanvas.Visibility = Visibility.Collapsed;

                if (border.IsMouseCaptured) border.ReleaseMouseCapture();

                foreach (var child in ShelfItemsContainer.Children)
                {
                    if (child is Border b && b.RenderTransform is ScaleTransform st)
                        AnimateButtonScale(st, 1.0);
                }
                return;
            }

            _sweepStartFile = null;
            bool isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (!isCtrl && _fileShelf.SelectedFiles.Count > 1 && _wasSelectedOnMouseDown)
            {
                _fileShelf.Select(filePath);
                foreach (var child in ShelfItemsContainer.Children)
                {
                    if (child is Border b && b.Tag is string p)
                        UpdateShelfItemVisualState(b, _fileShelf.IsSelected(p));
                }
            }

            if (border.IsMouseCaptured) border.ReleaseMouseCapture();
        };

        border.MouseMove += (s, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed && !_isSelecting && !_isSweepSelecting)
            {
                Point currentPosGlobal = e.GetPosition(null);
                Vector diff = _dragStartPoint - currentPosGlobal;

                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    if (_wasSelectedOnMouseDown)
                    {
                        if (border.IsMouseCaptured) border.ReleaseMouseCapture();
                        _didDragOutFromShelf = true;

                        var filesToDrag = _fileShelf.GetDragFiles();
                        var data = new DataObject(DataFormats.FileDrop, filesToDrag);
                        var result = DragDrop.DoDragDrop(border, data, DragDropEffects.Copy | DragDropEffects.Move);

                        if (result == DragDropEffects.Move)
                        {
                            _fileShelf.HandleDragMoveOut(filesToDrag);
                        }
                        return;
                    }

                    Point posInGrid = e.GetPosition(FileShelfGrid);
                    bool isInsideShelf = posInGrid.X >= 0 && posInGrid.Y >= 0 &&
                                         posInGrid.X <= FileShelfGrid.ActualWidth &&
                                         posInGrid.Y <= FileShelfGrid.ActualHeight;

                    if (isInsideShelf)
                    {
                        _isSelecting = true;
                        _isSweepSelecting = true;
                        SelectionCanvas.Visibility = Visibility.Visible;

                        bool isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                        if (isCtrl)
                            _selectionInitialState = new HashSet<string>(_fileShelf.SelectedFiles);
                        else
                            _selectionInitialState.Clear();
                    }
                    else
                    {
                        if (border.IsMouseCaptured) border.ReleaseMouseCapture();
                        _didDragOutFromShelf = true;
                        var data = new DataObject(DataFormats.FileDrop, new[] { filePath });
                        DragDrop.DoDragDrop(border, data, DragDropEffects.Copy | DragDropEffects.Move);
                    }
                }
            }

            if (_isSelecting && border.IsMouseCaptured)
            {
                HandleRectangleSelection(e.GetPosition(FileShelfGrid));
                e.Handled = true;
            }
        };
    }

    #endregion

    #region Rectangle Selection

    private void FileShelf_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_isAnimating) return;

        _dragStartPoint = e.GetPosition(null);
        _selectionStart = e.GetPosition(FileShelfGrid);
        _isSelecting = false;
        _isSweepSelecting = false;
        _wasSelectedOnMouseDown = false;

        bool isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (isCtrl)
        {
            _selectionInitialState = new HashSet<string>(_fileShelf.SelectedFiles);
        }
        else
        {
            _fileShelf.ClearSelection();
            _selectionInitialState.Clear();

            foreach (var child in ShelfItemsContainer.Children)
            {
                if (child is Border item) UpdateShelfItemVisualState(item, false);
            }
        }

        SelectionCanvas.Visibility = Visibility.Collapsed;
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;

        FileShelfGrid.CaptureMouse();
        e.Handled = true;
    }

    private void FileShelf_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            Point posInGrid = e.GetPosition(FileShelfGrid);

            if (!_isSelecting)
            {
                Point currentPosGlobal = e.GetPosition(null);
                Vector diff = _dragStartPoint - currentPosGlobal;

                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    _isSelecting = true;
                    _selectionStart = posInGrid;
                    SelectionCanvas.Visibility = Visibility.Visible;
                }
            }

            if (_isSelecting)
            {
                HandleRectangleSelection(posInGrid);
            }
        }
        else if (_isSelecting)
        {
            CancelLassoSelection();
        }
    }

    private void HandleRectangleSelection(Point currentPos)
    {
        if (!_isSelecting) return;

        double gridWidth = FileShelfGrid.ActualWidth;
        double gridHeight = FileShelfGrid.ActualHeight;

        double startX = _selectionStart.X;
        double startY = _selectionStart.Y;
        double endX = Math.Max(0, Math.Min(gridWidth, currentPos.X));
        double endY = Math.Max(0, Math.Min(gridHeight, currentPos.Y));

        double x = Math.Min(startX, endX);
        double y = Math.Min(startY, endY);
        double width = Math.Abs(startX - endX);
        double height = Math.Abs(startY - endY);

        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = width;
        SelectionRect.Height = height;

        Rect selectionRect = new Rect(x, y, width, height);
        bool isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        var intersected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int childCount = ShelfItemsContainer.Children.Count;
        for (int i = 0; i < childCount; i++)
        {
            if (ShelfItemsContainer.Children[i] is Border item && item.Tag is string path)
            {
                GeneralTransform transform = item.TransformToAncestor(FileShelfGrid);
                Rect itemBounds = transform.TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));

                if (selectionRect.IntersectsWith(itemBounds))
                    intersected.Add(path);
            }
        }

        _fileShelf.ApplyRectangleSelection(intersected, isCtrl, _selectionInitialState);

        for (int i = 0; i < childCount; i++)
        {
            if (ShelfItemsContainer.Children[i] is Border item && item.Tag is string path)
                UpdateShelfItemVisualState(item, _fileShelf.IsSelected(path));
        }
    }

    private void UpdateShelfItemVisualState(Border item, bool isSelected)
    {
        var targetBg = isSelected ? _brushShelfSelectedBg : _brushShelfItemBg;
        var targetBorder = isSelected ? _brushShelfSelectedBorder : _brushShelfItemBorder;

        if (item.Background != targetBg) item.Background = targetBg;
        if (item.BorderBrush != targetBorder) item.BorderBrush = targetBorder;
    }

    private void FileShelf_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isSweepSelecting)
        {
            _isSweepSelecting = false;
            _sweepStartFile = null;

            foreach (var child in ShelfItemsContainer.Children)
            {
                if (child is Border b)
                    AnimateButtonScale((ScaleTransform)b.RenderTransform!, 1.0);
            }
        }
        _sweepStartFile = null;

        _isSelecting = false;
        SelectionCanvas.Visibility = Visibility.Collapsed;

        if (FileShelfGrid.IsMouseCaptured)
            FileShelfGrid.ReleaseMouseCapture();
    }

    private void FileShelfGrid_LostMouseCapture(object sender, MouseEventArgs e)
    {
        CancelLassoSelection();
    }

    private void CancelLassoSelection()
    {
        if (!_isSelecting && !FileShelfGrid.IsMouseCaptured) return;

        _isSelecting = false;
        SelectionCanvas.Visibility = Visibility.Collapsed;

        if (FileShelfGrid.IsMouseCaptured)
            FileShelfGrid.ReleaseMouseCapture();
    }

    #endregion

    #region Keyboard Shortcuts

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool isShift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (_isSecondaryView && isCtrl && !isShift && e.Key == Key.Z && _fileShelf.CanUndo)
        {
            if (_fileShelf.UndoLastOperation())
                RefreshShelfLayout();
            e.Handled = true;
        }
        else if (_isSecondaryView && isCtrl &&
            (e.Key == Key.Y || (isShift && e.Key == Key.Z)) && _fileShelf.CanRedo)
        {
            if (_fileShelf.RedoLastOperation())
                RefreshShelfLayout();
            e.Handled = true;
        }
        else if (e.Key == Key.A && isCtrl
            && _isSecondaryView && _fileShelf.FileCount > 0)
        {
            _fileShelf.SelectAll();

            foreach (var child in ShelfItemsContainer.Children)
            {
                if (child is Border b && b.Tag is string path)
                    UpdateShelfItemVisualState(b, _fileShelf.IsSelected(path));
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _isSecondaryView && _fileShelf.SelectedFiles.Count > 0 && !_isAnimating)
        {
            _isAnimating = true;
            var filesToRemove = _fileShelf.GetSelectedForDeletion();

            var pinnedSelected = _fileShelf.SelectedFiles
                .Where(f => _fileShelf.IsPinned(f))
                .ToList();
            if (pinnedSelected.Count > 0)
            {
                foreach (var child in ShelfItemsContainer.Children)
                {
                    if (child is Border b && b.Tag is string path && pinnedSelected.Contains(path))
                    {
                        AnimatePinnedRejection(b);
                    }
                }
            }

            if (filesToRemove.Count > 0)
            {
                AnimateFileDeletion(filesToRemove, () =>
                {
                    _fileShelf.RemoveFiles(filesToRemove);
                    RefreshShelfLayout();
                    _isAnimating = false;
                });
            }
            else
            {
                _isAnimating = false;
            }
            e.Handled = true;
        }
    }

    #endregion

    #region File Deletion Animation

    private void AnimateFileDeletion(IEnumerable<string> filePaths, Action onCompletion)
    {
        var targets = new List<FrameworkElement>();
        foreach (var path in filePaths)
        {
            foreach (var child in ShelfItemsContainer.Children)
            {
                if (child is Border b && (string)b.Tag == path)
                {
                    targets.Add(b);
                    break;
                }
            }
        }

        if (targets.Count == 0)
        {
            onCompletion();
            return;
        }

        var dur = new Duration(TimeSpan.FromMilliseconds(320));
        var easeBack = new BackEase { EasingMode = EasingMode.EaseIn, Amplitude = 0.6 };
        easeBack.Freeze();

        int completed = 0;
        int total = targets.Count;

        foreach (var target in targets)
        {
            var st = new ScaleTransform(1, 1);
            var tt = new TranslateTransform(0, 0);
            var group = new TransformGroup();
            group.Children.Add(st);
            group.Children.Add(tt);

            target.RenderTransform = group;
            target.RenderTransformOrigin = new Point(0.5, 0.5);

            var fadeOut = new DoubleAnimation(1, 0, dur) { EasingFunction = _easeExpOut6 };
            var scaleDown = new DoubleAnimation(1, 0, dur) { EasingFunction = easeBack };
            var slideUp = new DoubleAnimation(0, -25, dur) { EasingFunction = _easeExpOut6 };

            Timeline.SetDesiredFrameRate(fadeOut, VNotch.Services.AnimationConfig.TargetFps);
            Timeline.SetDesiredFrameRate(scaleDown, VNotch.Services.AnimationConfig.TargetFps);
            Timeline.SetDesiredFrameRate(slideUp, VNotch.Services.AnimationConfig.TargetFps);

            scaleDown.Completed += (s, e) =>
            {
                completed++;
                if (completed == total)
                {
                    onCompletion();
                }
            };

            target.BeginAnimation(OpacityProperty, fadeOut);
            st.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDown);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDown);
            tt.BeginAnimation(TranslateTransform.YProperty, slideUp);
        }
    }

    #endregion

    #region Progress Animation & Scrollbar

    private void AnimateShelfItemProgress(FrameworkElement item)
    {
        if (item is not Border border) return;

        var progressBar = new Border
        {
            Height = 2,
            CornerRadius = new CornerRadius(1),
            Background = new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(4, 0, 4, 3),
            Width = 0,
            IsHitTestVisible = false
        };

        var originalChild = border.Child;
        var overlay = new Grid();
        if (originalChild != null)
        {
            border.Child = null;
            overlay.Children.Add(originalChild);
        }
        overlay.Children.Add(progressBar);
        border.Child = overlay;

        void StartProgress()
        {
            double targetW = Math.Max(8, border.ActualWidth - 8);
            var grow = new DoubleAnimation(0, targetW, new Duration(TimeSpan.FromMilliseconds(320)))
            {
                EasingFunction = _easeExpOut6
            };
            Timeline.SetDesiredFrameRate(grow, VNotch.Services.AnimationConfig.TargetFps);
            grow.Completed += (_, _) =>
            {
                var fade = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(280)))
                {
                    BeginTime = TimeSpan.FromMilliseconds(100)
                };
                fade.Completed += (_, _) =>
                {
                    overlay.Children.Remove(progressBar);
                    if (overlay.Children.Count == 1 && overlay.Children[0] is UIElement only)
                    {
                        overlay.Children.Remove(only);
                        border.Child = only;
                    }
                };
                System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fade, VNotch.Services.AnimationConfig.TargetFps);
                progressBar.BeginAnimation(OpacityProperty, fade);
            };
            progressBar.BeginAnimation(WidthProperty, grow);
        }

        if (border.IsLoaded)
            StartProgress();
        else
            border.Loaded += (_, _) => StartProgress();
    }

    private DispatcherTimer? _scrollbarFadeTimer;

    private void ShelfScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.HorizontalChange) < 0.01 &&
            Math.Abs(e.ViewportWidthChange) < 0.01 &&
            Math.Abs(e.ExtentWidthChange) < 0.01)
        {
            return;
        }

        QueueShelfViewportRefresh();
    }

    private void QueueShelfViewportRefresh()
    {
        if (_shelfViewportRefreshQueued)
            return;

        _shelfViewportRefreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _shelfViewportRefreshQueued = false;
            RealizeShelfViewport();
        }));
    }

    private void ShelfScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
            e.Handled = true;
            ShowShelfScrollbar();
        }
    }

    private void ShowShelfScrollbar()
    {
        var scrollBar = FindVisualChild<System.Windows.Controls.Primitives.ScrollBar>(ShelfScrollViewer);
        if (scrollBar != null)
        {
            var fadeIn = new DoubleAnimation(1, TimeSpan.FromMilliseconds(150));
            Timeline.SetDesiredFrameRate(fadeIn, VNotch.Services.AnimationConfig.TargetFps);
            scrollBar.BeginAnimation(OpacityProperty, fadeIn);
        }

        _scrollbarFadeTimer?.Stop();
        _scrollbarFadeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1200)
        };
        _scrollbarFadeTimer.Tick += (s, e) =>
        {
            _scrollbarFadeTimer.Stop();
            if (scrollBar != null)
            {
                var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400));
                Timeline.SetDesiredFrameRate(fadeOut, VNotch.Services.AnimationConfig.TargetFps);
                scrollBar.BeginAnimation(OpacityProperty, fadeOut);
            }
        };
        _scrollbarFadeTimer.Start();
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result) return result;
            var found = FindVisualChild<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    #endregion

    #region Pinned Rejection Animation

    private void AnimatePinnedRejection(Border target)
    {
        var dur = TimeSpan.FromMilliseconds(400);

        if (target.BorderBrush is SolidColorBrush existingBrush && !existingBrush.IsFrozen)
            existingBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);

        var redBrush = new SolidColorBrush(Color.FromRgb(220, 60, 60));
        target.BorderBrush = redBrush;
        target.BorderThickness = new Thickness(1.5);

        if (target.RenderTransform is TranslateTransform oldTt)
            oldTt.BeginAnimation(TranslateTransform.XProperty, null);

        var tt = new TranslateTransform(0, 0);
        target.RenderTransform = tt;

        var shake = new DoubleAnimationUsingKeyFrames { Duration = dur };
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(-3, KeyTime.FromPercent(0.1)));
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(3, KeyTime.FromPercent(0.25)));
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(-2.5, KeyTime.FromPercent(0.4)));
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(2, KeyTime.FromPercent(0.55)));
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(-1, KeyTime.FromPercent(0.7)));
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(0.5, KeyTime.FromPercent(0.85)));
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1.0)));
        Timeline.SetDesiredFrameRate(shake, VNotch.Services.AnimationConfig.TargetFps);

        shake.Completed += (_, _) =>
        {
            tt.BeginAnimation(TranslateTransform.XProperty, null);
            tt.X = 0;
            tt.Y = 0;

            var currentBrush = target.BorderBrush as SolidColorBrush;
            if (currentBrush == null || currentBrush.IsFrozen)
            {
                target.BorderBrush = _brushShelfItemBorder;
                target.BorderThickness = new Thickness(1);
                return;
            }

            var fadeBack = new ColorAnimation
            {
                To = Color.FromArgb(40, 255, 255, 255),
                Duration = TimeSpan.FromMilliseconds(500),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            fadeBack.Completed += (_, _) =>
            {
                target.BorderBrush = _brushShelfItemBorder;
                target.BorderThickness = new Thickness(1);
            };
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeBack, VNotch.Services.AnimationConfig.TargetFps);
            currentBrush.BeginAnimation(SolidColorBrush.ColorProperty, fadeBack);
        };

        tt.BeginAnimation(TranslateTransform.XProperty, shake);
    }

    #endregion

    #region Single File Remove

    private void RemoveFileFromShelf(string filePath, Border item)
    {
        if (_isAnimating) return;
        _isAnimating = true;

        AnimateFileDeletion(new[] { filePath }, () =>
        {
            _fileShelf.RemoveFile(filePath);
            RefreshShelfLayout();
            _isAnimating = false;
        });
    }

    private ImageSource? GetFileIcon(string filePath) => FileIconProvider.GetFileIcon(filePath);

    #endregion

    #region Dispose Shelf

    private void DisposeAllShelfWatchers()
    {
        _fileShelf?.Dispose();
    }

    #endregion
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Controllers;

public sealed class FileShelfController : IDisposable
{
    private const int DefaultFileLimit = 30;
    private const int UnlockedFileLimit = 999;

    private readonly NotchSettings _settings;
    private readonly ISettingsService _settingsService;
    private readonly List<string> _filesList = new();
    private readonly HashSet<string> _filesSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pinnedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new();
    private readonly Queue<string> _addQueue = new();
    private readonly object _lock = new();
    private readonly Dispatcher _dispatcher;
    private readonly FileShelfHistory _history = new();
    private bool _isProcessingQueue = false;

    private List<string>? _filesSnapshot;
    private int _snapshotVersion;
    private int _lastSnapshotVersion = -1;

    public IReadOnlyList<string> Files
    {
        get
        {
            lock (_lock)
            {
                if (_filesSnapshot == null || _lastSnapshotVersion != _snapshotVersion)
                {
                    _filesSnapshot = _filesList.ToList();
                    _lastSnapshotVersion = _snapshotVersion;
                }
                return _filesSnapshot;
            }
        }
    }
    public IReadOnlyCollection<string> SelectedFiles { get { lock (_lock) return _selectedFiles.ToHashSet(); } }
    public int FileCount { get { lock (_lock) return _filesList.Count; } }
    public int PendingCount { get { lock (_lock) return _pendingFiles.Count; } }
    public int OccupiedSlots { get { lock (_lock) return _filesList.Count + _pendingFiles.Count; } }
    public int MaxFiles => _settings.IsShelfUploadLimitUnlocked ? UnlockedFileLimit : DefaultFileLimit;
    public int RemainingSlots { get { lock (_lock) return Math.Max(0, MaxFiles - (_filesList.Count + _pendingFiles.Count)); } }
    public bool IsFull { get { lock (_lock) return (_filesList.Count + _pendingFiles.Count) >= MaxFiles; } }
    public bool IsLimitUnlocked => _settings.IsShelfUploadLimitUnlocked;
    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;
    public event Action? HistoryChanged
    {
        add => _history.HistoryChanged += value;
        remove => _history.HistoryChanged -= value;
    }

    public event Action<string>? FileReadyToAdd;
    public event Action? AddQueueDrained;
    public event Action? LayoutRefreshRequested;
    public event Action? CapacityChanged;
    public event Action? PinStateChanged;
    public event Action<string>? FileExternallyRemoved;
    public event Action<string, string>? FileExternallyRenamed;
    public event Action<string, Exception>? FileWatchFailed;
    public event Action? ProcessNextRequested;

    public FileShelfController(NotchSettings settings, ISettingsService settingsService)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _dispatcher = Dispatcher.CurrentDispatcher;
    }
    private void InvalidateSnapshot() => _snapshotVersion++;

    public enum DropResult
    {
        Accept,
        AlreadyOnShelf,
        NoFiles,
        ShelfFull,
        ExceedsLimit,
        UnlockPrompt
    }

    public record DropValidation(DropResult Result, string[] NewFiles, string Message, int FileCount = 0);
    public DropValidation ValidateDrop(string[]? rawFiles)
    {
        if (rawFiles == null || rawFiles.Length == 0)
            return new DropValidation(DropResult.NoFiles, Array.Empty<string>(), Loc.Get("shelf.noFiles"));

        lock (_lock)
        {
            var newFiles = rawFiles
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(f => !_filesSet.Contains(f))
                .Where(f => !_pendingFiles.Contains(f))
                .ToArray();

            if (newFiles.Length == 0)
                return new DropValidation(DropResult.AlreadyOnShelf, Array.Empty<string>(), Loc.Get("shelf.alreadyOnShelf"));

            if (_settings.IsShelfUploadLimitUnlocked)
                return new DropValidation(DropResult.Accept, newFiles, string.Empty);

            int occupiedSlots = _filesList.Count + _pendingFiles.Count;
            int totalAfterDrop = occupiedSlots + newFiles.Length;
            if (totalAfterDrop > DefaultFileLimit)
                return new DropValidation(DropResult.UnlockPrompt, newFiles, string.Empty, newFiles.Length);

            int remainingSlots = Math.Max(0, MaxFiles - occupiedSlots);
            if (remainingSlots <= 0)
                return new DropValidation(DropResult.ShelfFull, Array.Empty<string>(),
                    Loc.Get("shelf.full", Math.Min(MaxFiles, occupiedSlots), MaxFiles));

            if (newFiles.Length > remainingSlots)
                return new DropValidation(DropResult.ExceedsLimit, Array.Empty<string>(),
                    Loc.Get("shelf.exceedsLimit", MaxFiles, remainingSlots));

            return new DropValidation(DropResult.Accept, newFiles, string.Empty);
        }
    }

    public void EnqueueFiles(string[] filePaths)
    {
        bool shouldProcess = false;

        lock (_lock)
        {
            foreach (var f in filePaths)
            {
                if ((_filesList.Count + _pendingFiles.Count) >= MaxFiles) break;
                if (_filesSet.Contains(f) || _pendingFiles.Contains(f))
                    continue;

                _addQueue.Enqueue(f);
                _pendingFiles.Add(f);
            }

            if (!_isProcessingQueue)
                shouldProcess = true;
        }

        CapacityChanged?.Invoke();

        if (shouldProcess)
            ProcessNext();
    }
    public void ProcessNext()
    {
        string? filePath = null;
        bool queueEmpty = false;

        lock (_lock)
        {
            if (_addQueue.Count == 0)
            {
                _isProcessingQueue = false;
                queueEmpty = true;
            }
            else
            {
                _isProcessingQueue = true;
                filePath = _addQueue.Dequeue();
                _pendingFiles.Remove(filePath);
            }
        }

        if (queueEmpty)
        {
            AddQueueDrained?.Invoke();
            return;
        }

        bool fileExists = File.Exists(filePath!) || Directory.Exists(filePath!);

        bool shouldAdd = false;
        if (fileExists)
        {
            lock (_lock)
            {
                if ((_filesList.Count + _pendingFiles.Count) < MaxFiles
                    && !_filesSet.Contains(filePath!))
                {
                    _filesList.Add(filePath!);
                    _filesSet.Add(filePath!);
                    InvalidateSnapshot();
                    shouldAdd = true;
                }
            }
        }

        if (shouldAdd)
        {
            WatchDirectory(filePath!);
            _history.RecordOperation(new AddFilesOperation(new[] { filePath! }));
            CapacityChanged?.Invoke();
            FileReadyToAdd?.Invoke(filePath!);
        }
        else
        {
            ProcessNextRequested?.Invoke();
        }
    }
    internal bool AddFileDirect(string filePath)
    {
        lock (_lock)
        {
            if ((_filesList.Count + _pendingFiles.Count) >= MaxFiles
                || _filesSet.Contains(filePath))
                return false;
        }

        if (!File.Exists(filePath) && !Directory.Exists(filePath))
            return false;

        lock (_lock)
        {
            if ((_filesList.Count + _pendingFiles.Count) >= MaxFiles
                || _filesSet.Contains(filePath))
                return false;

            _filesList.Add(filePath);
            _filesSet.Add(filePath);
            InvalidateSnapshot();
        }
        WatchDirectory(filePath);
        CapacityChanged?.Invoke();
        LayoutRefreshRequested?.Invoke();
        return true;
    }

    public bool IsSelected(string path) { lock (_lock) return _selectedFiles.Contains(path); }
    public void Select(string path)
    {
        lock (_lock)
        {
            _selectedFiles.Clear();
            _selectedFiles.Add(path);
        }
    }

    public void SelectAll()
    {
        lock (_lock)
        {
            _selectedFiles.Clear();
            foreach (var f in _filesList)
                _selectedFiles.Add(f);
        }
    }

    public void ClearSelection()
    {
        lock (_lock) _selectedFiles.Clear();
    }

    public void SetSelection(IEnumerable<string> paths)
    {
        lock (_lock)
        {
            _selectedFiles.Clear();
            foreach (var p in paths)
                _selectedFiles.Add(p);
        }
    }

    public void ToggleSelection(string path)
    {
        lock (_lock)
        {
            if (_selectedFiles.Contains(path))
                _selectedFiles.Remove(path);
            else
                _selectedFiles.Add(path);
        }
    }
    public void ApplyRectangleSelection(HashSet<string> intersectedPaths, bool isCtrl, HashSet<string> initialState)
    {
        lock (_lock)
        {
            foreach (var path in _filesList)
            {
                bool select = isCtrl
                    ? (intersectedPaths.Contains(path) ^ initialState.Contains(path))
                    : intersectedPaths.Contains(path);

                if (select)
                    _selectedFiles.Add(path);
                else
                    _selectedFiles.Remove(path);
            }
        }
    }

    public bool RemoveFiles(IEnumerable<string> filePaths)
    {
        var removed = RemoveFilesDirect(filePaths, allowPinned: false);
        if (removed.Length == 0) return false;

        _history.RecordOperation(new RemoveFilesOperation(removed,
            removed.ToDictionary(file => file, IsPinned, StringComparer.OrdinalIgnoreCase)));
        return true;
    }

    internal bool CanAddFilesDirect(IEnumerable<string> filePaths)
    {
        var requestedFiles = filePaths.ToArray();
        var files = requestedFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0 || files.Length != requestedFiles.Length ||
            files.Any(file => !File.Exists(file) && !Directory.Exists(file)))
            return false;

        lock (_lock)
        {
            return _filesList.Count + _pendingFiles.Count + files.Length <= MaxFiles
                && files.All(file => !_filesSet.Contains(file));
        }
    }

    public bool RemoveFile(string filePath) => RemoveFiles(new[] { filePath });

    internal string[] RemoveFilesDirect(IEnumerable<string> filePaths, bool allowPinned)
    {
        var requested = new HashSet<string>(filePaths, StringComparer.OrdinalIgnoreCase);
        string[] removed;
        lock (_lock)
        {
            removed = _filesList
                .Where(file => requested.Contains(file) && (allowPinned || !_pinnedFiles.Contains(file)))
                .ToArray();
            if (removed.Length == 0) return Array.Empty<string>();

            _filesList.RemoveAll(file => requested.Contains(file) && (allowPinned || !_pinnedFiles.Contains(file)));
            _filesSet.ExceptWith(removed);
            _selectedFiles.ExceptWith(removed);
            _pinnedFiles.ExceptWith(removed);
            InvalidateSnapshot();
        }
        foreach (var file in removed)
        {
            UnwatchDirectory(file);
            FileIconProvider.Invalidate(file);
        }
        CapacityChanged?.Invoke();
        return removed;
    }

    internal bool CanRemoveFilesDirect(IEnumerable<string> filePaths, bool allowPinned)
    {
        var requestedFiles = filePaths.ToArray();
        if (requestedFiles.Length == 0 ||
            requestedFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count() != requestedFiles.Length)
            return false;

        lock (_lock)
        {
            return requestedFiles.All(file => _filesSet.Contains(file) &&
                (allowPinned || !_pinnedFiles.Contains(file)));
        }
    }
    public List<string> GetSelectedForDeletion() { lock (_lock) return _selectedFiles.Where(f => !_pinnedFiles.Contains(f)).ToList(); }

    public string[] GetDragFiles() { lock (_lock) return _selectedFiles.ToArray(); }
    public bool HandleDragMoveOut(string[] draggedFiles)
    {
        var removed = RemoveFilesDirect(draggedFiles, allowPinned: false);
        if (removed.Length == 0) return false;

        _history.RecordOperation(new RemoveFilesOperation(removed,
            removed.ToDictionary(file => file, IsPinned, StringComparer.OrdinalIgnoreCase)));
        LayoutRefreshRequested?.Invoke();
        return true;
    }

    public bool IsPinned(string path) { lock (_lock) return _pinnedFiles.Contains(path); }

    public bool TogglePin(string path)
    {
        bool wasPinned = IsPinned(path);
        if (!TogglePinDirect(path)) return false;

        _history.RecordOperation(new TogglePinOperation(path, wasPinned));
        return true;
    }

    internal bool TogglePinDirect(string path)
    {
        lock (_lock)
        {
            if (!_filesSet.Contains(path)) return false;
            if (_pinnedFiles.Contains(path)) _pinnedFiles.Remove(path);
            else _pinnedFiles.Add(path);
        }
        SortPinnedFirst();
        PinStateChanged?.Invoke();
        LayoutRefreshRequested?.Invoke();
        return true;
    }

    internal bool PinFileDirect(string path)
    {
        lock (_lock)
        {
            if (!_filesSet.Contains(path) || _pinnedFiles.Contains(path)) return false;
            _pinnedFiles.Add(path);
        }
        SortPinnedFirst();
        PinStateChanged?.Invoke();
        LayoutRefreshRequested?.Invoke();
        return true;
    }

    internal bool UnpinFileDirect(string path)
    {
        lock (_lock)
        {
            if (!_filesSet.Contains(path) || !_pinnedFiles.Contains(path)) return false;
            _pinnedFiles.Remove(path);
        }
        SortPinnedFirst();
        PinStateChanged?.Invoke();
        LayoutRefreshRequested?.Invoke();
        return true;
    }

    public bool UndoLastOperation() => _history.TryUndo(this);

    public bool RedoLastOperation() => _history.TryRedo(this);

    private void SortPinnedFirst()
    {
        lock (_lock)
        {
            _filesList.Sort((a, b) =>
            {
                bool aPinned = _pinnedFiles.Contains(a);
                bool bPinned = _pinnedFiles.Contains(b);
                if (aPinned == bPinned) return 0;
                return aPinned ? -1 : 1;
            });
            InvalidateSnapshot();
        }
    }

    public void UnlockLimit()
    {
        _settings.IsShelfUploadLimitUnlocked = true;
        _settingsService.Save(_settings);
        CapacityChanged?.Invoke();
    }

    public void UpdateSettings(NotchSettings newSettings)
    {
        _settings.IsShelfUploadLimitUnlocked = newSettings.IsShelfUploadLimitUnlocked;
        CapacityChanged?.Invoke();
    }

    public string GetCountDisplayText()
    {
        lock (_lock)
        {
            int currentCount = _filesList.Count + _pendingFiles.Count;
            if (_settings.IsShelfUploadLimitUnlocked)
                return $"{currentCount}";
            int displayCount = Math.Min(DefaultFileLimit, currentCount);
            return $"{displayCount}/{DefaultFileLimit}";
        }
    }

    public bool IsCountWarning { get { lock (_lock) return (_filesList.Count + _pendingFiles.Count) >= MaxFiles && !_settings.IsShelfUploadLimitUnlocked; } }

    public string? GetStatusMessage()
    {
        lock (_lock)
        {
            int occupiedSlots = _filesList.Count + _pendingFiles.Count;
            if (occupiedSlots >= MaxFiles && !_settings.IsShelfUploadLimitUnlocked)
            {
                return Loc.Get("shelf.full", occupiedSlots, DefaultFileLimit);
            }
            return null;
        }
    }

    public void HandleExternalRename(string oldPath, string newPath)
    {
        lock (_lock)
        {
            int idx = _filesList.IndexOf(oldPath);
            if (idx >= 0)
            {
                _filesList[idx] = newPath;
                _filesSet.Remove(oldPath);
                _filesSet.Add(newPath);
                InvalidateSnapshot();
            }

            if (_selectedFiles.Remove(oldPath))
                _selectedFiles.Add(newPath);
        }

        WatchDirectory(newPath);
        UnwatchDirectory(oldPath);
        CapacityChanged?.Invoke();
        LayoutRefreshRequested?.Invoke();
    }

    private void WatchDirectory(string filePath)
    {
        System.Diagnostics.Debug.Assert(_dispatcher.CheckAccess(), "WatchDirectory must be called on UI thread");

        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir) || _watchers.ContainsKey(dir)) return;

        try
        {
            var watcher = new FileSystemWatcher(dir)
            {
                NotifyFilter = NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            watcher.Deleted += OnFileExternallyDeleted;
            watcher.Renamed += OnFileExternallyRenamed;
            _watchers[dir] = watcher;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("SHELF-WATCH", ex.ToString());
            FileWatchFailed?.Invoke(dir, ex);
        }
    }

    private void UnwatchDirectory(string filePath)
    {
        System.Diagnostics.Debug.Assert(_dispatcher.CheckAccess(), "UnwatchDirectory must be called on UI thread");

        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir)) return;

        bool hasOtherFiles;
        lock (_lock)
        {
            hasOtherFiles = _filesList.Any(f => string.Equals(Path.GetDirectoryName(f), dir, StringComparison.OrdinalIgnoreCase));
        }

        if (!hasOtherFiles && _watchers.TryGetValue(dir, out var watcher))
        {
            watcher.EnableRaisingEvents = false;
            watcher.Deleted -= OnFileExternallyDeleted;
            watcher.Renamed -= OnFileExternallyRenamed;
            watcher.Dispose();
            _watchers.Remove(dir);
        }
    }

    private void OnFileExternallyDeleted(object sender, FileSystemEventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            bool shouldNotify;
            lock (_lock)
            {
                shouldNotify = _filesSet.Contains(e.FullPath) && !_isProcessingQueue;
            }
            if (shouldNotify)
                FileExternallyRemoved?.Invoke(e.FullPath);
        });
    }

    private void OnFileExternallyRenamed(object sender, RenamedEventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            bool shouldNotify;
            lock (_lock)
            {
                shouldNotify = _filesSet.Contains(e.OldFullPath);
            }
            if (shouldNotify)
                FileExternallyRenamed?.Invoke(e.OldFullPath, e.FullPath);
        });
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers.Values)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
    }
}

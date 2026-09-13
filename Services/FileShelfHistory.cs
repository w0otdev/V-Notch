using System;
using System.Collections.Generic;
using System.Linq;

namespace VNotch.Services;

public sealed class FileShelfHistory
{
    private readonly Stack<IFileShelfOperation> _undoStack = new();
    private readonly Stack<IFileShelfOperation> _redoStack = new();
    private const int MaxHistorySize = 50;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;
    public int UndoCount => _undoStack.Count;
    public int RedoCount => _redoStack.Count;

    public event Action? HistoryChanged;

    public void RecordOperation(IFileShelfOperation operation)
    {
        if (operation == null)
            throw new ArgumentNullException(nameof(operation));

        _undoStack.Push(operation);
        _redoStack.Clear();

        if (_undoStack.Count > MaxHistorySize)
        {
            var newest = _undoStack.Take(MaxHistorySize).Reverse().ToArray();
            _undoStack.Clear();
            foreach (var item in newest)
                _undoStack.Push(item);
        }

        HistoryChanged?.Invoke();
    }

    internal IFileShelfOperation? Undo()
    {
        if (!CanUndo)
            return null;

        var operation = _undoStack.Pop();
        _redoStack.Push(operation);
        HistoryChanged?.Invoke();
        return operation;
    }

    internal IFileShelfOperation? Redo()
    {
        if (!CanRedo)
            return null;

        var operation = _redoStack.Pop();
        _undoStack.Push(operation);
        HistoryChanged?.Invoke();
        return operation;
    }

    internal bool TryUndo(Controllers.FileShelfController controller)
    {
        if (!CanUndo || !_undoStack.Peek().Undo(controller))
            return false;

        _redoStack.Push(_undoStack.Pop());
        HistoryChanged?.Invoke();
        return true;
    }

    internal bool TryRedo(Controllers.FileShelfController controller)
    {
        if (!CanRedo || !_redoStack.Peek().Redo(controller))
            return false;

        _undoStack.Push(_redoStack.Pop());
        HistoryChanged?.Invoke();
        return true;
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        HistoryChanged?.Invoke();
    }

    public string? GetUndoDescription()
    {
        return CanUndo ? _undoStack.Peek().Description : null;
    }

    public string? GetRedoDescription()
    {
        return CanRedo ? _redoStack.Peek().Description : null;
    }
}

public interface IFileShelfOperation
{
    string Description { get; }
    bool Undo(Controllers.FileShelfController controller);
    bool Redo(Controllers.FileShelfController controller);
}

public sealed class AddFilesOperation : IFileShelfOperation
{
    private readonly string[] _files;

    public AddFilesOperation(string[] files)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    public string Description => _files.Length == 1
        ? $"Add file: {System.IO.Path.GetFileName(_files[0])}"
        : $"Add {_files.Length} files";

    public bool Undo(Controllers.FileShelfController controller)
    {
        if (!controller.CanRemoveFilesDirect(_files, allowPinned: true)) return false;
        return controller.RemoveFilesDirect(_files, allowPinned: true).Length == _files.Length;
    }

    public bool Redo(Controllers.FileShelfController controller)
    {
        if (!controller.CanAddFilesDirect(_files)) return false;
        return _files.All(controller.AddFileDirect);
    }
}

public sealed class RemoveFilesOperation : IFileShelfOperation
{
    private readonly string[] _files;
    private readonly Dictionary<string, bool> _pinnedStates;

    public RemoveFilesOperation(string[] files, Dictionary<string, bool> pinnedStates)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _pinnedStates = pinnedStates ?? throw new ArgumentNullException(nameof(pinnedStates));
    }

    public string Description => _files.Length == 1
        ? $"Remove file: {System.IO.Path.GetFileName(_files[0])}"
        : $"Remove {_files.Length} files";

    public bool Undo(Controllers.FileShelfController controller)
    {
        if (!controller.CanAddFilesDirect(_files)) return false;
        bool restored = true;
        foreach (var file in _files)
        {
            restored &= controller.AddFileDirect(file);
            if (_pinnedStates.TryGetValue(file, out bool wasPinned) && wasPinned)
                restored &= controller.PinFileDirect(file);
        }
        return restored;
    }

    public bool Redo(Controllers.FileShelfController controller)
    {
        if (!controller.CanRemoveFilesDirect(_files, allowPinned: true)) return false;
        return controller.RemoveFilesDirect(_files, allowPinned: true).Length == _files.Length;
    }
}

public sealed class TogglePinOperation : IFileShelfOperation
{
    private readonly string _file;
    private readonly bool _wasPinned;

    public TogglePinOperation(string file, bool wasPinned)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
        _wasPinned = wasPinned;
    }

    public string Description => _wasPinned
        ? $"Unpin: {System.IO.Path.GetFileName(_file)}"
        : $"Pin: {System.IO.Path.GetFileName(_file)}";

    public bool Undo(Controllers.FileShelfController controller)
    {
        return _wasPinned
            ? controller.PinFileDirect(_file)
            : controller.UnpinFileDirect(_file);
    }

    public bool Redo(Controllers.FileShelfController controller)
    {
        return controller.TogglePinDirect(_file);
    }
}

public sealed class BatchPinOperation : IFileShelfOperation
{
    private readonly Dictionary<string, bool> _fileStates;
    private readonly bool _pinning;

    public BatchPinOperation(Dictionary<string, bool> fileStates, bool pinning)
    {
        _fileStates = fileStates ?? throw new ArgumentNullException(nameof(fileStates));
        _pinning = pinning;
    }

    public string Description => _pinning
        ? $"Pin {_fileStates.Count} files"
        : $"Unpin {_fileStates.Count} files";

    public bool Undo(Controllers.FileShelfController controller)
    {
        bool changed = true;
        foreach (var kvp in _fileStates)
        {
            if (kvp.Value) // Was pinned
                changed &= controller.PinFileDirect(kvp.Key);
            else
                changed &= controller.UnpinFileDirect(kvp.Key);
        }
        return changed;
    }

    public bool Redo(Controllers.FileShelfController controller)
    {
        bool changed = true;
        foreach (var file in _fileStates.Keys)
        {
            if (_pinning)
                changed &= controller.PinFileDirect(file);
            else
                changed &= controller.UnpinFileDirect(file);
        }
        return changed;
    }
}

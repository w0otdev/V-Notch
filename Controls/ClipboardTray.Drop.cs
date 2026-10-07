using System.IO;
using System.Windows;
using System.Windows.Controls;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Clipboard;

namespace VNotch.Controls;

public partial class ClipboardTray
{
    private ClipboardEntry[] _pendingPersonalDrop = [];
    private string[] _pendingPersonalFiles = [];
    private int _personalDropVersion;
    private readonly List<ClipboardDropReader.Payload> _pendingBrowserDrops = [];

    private void CancelPendingPersonalDrop()
    {
        ++_personalDropVersion;
        _pendingPersonalDrop = []; _pendingPersonalFiles = [];
        _pendingBrowserDrops.Clear();
    }
    /// <summary>Cached DragOver support per category button, computed once in DragEnter to avoid
    /// repeated inter-process COM queries (IDataObject.GetDataPresent) on every mouse-move frame.</summary>
    private readonly Dictionary<string, bool> _dragEnterCache = [];
    private string? _lastDragRejectCategory;

    private static string GetCategoryRejectReason(string category) => category switch
    {
        "Images" => Loc.Get("clipboard.categoryOnlyImages"),
        "Links" => Loc.Get("clipboard.categoryOnlyLinks"),
        "Files" => Loc.Get("clipboard.categoryOnlyFiles"),
        _ => Loc.Get("clipboard.categoryMismatch")
    };

    private void Category_DragLeave(object sender, DragEventArgs e)
    {
        var button = (Button)sender;
        TrayMotion.SetOpacity(button, 1);
        // Tag is bound from the view model and can be null while a container is recycled.
        if (button.Tag is string category)
        {
            _dragEnterCache.Remove(category);
            if (_lastDragRejectCategory == category)
            {
                _lastDragRejectCategory = null;
                ClearStatus(immediately: true);
            }
        }
        e.Handled = true;
    }

    private void Category_DragEnter(object sender, DragEventArgs e)
    {
        var button = (Button)sender;
        if (button.Tag is not string category)
        {
            e.Effects = DragDropEffects.None; e.Handled = true; return;
        }
        var (entries, files) = DropPayload(e.Data);
        bool hasPayload = entries.Length > 0 || files.Length > 0 || e.Data.GetDataPresent(DataFormats.FileDrop) || ClipboardDropReader.Supports(e.Data);
        bool supported = _controller != null && files.Length <= ClipboardHistoryStore.MaxImportItems
            && (CanDropCategory(category, entries, files) ||
                entries.Length == 0 && files.Length == 0 && !e.Data.GetDataPresent(DataFormats.FileDrop) && ClipboardDropReader.Supports(e.Data));
        _dragEnterCache[category] = supported;
        e.Effects = supported ? DragDropEffects.Copy : DragDropEffects.None;
        TrayMotion.SetOpacity(button, supported ? 0.7 : 1);
        if (!supported && hasPayload)
        {
            _lastDragRejectCategory = category;
            StatusChanged(GetCategoryRejectReason(category));
        }
        else if (supported && _lastDragRejectCategory != null)
        {
            _lastDragRejectCategory = null;
            ClearStatus(immediately: true);
        }
        e.Handled = true;
    }

    internal static bool CanDropCategory(string category, ClipboardEntry[] entries, string[] files)
    {
        if (entries.Length == 0 && files.Length == 0) return false;
        if (files.Any(path => !ClipboardImportPaths.IsLocal(path))) return false;
        if (category is "All" or "Pin" or "Personal" or "Archive" || ClipboardClassifier.GroupNames.Contains(category))
            return true;
        if (category is not ("Images" or "Files" or "Text" or "Code" or "Links" or "Colors")) return false;
        // Type tabs filter content; dropping must never relabel an incompatible item.
        return entries.Length > 0
            ? entries.All(entry => ClipboardHistoryStore.MatchesCategory(entry, category))
            : files.All(path => ClipboardHistoryStore.MatchesCategory(
                new ClipboardEntry { Kind = ClipboardClassifier.Detect(new ClipboardCapture { FilePaths = [path] }) }, category));
    }

    private (ClipboardEntry[] Entries, string[] Files) DropPayload(IDataObject data)
    {
        var entries = _controller?.IsDragging == true && data.GetDataPresent(ClipboardHistoryController.EntryDragFormat)
            ? _controller.DraggedEntries.ToArray() : [];
        var files = entries.Length == 0 && data.GetDataPresent(DataFormats.FileDrop)
            ? data.GetData(DataFormats.FileDrop) as string[] ?? [] : [];
        return (entries, files);
    }

    private void Category_DragOver(object sender, DragEventArgs e)
    {
        var button = (Button)sender;
        // Read the result cached by DragEnter — avoids inter-process COM queries on every frame.
        bool supported = button.Tag is string category && _dragEnterCache.TryGetValue(category, out bool cached) && cached;
        e.Effects = supported ? DragDropEffects.Copy : DragDropEffects.None;
        TrayMotion.SetOpacity(button, supported ? 0.7 : 1);
        e.Handled = true;
    }

    private async void Category_Drop(object sender, DragEventArgs e)
    {
        var button = (Button)sender;
        TrayMotion.SetOpacity(button, 1);
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (button.Tag is not string category) return;
        _dragEnterCache.Remove(category);
        if (_controller == null) return;
        var (entries, files) = DropPayload(e.Data);
        if (entries.Length == 0 && files.Length == 0 && !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            await SafeAsync(async () =>
            {
                var payload = ClipboardDropReader.Read(e.Data);
                if (payload == null || !CanDropCategory(category, [new ClipboardEntry { Kind = payload.Kind }], [])) return;
                e.Effects = DragDropEffects.Copy;
                if (category == "Personal" && !_controller.Store.IsPersonalUnlocked)
                {
                    SelectCategory(category);
                    _pendingBrowserDrops.Add(payload);
                    return;
                }
                await _controller.ImportDropPayloadAsync(payload, category);
                if (!_disposed) SelectCategory(category);
            });
            return;
        }
        if (!CanDropCategory(category, entries, files))
        {
            StatusChanged(GetCategoryRejectReason(category));
            return;
        }
        if (files.Length > ClipboardHistoryStore.MaxImportItems)
        {
            StatusChanged(Loc.Get("clipboard.tooManyFiles"));
            return;
        }
        if (entries.Length == 0 && files.Length == 0) return;
        e.Effects = DragDropEffects.Copy;
        if (category == "Personal" && !_controller.Store.IsPersonalUnlocked)
        {
            SelectCategory(category);
            _pendingPersonalDrop = _pendingPersonalDrop.Concat(entries).DistinctBy(entry => entry.Id).ToArray();
            _pendingPersonalFiles = _pendingPersonalFiles.Union(files, StringComparer.OrdinalIgnoreCase).ToArray();
            return;
        }
        await SafeAsync(async () =>
        {
            foreach (var entry in entries) await _controller.AssignCategoryAsync(entry, category);
            if (files.Length > 0) await _controller.ImportAsync(new ClipboardCapture { FilePaths = files, TargetCategory = category }, notifyDrop: true);
        });
        SelectCategory(category);
    }

    internal async Task ImportCurrentCategoryDropAsync(IDataObject data)
    {
        if (_controller == null) return;
        var category = CurrentCategory;
        await SafeAsync(async () =>
        {
            var payload = ClipboardDropReader.Read(data);
            if (payload == null) return;
            if (!CanDropCategory(category, [new ClipboardEntry { Kind = payload.Kind }], []))
            {
                StatusChanged(GetCategoryRejectReason(category));
                return;
            }
            if (category == "Personal" && !_controller.Store.IsPersonalUnlocked)
            {
                _pendingBrowserDrops.Add(payload);
                return;
            }
            await _controller.ImportDropPayloadAsync(payload, category == "All" ? null : category);
        });
    }

    private async Task CompletePersonalDropAsync()
    {
        if (_controller == null || _disposed || _category != "Personal" || !_controller.Store.IsPersonalUnlocked)
        { CancelPendingPersonalDrop(); return; }
        int version = _personalDropVersion;
        var entries = _pendingPersonalDrop; var files = _pendingPersonalFiles;
        var browserDrops = _pendingBrowserDrops.ToArray();
        _pendingBrowserDrops.Clear();
        _pendingPersonalDrop = []; _pendingPersonalFiles = [];
        foreach (var entry in entries)
        {
            if (version != _personalDropVersion || _category != "Personal") return;
            await _controller.AssignCategoryAsync(entry, "Personal");
        }
        var validFiles = files.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
        if (validFiles.Length < files.Length)
        {
            StatusChanged(Loc.Get("clipboard.missingFilesSkipped"));
        }
        if (validFiles.Length > 0)
        {
            foreach (var batch in validFiles.Chunk(ClipboardHistoryStore.MaxImportItems))
            {
                if (version != _personalDropVersion || _category != "Personal") return;
                await _controller.ImportAsync(new ClipboardCapture { FilePaths = batch, TargetCategory = "Personal" }, notifyDrop: true);
            }
        }
        foreach (var payload in browserDrops)
        {
            if (version != _personalDropVersion || _category != "Personal") return;
            await _controller.ImportDropPayloadAsync(payload, "Personal");
        }
    }
}

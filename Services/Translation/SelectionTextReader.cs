using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using static VNotch.Services.Win32Interop;

namespace VNotch.Services.Translation;

internal static partial class SelectionTextReader
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_selection_text_reader_needs_sanitization(
        ReadOnlySpan<char> part,
        int partLen,
        out byte outNeeds);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_selection_text_reader_sanitize(
        ReadOnlySpan<char> part,
        int partLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    [LibraryImport(NativeDll)]
    private static partial int vn_selection_text_reader_valid_bounds(
        byte isEmpty,
        double x,
        double y,
        double width,
        double height,
        out byte outValid);

    internal sealed record Result(string Text, Rect Bounds, AutomationElement Element, TextPatternRange? Range, bool Editable)
    {
        internal bool InputTruncated { get; init; }
    }

    internal static Result? Read(IntPtr window, AutomationElement? eventElement, bool manual, out bool protectedInput,
        AutomationElement? previousElement = null, AutomationElement? rootElement = null, bool allowOwnProcess = false)
    {
        protectedInput = false;
        if (window == IntPtr.Zero) return null;
        GetWindowThreadProcessId(window, out uint windowPid);
        if (!allowOwnProcess && windowPid == Environment.ProcessId) return null;

        var focused = Safely(() => AutomationElement.FocusedElement);
        if (focused != null && Safely(() => focused.Current.IsPassword)) { protectedInput = true; return null; }

        var point = System.Windows.Forms.Cursor.Position;
        var cursorRect = new Rect(point.X, point.Y, 1, 1);
        IntPtr focusHwnd = NativeSelectionReader.FocusedControl(window);
        AutomationElement? focusedHwndElement = focusHwnd != IntPtr.Zero && focusHwnd != window && IsSameOrChildWindow(window, focusHwnd)
            ? Safely(() => AutomationElement.FromHandle(focusHwnd))
            : null;
        AutomationElement? pointElement = Safely(() => AutomationElement.FromPoint(new Point(point.X, point.Y)));

        var seeds = new List<AutomationElement?> { eventElement, previousElement, focused, focusedHwndElement, pointElement };
        if (manual)
        {
            // When a user finishes dragging a selection, the cursor frequently sits a few pixels
            // outside the text node. Probe nearby offsets to catch the actual text element.
            foreach (var (dx, dy) in new[] { (-6, 0), (6, 0), (0, -6), (0, 6) })
                seeds.Add(Safely(() => AutomationElement.FromPoint(new Point(point.X + dx, point.Y + dy))));
        }

        var visited = new List<AutomationElement>();
        var clock = Stopwatch.StartNew();
        foreach (var seed in seeds)
        {
            if (clock.ElapsedMilliseconds >= 900) break;
            // A stale selection event must not prevent trying the current focus.
            if (seed == null || !Safely(() => WithinWindow(seed, window, windowPid, clock))) continue;
            AutomationElement? element = seed;
            for (int depth = 0; element != null && depth < 48 && clock.ElapsedMilliseconds < 900; depth++)
            {
                var current = element;
                if (!ReferenceEquals(current, focused) && Safely(() => current.Current.IsPassword))
                {
                    if (ReferenceEquals(current, seed)) { protectedInput = true; return null; }
                    break;
                }
                if (visited.Any(x => Safely(() => Automation.Compare(x, current)))) break;
                visited.Add(current);
                var result = Safely(() => ReadElement(current, manual, cursorRect));
                if (result != null) return result;
                if (Safely(() => current.Current.NativeWindowHandle) == window.ToInt64()) { element = null; break; }
                element = Safely(() => TreeWalker.RawViewWalker.GetParent(current));
            }
            for (int controlDepth = 0; element != null && controlDepth < 16 && clock.ElapsedMilliseconds < 900; controlDepth++)
            {
                var current = element;
                if (!ReferenceEquals(current, focused) && Safely(() => current.Current.IsPassword)) break;
                if (!visited.Any(x => Safely(() => Automation.Compare(x, current))))
                {
                    visited.Add(current);
                    var result = Safely(() => ReadElement(current, manual, cursorRect));
                    if (result != null) return result;
                }
                if (Safely(() => current.Current.NativeWindowHandle) == window.ToInt64()) break;
                element = Safely(() => TreeWalker.ControlViewWalker.GetParent(current));
            }
        }

        if (clock.ElapsedMilliseconds < 900)
        {
            var containers = new List<AutomationElement?>
            {
                pointElement,
                focusedHwndElement,
                focused,
                rootElement ?? Safely(() => AutomationElement.FromHandle(window))
            };
            var searchedContainers = new List<AutomationElement>();
            foreach (var container in containers)
            {
                if (clock.ElapsedMilliseconds >= 900) break;
                if (container == null || !Safely(() => WithinWindow(container, window, windowPid, clock))) continue;
                if (searchedContainers.Any(x => Safely(() => Automation.Compare(x, container)))) continue;
                searchedContainers.Add(container);

                var textElements = Safely(() => container.FindAll(
                    TreeScope.Subtree,
                    new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true)));
                if (textElements != null)
                {
                    int count = Math.Min(Safely(() => textElements.Count), 64);
                    for (int i = 0; i < count && clock.ElapsedMilliseconds < 900; i++)
                    {
                        var candidate = Safely(() => textElements[i]);
                        if (candidate == null) continue;
                        if (Safely(() => candidate.Current.IsPassword)) continue;
                        if (visited.Any(x => Safely(() => Automation.Compare(x, candidate)))) continue;
                        visited.Add(candidate);
                        var result = Safely(() => ReadElement(candidate, manual, cursorRect));
                        if (result != null) return result;
                    }
                }

                // Many Chromium/Electron/WebView2 and custom UI containers do not set
                // IsTextPatternAvailableProperty on subtree queries or nest documents inside direct children.
                var child = Safely(() => TreeWalker.ControlViewWalker.GetFirstChild(container));
                for (int c = 0; child != null && c < 32 && clock.ElapsedMilliseconds < 900; c++)
                {
                    var currentChild = child;
                    if (!Safely(() => currentChild.Current.IsPassword) &&
                        !visited.Any(x => Safely(() => Automation.Compare(x, currentChild))))
                    {
                        visited.Add(currentChild);
                        var result = Safely(() => ReadElement(currentChild, manual, cursorRect));
                        if (result != null) return result;
                    }
                    child = Safely(() => TreeWalker.ControlViewWalker.GetNextSibling(currentChild));
                }
            }
        }

        if (manual && clock.ElapsedMilliseconds < 900)
        {
            foreach (var candidate in new[] { pointElement, focused, focusedHwndElement, eventElement, rootElement ?? Safely(() => AutomationElement.FromHandle(window)) })
            {
                if (clock.ElapsedMilliseconds >= 900) break;
                if (candidate == null || !Safely(() => WithinWindow(candidate, window, windowPid, clock))) continue;
                if (Safely(() => candidate.Current.IsPassword)) continue;
                var fallback = Safely(() => ReadFallbackElement(candidate, cursorRect));
                if (fallback != null) return fallback;
            }
        }
        return null;
    }

    internal static Result? ReadElement(AutomationElement element, bool manual, Rect cursor)
    {
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var found) || found is not TextPattern pattern) return null;
        var ranges = Safely(pattern.GetSelection);
        var parts = new List<string>();
        int rangeLimit = ranges == null ? 0 : Math.Min(ranges.Length, 128);
        int totalLength = 0;
        for (int i = 0; i < rangeLimit; i++)
        {
            string? part = Safely(() => ranges![i].GetText(TranslationInput.MaxCharacters + 256));
            if (string.IsNullOrEmpty(part)) continue;
            parts.Add(part);
            totalLength += part.Length + 1;
            if (totalLength > TranslationInput.MaxCharacters + 128) break;
        }
        string? text = JoinSelection(parts);
        if (text == null && manual)
        {
            var supported = Safely(() => pattern.SupportedTextSelection);
            var controlType = Safely(() => element.Current.ControlType);
            if (supported == SupportedTextSelection.None || controlType == ControlType.Text || controlType == ControlType.ToolTip)
            {
                string? docText = Safely(() => pattern.DocumentRange?.GetText(TranslationInput.MaxCharacters + 256));
                if (!string.IsNullOrWhiteSpace(docText)) text = JoinSelection([docText]);
            }
        }
        if (text == null) return null;
        var bounds = Rect.Empty;
        for (int i = 0; i < rangeLimit; i++)
            foreach (var rectangle in Safely(ranges![i].GetBoundingRectangles) ?? [])
                if (ValidBounds(rectangle)) bounds.Union(rectangle);
        // Some PDF, browser and custom providers return text but no selection rectangles.
        if (bounds.IsEmpty)
        {
            var controlBounds = Safely(() => element.Current.BoundingRectangle);
            bounds = manual || !ValidBounds(controlBounds) || controlBounds.Contains(cursor.Location)
                ? cursor
                : new Rect(controlBounds.Right, controlBounds.Bottom, 1, 1);


        }
        if (bounds.IsEmpty) return null;
        bool editable = Safely(() => element.Current.IsEnabled) &&
            text.Length <= TranslationInput.MaxCharacters &&
            rangeLimit == 1 &&
            Safely(() => ranges![0].GetAttributeValue(TextPattern.IsReadOnlyAttribute)) is false &&
            (Safely(() => element.Current.ControlType) == ControlType.Edit || Safely(() => element.Current.ControlType) == ControlType.Document);
        return new(TranslationInput.Prefix(text, TranslationInput.MaxCharacters), bounds, element, rangeLimit == 1 ? Safely(() => ranges![0].Clone()) : null, editable)
        { InputTruncated = text.Length > TranslationInput.MaxCharacters };
    }

    private static Result? ReadFallbackElement(AutomationElement element, Rect cursor)
    {
        // 1. Check SelectionPattern on element or its immediate parent container (ListBox, DataGrid, TreeView, ComboBox, TabControl)
        AutomationElement? container = element;
        for (int depth = 0; container != null && depth < 4; depth++)
        {
            var currentContainer = container;
            if (currentContainer.TryGetCurrentPattern(SelectionPattern.Pattern, out var selObj) && selObj is SelectionPattern selectionPattern)
            {
                var selectedItems = Safely(selectionPattern.Current.GetSelection);
                if (selectedItems is { Length: > 0 })
                {
                    var itemParts = new List<string>(Math.Min(selectedItems.Length, 32));
                    for (int i = 0; i < Math.Min(selectedItems.Length, 32); i++)
                    {
                        var item = selectedItems[i];
                        if (item == null || Safely(() => item.Current.IsPassword)) continue;
                        string? itemText = ExtractElementSingleText(item);
                        if (!string.IsNullOrWhiteSpace(itemText)) itemParts.Add(itemText);
                    }
                    string? joinedItems = JoinSelection(itemParts);
                    if (joinedItems != null)
                    {
                        var itemBounds = Safely(() => selectedItems[0].Current.BoundingRectangle);
                        var resolvedBounds = ValidBounds(itemBounds) && !itemBounds.Contains(cursor.Location) ? itemBounds : cursor;
                        return new(TranslationInput.Prefix(joinedItems, TranslationInput.MaxCharacters), resolvedBounds, selectedItems[0], null, false)
                        { InputTruncated = joinedItems.Length > TranslationInput.MaxCharacters };
                    }
                }
            }
            container = Safely(() => TreeWalker.ControlViewWalker.GetParent(currentContainer));
        }

        // 2. If element is a SelectionItem (e.g., ListItem, DataItem, TreeItem) or static text/value control
        var controlType = Safely(() => element.Current.ControlType);
        bool isSelectedItem = element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var itemObj) &&
            itemObj is SelectionItemPattern itemPattern && Safely(() => itemPattern.Current.IsSelected);

        if (isSelectedItem ||
            controlType == ControlType.Text || controlType == ControlType.DataItem || controlType == ControlType.ListItem ||
            controlType == ControlType.TreeItem || controlType == ControlType.Hyperlink || controlType == ControlType.ToolTip ||
            controlType == ControlType.StatusBar || controlType == ControlType.HeaderItem || controlType == ControlType.Document ||
            controlType == ControlType.Edit)
        {
            string? text = ExtractElementSingleText(element);
            text = text == null ? null : JoinSelection([text]);
            if (text != null)
            {
                var controlBounds = Safely(() => element.Current.BoundingRectangle);
                var bounds = ValidBounds(controlBounds) && !controlBounds.Contains(cursor.Location) ? controlBounds : cursor;
                return new(TranslationInput.Prefix(text, TranslationInput.MaxCharacters), bounds, element, null, false)
                { InputTruncated = text.Length > TranslationInput.MaxCharacters };
            }
        }
        return null;
    }

    private static string? ExtractElementSingleText(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valObj) && valObj is ValuePattern valuePattern)
        {
            string? val = Safely(() => valuePattern.Current.Value);
            if (!string.IsNullOrWhiteSpace(val)) return val;
        }
        string? name = Safely(() => element.Current.Name);
        if (!string.IsNullOrWhiteSpace(name)) return name;

        string? helpText = Safely(() => element.Current.HelpText);
        if (!string.IsNullOrWhiteSpace(helpText)) return helpText;

        // Some list/grid items wrap text inside immediate Text child elements
        var child = Safely(() => TreeWalker.ControlViewWalker.GetFirstChild(element));
        var parts = new List<string>(8);
        for (int i = 0; child != null && i < 8; i++)
        {
            var current = child;
            if (!Safely(() => current.Current.IsPassword))
            {
                string? childName = Safely(() => current.Current.Name);
                if (!string.IsNullOrWhiteSpace(childName)) parts.Add(childName);
            }
            child = Safely(() => TreeWalker.ControlViewWalker.GetNextSibling(current));
        }
        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }

    internal static string? JoinSelection(IEnumerable<string> parts)
    {
        var nonempty = parts
            .Select(SanitizeSelectionPart)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();
        if (nonempty.Length == 0) return null;
        string text = string.Join("\n", nonempty);
        // One extra character signals truncation without reading an unbounded UIA range.
        return text[..Math.Min(text.Length, TranslationInput.MaxCharacters + 1)];
    }

    internal static string SanitizeSelectionPart(string? part)
    {
        if (string.IsNullOrEmpty(part)) return "";
        if (vn_selection_text_reader_needs_sanitization(part.AsSpan(), part.Length, out byte needs) != 0 || needs == 0)
            return part;
        char[] buf = new char[part.Length];
        _ = vn_selection_text_reader_sanitize(part.AsSpan(), part.Length, buf, buf.Length, out int len);
        return new string(buf, 0, Math.Clamp(len, 0, buf.Length));
    }

    internal static bool ValidBounds(Rect r) =>
        vn_selection_text_reader_valid_bounds(r.IsEmpty ? (byte)1 : (byte)0, r.X, r.Y, r.Width, r.Height, out byte valid) == 0 && valid != 0;

    private static bool WithinWindow(AutomationElement element, IntPtr window, uint windowPid, Stopwatch clock)
    {
        int pid = Safely(() => element.Current.ProcessId);
        if (pid == Environment.ProcessId && windowPid != Environment.ProcessId) return false;
        IntPtr handle = new IntPtr(Safely(() => element.Current.NativeWindowHandle));
        if (handle != IntPtr.Zero && IsSameOrChildWindow(window, handle)) return true;
        if (windowPid != 0 && pid == (int)windowPid) return true;

        AutomationElement? current = element;
        for (int depth = 0; current != null && depth < 24 && clock.ElapsedMilliseconds < 800; depth++)
        {
            IntPtr currentHandle = new IntPtr(Safely(() => current.Current.NativeWindowHandle));
            if (currentHandle != IntPtr.Zero)
            {
                if (IsSameOrChildWindow(window, currentHandle)) return true;
                GetWindowThreadProcessId(currentHandle, out uint currentHwndPid);
                if (windowPid != 0 && currentHwndPid == windowPid) return true;
            }
            int currentPid = Safely(() => current.Current.ProcessId);
            if (windowPid != 0 && currentPid == (int)windowPid) return true;
            current = Safely(() => TreeWalker.ControlViewWalker.GetParent(current))
                   ?? Safely(() => TreeWalker.RawViewWalker.GetParent(current));
        }
        return false;
    }

    private static bool IsSameOrChildWindow(IntPtr window, IntPtr candidate)
    {
        if (candidate == IntPtr.Zero) return false;
        if (candidate == window || IsChild(window, candidate) || GetAncestor(candidate, 2) == window || GetAncestor(candidate, 3) == window)
            return true;
        IntPtr rootOwner = GetAncestor(window, 3);
        return rootOwner != IntPtr.Zero && GetAncestor(candidate, 3) == rootOwner;
    }

    private static T? Safely<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ElementNotAvailableException or ArgumentException or NotSupportedException) { return default; }
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace VNotch.Services.Translation;

internal static class SelectionTextReader
{
    internal sealed record Result(string Text, Rect Bounds, AutomationElement Element, TextPatternRange? Range, bool Editable);

    internal static Result? Read(IntPtr window, AutomationElement? eventElement, bool manual, out bool protectedInput)
    {
        protectedInput = false;
        var focused = Safely(() => AutomationElement.FocusedElement);
        if (focused != null && Safely(() => focused.Current.IsPassword)) { protectedInput = true; return null; }
        var point = System.Windows.Forms.Cursor.Position;
        var seeds = new List<AutomationElement?> { eventElement, focused };
        if (manual) seeds.Add(Safely(() => AutomationElement.FromPoint(new Point(point.X, point.Y))));
        var visited = new List<AutomationElement>();
        var clock = Stopwatch.StartNew();
        foreach (var seed in seeds)
        {
            // A stale selection event must not prevent trying the current focus.
            if (seed == null || !Safely(() => WithinWindow(seed, window))) continue;
            AutomationElement? element = seed;
            for (int depth = 0; element != null && depth < 16 && clock.ElapsedMilliseconds < 600; depth++)
            {
                var current = element;
                if (Safely(() => current.Current.IsPassword)) { protectedInput = true; return null; }
                if (visited.Any(x => Safely(() => Automation.Compare(x, current)))) break;
                visited.Add(current);
                var result = Safely(() => ReadElement(current, manual, new Rect(point.X, point.Y, 1, 1)));
                if (result != null) return result;
                if (Safely(() => current.Current.NativeWindowHandle) == window.ToInt64()) break;
                element = Safely(() => TreeWalker.RawViewWalker.GetParent(current));
            }
        }
        return null;
    }

    private static Result? ReadElement(AutomationElement element, bool manual, Rect cursor)
    {
        if (!element.Current.IsEnabled || element.Current.IsPassword || !element.TryGetCurrentPattern(TextPattern.Pattern, out var found) || found is not TextPattern pattern) return null;
        var ranges = pattern.GetSelection();
        if (ranges == null || ranges.Length == 0 || ranges.Length > 16) return null;
        var parts = ranges.Select(r => r.GetText(2001)).ToArray();
        string? text = JoinSelection(parts);
        if (text == null) return null;
        var bounds = Rect.Empty;
        foreach (var range in ranges)
            foreach (var rectangle in Safely(range.GetBoundingRectangles) ?? [])
                if (ValidBounds(rectangle)) bounds.Union(rectangle);
        // Some PDF, browser and custom providers return text but no selection rectangles.
        if (bounds.IsEmpty)
        {
            var controlBounds = Safely(() => element.Current.BoundingRectangle);
            bounds = ValidBounds(controlBounds) ? new Rect(controlBounds.Right, controlBounds.Bottom, 1, 1) : manual ? cursor : Rect.Empty;
        }
        if (bounds.IsEmpty) return null;
        bool editable = ranges.Length == 1 && ranges[0].GetAttributeValue(TextPattern.IsReadOnlyAttribute) is false &&
            (element.Current.ControlType == ControlType.Edit || element.Current.ControlType == ControlType.Document);
        return new(text, bounds, element, ranges.Length == 1 ? ranges[0].Clone() : null, editable);
    }

    internal static string? JoinSelection(IEnumerable<string> parts)
    {
        var nonempty = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        if (nonempty.Length == 0) return null;
        string text = string.Join("\n", nonempty);
        return text.Length <= 2000 ? text : null;
    }
    internal static bool ValidBounds(Rect r) => !r.IsEmpty && double.IsFinite(r.X) && double.IsFinite(r.Y) &&
        double.IsFinite(r.Width) && double.IsFinite(r.Height) && r.Width > 0 && r.Height > 0;

    private static bool WithinWindow(AutomationElement element, IntPtr window)
    {
        for (int depth = 0; element != null && depth < 64; depth++)
        {
            if (new IntPtr(element.Current.NativeWindowHandle) == window) return true;
            element = TreeWalker.RawViewWalker.GetParent(element);
        }
        return false;
    }
    private static T? Safely<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ElementNotAvailableException or ArgumentException) { return default; }
    }
}

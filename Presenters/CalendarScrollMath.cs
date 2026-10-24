using System;
using System.Runtime.InteropServices;

namespace VNotch.Presenters;

public static partial class CalendarScrollMath
{
    public const int TotalDays = 11;
    public const int VisibleDays = 3;
    public const double CellWidth = 30.0;

    public static int ClampIndex(int idx)
    {
        vn_calendar_clamp_index(idx, out int clamped);
        return clamped;
    }

    public static int GetCenterIndexFromStripX(double stripX)
    {
        vn_calendar_center_index_from_strip_x(stripX, out int centerIdx);
        return centerIdx;
    }

    public static double GetHighlightXForIndex(int centerIdx)
    {
        vn_calendar_highlight_x_for_index(centerIdx, out double highlightX);
        return highlightX;
    }

    public static double GetStripXForIndex(int centerIdx)
    {
        vn_calendar_strip_x_for_index(centerIdx, out double stripX);
        return stripX;
    }

    public static ScrollStepResult ComputeScrollStep(double accumulator, int delta, int currentCenterIdx)
    {
        vn_calendar_compute_scroll_step(
            accumulator, delta, currentCenterIdx,
            out double remaining, out int newIdx, out int oldIdx, out int stepCount);
        return new ScrollStepResult(remaining, newIdx, oldIdx, stepCount);
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_calendar_clamp_index(int idx, out int out_clamped);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_calendar_center_index_from_strip_x(double strip_x, out int out_center_idx);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_calendar_highlight_x_for_index(int center_idx, out double out_highlight_x);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_calendar_strip_x_for_index(int center_idx, out double out_strip_x);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_calendar_compute_scroll_step(
        double accumulator,
        int delta,
        int current_center_idx,
        out double out_remaining,
        out int out_new_idx,
        out int out_old_idx,
        out int out_step_count);
}

public readonly record struct ScrollStepResult(
    double ResultAccumulator,
    int NewCenterIdx,
    int OldCenterIdx,
    int StepCount)
{
    public bool HasStep => StepCount > 0;

    public bool IndexChanged => NewCenterIdx != OldCenterIdx;

    public int MovedCells => Math.Abs(NewCenterIdx - OldCenterIdx);
}

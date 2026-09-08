using System;

namespace VNotch.Presenters;

public static class CalendarScrollMath
{
    public const int TotalDays = 11;
    public const int VisibleDays = 3;
    public const double CellWidth = 30.0;

    private const double HighlightDiameter = 24.0;

    public static int ClampIndex(int idx) => Math.Max(0, Math.Min(TotalDays - 1, idx));

    public static int GetCenterIndexFromStripX(double stripX)
    {
        int centerIdx = (int)Math.Round((30.0 - stripX) / CellWidth);
        return ClampIndex(centerIdx);
    }

    public static double GetHighlightXForIndex(int centerIdx)
    {
        return centerIdx * CellWidth + (CellWidth - HighlightDiameter) / 2.0;
    }

    public static double GetStripXForIndex(int centerIdx)
    {
        return (1 * CellWidth) - (centerIdx * CellWidth);
    }

    public static ScrollStepResult ComputeScrollStep(double accumulator, int delta, int currentCenterIdx)
    {
        double acc = accumulator + delta;
        int direction = acc > 0 ? -1 : 1;
        int stepCount = (int)(Math.Abs(acc) / 120.0);
        if (stepCount == 0 && Math.Abs(acc) >= 72)
        {
            stepCount = 1;
        }

        if (stepCount == 0)
        {
            return new ScrollStepResult(acc, currentCenterIdx, currentCenterIdx, 0);
        }

        double remaining = acc - Math.Sign(acc) * stepCount * 120.0;

        int oldIdx = currentCenterIdx;
        int newIdx = ClampIndex(currentCenterIdx + (direction * stepCount));

        return new ScrollStepResult(remaining, newIdx, oldIdx, stepCount);
    }
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

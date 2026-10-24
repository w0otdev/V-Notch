using System;
using System.Runtime.InteropServices;

namespace VNotch.Services;

public sealed partial class NotchShellState
{
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_notchshell_is_effectively_visible(
        byte isNotchVisible, byte isHiddenByFullscreen, byte isHiddenByIdle,
        out byte outVisible);

    public IntPtr Hwnd { get; set; }

    public bool IsNotchVisible { get; set; } = true;

    public bool IsHiddenByFullscreen { get; set; }

    public bool IsHiddenByIdle { get; set; }

    public bool IsTrayMenuOpen { get; set; }

    public DateTime SuspendTopmostUntilUtc { get; set; } = DateTime.MinValue;

    public double CollapsedWidth { get; set; }
    public double CollapsedHeight { get; set; }
    public double ExpandedWidth { get; set; } = 480;
    public double ExpandedHeight { get; set; } = 147;
    public double CornerRadiusCollapsed { get; set; }
    public double CornerRadiusExpanded { get; set; } = 24;
    public int FixedX { get; set; }
    public int FixedY { get; set; }
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public bool HasFixedBounds { get; set; }

    public bool IsEffectivelyNotchVisible =>
        vn_notchshell_is_effectively_visible(
            IsNotchVisible ? (byte)1 : (byte)0,
            IsHiddenByFullscreen ? (byte)1 : (byte)0,
            IsHiddenByIdle ? (byte)1 : (byte)0,
            out byte vis) == 0 && vis != 0;
}

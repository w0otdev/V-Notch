using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace VNotch.Services;

internal static class AnimationConfig
{
    public const int MinFps = 30;
    // Keep this as a user-selectable cap rather than a 120 FPS ceiling. WPF then
    // limits the actual animation rate to the active display refresh below, so a
    // 144/165/240 Hz monitor can animate on every compositor refresh.
    public const int MaxFps = 240;
    private const int FallbackFps = 60;

    private static int _targetFps = DetectRefreshHz(null) ?? FallbackFps;
    private static int _configuredFps = MaxFps;
    private static string? _deviceName;
    private static bool _hooked;
    private static bool _reduceMotion;

    public static int TargetFps => _targetFps;

    public static bool ReduceMotion => _reduceMotion;

    public static event Action? ReduceMotionChanged;

    public static void SetReduceMotion(bool on)
    {
        if (_reduceMotion == on) return;
        _reduceMotion = on;
        ReduceMotionChanged?.Invoke();
    }

    public static void Configure(int animationFps)
    {
        _configuredFps = Math.Clamp(animationFps, MinFps, MaxFps);
        Recompute();
    }

    public static void Refresh(string? deviceName = null)
    {
        if (deviceName != null) _deviceName = deviceName;

        if (!_hooked)
        {
            _hooked = true;
            SystemEvents.DisplaySettingsChanged += (_, _) => Recompute();
        }

        Recompute();
    }

    private static void Recompute()
    {
        _targetFps = ComputeTargetFps(_configuredFps, DetectRefreshHz(_deviceName) ?? DetectRefreshHz(null));
    }

    internal static int ComputeTargetFps(int configuredFps, int? detectedRefreshHz)
    {
        int configuredCap = Math.Clamp(configuredFps, MinFps, MaxFps);
        int displayRefresh = detectedRefreshHz is >= 24 and <= 1000
            ? detectedRefreshHz.Value
            : FallbackFps;

        return Math.Min(configuredCap, displayRefresh);
    }

    private static int? DetectRefreshHz(string? deviceName)
    {
        try
        {
            var dm = new Win32Interop.DEVMODE { dmSize = (ushort)Marshal.SizeOf<Win32Interop.DEVMODE>() };
            if (Win32Interop.EnumDisplaySettings(deviceName, Win32Interop.ENUM_CURRENT_SETTINGS, ref dm))
            {
                if (dm.dmDisplayFrequency > 1) return (int)dm.dmDisplayFrequency;
            }
        }
        catch
        {
        }

        return null;
    }
}

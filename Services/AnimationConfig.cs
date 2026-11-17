using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace VNotch.Services;

internal static class AnimationConfig
{
    public const int MinFps = 30;
    // User-selectable animation target, independent of display refresh rate.
    public const int MaxFps = 550;

    private static int _targetFps = MaxFps;
    private static int _configuredFps = MaxFps;
    private static string? _deviceName;
    private static bool _hooked;
    private static bool _reduceMotion;
    private static bool _autoFps = true;

    public static int TargetFps => _targetFps;

    // Slow looping ambient effects (title shimmer, charging pulse) look the same at 30 fps, but each
    // extra frame makes WPF recompose the whole layered window, so they must not follow a 120 Hz target.
    public static int AmbientFps => ComputeAmbientFps(_targetFps);

    public static bool ReduceMotion => _reduceMotion;

    public static event Action? ReduceMotionChanged;

    public static void SetReduceMotion(bool on)
    {
        if (_reduceMotion == on) return;
        _reduceMotion = on;
        ReduceMotionChanged?.Invoke();
    }

    public static void Configure(int animationFps, bool autoFps = true)
    {
        _autoFps = autoFps;
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
        _targetFps = ComputeTargetFps(_configuredFps, DetectRefreshHz(_deviceName) ?? DetectRefreshHz(null), _autoFps);
    }

    internal static int ComputeAmbientFps(int targetFps) => Math.Min(targetFps, MinFps);

    internal static int ComputeTargetFps(int configuredFps, int? detectedRefreshHz, bool autoFps = false)
    {
        if (autoFps)
            return detectedRefreshHz is >= 24 and <= 1000 ? Math.Min(detectedRefreshHz.Value, MaxFps) : 60;
        return Math.Clamp(configuredFps, MinFps, MaxFps);
    }

    private static int? DetectRefreshHz(string? deviceName)
    {
        try
        {
            return Win32Interop.GetDisplayRefreshHz(deviceName);
        }
        catch (Exception)
        {
            // Fall back to default refresh rate if display settings cannot be enumerated
        }

        return null;
    }
}

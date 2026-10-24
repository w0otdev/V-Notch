using System.Runtime.InteropServices;
using VNotch.Models;

namespace VNotch.Services;

/// <summary>Owns committed and preview snapshots for one settings editing session.</summary>
internal sealed partial class SettingsEditSession
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, EntryPoint = "vn_settings_edit_session_should_apply_preview")]
    private static partial int NativeShouldApplyPreview(byte valueEquals, out byte outShouldApply);

    private NotchSettings _baseline;
    private NotchSettings _applied;
    internal SettingsEditSession(NotchSettings settings)
    {
        _baseline = settings.Clone();
        _applied = settings.Clone();
    }
    internal NotchSettings Baseline => _baseline.Clone();
    internal NotchSettings Applied => _applied.Clone();

    internal bool Preview(NotchSettings settings)
    {
        NativeShouldApplyPreview(_applied.ValueEquals(settings) ? (byte)1 : (byte)0, out byte shouldApply);
        if (shouldApply == 0) return false;
        _applied = settings.Clone();
        return true;
    }

    internal void Commit(NotchSettings settings) => _baseline = settings.Clone();
    internal void Reset(NotchSettings settings)
    {
        Commit(settings);
        _applied = settings.Clone();
    }
    internal NotchSettings Revert()
    {
        _applied = _baseline.Clone();
        return _baseline.Clone();
    }
    internal NotchSettings CommitInvisibleMode(bool enabled)
    {
        _baseline.InvisibleMode = enabled;
        return Baseline;
    }
    internal NotchSettings CommitCpp26NoticeSeen()
    {
        _baseline.HasSeenCpp26Notice = true;
        _applied.HasSeenCpp26Notice = true;
        return Baseline;
    }
    internal void EnsureCustomGlass(LiquidGlassConfig config)
    {
        _applied.LiquidGlassCustom ??= config.Clone();
    }
}

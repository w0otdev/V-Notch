using VNotch.Models;

namespace VNotch.Services;

/// <summary>Owns committed and preview snapshots for one settings editing session.</summary>
internal sealed class SettingsEditSession
{
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
        if (_applied.ValueEquals(settings)) return false;
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
    internal void EnsureCustomGlass(LiquidGlassConfig config)
    {
        _baseline.LiquidGlassCustom ??= config.Clone();
        _applied.LiquidGlassCustom ??= config.Clone();
    }
}

using VNotch.Models;

namespace VNotch.Controllers;

/// <summary>Coordinates one invisible-mode command and its persistence.</summary>
internal sealed class InvisibleModeController(
    Func<NotchSettings> settings,
    Action applyVisibility,
    Func<NotchSettings, Task> save,
    Func<Task<bool>> toggleInSettings)
{
    private int _saving;

    internal async Task ToggleAsync()
    {
        if (Interlocked.CompareExchange(ref _saving, 1, 0) != 0) return;
        try
        {
            if (await toggleInSettings()) return;
            var current = settings();
            current.InvisibleMode = !current.InvisibleMode;
            applyVisibility();
            await save(current.Clone());
        }
        finally { Volatile.Write(ref _saving, 0); }
    }
}

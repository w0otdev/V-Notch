using System.Runtime.InteropServices;

namespace VNotch.Services;

// Thin native UI facade: the standalone uninstaller does not need WinForms or
// the Windows Desktop runtime just to display confirmation/result messages.
internal static partial class SetupDialogs
{
    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_setup_show_message", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
    private static partial int ShowMessage(string text, int textLength, string title, int titleLength, int mode, out int result);

    internal static bool ConfirmUninstall(string text, string title)
    {
        if (ShowMessage(text, text.Length, title, title.Length, 0, out int result) != 0)
            throw new InvalidOperationException("Could not show the uninstall confirmation.");
        return result == 6; // IDYES; the native dialog defaults to No.
    }

    internal static void ShowUninstallResult(string text, string title, bool hasErrors)
    {
        if (ShowMessage(text, text.Length, title, title.Length, hasErrors ? 2 : 1, out _) != 0)
            throw new InvalidOperationException("Could not show the uninstall result.");
    }
}

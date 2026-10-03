// The real updater and signature policy come from the immutable 1.9.3 commit.
// These UI/log adapters are never called to install or launch anything.
namespace VNotch.Services
{
    internal static class RuntimeLog
    {
        public static void Log(string category, string message) { }
        public static void Warn(string category, string message) { }
        public static void Error(string category, Exception error, string message) { }
    }

    internal static class Loc
    {
        public static string Get(string key, params object[] args) => key;
    }
}

namespace VNotch.Windows
{
    internal static class ConfirmationDialog
    {
        public enum DialogIcon { Warning }
        public enum DialogStyle { Danger }
        public sealed record DialogOptions(string Title, string ConfirmText, string CancelText, DialogIcon Icon, DialogStyle Style);
        public static bool Show(System.Windows.Window? owner, string message, DialogOptions options)
            => throw new InvalidOperationException("UI execution is forbidden in the compatibility probe.");
    }
}

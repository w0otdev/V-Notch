// Real updater/security sources come from the pinned 1.9.3 client or the current checkout.
// These UI/log adapters are never called to install or launch anything.
namespace VNotch.Services
{
    internal static class RuntimeLog
    {
        public static void Log(string category, string message) { }
        public static void Warn(string category, string message) { }
        public static void Error(string category, Exception error, string message) { }
        public static void Error(string category, string message) { }
    }

    internal static class Loc
    {
        public static string Get(string key, params object[] args) => key;
    }

    // The probe injects its in-memory HTTP transport; these adapters cannot access the network.
    internal enum NetworkFeature { Updates }
    internal static class NetworkPrivacy
    {
        public static System.Net.Http.HttpMessageHandler Handler(NetworkFeature feature, System.Net.Http.HttpMessageHandler? inner = null)
            => new ForbiddenNetworkHandler();

        private sealed class ForbiddenNetworkHandler : System.Net.Http.HttpMessageHandler
        {
            protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
                => throw new InvalidOperationException("Real network access is forbidden in the compatibility probe.");
        }
    }

    internal static class AppFileCleanupService
    {
        public static IDisposable Protect(string path)
            => throw new InvalidOperationException("Installer execution is forbidden in the compatibility probe.");
    }
}

namespace VNotch
{
    internal static class App
    {
        public static Task RequestShutdownAsync()
            => throw new InvalidOperationException("Application shutdown is forbidden in the compatibility probe.");
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

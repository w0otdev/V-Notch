using System.Runtime.InteropServices;

namespace VNotch.Services.AiUsage;

// Local preview transport only; never changes provider samples or threshold history.
internal static partial class AiQuotaPreview
{
    internal static int Send(string threshold, string provider)
    {
        try { return SendNative(threshold.AsSpan(), threshold.Length, provider.AsSpan(), provider.Length); }
        catch (EntryPointNotFoundException) { return -3; }
        catch (DllNotFoundException) { return -3; }
    }

    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_ai_quota_preview_register")]
    internal static partial int Register(IntPtr window, out uint message);

    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_ai_quota_preview_unregister")]
    internal static partial int Unregister(IntPtr window);

    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_ai_quota_preview_send", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SendNative(ReadOnlySpan<char> threshold, int thresholdLength,
        ReadOnlySpan<char> provider, int providerLength);
}

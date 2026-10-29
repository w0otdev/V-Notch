using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace VNotch.Services.Translation;

internal static class TranslationNativeRuntime
{
    // The engine serializes initialization. Keep the library resident for the process lifetime.
    private static IntPtr _handle;
    private static string? _library;

    internal static string Load()
    {
        if (_handle != IntPtr.Zero && _library != null) return _library;
        foreach (string path in Candidates(AppContext.BaseDirectory, Avx.IsSupported, Avx2.IsSupported, Avx512F.IsSupported))
        {
            if (!File.Exists(path)) continue;
            // Resolve ggml beside llama.dll even with App's hardened DLL search policy.
            if (!NativeLibrary.TryLoad(path, typeof(TranslationNativeRuntime).Assembly,
                DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32 | DllImportSearchPath.ApplicationDirectory,
                out var handle)) continue;
            _handle = handle;
            return _library = path;
        }
        throw new TranslationException("translation.runtimeMissing");
    }

    internal static IEnumerable<string> Candidates(string root, bool avx, bool avx2, bool avx512)
    {
        string native = Path.Combine(Path.GetFullPath(root), "runtimes", "win-x64", "native");
        yield return Path.Combine(native, "vulkan", "llama.dll");
        if (avx512) yield return Path.Combine(native, "avx512", "llama.dll");
        if (avx2) yield return Path.Combine(native, "avx2", "llama.dll");
        if (avx) yield return Path.Combine(native, "avx", "llama.dll");
        yield return Path.Combine(native, "noavx", "llama.dll");
    }

    internal static bool IsLoadFailure(Exception error) =>
        error is DllNotFoundException or BadImageFormatException ||
        error is TypeInitializationException { TypeName: "LLama.Native.NativeApi" } ||
        error.InnerException != null && IsLoadFailure(error.InnerException);
}

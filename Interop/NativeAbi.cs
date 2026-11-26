using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VNotch.Interop;

// One resolver gates all host imports, including legacy declarations in partial services.
// Registration is cheap: the DLL is loaded and checked only on its first actual import.
internal static class NativeAbi
{
    internal const string LibraryName = "VNotch.Native.dll";
    private static readonly Lazy<nint> Module = new(LoadCompatibleModule,
        LazyThreadSafetyMode.ExecutionAndPublication);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct Info
    {
        internal uint Size, Major, Minor, PointerSize, RegistryHandleVersion, StatusVersion;
    }

#pragma warning disable CA2255 // The host owns its single assembly-wide native resolver.
    [ModuleInitializer]
    internal static void Register() => NativeLibrary.SetDllImportResolver(typeof(NativeAbi).Assembly, Resolve);
#pragma warning restore CA2255

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath) =>
        string.Equals(name, LibraryName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "VNotch.Native", StringComparison.OrdinalIgnoreCase) ? Module.Value : 0;

    private static nint LoadCompatibleModule() =>
        LoadCheckedModule(Path.Combine(AppContext.BaseDirectory, LibraryName));

    internal static unsafe nint LoadCheckedModule(string path)
    {
        const DllImportSearchPath searchFlags =
            DllImportSearchPath.UseDllDirectoryForDependencies |
            DllImportSearchPath.System32 |
            DllImportSearchPath.ApplicationDirectory;
        nint module = NativeLibrary.Load(path, typeof(NativeAbi).Assembly, searchFlags);
        try
        {
            if (!NativeLibrary.TryGetExport(module, "vn_abi_query", out nint address))
                throw new BadImageFormatException("VNotch.Native.dll has no ABI descriptor. Rebuild or reinstall the matching native library.");
            var query = (delegate* unmanaged[Stdcall]<uint, Info*, int>)address;
            Info info = default;
            NativeResult.RequireSuccess(query((uint)sizeof(Info), &info), "vn_abi_query");
            Validate(info);
            return module; // Retained for the process lifetime; imports and handles depend on it.
        }
        catch
        {
            NativeLibrary.Free(module);
            throw;
        }
    }

    internal static void Validate(Info info)
    {
        if (info.Size != Marshal.SizeOf<Info>() || info.Major != 2 || info.Minor < 1 ||
            info.PointerSize != (uint)IntPtr.Size || info.RegistryHandleVersion != 1 || info.StatusVersion != 1)
            throw new BadImageFormatException(
                $"Incompatible VNotch.Native.dll ABI {info.Major}.{info.Minor} (size={info.Size}, pointer={info.PointerSize}, handles={info.RegistryHandleVersion}, status={info.StatusVersion}). Expected ABI 2.1 or a later 2.x; rebuild or reinstall the matching library.");
    }
}

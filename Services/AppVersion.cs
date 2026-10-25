using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace VNotch.Services;

public static partial class AppVersion
{
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_app_version_strip_informational(
        ReadOnlySpan<char> infoPtr,
        int infoLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_app_version_format_assembly(
        byte hasVersion,
        int major,
        int minor,
        int build,
        int revision,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    public static string Current { get; } = Resolve(
        typeof(AppVersion).Assembly.GetName().Version,
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    public static string UserAgent { get; } = $"V-Notch/{Current} (https://github.com/w0otdev/V-Notch)";

    internal static string Resolve(Version? assemblyVersion, string? informationalVersion)
    {
        Span<char> buf = stackalloc char[128];
        if (informationalVersion != null && UpdateService.TryParseReleaseVersion(informationalVersion, out _, out _))
        {
            ReadOnlySpan<char> infoSpan = informationalVersion.AsSpan();
            if (vn_app_version_strip_informational(infoSpan, infoSpan.Length, buf, buf.Length, out int len) == 0)
                return new string(buf[..len]);
            return informationalVersion.Split('+')[0].TrimStart('v', 'V');
        }
        if (assemblyVersion == null)
        {
            if (vn_app_version_format_assembly(0, 0, 0, 0, 0, buf, buf.Length, out int len0) == 0)
                return new string(buf[..len0]);
            return "0.0.0";
        }
        var version = assemblyVersion;
        if (vn_app_version_format_assembly(1, version.Major, version.Minor, version.Build, version.Revision, buf, buf.Length, out int vLen) == 0)
            return new string(buf[..vLen]);
        return version.Revision > 0
            ? $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}.{version.Revision}"
            : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }
}

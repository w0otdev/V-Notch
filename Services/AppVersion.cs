using System.Reflection;

namespace VNotch.Services;

public static class AppVersion
{
    public static string Current { get; } = Resolve(
        typeof(AppVersion).Assembly.GetName().Version,
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    internal static string Resolve(Version? assemblyVersion, string? informationalVersion)
    {
        if (informationalVersion != null && UpdateService.TryParseReleaseVersion(informationalVersion, out _, out _))
            return informationalVersion.Split('+')[0].TrimStart('v', 'V');
        if (assemblyVersion == null) return "0.0.0";
        var version = assemblyVersion;
        return version.Revision > 0
            ? $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}.{version.Revision}"
            : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }
}

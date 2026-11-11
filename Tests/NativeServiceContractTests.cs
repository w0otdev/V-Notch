using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using VNotch.Services;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

public sealed class NativeServiceContractTests(ITestOutputHelper output)
{
    private static bool IsVNotchNativeLibrary(string? name) =>
        string.Equals(name, "VNotch.Native.dll", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "VNotch.Native", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void EveryApplicationNativeImportHasAnExportInTheShippedLibrary()
    {
        const string library = "VNotch.Native.dll";
        var imports = typeof(ServiceConfigurator).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Static | BindingFlags.Instance |
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .SelectMany(method =>
            {
                var generated = method.GetCustomAttribute<LibraryImportAttribute>();
                var legacy = method.GetCustomAttribute<DllImportAttribute>();
                var names = new List<string>();
                if (generated != null && IsVNotchNativeLibrary(generated.LibraryName))
                    names.Add(generated.EntryPoint ?? method.Name);
                if (legacy != null && IsVNotchNativeLibrary(legacy.Value))
                    names.Add(legacy.EntryPoint ?? method.Name);
                return names;
            }).Distinct(StringComparer.Ordinal).Order().ToArray();
        Assert.NotEmpty(imports);
        nint handle = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, library));
        try
        {
            var missing = imports.Where(name => !NativeLibrary.TryGetExport(handle, name, out _)).ToArray();
            output.WriteLine($"Checked {imports.Length} distinct native entry points from the application assembly.");
            Assert.True(missing.Length == 0, "Missing native entry points: " + string.Join(", ", missing));
        }
        finally { NativeLibrary.Free(handle); }
    }
}

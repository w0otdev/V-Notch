using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using VNotch.Services;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

public sealed class NativeServiceContractTests(ITestOutputHelper output)
{
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
                if (generated != null && string.Equals(generated.LibraryName, library, StringComparison.OrdinalIgnoreCase))
                    names.Add(generated.EntryPoint ?? method.Name);
                if (legacy != null && string.Equals(legacy.Value, library, StringComparison.OrdinalIgnoreCase))
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

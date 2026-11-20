using System.IO;

namespace VNotch.Tests;

internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("vnotch-test-").FullName;
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

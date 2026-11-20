using System.IO;

namespace VNotch.Tests;

internal static class SearchFixtureDirectory
{
    // Native discovery filters AppData and Temp path components even when no hidden roots are supplied.
    internal static string Create(Environment.SpecialFolder parent = Environment.SpecialFolder.UserProfile)
    {
        string directory = Environment.GetFolderPath(parent, Environment.SpecialFolderOption.DoNotVerify);
        if (!Path.IsPathFullyQualified(directory)) throw new DirectoryNotFoundException($"Missing {parent} directory.");
        return Directory.CreateDirectory(Path.Combine(directory, $"vnotch-search-{Guid.NewGuid():N}")).FullName;
    }
}

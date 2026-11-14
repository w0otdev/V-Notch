using System.IO;
using System.Text;

namespace VNotch.Services.AiUsage;

internal static class AiUsageFiles
{
    internal static void CheckPath(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Usage files cannot follow symbolic links or junctions.");
    }

    internal static string Read(string path, int maxBytes)
    {
        CheckPath(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > maxBytes) throw new IOException("Usage file too large.");
        using var reader = new StreamReader(file, Encoding.UTF8, true);
        char[] chars = new char[maxBytes + 1];
        int count = reader.ReadBlock(chars, 0, chars.Length);
        if (count > maxBytes) throw new IOException("Usage file too large.");
        return new string(chars, 0, count);
    }

    internal static void WriteAtomic(string path, string content)
    {
        CheckPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(content);
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            CheckPath(path);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

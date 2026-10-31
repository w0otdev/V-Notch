using System.IO;
using VNotch.Models;
using VNotch.Services.Clipboard;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ClipboardBatchImportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportFilesHandlesThreeWayFilenameAndZipCollisionsWithoutThrowing(bool personal)
    {
        string root = Path.Combine(Path.GetTempPath(), "vnotch-batch-collision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string f1 = Path.Combine(root, "1-doc.txt");
            string sub1 = Path.Combine(root, "sub1");
            string sub2 = Path.Combine(root, "sub2");
            Directory.CreateDirectory(sub1);
            Directory.CreateDirectory(sub2);
            string f2 = Path.Combine(sub1, "doc.txt");
            string f3 = Path.Combine(sub2, "doc.txt");

            await File.WriteAllTextAsync(f1, "content-1-doc");
            await File.WriteAllTextAsync(f2, "content-sub1-doc");
            await File.WriteAllTextAsync(f3, "content-sub2-doc");

            using var store = new ClipboardHistoryStore(Path.Combine(root, "history"));
            await store.InitializeAsync();
            using var pin = new System.Security.SecureString();
            foreach (char c in "123456") pin.AppendChar(c);
            pin.MakeReadOnly();
            Assert.True(await store.UnlockPersonalAsync(pin, create: true));

            var entry = (await store.ImportAsync(new ClipboardCapture
            {
                FilePaths = [f1, f2, f3],
                TargetCategory = personal ? "Personal" : null
            })).Entry!;
            string[] exported = await store.ExportFilesAsync(entry.Id);

            static async Task<string> ReadExportedTextAsync(string path)
            {
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                using var sr = new StreamReader(fs);
                return await sr.ReadToEndAsync();
            }

            Assert.Equal(3, exported.Length);
            Assert.Equal(3, exported.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal("content-1-doc", await ReadExportedTextAsync(exported[0]));
            Assert.Equal("content-sub1-doc", await ReadExportedTextAsync(exported[1]));
            Assert.Equal("content-sub2-doc", await ReadExportedTextAsync(exported[2]));

            string zipFile = Path.Combine(root, "bundle.zip");
            string folderWithSameStem = Path.Combine(sub1, "bundle");
            Directory.CreateDirectory(folderWithSameStem);
            await File.WriteAllTextAsync(zipFile, "raw-zip-file");
            await File.WriteAllTextAsync(Path.Combine(folderWithSameStem, "nested.txt"), "inside-folder");

            var zipCollisionEntry = (await store.ImportAsync(new ClipboardCapture
            {
                FilePaths = [zipFile, folderWithSameStem],
                TargetCategory = personal ? "Personal" : null
            })).Entry!;
            string[] zipExported = await store.ExportFilesAsync(zipCollisionEntry.Id);
            Assert.Equal(2, zipExported.Length);
            Assert.True(File.Exists(zipExported[0]));
            Assert.True(Directory.Exists(zipExported[1]));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LockedFileDoesNotDiscardNeighborsOrContaminateDeduplication(bool personal)
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string first = Path.Combine(root, "Hop_dong, Final.docx");
            string locked = Path.Combine(root, "locked.txt");
            string last = Path.Combine(root, new string('x', 130) + ".pdf");
            await File.WriteAllTextAsync(first, "first");
            await File.WriteAllTextAsync(locked, "locked");
            await File.WriteAllTextAsync(last, "last");
            using var store = new ClipboardHistoryStore(Path.Combine(root, "history"));
            await store.InitializeAsync();
            if (personal)
            {
                using var pin = new System.Security.SecureString();
                foreach (char c in "123456") pin.AppendChar(c);
                Assert.True(await store.UnlockPersonalAsync(pin, true));
            }
            using var exclusive = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var result = await store.ImportAsync(new ClipboardCapture { TargetCategory = personal ? "Personal" : null, FilePaths = [first, locked, last] });
            Assert.Equal(2, result.ImportedCount);
            Assert.Equal(1, result.FailedCount);
            Assert.Equal(2, result.Entry!.FileCount);
            Assert.Empty(result.Entry.PrimaryExtension);
            var content = await store.GetContentAsync(result.Entry.Id);
            Assert.Equal(new[] { Path.GetFileName(first), Path.GetFileName(last) }, content.Files.Select(f => f.Name));
            Assert.Equal(9, result.Entry.ByteSize);
            var repeated = await store.ImportAsync(new ClipboardCapture { TargetCategory = personal ? "Personal" : null, FilePaths = [first, last] });
            Assert.Equal(result.Entry.Id, repeated.Entry!.Id);
            Assert.Equal(2, Directory.GetFiles(Path.Combine(root, "history", personal ? "personal" : "items", result.Entry.Id.ToString("N")), personal ? "*.bin.enc" : "*.bin").Length);

            foreach (var path in new[] { first, last })
            {
                var single = await store.ImportAsync(new ClipboardCapture { TargetCategory = personal ? "Personal" : null, FilePaths = [path] });
                Assert.Equal(Path.GetExtension(path), single.Entry!.PrimaryExtension);
                var card = new ClipboardCardViewModel(single.Entry);
                Assert.Equal(Path.GetExtension(path), card.FileExtension);
            }
            var failed = await store.ImportAsync(new ClipboardCapture { TargetCategory = personal ? "Personal" : null, FilePaths = [locked] });
            Assert.Null(failed.Entry);
            Assert.Equal(1, failed.FailedCount);
            Assert.Empty(Directory.GetDirectories(Path.Combine(root, "history", "staging")));
        }
        finally { Directory.Delete(root, true); }
    }
}

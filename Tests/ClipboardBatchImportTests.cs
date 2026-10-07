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
    public async Task LockedFileDoesNotDiscardNeighborsOrContaminateDeduplication(bool personal)
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string first = Path.Combine(root, "Hop_dong, Final.docx");
            string locked = Path.Combine(root, "locked.txt");
            string last = Path.Combine(root, new string('x', 130) + ".pdf");
            File.WriteAllText(first, "first");
            File.WriteAllText(locked, "locked");
            File.WriteAllText(last, "last");
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

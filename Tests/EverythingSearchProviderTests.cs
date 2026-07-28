using System.Runtime.InteropServices;
using System.Text;
using VNotch.Services.Spotlight.Providers;
using Xunit;

namespace VNotch.Tests;

/// <summary>
/// Locks the EVERYTHING_IPC_LISTW v1 layout the provider parses: a 28-byte
/// header (totfolders, totfiles, totitems, numfolders, numfiles, numitems,
/// offset) followed by 12-byte items whose string offsets are relative to the
/// start of the list.
/// </summary>
public sealed class EverythingSearchProviderTests
{
    private const int HeaderBytes = 28;
    private const int ItemBytes = 12;
    private const int FolderFlag = 0x1;

    [Fact]
    public void ParseReply_ReadsMixedFolderAndFileItems()
    {
        var strings = new List<(string Value, int Offset)>();
        int stringBase = HeaderBytes + 2 * ItemBytes;
        int cursor = stringBase;
        int Add(string value)
        {
            strings.Add((value, cursor));
            int offset = cursor;
            cursor += (value.Length + 1) * sizeof(char);
            return offset;
        }

        int folderName = Add("Projects");
        int folderParent = Add(@"C:\Users\dev");
        int fileName = Add("report.pdf");
        int fileParent = Add(@"C:\Users\dev\Projects");

        int totalBytes = cursor;
        IntPtr buffer = Marshal.AllocHGlobal(totalBytes);
        try
        {
            // Header: index-wide totals first, then the counts for this list.
            Marshal.WriteInt32(buffer, 0, 5000);   // totfolders in index
            Marshal.WriteInt32(buffer, 4, 90000);  // totfiles in index
            Marshal.WriteInt32(buffer, 8, 95000);  // totitems in index
            Marshal.WriteInt32(buffer, 12, 1);     // numfolders in list
            Marshal.WriteInt32(buffer, 16, 1);     // numfiles in list
            Marshal.WriteInt32(buffer, 20, 2);     // numitems in list
            Marshal.WriteInt32(buffer, 24, 0);     // offset of first result

            WriteItem(buffer, HeaderBytes, FolderFlag, folderName, folderParent);
            WriteItem(buffer, HeaderBytes + ItemBytes, 0, fileName, fileParent);
            foreach ((string value, int offset) in strings)
                WriteString(buffer, offset, value);

            var rows = EverythingSearchProvider.ParseReply(buffer, totalBytes);

            Assert.Equal(2, rows.Count);
            Assert.Equal(("Projects", @"C:\Users\dev", true), rows[0]);
            Assert.Equal(("report.pdf", @"C:\Users\dev\Projects", false), rows[1]);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void ParseReply_IgnoresItemsWithOutOfRangeOffsets_AndTruncatedLists()
    {
        int totalBytes = HeaderBytes + 2 * ItemBytes + 32;
        IntPtr buffer = Marshal.AllocHGlobal(totalBytes);
        try
        {
            for (int offset = 0; offset < totalBytes; offset += 4)
                Marshal.WriteInt32(buffer, offset, 0);

            // Claim far more items than the buffer holds; the parser must stop
            // at the buffer boundary instead of reading past it.
            Marshal.WriteInt32(buffer, 20, int.MaxValue);

            int nameOffset = HeaderBytes + 2 * ItemBytes;
            WriteString(buffer, nameOffset, "ok.txt");
            WriteItem(buffer, HeaderBytes, 0, nameOffset, totalBytes + 128); // bad parent offset
            WriteItem(buffer, HeaderBytes + ItemBytes, 0, nameOffset, nameOffset);

            var rows = EverythingSearchProvider.ParseReply(buffer, totalBytes);

            Assert.Single(rows);
            Assert.Equal(("ok.txt", "ok.txt", false), rows[0]);

            Assert.Empty(EverythingSearchProvider.ParseReply(buffer, HeaderBytes - 4));
            Assert.Empty(EverythingSearchProvider.ParseReply(IntPtr.Zero, totalBytes));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void WriteItem(IntPtr buffer, int itemOffset, int flags, int nameOffset, int parentOffset)
    {
        Marshal.WriteInt32(buffer, itemOffset, flags);
        Marshal.WriteInt32(buffer, itemOffset + 4, nameOffset);
        Marshal.WriteInt32(buffer, itemOffset + 8, parentOffset);
    }

    private static void WriteString(IntPtr buffer, int offset, string value)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(value + "\0");
        Marshal.Copy(bytes, 0, buffer + offset, bytes.Length);
    }
}

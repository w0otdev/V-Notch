using System;
using System.Runtime.InteropServices;

namespace VNotch.Services;

public enum VnMarkdownBlockKind : byte
{
    Empty = 0,
    Heading = 1,
    HorizontalRule = 2,
    TableRow = 3,
    TableSeparator = 4,
    Blockquote = 5,
    ChecklistItem = 6,
    BulletItem = 7,
    NumberedItem = 8,
    Paragraph = 9,
}

public enum VnMarkdownInlineKind : byte
{
    Text = 0,
    Bold = 1,
    Italic = 2,
    Code = 3,
    Link = 4,
    Url = 5,
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VnMarkdownBlockToken
{
    public byte Kind;
    public byte Level;
    public byte IsChecked;
    public byte HardBreak;
    public int TextStart;
    public int TextLen;
    public int MarkerStart;
    public int MarkerLen;

    public readonly VnMarkdownBlockKind BlockKind => (VnMarkdownBlockKind)Kind;
    public readonly bool Checked => IsChecked != 0;
    public readonly bool HasHardBreak => HardBreak != 0;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VnMarkdownSlice
{
    public int Start;
    public int Len;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VnMarkdownInlineToken
{
    public byte Kind;
    public int TextStart;
    public int TextLen;
    public int AuxStart;
    public int AuxLen;

    public readonly VnMarkdownInlineKind InlineKind => (VnMarkdownInlineKind)Kind;
}

public static partial class VNotchNative
{
    public const int VN_OK = 0;
    public const int VN_ERR_INVALID_ARG = -1;
    public const int VN_ERR_BUFFER_TOO_SMALL = -2;
    public const int VN_ERR_FAIL = -3;

    [LibraryImport("VNotch.Native", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int vn_markdown_classify_line(
        ReadOnlySpan<char> line_ptr,
        int line_len,
        out VnMarkdownBlockToken out_token);

    [LibraryImport("VNotch.Native", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int vn_markdown_tokenize_blocks(
        ReadOnlySpan<char> text_ptr,
        int text_len,
        Span<VnMarkdownBlockToken> out_tokens,
        int token_cap,
        out int out_written);

    [LibraryImport("VNotch.Native", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int vn_markdown_split_table_cells(
        ReadOnlySpan<char> line_ptr,
        int line_len,
        Span<VnMarkdownSlice> out_cells,
        int cell_cap,
        out int out_written);

    [LibraryImport("VNotch.Native", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int vn_markdown_tokenize_inlines(
        ReadOnlySpan<char> text_ptr,
        int text_len,
        Span<VnMarkdownInlineToken> out_tokens,
        int token_cap,
        out int out_written);

    public static VnMarkdownBlockToken ClassifyMarkdownLine(ReadOnlySpan<char> line)
    {
        if (line.IsEmpty)
        {
            return default;
        }

        int rc = vn_markdown_classify_line(line, line.Length, out var token);
        return rc == VN_OK ? token : new VnMarkdownBlockToken
        {
            Kind = (byte)VnMarkdownBlockKind.Paragraph,
            TextStart = 0,
            TextLen = line.Length
        };
    }

    public static VnMarkdownBlockToken[] TokenizeMarkdownBlocks(ReadOnlySpan<char> markdown)
    {
        if (markdown.IsEmpty)
        {
            return Array.Empty<VnMarkdownBlockToken>();
        }

        Span<VnMarkdownBlockToken> stackBuf = stackalloc VnMarkdownBlockToken[128];
        int rc = vn_markdown_tokenize_blocks(markdown, markdown.Length, stackBuf, stackBuf.Length, out int written);
        if (rc == VN_OK)
        {
            return written > 0 ? stackBuf[..written].ToArray() : Array.Empty<VnMarkdownBlockToken>();
        }

        if (rc == VN_ERR_BUFFER_TOO_SMALL && written > 0)
        {
            var heapBuf = new VnMarkdownBlockToken[written];
            int rc2 = vn_markdown_tokenize_blocks(markdown, markdown.Length, heapBuf, heapBuf.Length, out int written2);
            if (rc2 == VN_OK && written2 > 0)
            {
                return written2 == heapBuf.Length ? heapBuf : heapBuf[..written2];
            }
        }

        return Array.Empty<VnMarkdownBlockToken>();
    }

    public static VnMarkdownSlice[] SplitMarkdownTableCells(ReadOnlySpan<char> line)
    {
        if (line.IsEmpty)
        {
            return Array.Empty<VnMarkdownSlice>();
        }

        Span<VnMarkdownSlice> stackBuf = stackalloc VnMarkdownSlice[32];
        int rc = vn_markdown_split_table_cells(line, line.Length, stackBuf, stackBuf.Length, out int written);
        if (rc == VN_OK)
        {
            return written > 0 ? stackBuf[..written].ToArray() : Array.Empty<VnMarkdownSlice>();
        }

        if (rc == VN_ERR_BUFFER_TOO_SMALL && written > 0)
        {
            var heapBuf = new VnMarkdownSlice[written];
            int rc2 = vn_markdown_split_table_cells(line, line.Length, heapBuf, heapBuf.Length, out int written2);
            if (rc2 == VN_OK && written2 > 0)
            {
                return written2 == heapBuf.Length ? heapBuf : heapBuf[..written2];
            }
        }

        return Array.Empty<VnMarkdownSlice>();
    }

    public static VnMarkdownInlineToken[] TokenizeMarkdownInlines(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return Array.Empty<VnMarkdownInlineToken>();
        }

        Span<VnMarkdownInlineToken> stackBuf = stackalloc VnMarkdownInlineToken[64];
        int rc = vn_markdown_tokenize_inlines(text, text.Length, stackBuf, stackBuf.Length, out int written);
        if (rc == VN_OK)
        {
            return written > 0 ? stackBuf[..written].ToArray() : Array.Empty<VnMarkdownInlineToken>();
        }

        if (rc == VN_ERR_BUFFER_TOO_SMALL && written > 0)
        {
            var heapBuf = new VnMarkdownInlineToken[written];
            int rc2 = vn_markdown_tokenize_inlines(text, text.Length, heapBuf, heapBuf.Length, out int written2);
            if (rc2 == VN_OK && written2 > 0)
            {
                return written2 == heapBuf.Length ? heapBuf : heapBuf[..written2];
            }
        }

        return new[]
        {
            new VnMarkdownInlineToken
            {
                Kind = (byte)VnMarkdownInlineKind.Text,
                TextStart = 0,
                TextLen = text.Length
            }
        };
    }
}

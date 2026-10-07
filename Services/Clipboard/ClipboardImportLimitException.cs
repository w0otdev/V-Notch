using System.IO;

namespace VNotch.Services.Clipboard;

public sealed class ClipboardImportLimitException : IOException
{
    public ClipboardImportLimitException() : base($"A clipboard import can contain at most {ClipboardHistoryStore.MaxImportItems} files and folders, including folder contents.") { }
}

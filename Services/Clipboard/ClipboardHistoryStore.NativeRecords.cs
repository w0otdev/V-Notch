namespace VNotch.Services.Clipboard;

public sealed partial class ClipboardHistoryStore
{
    private NativeClipboardRecordStore? _publicRecords;
    private NativeClipboardRecordStore PublicRecords => _publicRecords ??= new NativeClipboardRecordStore(_root);
}

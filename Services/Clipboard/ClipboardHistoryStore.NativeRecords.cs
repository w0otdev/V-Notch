namespace VNotch.Services.Clipboard;

public sealed partial class ClipboardHistoryStore
{
    private NativeClipboardRecordStore? _publicRecords;
    private readonly object _recordsGate = new();
    private NativeClipboardRecordStore PublicRecords
    {
        get { lock (_recordsGate) return _publicRecords ??= new NativeClipboardRecordStore(_root); }
    }
}

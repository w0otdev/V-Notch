using System.Windows.Interop;
using VNotch.Services;
using VNotch.Services.AiUsage;

namespace VNotch;

public partial class MainWindow
{
    private HwndSource? _aiQuotaPreviewSource;
    private uint _aiQuotaPreviewMessage;

    private void InitializeAiQuotaPreview()
    {
        if (_aiQuotaPreviewSource != null) return;
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        if (source == null) return;
        try
        {
            if (AiQuotaPreview.Register(source.Handle, out _aiQuotaPreviewMessage) != 0) return;
            _aiQuotaPreviewSource = source;
            source.AddHook(OnAiQuotaPreviewMessage);
        }
        catch (EntryPointNotFoundException)
        {
            RuntimeLog.Warn("AI-QUOTA", "Quota preview requires the updated native library.");
        }
    }

    private IntPtr OnAiQuotaPreviewMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_cleanedUp || _aiQuotaPreviewMessage == 0 || (uint)message != _aiQuotaPreviewMessage) return IntPtr.Zero;
        handled = true;
        long threshold = wParam.ToInt64();
        long provider = lParam.ToInt64();
        if (threshold is not (50 or 20) || provider is not (0 or 1)) return IntPtr.Zero;
        Dispatcher.BeginInvoke(() => PreviewAiQuotaNotification((int)threshold, provider == 1 ? "Claude" : "Codex"));
        return new IntPtr(1);
    }

    private void DisposeAiQuotaPreview()
    {
        if (_aiQuotaPreviewSource == null) return;
        _aiQuotaPreviewSource.RemoveHook(OnAiQuotaPreviewMessage);
        _ = AiQuotaPreview.Unregister(_aiQuotaPreviewSource.Handle);
        _aiQuotaPreviewSource = null;
        _aiQuotaPreviewMessage = 0;
    }
}

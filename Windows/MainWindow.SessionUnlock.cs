using Microsoft.Win32;
using System.Windows.Threading;

namespace VNotch;

public partial class MainWindow
{
    private bool _sessionFeedbackSubscribed;
    private long _lastUnlockFeedbackTick;

    private void InitializeSessionUnlockFeedback()
    {
        if (_sessionFeedbackSubscribed) return;
        SystemEvents.SessionSwitch += OnWindowsSessionSwitch;
        _sessionFeedbackSubscribed = true;
    }

    private void OnWindowsSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is not (SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLock)) return;
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
        {
            if (_cleanedUp) return;
            if (e.Reason == SessionSwitchReason.SessionLock)
            {
                _lastUnlockFeedbackTick = 0;
                CompactUnlockFeedback.Stop();
                ExpandedUnlockFeedback.Stop();
                return;
            }
            long now = Environment.TickCount64;
            if (_lastUnlockFeedbackTick != 0 && now - _lastUnlockFeedbackTick < 2000) return;
            _lastUnlockFeedbackTick = now;
            WakeFromIdle();
            CompactUnlockFeedback.Play();
            ExpandedUnlockFeedback.Play();
        }));
    }

    private void DisposeSessionUnlockFeedback()
    {
        if (_sessionFeedbackSubscribed)
        {
            SystemEvents.SessionSwitch -= OnWindowsSessionSwitch;
            _sessionFeedbackSubscribed = false;
        }
        CompactUnlockFeedback.Stop();
        ExpandedUnlockFeedback.Stop();
    }
}

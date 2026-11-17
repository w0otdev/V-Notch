using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using VNotch.Services;
using VNotch.Services.Spotlight;

namespace VNotch;

public partial class SpotlightWindow
{
    private UiScheduledTimer? _aiTranscriptExpiry;
    private SpotlightAiMessage[] _renderedAiMessages = [];
    private bool _aiTranscriptReleased;
    private double _releasedAiScrollOffset;

    private void ScheduleAiTranscriptRelease()
    {
        if (_allowClose) return;
        if (_aiTranscriptExpiry == null)
        {
            _aiTranscriptExpiry = new UiScheduledTimer { Interval = TimeSpan.FromSeconds(30) };
            _aiTranscriptExpiry.Tick += (_, _) => ReleaseHiddenAiTranscript();
        }
        _aiTranscriptExpiry.Stop();
        _aiTranscriptExpiry.Start();
    }

    private void ReleaseHiddenAiTranscript()
    {
        if (IsSpotlightOpen || _allowClose)
        {
            _aiTranscriptExpiry?.Stop();
            return;
        }
        // An in-flight request owns its live RichTextBox. Retry after it ends.
        if (_aiRequest != null) return;
        _aiTranscriptExpiry?.Stop();
        if (_aiTranscriptReleased || AiTranscript.Children.Count == 0) return;
        _releasedAiScrollOffset = AiTranscriptScroll.VerticalOffset;
        _aiTranscriptReleased = true;
        if (FocusManager.GetFocusedElement(this) is DependencyObject focused && AiTranscript.IsAncestorOf(focused))
            FocusManager.SetFocusedElement(this, SearchBox);
        AiTranscript.Children.Clear();
    }

    private void RestoreAiTranscript()
    {
        if (!_aiTranscriptReleased) return;
        double offset = _releasedAiScrollOffset;
        RenderTranscriptPage(_renderedAiMessages, _transcriptPageOffset);
        int generation = _transcriptGeneration;
        // Restore after measure, before the rebuilt page becomes an interactive view.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (generation == _transcriptGeneration && !_aiTranscriptReleased)
                AiTranscriptScroll.ScrollToVerticalOffset(offset);
        });
    }
}

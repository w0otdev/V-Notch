using VNotch.Services;
using VNotch.Services.Translation;

namespace VNotch.Controllers;

internal sealed partial class LiveTranslationController
{
    private async Task SummarizeAsync()
    {
        if (_disposed || _window is not { IsVisible: true } window) return;
        // Capture the full answer before SetStatus clears the active result.
        var input = window.GetSummaryInput();
        if (string.IsNullOrWhiteSpace(input.Text))
        {
            window.SetStatus("translation.summaryNoSelection");
            return;
        }
        CancelRequest();
        long version = _requestVersion;
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.TranslationTimeoutSeconds));
        _request = request;
        window.SetStatus("translation.summaryWorking");
        try
        {
            var service = _service;
            _idle.Start();
            var result = await Task.Run(() => service.SummarizeAsync(input, request.Token), request.Token).WaitAsync(request.Token);
            await window.WaitForEntranceAsync(request.Token);
            if (!_disposed && version == _requestVersion && window.IsVisible)
                window.ShowResult(result);
        }
        catch (OperationCanceledException)
        {
            if (!_disposed && version == _requestVersion) window.SetStatus("translation.summaryFailed");
        }
        catch (TranslationException ex)
        {
            if (!_disposed && version == _requestVersion) window.SetStatus(ex.MessageKey);
        }
        catch (Exception)
        {
            RuntimeLog.Error("SUMMARIZATION", "Summary failed; exception payload omitted for privacy.");
            if (!_disposed && version == _requestVersion) window.SetStatus("translation.summaryFailed");
        }
        finally { if (ReferenceEquals(_request, request)) _request = null; }
    }
}

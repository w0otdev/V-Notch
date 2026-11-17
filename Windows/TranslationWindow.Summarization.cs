using System.Windows;
using VNotch.Services;
using VNotch.Services.Translation;

namespace VNotch;

public partial class TranslationWindow
{
    private TranslationResult? _summarySource;
    private bool _summarySelectionTruncated;
    internal event Action? SummarizeRequested;

    internal TranslationResult GetSummaryInput()
    {
        var input = _summarySource ?? (_result is { Currency: null } result ? result : null);
        // A translation may only contain a fitted prefix. Summarize the full
        // captured original in the result language instead of summarizing that prefix.
        if (input == null || input.InputTruncated)
            input = new(OriginalText.Text, SourceLanguage, input?.TargetLanguage ?? SourceLanguage, true)
                { InputTruncated = _summarySelectionTruncated };
        return _summarySource = input with { InputTruncated = input.InputTruncated || _summarySelectionTruncated };
    }

    private void Summarize_Click(object sender, RoutedEventArgs e) => SummarizeRequested?.Invoke();

    private void LocalizeSummaryAction()
    {
        SummarizeLabel.Text = Loc.Get("translation.summarize");
        SummarizeButton.ToolTip = Loc.Get("translation.summaryHint");
        System.Windows.Automation.AutomationProperties.SetName(SummarizeButton, SummarizeLabel.Text);
        System.Windows.Automation.AutomationProperties.SetHelpText(SummarizeButton, (string)SummarizeButton.ToolTip);
        if (_result?.IsSummary == true) ShowSummaryStatus(_result);
    }

    private void ShowSummaryStatus(TranslationResult result)
    {
        LanguageSummary.Text = Loc.Get("translation.summary") +
            (result.TargetLanguage == "auto" ? "" : " · " + LanguageName(result.TargetLanguage));
        StatusText.Text = Loc.Get("translation.summaryReady");
        if (!result.WeekdaysPreserved) StatusText.Text += " " + Loc.Get("translation.checkDates");
        else if (!result.LiteralsPreserved) StatusText.Text += " " + Loc.Get("translation.checkFacts");
    }
}

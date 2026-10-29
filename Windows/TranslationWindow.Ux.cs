using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using VNotch.Services;
using VNotch.Services.Translation;

namespace VNotch;

public partial class TranslationWindow
{
    private bool _configuringCurrencies, _inputTruncated;
    internal event Action? ManageModelRequested;
    internal event Action<string?, string?>? CurrenciesChanged;
    internal bool CanReplaceResult => _selection?.CanReplace == true && _result != null && !_inputTruncated &&
        (_result.Currency != null || (_result.LiteralsPreserved && _result.WeekdaysPreserved));
    internal string OutputText
    {
        get
        {
            if (_result?.Currency is not { } conversion || CurrencyNumberOnlyCheck.IsChecked == false)
                return ResultText.Text;
            int decimals = TranslationCurrencies.DecimalPlaces(conversion.Target);
            string format = decimals == 0 ? "0" : "0." + new string('#', decimals);
            return conversion.Amount.ToString(format, CultureInfo.InvariantCulture);
        }
    }

    private void ResetSelectionUx()
    {
        SetInputTruncated(false);
        ConfigureCurrencies(false, null, null);
        CurrencyNumberOnlyCheck.IsChecked = true;
        ManageModelButton.Visibility = Visibility.Collapsed;
        UpdateReplacementHint();
    }

    internal void SetInputTruncated(bool truncated)
    {
        _inputTruncated = truncated;
        TruncatedBadge.Visibility = truncated ? Visibility.Visible : Visibility.Collapsed;
        if (truncated) ReplaceButton.IsEnabled = false;
        UpdateReplacementHint();
    }

    internal void ConfigureCurrencies(bool visible, string? source, string? target)
    {
        _configuringCurrencies = true;
        try
        {
            var choices = TranslationCurrencies.Codes.Select(code => new TranslationLanguages.Language(code, code)).ToArray();
            SourceCurrencyCombo.ItemsSource = new[] { new TranslationLanguages.Language("", Loc.Get("translation.currencyChooseSource")) }.Concat(choices);
            TargetCurrencyCombo.ItemsSource = new[] { new TranslationLanguages.Language("", Loc.Get("translation.currencyChoose")) }.Concat(choices);
            SourceCurrencyCombo.SelectedValue = source ?? "";
            TargetCurrencyCombo.SelectedValue = target ?? "";
            CurrencySelectors.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            LanguageSelectors.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { _configuringCurrencies = false; }
    }

    private void Currency_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_configuringCurrencies || SourceCurrencyCombo.SelectedValue == null || TargetCurrencyCombo.SelectedValue == null) return;
        StopResultReveal();
        _result = null; ResultText.Clear(); CopyButton.IsEnabled = ReplaceButton.IsEnabled = false;
        UpdateReplacementHint();
        CurrenciesChanged?.Invoke(TranslationCurrencies.Supported(SourceCurrencyCombo.SelectedValue as string) ? (string)SourceCurrencyCombo.SelectedValue : null,
            TranslationCurrencies.Supported(TargetCurrencyCombo.SelectedValue as string) ? (string)TargetCurrencyCombo.SelectedValue : null);
    }

    private void ManageModel_Click(object sender, RoutedEventArgs e) => ManageModelRequested?.Invoke();

    private void UpdateReplacementHint()
    {
        string fallbackKey = _selection?.CanReplace != true ? "translation.replaceReadOnly" : "translation.replaceUnavailable";
        string key = _inputTruncated ? "translation.replaceTruncated" : fallbackKey;
        ReplaceButton.ToolTip = ReplaceButton.IsEnabled ? null : Loc.Get(key);
        System.Windows.Automation.AutomationProperties.SetHelpText(ReplaceButton, ReplaceButton.ToolTip as string ?? "");
    }

    private void LocalizeSelectionUx()
    {
        ManageModelButton.Content = Loc.Get("translation.getModel");
        TruncatedBadge.Text = Loc.Get("translation.truncated");
        CurrencyNumberOnlyCheck.Content = Loc.Get("translation.currencyNumberOnly");
        CurrencyNumberOnlyCheck.ToolTip = Loc.Get("translation.currencyOutputHint");
        SourceCurrencyCombo.ToolTip = Loc.Get("translation.currencySource");
        TargetCurrencyCombo.ToolTip = Loc.Get("translation.currencyTarget");
        System.Windows.Automation.AutomationProperties.SetName(SourceCurrencyCombo, Loc.Get("translation.currencySource"));
        System.Windows.Automation.AutomationProperties.SetName(TargetCurrencyCombo, Loc.Get("translation.currencyTarget"));
        UpdateReplacementHint();
    }
}

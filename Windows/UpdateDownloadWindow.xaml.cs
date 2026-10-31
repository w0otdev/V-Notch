using System;
using System.Windows;
using VNotch.Services;

namespace VNotch;

public partial class UpdateDownloadWindow : Window
{
    public UpdateDownloadWindow()
    {
        InitializeComponent();
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Loc.GetCulture().IetfLanguageTag);
        Title = Loc.Get("update.installingTitle");
        TitleText.Text = Loc.Get("update.installingTitle");
        StatusText.Text = Loc.Get("update.preparingDownload");
    }

    public void SetStatus(string text)
    {
        StatusText.Text = text;
    }

    public void SetIndeterminate(string status)
    {
        DownloadProgressBar.IsIndeterminate = true;
        PercentText.Text = "...";
        StatusText.Text = status;
    }

    public void SetProgress(double percent)
    {
        var value = Math.Clamp(percent, 0d, 100d);
        DownloadProgressBar.IsIndeterminate = false;
        DownloadProgressBar.Value = value;
        PercentText.Text = $"{value:0}%";
    }
}

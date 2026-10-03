using System.Windows;
using System.Windows.Controls;
using VNotch.Services;
using VNotch.Windows;

namespace VNotch.Presenters;

public sealed record SettingsUpdateViewRefs(Window Owner, TextBlock Status, Button Check, Button Download);

public sealed class SettingsUpdatePresenter : IDisposable
{
    private readonly IUpdateService _service;
    private readonly Func<bool> _canUseNetwork;
    private SettingsUpdateViewRefs? _refs;
    private UpdateInfo? _availableUpdate;
    private bool _checking;
    private bool _downloading;
    private readonly CancellationTokenSource _lifetime = new();
    private UpdateDownloadWindow? _progressWindow;

    public SettingsUpdatePresenter(IUpdateService service, SettingsUpdateViewRefs refs, Func<bool>? canUseNetwork = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));
        _canUseNetwork = canUseNetwork ?? (() => NetworkPrivacy.Current.IsAllowed(NetworkFeature.Updates));
    }

    public async Task CheckForUpdatesAsync()
    {
        if (_refs == null || _checking || _downloading || !_canUseNetwork()) return;
        _checking = true;
        _refs.Status.Text = Loc.Get("settings.checkingUpdates");
        _refs.Check.IsEnabled = false;
        _refs.Download.Visibility = Visibility.Collapsed;
        try
        {
            var update = await _service.CheckForUpdatesAsync();
            if (_refs == null || !_canUseNetwork()) return;
            _availableUpdate = update;
            _refs.Status.Text = update == null ? Loc.Get("settings.checkUpdate") :
                update.IsNewerVersion ? Loc.Get("settings.updateAvailable", update.Version) : Loc.Get("settings.upToDate");
            _refs.Download.Visibility = update?.IsNewerVersion == true ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("SETTINGS", ex, "Update check failed");
            if (_refs != null) _refs.Status.Text = Loc.Get("error.updateStatus", ex.Message);
        }
        finally
        {
            _checking = false;
            if (_refs != null) _refs.Check.IsEnabled = true;
        }
    }

    public async Task DownloadUpdateAsync()
    {
        if (_refs == null || _availableUpdate is not { IsNewerVersion: true } update ||
            _checking || _downloading || !_canUseNetwork()) return;
        _downloading = true;
        _refs.Status.Text = Loc.Get("update.preparing");
        _refs.Download.IsEnabled = false;
        _refs.Check.IsEnabled = false;
        try
        {
            _progressWindow = new UpdateDownloadWindow();
            _progressWindow.SetIndeterminate(Loc.Get("update.preparing"));
            _progressWindow.Show();
            var progress = new Progress<double>(p =>
            {
                if (_refs == null || _progressWindow == null) return;
                if (p < 0)
                {
                    _progressWindow.SetIndeterminate(Loc.Get("update.downloading"));
                    _refs.Status.Text = Loc.Get("update.downloading");
                }
                else
                {
                    _refs.Status.Text = Loc.Get("update.downloadingPercent", (int)p);
                    _progressWindow.SetStatus(_refs.Status.Text);
                    _progressWindow.SetProgress(p);
                }
            });
            bool success = await _service.DownloadAndInstallUpdateAsync(update, progress, _lifetime.Token);
            if (_refs == null || success) return;
            _refs.Status.Text = Loc.Get("settings.updateAvailable", update.Version);
            MessageBox.Show(_refs.Owner, Loc.Get("error.updateFailed"), Loc.Get("error.updateFailedTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            RuntimeLog.Error("SETTINGS", ex, "Update download failed");
            if (_refs != null) _refs.Status.Text = Loc.Get("error.updateStatus", ex.Message);
        }
        finally
        {
            _progressWindow?.Close();
            _progressWindow = null;
            _downloading = false;
            if (_refs != null)
            {
                _refs.Download.IsEnabled = true;
                _refs.Check.IsEnabled = true;
            }
        }
    }

    public void ShowChangelog()
    {
        if (_refs == null) return;
        try
        {
            new ChangelogWindow(_service)
            {
                Owner = _refs.Owner, WindowStartupLocation = WindowStartupLocation.CenterOwner
            }.ShowDialog();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("SETTINGS", ex, "Failed to open changelog window");
            if (_refs != null)
                MessageBox.Show(_refs.Owner, Loc.Get("settings.changelogOpenFailed", ex.Message), Loc.Get("error.title"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void Dispose()
    {
        if (_refs == null) return;
        _refs = null;
        _availableUpdate = null;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _progressWindow?.Close();
        _progressWindow = null;
    }
}

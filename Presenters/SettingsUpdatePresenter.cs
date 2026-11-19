using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Controls;
using VNotch.Services;

namespace VNotch.Presenters;

public sealed record SettingsUpdateViewRefs(Window Owner, TextBlock Status, Button Check, Button Download);

public sealed class SettingsUpdatePresenter : IDisposable
{
    private readonly IUpdateService _service;
    private readonly bool _ownsService;
    private readonly Func<bool> _canUseNetwork;
    private readonly Func<bool> _includePrereleases;
    private SettingsUpdateViewRefs? _refs;
    private UpdateInfo? _availableUpdate;
    private bool _checking;
    private string _checkStatusKey = "tooltip.checkUpdates";
    private bool _downloading;
    private readonly CancellationTokenSource _lifetime = new();
    private UpdateDownloadWindow? _progressWindow;

    public event EventHandler<UpdateInfo?>? UpdateDetected;

    public SettingsUpdatePresenter(IUpdateService service, SettingsUpdateViewRefs refs, Func<bool>? canUseNetwork = null, bool ownsService = false, Func<bool>? includePrereleases = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _ownsService = ownsService;
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));
        _canUseNetwork = canUseNetwork ?? (() => NetworkPrivacy.Current.IsAllowed(NetworkFeature.Updates));
        _includePrereleases = includePrereleases ?? (() => false);
    }

    public async Task CheckForUpdatesAsync(bool forceRefresh = false)
    {
        if (_refs == null || _checking || _downloading) return;
        if (!_canUseNetwork())
        {
            RefreshCheckStatus(localOnly: true);
            return;
        }
        _checking = true;
        try
        {
            while (_refs != null && !_downloading && _canUseNetwork() && !_lifetime.IsCancellationRequested)
            {
                bool includePrereleases = _includePrereleases();
                _refs.Status.Text = Loc.Get("settings.checkingUpdates");
                _refs.Check.IsEnabled = false;
                _refs.Download.Visibility = Visibility.Collapsed;
                try
                {
                    var update = await _service.CheckForUpdatesAsync(includePrereleases, forceRefresh);
                    if (_refs == null || !_canUseNetwork() || _lifetime.IsCancellationRequested) return;
                    if (includePrereleases != _includePrereleases()) continue;
                    _availableUpdate = update;
                    bool newer = IsNewerUpdate(update);
                    _checkStatusKey = update == null ? "update.releaseUnavailable"
                        : newer ? "settings.updateAvailable" : "settings.upToDate";

                    _refs.Download.Visibility = newer ? Visibility.Visible : Visibility.Collapsed;
                    try { UpdateDetected?.Invoke(this, update); }
                    catch (Exception ex) { RuntimeLog.Error("SETTINGS", ex, "UpdateDetected handler threw"); }
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error("SETTINGS", ex, "Update check failed");
                    _availableUpdate = null;
                    _checkStatusKey = "update.releaseUnavailable";
                }

                if (_refs == null || includePrereleases == _includePrereleases()) break;
            }
        }
        finally
        {
            _checking = false;
            RefreshCheckStatus(localOnly: !_canUseNetwork());
        }
    }

    internal void RefreshCheckStatus(bool localOnly)
    {
        if (_refs == null || _downloading) return;
        _refs.Check.IsEnabled = !localOnly && !_checking;
        _refs.Download.IsEnabled = !localOnly;
        if (localOnly) _refs.Status.Text = Loc.Get("update.localOnly");
        else if (_checking) _refs.Status.Text = Loc.Get("settings.checkingUpdates");
        else if (_checkStatusKey == "settings.updateAvailable" && _availableUpdate != null)
            _refs.Status.Text = Loc.Get(_checkStatusKey, _availableUpdate.Version);
        else _refs.Status.Text = Loc.Get(_checkStatusKey);
    }

    public Task RefreshUpdateChannelAsync()
    {
        _availableUpdate = null;
        _checkStatusKey = "tooltip.checkUpdates";
        if (_refs != null)
        {
            _refs.Download.Visibility = Visibility.Collapsed;
            RefreshCheckStatus(localOnly: !_canUseNetwork());
        }
        return CheckForUpdatesAsync();
    }

    private bool IsNewerUpdate([NotNullWhen(true)] UpdateInfo? update) =>
        update != null && (update.IsNewerVersion || UpdateService.CompareVersions(update.Version, _service.CurrentVersion) > 0);

    public async Task DownloadUpdateAsync()
    {
        var update = _availableUpdate;
        if (_refs == null || !IsNewerUpdate(update) ||
            _checking || _downloading || !_canUseNetwork()) return;
        _downloading = true;
        _refs.Status.Text = Loc.Get("update.preparing");
        _refs.Download.IsEnabled = false;
        _refs.Check.IsEnabled = false;
        using var downloadLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        try
        {
            var dialog = new UpdateDownloadWindow();
            if (_refs.Owner.IsLoaded)
            {
                dialog.Owner = _refs.Owner;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                dialog.Topmost = _refs.Owner.Topmost;
            }
            dialog.Closed += (_, _) => downloadLifetime.Cancel();
            _progressWindow = dialog;
            _progressWindow.SetIndeterminate(Loc.Get("update.preparing"));
            _progressWindow.Show();
            var progress = new Progress<double>(p =>
            {
                if (_refs == null || _progressWindow != dialog || downloadLifetime.IsCancellationRequested) return;
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
            bool success = await _service.DownloadAndInstallUpdateAsync(update, progress, downloadLifetime.Token);
            if (_refs == null || success || downloadLifetime.IsCancellationRequested) return;
            _refs.Status.Text = Loc.Get("settings.updateAvailable", update.Version);
            // An owned topmost progress window must not cover the error dialog.
            CloseProgressWindow();
            MessageBox.Show(_refs.Owner, Loc.Get("error.updateFailed"), Loc.Get("error.updateFailedTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (OperationCanceledException) when (downloadLifetime.IsCancellationRequested) { /* Intentionally ignored. */ }
        catch (Exception ex)
        {
            RuntimeLog.Error("SETTINGS", ex, "Update download failed");
            if (_refs != null) _refs.Status.Text = Loc.Get("error.updateStatus", ex.Message);
        }
        finally
        {
            bool canceled = downloadLifetime.IsCancellationRequested;
            CloseProgressWindow();
            _downloading = false;
            if (_refs != null)
            {
                _refs.Download.IsEnabled = _canUseNetwork();
                _refs.Check.IsEnabled = _canUseNetwork();
                if (canceled && !_lifetime.IsCancellationRequested)
                    _refs.Status.Text = Loc.Get("settings.updateAvailable", update.Version);
            }
        }
    }

    private void CloseProgressWindow()
    {
        var dialog = _progressWindow;
        _progressWindow = null;
        dialog?.Close();
    }

    internal Task LoadChangelogAsync(Func<IUpdateService, Func<bool>, CancellationToken, Task> loadAsync) =>
        _refs == null ? Task.CompletedTask : loadAsync(_service, _canUseNetwork, _lifetime.Token);

    public void Dispose()
    {
        if (_refs == null) return;
        _refs = null;
        _availableUpdate = null;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _progressWindow?.Close();
        _progressWindow = null;
        if (_ownsService && _service is IDisposable disposable) disposable.Dispose();
    }
}

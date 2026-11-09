using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Models;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace VNotch.Services;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct VnAppNotificationBannerMetrics
{
    public double TargetWidth;
    public double TargetHeight;
    public double TargetCornerRadius;
    public int MorphDurationMs;
    public int ContentFadeMs;
    public int DismissDurationMs;
    public byte HasBody;
    public byte IsMultilineBody;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct VnAppNotificationFilterResult
{
    public byte Accepted;
    public ulong ContentHash;
    public int NormalizedAppLen;
    public int NormalizedTitleLen;
    public int NormalizedBodyLen;
}

public sealed partial class AppNotificationService : IDisposable
{
    private const string LogTag = "APP-NOTIFY";

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_appnotify_reset_state();

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_appnotify_seed_existing_id(uint notificationId, long creationUnixMs);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_appnotify_evaluate_and_format(
        uint notificationId,
        long creationUnixMs,
        long nowUnixMs,
        ReadOnlySpan<ushort> appIdPtr, int appIdLen,
        ReadOnlySpan<ushort> appNamePtr, int appNameLen,
        ReadOnlySpan<ushort> titlePtr, int titleLen,
        ReadOnlySpan<ushort> bodyPtr, int bodyLen,
        Span<ushort> outAppBuf, int appCap,
        Span<ushort> outTitleBuf, int titleCap,
        Span<ushort> outBodyBuf, int bodyCap,
        out VnAppNotificationFilterResult outResult);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_appnotify_compute_banner_metrics(
        double collapsedWidth,
        double collapsedHeight,
        double expandedWidth,
        double cornerRadiusCollapsed,
        double cornerRadiusExpanded,
        byte isDynamicIsland,
        byte reduceMotion,
        int configuredDurationMs,
        int titleLen,
        int bodyLen,
        byte hasNewlineInBody,
        out VnAppNotificationBannerMetrics outMetrics);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_appnotify_try_activate_app(
        ReadOnlySpan<ushort> appIdPtr, int appIdLen,
        ReadOnlySpan<ushort> appNamePtr, int appNameLen,
        out byte outActivated);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_appnotify_resolve_app_icon_source(
        ReadOnlySpan<ushort> appIdPtr, int appIdLen,
        ReadOnlySpan<ushort> appNamePtr, int appNameLen,
        Span<ushort> outPathBuf, int pathCap,
        out int outPathLen);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_appnotify_extract_initials(
        ReadOnlySpan<ushort> titlePtr, int titleLen,
        ReadOnlySpan<ushort> appNamePtr, int appNameLen,
        Span<ushort> outBuf, int bufCap,
        out int outLen);

    private readonly Func<bool> _isEnabledCheck;
    private CancellationTokenSource? _cts;
    private UserNotificationListener? _listener;
    private bool _notificationChangedSubscribed;
    private int _pollGate;
    private bool _disposed;

    public event Action<AppNotificationInfo>? NotificationReceived;

    public AppNotificationService(Func<bool>? isEnabledCheck = null)
    {
        _isEnabledCheck = isEnabledCheck ?? (() => true);
    }

    public static void ResetNativeState()
    {
        _ = vn_appnotify_reset_state();
    }

    public static void SeedExistingId(uint notificationId, long creationUnixMs)
    {
        _ = vn_appnotify_seed_existing_id(notificationId, creationUnixMs);
    }

    public static bool TryEvaluateAndFormat(
        uint notificationId,
        long creationUnixMs,
        long nowUnixMs,
        ReadOnlySpan<char> appId,
        ReadOnlySpan<char> appName,
        ReadOnlySpan<char> title,
        ReadOnlySpan<char> body,
        out string normalizedAppName,
        out string normalizedTitle,
        out string normalizedBody)
    {
        normalizedAppName = string.Empty;
        normalizedTitle = string.Empty;
        normalizedBody = string.Empty;

        Span<ushort> appBuf = stackalloc ushort[64];
        Span<ushort> titleBuf = stackalloc ushort[128];
        Span<ushort> bodyBuf = stackalloc ushort[256];

        int rc = vn_appnotify_evaluate_and_format(
            notificationId,
            creationUnixMs,
            nowUnixMs,
            MemoryMarshal.Cast<char, ushort>(appId), appId.Length,
            MemoryMarshal.Cast<char, ushort>(appName), appName.Length,
            MemoryMarshal.Cast<char, ushort>(title), title.Length,
            MemoryMarshal.Cast<char, ushort>(body), body.Length,
            appBuf, appBuf.Length,
            titleBuf, titleBuf.Length,
            bodyBuf, bodyBuf.Length,
            out var result);

        if (rc != 0 || result.Accepted == 0)
        {
            return false;
        }

        if (result.NormalizedAppLen > 0)
        {
            normalizedAppName = new string(MemoryMarshal.Cast<ushort, char>(appBuf.Slice(0, result.NormalizedAppLen)));
        }
        if (result.NormalizedTitleLen > 0)
        {
            normalizedTitle = new string(MemoryMarshal.Cast<ushort, char>(titleBuf.Slice(0, result.NormalizedTitleLen)));
        }
        if (result.NormalizedBodyLen > 0)
        {
            normalizedBody = new string(MemoryMarshal.Cast<ushort, char>(bodyBuf.Slice(0, result.NormalizedBodyLen)));
        }

        return true;
    }

    internal static VnAppNotificationBannerMetrics ComputeBannerMetrics(
        double collapsedWidth,
        double collapsedHeight,
        double expandedWidth,
        double cornerRadiusCollapsed,
        double cornerRadiusExpanded,
        bool isDynamicIsland,
        bool reduceMotion,
        int configuredDurationMs,
        string? title,
        string? body)
    {
        string safeTitle = title ?? string.Empty;
        string safeBody = body ?? string.Empty;
        byte hasNewline = safeBody.IndexOf('\n') >= 0 ? (byte)1 : (byte)0;

        if (vn_appnotify_compute_banner_metrics(
                collapsedWidth,
                collapsedHeight,
                expandedWidth,
                cornerRadiusCollapsed,
                cornerRadiusExpanded,
                isDynamicIsland ? (byte)1 : (byte)0,
                reduceMotion ? (byte)1 : (byte)0,
                configuredDurationMs,
                safeTitle.Length,
                safeBody.Length,
                hasNewline,
                out var metrics) == 0)
        {
            return metrics;
        }

        return new VnAppNotificationBannerMetrics
        {
            TargetWidth = 384.0,
            TargetHeight = safeBody.Length > 0 ? 92.0 : 76.0,
            TargetCornerRadius = isDynamicIsland ? 22.0 : 18.0,
            MorphDurationMs = reduceMotion ? 120 : 380,
            ContentFadeMs = reduceMotion ? 90 : 240,
            DismissDurationMs = Math.Clamp(configuredDurationMs > 0 ? configuredDurationMs : 5000, 2000, 15000),
            HasBody = safeBody.Length > 0 ? (byte)1 : (byte)0,
            IsMultilineBody = hasNewline
        };
    }

    public static bool TryActivateApp(string? appId, string? appName)
    {
        ReadOnlySpan<char> idSpan = (appId ?? string.Empty).AsSpan();
        ReadOnlySpan<char> nameSpan = (appName ?? string.Empty).AsSpan();
        int rc = vn_appnotify_try_activate_app(
            MemoryMarshal.Cast<char, ushort>(idSpan), idSpan.Length,
            MemoryMarshal.Cast<char, ushort>(nameSpan), nameSpan.Length,
            out byte activated);
        return rc == 0 && activated != 0;
    }

    public static string? TryResolveAppIconSourcePath(string? appId, string? appName)
    {
        ReadOnlySpan<char> idSpan = (appId ?? string.Empty).AsSpan();
        ReadOnlySpan<char> nameSpan = (appName ?? string.Empty).AsSpan();
        Span<ushort> pathBuf = stackalloc ushort[260];

        int rc = vn_appnotify_resolve_app_icon_source(
            MemoryMarshal.Cast<char, ushort>(idSpan), idSpan.Length,
            MemoryMarshal.Cast<char, ushort>(nameSpan), nameSpan.Length,
            pathBuf, pathBuf.Length,
            out int pathLen);

        if (rc != 0 || pathLen <= 0)
        {
            return null;
        }

        return new string(MemoryMarshal.Cast<ushort, char>(pathBuf.Slice(0, pathLen)));
    }

    public static ImageSource? TryResolveAppIcon(string? appId, string? appName)
    {
        string? resolvedPath = TryResolveAppIconSourcePath(appId, appName);
        if (string.IsNullOrEmpty(resolvedPath))
        {
            return null;
        }

        return TryLoadAppIconSource(resolvedPath);
    }

    public static string ExtractInitials(string? title, string? appName)
    {
        ReadOnlySpan<char> titleSpan = (title ?? string.Empty).AsSpan();
        ReadOnlySpan<char> appSpan = (appName ?? string.Empty).AsSpan();
        Span<ushort> buf = stackalloc ushort[8];

        int rc = vn_appnotify_extract_initials(
            MemoryMarshal.Cast<char, ushort>(titleSpan), titleSpan.Length,
            MemoryMarshal.Cast<char, ushort>(appSpan), appSpan.Length,
            buf, buf.Length,
            out int len);

        if (rc != 0 || len <= 0)
        {
            return "VN";
        }

        return new string(MemoryMarshal.Cast<ushort, char>(buf.Slice(0, len)));
    }

    public void Start()
    {
        if (_disposed || _cts != null) return;
        _cts = new CancellationTokenSource();
        _ = RunListenerLoopAsync(_cts.Token);
    }

    private async Task RunListenerLoopAsync(CancellationToken ct)
    {
        try
        {
            var listener = UserNotificationListener.Current;
            var access = await listener.RequestAccessAsync();
            if (access != UserNotificationListenerAccessStatus.Allowed || ct.IsCancellationRequested)
            {
                RuntimeLog.Log(LogTag, $"UserNotificationListener access status: {access}");
                return;
            }

            _listener = listener;
            await SeedInitialNotificationsAsync(listener, ct);

            try
            {
                listener.NotificationChanged += OnListenerNotificationChanged;
                _notificationChangedSubscribed = true;
            }
            catch
            {
                // Unpackaged desktop sessions may not support COM push subscription; polling loop handles it.
            }

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1100));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!_isEnabledCheck()) continue;
                await PollNotificationsAsync(listener, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogTag, $"Notification listener stopped: {ex.Message}");
        }
    }

    private void OnListenerNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        if (_disposed || args.ChangeKind != UserNotificationChangedKind.Added || !_isEnabledCheck())
        {
            return;
        }

        var token = _cts?.Token ?? CancellationToken.None;
        _ = PollNotificationsAsync(sender, token);
    }

    private static async Task SeedInitialNotificationsAsync(UserNotificationListener listener, CancellationToken ct)
    {
        try
        {
            var existing = await listener.GetNotificationsAsync(NotificationKinds.Toast);
            if (existing == null || ct.IsCancellationRequested) return;

            for (int i = 0; i < existing.Count; i++)
            {
                var item = existing[i];
                SeedExistingId(item.Id, item.CreationTime.ToUnixTimeMilliseconds());
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogTag, $"Initial notification seed failed: {ex.Message}");
        }
    }

    private async Task PollNotificationsAsync(UserNotificationListener listener, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _pollGate, 1) != 0) return;
        try
        {
            var list = await listener.GetNotificationsAsync(NotificationKinds.Toast);
            if (list == null || list.Count == 0 || ct.IsCancellationRequested) return;

            long nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            for (int i = 0; i < list.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                var userNotif = list[i];
                var info = await TryExtractNotificationAsync(userNotif, nowUnixMs).ConfigureAwait(false);
                if (info != null)
                {
                    NotificationReceived?.Invoke(info);
                }
            }
        }
        catch
        {
            // Transient WinRT notification center error; ignore and retry next tick.
        }
        finally
        {
            Interlocked.Exchange(ref _pollGate, 0);
        }
    }

    private static async Task<AppNotificationInfo?> TryExtractNotificationAsync(UserNotification userNotif, long nowUnixMs)
    {
        uint id = userNotif.Id;
        long creationMs = userNotif.CreationTime.ToUnixTimeMilliseconds();

        string appId = string.Empty;
        string appName = string.Empty;
        global::Windows.ApplicationModel.AppDisplayInfo? displayInfo = null;
        try
        {
            var appInfo = userNotif.AppInfo;
            appId = appInfo?.AppUserModelId ?? string.Empty;
            displayInfo = appInfo?.DisplayInfo;
            appName = displayInfo?.DisplayName ?? string.Empty;
        }
        catch
        {
            // Some system toast sources omit AppInfo metadata.
        }

        string rawTitle = string.Empty;
        string rawBody = string.Empty;
        try
        {
            var binding = userNotif.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            if (binding != null)
            {
                var texts = binding.GetTextElements();
                if (texts != null && texts.Count > 0)
                {
                    rawTitle = texts[0]?.Text ?? string.Empty;
                    if (texts.Count == 2)
                    {
                        rawBody = texts[1]?.Text ?? string.Empty;
                    }
                    else if (texts.Count > 2)
                    {
                        string line1 = texts[1]?.Text ?? string.Empty;
                        string line2 = texts[2]?.Text ?? string.Empty;
                        rawBody = string.IsNullOrEmpty(line1) ? line2 : (string.IsNullOrEmpty(line2) ? line1 : string.Concat(line1, "\n", line2));
                    }
                }
            }
        }
        catch
        {
            return null;
        }

        if (!TryEvaluateAndFormat(
                id,
                creationMs,
                nowUnixMs,
                appId,
                appName,
                rawTitle,
                rawBody,
                out string normApp,
                out string normTitle,
                out string normBody))
        {
            return null;
        }

        ImageSource? icon = await TryLoadAppLogoAsync(displayInfo).ConfigureAwait(false)
            ?? TryResolveAppIcon(appId, normApp);

        return new AppNotificationInfo
        {
            Id = id,
            AppId = appId,
            AppName = normApp,
            Title = normTitle,
            Body = normBody,
            AppIcon = icon,
            CreationTime = userNotif.CreationTime
        };
    }

    internal static async Task<ImageSource?> TryLoadAppLogoAsync(global::Windows.ApplicationModel.AppDisplayInfo? displayInfo)
    {
        try
        {
            if (displayInfo == null) return null;

            var streamRef = displayInfo.GetLogo(new global::Windows.Foundation.Size(64, 64));
            if (streamRef == null) return null;

            using var winrtStream = await streamRef.OpenReadAsync();
            using var netStream = winrtStream.AsStreamForRead();
            using var ms = new MemoryStream();
            await netStream.CopyToAsync(ms).ConfigureAwait(false);
            ms.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = AppIconDecodeSize;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return NormalizeAppIcon(bmp);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var cts = _cts;
        _cts = null;
        try { cts?.Cancel(); } catch { }
        cts?.Dispose();

        if (_listener != null && _notificationChangedSubscribed)
        {
            try { _listener.NotificationChanged -= OnListenerNotificationChanged; } catch { }
            _notificationChangedSubscribed = false;
        }
        _listener = null;
    }
}

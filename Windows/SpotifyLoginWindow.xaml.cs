using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using VNotch.Services;

namespace VNotch;

public partial class SpotifyLoginWindow : Window
{
    private const string LogCategory = "SPOTIFY-LOGIN";
#pragma warning disable S1075 // Spotify login and cookie endpoints
    private const string SpotifyLoginUrl = "https://accounts.spotify.com/login?continue=https%3A%2F%2Fopen.spotify.com%2F";
    private const string SpotifyCookieUrl = "https://open.spotify.com/";
#pragma warning restore S1075

    private const double ShellMarginDip = 18;
    private const double ShellCornerRadiusDip = 24;

    private readonly DispatcherTimer _cookieTimer;
    private bool _cookieCheckInProgress;
    private string? _userDataFolder;

    public string SpotifySpDc { get; private set; } = "";

    public SpotifyLoginWindow()
    {
        InitializeComponent();

        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Loc.GetCulture().IetfLanguageTag);
        Title = Loc.Get("spotifyLogin.title");
        HeadingText.Text = Loc.Get("spotifyLogin.heading");
        HintText.Text = Loc.Get("spotifyLogin.hint");
        StatusText.Text = Loc.Get("spotifyLogin.opening");
        CancelButton.Content = Loc.Get("spotifyLogin.cancel");

        _cookieTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _cookieTimer.Tick += CookieTimer_Tick;
        Loaded += SpotifyLoginWindow_Loaded;
        SizeChanged += (_, _) => ApplyRoundedWindowRegion();
        DpiChanged += (_, _) => ApplyRoundedWindowRegion();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyRoundedWindowRegion();
    }

    private void ApplyRoundedWindowRegion()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        double widthDip = ActualWidth > 0 ? ActualWidth : Width;
        double heightDip = ActualHeight > 0 ? ActualHeight : Height;
        uint dpi = Win32Interop.GetDpiForWindow(hwnd);
        double dpiScale = dpi > 0 ? dpi / 96.0 : 1.0;

        var regionBounds = CalculateRoundedWindowRegion(widthDip, heightDip, dpiScale);
        IntPtr region = CreateRoundRectRgn(
            regionBounds.Left,
            regionBounds.Top,
            regionBounds.Right,
            regionBounds.Bottom,
            regionBounds.CornerDiameter,
            regionBounds.CornerDiameter);
        if (region == IntPtr.Zero)
            return;

        // SetWindowRgn owns the region after success. On failure we must release it.
        if (SetWindowRgn(hwnd, region, true) == 0)
            Win32Interop.DeleteObject(region);
    }

    internal static RoundedWindowRegion CalculateRoundedWindowRegion(
        double widthDip,
        double heightDip,
        double dpiScale)
    {
        dpiScale = dpiScale > 0 && double.IsFinite(dpiScale) ? dpiScale : 1.0;

        int left = (int)Math.Round(ShellMarginDip * dpiScale);
        int top = left;
        int right = Math.Max(left + 1,
            (int)Math.Round((Math.Max(widthDip, ShellMarginDip * 2 + 1) - ShellMarginDip) * dpiScale));
        int bottom = Math.Max(top + 1,
            (int)Math.Round((Math.Max(heightDip, ShellMarginDip * 2 + 1) - ShellMarginDip) * dpiScale));
        int cornerDiameter = Math.Max(1, (int)Math.Round(ShellCornerRadiusDip * 2 * dpiScale));

        return new RoundedWindowRegion(left, top, right, bottom, cornerDiameter);
    }

    internal readonly record struct RoundedWindowRegion(
        int Left,
        int Top,
        int Right,
        int Bottom,
        int CornerDiameter);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(
        int x1,
        int y1,
        int x2,
        int y2,
        int widthEllipse,
        int heightEllipse);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    private async void SpotifyLoginWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= SpotifyLoginWindow_Loaded;
        PlayEntranceAnimation();
        try
        {
            _userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "V-Notch",
                "SpotifyWebView2",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_userDataFolder);

            CoreWebView2Environment environment =
                await CoreWebView2Environment.CreateAsync(userDataFolder: _userDataFolder);
            await SpotifyWebView.EnsureCoreWebView2Async(environment);

            SpotifyWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            SpotifyWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            SpotifyWebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            SpotifyWebView.CoreWebView2.Navigate(SpotifyLoginUrl);
            _cookieTimer.Start();
            await CheckForSpotifySessionAsync();
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogCategory, $"Unable to initialize Spotify login: {ex.Message}");
            StatusText.Text = Loc.Get("spotifyLogin.failed");
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
        }
    }

    private async void CoreWebView2_NavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        await CheckForSpotifySessionAsync();
    }

    private async void CookieTimer_Tick(object? sender, EventArgs e)
    {
        await CheckForSpotifySessionAsync();
    }

    private async Task CheckForSpotifySessionAsync()
    {
        if (_cookieCheckInProgress || SpotifyWebView.CoreWebView2 == null)
            return;

        _cookieCheckInProgress = true;
        try
        {
            IReadOnlyList<CoreWebView2Cookie> cookies =
                await SpotifyWebView.CoreWebView2.CookieManager.GetCookiesAsync(SpotifyCookieUrl);
            CoreWebView2Cookie? session = cookies.FirstOrDefault(cookie =>
                cookie.Name.Equals("sp_dc", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(cookie.Value));
            if (session == null)
                return;

            SpotifySpDc = session.Value;
            _cookieTimer.Stop();
            StatusText.Text = Loc.Get("spotifyLogin.connected");
            CloseWithAnimation(true);
        }
        catch (Exception ex)
        {
            RuntimeLog.Debug(LogCategory, () => $"Spotify cookie check failed: {ex.Message}");
        }
        finally
        {
            _cookieCheckInProgress = false;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CloseWithAnimation(false);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        try
        {
            _cookieTimer.Stop();
            if (SpotifyWebView.CoreWebView2 != null)
            {
                SpotifyWebView.CoreWebView2.NavigationCompleted -= CoreWebView2_NavigationCompleted;
                try
                {
                    SpotifyWebView.CoreWebView2.CookieManager.DeleteAllCookies();
                }
                catch (Exception ex)
                {
                    RuntimeLog.Debug(LogCategory, () => $"Failed to delete cookies: {ex.Message}");
                }
            }
            SpotifyWebView.Dispose();
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogCategory, $"Error during WebView2 disposal: {ex.Message}");
        }

        if (_userDataFolder != null)
            DeleteTemporaryProfileAsync(_userDataFolder).SafeFireAndForget("SPOTIFY-LOGIN-CLEANUP");
    }

    private static async Task DeleteTemporaryProfileAsync(string userDataFolder)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(userDataFolder))
                    Directory.Delete(userDataFolder, recursive: true);
                return;
            }
            catch (IOException)
            {
                await Task.Delay(200);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(200);
            }
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void WindowSurface_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (IsInteractiveElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        DragMove();
    }

    private static bool IsInteractiveElement(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.ScrollBar)
            {
                return true;
            }
            if (source.GetType().FullName == "Microsoft.Web.WebView2.Wpf.WebView2")
            {
                return true;
            }
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private bool _isClosing;
    private bool? _dialogResultToSet;

    private void PlayEntranceAnimation()
    {
        var easeOut = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };
        int fps = VNotch.Services.AnimationConfig.TargetFps;
        var duration = TimeSpan.FromMilliseconds(450);

        var fade = new DoubleAnimation(0.0, 1.0, duration) { EasingFunction = easeOut };
        Timeline.SetDesiredFrameRate(fade, fps);
        MainShell.BeginAnimation(OpacityProperty, fade);

        var scaleX = new DoubleAnimation(0.95, 1.0, duration) { EasingFunction = easeOut };
        Timeline.SetDesiredFrameRate(scaleX, fps);
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);

        var scaleY = new DoubleAnimation(0.95, 1.0, duration) { EasingFunction = easeOut };
        Timeline.SetDesiredFrameRate(scaleY, fps);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);

        var translateY = new DoubleAnimation(8.0, 0.0, duration) { EasingFunction = easeOut };
        Timeline.SetDesiredFrameRate(translateY, fps);
        ShellTranslate.BeginAnimation(TranslateTransform.YProperty, translateY);
    }

    private void CloseWithAnimation(bool? result)
    {
        if (_isClosing) return;
        _isClosing = true;
        _dialogResultToSet = result;

        var easeIn = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 6 };
        int fps = VNotch.Services.AnimationConfig.TargetFps;
        var duration = TimeSpan.FromMilliseconds(400);

        var fade = new DoubleAnimation(MainShell.Opacity, 0.0, duration) { EasingFunction = easeIn };
        Timeline.SetDesiredFrameRate(fade, fps);

        fade.Completed += (s, e) =>
        {
            RuntimeLog.Debug(LogCategory, () => "Exit animation completed. Setting DialogResult.");
            try
            {
                if (_dialogResultToSet == true)
                {
                    DialogResult = true;
                }
                else if (_dialogResultToSet == false)
                {
                    DialogResult = false;
                }
                else
                {
                    Close();
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn(LogCategory, $"Error during animated close: {ex.Message}");
                Close();
            }
        };

        MainShell.BeginAnimation(OpacityProperty, fade);

        var scaleX = new DoubleAnimation(ShellScale.ScaleX, 0.95, duration) { EasingFunction = easeIn };
        Timeline.SetDesiredFrameRate(scaleX, fps);
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);

        var scaleY = new DoubleAnimation(ShellScale.ScaleY, 0.95, duration) { EasingFunction = easeIn };
        Timeline.SetDesiredFrameRate(scaleY, fps);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);

        var translateY = new DoubleAnimation(ShellTranslate.Y, 8.0, duration) { EasingFunction = easeIn };
        Timeline.SetDesiredFrameRate(translateY, fps);
        ShellTranslate.BeginAnimation(TranslateTransform.YProperty, translateY);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_isClosing)
        {
            e.Cancel = true;
            CloseWithAnimation(DialogResult);
        }
        else
        {
            base.OnClosing(e);
        }
    }
}


using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using VNotch.Services;
using IOPath = System.IO.Path;

namespace VNotch;

internal interface ISetupEntryAwarePage
{
    void OnPageEntryCompleted();
}

internal interface ISetupAnimatedPage
{
    IReadOnlyList<UIElement> GetAnimatedElements();
}

public partial class SetupWindow : Window
{
    private const int SwShow = 5;

    private enum NavigationDirection
    {
        Forward,
        Backward
    }

    private int _currentPageIndex = 0;
    private readonly Func<UIElement>[] _pageFactories;
    private readonly IntroductionPage _introductionPage;
    private readonly DirectoryPage _directoryPage;
    private readonly StartupOptionsPage _startupOptionsPage;
    private readonly InstallProgressPage _installProgressPage;
    private readonly FinishPage _finishPage;
    private readonly CancelSetupPage _cancelSetupPage;
    private readonly LanguagePage _languagePage;
    private readonly string _sourceDirectory;
    private bool _isWelcomePage = true;
    private bool _isTransitioning;
    private bool _isInstalling;
    private bool _installationSucceeded;
    private bool _isShowingCancelSetupPage;
    private bool _hasForcedForeground;
    public int ResultExitCode { get; private set; } = 1;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public SetupWindow(string? sourceDirectory = null)
    {
        InitializeComponent();
        AnimationPrimitives.ApplyFpsToTree(this);

        _sourceDirectory = sourceDirectory ?? AppContext.BaseDirectory;
        _introductionPage = new IntroductionPage();
        _languagePage = new LanguagePage("en");
        _directoryPage = new DirectoryPage(SetupOperations.GetDefaultInstallDirectory());
        _startupOptionsPage = new StartupOptionsPage(startWithWindows: true);
        _installProgressPage = new InstallProgressPage();
        _finishPage = new FinishPage(launchAfterInstall: true);
        _cancelSetupPage = new CancelSetupPage();

        _pageFactories = new Func<UIElement>[]
        {
            () => _languagePage,
            () => null!,
            () => _introductionPage,
            () => _directoryPage,
            () => _startupOptionsPage,
            () => _installProgressPage,
            () => _finishPage
        };

        _languagePage.LanguageChanged += OnSetupLanguageChanged;
        ApplyLocalizationToSetupUi();
    }

    private void OnSetupLanguageChanged(string lang)
    {
        Loc.SetLanguage(lang);
        ApplyLocalizationToSetupUi();

        if (_currentPageIndex >= 0 && _currentPageIndex < _pageFactories.Length)
        {
            var page = _pageFactories[_currentPageIndex]();
            if (page is LanguagePage langPage) langPage.RefreshLocalization();
            else if (page is IntroductionPage intro) intro.RefreshLocalization();
            else if (page is DirectoryPage dir) dir.RefreshLocalization();
            else if (page is StartupOptionsPage startup) startup.RefreshLocalization();
            else if (page is InstallProgressPage progress) progress.RefreshLocalization();
            else if (page is FinishPage finish) finish.RefreshLocalization();
        }

        if (_cancelSetupPage != null)
        {
            _cancelSetupPage.RefreshLocalization();
        }
    }

    private void ApplyLocalizationToSetupUi()
    {
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Loc.GetCulture().IetfLanguageTag);
        Title = Loc.Get("setup.windowTitle");
        SetupAssistantText.Text = Loc.Get("setup.assistant");
        SetupTaglineText.Text = Loc.Get("setup.tagline");
        TooltipHelper.SetLocalizedTooltip(CloseSetupButton, "tooltip.close");
        HeadlineText.Text = Loc.Get("setup.welcome.headline");

        Step1Text.Text = Loc.Get("setup.step.language");
        Step2Text.Text = Loc.Get("setup.step.welcome");
        Step3Text.Text = Loc.Get("setup.step.about");
        Step4Text.Text = Loc.Get("setup.step.location");
        Step5Text.Text = Loc.Get("setup.step.startup");
        Step6Text.Text = Loc.Get("setup.step.install");
        Step7Text.Text = Loc.Get("setup.step.finish");

        if (!_isShowingCancelSetupPage)
        {
            NextButton.Content = _currentPageIndex == _pageFactories.Length - 1
                ? Loc.Get("setup.btn.finish")
                : Loc.Get("setup.btn.continue");
            BackButton.Content = Loc.Get("setup.btn.back");
            CancelButton.Content = Loc.Get("setup.btn.cancel");
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        AnimationPrimitives.ApplyFpsToTree(this);
        var storyboard = AnimationPrimitives.WithFps((Storyboard)FindResource("WindowEntranceStoryboard"));
        storyboard.Begin();
        ForceWindowToFront();

        IconContainer.Visibility = Visibility.Collapsed;
        HeadlineText.Visibility = Visibility.Collapsed;
        BodyText.Visibility = Visibility.Collapsed;
        _isWelcomePage = false;
        _currentPageIndex = 0;

        ConfigurePageScrolling(_languagePage);
        ContentPresenter.Content = _languagePage;
        ContentPresenter.Visibility = Visibility.Visible;
        ContentPresenter.Opacity = 1;
        UpdateStepIndicators(0);
        UpdateNavigationButtons(0);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        ForceWindowToFront();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        MainBorder.CornerRadius = new CornerRadius(ActualWidth < 580 ? 18 : 24);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isInstalling)
        {
            MessageBox.Show(
                Loc.Get("setup.install.inProgress"),
                Loc.Get("setup.windowTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (_installationSucceeded)
        {
            ResultExitCode = 0;
            Close();
            return;
        }

        if (_isShowingCancelSetupPage)
        {
            Close();
            return;
        }

        ShowCancelSetupPage();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isTransitioning)
        {
            return;
        }

        if (_isShowingCancelSetupPage)
        {
            HideCancelSetupPage();
            return;
        }

        if (_currentPageIndex > 0)
        {
            ShowPage(_currentPageIndex - 1, NavigationDirection.Backward);
        }
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isTransitioning)
        {
            return;
        }

        if (_isShowingCancelSetupPage)
        {
            Close();
            return;
        }

        if (!CommitCurrentStep())
        {
            return;
        }

        if (_currentPageIndex < _pageFactories.Length - 1)
        {
            ShowPage(_currentPageIndex + 1, NavigationDirection.Forward);
        }
        else
        {
            if (_installationSucceeded && _finishPage.LaunchAfterInstall)
            {
                SetupOperations.LaunchInstalledApp(_directoryPage.InstallPath);
            }

            ResultExitCode = 0;
            Close();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CloseButton_Click(sender, e);
    }

    private void ShowPage(int index, NavigationDirection direction)
    {
        if (index < 0 || index >= _pageFactories.Length)
        {
            return;
        }

        if (_isTransitioning)
        {
            return;
        }

        _isTransitioning = true;
        SetNavigationEnabled(false);

        AnimateCurrentViewOut(direction, () =>
        {
            try
            {
                _currentPageIndex = index;

                if (index == 1)
                {
                    ShowWelcomePage(direction);
                }
                else
                {
                    ShowDynamicPage(index, direction);
                }

                UpdateStepIndicators(index);
                UpdateNavigationButtons(index);
            }
            catch (Exception ex)
            {
                CompleteTransition();
                MessageBox.Show(
                    Loc.Get("setup.pageLoadFailed", ex.Message),
                    Loc.Get("setup.windowTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        });
    }

    private void ShowWelcomePage(NavigationDirection direction)
    {
        PageScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        PageScrollViewer.ScrollToHome();
        ResetContentPresenter();
        ContentPresenter.Content = null;
        ContentPresenter.Visibility = Visibility.Collapsed;

        _isWelcomePage = true;

        HeadlineText.Text = Loc.Get("setup.welcome.headline");
        BodyText.Inlines.Clear();
        BodyText.Text = Loc.Get("setup.welcome.body");

        ResetElementForEntry(IconContainer, GetWelcomeOffset(direction));
        ResetElementForEntry(HeadlineText, GetWelcomeOffset(direction));
        ResetElementForEntry(BodyText, GetWelcomeOffset(direction));

        AnimateWelcomeIn(direction, CompleteTransition);
        AnimateButtonPanel();
    }

    private void ShowDynamicPage(int index, NavigationDirection direction)
    {
        var page = _pageFactories[index]();

        if (page is LanguagePage langPage) langPage.RefreshLocalization();
        else if (page is IntroductionPage intro) intro.RefreshLocalization();
        else if (page is DirectoryPage dir) dir.RefreshLocalization();
        else if (page is StartupOptionsPage startup) startup.RefreshLocalization();
        else if (page is InstallProgressPage progress) progress.RefreshLocalization();
        else if (page is FinishPage finish) finish.RefreshLocalization();

        ShowDynamicContent(page, direction, () =>
        {
            CompleteTransition();

            if (index == _pageFactories.Length - 2)
            {
                BeginInstallationAsync();
            }
        });
    }

    private void UpdateNavigationButtons(int index)
    {
        BackButton.Content = Loc.Get("setup.btn.back");
        CancelButton.Content = Loc.Get("setup.btn.cancel");

        if (_isShowingCancelSetupPage)
        {
            BackButton.Content = Loc.Get("setup.btn.keepSetup");
            NextButton.Content = Loc.Get("setup.btn.cancelSetup");
            BackButton.Visibility = Visibility.Visible;
            CancelButton.Visibility = Visibility.Collapsed;
            NextButton.Visibility = Visibility.Visible;
            return;
        }

        var showBack = index > 0 && index < _pageFactories.Length - 1;
        var showCancel = index < _pageFactories.Length - 1;
        var showNext = true;

        if (index == _pageFactories.Length - 2)
        {
            showBack = false;
            showCancel = false;
            showNext = false;
        }
        else if (index == _pageFactories.Length - 1)
        {
            NextButton.Content = Loc.Get("setup.btn.finish");
        }
        else if (index == _pageFactories.Length - 3)
        {
            NextButton.Content = Loc.Get("setup.btn.continue");
        }
        else
        {
            NextButton.Content = Loc.Get("setup.btn.continue");
        }

        BackButton.Visibility = showBack ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = showNext ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AnimateCurrentViewOut(NavigationDirection direction, Action onComplete)
    {
        ResetButtonPanelForEntry();

        if (_isWelcomePage)
        {
            AnimateWelcomeOut(direction, () =>
            {
                HideWelcomeElements();
                onComplete?.Invoke();
            });

            return;
        }

        if (ContentPresenter.Visibility != Visibility.Visible || ContentPresenter.Content == null)
        {
            ResetContentPresenter();
            onComplete?.Invoke();
            return;
        }

        AnimatePageOut(direction, () =>
        {
            ResetContentPresenter();
            onComplete?.Invoke();
        });
    }

    private void AnimatePageOut(NavigationDirection direction, Action onComplete)
    {
        var offset = direction == NavigationDirection.Forward ? -24 : 24;
        var slideOut = new DoubleAnimation
        {
            From = 0,
            To = offset,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        var fadeOut = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        var transform = EnsureTranslateTransform(ContentPresenter);
        StopAnimations(ContentPresenter);

        fadeOut.Completed += (s, e) =>
        {
            StopAnimations(ContentPresenter);
            transform.Y = offset;
            ContentPresenter.Opacity = 0;
            onComplete?.Invoke();
        };

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(slideOut, VNotch.Services.AnimationConfig.TargetFps);
        transform.BeginAnimation(TranslateTransform.YProperty, slideOut);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeOut, VNotch.Services.AnimationConfig.TargetFps);
        ContentPresenter.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void AnimatePageIn(NavigationDirection direction, Action? onComplete = null)
    {
        var offset = direction == NavigationDirection.Forward ? 24 : -24;
        var slideIn = new DoubleAnimation
        {
            From = offset,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(240),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(240),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var transform = EnsureTranslateTransform(ContentPresenter);
        StopAnimations(ContentPresenter);
        transform.Y = offset;
        ContentPresenter.Opacity = 0;

        fadeIn.Completed += (s, e) =>
        {
            StopAnimations(ContentPresenter);
            transform.Y = 0;
            ContentPresenter.Opacity = 1;
            onComplete?.Invoke();
        };

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(slideIn, VNotch.Services.AnimationConfig.TargetFps);
        transform.BeginAnimation(TranslateTransform.YProperty, slideIn);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeIn, VNotch.Services.AnimationConfig.TargetFps);
        ContentPresenter.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void AnimateDynamicPageIn(NavigationDirection direction, Action? onComplete = null)
    {
        var animatedElements = GetAnimatedElementsForCurrentPage();
        if (animatedElements.Count == 0)
        {
            AnimatePageIn(direction, onComplete);
            return;
        }

        StopAnimations(ContentPresenter);
        EnsureTranslateTransform(ContentPresenter).Y = 0;
        ContentPresenter.Opacity = 1;

        var offset = direction == NavigationDirection.Forward ? 12 : -12;
        var lastIndex = animatedElements.Count - 1;

        for (int i = 0; i < animatedElements.Count; i++)
        {
            var element = animatedElements[i];
            ResetElementForEntry(element, offset);

            Action? completion = null;
            if (i == lastIndex)
            {
                completion = () =>
                {
                    if (ContentPresenter.Content is ISetupEntryAwarePage entryAwarePage)
                    {
                        entryAwarePage.OnPageEntryCompleted();
                    }

                    onComplete?.Invoke();
                };
            }

            AnimateContentIn(element, i * 55, offset, completion);
        }
    }

    private void AnimateWelcomeIn(NavigationDirection direction, Action? onComplete = null)
    {
        var offset = GetWelcomeOffset(direction);
        AnimateContentIn(IconContainer, 0, offset);
        AnimateContentIn(HeadlineText, 50, offset);
        AnimateContentIn(BodyText, 100, offset, onComplete);
    }

    private void AnimateWelcomeOut(NavigationDirection direction, Action? onComplete = null)
    {
        var offset = direction == NavigationDirection.Forward ? -8 : 8;
        AnimateContentOut(IconContainer, offset);
        AnimateContentOut(HeadlineText, offset);
        AnimateContentOut(BodyText, offset, onComplete);
    }

    private void AnimateContentIn(UIElement element, int delay, double offsetY, Action? onComplete = null)
    {
        var slideIn = new DoubleAnimation
        {
            From = offsetY,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(260),
            BeginTime = TimeSpan.FromMilliseconds(delay),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(260),
            BeginTime = TimeSpan.FromMilliseconds(delay),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var transform = EnsureTranslateTransform(element);
        StopAnimations(element);
        transform.Y = offsetY;
        element.Opacity = 0;
        element.Visibility = Visibility.Visible;

        fadeIn.Completed += (s, e) =>
        {
            StopAnimations(element);
            transform.Y = 0;
            element.Opacity = 1;
            onComplete?.Invoke();
        };

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(slideIn, VNotch.Services.AnimationConfig.TargetFps);
        transform.BeginAnimation(TranslateTransform.YProperty, slideIn);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeIn, VNotch.Services.AnimationConfig.TargetFps);
        element.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void AnimateContentOut(UIElement element, double offsetY, Action? onComplete = null)
    {
        var slideOut = new DoubleAnimation
        {
            From = 0,
            To = offsetY,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        var fadeOut = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        var transform = EnsureTranslateTransform(element);
        StopAnimations(element);

        fadeOut.Completed += (s, e) =>
        {
            StopAnimations(element);
            transform.Y = offsetY;
            element.Opacity = 0;
            element.Visibility = Visibility.Collapsed;
            onComplete?.Invoke();
        };

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(slideOut, VNotch.Services.AnimationConfig.TargetFps);
        transform.BeginAnimation(TranslateTransform.YProperty, slideOut);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeOut, VNotch.Services.AnimationConfig.TargetFps);
        element.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void AnimateButtonPanel()
    {
        ResetButtonPanelForEntry();

        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 3 }
        };

        var slideIn = new DoubleAnimation
        {
            From = 8,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeIn, VNotch.Services.AnimationConfig.TargetFps);
        ButtonPanel.BeginAnimation(OpacityProperty, fadeIn);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(slideIn, VNotch.Services.AnimationConfig.TargetFps);
        ButtonTranslate.BeginAnimation(TranslateTransform.YProperty, slideIn);
    }

    private void ShowCancelSetupPage()
    {
        if (_isTransitioning || _isShowingCancelSetupPage)
        {
            return;
        }

        _isTransitioning = true;
        SetNavigationEnabled(false);

        _cancelSetupPage.RefreshLocalization();

        AnimateCurrentViewOut(NavigationDirection.Forward, () =>
        {
            _isShowingCancelSetupPage = true;
            ShowDynamicContent(_cancelSetupPage, NavigationDirection.Forward, CompleteTransition);
            UpdateNavigationButtons(_currentPageIndex);
        });
    }

    private void HideCancelSetupPage()
    {
        if (_isTransitioning || !_isShowingCancelSetupPage)
        {
            return;
        }

        _isTransitioning = true;
        SetNavigationEnabled(false);

        AnimateCurrentViewOut(NavigationDirection.Backward, () =>
        {
            _isShowingCancelSetupPage = false;

            if (_currentPageIndex == 1)
            {
                ShowWelcomePage(NavigationDirection.Backward);
            }
            else
            {
                ShowDynamicPage(_currentPageIndex, NavigationDirection.Backward);
            }

            UpdateStepIndicators(_currentPageIndex);
            UpdateNavigationButtons(_currentPageIndex);
        });
    }

    private void ShowDynamicContent(UIElement content, NavigationDirection direction, Action? onComplete = null)
    {
        HideWelcomeElements();

        ConfigurePageScrolling(content);
        ResetContentPresenter();
        ContentPresenter.Content = content;
        ContentPresenter.Visibility = Visibility.Visible;
        _isWelcomePage = false;

        AnimateDynamicPageIn(direction, onComplete);
        AnimateButtonPanel();
    }

    private void ConfigurePageScrolling(UIElement content)
    {
        // LanguagePage owns its scroll viewport so its header stays visible and
        // the seventh option remains reachable at every window size/DPI scale.
        PageScrollViewer.VerticalScrollBarVisibility = content is LanguagePage
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;
        PageScrollViewer.ScrollToHome();
    }

    private List<UIElement> GetAnimatedElementsForCurrentPage()
    {
        if (ContentPresenter.Content is ISetupAnimatedPage animatedPage)
        {
            return new List<UIElement>(animatedPage.GetAnimatedElements());
        }

        var elements = new List<UIElement>();

        if (ContentPresenter.Content is Panel panel)
        {
            foreach (UIElement child in panel.Children)
            {
                elements.Add(child);
            }

            return elements;
        }

        if (ContentPresenter.Content is Decorator decorator && decorator.Child != null)
        {
            elements.Add(decorator.Child);
            return elements;
        }

        if (ContentPresenter.Content is UIElement element)
        {
            elements.Add(element);
        }

        return elements;
    }

    private void ResetContentPresenter()
    {
        StopAnimations(ContentPresenter);
        var transform = EnsureTranslateTransform(ContentPresenter);
        transform.Y = 0;
        ContentPresenter.Opacity = 0;
    }

    private void HideWelcomeElements()
    {
        HideAndResetElement(IconContainer);
        HideAndResetElement(HeadlineText);
        HideAndResetElement(BodyText);
    }

    private void HideAndResetElement(UIElement element)
    {
        StopAnimations(element);
        var transform = EnsureTranslateTransform(element);
        transform.Y = 0;
        element.Opacity = 0;
        element.Visibility = Visibility.Collapsed;
    }

    private void ResetElementForEntry(UIElement element, double offsetY)
    {
        StopAnimations(element);
        var transform = EnsureTranslateTransform(element);
        transform.Y = offsetY;
        element.Opacity = 0;
        element.Visibility = Visibility.Visible;
    }

    private void ResetButtonPanelForEntry()
    {
        ButtonPanel.BeginAnimation(OpacityProperty, null);
        ButtonTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        ButtonPanel.Opacity = 0;
        ButtonTranslate.Y = 8;
    }

    private TranslateTransform EnsureTranslateTransform(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform translateTransform)
        {
            return translateTransform;
        }

        var newTransform = new TranslateTransform();
        element.RenderTransform = newTransform;
        return newTransform;
    }

    private void StopAnimations(UIElement element)
    {
        element.BeginAnimation(OpacityProperty, null);
        EnsureTranslateTransform(element).BeginAnimation(TranslateTransform.YProperty, null);
    }

    private static double GetWelcomeOffset(NavigationDirection direction)
    {
        return direction == NavigationDirection.Forward ? 8 : -8;
    }

    private void SetNavigationEnabled(bool isEnabled)
    {
        BackButton.IsEnabled = isEnabled;
        CancelButton.IsEnabled = isEnabled;
        NextButton.IsEnabled = isEnabled;
    }

    private void CompleteTransition()
    {
        _isTransitioning = false;
        SetNavigationEnabled(true);
    }

    private void UpdateStepIndicators(int currentStep)
    {
        var steps = new[] { Step1Text, Step2Text, Step3Text, Step4Text, Step5Text, Step6Text, Step7Text };

        for (int i = 0; i < steps.Length; i++)
        {
            if (i == currentStep)
            {
                steps[i].Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(255, 255, 255, 255));
                steps[i].FontWeight = FontWeights.Bold;
            }
            else if (i < currentStep)
            {
                steps[i].Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(153, 255, 255, 255));
                steps[i].FontWeight = FontWeights.Bold;
            }
            else
            {
                steps[i].Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(136, 255, 255, 255));
                steps[i].FontWeight = FontWeights.Bold;
            }
        }
    }

    public void AutoAdvanceToFinish()
    {
        if (_currentPageIndex == _pageFactories.Length - 2)
        {
            ShowPage(_currentPageIndex + 1, NavigationDirection.Forward);
        }
    }

    private void ForceWindowToFront()
    {
        if (_hasForcedForeground)
        {
            return;
        }

        _hasForcedForeground = true;
        BringToFront();

        var followUpTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        followUpTimer.Tick += (_, _) =>
        {
            followUpTimer.Stop();
            BringToFront();
        };
        followUpTimer.Start();
    }

    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
        Focus();

        Topmost = true;
        Topmost = false;

        if (ContentPresenter.IsVisible)
        {
            ContentPresenter.Focus();
        }

        var windowHandle = new WindowInteropHelper(this).Handle;
        if (windowHandle != IntPtr.Zero)
        {
            ShowWindow(windowHandle, SwShow);
            SetForegroundWindow(windowHandle);
        }
    }

    private bool CommitCurrentStep()
    {
        if (_currentPageIndex == 2)
        {
            return ValidateInstallDirectory();
        }

        return true;
    }

    private bool ValidateInstallDirectory()
    {
        var installPath = _directoryPage.InstallPath;
        if (string.IsNullOrWhiteSpace(installPath))
        {
            MessageBox.Show(
                Loc.Get("setup.directory.required"),
                Loc.Get("setup.windowTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        try
        {
            _directoryPage.InstallPath = IOPath.GetFullPath(installPath);

            if (SetupOperations.RequiresAdministratorForInstallPath(_directoryPage.InstallPath) &&
                !SetupOperations.IsRunningAsAdministrator())
            {
                MessageBox.Show(
                    Loc.Get("setup.directory.adminRequired"),
                    Loc.Get("setup.windowTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Loc.Get("setup.directory.invalid", ex.Message),
                Loc.Get("setup.windowTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
    }

    private async void BeginInstallationAsync()
    {
        if (_isInstalling || _installationSucceeded)
        {
            return;
        }

        _isInstalling = true;
        _installProgressPage.ShowIndeterminate(Loc.Get("setup.install.preparing"));

        try
        {
            var installOptions = new SetupInstallOptions(
                _sourceDirectory,
                _directoryPage.InstallPath,
                _startupOptionsPage.StartWithWindows,
                _languagePage.SelectedLanguage);

            await SetupOperations.InstallAsync(installOptions, progress =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (progress.IsIndeterminate)
                    {
                        _installProgressPage.ShowIndeterminate(progress.StatusText);
                    }
                    else
                    {
                        _installProgressPage.SetProgress(
                            progress.CurrentStep,
                            progress.TotalSteps,
                            progress.StatusText);
                    }
                });
            });

            _isInstalling = false;
            _installationSucceeded = true;
            _installProgressPage.SetProgress(1, 1, Loc.Get("setup.install.complete"));
            AutoAdvanceToFinish();
        }
        catch (Exception ex)
        {
            _isInstalling = false;
            _installProgressPage.ShowFailure(ex.Message);
            BackButton.Visibility = Visibility.Visible;
            CancelButton.Visibility = Visibility.Visible;
            NextButton.Visibility = Visibility.Collapsed;
            SetNavigationEnabled(true);
        }
    }
}

public class IntroductionPage : UserControl, ISetupAnimatedPage
{
    private readonly Border _eyebrow;
    private readonly TextBlock _headline;
    private readonly TextBlock _lead;
    private readonly Border _projectCard;
    private readonly Border _sourceCard;
    private readonly TextBlock _eyebrowText;
    private readonly TextBlock _projectTitle;
    private readonly TextBlock _projectBody;
    private readonly TextBlock _sourceTitle;
    private readonly TextBlock _sourceBody;

    public IntroductionPage()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _eyebrow = CreateEyebrow(Loc.Get("setup.intro.eyebrow"), out _eyebrowText);
        Grid.SetRow(_eyebrow, 0);
        grid.Children.Add(_eyebrow);

        _headline = new TextBlock
        {
            Text = Loc.Get("setup.intro.headline"),
            FontSize = 28,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("pack://application:,,,/Fonts/#SF Pro Display"),
            Margin = new Thickness(0, 0, 0, 12),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(_headline, 1);
        grid.Children.Add(_headline);

        _lead = new TextBlock
        {
            Text = Loc.Get("setup.intro.lead"),
            FontSize = 14,
            LineHeight = 21,
            Foreground = new SolidColorBrush(Color.FromArgb(204, 255, 255, 255)),
            FontFamily = new FontFamily("pack://application:,,,/Fonts/#SF Pro Display"),
            Margin = new Thickness(0, 0, 0, 18),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(_lead, 2);
        grid.Children.Add(_lead);

        _projectCard = CreateInfoCard(Loc.Get("setup.intro.projectTitle"), Loc.Get("setup.intro.projectBody"), out _projectTitle, out _projectBody);
        Grid.SetRow(_projectCard, 3);
        grid.Children.Add(_projectCard);

        _sourceCard = CreateInfoCard(Loc.Get("setup.intro.sourceTitle"), Loc.Get("setup.intro.sourceBody"), out _sourceTitle, out _sourceBody);
        Grid.SetRow(_sourceCard, 4);
        grid.Children.Add(_sourceCard);

        Content = grid;
    }

    public void RefreshLocalization()
    {
        _eyebrowText.Text = Loc.Get("setup.intro.eyebrow");
        _headline.Text = Loc.Get("setup.intro.headline");
        _lead.Text = Loc.Get("setup.intro.lead");
        _projectTitle.Text = Loc.Get("setup.intro.projectTitle");
        _projectBody.Text = Loc.Get("setup.intro.projectBody");
        _sourceTitle.Text = Loc.Get("setup.intro.sourceTitle");
        _sourceBody.Text = Loc.Get("setup.intro.sourceBody");
    }

    public IReadOnlyList<UIElement> GetAnimatedElements()
    {
        return new UIElement[] { _eyebrow, _headline, _lead, _projectCard, _sourceCard };
    }

    private static Border CreateEyebrow(string text, out TextBlock textBlock)
    {
        textBlock = new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromArgb(224, 255, 255, 255)),
            FontFamily = new FontFamily("pack://application:,,,/Fonts/#SF Pro Display")
        };
        return new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 18),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = textBlock
        };
    }

    private static Border CreateInfoCard(string title, string body, out TextBlock titleBlock, out TextBlock bodyBlock)
    {
        var stack = new StackPanel();
        titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("pack://application:,,,/Fonts/#SF Pro Display"),
            Margin = new Thickness(0, 0, 0, 7)
        };
        stack.Children.Add(titleBlock);

        bodyBlock = new TextBlock
        {
            Text = body,
            FontSize = 13,
            LineHeight = 20,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromArgb(196, 255, 255, 255)),
            FontFamily = new FontFamily("pack://application:,,,/Fonts/#SF Pro Display")
        };
        stack.Children.Add(bodyBlock);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 18, 18, 22)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(0, 0, 0, 10),
            Child = stack
        };
    }
}

public class DirectoryPage : UserControl, ISetupAnimatedPage
{
    private TextBox? _pathBox;
    private readonly TextBlock _headline;
    private readonly TextBlock _description;
    private readonly Border _container;
    private readonly TextBlock _browseText;

    public DirectoryPage(string initialInstallPath)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _headline = new TextBlock
        {
            Text = Loc.Get("setup.directory.headline"),
            FontSize = 28,
            FontWeight = FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.White,
            FontFamily = new System.Windows.Media.FontFamily("pack://application:,,,/Fonts/#SF Pro Display"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(_headline, 0);
        grid.Children.Add(_headline);

        _description = new TextBlock
        {
            Text = Loc.Get("setup.directory.description"),
            FontSize = 14,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(204, 255, 255, 255)),
            FontFamily = new System.Windows.Media.FontFamily("pack://application:,,,/Fonts/#SF Pro Display"),
            Margin = new Thickness(0, 0, 0, 24)
        };
        Grid.SetRow(_description, 1);
        grid.Children.Add(_description);

        _container = new Border
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 26, 26, 26)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 60, 60, 60)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10)
        };

        var pathPanel = new Grid();
        pathPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        pathPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

        var folderIcon = new TextBlock
        {
            Text = "\uE8B7",
            FontSize = 16,
            FontFamily = new System.Windows.Media.FontFamily("pack://application:,,,/Fonts/#Segoe MDL2 Assets, Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Symbol, Segoe UI"),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(204, 255, 255, 255)),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(folderIcon, 0);
        pathPanel.Children.Add(folderIcon);

        _pathBox = new TextBox
        {
            Text = initialInstallPath,
            FontSize = 13,
            FontFamily = new System.Windows.Media.FontFamily("pack://application:,,,/Fonts/#SF Pro Display"),
            Background = System.Windows.Media.Brushes.Transparent,
            Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0)
        };
        Grid.SetColumn(_pathBox, 1);
        pathPanel.Children.Add(_pathBox);

        var browseButton = new Button
        {
            Height = 32,
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(12, 0, 0, 0),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            FontFamily = new System.Windows.Media.FontFamily("pack://application:,,,/Fonts/#SF Pro Display"),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 10, 122, 255)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand
        };

        _browseText = new TextBlock
        {
            Text = Loc.Get("setup.directory.browse"),
            VerticalAlignment = VerticalAlignment.Center
        };
        browseButton.Content = _browseText;
        browseButton.Click += BrowseButton_Click;

        var buttonTemplate = new ControlTemplate(typeof(Button));
        var buttonBorder = new FrameworkElementFactory(typeof(Border));
        buttonBorder.Name = "border";
        buttonBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        buttonBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        buttonBorder.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
        var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
        contentPresenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        contentPresenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        buttonBorder.AppendChild(contentPresenter);
        buttonTemplate.VisualTree = buttonBorder;
        browseButton.Template = buttonTemplate;

        Grid.SetColumn(browseButton, 2);
        pathPanel.Children.Add(browseButton);

        _container.Child = pathPanel;
        Grid.SetRow(_container, 2);
        grid.Children.Add(_container);

        Content = grid;
    }

    public void RefreshLocalization()
    {
        _headline.Text = Loc.Get("setup.directory.headline");
        _description.Text = Loc.Get("setup.directory.description");
        _browseText.Text = Loc.Get("setup.directory.browse");
    }

    public IReadOnlyList<UIElement> GetAnimatedElements()
    {
        return new UIElement[] { _headline, _description, _container };
    }

    public string InstallPath
    {
        get => _pathBox?.Text ?? string.Empty;
        set { if (_pathBox != null) _pathBox.Text = value; }
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.Get("setup.directory.headline"),
            FileName = "V-Notch",
            Filter = "Folder|*.none",
            CheckFileExists = false,
            CheckPathExists = false
        };

        if (dialog.ShowDialog() == true)
        {
            if (_pathBox != null)
            {
                var selectedPath = System.IO.Path.GetDirectoryName(dialog.FileName);
                if (string.IsNullOrEmpty(selectedPath))
                {
                    selectedPath = System.IO.Path.GetPathRoot(dialog.FileName);
                }

                if (!string.IsNullOrEmpty(selectedPath))
                {
                    var folderName = System.IO.Path.GetFileName(selectedPath.TrimEnd('\\', '/'));
                    if (!string.Equals(folderName, "V-Notch", StringComparison.OrdinalIgnoreCase))
                    {
                        selectedPath = System.IO.Path.Combine(selectedPath, "V-Notch");
                    }
                    _pathBox.Text = selectedPath;
                }
            }
        }
    }
}

public class StartupOptionsPage : UserControl, ISetupAnimatedPage
{
    private readonly TextBlock _headline;
    private readonly TextBlock _description;
    private readonly CheckBox _checkbox;

    public StartupOptionsPage(bool startWithWindows)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _headline = new TextBlock
        {
            Text = Loc.Get("setup.startup.headline"),
            FontSize = 28,
            FontWeight = FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.White,
            FontFamily = new System.Windows.Media.FontFamily("pack://application:,,,/Fonts/#SF Pro Display"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(_headline, 0);
        grid.Children.Add(_headline);

        _description = new TextBlock
        {
            Text = Loc.Get("setup.startup.description"),
            FontSize = 14,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(204, 255, 255, 255)),
            FontFamily = new System.Windows.Media.FontFamily("pack://application:,,,/Fonts/#SF Pro Display"),
            Margin = new Thickness(0, 0, 0, 32)
        };
        Grid.SetRow(_description, 1);
        grid.Children.Add(_description);

        _checkbox = new CheckBox
        {
            Content = Loc.Get("setup.startup.checkbox"),
            IsChecked = startWithWindows,
            FontSize = 14,
            Foreground = System.Windows.Media.Brushes.White,
            FontFamily = new System.Windows.Media.FontFamily("pack://application:,,,/Fonts/#SF Pro Display")
        };
        Grid.SetRow(_checkbox, 2);
        grid.Children.Add(_checkbox);

        Content = grid;
    }

    public void RefreshLocalization()
    {
        _headline.Text = Loc.Get("setup.startup.headline");
        _description.Text = Loc.Get("setup.startup.description");
        _checkbox.Content = Loc.Get("setup.startup.checkbox");
    }

    public IReadOnlyList<UIElement> GetAnimatedElements()
    {
        return new UIElement[] { _headline, _description, _checkbox };
    }

    public bool StartWithWindows => _checkbox.IsChecked == true;
}

public class CancelSetupPage : UserControl, ISetupAnimatedPage
{
    private readonly TextBlock _headline;
    private readonly TextBlock _description;
    private readonly Border _warningCard;
    private readonly TextBlock _warningTitle;
    private readonly TextBlock _warningBody;

    public CancelSetupPage()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _headline = new TextBlock
        {
            Text = Loc.Get("setup.cancel.headline"),
            FontSize = 28,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("pack://application:,,,/Fonts/#SF Pro Display"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(_headline, 0);
        grid.Children.Add(_headline);

        _description = new TextBlock
        {
            Text = Loc.Get("setup.cancel.description"),
            FontSize = 14,
            LineHeight = 22,
            Foreground = new SolidColorBrush(Color.FromArgb(204, 255, 255, 255)),
            FontFamily = new FontFamily("pack://application:,,,/Fonts/#SF Pro Display"),
            Margin = new Thickness(0, 0, 0, 24),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(_description, 1);
        grid.Children.Add(_description);

        _warningCard = CreateWarningCard(out _warningTitle, out _warningBody);
        Grid.SetRow(_warningCard, 2);
        grid.Children.Add(_warningCard);

        Content = grid;
    }
    public void RefreshLocalization()
    {
        _headline.Text = Loc.Get("setup.cancel.headline");
        _description.Text = Loc.Get("setup.cancel.description");
        _warningTitle.Text = Loc.Get("setup.cancel.warningTitle");
        _warningBody.Text = Loc.Get("setup.cancel.warningBody");
    }

    public IReadOnlyList<UIElement> GetAnimatedElements()
    {
        return new UIElement[] { _headline, _description, _warningCard };
    }

    private static Border CreateWarningCard(out TextBlock titleBlock, out TextBlock bodyBlock)
    {
        var stack = new StackPanel();
        titleBlock = new TextBlock
        {
            Text = Loc.Get("setup.cancel.warningTitle"),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("pack://application:,,,/Fonts/#SF Pro Display"),
            Margin = new Thickness(0, 0, 0, 6),
            TextWrapping = TextWrapping.Wrap
        };
        stack.Children.Add(titleBlock);

        bodyBlock = new TextBlock
        {
            Text = Loc.Get("setup.cancel.warningBody"),
            FontSize = 13,
            LineHeight = 20,
            Foreground = new SolidColorBrush(Color.FromArgb(196, 255, 255, 255)),
            FontFamily = new FontFamily("pack://application:,,,/Fonts/#SF Pro Display"),
            TextWrapping = TextWrapping.Wrap
        };
        stack.Children.Add(bodyBlock);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 18, 18, 22)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 14, 16, 14),
            Child = stack
        };
    }
}

public class InstallProgressPage : UserControl, ISetupAnimatedPage
{
    private readonly TextBlock _headline;
    private readonly TextBlock _status;
    private readonly ProgressBar _progressBar;

    public InstallProgressPage()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _headline = new TextBlock
        {
            Text = "Installing V-Notch",
            FontSize = 28,
            FontWeight = FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.White,
            FontFamily = new System.Windows.Media.FontFamily("pack://application:,,,/Fonts/#SF Pro Display"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(_headline, 0);
        grid.Children.Add(_headline);

        _status = new TextBlock
        {
            Text = "Copying files...",
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(204, 255, 255, 255)),
            FontFamily = new System.Windows.Media.FontFamily("pack://application:,,,/Fonts/#SF Pro Display"),
            MaxWidth = 430,
            Margin = new Thickness(0, 0, 0, 32)
        };
        Grid.SetRow(_status, 1);
        grid.Children.Add(_status);

        _progressBar = new ProgressBar
        {
            Height = 8,
            IsIndeterminate = true,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 26, 26, 26)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 48, 209, 88)),
            BorderThickness = new Thickness(0)
        };
        Grid.SetRow(_progressBar, 2);
        grid.Children.Add(_progressBar);

        Content = grid;
    }

    public void RefreshLocalization()
    {
        _headline.Text = Loc.Get("setup.install.headline");
        _status.Text = Loc.Get("setup.install.copying");
    }

    public IReadOnlyList<UIElement> GetAnimatedElements()
    {
        return new UIElement[] { _headline, _status, _progressBar };
    }

    public void ShowIndeterminate(string status)
    {
        _status.Text = status;
        _progressBar.IsIndeterminate = true;
        _progressBar.Value = 0;
        _progressBar.Foreground = new SolidColorBrush(Color.FromArgb(255, 48, 209, 88));
    }

    public void SetProgress(int current, int total, string status)
    {
        _status.Text = status;
        _progressBar.IsIndeterminate = false;
        _progressBar.Minimum = 0;
        _progressBar.Maximum = Math.Max(1, total);
        _progressBar.Value = Math.Min(Math.Max(0, current), _progressBar.Maximum);
        _progressBar.Foreground = new SolidColorBrush(Color.FromArgb(255, 48, 209, 88));
    }

    public void ShowFailure(string errorMessage)
    {
        _status.Text = Loc.Get("setup.install.failed", errorMessage);
        _progressBar.IsIndeterminate = false;
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 1;
        _progressBar.Value = 0;
        _progressBar.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 69, 58));
    }
}

public class FinishPage : UserControl, ISetupEntryAwarePage, ISetupAnimatedPage
{
    private readonly Grid _icon;
    private readonly Ellipse _glow;
    private readonly System.Windows.Shapes.Path _checkPath;
    private readonly double _checkDashUnits;
    private readonly TextBlock _headline;
    private readonly TextBlock _description;
    private readonly CheckBox _checkbox;

    public FinishPage(bool launchAfterInstall)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _icon = new Grid
        {
            Width = 84,
            Height = 84,
            Margin = new Thickness(0, 0, 0, 24),
            HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };

        _glow = new Ellipse
        {
            Width = 84,
            Height = 84,
            Fill = new RadialGradientBrush(
                Color.FromArgb(90, 48, 209, 88),
                Color.FromArgb(0, 48, 209, 88)),
            Opacity = 0
        };
        _icon.Children.Add(_glow);

        var badge = new Ellipse
        {
            Width = 72,
            Height = 72,
            Fill = new LinearGradientBrush(
                Color.FromArgb(255, 16, 34, 22),
                Color.FromArgb(255, 12, 23, 16),
                90),
            Stroke = new SolidColorBrush(Color.FromArgb(48, 48, 209, 88)),
            StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _icon.Children.Add(badge);

        var ring = new Ellipse
        {
            Width = 62,
            Height = 62,
            Stroke = new SolidColorBrush(Color.FromArgb(32, 255, 255, 255)),
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _icon.Children.Add(ring);

        var checkContainer = new Viewbox
        {
            Width = 44,
            Height = 34,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
            Stretch = Stretch.Uniform
        };

        _checkPath = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 4 18 L 16 30 L 40 4"),
            Stroke = new SolidColorBrush(Color.FromArgb(255, 48, 209, 88)),
            StrokeThickness = 5.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.Fill
        };

        var checkPathLength = CalculatePathLength(_checkPath.Data);
        _checkDashUnits = checkPathLength / _checkPath.StrokeThickness;
        _checkPath.StrokeDashArray = new DoubleCollection { _checkDashUnits, _checkDashUnits };
        _checkPath.StrokeDashOffset = _checkDashUnits;

        checkContainer.Child = _checkPath;
        _icon.Children.Add(checkContainer);

        Grid.SetRow(_icon, 0);
        grid.Children.Add(_icon);

        _headline = new TextBlock
        {
            Text = Loc.Get("setup.finish.headline"),
            FontSize = 28,
            FontWeight = FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.White,
            FontFamily = new System.Windows.Media.FontFamily("pack://application:,,,/Fonts/#SF Pro Display"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(_headline, 1);
        grid.Children.Add(_headline);

        _description = new TextBlock
        {
            Text = Loc.Get("setup.finish.description"),
            FontSize = 14,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(204, 255, 255, 255)),
            FontFamily = new System.Windows.Media.FontFamily("pack://application:,,,/Fonts/#SF Pro Display"),
            LineHeight = 22,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 24)
        };
        Grid.SetRow(_description, 2);
        grid.Children.Add(_description);

        _checkbox = new CheckBox
        {
            Content = Loc.Get("setup.finish.launch"),
            IsChecked = launchAfterInstall,
            FontSize = 14,
            Foreground = System.Windows.Media.Brushes.White,
            FontFamily = new System.Windows.Media.FontFamily("pack://application:,,,/Fonts/#SF Pro Display")
        };
        Grid.SetRow(_checkbox, 3);
        grid.Children.Add(_checkbox);

        Content = grid;
    }

    public void RefreshLocalization()
    {
        _headline.Text = Loc.Get("setup.finish.headline");
        _description.Text = Loc.Get("setup.finish.description");
        _checkbox.Content = Loc.Get("setup.finish.launch");
    }

    public IReadOnlyList<UIElement> GetAnimatedElements()
    {
        return new UIElement[] { _icon, _headline, _description, _checkbox };
    }

    public bool LaunchAfterInstall => _checkbox.IsChecked == true;

    public void OnPageEntryCompleted()
    {
        StartFinishIconAnimation(_glow, _checkPath, _checkDashUnits);
    }

    private static void StartFinishIconAnimation(Ellipse glow, System.Windows.Shapes.Path checkPath, double dashUnits)
    {
        glow.BeginAnimation(OpacityProperty, null);
        checkPath.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        glow.Opacity = 0;
        checkPath.StrokeDashOffset = dashUnits;

        var glowFade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            BeginTime = TimeSpan.FromMilliseconds(40),
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var drawCheck = new DoubleAnimation
        {
            From = dashUnits,
            To = 0,
            BeginTime = TimeSpan.FromMilliseconds(10),
            Duration = TimeSpan.FromMilliseconds(520),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(glowFade, VNotch.Services.AnimationConfig.TargetFps);
        glow.BeginAnimation(OpacityProperty, glowFade);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(drawCheck, VNotch.Services.AnimationConfig.TargetFps);
        checkPath.BeginAnimation(Shape.StrokeDashOffsetProperty, drawCheck);
    }

    private static double CalculatePathLength(Geometry geometry)
    {
        var pathGeometry = geometry.GetFlattenedPathGeometry();
        double length = 0;

        foreach (var figure in pathGeometry.Figures)
        {
            var currentPoint = figure.StartPoint;

            foreach (var segment in figure.Segments)
            {
                if (segment is PolyLineSegment polyLineSegment)
                {
                    foreach (var point in polyLineSegment.Points)
                    {
                        length += (point - currentPoint).Length;
                        currentPoint = point;
                    }
                }
                else if (segment is LineSegment lineSegment)
                {
                    length += (lineSegment.Point - currentPoint).Length;
                    currentPoint = lineSegment.Point;
                }
            }
        }

        return length;
    }
}

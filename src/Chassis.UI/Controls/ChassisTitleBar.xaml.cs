using Chassis.Core.Versioning;
using Chassis.UI.Theme;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;

namespace Chassis.UI.Controls;

/// <summary>
/// Custom 40px WinUI 3 Extended Titlebar providing dynamic title/version badge, quick Feedback & Help actions,
/// and DPI-scaled drag hit-testing across the central spacer.
/// </summary>
public sealed partial class ChassisTitleBar : UserControl
{
    private readonly DispatcherQueueTimer _subtitleHideTimer;

    private AppWindow? _appWindow;
    private ThemeManager? _theme;
    private RectInt32 _lastDragRect;
    private string _feedbackTooltip = string.Empty;
    private bool _hasSubtitle;
    private bool _isSubtitleVisible;
    private bool _isSubtitleAnimating;
    private double _subtitleExpandedWidth;
    private Func<string?>? _nextSubtitleProvider;
    private Storyboard? _subtitleStoryboard;
    private bool _hasLiveStatus;

    public ChassisTitleBar()
    {
        InitializeComponent();

        _subtitleHideTimer = DispatcherQueue.CreateTimer();
        _subtitleHideTimer.Interval = TimeSpan.FromSeconds(5);
        _subtitleHideTimer.IsRepeating = false;
        _subtitleHideTimer.Tick += (_, _) => CollapseSubtitleAnimated();

        SizeChanged += (_, _) =>
        {
            UpdateResponsiveBadge();
            UpdateDragRectangles();
        };
        LayoutUpdated += (_, _) => UpdateDragRectangles();
        Unloaded += (_, _) =>
        {
            _subtitleHideTimer.Stop();
            _subtitleStoryboard?.Stop();
        };
    }

    public event EventHandler? ChangelogRequested;
    public event EventHandler? FeedbackRequested;
    public event EventHandler? HelpRequested;

    public void Configure(
        AppWindow appWindow,
        ThemeManager theme,
        bool hasUnreadChangelog,
        string feedbackTooltip,
        string helpTooltip,
        string versionTooltip)
    {
        _appWindow = appWindow;
        _theme = theme;

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        }

        TitleBlock.Text = AppVersion.ProductTitle;
        UpdateLabels(feedbackTooltip, helpTooltip, versionTooltip);
        SetUnreadChangelogState(hasUnreadChangelog);

        _theme.BindInteractiveButton(VersionBadgeButton, () => false);
        _theme.BindInteractiveButton(FeedbackButton, () => false);
        _theme.BindInteractiveButton(HelpButton, () => false);

        ApplyThemeVisuals();
        UpdateResponsiveBadge();
        UpdateDragRectangles();
    }

    public void UpdateLabels(string feedbackTooltip, string helpTooltip, string versionTooltip)
    {
        _feedbackTooltip = feedbackTooltip;
        ApplyFeedbackTooltip();
        AutomationProperties.SetName(FeedbackButton, feedbackTooltip);

        ToolTipService.SetToolTip(HelpButton, helpTooltip);
        AutomationProperties.SetName(HelpButton, helpTooltip);

        ToolTipService.SetToolTip(VersionBadgeButton, versionTooltip);
        AutomationProperties.SetName(VersionBadgeButton, versionTooltip);
    }

    public void SetFeedbackEnabled(bool isEnabled)
    {
        FeedbackButton.IsEnabled = isEnabled;
        ApplyFeedbackTooltip();
        _theme?.RefreshInteractiveButton(FeedbackButton);
    }

    private void ApplyFeedbackTooltip()
    {
        if (FeedbackButton.IsEnabled)
        {
            ToolTipService.SetToolTip(FeedbackTooltipHost, null);
            ToolTipService.SetToolTip(FeedbackButton, _feedbackTooltip);
        }
        else
        {
            // Disabled WinUI buttons do not receive hover events, so host the tooltip on the enabled wrapper.
            ToolTipService.SetToolTip(FeedbackButton, null);
            ToolTipService.SetToolTip(FeedbackTooltipHost, _feedbackTooltip);
        }
    }

    public void SetSubtitle(string? subtitle, Func<string?>? nextSubtitleProvider = null)
    {
        _subtitleStoryboard?.Stop();
        _subtitleHideTimer.Stop();
        _isSubtitleAnimating = false;
        _nextSubtitleProvider = nextSubtitleProvider;

        if (string.IsNullOrWhiteSpace(subtitle))
        {
            _hasSubtitle = false;
            _isSubtitleVisible = false;
            _subtitleExpandedWidth = 0;
            SubtitleBlock.Text = string.Empty;
            SubtitleHost.Visibility = Visibility.Collapsed;
        }
        else
        {
            _hasSubtitle = true;
            _subtitleExpandedWidth = MeasureSubtitleWidth(subtitle);
            SubtitleTranslate.X = 0;

            var canShow = ActualWidth <= 0 || ActualWidth >= 760;
            _isSubtitleVisible = canShow;
            if (canShow)
            {
                SubtitleHost.Width = _subtitleExpandedWidth;
                SubtitleHost.Opacity = 1;
                SubtitleBlock.Opacity = 1;
                SubtitleHost.Visibility = Visibility.Visible;
                _subtitleHideTimer.Start();
            }
            else
            {
                SubtitleHost.Visibility = Visibility.Collapsed;
            }
        }

        UpdateResponsiveBadge();
    }

    private double MeasureSubtitleWidth(string subtitle)
    {
        SubtitleBlock.Text = $"— {subtitle.Trim()}";
        SubtitleBlock.Measure(new Size(double.PositiveInfinity, 40));
        return Math.Ceiling(SubtitleBlock.DesiredSize.Width) + 8;
    }

    private void OnBrandClicked()
    {
        if (_isSubtitleAnimating || !_hasSubtitle || (ActualWidth > 0 && ActualWidth < 760))
        {
            return;
        }

        var nextText = _nextSubtitleProvider?.Invoke();
        if (_isSubtitleVisible && SubtitleHost.Visibility == Visibility.Visible)
        {
            if (!string.IsNullOrWhiteSpace(nextText))
            {
                RotateSubtitleWhileOpenAnimated(nextText);
            }
            else
            {
                _subtitleHideTimer.Stop();
                _subtitleHideTimer.Start();
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(nextText))
        {
            _subtitleExpandedWidth = MeasureSubtitleWidth(nextText);
        }

        ExpandSubtitleAnimated();
    }

    private void ExpandSubtitleAnimated()
    {
        if (_isSubtitleAnimating || !_hasSubtitle || (ActualWidth > 0 && ActualWidth < 760))
        {
            return;
        }

        _subtitleHideTimer.Stop();
        _isSubtitleAnimating = true;
        _isSubtitleVisible = true;

        SubtitleHost.Width = 0;
        SubtitleHost.Opacity = 0;
        SubtitleBlock.Opacity = 1;
        SubtitleTranslate.X = -10;
        SubtitleHost.Visibility = Visibility.Visible;

        var sb = new Storyboard();

        var widthAnim = new DoubleAnimation
        {
            From = 0,
            To = _subtitleExpandedWidth,
            Duration = new Duration(TimeSpan.FromMilliseconds(340)),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(widthAnim, SubtitleHost);
        Storyboard.SetTargetProperty(widthAnim, nameof(SubtitleHost.Width));

        var opacityAnim = new DoubleAnimation
        {
            From = 0.0,
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(260)),
            BeginTime = TimeSpan.FromMilliseconds(40),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(opacityAnim, SubtitleHost);
        Storyboard.SetTargetProperty(opacityAnim, nameof(SubtitleHost.Opacity));

        var slideAnim = new DoubleAnimation
        {
            From = -10,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(340)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(slideAnim, SubtitleTranslate);
        Storyboard.SetTargetProperty(slideAnim, nameof(TranslateTransform.X));

        sb.Children.Add(widthAnim);
        sb.Children.Add(opacityAnim);
        sb.Children.Add(slideAnim);
        sb.Completed += (_, _) =>
        {
            _isSubtitleAnimating = false;
            _subtitleHideTimer.Start();
        };

        _subtitleStoryboard = sb;
        sb.Begin();
    }

    private void RotateSubtitleWhileOpenAnimated(string nextSubtitle)
    {
        if (_isSubtitleAnimating)
        {
            return;
        }

        _subtitleHideTimer.Stop();
        _isSubtitleAnimating = true;

        var fromWidth = double.IsNaN(SubtitleHost.Width) ? SubtitleHost.ActualWidth : SubtitleHost.Width;

        var fadeOutSb = new Storyboard();
        var outOpacity = new DoubleAnimation
        {
            From = SubtitleBlock.Opacity,
            To = 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(130)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(outOpacity, SubtitleBlock);
        Storyboard.SetTargetProperty(outOpacity, nameof(SubtitleBlock.Opacity));
        fadeOutSb.Children.Add(outOpacity);

        fadeOutSb.Completed += (_, _) =>
        {
            _subtitleExpandedWidth = MeasureSubtitleWidth(nextSubtitle);
            SubtitleTranslate.X = -8;

            var swapInSb = new Storyboard();

            var widthAnim = new DoubleAnimation
            {
                From = fromWidth,
                To = _subtitleExpandedWidth,
                Duration = new Duration(TimeSpan.FromMilliseconds(260)),
                EnableDependentAnimation = true,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(widthAnim, SubtitleHost);
            Storyboard.SetTargetProperty(widthAnim, nameof(SubtitleHost.Width));

            var inOpacity = new DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                BeginTime = TimeSpan.FromMilliseconds(40),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(inOpacity, SubtitleBlock);
            Storyboard.SetTargetProperty(inOpacity, nameof(SubtitleBlock.Opacity));

            var slideAnim = new DoubleAnimation
            {
                From = -8,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(240)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(slideAnim, SubtitleTranslate);
            Storyboard.SetTargetProperty(slideAnim, nameof(TranslateTransform.X));

            swapInSb.Children.Add(widthAnim);
            swapInSb.Children.Add(inOpacity);
            swapInSb.Children.Add(slideAnim);
            swapInSb.Completed += (_, _) =>
            {
                _isSubtitleAnimating = false;
                _subtitleHideTimer.Start();
            };

            _subtitleStoryboard = swapInSb;
            swapInSb.Begin();
        };

        _subtitleStoryboard = fadeOutSb;
        fadeOutSb.Begin();
    }

    private void CollapseSubtitleAnimated()
    {
        if (_isSubtitleAnimating || !_isSubtitleVisible || SubtitleHost.Visibility != Visibility.Visible)
        {
            return;
        }

        _subtitleHideTimer.Stop();
        _isSubtitleAnimating = true;
        _isSubtitleVisible = false;

        var fromWidth = double.IsNaN(SubtitleHost.Width) ? SubtitleHost.ActualWidth : SubtitleHost.Width;
        if (fromWidth <= 0)
        {
            SubtitleHost.Visibility = Visibility.Collapsed;
            _isSubtitleAnimating = false;
            return;
        }

        var sb = new Storyboard();

        var widthAnim = new DoubleAnimation
        {
            From = fromWidth,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(320)),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(widthAnim, SubtitleHost);
        Storyboard.SetTargetProperty(widthAnim, nameof(SubtitleHost.Width));

        var opacityAnim = new DoubleAnimation
        {
            From = SubtitleHost.Opacity,
            To = 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(160)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(opacityAnim, SubtitleHost);
        Storyboard.SetTargetProperty(opacityAnim, nameof(SubtitleHost.Opacity));

        var slideAnim = new DoubleAnimation
        {
            From = SubtitleTranslate.X,
            To = -10,
            Duration = new Duration(TimeSpan.FromMilliseconds(240)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(slideAnim, SubtitleTranslate);
        Storyboard.SetTargetProperty(slideAnim, nameof(TranslateTransform.X));

        sb.Children.Add(widthAnim);
        sb.Children.Add(opacityAnim);
        sb.Children.Add(slideAnim);
        sb.Completed += (_, _) =>
        {
            _isSubtitleAnimating = false;
            if (!_isSubtitleVisible)
            {
                SubtitleHost.Visibility = Visibility.Collapsed;
                SubtitleTranslate.X = 0;
            }
        };

        _subtitleStoryboard = sb;
        sb.Begin();
    }

    private void BrandGroup_Tapped(object sender, TappedRoutedEventArgs e)
        => OnBrandClicked();

    public void SetLiveStatus(string? statusText, SolidColorBrush? dotBrush)
    {
        if (string.IsNullOrWhiteSpace(statusText))
        {
            _hasLiveStatus = false;
            LiveStatusText.Text = string.Empty;
        }
        else
        {
            _hasLiveStatus = true;
            LiveStatusText.Text = statusText.Trim();
            if (dotBrush is not null)
            {
                LiveStatusDot.Fill = dotBrush;
            }
        }

        UpdateResponsiveBadge();
    }

    public void SetUnreadChangelogState(bool hasUnread)
    {
        HelpUnreadDot.Visibility = hasUnread ? Visibility.Visible : Visibility.Collapsed;
        ApplyThemeVisuals();
    }

    public void SetLogoImage(string? imagePath)
    {
        if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
        {
            LogoImage.Source = new BitmapImage(new Uri(imagePath));
            LogoImage.Visibility = Visibility.Visible;
            LogoGlyph.Visibility = Visibility.Collapsed;
        }
        else
        {
            LogoImage.Source = null;
            LogoImage.Visibility = Visibility.Collapsed;
            LogoGlyph.Visibility = Visibility.Visible;
        }

        ApplyThemeVisuals();
    }

    public void ApplyThemeVisuals()
    {
        if (_theme is null)
        {
            return;
        }

        TitleBlock.Foreground = _theme.GetTextPrimaryBrush();
        SubtitleBlock.Foreground = _theme.GetTextSecondaryBrush();
        LiveStatusText.Foreground = _theme.GetTextPrimaryBrush();
        if (LogoImage.Visibility == Visibility.Visible)
        {
            LogoBadge.BorderThickness = new Thickness(0);
            LogoBadge.Background = new SolidColorBrush(Colors.Transparent);
            LogoBadge.BorderBrush = new SolidColorBrush(Colors.Transparent);
        }
        else
        {
            LogoBadge.BorderThickness = new Thickness(1);
            LogoBadge.Background = _theme.GetAdaptiveAccentTintBrush();
            LogoBadge.BorderBrush = _theme.GetAdaptiveAccentBrush();
            LogoGlyph.Foreground = _theme.GetAdaptiveAccentBrush();
        }

        VersionStatusDot.Fill = HelpUnreadDot.Visibility == Visibility.Visible
            ? _theme.GetAdaptiveAccentBrush()
            : _theme.GetSuccessBrush();
        HelpUnreadDot.Fill = _theme.GetAdaptiveAccentBrush();

        _theme.RefreshInteractiveButton(VersionBadgeButton);
        _theme.RefreshInteractiveButton(FeedbackButton);
        _theme.RefreshInteractiveButton(HelpButton);
    }

    private void UpdateResponsiveBadge()
    {
        var width = ActualWidth;
        VersionBadgeText.Text = width > 0 && width < 480
            ? $"v{AppVersion.BaseVersion}"
            : AppVersion.ShortDisplayVersion;

        if (!_hasSubtitle || (width > 0 && width < 760))
        {
            _subtitleHideTimer.Stop();
            _subtitleStoryboard?.Stop();
            _isSubtitleAnimating = false;
            _isSubtitleVisible = false;
            SubtitleHost.Visibility = Visibility.Collapsed;
        }

        LiveStatusPanel.Visibility = _hasLiveStatus && (width <= 0 || width >= 460)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateDragRectangles()
    {
        if (_appWindow is null || XamlRoot is null || !AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var scale = XamlRoot.RasterizationScale;
        if (scale <= 0 || DragRegion.ActualWidth <= 0 || DragRegion.ActualHeight <= 0)
        {
            return;
        }

        var transform = DragRegion.TransformToVisual(null);
        var pt = transform.TransformPoint(new Point(0, 0));

        var rect = new RectInt32(
            (int)Math.Round(pt.X * scale),
            (int)Math.Round(pt.Y * scale),
            Math.Max(1, (int)Math.Round(DragRegion.ActualWidth * scale)),
            Math.Max(1, (int)Math.Round(ActualHeight * scale)));

        if (rect.Equals(_lastDragRect))
        {
            return;
        }

        _lastDragRect = rect;
        _appWindow.TitleBar.SetDragRectangles([rect]);
    }

    private void VersionBadgeButton_Click(object sender, RoutedEventArgs e)
        => ChangelogRequested?.Invoke(this, EventArgs.Empty);

    private void FeedbackButton_Click(object sender, RoutedEventArgs e)
        => FeedbackRequested?.Invoke(this, EventArgs.Empty);

    private void HelpButton_Click(object sender, RoutedEventArgs e)
        => HelpRequested?.Invoke(this, EventArgs.Empty);
}

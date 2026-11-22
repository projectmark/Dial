using Chassis.Core.Versioning;
using Chassis.UI.Theme;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
    private AppWindow? _appWindow;
    private ThemeManager? _theme;
    private RectInt32 _lastDragRect;
    private string _feedbackTooltip = string.Empty;
    private bool _hasSubtitle;
    private bool _hasLiveStatus;

    public ChassisTitleBar()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            UpdateResponsiveBadge();
            UpdateDragRectangles();
        };
        LayoutUpdated += (_, _) => UpdateDragRectangles();
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

    public void SetSubtitle(string? subtitle)
    {
        if (string.IsNullOrWhiteSpace(subtitle))
        {
            _hasSubtitle = false;
            SubtitleBlock.Text = string.Empty;
        }
        else
        {
            _hasSubtitle = true;
            SubtitleBlock.Text = $"— {subtitle.Trim()}";
        }

        UpdateResponsiveBadge();
    }

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

        SubtitleBlock.Visibility = _hasSubtitle && (width <= 0 || width >= 760)
            ? Visibility.Visible
            : Visibility.Collapsed;

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

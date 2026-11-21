using Chassis.Core.Versioning;
using Chassis.UI.Theme;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
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
        ToolTipService.SetToolTip(FeedbackButton, feedbackTooltip);
        AutomationProperties.SetName(FeedbackButton, feedbackTooltip);

        ToolTipService.SetToolTip(HelpButton, helpTooltip);
        AutomationProperties.SetName(HelpButton, helpTooltip);

        ToolTipService.SetToolTip(VersionBadgeButton, versionTooltip);
        AutomationProperties.SetName(VersionBadgeButton, versionTooltip);
    }

    public void SetUnreadChangelogState(bool hasUnread)
    {
        HelpUnreadDot.Visibility = hasUnread ? Visibility.Visible : Visibility.Collapsed;
        ApplyThemeVisuals();
    }

    public void ApplyThemeVisuals()
    {
        if (_theme is null)
        {
            return;
        }

        TitleBlock.Foreground = _theme.GetTextPrimaryBrush();
        LogoBadge.Background = _theme.GetAdaptiveAccentTintBrush();
        LogoBadge.BorderBrush = _theme.GetAdaptiveAccentBrush();
        LogoGlyph.Foreground = _theme.GetAdaptiveAccentBrush();

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
        VersionBadgeText.Text = ActualWidth > 0 && ActualWidth < 480
            ? $"v{AppVersion.BaseVersion}"
            : AppVersion.ShortDisplayVersion;
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

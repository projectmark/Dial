using Chassis.Core.Localization;
using Chassis.Core.Updates;
using Chassis.Core.Versioning;
using Chassis.UI.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Chassis.UI.Dialogs;

/// <summary>
/// Layer 2 Acrylic update notification dialog that opens the verified GitHub Release page in the user's browser.
/// </summary>
public sealed partial class ChassisUpdateDialog : ContentDialog
{
    private readonly IUpdateService _updates;
    private readonly ILocalizationService _localization;
    private readonly ThemeManager _theme;
    private readonly UpdateManifest _manifest;
    private readonly string _currentVersion;
    private readonly Action<string> _onBeforeRestart;
    private readonly bool _canOpenRelease;

    public ChassisUpdateDialog(
        IUpdateService updates,
        ILocalizationService localization,
        ThemeManager theme,
        UpdateCheckResult checkResult,
        Action<string> onBeforeRestart,
        Action? onRequestExit = null)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(checkResult);
        ArgumentNullException.ThrowIfNull(checkResult.Manifest);
        ArgumentNullException.ThrowIfNull(onBeforeRestart);
        _ = onRequestExit;

        _updates = updates;
        _localization = localization;
        _theme = theme;
        _manifest = checkResult.Manifest;
        _currentVersion = checkResult.CurrentVersion;
        _onBeforeRestart = onBeforeRestart;
        _canOpenRelease = (Uri.TryCreate(_manifest.ReleaseNotesUrl, UriKind.Absolute, out var notesUri) &&
                           UpdateService.IsAllowedGitHubUri(notesUri)) ||
                          (Uri.TryCreate(_manifest.DownloadUrl, UriKind.Absolute, out var dlUri) &&
                           UpdateService.IsAllowedGitHubUri(dlUri));

        InitializeComponent();
        ApplyLocalizationAndTheme();
    }

    private void ApplyLocalizationAndTheme()
    {
        RequestedTheme = _theme.IsDarkTheme ? ElementTheme.Dark : ElementTheme.Light;

        Title = _localization["Updates.Dialog.Title"];
        SubtitleBlock.Text = _localization.GetString("Updates.Dialog.Subtitle", AppVersion.ProductTitle);
        SubtitleBlock.Foreground = _theme.GetTextSecondaryBrush();

        VersionCard.Background = _theme.GetCardBackgroundBrush();
        VersionCard.BorderBrush = _theme.GetCardBorderBrush();

        var latestDisplay = _manifest.Version.StartsWith('v') ? _manifest.Version : $"v{_manifest.Version}";
        var currentDisplay = _currentVersion.StartsWith('v') ? _currentVersion : $"v{_currentVersion}";
        VersionHeadline.Text = latestDisplay;
        VersionHeadline.Foreground = _theme.GetTextPrimaryBrush();
        VersionDetail.Text = _localization.GetString("Updates.Dialog.VersionLine", currentDisplay, latestDisplay);
        VersionDetail.Foreground = _theme.GetTextSecondaryBrush();

        if (!string.IsNullOrWhiteSpace(_manifest.ReleaseNotesUrl) &&
            Uri.TryCreate(_manifest.ReleaseNotesUrl, UriKind.Absolute, out var notesUri) &&
            UpdateService.IsAllowedGitHubUri(notesUri))
        {
            ReleaseNotesLabel.Text = _localization["Updates.Dialog.ReleaseNotes"];
            AutomationProperties.SetName(ReleaseNotesBtn, _localization["Updates.Dialog.ReleaseNotes"]);
            ReleaseNotesBtn.Visibility = Visibility.Visible;
            _theme.BindInteractiveButton(ReleaseNotesBtn, () => false);
        }

        if (_canOpenRelease)
        {
            PrimaryButtonText = _localization["Updates.Dialog.InstallNow"];
            IsPrimaryButtonEnabled = true;
            DefaultButton = ContentDialogButton.Primary;
        }
        else
        {
            PrimaryButtonText = string.Empty;
            IsPrimaryButtonEnabled = false;
            DefaultButton = ContentDialogButton.Close;
        }

        CloseButtonText = _localization["Updates.Dialog.Later"];
        ProgressStatusText.Foreground = _theme.GetTextSecondaryBrush();
    }

    private void ReleaseNotesBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _updates.TryOpenReleasePage(_manifest);
        }
        catch
        {
            // Ignore shell launch errors if no default browser is configured.
        }
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!_canOpenRelease)
        {
            return;
        }

        try
        {
            _onBeforeRestart(_manifest.Version);
            if (!_updates.TryOpenReleasePage(_manifest))
            {
                args.Cancel = true;
                NoticeBlock.Text = _localization["Updates.Dialog.Failed"];
                NoticeBlock.Foreground = _theme.GetCriticalBrush();
                NoticeBlock.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            NoticeBlock.Text = _localization.GetString("Updates.Dialog.FailedReason", ex.Message);
            NoticeBlock.Foreground = _theme.GetCriticalBrush();
            NoticeBlock.Visibility = Visibility.Visible;
        }
    }

    private void ContentDialog_CloseButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
    }
}

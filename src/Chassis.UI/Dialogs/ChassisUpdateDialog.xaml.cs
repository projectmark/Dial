using System.Diagnostics;
using Chassis.Core.Localization;
using Chassis.Core.Updates;
using Chassis.Core.Versioning;
using Chassis.UI.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Chassis.UI.Dialogs;

/// <summary>
/// Layer 2 Acrylic update confirmation and SHA256-verified package downloader/extractor dialog.
/// </summary>
public sealed partial class ChassisUpdateDialog : ContentDialog
{
    private readonly IUpdateService _updates;
    private readonly ILocalizationService _localization;
    private readonly ThemeManager _theme;
    private readonly UpdateManifest _manifest;
    private readonly string _currentVersion;
    private readonly Action<string> _onBeforeRestart;
    private readonly bool _canDownload;

    private CancellationTokenSource? _downloadCts;
    private bool _isInstalling;
    private bool _completed;

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
        _canDownload = !string.IsNullOrWhiteSpace(_manifest.DownloadUrl);

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

        if (_canDownload)
        {
            PrimaryButtonText = _localization["Updates.Dialog.InstallNow"];
            IsPrimaryButtonEnabled = true;
            DefaultButton = ContentDialogButton.Primary;
        }
        else
        {
            PrimaryButtonText = string.Empty;
            IsPrimaryButtonEnabled = false;
            NoticeBlock.Text = _localization["Updates.Dialog.ReadOnlyLocation"];
            NoticeBlock.Foreground = _theme.GetCautionBrush();
            NoticeBlock.Visibility = Visibility.Visible;
            DefaultButton = ContentDialogButton.Close;
        }

        CloseButtonText = _localization["Updates.Dialog.Later"];
        ProgressStatusText.Foreground = _theme.GetTextSecondaryBrush();
    }

    private void ReleaseNotesBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(_manifest.ReleaseNotesUrl, UriKind.Absolute, out var uri) ||
            !UpdateService.IsAllowedGitHubUri(uri))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // Ignore shell launch errors if no default browser is configured.
        }
    }

    private async void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!_canDownload || _isInstalling || _completed)
        {
            return;
        }

        var deferral = args.GetDeferral();
        _isInstalling = true;
        IsPrimaryButtonEnabled = false;
        NoticeBlock.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        DownloadProgressBar.IsIndeterminate = true;
        ProgressStatusText.Text = _localization["Updates.Dialog.Downloading"];

        var cts = new CancellationTokenSource();
        _downloadCts = cts;

        try
        {
            var progress = new Progress<double>(ratio =>
            {
                if (ratio < 0)
                {
                    DownloadProgressBar.IsIndeterminate = true;
                }
                else
                {
                    DownloadProgressBar.IsIndeterminate = false;
                    var pct = Math.Clamp(ratio * 100.0, 0.0, 100.0);
                    DownloadProgressBar.Value = pct;
                    ProgressStatusText.Text = _localization.GetString("Updates.Dialog.DownloadingPercent", (int)Math.Round(pct));
                }
            });

            var stagedPath = await _updates.DownloadAndVerifyUpdateAsync(_manifest, progress, cts.Token);
            ProgressStatusText.Text = _localization["Updates.Dialog.Installing"];
            DownloadProgressBar.IsIndeterminate = true;

            string targetFolder;
            if (stagedPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                targetFolder = _updates.ExtractVerifiedPackage(stagedPath, _manifest.Sha256, _manifest.Version);
            }
            else
            {
                targetFolder = Path.GetDirectoryName(stagedPath) ?? stagedPath;
            }

            try
            {
                _updates.OpenExtractedUpdateFolder(targetFolder);
            }
            catch
            {
                // Opening Explorer is best-effort; the extracted path is still shown below.
            }

            _onBeforeRestart(_manifest.Version);

            DownloadProgressBar.IsIndeterminate = false;
            DownloadProgressBar.Value = 100;
            _completed = true;

            args.Cancel = true;
            NoticeBlock.Text = _localization.GetString("Updates.Dialog.ExtractedSuccess", targetFolder);
            NoticeBlock.Foreground = _theme.GetSuccessBrush();
            NoticeBlock.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            args.Cancel = true;
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            NoticeBlock.Text = _localization.GetString("Updates.Dialog.FailedReason", ex.Message);
            NoticeBlock.Foreground = _theme.GetCriticalBrush();
            NoticeBlock.Visibility = Visibility.Visible;
        }
        finally
        {
            _downloadCts = null;
            cts.Dispose();
            _isInstalling = false;
            IsPrimaryButtonEnabled = _canDownload && !_completed;
            deferral.Complete();
        }
    }

    private void ContentDialog_CloseButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        try
        {
            _downloadCts?.Cancel();
        }
        catch
        {
            // Ignore cancellation race during dialog close.
        }
    }
}

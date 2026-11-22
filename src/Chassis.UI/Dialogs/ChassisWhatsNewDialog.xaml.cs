using Chassis.Core.Changelog;
using Chassis.Core.Localization;
using Chassis.UI.Theme;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Chassis.UI.Dialogs;

/// <summary>
/// Layer 2 Acrylic "What's New" modal displaying only the latest changelog release after an update.
/// </summary>
public sealed partial class ChassisWhatsNewDialog : ContentDialog
{
    private readonly ILocalizationService _localization;
    private readonly ThemeManager _theme;
    private readonly ChangelogRelease _release;

    public ChassisWhatsNewDialog(
        ChangelogRelease latestRelease,
        ILocalizationService localization,
        ThemeManager theme)
    {
        ArgumentNullException.ThrowIfNull(latestRelease);
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(theme);

        _release = latestRelease;
        _localization = localization;
        _theme = theme;

        InitializeComponent();
        ApplyContentAndTheme();
    }

    private void ApplyContentAndTheme()
    {
        RequestedTheme = _theme.IsDarkTheme ? ElementTheme.Dark : ElementTheme.Light;

        Title = _localization["WhatsNew.Title"];
        SubtitleBlock.Text = _localization["WhatsNew.Subtitle"];
        SubtitleBlock.Foreground = _theme.GetTextSecondaryBrush();

        PrimaryButtonText = _localization["WhatsNew.Close"];
        SecondaryButtonText = _localization["WhatsNew.ViewAll"];
        DefaultButton = ContentDialogButton.Primary;

        ReleaseCard.Background = _theme.GetCardBackgroundBrush();
        ReleaseCard.BorderBrush = _theme.GetCardBorderBrush();

        var displayVersion = _release.Version.StartsWith('v') ? _release.Version : $"v{_release.Version}";
        VersionHeading.Text = displayVersion;
        VersionHeading.Foreground = _theme.GetTextPrimaryBrush();

        LatestPill.BorderBrush = _theme.GetAdaptiveAccentBrush();
        LatestPill.Background = _theme.GetAdaptiveAccentTintBrush();
        LatestPillText.Text = _localization["Help.Changelog.LatestBadge"];
        LatestPillText.Foreground = _theme.GetAdaptiveAccentBrush();

        DateText.Text = _release.ReleaseDate.ToString("dd-MM-yyyy");
        DateText.Foreground = _theme.GetTextSecondaryBrush();

        ChangesPanel.Children.Clear();
        foreach (var ch in _release.Changes)
        {
            var badgeBrush = ch.ChangeType.ToUpperInvariant() switch
            {
                "ADDED" => _theme.GetSuccessBrush(),
                "CHANGED" => _theme.GetCautionBrush(),
                _ => _theme.GetAdaptiveAccentBrush()
            };

            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var chip = new Border
            {
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = badgeBrush,
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock
                {
                    Text = _localization[$"Help.Changelog.{ch.ChangeType}"],
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = badgeBrush
                }
            };
            Grid.SetColumn(chip, 0);

            var desc = new TextBlock
            {
                Text = _localization[ch.DescriptionKey],
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = _theme.GetTextPrimaryBrush()
            };
            Grid.SetColumn(desc, 1);

            row.Children.Add(chip);
            row.Children.Add(desc);
            ChangesPanel.Children.Add(row);
        }
    }
}

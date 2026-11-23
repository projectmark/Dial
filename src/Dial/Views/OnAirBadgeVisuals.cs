using Chassis.Core.Localization;
using Chassis.UI.Theme;
using Dial.Modules.Radio;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Dial.Views;

internal sealed record OnAirStudioPalette(
    SolidColorBrush TextBrush,
    SolidColorBrush DotCoreBrush,
    SolidColorBrush DotHaloBrush,
    SolidColorBrush BadgeBorderBrush,
    RadialGradientBrush BadgeBgBrush,
    RadialGradientBrush OuterGlowBrush);

internal static class OnAirBadgeVisuals
{
    private static OnAirStudioPalette? _lightPalette;
    private static OnAirStudioPalette? _darkPalette;

    public static OnAirStudioPalette GetPalette(bool isDark)
        => isDark
            ? (_darkPalette ??= CreatePalette(isDark: true))
            : (_lightPalette ??= CreatePalette(isDark: false));

    public static void ApplyStatusBadge(
        Border glow,
        Border badge,
        Ellipse dotHalo,
        Ellipse dotCore,
        TextBlock badgeText,
        RadioPlaybackState state,
        ThemeManager theme,
        ILocalizationService localization,
        Brush? idleForeground = null)
    {
        if (state == RadioPlaybackState.Playing)
        {
            var onAir = GetPalette(theme.IsDarkTheme);
            badgeText.Text = localization["Radio.Status.Live"];
            badgeText.CharacterSpacing = 45;
            badgeText.Foreground = onAir.TextBrush;
            dotCore.Fill = onAir.DotCoreBrush;
            dotHalo.Fill = onAir.DotHaloBrush;
            dotHalo.Visibility = Visibility.Visible;
            badge.BorderBrush = onAir.BadgeBorderBrush;
            badge.Background = onAir.BadgeBgBrush;
            glow.Background = onAir.OuterGlowBrush;
            glow.Visibility = Visibility.Visible;
            return;
        }

        var (statusLabel, statusBrush) = state switch
        {
            RadioPlaybackState.Buffering => (localization["Radio.Status.Buffering"], theme.GetCautionBrush()),
            RadioPlaybackState.Error => (localization["Radio.Status.Error"], theme.GetCriticalBrush()),
            _ => (localization["Radio.Status.Stopped"], idleForeground ?? theme.GetTextSecondaryBrush())
        };

        badgeText.Text = statusLabel;
        badgeText.CharacterSpacing = 0;
        badgeText.Foreground = statusBrush;
        dotCore.Fill = statusBrush;
        dotHalo.Visibility = Visibility.Collapsed;
        badge.BorderBrush = statusBrush;
        badge.Background = theme.GetCardBackgroundBrush();
        glow.Visibility = Visibility.Collapsed;
    }

    private static OnAirStudioPalette CreatePalette(bool isDark)
    {
        var textColor = isDark
            ? Color.FromArgb(0xFF, 0xFF, 0x5C, 0x64)
            : Color.FromArgb(0xFF, 0xCC, 0x14, 0x1D);
        var dotCoreColor = isDark
            ? Color.FromArgb(0xFF, 0xFF, 0x3B, 0x44)
            : Color.FromArgb(0xFF, 0xE6, 0x19, 0x24);
        var dotHaloColor = isDark
            ? Color.FromArgb(0x58, 0xFF, 0x2E, 0x38)
            : Color.FromArgb(0x45, 0xFF, 0x24, 0x2E);
        var badgeBorderColor = isDark
            ? Color.FromArgb(0x68, 0xFF, 0x4D, 0x57)
            : Color.FromArgb(0x45, 0xE0, 0x1E, 0x28);

        var badgeBg = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.65,
            RadiusY = 0.75
        };
        badgeBg.GradientStops.Add(new GradientStop
        {
            Offset = 0.0,
            Color = isDark ? Color.FromArgb(0x36, 0xFF, 0x29, 0x34) : Color.FromArgb(0x26, 0xFF, 0x2A, 0x32)
        });
        badgeBg.GradientStops.Add(new GradientStop
        {
            Offset = 0.7,
            Color = isDark ? Color.FromArgb(0x22, 0xFF, 0x24, 0x2E) : Color.FromArgb(0x16, 0xFF, 0x2A, 0x32)
        });
        badgeBg.GradientStops.Add(new GradientStop
        {
            Offset = 1.0,
            Color = isDark ? Color.FromArgb(0x16, 0xD9, 0x18, 0x22) : Color.FromArgb(0x0E, 0xE6, 0x19, 0x24)
        });

        var outerGlow = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        outerGlow.GradientStops.Add(new GradientStop
        {
            Offset = 0.0,
            Color = isDark ? Color.FromArgb(0x54, 0xFF, 0x24, 0x2E) : Color.FromArgb(0x46, 0xFF, 0x24, 0x2E)
        });
        outerGlow.GradientStops.Add(new GradientStop
        {
            Offset = 0.45,
            Color = isDark ? Color.FromArgb(0x2A, 0xFF, 0x2A, 0x34) : Color.FromArgb(0x22, 0xFF, 0x2E, 0x36)
        });
        outerGlow.GradientStops.Add(new GradientStop
        {
            Offset = 0.78,
            Color = isDark ? Color.FromArgb(0x0E, 0xFF, 0x33, 0x3B) : Color.FromArgb(0x0B, 0xFF, 0x38, 0x40)
        });
        outerGlow.GradientStops.Add(new GradientStop
        {
            Offset = 1.0,
            Color = Color.FromArgb(0x00, 0xFF, 0x38, 0x40)
        });

        return new OnAirStudioPalette(
            TextBrush: new SolidColorBrush(textColor),
            DotCoreBrush: new SolidColorBrush(dotCoreColor),
            DotHaloBrush: new SolidColorBrush(dotHaloColor),
            BadgeBorderBrush: new SolidColorBrush(badgeBorderColor),
            BadgeBgBrush: badgeBg,
            OuterGlowBrush: outerGlow);
    }
}

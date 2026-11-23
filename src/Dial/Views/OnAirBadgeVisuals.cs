using Chassis.Core.Localization;
using Chassis.UI.Theme;
using Dial.Modules.Radio;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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
    internal const double GlowRestOpacity = 0.72;
    internal const double DotHaloRestOpacity = 0.68;

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
            ? Color.FromArgb(0x78, 0xFF, 0x2E, 0x38)
            : Color.FromArgb(0x60, 0xFF, 0x24, 0x2E);
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

        // Tuned slightly richer at full opacity so GlowRestOpacity (~0.72) keeps the calm rest look
        // while allowing subtle 2-5s studio-lamp swells up to 0.92-1.00.
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
            Color = isDark ? Color.FromArgb(0x70, 0xFF, 0x24, 0x2E) : Color.FromArgb(0x5E, 0xFF, 0x24, 0x2E)
        });
        outerGlow.GradientStops.Add(new GradientStop
        {
            Offset = 0.45,
            Color = isDark ? Color.FromArgb(0x38, 0xFF, 0x2A, 0x34) : Color.FromArgb(0x2E, 0xFF, 0x2E, 0x36)
        });
        outerGlow.GradientStops.Add(new GradientStop
        {
            Offset = 0.78,
            Color = isDark ? Color.FromArgb(0x14, 0xFF, 0x33, 0x3B) : Color.FromArgb(0x10, 0xFF, 0x38, 0x40)
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

/// <summary>
/// Schedules subtle, non-distracting random studio-lamp glow swells every 2-5 seconds
/// while a view is loaded and playing, and cleans up immediately on unload or stop.
/// </summary>
internal sealed class OnAirGlowPulseController
{
    private readonly DispatcherQueueTimer _timer;
    private readonly Func<bool> _animationsEnabled;
    private readonly List<(UIElement Glow, UIElement? DotHalo)> _targets = [];

    private Storyboard? _activeStoryboard;
    private bool _isActive;
    private int _cycleToken;

    public OnAirGlowPulseController(DispatcherQueue dispatcherQueue, Func<bool> animationsEnabled)
    {
        _animationsEnabled = animationsEnabled;
        _timer = dispatcherQueue.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += OnTimerTick;
    }

    public void Update(bool isLiveAndVisible, IEnumerable<(UIElement Glow, UIElement? DotHalo)> targets)
    {
        var nextTargets = targets.ToList();
        if (!isLiveAndVisible || nextTargets.Count == 0 || !_animationsEnabled())
        {
            _targets.Clear();
            _targets.AddRange(nextTargets);
            Stop();
            return;
        }

        var targetsChanged = _targets.Count != nextTargets.Count ||
                             !_targets.SequenceEqual(nextTargets);
        if (targetsChanged)
        {
            StopActiveStoryboard();
            _targets.Clear();
            _targets.AddRange(nextTargets);
            ApplyRestOpacity();
        }
        else if (_activeStoryboard is null)
        {
            ApplyRestOpacity();
        }

        if (!_isActive)
        {
            _isActive = true;
            ScheduleNextPulse();
        }
        else if (_activeStoryboard is null && !_timer.IsRunning)
        {
            ScheduleNextPulse();
        }
    }

    public void Stop()
    {
        _isActive = false;
        _cycleToken++;
        _timer.Stop();
        StopActiveStoryboard();
        ApplyRestOpacity();
    }

    private void ScheduleNextPulse()
    {
        if (!_isActive)
        {
            return;
        }

        _timer.Stop();
        _timer.Interval = TimeSpan.FromMilliseconds(Random.Shared.Next(2000, 5001));
        _timer.Start();
    }

    private void OnTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!_isActive || _targets.Count == 0 || !_animationsEnabled())
        {
            Stop();
            return;
        }

        StartGlowSwell();
    }

    private void StartGlowSwell()
    {
        StopActiveStoryboard();

        var token = ++_cycleToken;
        var peakGlow = 0.90 + (Random.Shared.NextDouble() * 0.10); // 0.90 .. 1.00
        var peakHalo = Math.Min(1.0, peakGlow);
        var halfDuration = TimeSpan.FromMilliseconds(650 + Random.Shared.Next(250)); // ~1.3s .. 1.8s full swell

        var sb = new Storyboard();
        foreach (var (glow, dotHalo) in _targets)
        {
            glow.Opacity = OnAirBadgeVisuals.GlowRestOpacity;
            var glowAnim = new DoubleAnimation
            {
                From = OnAirBadgeVisuals.GlowRestOpacity,
                To = peakGlow,
                Duration = new Duration(halfDuration),
                AutoReverse = true,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(glowAnim, glow);
            Storyboard.SetTargetProperty(glowAnim, nameof(UIElement.Opacity));
            sb.Children.Add(glowAnim);

            if (dotHalo is not null)
            {
                dotHalo.Opacity = OnAirBadgeVisuals.DotHaloRestOpacity;
                var haloAnim = new DoubleAnimation
                {
                    From = OnAirBadgeVisuals.DotHaloRestOpacity,
                    To = peakHalo,
                    Duration = new Duration(halfDuration),
                    AutoReverse = true,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                Storyboard.SetTarget(haloAnim, dotHalo);
                Storyboard.SetTargetProperty(haloAnim, nameof(UIElement.Opacity));
                sb.Children.Add(haloAnim);
            }
        }

        sb.Completed += (_, _) =>
        {
            if (ReferenceEquals(_activeStoryboard, sb))
            {
                _activeStoryboard = null;
            }

            if (token != _cycleToken || !_isActive)
            {
                return;
            }

            ApplyRestOpacity();
            ScheduleNextPulse();
        };

        _activeStoryboard = sb;
        sb.Begin();
    }

    private void StopActiveStoryboard()
    {
        if (_activeStoryboard is not null)
        {
            _activeStoryboard.Stop();
            _activeStoryboard = null;
        }
    }

    private void ApplyRestOpacity()
    {
        foreach (var (glow, dotHalo) in _targets)
        {
            glow.Opacity = OnAirBadgeVisuals.GlowRestOpacity;
            if (dotHalo is not null)
            {
                dotHalo.Opacity = OnAirBadgeVisuals.DotHaloRestOpacity;
            }
        }
    }
}

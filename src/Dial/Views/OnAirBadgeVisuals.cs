using System.Diagnostics;
using Chassis.Core.Localization;
using Chassis.Core.Settings;
using Chassis.UI.Theme;
using Dial.Modules.Radio;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
    internal const double GlowRestOpacity = 0.44;
    internal const double DotHaloRestOpacity = 0.46;
    internal const double GlowRestScale = 0.94;
    internal const double DotHaloRestScale = 0.92;

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
            ? Color.FromArgb(0xB8, 0xFF, 0x38, 0x30)
            : Color.FromArgb(0x98, 0xFF, 0x2B, 0x26);
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

        // Warm incandescent lamp aura: calm ambient glow at GlowRestOpacity (0.44)
        // and rich crimson-red bloom when swelling toward 0.92-1.00.
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
            Color = isDark ? Color.FromArgb(0xA4, 0xFF, 0x3A, 0x2E) : Color.FromArgb(0x88, 0xFF, 0x2C, 0x28)
        });
        outerGlow.GradientStops.Add(new GradientStop
        {
            Offset = 0.36,
            Color = isDark ? Color.FromArgb(0x5E, 0xFF, 0x2A, 0x34) : Color.FromArgb(0x4A, 0xFF, 0x2A, 0x34)
        });
        outerGlow.GradientStops.Add(new GradientStop
        {
            Offset = 0.70,
            Color = isDark ? Color.FromArgb(0x22, 0xFF, 0x33, 0x3B) : Color.FromArgb(0x1A, 0xFF, 0x36, 0x3E)
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

    public static string FormatDuration(TimeSpan ts)
        => ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes}:{ts.Seconds:00}";

    public static string FormatStationDisplayName(RadioStation station, int index, bool showNumbers)
        => showNumbers ? $"{index + 1:00}. {station.Name}" : station.Name;

    public static void ApplyTransportButtonState(
        FontIcon playPauseGlyph,
        Button playPauseBtn,
        FontIcon muteGlyph,
        Button muteBtn,
        bool isLiveOrBuffering,
        bool isMuted,
        ILocalizationService localization)
    {
        playPauseGlyph.Glyph = isLiveOrBuffering ? "\uE769" : "\uE768";
        var playLabel = isLiveOrBuffering ? localization["Radio.Stop"] : localization["Radio.Play"];
        AutomationProperties.SetName(playPauseBtn, playLabel);
        ToolTipService.SetToolTip(playPauseBtn, playLabel);

        muteGlyph.Glyph = isMuted ? "\uE74F" : "\uE767";
        var muteLabel = isMuted ? localization["Radio.Unmute"] : localization["Radio.Mute"];
        AutomationProperties.SetName(muteBtn, muteLabel);
        ToolTipService.SetToolTip(muteBtn, muteLabel);
    }

    public static void UpdateTrackLookupButton(
        Button lookupBtn,
        TextBlock? lookupLabel,
        RadioStateSnapshot snapshot,
        ISettingsService settings,
        ILocalizationService localization,
        ThemeManager theme)
    {
        var enabled = settings.GetOption(RadioOptions.TrackLookupEnabled, true);
        var hasTrack = snapshot.State != RadioPlaybackState.Error && !string.IsNullOrWhiteSpace(snapshot.NowPlaying);

        if (!enabled || !hasTrack)
        {
            lookupBtn.Visibility = Visibility.Collapsed;
            return;
        }

        var serviceId = settings.GetOption(RadioOptions.TrackLookupService, MusicLookupHelper.SpotifyId);
        var serviceName = MusicLookupHelper.GetDisplayName(serviceId);
        var tooltip = localization.GetString("Radio.TrackLookup.Tooltip", snapshot.NowPlaying!, serviceName);

        if (lookupLabel is not null)
        {
            lookupLabel.Text = serviceName;
        }

        AutomationProperties.SetName(lookupBtn, tooltip);
        ToolTipService.SetToolTip(lookupBtn, tooltip);
        lookupBtn.Visibility = Visibility.Visible;
        theme.RefreshInteractiveButton(lookupBtn);
    }

    public static void TryOpenTrackLookup(RadioStateSnapshot snapshot, ISettingsService settings)
    {
        var serviceId = settings.GetOption(RadioOptions.TrackLookupService, MusicLookupHelper.SpotifyId);
        var url = MusicLookupHelper.BuildSearchUrl(serviceId, snapshot.NowPlaying);
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Ignore shell launch errors on systems without an associated browser handler.
        }
    }
}

/// <summary>
/// Schedules subtle, warm studio-lamp glow swells every 2-5 seconds (warm-up, brief linger, slow cool-down)
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
            ApplyRestState();
        }
        else if (_activeStoryboard is null)
        {
            ApplyRestState();
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
        ApplyRestState();
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

        StartLampGlow();
    }

    private void StartLampGlow()
    {
        StopActiveStoryboard();

        var token = ++_cycleToken;
        var peakGlow = 0.88 + (Random.Shared.NextDouble() * 0.12); // 0.88 .. 1.00
        var peakHalo = Math.Min(1.0, peakGlow + 0.04);
        var peakGlowScale = 1.12 + (Random.Shared.NextDouble() * 0.06); // 1.12 .. 1.18
        var peakHaloScale = 1.24 + (Random.Shared.NextDouble() * 0.10); // 1.24 .. 1.34

        var warmUpMs = 380 + Random.Shared.Next(140);      // 380-520ms warm-up
        var lingerMs = 260 + Random.Shared.Next(180);      // 260-440ms warm hold
        var coolDownMs = 950 + Random.Shared.Next(350);    // 950-1300ms gentle fade back
        var tPeak = TimeSpan.FromMilliseconds(warmUpMs);
        var tHold = TimeSpan.FromMilliseconds(warmUpMs + lingerMs);
        var tEnd = TimeSpan.FromMilliseconds(warmUpMs + lingerMs + coolDownMs);

        var sb = new Storyboard();
        foreach (var (glow, dotHalo) in _targets)
        {
            glow.Opacity = OnAirBadgeVisuals.GlowRestOpacity;
            var glowScale = EnsureScaleTransform(glow, OnAirBadgeVisuals.GlowRestScale);

            AddLampCurve(sb, glow, nameof(UIElement.Opacity),
                OnAirBadgeVisuals.GlowRestOpacity, peakGlow, peakGlow * 0.94, tPeak, tHold, tEnd);
            AddLampCurve(sb, glowScale, nameof(ScaleTransform.ScaleX),
                OnAirBadgeVisuals.GlowRestScale, peakGlowScale, peakGlowScale * 0.98, tPeak, tHold, tEnd);
            AddLampCurve(sb, glowScale, nameof(ScaleTransform.ScaleY),
                OnAirBadgeVisuals.GlowRestScale, peakGlowScale, peakGlowScale * 0.98, tPeak, tHold, tEnd);

            if (dotHalo is not null)
            {
                dotHalo.Opacity = OnAirBadgeVisuals.DotHaloRestOpacity;
                var haloScale = EnsureScaleTransform(dotHalo, OnAirBadgeVisuals.DotHaloRestScale);

                AddLampCurve(sb, dotHalo, nameof(UIElement.Opacity),
                    OnAirBadgeVisuals.DotHaloRestOpacity, peakHalo, peakHalo * 0.94, tPeak, tHold, tEnd);
                AddLampCurve(sb, haloScale, nameof(ScaleTransform.ScaleX),
                    OnAirBadgeVisuals.DotHaloRestScale, peakHaloScale, peakHaloScale * 0.98, tPeak, tHold, tEnd);
                AddLampCurve(sb, haloScale, nameof(ScaleTransform.ScaleY),
                    OnAirBadgeVisuals.DotHaloRestScale, peakHaloScale, peakHaloScale * 0.98, tPeak, tHold, tEnd);
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

            ApplyRestState();
            ScheduleNextPulse();
        };

        _activeStoryboard = sb;
        sb.Begin();
    }

    private static void AddLampCurve(
        Storyboard sb,
        DependencyObject target,
        string propertyPath,
        double restValue,
        double peakValue,
        double lingerValue,
        TimeSpan tPeak,
        TimeSpan tHold,
        TimeSpan tEnd)
    {
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(tPeak),
            Value = peakValue,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(tHold),
            Value = lingerValue,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(tEnd),
            Value = restValue,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });

        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, propertyPath);
        sb.Children.Add(anim);
    }

    private static ScaleTransform EnsureScaleTransform(UIElement element, double restScale)
    {
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        if (element.RenderTransform is ScaleTransform existing)
        {
            return existing;
        }

        var created = new ScaleTransform { ScaleX = restScale, ScaleY = restScale };
        element.RenderTransform = created;
        return created;
    }

    private void StopActiveStoryboard()
    {
        if (_activeStoryboard is not null)
        {
            _activeStoryboard.Stop();
            _activeStoryboard = null;
        }
    }

    private void ApplyRestState()
    {
        foreach (var (glow, dotHalo) in _targets)
        {
            glow.Opacity = OnAirBadgeVisuals.GlowRestOpacity;
            var glowScale = EnsureScaleTransform(glow, OnAirBadgeVisuals.GlowRestScale);
            glowScale.ScaleX = OnAirBadgeVisuals.GlowRestScale;
            glowScale.ScaleY = OnAirBadgeVisuals.GlowRestScale;

            if (dotHalo is not null)
            {
                dotHalo.Opacity = OnAirBadgeVisuals.DotHaloRestOpacity;
                var haloScale = EnsureScaleTransform(dotHalo, OnAirBadgeVisuals.DotHaloRestScale);
                haloScale.ScaleX = OnAirBadgeVisuals.DotHaloRestScale;
                haloScale.ScaleY = OnAirBadgeVisuals.DotHaloRestScale;
            }
        }
    }
}

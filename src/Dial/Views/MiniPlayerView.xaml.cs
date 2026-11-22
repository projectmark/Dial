using Chassis.Core.Localization;
using Chassis.Core.Settings;
using Chassis.UI.Theme;
using Dial.Modules.Radio;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Dial.Views;

public sealed partial class MiniPlayerView : UserControl
{
    private readonly IRadioPlayerService _player;
    private readonly IAudioMeterService _audioMeter;
    private readonly ISleepTimerService _sleepTimer;
    private readonly ISettingsService _settings;
    private readonly ILocalizationService _localization;
    private readonly ThemeManager _theme;
    private readonly DispatcherQueueTimer _vuTimer;

    private bool _isSubscribed;
    private bool _isSyncingVolume;
    private bool _isUpdatingSliderFromInput;
    private bool? _lastRenderedMuted;
    private float _smoothedLeft;
    private float _smoothedRight;

    public MiniPlayerView()
    {
        InitializeComponent();

        _player = App.Services.GetRequiredService<IRadioPlayerService>();
        _audioMeter = App.Services.GetRequiredService<IAudioMeterService>();
        _sleepTimer = App.Services.GetRequiredService<ISleepTimerService>();
        _settings = App.Services.GetRequiredService<ISettingsService>();
        _localization = App.Services.GetRequiredService<ILocalizationService>();
        _theme = App.Services.GetRequiredService<ThemeManager>();

        _vuTimer = DispatcherQueue.CreateTimer();
        _vuTimer.Interval = TimeSpan.FromMilliseconds(16);
        _vuTimer.Tick += OnVuTimerTick;

        _theme.BindInteractiveButton(PrevBtn, () => false);
        _theme.BindInteractiveButton(PlayPauseBtn, () => _player.Current.State is RadioPlaybackState.Playing or RadioPlaybackState.Buffering);
        _theme.BindInteractiveButton(NextBtn, () => false);
        _theme.BindInteractiveButton(MuteBtn, () => _player.Current.IsMuted);
        _theme.BindInteractiveButton(PinBtn, () => _settings.GetOption(RadioModule.MiniAlwaysOnTopOptionKey, true));
        _theme.BindInteractiveButton(ExpandBtn, () => false);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        ApplyLocalization();
        RenderState(_player.Current);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_isSubscribed)
        {
            _player.StateChanged += OnPlayerStateChanged;
            _player.VolumeChanged += OnPlayerVolumeChanged;
            _sleepTimer.RemainingChanged += OnSleepRemainingChanged;
            _theme.ThemeChanged += OnThemeChanged;
            _localization.LanguageChanged += OnLanguageChanged;
            _isSubscribed = true;
        }

        ApplyLocalization();
        RenderState(_player.Current);
        _vuTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _vuTimer.Stop();
        if (_isSubscribed)
        {
            _player.StateChanged -= OnPlayerStateChanged;
            _player.VolumeChanged -= OnPlayerVolumeChanged;
            _sleepTimer.RemainingChanged -= OnSleepRemainingChanged;
            _theme.ThemeChanged -= OnThemeChanged;
            _localization.LanguageChanged -= OnLanguageChanged;
            _isSubscribed = false;
        }
    }

    private void OnVuTimerTick(DispatcherQueueTimer sender, object args)
    {
        var snap = _player.Current;
        float targetL = 0f;
        float targetR = 0f;

        if (snap.State == RadioPlaybackState.Playing && !snap.IsMuted)
        {
            (targetL, targetR) = _audioMeter.SampleCurrentProcessLevels();
        }

        _smoothedLeft = SmoothVuLevel(_smoothedLeft, targetL);
        _smoothedRight = SmoothVuLevel(_smoothedRight, targetR);

        LeftVuScale.ScaleX = _smoothedLeft;
        RightVuScale.ScaleX = _smoothedRight;
    }

    private static float SmoothVuLevel(float current, float target)
    {
        const float attackFactor = 0.30f;
        const float releaseFactor = 0.11f;

        var factor = target >= current ? attackFactor : releaseFactor;
        var next = current + ((target - current) * factor);
        return next < 0.005f ? 0f : Math.Clamp(next, 0f, 1f);
    }

    private void OnPlayerStateChanged(RadioStateSnapshot snapshot)
        => DispatcherQueue?.TryEnqueue(() =>
        {
            if (_isSubscribed)
            {
                RenderState(snapshot);
            }
        });

    private void OnPlayerVolumeChanged(double volume, bool isMuted)
        => DispatcherQueue?.TryEnqueue(() =>
        {
            if (_isSubscribed && !_isUpdatingSliderFromInput)
            {
                ApplyVolumeUi(volume, isMuted, updateSlider: true);
            }
        });

    private void OnSleepRemainingChanged(TimeSpan? remaining)
        => DispatcherQueue?.TryEnqueue(() =>
        {
            if (_isSubscribed)
            {
                UpdateSleepReadout(remaining);
            }
        });

    private void OnThemeChanged() => RenderState(_player.Current);

    private void OnLanguageChanged(string _)
    {
        ApplyLocalization();
        RenderState(_player.Current);
    }

    private void ApplyLocalization()
    {
        AutomationProperties.SetName(PrevBtn, _localization["Radio.Previous"]);
        ToolTipService.SetToolTip(PrevBtn, _localization["Radio.Previous"]);
        AutomationProperties.SetName(NextBtn, _localization["Radio.Next"]);
        ToolTipService.SetToolTip(NextBtn, _localization["Radio.Next"]);
        AutomationProperties.SetName(VolumeSlider, _localization["Radio.Volume"]);
        ToolTipService.SetToolTip(VolumeSlider, _localization["Radio.Volume"]);
        AutomationProperties.SetName(PinBtn, _localization["Radio.MiniPlayer.Pin"]);
        ToolTipService.SetToolTip(PinBtn, _localization["Radio.MiniPlayer.Pin"]);
        AutomationProperties.SetName(ExpandBtn, _localization["Radio.MiniPlayer.Expand"]);
        ToolTipService.SetToolTip(ExpandBtn, _localization["Radio.MiniPlayer.Expand"]);
        AutomationProperties.SetName(LeftVuTrack, _localization["Radio.Vu.Left"]);
        AutomationProperties.SetName(RightVuTrack, _localization["Radio.Vu.Right"]);
    }

    private void RenderState(RadioStateSnapshot snapshot)
    {
        var primary = _theme.GetTextPrimaryBrush();
        var secondary = _theme.GetTextSecondaryBrush();
        var accent = _theme.GetAdaptiveAccentBrush();

        MiniCard.Background = _theme.GetCardBackgroundBrush();
        MiniCard.BorderBrush = _theme.GetCardBorderBrush();

        var vuTrackBrush = new SolidColorBrush(_theme.IsDarkTheme
            ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x16, 0x00, 0x00, 0x00));
        LeftVuTrack.Background = vuTrackBrush;
        RightVuTrack.Background = vuTrackBrush;
        LeftVuFill.Background = accent;
        RightVuFill.Background = accent;
        LeftVuLabel.Foreground = secondary;
        RightVuLabel.Foreground = secondary;

        StationTitle.Text = snapshot.ActiveStation?.Name ?? _localization["Radio.NoStationSelected"];
        StationTitle.Foreground = primary;

        StatusDot.Fill = snapshot.State switch
        {
            RadioPlaybackState.Playing => _theme.GetSuccessBrush(),
            RadioPlaybackState.Buffering => _theme.GetCautionBrush(),
            RadioPlaybackState.Error => _theme.GetCriticalBrush(),
            _ => secondary
        };

        if (snapshot.State == RadioPlaybackState.Error && !string.IsNullOrWhiteSpace(snapshot.ErrorMessage))
        {
            NowPlayingText.Text = snapshot.ErrorMessage;
            NowPlayingText.Foreground = _theme.GetCriticalBrush();
        }
        else if (!string.IsNullOrWhiteSpace(snapshot.NowPlaying))
        {
            NowPlayingText.Text = snapshot.NowPlaying;
            NowPlayingText.Foreground = primary;
        }
        else
        {
            NowPlayingText.Text = string.IsNullOrWhiteSpace(snapshot.ActiveStation?.Tagline)
                ? _localization["Radio.SelectStationHint"]
                : snapshot.ActiveStation.Tagline;
            NowPlayingText.Foreground = secondary;
        }

        var isLive = snapshot.State is RadioPlaybackState.Playing or RadioPlaybackState.Buffering;
        PlayPauseGlyph.Glyph = isLive ? "\uE769" : "\uE768";
        var playLabel = isLive ? _localization["Radio.Stop"] : _localization["Radio.Play"];
        AutomationProperties.SetName(PlayPauseBtn, playLabel);
        ToolTipService.SetToolTip(PlayPauseBtn, playLabel);

        MuteGlyph.Glyph = snapshot.IsMuted ? "\uE74F" : "\uE767";
        var muteLabel = snapshot.IsMuted ? _localization["Radio.Unmute"] : _localization["Radio.Mute"];
        AutomationProperties.SetName(MuteBtn, muteLabel);
        ToolTipService.SetToolTip(MuteBtn, muteLabel);
        _lastRenderedMuted = snapshot.IsMuted;

        ApplyVolumeUi(snapshot.Volume, snapshot.IsMuted, updateSlider: !_isUpdatingSliderFromInput);

        UpdateSleepReadout(_sleepTimer.IsActive ? _sleepTimer.Remaining : null);

        _theme.RefreshInteractiveButton(PrevBtn);
        _theme.RefreshInteractiveButton(PlayPauseBtn);
        _theme.RefreshInteractiveButton(NextBtn);
        _theme.RefreshInteractiveButton(MuteBtn);
        _theme.RefreshInteractiveButton(PinBtn);
        _theme.RefreshInteractiveButton(ExpandBtn);
    }

    private void UpdateSleepReadout(TimeSpan? remaining)
    {
        if (remaining.HasValue && remaining.Value > TimeSpan.Zero)
        {
            var accent = _theme.GetAdaptiveAccentBrush();
            SleepIndicator.Visibility = Visibility.Visible;
            SleepIcon.Foreground = accent;
            SleepText.Foreground = accent;
            SleepText.Text = remaining.Value.TotalHours >= 1
                ? $"{(int)remaining.Value.TotalHours}:{remaining.Value.Minutes:00}:{remaining.Value.Seconds:00}"
                : $"{remaining.Value.Minutes}:{remaining.Value.Seconds:00}";
        }
        else
        {
            SleepIndicator.Visibility = Visibility.Collapsed;
        }
    }

    private async void PrevBtn_Click(object sender, RoutedEventArgs e)
        => await _player.PreviousStationAsync();

    private async void PlayPauseBtn_Click(object sender, RoutedEventArgs e)
        => await _player.TogglePlayPauseAsync();

    private async void NextBtn_Click(object sender, RoutedEventArgs e)
        => await _player.NextStationAsync();

    private void MuteBtn_Click(object sender, RoutedEventArgs e)
        => _player.ToggleMute();

    private void ApplyVolumeUi(double volume, bool isMuted, bool updateSlider)
    {
        var percent = (int)Math.Round(volume * 100);
        if (updateSlider && Math.Abs(VolumeSlider.Value - percent) >= 0.5)
        {
            _isSyncingVolume = true;
            VolumeSlider.Value = percent;
            _isSyncingVolume = false;
        }

        VolumeValueText.Text = $"{percent}%";
        VolumeValueText.Foreground = isMuted ? _theme.GetTextDisabledBrush() : _theme.GetTextSecondaryBrush();

        if (_lastRenderedMuted != isMuted)
        {
            _lastRenderedMuted = isMuted;
            MuteGlyph.Glyph = isMuted ? "\uE74F" : "\uE767";
            var muteLabel = isMuted ? _localization["Radio.Unmute"] : _localization["Radio.Mute"];
            AutomationProperties.SetName(MuteBtn, muteLabel);
            ToolTipService.SetToolTip(MuteBtn, muteLabel);
            _theme.RefreshInteractiveButton(MuteBtn);
        }
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isSyncingVolume)
        {
            return;
        }

        _isUpdatingSliderFromInput = true;
        try
        {
            var percent = (int)Math.Round(e.NewValue);
            VolumeValueText.Text = $"{percent}%";
            _player.SetVolume(e.NewValue / 100.0);
        }
        finally
        {
            _isUpdatingSliderFromInput = false;
        }
    }

    private void PinBtn_Click(object sender, RoutedEventArgs e)
    {
        var next = !_settings.GetOption(RadioModule.MiniAlwaysOnTopOptionKey, true);
        _settings.SetOption(RadioModule.MiniAlwaysOnTopOptionKey, next);
        MainWindow.Instance?.ApplyMiniAlwaysOnTop();
        _theme.RefreshInteractiveButton(PinBtn);
    }

    private void ExpandBtn_Click(object sender, RoutedEventArgs e)
        => MainWindow.Instance?.ToggleMiniPlayer();
}

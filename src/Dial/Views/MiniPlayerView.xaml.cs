using Chassis.Core.Localization;
using Chassis.Core.Settings;
using Chassis.UI.Theme;
using Dial.Modules.Radio;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Dial.Views;

public sealed partial class MiniPlayerView : UserControl
{
    private readonly IRadioPlayerService _player;
    private readonly ISleepTimerService _sleepTimer;
    private readonly ISettingsService _settings;
    private readonly ILocalizationService _localization;
    private readonly ThemeManager _theme;
    private bool _isSubscribed;

    public MiniPlayerView()
    {
        InitializeComponent();

        _player = App.Services.GetRequiredService<IRadioPlayerService>();
        _sleepTimer = App.Services.GetRequiredService<ISleepTimerService>();
        _settings = App.Services.GetRequiredService<ISettingsService>();
        _localization = App.Services.GetRequiredService<ILocalizationService>();
        _theme = App.Services.GetRequiredService<ThemeManager>();

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
            _sleepTimer.RemainingChanged += OnSleepRemainingChanged;
            _theme.ThemeChanged += OnThemeChanged;
            _localization.LanguageChanged += OnLanguageChanged;
            _isSubscribed = true;
        }

        ApplyLocalization();
        RenderState(_player.Current);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_isSubscribed)
        {
            _player.StateChanged -= OnPlayerStateChanged;
            _sleepTimer.RemainingChanged -= OnSleepRemainingChanged;
            _theme.ThemeChanged -= OnThemeChanged;
            _localization.LanguageChanged -= OnLanguageChanged;
            _isSubscribed = false;
        }
    }

    private void OnPlayerStateChanged(RadioStateSnapshot snapshot)
        => DispatcherQueue?.TryEnqueue(() =>
        {
            if (_isSubscribed)
            {
                RenderState(snapshot);
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
        AutomationProperties.SetName(PinBtn, _localization["Radio.MiniPlayer.Pin"]);
        ToolTipService.SetToolTip(PinBtn, _localization["Radio.MiniPlayer.Pin"]);
        AutomationProperties.SetName(ExpandBtn, _localization["Radio.MiniPlayer.Expand"]);
        ToolTipService.SetToolTip(ExpandBtn, _localization["Radio.MiniPlayer.Expand"]);
    }

    private void RenderState(RadioStateSnapshot snapshot)
    {
        var primary = _theme.GetTextPrimaryBrush();
        var secondary = _theme.GetTextSecondaryBrush();

        MiniCard.Background = _theme.GetCardBackgroundBrush();
        MiniCard.BorderBrush = _theme.GetCardBorderBrush();

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

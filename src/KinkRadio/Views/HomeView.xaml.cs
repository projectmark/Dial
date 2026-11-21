using Chassis.Core.Localization;
using Chassis.UI.Theme;
using KinkRadio.Modules.Radio;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace KinkRadio.Views;

public sealed partial class HomeView : UserControl
{
    private sealed record StationCardVisual(
        RadioStation Station,
        Button CardButton,
        Border AccentPill,
        Border IconBox,
        FontIcon IconGlyph,
        TextBlock TitleText,
        TextBlock TaglineText,
        TextBlock BitrateText,
        TextBlock ActiveBadgeText);

    private readonly IRadioPlayerService _player;
    private readonly ILocalizationService _localization;
    private readonly ThemeManager _theme;
    private readonly List<StationCardVisual> _stationCards = [];

    private bool _isSyncingVolume;
    private bool _isSubscribed;
    private bool _isSingleColumn;
    private bool _isCompactTransport;

    public HomeView()
    {
        InitializeComponent();

        _player = App.Services.GetRequiredService<IRadioPlayerService>();
        _localization = App.Services.GetRequiredService<ILocalizationService>();
        _theme = App.Services.GetRequiredService<ThemeManager>();

        _theme.BindInteractiveButton(PrevStationBtn, () => false);
        _theme.BindInteractiveButton(PlayPauseBtn, () => _player.Current.State is RadioPlaybackState.Playing or RadioPlaybackState.Buffering);
        _theme.BindInteractiveButton(NextStationBtn, () => false);
        _theme.BindInteractiveButton(MuteBtn, () => _player.Current.IsMuted);
        _theme.BindInteractiveButton(MinimizeTrayBtn, () => false);

        BuildStationCards();

        SizeChanged += OnHomeSizeChanged;
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
            _theme.ThemeChanged -= OnThemeChanged;
            _localization.LanguageChanged -= OnLanguageChanged;
            _isSubscribed = false;
        }
    }

    private void OnPlayerStateChanged(RadioStateSnapshot snapshot)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (_isSubscribed)
            {
                RenderState(snapshot);
            }
        });
    }

    private void OnThemeChanged() => RenderState(_player.Current);

    private void OnLanguageChanged(string _)
    {
        ApplyLocalization();
        RenderState(_player.Current);
    }

    private void BuildStationCards()
    {
        StationsGrid.Children.Clear();
        _stationCards.Clear();

        foreach (var station in _player.Stations)
        {
            var accentPill = new Border
            {
                Width = 3,
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Stretch
            };

            var iconGlyph = new FontIcon
            {
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                Glyph = station.IconGlyph,
                FontSize = 18
            };

            var iconBox = new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Top,
                Child = iconGlyph
            };

            var titleText = new TextBlock
            {
                Text = station.Name,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            };

            var activeBadgeText = new TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };

            var titleRow = new Grid { ColumnSpacing = 8 };
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(titleText, 0);
            Grid.SetColumn(activeBadgeText, 1);
            titleRow.Children.Add(titleText);
            titleRow.Children.Add(activeBadgeText);

            var taglineText = new TextBlock
            {
                Text = station.Tagline,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };

            var bitrateText = new TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            };

            var textStack = new StackPanel { Spacing = 4 };
            textStack.Children.Add(titleRow);
            textStack.Children.Add(taglineText);
            textStack.Children.Add(bitrateText);

            var cardGrid = new Grid { ColumnSpacing = 12 };
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(accentPill, 0);
            Grid.SetColumn(iconBox, 1);
            Grid.SetColumn(textStack, 2);
            cardGrid.Children.Add(accentPill);
            cardGrid.Children.Add(iconBox);
            cardGrid.Children.Add(textStack);

            var capturedId = station.Id;
            var cardBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Padding = new Thickness(16, 14, 16, 14),
                CornerRadius = new CornerRadius(8),
                Content = cardGrid
            };
            cardBtn.Click += async (_, _) => await _player.PlayStationAsync(capturedId);
            AutomationProperties.SetName(cardBtn, $"{station.Name} — {station.Tagline}");
            ToolTipService.SetToolTip(cardBtn, station.Tagline);

            _theme.BindInteractiveButton(
                cardBtn,
                () => string.Equals(_player.Current.ActiveStation?.Id, capturedId, StringComparison.OrdinalIgnoreCase));

            StationsGrid.Children.Add(cardBtn);
            _stationCards.Add(new StationCardVisual(
                station,
                cardBtn,
                accentPill,
                iconBox,
                iconGlyph,
                titleText,
                taglineText,
                bitrateText,
                activeBadgeText));
        }

        ArrangeStationGridLayout();
    }

    private void ApplyLocalization()
    {
        StationsHeading.Text = _localization["Radio.StationsHeading"];
        MinimizeTrayLabel.Text = _localization["Radio.MinimizeToTray"];

        AutomationProperties.SetName(PrevStationBtn, _localization["Radio.Previous"]);
        ToolTipService.SetToolTip(PrevStationBtn, _localization["Radio.Previous"]);

        AutomationProperties.SetName(NextStationBtn, _localization["Radio.Next"]);
        ToolTipService.SetToolTip(NextStationBtn, _localization["Radio.Next"]);

        AutomationProperties.SetName(VolumeSlider, _localization["Radio.Volume"]);
        ToolTipService.SetToolTip(VolumeSlider, _localization["Radio.Volume"]);

        AutomationProperties.SetName(MinimizeTrayBtn, _localization["Radio.MinimizeToTray"]);
        ToolTipService.SetToolTip(MinimizeTrayBtn, _localization["Radio.MinimizeToTray"]);
    }

    private void RenderState(RadioStateSnapshot snapshot)
    {
        var primary = _theme.GetTextPrimaryBrush();
        var secondary = _theme.GetTextSecondaryBrush();
        var accent = _theme.GetAdaptiveAccentBrush();
        var accentTint = _theme.GetAdaptiveAccentTintBrush();

        HeroCard.Background = _theme.GetCardBackgroundBrush();
        HeroCard.BorderBrush = _theme.GetCardBorderBrush();
        StationsHeading.Foreground = primary;

        var station = snapshot.ActiveStation;
        var isLiveOrBuffering = snapshot.State is RadioPlaybackState.Playing or RadioPlaybackState.Buffering;

        HeroStationTitle.Text = station?.Name ?? _localization["Radio.NoStationSelected"];
        HeroStationTitle.Foreground = primary;
        HeroIconGlyph.Glyph = station?.IconGlyph ?? "\uE7F6";
        HeroIconGlyph.Foreground = accent;
        HeroIconBox.Background = accentTint;
        HeroIconBox.BorderBrush = accent;

        var (statusLabel, statusBrush) = snapshot.State switch
        {
            RadioPlaybackState.Playing => (_localization["Radio.Status.Live"], _theme.GetSuccessBrush()),
            RadioPlaybackState.Buffering => (_localization["Radio.Status.Buffering"], _theme.GetCautionBrush()),
            RadioPlaybackState.Error => (_localization["Radio.Status.Error"], _theme.GetCriticalBrush()),
            _ => (_localization["Radio.Status.Stopped"], secondary)
        };

        StatusBadgeText.Text = statusLabel;
        StatusBadgeText.Foreground = statusBrush;
        StatusDot.Fill = statusBrush;
        StatusBadge.BorderBrush = statusBrush;
        StatusBadge.Background = _theme.GetCardBackgroundBrush();

        if (snapshot.State == RadioPlaybackState.Error && !string.IsNullOrWhiteSpace(snapshot.ErrorMessage))
        {
            HeroNowPlayingText.Text = snapshot.ErrorMessage;
            HeroNowPlayingText.Foreground = _theme.GetCriticalBrush();
        }
        else if (!string.IsNullOrWhiteSpace(snapshot.NowPlaying))
        {
            HeroNowPlayingText.Text = snapshot.NowPlaying;
            HeroNowPlayingText.Foreground = primary;
        }
        else
        {
            HeroNowPlayingText.Text = station?.Tagline ?? _localization["Radio.SelectStationHint"];
            HeroNowPlayingText.Foreground = secondary;
        }

        HeroMetaText.Text = station is not null
            ? _localization.GetString("Radio.LiveStreamLabel", station.BitrateKbps)
            : string.Empty;
        HeroMetaText.Foreground = secondary;

        PlayPauseGlyph.Glyph = isLiveOrBuffering ? "\uE769" : "\uE768";
        var playPauseLabel = isLiveOrBuffering ? _localization["Radio.Stop"] : _localization["Radio.Play"];
        AutomationProperties.SetName(PlayPauseBtn, playPauseLabel);
        ToolTipService.SetToolTip(PlayPauseBtn, playPauseLabel);

        MuteGlyph.Glyph = snapshot.IsMuted ? "\uE74F" : "\uE767";
        var muteLabel = snapshot.IsMuted ? _localization["Radio.Unmute"] : _localization["Radio.Mute"];
        AutomationProperties.SetName(MuteBtn, muteLabel);
        ToolTipService.SetToolTip(MuteBtn, muteLabel);

        var percent = (int)Math.Round(snapshot.Volume * 100);
        _isSyncingVolume = true;
        VolumeSlider.Value = percent;
        _isSyncingVolume = false;
        VolumeValueText.Text = $"{percent}%";
        VolumeValueText.Foreground = snapshot.IsMuted ? _theme.GetTextDisabledBrush() : secondary;

        _theme.RefreshInteractiveButton(PrevStationBtn);
        _theme.RefreshInteractiveButton(PlayPauseBtn);
        _theme.RefreshInteractiveButton(NextStationBtn);
        _theme.RefreshInteractiveButton(MuteBtn);
        _theme.RefreshInteractiveButton(MinimizeTrayBtn);

        foreach (var card in _stationCards)
        {
            var isSelected = station is not null &&
                             string.Equals(card.Station.Id, station.Id, StringComparison.OrdinalIgnoreCase);

            _theme.RefreshInteractiveButton(card.CardButton);

            card.AccentPill.Background = isSelected ? accent : new SolidColorBrush(Colors.Transparent);
            card.IconBox.Background = isSelected ? accentTint : _theme.GetCardBackgroundBrush();
            card.IconBox.BorderBrush = isSelected ? accent : _theme.GetCardBorderBrush();
            card.IconGlyph.Foreground = isSelected ? accent : primary;
            card.TitleText.Foreground = isSelected ? accent : primary;
            card.TaglineText.Foreground = secondary;
            card.BitrateText.Text = _localization.GetString("Radio.LiveStreamLabel", card.Station.BitrateKbps);
            card.BitrateText.Foreground = secondary;

            card.ActiveBadgeText.Text = isSelected ? _localization["Radio.ActiveBadge"] : string.Empty;
            card.ActiveBadgeText.Foreground = isSelected && isLiveOrBuffering ? _theme.GetSuccessBrush() : accent;
        }
    }

    private void OnHomeSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var singleCol = e.NewSize.Width < 640;
        if (singleCol != _isSingleColumn)
        {
            _isSingleColumn = singleCol;
            ArrangeStationGridLayout();
        }

        var compactTransport = e.NewSize.Width < 560;
        if (compactTransport != _isCompactTransport)
        {
            _isCompactTransport = compactTransport;
            ArrangeTransportLayout();
        }

        MinimizeTrayLabel.Visibility = e.NewSize.Width < 660 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ArrangeStationGridLayout()
    {
        StationsGrid.ColumnDefinitions.Clear();
        StationsGrid.RowDefinitions.Clear();

        if (_isSingleColumn)
        {
            StationsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var i = 0; i < _stationCards.Count; i++)
            {
                StationsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(_stationCards[i].CardButton, i);
                Grid.SetColumn(_stationCards[i].CardButton, 0);
            }
        }
        else
        {
            StationsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            StationsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var rows = Math.Max(1, (_stationCards.Count + 1) / 2);
            for (var r = 0; r < rows; r++)
            {
                StationsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            for (var i = 0; i < _stationCards.Count; i++)
            {
                Grid.SetRow(_stationCards[i].CardButton, i / 2);
                Grid.SetColumn(_stationCards[i].CardButton, i % 2);
            }
        }
    }

    private void ArrangeTransportLayout()
    {
        if (_isCompactTransport)
        {
            Grid.SetRow(VolumePanel, 1);
            Grid.SetColumn(VolumePanel, 0);
            Grid.SetColumnSpan(VolumePanel, 3);
        }
        else
        {
            Grid.SetRow(VolumePanel, 0);
            Grid.SetColumn(VolumePanel, 1);
            Grid.SetColumnSpan(VolumePanel, 1);
        }
    }

    private async void PrevStationBtn_Click(object sender, RoutedEventArgs e)
        => await _player.PreviousStationAsync();

    private async void PlayPauseBtn_Click(object sender, RoutedEventArgs e)
        => await _player.TogglePlayPauseAsync();

    private async void NextStationBtn_Click(object sender, RoutedEventArgs e)
        => await _player.NextStationAsync();

    private void MuteBtn_Click(object sender, RoutedEventArgs e)
        => _player.ToggleMute();

    private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isSyncingVolume)
        {
            return;
        }

        _player.SetVolume(e.NewValue / 100.0);
    }

    private void MinimizeTrayBtn_Click(object sender, RoutedEventArgs e)
        => MainWindow.Instance?.CollapseToTray(showNotification: true);
}

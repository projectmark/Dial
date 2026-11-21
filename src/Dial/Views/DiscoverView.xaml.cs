using Chassis.Core.Localization;
using Chassis.UI.Theme;
using Dial.Modules.Radio;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Dial.Views;

public sealed partial class DiscoverView : UserControl
{
    private sealed record ResultRowVisual(
        RadioStation Station,
        Border CardBorder,
        TextBlock TitleText,
        TextBlock MetaText,
        Button PreviewButton,
        FontIcon PreviewIcon,
        TextBlock PreviewLabel,
        Button FavoriteButton,
        FontIcon FavoriteIcon,
        TextBlock FavoriteLabel);

    private readonly IRadioBrowserService _browser;
    private readonly IRadioPlayerService _player;
    private readonly ILocalizationService _localization;
    private readonly ThemeManager _theme;
    private readonly List<ResultRowVisual> _resultVisuals = [];
    private readonly List<(char Letter, Button Button)> _alphaButtons = [];

    private IReadOnlyList<RadioStation> _results = Array.Empty<RadioStation>();
    private IReadOnlyList<RadioBrowserCountry> _countries = Array.Empty<RadioBrowserCountry>();
    private string? _selectedCountryCode;
    private char? _selectedAlphaFilter;
    private bool _hasLoadedInitial;
    private bool _isSubscribed;
    private CancellationTokenSource? _searchCts;

    public DiscoverView()
    {
        InitializeComponent();

        _browser = App.Services.GetRequiredService<IRadioBrowserService>();
        _player = App.Services.GetRequiredService<IRadioPlayerService>();
        _localization = App.Services.GetRequiredService<ILocalizationService>();
        _theme = App.Services.GetRequiredService<ThemeManager>();

        _selectedCountryCode = _localization.CurrentCulture.StartsWith("nl", StringComparison.OrdinalIgnoreCase)
            ? "NL"
            : null;

        _theme.BindInteractiveButton(SearchBtn, () => false);
        _theme.BindInteractiveButton(CountryFilterBtn, () => !string.IsNullOrWhiteSpace(_selectedCountryCode));
        _theme.BindInteractiveButton(CountryQuickNlBtn, () => string.Equals(_selectedCountryCode, "NL", StringComparison.OrdinalIgnoreCase));
        _theme.BindInteractiveButton(CountryResetBtn, () => false);

        BuildAlphabetGrid();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        ApplyLocalization();
        ApplyThemeVisuals();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_isSubscribed)
        {
            _player.StateChanged += OnPlayerStateChanged;
            _player.StationsChanged += OnStationsChanged;
            _theme.ThemeChanged += OnThemeChanged;
            _localization.LanguageChanged += OnLanguageChanged;
            _isSubscribed = true;
        }

        ApplyLocalization();
        ApplyThemeVisuals();

        if (!_hasLoadedInitial)
        {
            _hasLoadedInitial = true;
            _ = ExecuteSearchAsync();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _searchCts?.Cancel();
        if (_isSubscribed)
        {
            _player.StateChanged -= OnPlayerStateChanged;
            _player.StationsChanged -= OnStationsChanged;
            _theme.ThemeChanged -= OnThemeChanged;
            _localization.LanguageChanged -= OnLanguageChanged;
            _isSubscribed = false;
        }
    }

    private void OnPlayerStateChanged(RadioStateSnapshot _)
        => DispatcherQueue?.TryEnqueue(() =>
        {
            if (_isSubscribed)
            {
                RefreshResultStates();
            }
        });

    private void OnStationsChanged()
        => DispatcherQueue?.TryEnqueue(() =>
        {
            if (_isSubscribed)
            {
                RefreshResultStates();
            }
        });

    private void OnThemeChanged() => ApplyThemeVisuals();

    private void OnLanguageChanged(string _)
    {
        ApplyLocalization();
        ApplyThemeVisuals();
    }

    private void BuildAlphabetGrid()
    {
        AlphabetGrid.Children.Clear();
        AlphabetGrid.ColumnDefinitions.Clear();
        AlphabetGrid.RowDefinitions.Clear();
        _alphaButtons.Clear();

        const int cols = 9;
        for (var c = 0; c < cols; c++)
        {
            AlphabetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var r = 0; r < 3; r++)
        {
            AlphabetGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (var i = 0; i < 26; i++)
        {
            var letter = (char)('A' + i);
            var btn = new Button
            {
                Height = 26,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                CornerRadius = new CornerRadius(4),
                Content = letter.ToString(),
                FontSize = 11
            };
            AutomationProperties.SetName(btn, letter.ToString());
            var captured = letter;
            btn.Click += (_, _) =>
            {
                _selectedAlphaFilter = _selectedAlphaFilter == captured ? null : captured;
                RenderCountryList();
            };
            _theme.BindInteractiveButton(btn, () => _selectedAlphaFilter == captured);
            Grid.SetRow(btn, i / cols);
            Grid.SetColumn(btn, i % cols);
            AlphabetGrid.Children.Add(btn);
            _alphaButtons.Add((letter, btn));
        }
    }

    private void ApplyLocalization()
    {
        HeaderTitle.Text = _localization["Discover.Title"];
        HeaderSubtitle.Text = _localization["Discover.Subtitle"];
        SearchBox.PlaceholderText = _localization["Discover.SearchPlaceholder"];
        AutomationProperties.SetName(SearchBox, _localization["Discover.SearchPlaceholder"]);

        SearchBtnLabel.Text = _localization["Discover.SearchButton"];
        AutomationProperties.SetName(SearchBtn, _localization["Discover.SearchButton"]);

        CountryFilterLabel.Text = string.IsNullOrWhiteSpace(_selectedCountryCode)
            ? _localization["Discover.CountryFilter"]
            : $"{_localization["Discover.CountryFilter"]}: {_selectedCountryCode}";
        AutomationProperties.SetName(CountryFilterBtn, CountryFilterLabel.Text);

        CountryResetBtn.Content = _localization["Discover.CountryReset"];
        AutomationProperties.SetName(CountryResetBtn, _localization["Discover.CountryReset"]);
        AutomationProperties.SetName(CountryQuickNlBtn, "Nederland (NL)");

        RefreshResultStates();
    }

    private async Task ExecuteSearchAsync()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        StatusText.Text = _localization["Discover.Loading"];
        StatusText.Foreground = _theme.GetTextSecondaryBrush();

        try
        {
            var stations = await _browser.SearchStationsAsync(SearchBox.Text, _selectedCountryCode, 60, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            _results = stations;
            BuildResultsList();
            StatusText.Text = _results.Count == 0
                ? _localization["Discover.Empty"]
                : _localization.GetString("Discover.ResultsCount", _results.Count);
            StatusText.Foreground = _theme.GetTextSecondaryBrush();
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer search.
        }
        catch
        {
            if (!cts.IsCancellationRequested)
            {
                _results = Array.Empty<RadioStation>();
                BuildResultsList();
                StatusText.Text = _localization["Discover.Error"];
                StatusText.Foreground = _theme.GetCriticalBrush();
            }
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void BuildResultsList()
    {
        ResultsListPanel.Children.Clear();
        _resultVisuals.Clear();

        foreach (var station in _results)
        {
            var titleText = new TextBlock
            {
                Text = station.Name,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            };

            var bitrateLabel = station.BitrateKbps > 0 ? $"{station.BitrateKbps}k " : string.Empty;
            var metaLine = string.IsNullOrWhiteSpace(station.Tags)
                ? $"[{station.CountryCode}] {bitrateLabel}{station.Codec}"
                : $"[{station.CountryCode}] {bitrateLabel}{station.Codec}  •  {station.Tags}";

            var metaText = new TextBlock
            {
                Text = metaLine,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };

            var infoStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            infoStack.Children.Add(titleText);
            infoStack.Children.Add(metaText);

            var previewIcon = new FontIcon
            {
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                Glyph = "\uE768",
                FontSize = 12
            };
            var previewLabel = new TextBlock
            {
                Text = _localization["Discover.Preview"],
                FontSize = 12,
                FontWeight = FontWeights.SemiBold
            };
            var previewStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            previewStack.Children.Add(previewIcon);
            previewStack.Children.Add(previewLabel);

            var capturedStation = station;
            var previewBtn = new Button
            {
                Height = 32,
                Padding = new Thickness(12, 4, 12, 4),
                CornerRadius = new CornerRadius(6),
                Content = previewStack
            };
            AutomationProperties.SetName(previewBtn, $"{_localization["Discover.Preview"]} {station.Name}");
            previewBtn.Click += async (_, _) =>
            {
                var snap = _player.Current;
                var isSamePlaying = snap.ActiveStation is not null &&
                                    string.Equals(snap.ActiveStation.StreamUrl, capturedStation.StreamUrl, StringComparison.OrdinalIgnoreCase) &&
                                    snap.State is RadioPlaybackState.Playing or RadioPlaybackState.Buffering;
                if (isSamePlaying)
                {
                    _player.Stop();
                }
                else
                {
                    await _player.PreviewStationAsync(capturedStation);
                }
            };
            _theme.BindInteractiveButton(previewBtn, () =>
            {
                var snap = _player.Current;
                return snap.ActiveStation is not null &&
                       string.Equals(snap.ActiveStation.StreamUrl, capturedStation.StreamUrl, StringComparison.OrdinalIgnoreCase) &&
                       snap.State is RadioPlaybackState.Playing or RadioPlaybackState.Buffering;
            });

            var favIcon = new FontIcon
            {
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                Glyph = "\uE710",
                FontSize = 12
            };
            var favLabel = new TextBlock
            {
                Text = _localization["Discover.AddFavorite"],
                FontSize = 12,
                FontWeight = FontWeights.SemiBold
            };
            var favStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            favStack.Children.Add(favIcon);
            favStack.Children.Add(favLabel);

            var favBtn = new Button
            {
                Height = 32,
                Padding = new Thickness(12, 4, 12, 4),
                CornerRadius = new CornerRadius(6),
                Content = favStack
            };
            AutomationProperties.SetName(favBtn, $"{_localization["Discover.AddFavorite"]} {station.Name}");
            favBtn.Click += async (_, _) =>
            {
                if (_player.IsFavorite(capturedStation.StreamUrl))
                {
                    return;
                }

                await _player.AddStationAsync(capturedStation);
            };
            _theme.BindInteractiveButton(favBtn, () => _player.IsFavorite(capturedStation.StreamUrl));

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center
            };
            actions.Children.Add(previewBtn);
            actions.Children.Add(favBtn);

            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(infoStack, 0);
            Grid.SetColumn(actions, 1);
            grid.Children.Add(infoStack);
            grid.Children.Add(actions);

            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(16, 12, 16, 12),
                Child = grid
            };
            ToolTipService.SetToolTip(card, station.StreamUrl);

            ResultsListPanel.Children.Add(card);
            _resultVisuals.Add(new ResultRowVisual(
                station,
                card,
                titleText,
                metaText,
                previewBtn,
                previewIcon,
                previewLabel,
                favBtn,
                favIcon,
                favLabel));
        }

        ApplyThemeVisuals();
        RefreshResultStates();
    }

    private void RefreshResultStates()
    {
        var snap = _player.Current;
        foreach (var row in _resultVisuals)
        {
            var isPlayingThis = snap.ActiveStation is not null &&
                                string.Equals(snap.ActiveStation.StreamUrl, row.Station.StreamUrl, StringComparison.OrdinalIgnoreCase) &&
                                snap.State is RadioPlaybackState.Playing or RadioPlaybackState.Buffering;

            row.PreviewIcon.Glyph = isPlayingThis ? "\uE769" : "\uE768";
            row.PreviewLabel.Text = isPlayingThis ? _localization["Radio.Stop"] : _localization["Discover.Preview"];
            _theme.RefreshInteractiveButton(row.PreviewButton);

            var isFav = _player.IsFavorite(row.Station.StreamUrl);
            row.FavoriteIcon.Glyph = isFav ? "\uE73E" : "\uE710";
            row.FavoriteLabel.Text = isFav ? _localization["Discover.AddedFavorite"] : _localization["Discover.AddFavorite"];
            _theme.RefreshInteractiveButton(row.FavoriteButton);
        }
    }

    private void ApplyThemeVisuals()
    {
        var primary = _theme.GetTextPrimaryBrush();
        var secondary = _theme.GetTextSecondaryBrush();
        var cardBg = _theme.GetCardBackgroundBrush();
        var cardBorder = _theme.GetCardBorderBrush();

        HeaderTitle.Foreground = primary;
        HeaderSubtitle.Foreground = secondary;
        StatusText.Foreground = secondary;

        _theme.RefreshInteractiveButton(SearchBtn);
        _theme.RefreshInteractiveButton(CountryFilterBtn);
        _theme.RefreshInteractiveButton(CountryQuickNlBtn);
        _theme.RefreshInteractiveButton(CountryResetBtn);

        foreach (var row in _resultVisuals)
        {
            row.CardBorder.Background = cardBg;
            row.CardBorder.BorderBrush = cardBorder;
            row.TitleText.Foreground = primary;
            row.MetaText.Foreground = secondary;
            _theme.RefreshInteractiveButton(row.PreviewButton);
            _theme.RefreshInteractiveButton(row.FavoriteButton);
        }
    }

    private async void CountryFlyout_Opening(object sender, object e)
    {
        _theme.RefreshInteractiveButton(CountryQuickNlBtn);
        _theme.RefreshInteractiveButton(CountryResetBtn);
        foreach (var (_, btn) in _alphaButtons)
        {
            _theme.RefreshInteractiveButton(btn);
        }

        if (_countries.Count == 0)
        {
            CountryLoadingRing.IsActive = true;
            CountryLoadingRing.Visibility = Visibility.Visible;
            try
            {
                _countries = await _browser.GetCountriesAsync();
            }
            catch
            {
                _countries = Array.Empty<RadioBrowserCountry>();
            }
            finally
            {
                CountryLoadingRing.IsActive = false;
                CountryLoadingRing.Visibility = Visibility.Collapsed;
            }
        }

        RenderCountryList();
    }

    private void RenderCountryList()
    {
        CountryListPanel.Children.Clear();
        foreach (var (_, btn) in _alphaButtons)
        {
            _theme.RefreshInteractiveButton(btn);
        }

        IEnumerable<RadioBrowserCountry> filtered = _countries;
        if (_selectedAlphaFilter.HasValue)
        {
            var prefix = _selectedAlphaFilter.Value.ToString();
            filtered = filtered.Where(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var country in filtered)
        {
            var capturedCode = country.Code;
            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Height = 32,
                Padding = new Thickness(10, 4, 10, 4),
                CornerRadius = new CornerRadius(6),
                Content = $"{country.Name} [{country.Code}] ({country.StationCount})"
            };
            AutomationProperties.SetName(btn, country.Name);
            btn.Click += (_, _) => SelectCountry(capturedCode);
            _theme.BindInteractiveButton(btn, () => string.Equals(_selectedCountryCode, capturedCode, StringComparison.OrdinalIgnoreCase));
            CountryListPanel.Children.Add(btn);
        }
    }

    private void SelectCountry(string? countryCode)
    {
        _selectedCountryCode = countryCode;
        CountryFlyout.Hide();
        ApplyLocalization();
        _theme.RefreshInteractiveButton(CountryFilterBtn);
        _ = ExecuteSearchAsync();
    }

    private void CountryQuickNlBtn_Click(object sender, RoutedEventArgs e) => SelectCountry("NL");
    private void CountryResetBtn_Click(object sender, RoutedEventArgs e) => SelectCountry(null);
    private void SearchBtn_Click(object sender, RoutedEventArgs e) => _ = ExecuteSearchAsync();

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            _ = ExecuteSearchAsync();
        }
    }
}

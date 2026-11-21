using System.Diagnostics;
using Chassis.Core.Localization;
using Chassis.Core.Paths;
using Chassis.Core.Settings;
using Chassis.Core.Updates;
using Chassis.Core.Versioning;
using Chassis.UI.Navigation;
using Chassis.UI.Theme;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace KinkRadio.Views;

public sealed partial class SettingsView : UserControl
{
    private const string MinimizeToTrayOptionKey = "radio.minimize_to_tray";

    private sealed record CategoryEntry(string Id, string LocalizationKey, UIElement? CustomSection = null);

    private readonly ISettingsService _settings;
    private readonly ILocalizationService _localization;
    private readonly IPathService _paths;
    private readonly IUpdateService _updates;
    private readonly ThemeManager _theme;
    private readonly List<CategoryEntry> _categories = [];
    private readonly List<(string Id, Button Button)> _categoryPillButtons = [];
    private readonly List<(string Id, UIElement Section)> _customSections = [];

    private string _selectedCategory = "all";
    private bool _isUpdating;
    private bool _isSubscribed;

    public SettingsView()
    {
        InitializeComponent();

        _settings = App.Services.GetRequiredService<ISettingsService>();
        _localization = App.Services.GetRequiredService<ILocalizationService>();
        _paths = App.Services.GetRequiredService<IPathService>();
        _updates = App.Services.GetRequiredService<IUpdateService>();
        _theme = App.Services.GetRequiredService<ThemeManager>();

        _theme.BindInteractiveButton(OpenAppDataBtn, () => false);
        _theme.BindInteractiveButton(OpenLogsBtn, () => false);
        _theme.BindInteractiveButton(CheckUpdatesBtn, () => false);

        InitializeCategoriesOnce();
        InitializeLanguageComboOnce();
        SyncControlsFromSettings();

        SizeChanged += OnSettingsSizeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        ApplyLocalization();
        ApplyThemeVisuals();
        UpdateCategoryVisibility();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_isSubscribed)
        {
            _theme.ThemeChanged += ApplyThemeVisuals;
            _localization.LanguageChanged += OnLanguageChanged;
            _isSubscribed = true;
        }

        SyncControlsFromSettings();
        ApplyLocalization();
        ApplyThemeVisuals();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_isSubscribed)
        {
            _theme.ThemeChanged -= ApplyThemeVisuals;
            _localization.LanguageChanged -= OnLanguageChanged;
            _isSubscribed = false;
        }
    }

    private void OnLanguageChanged(string _) => ApplyLocalization();

    private void InitializeCategoriesOnce()
    {
        _categories.Add(new CategoryEntry("all", "Settings.Category.All"));
        _categories.Add(new CategoryEntry("appearance", "Settings.Category.Appearance"));
        _categories.Add(new CategoryEntry("background", "Settings.Category.Background"));

        var providers = App.Services.GetServices<ISettingsCategoryProvider>();
        foreach (var descriptor in providers.SelectMany(p => p.GetCategories()).OrderBy(d => d.Order))
        {
            var section = descriptor.SectionFactory();
            ModuleSectionsHost.Children.Add(section);
            _customSections.Add((descriptor.Id, section));
            _categories.Add(new CategoryEntry(descriptor.Id, descriptor.TitleLocalizationKey, section));
        }

        if (_localization.SupportedLanguages.Count >= 2)
        {
            _categories.Add(new CategoryEntry("language", "Settings.Category.Language"));
        }

        _categories.Add(new CategoryEntry("storage", "Settings.Category.StorageUpdates"));

        _isUpdating = true;
        foreach (var cat in _categories)
        {
            var pill = new Button
            {
                Height = 32,
                Padding = new Thickness(14, 4, 14, 4),
                CornerRadius = new CornerRadius(16),
                Tag = cat.LocalizationKey,
                Content = _localization[cat.LocalizationKey]
            };
            var capturedId = cat.Id;
            pill.Click += (_, _) => SelectCategory(capturedId);
            _theme.BindInteractiveButton(pill, () => string.Equals(_selectedCategory, capturedId, StringComparison.OrdinalIgnoreCase));
            CategoryPillsPanel.Children.Add(pill);
            _categoryPillButtons.Add((cat.Id, pill));

            CategoryCombo.Items.Add(new ComboBoxItem
            {
                Tag = cat.Id,
                Content = _localization[cat.LocalizationKey]
            });
        }
        CategoryCombo.SelectedIndex = 0;
        _isUpdating = false;
    }

    private void InitializeLanguageComboOnce()
    {
        _isUpdating = true;
        var languages = _localization.SupportedLanguages;
        LanguageSection.Visibility = languages.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;

        var selectedIdx = 0;
        for (var i = 0; i < languages.Count; i++)
        {
            var lang = languages[i];
            LanguageCombo.Items.Add(new ComboBoxItem
            {
                Tag = lang.CultureCode,
                Content = $"{lang.DisplayName} ({lang.CultureCode})"
            });

            if (string.Equals(lang.CultureCode, _localization.CurrentCulture, StringComparison.OrdinalIgnoreCase))
            {
                selectedIdx = i;
            }
        }

        if (LanguageCombo.Items.Count > 0)
        {
            LanguageCombo.SelectedIndex = selectedIdx;
        }
        _isUpdating = false;
    }

    private void SyncControlsFromSettings()
    {
        _isUpdating = true;
        ThemeCombo.SelectedIndex = _settings.Current.Theme switch
        {
            ThemeMode.Light => 1,
            ThemeMode.Dark => 2,
            _ => 0
        };
        SystemAccentToggle.IsOn = _settings.Current.UseSystemAccentColor;
        CloseToTrayToggle.IsOn = _settings.Current.CloseToTrayEnabled;
        MinimizeToTrayToggle.IsOn = _settings.GetOption(MinimizeToTrayOptionKey, true);
        StartMinimizedToggle.IsOn = _settings.Current.StartMinimized;
        TrayNotificationsToggle.IsOn = _settings.Current.ShowTrayNotifications;
        _isUpdating = false;

        BuildAccentSwatches();
    }

    private void BuildAccentSwatches()
    {
        SwatchesPanel.Children.Clear();

        var palette = new List<string>(ThemeManager.StandardAccentPresets);
        if (!string.IsNullOrWhiteSpace(_settings.Current.PersonalAccentPreset1Hex))
        {
            palette.Add(_settings.Current.PersonalAccentPreset1Hex);
        }

        if (!string.IsNullOrWhiteSpace(_settings.Current.PersonalAccentPreset2Hex))
        {
            palette.Add(_settings.Current.PersonalAccentPreset2Hex);
        }

        var enabled = !_settings.Current.UseSystemAccentColor;
        foreach (var hex in palette)
        {
            if (!ThemeManager.TryParseHexColor(hex, out var parsedColor))
            {
                continue;
            }

            var isCurrent = enabled && string.Equals(_settings.Current.CustomAccentHex, hex, StringComparison.OrdinalIgnoreCase);
            var swatchBtn = new Button
            {
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(isCurrent ? 2 : 1),
                Background = new SolidColorBrush(parsedColor),
                BorderBrush = isCurrent ? _theme.GetTextPrimaryBrush() : _theme.GetCardBorderBrush(),
                IsEnabled = enabled
            };
            AutomationProperties.SetName(swatchBtn, hex);
            ToolTipService.SetToolTip(swatchBtn, hex);

            var capturedHex = hex;
            swatchBtn.Click += (_, _) => ApplyCustomAccentHex(capturedHex, savePresetSlot: false);
            SwatchesPanel.Children.Add(swatchBtn);
        }

        var picker = new ColorPicker
        {
            IsAlphaEnabled = false,
            IsMoreButtonVisible = false
        };
        if (ThemeManager.TryParseHexColor(_settings.Current.CustomAccentHex, out var initColor))
        {
            picker.Color = initColor;
        }

        var confirmBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = 32,
            CornerRadius = new CornerRadius(6),
            Content = "OK"
        };
        _theme.BindInteractiveButton(confirmBtn, () => false);

        var flyoutBody = new StackPanel { Spacing = 10, Padding = new Thickness(4) };
        flyoutBody.Children.Add(picker);
        flyoutBody.Children.Add(confirmBtn);

        var flyout = new Flyout { Content = flyoutBody };
        confirmBtn.Click += (_, _) =>
        {
            var c = picker.Color;
            var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            ApplyCustomAccentHex(hex, savePresetSlot: true);
            flyout.Hide();
        };

        var addBtn = new Button
        {
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(14),
            Content = new FontIcon
            {
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                Glyph = "\uE710",
                FontSize = 12
            },
            Flyout = flyout,
            IsEnabled = enabled
        };
        AutomationProperties.SetName(addBtn, _localization["Settings.AccentSwatches.Title"]);
        _theme.BindInteractiveButton(addBtn, () => false);
        SwatchesPanel.Children.Add(addBtn);
    }

    private void ApplyCustomAccentHex(string hex, bool savePresetSlot)
    {
        _settings.Update(s =>
        {
            s.UseSystemAccentColor = false;
            s.CustomAccentHex = hex;
            if (savePresetSlot && !ThemeManager.StandardAccentPresets.Contains(hex, StringComparer.OrdinalIgnoreCase))
            {
                s.PersonalAccentPreset2Hex = s.PersonalAccentPreset1Hex;
                s.PersonalAccentPreset1Hex = hex;
            }
        });

        _isUpdating = true;
        SystemAccentToggle.IsOn = false;
        _isUpdating = false;

        _theme.RefreshFromSettings();
        BuildAccentSwatches();
    }

    private void ApplyLocalization()
    {
        HeaderTitle.Text = _localization["Settings.Title"];
        HeaderSubtitle.Text = _localization["Settings.Subtitle"];

        AppearanceHeading.Text = _localization["Settings.Category.Appearance"];
        ThemeCard.Title = _localization["Settings.Theme.Title"];
        ThemeCard.Description = _localization["Settings.Theme.Desc"];
        AutomationProperties.SetName(ThemeCombo, _localization["Settings.Theme.Title"]);

        _isUpdating = true;
        ThemeSystemItem.Content = _localization["Settings.Theme.System"];
        ThemeLightItem.Content = _localization["Settings.Theme.Light"];
        ThemeDarkItem.Content = _localization["Settings.Theme.Dark"];

        foreach (var (_, pill) in _categoryPillButtons)
        {
            if (pill.Tag is string key)
            {
                pill.Content = _localization[key];
                AutomationProperties.SetName(pill, _localization[key]);
            }
        }

        for (var i = 0; i < _categories.Count && i < CategoryCombo.Items.Count; i++)
        {
            if (CategoryCombo.Items[i] is ComboBoxItem item)
            {
                item.Content = _localization[_categories[i].LocalizationKey];
            }
        }
        _isUpdating = false;

        SystemAccentCard.Title = _localization["Settings.SystemAccent.Title"];
        SystemAccentCard.Description = _localization["Settings.SystemAccent.Desc"];
        AutomationProperties.SetName(SystemAccentToggle, _localization["Settings.SystemAccent.Title"]);

        AccentSwatchesCard.Title = _localization["Settings.AccentSwatches.Title"];
        AccentSwatchesCard.Description = _localization["Settings.AccentSwatches.Desc"];

        BackgroundHeading.Text = _localization["Settings.Category.Background"];
        CloseToTrayCard.Title = _localization["Settings.Background.CloseToTray.Title"];
        CloseToTrayCard.Description = _localization["Settings.Background.CloseToTray.Desc"];
        AutomationProperties.SetName(CloseToTrayToggle, _localization["Settings.Background.CloseToTray.Title"]);

        MinimizeToTrayCard.Title = _localization["Settings.Background.MinimizeToTray.Title"];
        MinimizeToTrayCard.Description = _localization["Settings.Background.MinimizeToTray.Desc"];
        AutomationProperties.SetName(MinimizeToTrayToggle, _localization["Settings.Background.MinimizeToTray.Title"]);

        StartMinimizedCard.Title = _localization["Settings.Background.StartMinimized.Title"];
        StartMinimizedCard.Description = _localization["Settings.Background.StartMinimized.Desc"];
        AutomationProperties.SetName(StartMinimizedToggle, _localization["Settings.Background.StartMinimized.Title"]);

        TrayNotificationsCard.Title = _localization["Settings.Background.TrayNotifications.Title"];
        TrayNotificationsCard.Description = _localization["Settings.Background.TrayNotifications.Desc"];
        AutomationProperties.SetName(TrayNotificationsToggle, _localization["Settings.Background.TrayNotifications.Title"]);

        LanguageHeading.Text = _localization["Settings.Category.Language"];
        LanguageCard.Title = _localization["Settings.Language.Title"];
        LanguageCard.Description = _localization["Settings.Language.Desc"];
        AutomationProperties.SetName(LanguageCombo, _localization["Settings.Language.Title"]);

        StorageUpdatesHeading.Text = _localization["Settings.Category.StorageUpdates"];
        AppDataCard.Title = _localization["Settings.AppData.Title"];
        AppDataCard.Description = _paths.RootDirectory;
        OpenAppDataBtn.Content = _localization["Settings.AppData.Action"];
        AutomationProperties.SetName(OpenAppDataBtn, _localization["Settings.AppData.Action"]);

        LogsCard.Title = _localization["Settings.Logs.Title"];
        LogsCard.Description = _localization["Settings.Logs.Desc"];
        OpenLogsBtn.Content = _localization["Settings.Logs.Action"];
        AutomationProperties.SetName(OpenLogsBtn, _localization["Settings.Logs.Action"]);

        UpdatesCard.Title = _localization["Settings.Updates.Title"];
        UpdatesCard.Description = _localization.GetString("Settings.Updates.DescTemplate", AppVersion.ShortDisplayVersion);
        CheckUpdatesBtn.Content = _localization["Settings.Updates.Action"];
        AutomationProperties.SetName(CheckUpdatesBtn, _localization["Settings.Updates.Action"]);
    }

    private void ApplyThemeVisuals()
    {
        var primary = _theme.GetTextPrimaryBrush();
        var secondary = _theme.GetTextSecondaryBrush();

        HeaderTitle.Foreground = primary;
        HeaderSubtitle.Foreground = secondary;
        AppearanceHeading.Foreground = primary;
        BackgroundHeading.Foreground = primary;
        LanguageHeading.Foreground = primary;
        StorageUpdatesHeading.Foreground = primary;

        foreach (var (_, pill) in _categoryPillButtons)
        {
            _theme.RefreshInteractiveButton(pill);
        }

        _theme.RefreshInteractiveButton(OpenAppDataBtn);
        _theme.RefreshInteractiveButton(OpenLogsBtn);
        _theme.RefreshInteractiveButton(CheckUpdatesBtn);
        BuildAccentSwatches();
    }

    private void SelectCategory(string categoryId)
    {
        _selectedCategory = categoryId;

        _isUpdating = true;
        var idx = _categories.FindIndex(c => string.Equals(c.Id, categoryId, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            CategoryCombo.SelectedIndex = idx;
        }
        _isUpdating = false;

        foreach (var (_, pill) in _categoryPillButtons)
        {
            _theme.RefreshInteractiveButton(pill);
        }

        UpdateCategoryVisibility();
    }

    private void UpdateCategoryVisibility()
    {
        var showAll = string.Equals(_selectedCategory, "all", StringComparison.OrdinalIgnoreCase);

        AppearanceSection.Visibility = showAll || _selectedCategory == "appearance"
            ? Visibility.Visible
            : Visibility.Collapsed;

        BackgroundSection.Visibility = showAll || _selectedCategory == "background"
            ? Visibility.Visible
            : Visibility.Collapsed;

        LanguageSection.Visibility = (showAll || _selectedCategory == "language") && _localization.SupportedLanguages.Count >= 2
            ? Visibility.Visible
            : Visibility.Collapsed;

        StorageUpdatesSection.Visibility = showAll || _selectedCategory == "storage"
            ? Visibility.Visible
            : Visibility.Collapsed;

        foreach (var (id, section) in _customSections)
        {
            section.Visibility = showAll || string.Equals(_selectedCategory, id, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void OnSettingsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 720;
        CategoryPillsPanel.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        CategoryCombo.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdating)
        {
            return;
        }

        if (CategoryCombo.SelectedItem is ComboBoxItem item && item.Tag is string id)
        {
            SelectCategory(id);
        }
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdating)
        {
            return;
        }

        var mode = ThemeCombo.SelectedIndex switch
        {
            1 => ThemeMode.Light,
            2 => ThemeMode.Dark,
            _ => ThemeMode.System
        };

        _settings.Update(s => s.Theme = mode);
        _theme.RefreshFromSettings();
    }

    private void SystemAccentToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdating)
        {
            return;
        }

        _settings.Update(s => s.UseSystemAccentColor = SystemAccentToggle.IsOn);
        _theme.RefreshFromSettings();
        BuildAccentSwatches();
    }

    private void CloseToTrayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdating)
        {
            return;
        }

        _settings.Update(s => s.CloseToTrayEnabled = CloseToTrayToggle.IsOn);
    }

    private void MinimizeToTrayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdating)
        {
            return;
        }

        _settings.SetOption(MinimizeToTrayOptionKey, MinimizeToTrayToggle.IsOn);
    }

    private void StartMinimizedToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdating)
        {
            return;
        }

        _settings.Update(s => s.StartMinimized = StartMinimizedToggle.IsOn);
    }

    private void TrayNotificationsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdating)
        {
            return;
        }

        _settings.Update(s => s.ShowTrayNotifications = TrayNotificationsToggle.IsOn);
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdating)
        {
            return;
        }

        if (LanguageCombo.SelectedItem is ComboBoxItem item && item.Tag is string culture)
        {
            _localization.SetLanguage(culture);
        }
    }

    private void OpenAppDataBtn_Click(object sender, RoutedEventArgs e)
    {
        _paths.EnsureDirectoriesExist();
        Process.Start(new ProcessStartInfo(_paths.RootDirectory) { UseShellExecute = true });
    }

    private void OpenLogsBtn_Click(object sender, RoutedEventArgs e)
    {
        _paths.EnsureDirectoriesExist();
        Process.Start(new ProcessStartInfo(_paths.LogsDirectory) { UseShellExecute = true });
    }

    private async void CheckUpdatesBtn_Click(object sender, RoutedEventArgs e)
    {
        var manifestCandidate = Path.Combine(AppContext.BaseDirectory, "releases.json");
        UpdateStatusText.Visibility = Visibility.Visible;

        if (!File.Exists(manifestCandidate))
        {
            UpdateStatusText.Foreground = _theme.GetTextSecondaryBrush();
            UpdateStatusText.Text = _localization["Settings.Updates.NoManifest"];
            return;
        }

        var result = await _updates.CheckForUpdatesAsync(manifestCandidate);
        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            UpdateStatusText.Foreground = _theme.GetCautionBrush();
            UpdateStatusText.Text = result.ErrorMessage;
        }
        else if (result.IsUpdateAvailable)
        {
            UpdateStatusText.Foreground = _theme.GetAdaptiveAccentBrush();
            UpdateStatusText.Text = _localization.GetString("Settings.Updates.Available", result.LatestVersion ?? string.Empty);
        }
        else
        {
            UpdateStatusText.Foreground = _theme.GetSuccessBrush();
            UpdateStatusText.Text = _localization.GetString("Settings.Updates.UpToDate", AppVersion.ShortDisplayVersion);
        }
    }
}

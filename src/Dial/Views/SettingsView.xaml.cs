using System.Diagnostics;
using Chassis.Core.Localization;
using Chassis.Core.Paths;
using Chassis.Core.Settings;
using Chassis.Core.Updates;
using Chassis.Core.Versioning;
using Chassis.UI.Navigation;
using Chassis.UI.Theme;
using Dial.Modules.Radio;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Dial.Views;

public sealed partial class SettingsView : UserControl
{
    private const string MinimizeToTrayOptionKey = RadioOptions.MinimizeToTray;

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
        SyncControlsFromSettings(rebuildSwatches: false);

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

        SyncControlsFromSettings(rebuildSwatches: false);
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

    private void OnLanguageChanged(string _)
    {
        ApplyLocalization();
        BuildAccentSwatches();
    }

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

    private void SyncControlsFromSettings(bool rebuildSwatches = true)
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
        UpdateFrequencyCombo.SelectedIndex = _settings.Current.UpdateCheckFrequency switch
        {
            UpdateCheckFrequency.Daily => 1,
            UpdateCheckFrequency.Weekly => 2,
            UpdateCheckFrequency.Manual => 3,
            _ => 0
        };
        PreReleaseUpdatesToggle.IsOn = _settings.Current.IncludePreReleases;
        _isUpdating = false;

        if (rebuildSwatches)
        {
            BuildAccentSwatches();
        }
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
        AccentSwatchesCard.IsDimmed = !enabled;
        AccentSwatchesCard.Description = enabled
            ? _localization["Settings.AccentSwatches.Desc"]
            : _localization["Settings.AccentSwatches.DisabledBySystemDesc"];
        SwatchesPanel.Opacity = enabled ? 1.0 : 0.42;

        var transparentBrush = new SolidColorBrush(Colors.Transparent);
        var hoverRingBg = _theme.GetCardBackgroundBrush();

        foreach (var hex in palette)
        {
            if (!ThemeManager.TryParseHexColor(hex, out var parsedColor))
            {
                continue;
            }

            var isCurrent = enabled && string.Equals(_settings.Current.CustomAccentHex, hex, StringComparison.OrdinalIgnoreCase);
            var ringBorderBrush = isCurrent ? _theme.GetTextPrimaryBrush() : _theme.GetCardBorderBrush();

            var colorDot = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(parsedColor),
                BorderThickness = new Thickness(1),
                BorderBrush = _theme.GetCardBorderBrush(),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var swatchBtn = new Button
            {
                Width = 30,
                Height = 30,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(15),
                BorderThickness = new Thickness(1),
                Background = isCurrent ? _theme.GetAdaptiveAccentTintBrush() : transparentBrush,
                BorderBrush = ringBorderBrush,
                Content = colorDot,
                IsEnabled = enabled
            };

            swatchBtn.Resources["ButtonBackground"] = isCurrent ? _theme.GetAdaptiveAccentTintBrush() : transparentBrush;
            swatchBtn.Resources["ButtonBackgroundPointerOver"] = hoverRingBg;
            swatchBtn.Resources["ButtonBackgroundPressed"] = hoverRingBg;
            swatchBtn.Resources["ButtonBackgroundDisabled"] = transparentBrush;
            swatchBtn.Resources["ButtonBorderBrush"] = ringBorderBrush;
            swatchBtn.Resources["ButtonBorderBrushPointerOver"] = _theme.GetTextPrimaryBrush();
            swatchBtn.Resources["ButtonBorderBrushPressed"] = _theme.GetTextPrimaryBrush();
            swatchBtn.Resources["ButtonBorderBrushDisabled"] = _theme.GetCardBorderBrush();

            var tooltipText = enabled
                ? hex
                : _localization["Settings.AccentSwatches.DisabledBySystemDesc"];
            AutomationProperties.SetName(swatchBtn, hex);
            ToolTipService.SetToolTip(swatchBtn, tooltipText);

            var capturedHex = hex;
            swatchBtn.Click += (_, _) => ApplyCustomAccentHex(capturedHex, savePresetSlot: false);
            SwatchesPanel.Children.Add(swatchBtn);
        }

        var addBtn = new Button
        {
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(15),
            Content = new FontIcon
            {
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                Glyph = "\uE710",
                FontSize = 12
            },
            IsEnabled = enabled
        };
        var addTooltip = enabled
            ? _localization["Settings.AccentSwatches.CustomPickerTooltip"]
            : _localization["Settings.AccentSwatches.DisabledBySystemDesc"];
        AutomationProperties.SetName(addBtn, _localization["Settings.AccentSwatches.Title"]);
        ToolTipService.SetToolTip(addBtn, addTooltip);
        _theme.BindInteractiveButton(addBtn, () => false);
        addBtn.Click += (_, _) => ShowCustomAccentFlyout(addBtn);
        SwatchesPanel.Children.Add(addBtn);
    }

    private void ShowCustomAccentFlyout(FrameworkElement anchor)
    {
        if (_settings.Current.UseSystemAccentColor)
        {
            return;
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
            Height = 34,
            CornerRadius = new CornerRadius(6),
            Content = _localization["Common.Apply"]
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

        flyout.ShowAt(anchor);
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
        if (!_isSubscribed)
        {
            BuildAccentSwatches();
        }
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
        AccentSwatchesCard.Description = _settings.Current.UseSystemAccentColor
            ? _localization["Settings.AccentSwatches.DisabledBySystemDesc"]
            : _localization["Settings.AccentSwatches.Desc"];

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

        UpdateFrequencyCard.Title = _localization["Settings.Updates.Frequency.Title"];
        UpdateFrequencyCard.Description = _localization["Settings.Updates.Frequency.Desc"];
        AutomationProperties.SetName(UpdateFrequencyCombo, _localization["Settings.Updates.Frequency.Title"]);
        _isUpdating = true;
        UpdateFreqStartupItem.Content = _localization["Settings.Updates.Frequency.Startup"];
        UpdateFreqDailyItem.Content = _localization["Settings.Updates.Frequency.Daily"];
        UpdateFreqWeeklyItem.Content = _localization["Settings.Updates.Frequency.Weekly"];
        UpdateFreqManualItem.Content = _localization["Settings.Updates.Frequency.Manual"];
        _isUpdating = false;

        PreReleaseUpdatesCard.Title = _localization["Settings.Updates.PreRelease.Title"];
        PreReleaseUpdatesCard.Description = _localization["Settings.Updates.PreRelease.Desc"];
        AutomationProperties.SetName(PreReleaseUpdatesToggle, _localization["Settings.Updates.PreRelease.Title"]);
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
        if (!_isSubscribed)
        {
            BuildAccentSwatches();
        }
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

    private void UpdateFrequencyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdating)
        {
            return;
        }

        var freq = UpdateFrequencyCombo.SelectedIndex switch
        {
            1 => UpdateCheckFrequency.Daily,
            2 => UpdateCheckFrequency.Weekly,
            3 => UpdateCheckFrequency.Manual,
            _ => UpdateCheckFrequency.Startup
        };

        _settings.Update(s => s.UpdateCheckFrequency = freq);
    }

    private void PreReleaseUpdatesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdating)
        {
            return;
        }

        _settings.Update(s => s.IncludePreReleases = PreReleaseUpdatesToggle.IsOn);
    }

    private async void CheckUpdatesBtn_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesBtn.IsEnabled = false;
        _theme.RefreshInteractiveButton(CheckUpdatesBtn);
        UpdateStatusText.Visibility = Visibility.Visible;
        UpdateStatusText.Foreground = _theme.GetTextSecondaryBrush();
        UpdateStatusText.Text = _localization["Settings.Updates.Checking"];

        try
        {
            UpdateCheckResult result;
            if (!string.IsNullOrWhiteSpace(AppVersion.GitHubRepositorySlug))
            {
                result = await _updates.CheckForGitHubUpdatesAsync(
                    includePreReleases: _settings.Current.IncludePreReleases);
                _settings.Update(s => s.LastUpdateCheckUtc = DateTimeOffset.UtcNow);
            }
            else
            {
                var manifestCandidate = Path.Combine(AppContext.BaseDirectory, "releases.json");
                if (!File.Exists(manifestCandidate))
                {
                    UpdateStatusText.Foreground = _theme.GetTextSecondaryBrush();
                    UpdateStatusText.Text = _localization["Settings.Updates.NoManifest"];
                    return;
                }

                result = await _updates.CheckForUpdatesAsync(manifestCandidate);
            }

            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                UpdateStatusText.Foreground = _theme.GetCautionBrush();
                UpdateStatusText.Text = result.ErrorMessage;
            }
            else if (result.IsUpdateAvailable)
            {
                UpdateStatusText.Foreground = _theme.GetAdaptiveAccentBrush();
                UpdateStatusText.Text = _localization.GetString("Settings.Updates.Available", result.LatestVersion ?? string.Empty);
                if (MainWindow.Instance is not null)
                {
                    await MainWindow.Instance.ShowUpdatePromptAsync(result);
                }
            }
            else
            {
                UpdateStatusText.Foreground = _theme.GetSuccessBrush();
                UpdateStatusText.Text = _localization.GetString("Settings.Updates.UpToDate", AppVersion.ShortDisplayVersion);
            }
        }
        finally
        {
            CheckUpdatesBtn.IsEnabled = true;
            _theme.RefreshInteractiveButton(CheckUpdatesBtn);
        }
    }
}

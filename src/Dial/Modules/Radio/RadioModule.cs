using Chassis.Core.Data;
using Chassis.Core.Hotkeys;
using Chassis.Core.Localization;
using Chassis.Core.Modules;
using Chassis.Core.Settings;
using Chassis.Core.Tray;
using Chassis.UI.Controls;
using Chassis.UI.Navigation;
using Chassis.UI.Theme;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Dial.Modules.Radio;

/// <summary>
/// Plug-and-play Dial radio module wiring playback, global directory search, sleep timer, stats, tray menu, and hotkeys.
/// </summary>
public sealed class RadioModule : IChassisModule
{
    public const string AutoplayOptionKey = "radio.autoplay_on_start";
    public const string ViewModeOptionKey = "radio.view_mode";
    public const string ShowNumbersOptionKey = "radio.show_position_numbers";
    public const string AlignmentOptionKey = "radio.list_alignment";
    public const string FontSizeOptionKey = "radio.list_font_size";
    public const string MiniAlwaysOnTopOptionKey = "radio.mini_always_on_top";

    private const string HotkeyPlayPauseId = "dial.play_pause";
    private const string HotkeyNextId = "dial.next";
    private const string HotkeyPrevId = "dial.prev";
    private const string HotkeyMuteId = "dial.mute";
    private const string HotkeyToggleWindowId = "dial.toggle_window";

    public string ModuleId => "dial.radio";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<IDataStore<RadioStation>, JsonDataStore<RadioStation>>();
        services.AddSingleton<IDataStore<StationListeningStat>, JsonDataStore<StationListeningStat>>();
        services.AddSingleton<IPackageShareService<List<RadioStation>>, PackageShareService<List<RadioStation>>>();
        services.AddSingleton<IIcyMetadataReader, IcyMetadataReader>();
        services.AddSingleton<IRadioBrowserService, RadioBrowserService>();
        services.AddSingleton<ISleepTimerService, SleepTimerService>();
        services.AddSingleton<IListeningStatsService, ListeningStatsService>();
        services.AddSingleton<IRadioPlayerService, RadioPlayerService>();
        services.AddSingleton<ISettingsCategoryProvider, RadioSettingsCategoryProvider>();
    }

    public void Initialize(IServiceProvider services)
    {
        var player = services.GetRequiredService<IRadioPlayerService>();
        var tray = services.GetRequiredService<ITrayService>();
        var localization = services.GetRequiredService<ILocalizationService>();
        var settings = services.GetRequiredService<ISettingsService>();
        var sleepTimer = services.GetRequiredService<ISleepTimerService>();
        var stats = services.GetRequiredService<IListeningStatsService>();
        var hotkeys = services.GetRequiredService<IHotkeyService>();

        var lastSleepMinutes = -1;

        void RebuildTrayMenu()
        {
            var snapshot = player.Current;
            var isLive = snapshot.State is RadioPlaybackState.Playing or RadioPlaybackState.Buffering;
            var items = new List<TrayMenuItemDescriptor>
            {
                new(
                    "radio.toggle",
                    isLive ? localization["Tray.Pause"] : localization["Tray.Play"],
                    () => _ = player.TogglePlayPauseAsync())
            };

            if (sleepTimer.IsActive)
            {
                var mins = Math.Max(1, (int)Math.Ceiling(sleepTimer.Remaining.TotalMinutes));
                items.Add(new TrayMenuItemDescriptor(
                    "radio.sleep.cancel",
                    localization.GetString("Tray.SleepTimerCancel", mins),
                    sleepTimer.Cancel));
            }

            items.Add(new TrayMenuItemDescriptor("radio.sep1", string.Empty, static () => { }, IsSeparator: true));

            foreach (var station in player.Stations.Take(12))
            {
                var isActive = string.Equals(snapshot.ActiveStation?.Id, station.Id, StringComparison.OrdinalIgnoreCase);
                var label = isActive ? $"● {station.Name}" : $"   {station.Name}";
                var capturedId = station.Id;
                items.Add(new TrayMenuItemDescriptor(
                    $"radio.station.{station.Id}",
                    label,
                    () => _ = player.PlayStationAsync(capturedId)));
            }

            items.Add(new TrayMenuItemDescriptor("radio.sep2", string.Empty, static () => { }, IsSeparator: true));
            items.Add(new TrayMenuItemDescriptor(
                "radio.mute",
                snapshot.IsMuted ? localization["Tray.Unmute"] : localization["Tray.Mute"],
                player.ToggleMute));

            tray.SetCustomMenuItems(items);
        }

        var lastHotkeysEnabled = false;
        string? lastToggleHotkey = null;

        void SyncGlobalHotkeys(bool force = false)
        {
            var enabled = settings.Current.GlobalHotkeysEnabled;
            var toggleHotkey = settings.Current.ToggleWindowHotkey;
            if (!force && enabled == lastHotkeysEnabled && string.Equals(toggleHotkey, lastToggleHotkey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            lastHotkeysEnabled = enabled;
            lastToggleHotkey = toggleHotkey;

            hotkeys.Unregister(HotkeyPlayPauseId);
            hotkeys.Unregister(HotkeyNextId);
            hotkeys.Unregister(HotkeyPrevId);
            hotkeys.Unregister(HotkeyMuteId);
            hotkeys.Unregister(HotkeyToggleWindowId);

            if (!enabled)
            {
                return;
            }

            hotkeys.Register(HotkeyPlayPauseId, HotkeyModifiers.Control | HotkeyModifiers.Alt, 'P', () => _ = player.TogglePlayPauseAsync());
            hotkeys.Register(HotkeyNextId, HotkeyModifiers.Control | HotkeyModifiers.Alt, 'N', () => _ = player.NextStationAsync());
            hotkeys.Register(HotkeyPrevId, HotkeyModifiers.Control | HotkeyModifiers.Alt, 'B', () => _ = player.PreviousStationAsync());
            hotkeys.Register(HotkeyMuteId, HotkeyModifiers.Control | HotkeyModifiers.Alt, 'M', player.ToggleMute);

            if (HotkeyService.TryParseHotkeyString(toggleHotkey, out var mods, out var vk))
            {
                hotkeys.Register(HotkeyToggleWindowId, mods, vk, () =>
                {
                    MainWindow.Instance?.DispatcherQueue.TryEnqueue(() => MainWindow.Instance?.ToggleVisibilityFromHotkey());
                });
            }
        }

        sleepTimer.Expired += () => player.Stop();
        sleepTimer.RemainingChanged += remaining =>
        {
            var currentMinutes = remaining.HasValue
                ? Math.Max(1, (int)Math.Ceiling(remaining.Value.TotalMinutes))
                : -1;
            if (currentMinutes != lastSleepMinutes)
            {
                lastSleepMinutes = currentMinutes;
                RebuildTrayMenu();
            }
        };

        player.StateChanged += snapshot =>
        {
            stats.OnPlaybackStateChanged(snapshot);
            RebuildTrayMenu();
        };
        player.StationsChanged += RebuildTrayMenu;
        localization.LanguageChanged += _ => RebuildTrayMenu();
        settings.SettingsChanged += _ => SyncGlobalHotkeys();

        SyncGlobalHotkeys(force: true);

        _ = InitializeAsyncCore(player, stats, settings, RebuildTrayMenu);
    }

    private static async Task InitializeAsyncCore(
        IRadioPlayerService player,
        IListeningStatsService stats,
        ISettingsService settings,
        Action rebuildTrayMenu)
    {
        await stats.InitializeAsync().ConfigureAwait(false);
        await player.InitializeAsync().ConfigureAwait(false);
        rebuildTrayMenu();

        if (settings.GetOption(AutoplayOptionKey, false))
        {
            var targetId = player.Current.ActiveStation?.Id ?? player.Stations.FirstOrDefault()?.Id;
            if (!string.IsNullOrWhiteSpace(targetId))
            {
                await player.PlayStationAsync(targetId).ConfigureAwait(false);
            }
        }
    }

    private sealed class RadioSettingsCategoryProvider(
        ISettingsService settings,
        ILocalizationService localization,
        ThemeManager theme) : ISettingsCategoryProvider
    {
        public IEnumerable<SettingsCategoryDescriptor> GetCategories()
        {
            yield return new SettingsCategoryDescriptor(
                Id: "radio",
                TitleLocalizationKey: "Radio.Settings.Category",
                SectionFactory: () => BuildSettingsSection(settings, localization, theme),
                Order: 25);
        }

        private static UIElement BuildSettingsSection(
            ISettingsService settings,
            ILocalizationService localization,
            ThemeManager theme)
        {
            var heading = new TextBlock
            {
                FontSize = 15,
                FontWeight = FontWeights.SemiBold
            };

            var isSyncing = false;

            var autoplayToggle = new ToggleSwitch { OnContent = string.Empty, OffContent = string.Empty };
            var autoplayCard = new SettingsCard { Glyph = "\uE7F6", ActionContent = autoplayToggle };

            var viewModeListItem = new ComboBoxItem { Tag = "list" };
            var viewModeGridItem = new ComboBoxItem { Tag = "grid" };
            var viewModeCombo = new ComboBox { MinWidth = 170 };
            viewModeCombo.Items.Add(viewModeListItem);
            viewModeCombo.Items.Add(viewModeGridItem);
            var viewModeCard = new SettingsCard { Glyph = "\uE8FD", ActionContent = viewModeCombo };

            var numbersToggle = new ToggleSwitch { OnContent = string.Empty, OffContent = string.Empty };
            var numbersCard = new SettingsCard { Glyph = "\uE8A9", ActionContent = numbersToggle };

            var alignLeftItem = new ComboBoxItem { Tag = "left" };
            var alignCenterItem = new ComboBoxItem { Tag = "center" };
            var alignRightItem = new ComboBoxItem { Tag = "right" };
            var alignCombo = new ComboBox { MinWidth = 170 };
            alignCombo.Items.Add(alignLeftItem);
            alignCombo.Items.Add(alignCenterItem);
            alignCombo.Items.Add(alignRightItem);
            var alignCard = new SettingsCard { Glyph = "\uE8E4", ActionContent = alignCombo };

            var sizeCompactItem = new ComboBoxItem { Tag = "compact" };
            var sizeNormalItem = new ComboBoxItem { Tag = "normal" };
            var sizeLargeItem = new ComboBoxItem { Tag = "large" };
            var sizeCombo = new ComboBox { MinWidth = 170 };
            sizeCombo.Items.Add(sizeCompactItem);
            sizeCombo.Items.Add(sizeNormalItem);
            sizeCombo.Items.Add(sizeLargeItem);
            var sizeCard = new SettingsCard { Glyph = "\uE8E9", ActionContent = sizeCombo };

            var hotkeysToggle = new ToggleSwitch { OnContent = string.Empty, OffContent = string.Empty };
            var hotkeysCard = new SettingsCard { Glyph = "\uE765", ActionContent = hotkeysToggle };

            void SyncFromSettings()
            {
                isSyncing = true;
                autoplayToggle.IsOn = settings.GetOption(AutoplayOptionKey, false);
                numbersToggle.IsOn = settings.GetOption(ShowNumbersOptionKey, true);
                hotkeysToggle.IsOn = settings.Current.GlobalHotkeysEnabled;

                viewModeCombo.SelectedIndex = string.Equals(settings.GetOption(ViewModeOptionKey, "list"), "grid", StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : 0;

                alignCombo.SelectedIndex = settings.GetOption(AlignmentOptionKey, "left").ToLowerInvariant() switch
                {
                    "center" => 1,
                    "right" => 2,
                    _ => 0
                };

                sizeCombo.SelectedIndex = settings.GetOption(FontSizeOptionKey, "normal").ToLowerInvariant() switch
                {
                    "compact" => 0,
                    "large" => 2,
                    _ => 1
                };
                isSyncing = false;
            }

            autoplayToggle.Toggled += (_, _) =>
            {
                if (!isSyncing) settings.SetOption(AutoplayOptionKey, autoplayToggle.IsOn);
            };
            numbersToggle.Toggled += (_, _) =>
            {
                if (!isSyncing) settings.SetOption(ShowNumbersOptionKey, numbersToggle.IsOn);
            };
            hotkeysToggle.Toggled += (_, _) =>
            {
                if (!isSyncing) settings.Update(s => s.GlobalHotkeysEnabled = hotkeysToggle.IsOn);
            };
            viewModeCombo.SelectionChanged += (_, _) =>
            {
                if (!isSyncing && viewModeCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                {
                    settings.SetOption(ViewModeOptionKey, tag);
                }
            };
            alignCombo.SelectionChanged += (_, _) =>
            {
                if (!isSyncing && alignCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                {
                    settings.SetOption(AlignmentOptionKey, tag);
                }
            };
            sizeCombo.SelectionChanged += (_, _) =>
            {
                if (!isSyncing && sizeCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                {
                    settings.SetOption(FontSizeOptionKey, tag);
                }
            };

            void RefreshText()
            {
                isSyncing = true;
                heading.Text = localization["Radio.Settings.Heading"];

                autoplayCard.Title = localization["Radio.Settings.Autoplay.Title"];
                autoplayCard.Description = localization["Radio.Settings.Autoplay.Desc"];
                AutomationProperties.SetName(autoplayToggle, localization["Radio.Settings.Autoplay.Title"]);

                viewModeCard.Title = localization["Radio.Settings.ViewMode.Title"];
                viewModeCard.Description = localization["Radio.Settings.ViewMode.Desc"];
                viewModeListItem.Content = localization["Radio.Settings.ViewMode.List"];
                viewModeGridItem.Content = localization["Radio.Settings.ViewMode.Grid"];
                AutomationProperties.SetName(viewModeCombo, localization["Radio.Settings.ViewMode.Title"]);

                numbersCard.Title = localization["Radio.Settings.ShowNumbers.Title"];
                numbersCard.Description = localization["Radio.Settings.ShowNumbers.Desc"];
                AutomationProperties.SetName(numbersToggle, localization["Radio.Settings.ShowNumbers.Title"]);

                alignCard.Title = localization["Radio.Settings.Alignment.Title"];
                alignCard.Description = localization["Radio.Settings.Alignment.Desc"];
                alignLeftItem.Content = localization["Radio.Settings.Alignment.Left"];
                alignCenterItem.Content = localization["Radio.Settings.Alignment.Center"];
                alignRightItem.Content = localization["Radio.Settings.Alignment.Right"];
                AutomationProperties.SetName(alignCombo, localization["Radio.Settings.Alignment.Title"]);

                sizeCard.Title = localization["Radio.Settings.FontSize.Title"];
                sizeCard.Description = localization["Radio.Settings.FontSize.Desc"];
                sizeCompactItem.Content = localization["Radio.Settings.FontSize.Compact"];
                sizeNormalItem.Content = localization["Radio.Settings.FontSize.Normal"];
                sizeLargeItem.Content = localization["Radio.Settings.FontSize.Large"];
                AutomationProperties.SetName(sizeCombo, localization["Radio.Settings.FontSize.Title"]);

                hotkeysCard.Title = localization["Radio.Settings.Hotkeys.Title"];
                hotkeysCard.Description = localization["Radio.Settings.Hotkeys.Desc"];
                AutomationProperties.SetName(hotkeysToggle, localization["Radio.Settings.Hotkeys.Title"]);
                isSyncing = false;
            }

            void RefreshTheme() => heading.Foreground = theme.GetTextPrimaryBrush();
            void OnLanguageChanged(string _) => RefreshText();

            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(heading);
            panel.Children.Add(autoplayCard);
            panel.Children.Add(viewModeCard);
            panel.Children.Add(numbersCard);
            panel.Children.Add(alignCard);
            panel.Children.Add(sizeCard);
            panel.Children.Add(hotkeysCard);

            SyncFromSettings();
            RefreshText();
            RefreshTheme();

            panel.Loaded += (_, _) =>
            {
                theme.ThemeChanged += RefreshTheme;
                localization.LanguageChanged += OnLanguageChanged;
                SyncFromSettings();
                RefreshText();
                RefreshTheme();
            };

            panel.Unloaded += (_, _) =>
            {
                theme.ThemeChanged -= RefreshTheme;
                localization.LanguageChanged -= OnLanguageChanged;
            };

            return panel;
        }
    }
}

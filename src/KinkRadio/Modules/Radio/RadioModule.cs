using Chassis.Core.Data;
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

namespace KinkRadio.Modules.Radio;

/// <summary>
/// Plug-and-play KINK Radio module registering audio playback, tray menu quick controls, and module settings.
/// </summary>
public sealed class RadioModule : IChassisModule
{
    public const string AutoplayOptionKey = "radio.autoplay_on_start";

    public string ModuleId => "kink.radio";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<IDataStore<RadioStation>, JsonDataStore<RadioStation>>();
        services.AddSingleton<IIcyMetadataReader, IcyMetadataReader>();
        services.AddSingleton<IRadioPlayerService, RadioPlayerService>();
        services.AddSingleton<ISettingsCategoryProvider, RadioSettingsCategoryProvider>();
    }

    public void Initialize(IServiceProvider services)
    {
        var player = services.GetRequiredService<IRadioPlayerService>();
        var tray = services.GetRequiredService<ITrayService>();
        var localization = services.GetRequiredService<ILocalizationService>();
        var settings = services.GetRequiredService<ISettingsService>();

        void RebuildTrayMenu()
        {
            var snapshot = player.Current;
            var isLive = snapshot.State is RadioPlaybackState.Playing or RadioPlaybackState.Buffering;
            var items = new List<TrayMenuItemDescriptor>
            {
                new(
                    "radio.toggle",
                    isLive ? localization["Tray.Pause"] : localization["Tray.Play"],
                    () => _ = player.TogglePlayPauseAsync()),
                new("radio.sep1", string.Empty, static () => { }, IsSeparator: true)
            };

            foreach (var station in player.Stations)
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

        player.StateChanged += _ => RebuildTrayMenu();
        localization.LanguageChanged += _ => RebuildTrayMenu();

        _ = player.InitializeAsync().ContinueWith(_ =>
        {
            RebuildTrayMenu();
            if (settings.GetOption(AutoplayOptionKey, false))
            {
                var targetId = player.Current.ActiveStation?.Id ?? KinkStationCatalog.DefaultStations[0].Id;
                _ = player.PlayStationAsync(targetId);
            }
        }, TaskScheduler.Default);
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

            var isSyncing = true;
            var autoplayToggle = new ToggleSwitch
            {
                OnContent = string.Empty,
                OffContent = string.Empty,
                IsOn = settings.GetOption(AutoplayOptionKey, false)
            };
            autoplayToggle.Toggled += (_, _) =>
            {
                if (!isSyncing)
                {
                    settings.SetOption(AutoplayOptionKey, autoplayToggle.IsOn);
                }
            };
            isSyncing = false;

            var autoplayCard = new SettingsCard
            {
                Glyph = "\uE7F6",
                ActionContent = autoplayToggle
            };

            void RefreshText()
            {
                heading.Text = localization["Radio.Settings.Heading"];
                autoplayCard.Title = localization["Radio.Settings.Autoplay.Title"];
                autoplayCard.Description = localization["Radio.Settings.Autoplay.Desc"];
                AutomationProperties.SetName(autoplayToggle, localization["Radio.Settings.Autoplay.Title"]);
            }

            void RefreshTheme() => heading.Foreground = theme.GetTextPrimaryBrush();

            void OnLanguageChanged(string _) => RefreshText();

            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(heading);
            panel.Children.Add(autoplayCard);

            panel.Loaded += (_, _) =>
            {
                theme.ThemeChanged += RefreshTheme;
                localization.LanguageChanged += OnLanguageChanged;
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

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
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace Dial.Modules.Radio;

/// <summary>
/// Plug-and-play Dial radio module wiring playback, global directory search, sleep timer, stats, tray menu, and hotkeys.
/// </summary>
public sealed class RadioModule : IChassisModule
{
    public const string AutoplayOptionKey = RadioOptions.AutoplayOnStart;
    public const string BufferSecondsOptionKey = RadioOptions.BufferSeconds;
    public const string ViewModeOptionKey = RadioOptions.ViewMode;
    public const string ShowNumbersOptionKey = RadioOptions.ShowPositionNumbers;
    public const string AlignmentOptionKey = RadioOptions.ListAlignment;
    public const string FontSizeOptionKey = RadioOptions.ListFontSize;
    public const string MiniAlwaysOnTopOptionKey = RadioOptions.MiniAlwaysOnTop;

    public string ModuleId => "dial.radio";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<IDataStore<RadioStation>, JsonDataStore<RadioStation>>();
        services.AddSingleton<IDataStore<StationListeningStat>, JsonDataStore<StationListeningStat>>();
        services.AddSingleton<IPackageShareService<List<RadioStation>>, PackageShareService<List<RadioStation>>>();
        services.AddSingleton<IIcyMetadataReader, IcyMetadataReader>();
        services.AddSingleton<IAudioMeterService, AudioMeterService>();
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

        var hotkeyCallbacks = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase)
        {
            [RadioHotkeyDefaults.PlayPauseId] = () => _ = player.TogglePlayPauseAsync(),
            [RadioHotkeyDefaults.NextId] = () => _ = player.NextStationAsync(),
            [RadioHotkeyDefaults.PrevId] = () => _ = player.PreviousStationAsync(),
            [RadioHotkeyDefaults.MuteId] = player.ToggleMute,
            [RadioHotkeyDefaults.ToggleWindowId] = () =>
                MainWindow.Instance?.DispatcherQueue.TryEnqueue(() => MainWindow.Instance?.ToggleVisibilityFromHotkey())
        };

        var lastHotkeysSnapshot = string.Empty;

        void SyncGlobalHotkeys(bool force = false)
        {
            var enabled = settings.Current.GlobalHotkeysEnabled;
            var configuredPairs = RadioHotkeyCatalog.Actions
                .Select(spec => (Spec: spec, Chord: RadioHotkeyCatalog.GetConfiguredChord(settings, spec)))
                .ToList();

            var snapshotKey = $"{enabled}|{string.Join("|", configuredPairs.Select(p => $"{p.Spec.Id}={p.Chord}"))}";
            if (!force && string.Equals(snapshotKey, lastHotkeysSnapshot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            lastHotkeysSnapshot = snapshotKey;

            foreach (var spec in RadioHotkeyCatalog.Actions)
            {
                hotkeys.Unregister(spec.Id);
            }

            if (!enabled)
            {
                return;
            }

            var claimedChords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (spec, chord) in configuredPairs)
            {
                if (!claimedChords.Add(chord))
                {
                    continue;
                }

                if (HotkeyService.TryParseHotkeyString(chord, out var mods, out var vk) &&
                    hotkeyCallbacks.TryGetValue(spec.Id, out var callback))
                {
                    hotkeys.Register(spec.Id, mods, vk, callback);
                }
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

    private sealed class HotkeyRowVisual
    {
        public required RadioHotkeySpec Spec { get; init; }
        public required SettingsCard Card { get; init; }
        public required StackPanel ControlsPanel { get; init; }
        public required Button CtrlBtn { get; init; }
        public required Button AltBtn { get; init; }
        public required Button ShiftBtn { get; init; }
        public required ComboBox KeyCombo { get; init; }
        public required Button RecordBtn { get; init; }
        public HotkeyModifiers CurrentModifiers { get; set; }
        public uint CurrentVirtualKey { get; set; }
    }

    private sealed class RadioSettingsCategoryProvider(
        ISettingsService settings,
        ILocalizationService localization,
        IHotkeyService hotkeys,
        ThemeManager theme) : ISettingsCategoryProvider
    {
        public IEnumerable<SettingsCategoryDescriptor> GetCategories()
        {
            yield return new SettingsCategoryDescriptor(
                Id: "radio",
                TitleLocalizationKey: "Radio.Settings.Category",
                SectionFactory: () => BuildSettingsSection(settings, localization, hotkeys, theme),
                Order: 25);
        }

        private static UIElement BuildSettingsSection(
            ISettingsService settings,
            ILocalizationService localization,
            IHotkeyService hotkeys,
            ThemeManager theme)
        {
            var heading = new TextBlock
            {
                FontSize = 15,
                FontWeight = FontWeights.SemiBold
            };

            var isSyncing = false;

            static ToggleSwitch CreateAlignedToggle() => new()
            {
                MinWidth = 0,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right,
                OnContent = string.Empty,
                OffContent = string.Empty
            };

            var autoplayToggle = CreateAlignedToggle();
            var autoplayCard = new SettingsCard { Glyph = "\uE7F6", ActionContent = autoplayToggle };

            var buffer2sItem = new ComboBoxItem { Tag = 2 };
            var buffer5sItem = new ComboBoxItem { Tag = 5 };
            var buffer10sItem = new ComboBoxItem { Tag = 10 };
            var buffer15sItem = new ComboBoxItem { Tag = 15 };
            var buffer30sItem = new ComboBoxItem { Tag = 30 };
            var bufferCombo = new ComboBox { Width = 220 };
            bufferCombo.Items.Add(buffer2sItem);
            bufferCombo.Items.Add(buffer5sItem);
            bufferCombo.Items.Add(buffer10sItem);
            bufferCombo.Items.Add(buffer15sItem);
            bufferCombo.Items.Add(buffer30sItem);
            var bufferCard = new SettingsCard { Glyph = "\uE896", ActionContent = bufferCombo };

            var viewModeListItem = new ComboBoxItem { Tag = "list" };
            var viewModeGridItem = new ComboBoxItem { Tag = "grid" };
            var viewModeCombo = new ComboBox { Width = 220 };
            viewModeCombo.Items.Add(viewModeListItem);
            viewModeCombo.Items.Add(viewModeGridItem);
            var viewModeCard = new SettingsCard { Glyph = "\uE8FD", ActionContent = viewModeCombo };

            var numbersToggle = CreateAlignedToggle();
            var numbersCard = new SettingsCard { Glyph = "\uE8A9", ActionContent = numbersToggle };

            var alignLeftItem = new ComboBoxItem { Tag = "left" };
            var alignCenterItem = new ComboBoxItem { Tag = "center" };
            var alignRightItem = new ComboBoxItem { Tag = "right" };
            var alignCombo = new ComboBox { Width = 220 };
            alignCombo.Items.Add(alignLeftItem);
            alignCombo.Items.Add(alignCenterItem);
            alignCombo.Items.Add(alignRightItem);
            var alignCard = new SettingsCard { Glyph = "\uE8E4", ActionContent = alignCombo };

            var sizeCompactItem = new ComboBoxItem { Tag = "compact" };
            var sizeNormalItem = new ComboBoxItem { Tag = "normal" };
            var sizeLargeItem = new ComboBoxItem { Tag = "large" };
            var sizeCombo = new ComboBox { Width = 220 };
            sizeCombo.Items.Add(sizeCompactItem);
            sizeCombo.Items.Add(sizeNormalItem);
            sizeCombo.Items.Add(sizeLargeItem);
            var sizeCard = new SettingsCard { Glyph = "\uE8E9", ActionContent = sizeCombo };

            var trackLookupToggle = CreateAlignedToggle();
            var trackLookupCard = new SettingsCard { Glyph = "\uE8D6", ActionContent = trackLookupToggle };

            var trackLookupServiceCombo = new ComboBox { Width = 220 };
            foreach (var (serviceId, displayName) in MusicLookupHelper.Services)
            {
                trackLookupServiceCombo.Items.Add(new ComboBoxItem
                {
                    Tag = serviceId,
                    Content = displayName
                });
            }
            var trackLookupServiceCard = new SettingsCard { Glyph = "\uE8A7", ActionContent = trackLookupServiceCombo };

            var hotkeysToggle = CreateAlignedToggle();
            var hotkeysCard = new SettingsCard { Glyph = "\uE765", ActionContent = hotkeysToggle };

            var hotkeysSubheading = new TextBlock
            {
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            var resetHotkeysBtn = new Button
            {
                Height = 30,
                Padding = new Thickness(12, 4, 12, 4),
                CornerRadius = new CornerRadius(6)
            };
            theme.BindInteractiveButton(resetHotkeysBtn, () => false);

            var hotkeysHeaderRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            hotkeysHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hotkeysHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(hotkeysSubheading, 0);
            Grid.SetColumn(resetHotkeysBtn, 1);
            hotkeysHeaderRow.Children.Add(hotkeysSubheading);
            hotkeysHeaderRow.Children.Add(resetHotkeysBtn);

            var hotkeyRows = new List<HotkeyRowVisual>();
            RadioHotkeySpec? recordingSpec = null;

            void SyncHotkeyRowStates()
            {
                isSyncing = true;
                var hotkeysEnabled = settings.Current.GlobalHotkeysEnabled;
                var activeBindings = hotkeys.GetActiveBindings();

                // Map chord -> list of action titles to detect in-app duplicates cleanly.
                var configuredBySpec = RadioHotkeyCatalog.Actions.ToDictionary(
                    s => s.Id,
                    s => RadioHotkeyCatalog.GetConfiguredChord(settings, s),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var row in hotkeyRows)
                {
                    var chord = configuredBySpec[row.Spec.Id];
                    if (HotkeyService.TryParseHotkeyString(chord, out var mods, out var vk))
                    {
                        row.CurrentModifiers = mods;
                        row.CurrentVirtualKey = vk;
                    }
                    else
                    {
                        row.CurrentModifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt;
                        row.CurrentVirtualKey = 'P';
                    }

                    row.Card.IsDimmed = !hotkeysEnabled;
                    row.ControlsPanel.Opacity = hotkeysEnabled ? 1.0 : 0.42;
                    row.CtrlBtn.IsEnabled = hotkeysEnabled;
                    row.AltBtn.IsEnabled = hotkeysEnabled;
                    row.ShiftBtn.IsEnabled = hotkeysEnabled;
                    row.KeyCombo.IsEnabled = hotkeysEnabled;
                    row.RecordBtn.IsEnabled = hotkeysEnabled;

                    var targetIdx = -1;
                    for (var i = 0; i < HotkeyService.SelectableVirtualKeys.Count; i++)
                    {
                        if (HotkeyService.SelectableVirtualKeys[i] == row.CurrentVirtualKey)
                        {
                            targetIdx = i;
                            break;
                        }
                    }

                    row.KeyCombo.SelectedIndex = targetIdx;

                    var isRecordingThis = hotkeysEnabled && recordingSpec?.Id == row.Spec.Id;
                    var duplicateWith = hotkeyRows.FirstOrDefault(other =>
                        other.Spec.Id != row.Spec.Id &&
                        string.Equals(configuredBySpec[other.Spec.Id], chord, StringComparison.OrdinalIgnoreCase));

                    if (!hotkeysEnabled)
                    {
                        row.Card.Description = localization.GetString("Radio.Settings.Hotkey.DisabledDesc", chord);
                    }
                    else if (isRecordingThis)
                    {
                        row.Card.Description = localization["Radio.Settings.Hotkey.RecordingHint"];
                    }
                    else if (duplicateWith is not null)
                    {
                        row.Card.Description = localization.GetString(
                            "Radio.Settings.Hotkey.ConflictDesc",
                            localization[duplicateWith.Spec.TitleLocalizationKey],
                            chord);
                    }
                    else if (!activeBindings.ContainsKey(row.Spec.Id))
                    {
                        row.Card.Description = localization.GetString("Radio.Settings.Hotkey.UnavailableDesc", chord);
                    }
                    else
                    {
                        row.Card.Description = localization.GetString("Radio.Settings.Hotkey.ActiveDesc", chord);
                    }

                    theme.RefreshInteractiveButton(row.CtrlBtn);
                    theme.RefreshInteractiveButton(row.AltBtn);
                    theme.RefreshInteractiveButton(row.ShiftBtn);
                    theme.RefreshInteractiveButton(row.RecordBtn);
                }

                resetHotkeysBtn.IsEnabled = !RadioHotkeyCatalog.AreAllDefaults(settings);
                theme.RefreshInteractiveButton(resetHotkeysBtn);
                isSyncing = false;
            }

            void CommitRowChord(HotkeyRowVisual row, HotkeyModifiers mods, uint vk)
            {
                if (HotkeyService.TryFormatNormalizedHotkey(mods, vk, requireModifier: true, out var normalized))
                {
                    RadioHotkeyCatalog.SetConfiguredChord(settings, row.Spec, normalized);
                    SyncHotkeyRowStates();
                }
            }

            static Button CreateModifierButton(string text) => new()
            {
                Height = 32,
                MinWidth = 44,
                Padding = new Thickness(9, 4, 9, 4),
                CornerRadius = new CornerRadius(6),
                Content = text,
                FontSize = 12
            };

            foreach (var spec in RadioHotkeyCatalog.Actions)
            {
                HotkeyRowVisual? row = null;

                var ctrlBtn = CreateModifierButton("Ctrl");
                var altBtn = CreateModifierButton("Alt");
                var shiftBtn = CreateModifierButton("Shift");

                var keyCombo = new ComboBox { Width = 92 };
                foreach (var vk in HotkeyService.SelectableVirtualKeys)
                {
                    keyCombo.Items.Add(new ComboBoxItem
                    {
                        Tag = vk,
                        Content = HotkeyService.FormatKeyName(vk)
                    });
                }

                var recordBtn = new Button
                {
                    Width = 34,
                    Height = 32,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(6),
                    Content = new FontIcon
                    {
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                        Glyph = "\uE765",
                        FontSize = 13
                    }
                };

                var controlsPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center
                };
                controlsPanel.Children.Add(ctrlBtn);
                controlsPanel.Children.Add(altBtn);
                controlsPanel.Children.Add(shiftBtn);
                controlsPanel.Children.Add(keyCombo);
                controlsPanel.Children.Add(recordBtn);

                var card = new SettingsCard
                {
                    Glyph = spec.Glyph,
                    ActionContent = controlsPanel
                };

                row = new HotkeyRowVisual
                {
                    Spec = spec,
                    Card = card,
                    ControlsPanel = controlsPanel,
                    CtrlBtn = ctrlBtn,
                    AltBtn = altBtn,
                    ShiftBtn = shiftBtn,
                    KeyCombo = keyCombo,
                    RecordBtn = recordBtn
                };

                theme.BindInteractiveButton(ctrlBtn, () => row.CurrentModifiers.HasFlag(HotkeyModifiers.Control));
                theme.BindInteractiveButton(altBtn, () => row.CurrentModifiers.HasFlag(HotkeyModifiers.Alt));
                theme.BindInteractiveButton(shiftBtn, () => row.CurrentModifiers.HasFlag(HotkeyModifiers.Shift));
                theme.BindInteractiveButton(recordBtn, () => recordingSpec?.Id == row.Spec.Id);

                void ToggleModifier(HotkeyModifiers flag)
                {
                    var nextMods = row.CurrentModifiers ^ flag;
                    if ((nextMods & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift)) == 0)
                    {
                        return;
                    }

                    recordingSpec = null;
                    CommitRowChord(row, nextMods, row.CurrentVirtualKey);
                }

                ctrlBtn.Click += (_, _) => ToggleModifier(HotkeyModifiers.Control);
                altBtn.Click += (_, _) => ToggleModifier(HotkeyModifiers.Alt);
                shiftBtn.Click += (_, _) => ToggleModifier(HotkeyModifiers.Shift);

                keyCombo.SelectionChanged += (_, _) =>
                {
                    if (isSyncing)
                    {
                        return;
                    }

                    if (keyCombo.SelectedItem is ComboBoxItem item && item.Tag is uint selectedVk)
                    {
                        recordingSpec = null;
                        CommitRowChord(row, row.CurrentModifiers, selectedVk);
                    }
                };

                recordBtn.Click += (_, _) =>
                {
                    recordingSpec = recordingSpec?.Id == row.Spec.Id ? null : row.Spec;
                    SyncHotkeyRowStates();
                    if (recordingSpec is not null)
                    {
                        recordBtn.Focus(FocusState.Programmatic);
                    }
                };

                recordBtn.LostFocus += (_, _) =>
                {
                    if (recordingSpec?.Id == row.Spec.Id)
                    {
                        recordingSpec = null;
                        SyncHotkeyRowStates();
                    }
                };

                recordBtn.PreviewKeyDown += (_, e) =>
                {
                    if (recordingSpec?.Id != row.Spec.Id)
                    {
                        return;
                    }

                    if (e.Key == VirtualKey.Escape)
                    {
                        e.Handled = true;
                        recordingSpec = null;
                        SyncHotkeyRowStates();
                        return;
                    }

                    var capturedVk = MapCapturedVirtualKey(e.Key);
                    if (capturedVk == 0)
                    {
                        e.Handled = true;
                        return;
                    }

                    var capturedMods = ReadCurrentModifiers();
                    if (capturedMods == HotkeyModifiers.None)
                    {
                        e.Handled = true;
                        return;
                    }

                    e.Handled = true;
                    recordingSpec = null;
                    CommitRowChord(row, capturedMods, capturedVk);
                };

                hotkeyRows.Add(row);
            }

            resetHotkeysBtn.Click += (_, _) =>
            {
                recordingSpec = null;
                RadioHotkeyCatalog.ResetAllToDefaults(settings);
                SyncHotkeyRowStates();
            };

            void SyncFromSettings()
            {
                isSyncing = true;
                autoplayToggle.IsOn = settings.GetOption(AutoplayOptionKey, false);

                var configuredBuffer = AudioBufferSizing.NormalizeSeconds(
                    settings.GetOption(BufferSecondsOptionKey, AudioBufferSizing.DefaultBufferSeconds));
                bufferCombo.SelectedIndex = configuredBuffer switch
                {
                    <= 2 => 0,
                    <= 5 => 1,
                    <= 10 => 2,
                    <= 15 => 3,
                    _ => 4
                };

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

                var lookupEnabled = settings.GetOption(RadioOptions.TrackLookupEnabled, true);
                trackLookupToggle.IsOn = lookupEnabled;
                var selectedService = MusicLookupHelper.NormalizeServiceId(
                    settings.GetOption(RadioOptions.TrackLookupService, MusicLookupHelper.SpotifyId));
                for (var i = 0; i < trackLookupServiceCombo.Items.Count; i++)
                {
                    if (trackLookupServiceCombo.Items[i] is ComboBoxItem item &&
                        string.Equals(item.Tag as string, selectedService, StringComparison.OrdinalIgnoreCase))
                    {
                        trackLookupServiceCombo.SelectedIndex = i;
                        break;
                    }
                }
                trackLookupServiceCard.IsDimmed = !lookupEnabled;
                trackLookupServiceCombo.IsEnabled = lookupEnabled;

                isSyncing = false;

                SyncHotkeyRowStates();
            }

            autoplayToggle.Toggled += (_, _) =>
            {
                if (!isSyncing) settings.SetOption(AutoplayOptionKey, autoplayToggle.IsOn);
            };
            bufferCombo.SelectionChanged += (_, _) =>
            {
                if (!isSyncing && bufferCombo.SelectedItem is ComboBoxItem item && item.Tag is int seconds)
                {
                    settings.SetOption(BufferSecondsOptionKey, seconds);
                }
            };
            numbersToggle.Toggled += (_, _) =>
            {
                if (!isSyncing) settings.SetOption(ShowNumbersOptionKey, numbersToggle.IsOn);
            };
            hotkeysToggle.Toggled += (_, _) =>
            {
                if (!isSyncing)
                {
                    recordingSpec = null;
                    settings.Update(s => s.GlobalHotkeysEnabled = hotkeysToggle.IsOn);
                    SyncHotkeyRowStates();
                }
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
            trackLookupToggle.Toggled += (_, _) =>
            {
                if (!isSyncing)
                {
                    var enabled = trackLookupToggle.IsOn;
                    trackLookupServiceCard.IsDimmed = !enabled;
                    trackLookupServiceCombo.IsEnabled = enabled;
                    settings.SetOption(RadioOptions.TrackLookupEnabled, enabled);
                }
            };
            trackLookupServiceCombo.SelectionChanged += (_, _) =>
            {
                if (!isSyncing && trackLookupServiceCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                {
                    settings.SetOption(RadioOptions.TrackLookupService, MusicLookupHelper.NormalizeServiceId(tag));
                }
            };

            void RefreshText()
            {
                isSyncing = true;
                heading.Text = localization["Radio.Settings.Heading"];

                autoplayCard.Title = localization["Radio.Settings.Autoplay.Title"];
                autoplayCard.Description = localization["Radio.Settings.Autoplay.Desc"];
                AutomationProperties.SetName(autoplayToggle, localization["Radio.Settings.Autoplay.Title"]);

                bufferCard.Title = localization["Radio.Settings.Buffer.Title"];
                bufferCard.Description = localization["Radio.Settings.Buffer.Desc"];
                buffer2sItem.Content = localization["Radio.Settings.Buffer.2s"];
                buffer5sItem.Content = localization["Radio.Settings.Buffer.5s"];
                buffer10sItem.Content = localization["Radio.Settings.Buffer.10s"];
                buffer15sItem.Content = localization["Radio.Settings.Buffer.15s"];
                buffer30sItem.Content = localization["Radio.Settings.Buffer.30s"];
                AutomationProperties.SetName(bufferCombo, localization["Radio.Settings.Buffer.Title"]);

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

                trackLookupCard.Title = localization["Radio.Settings.TrackLookup.Title"];
                trackLookupCard.Description = localization["Radio.Settings.TrackLookup.Desc"];
                AutomationProperties.SetName(trackLookupToggle, localization["Radio.Settings.TrackLookup.Title"]);

                trackLookupServiceCard.Title = localization["Radio.Settings.TrackLookupService.Title"];
                trackLookupServiceCard.Description = localization["Radio.Settings.TrackLookupService.Desc"];
                AutomationProperties.SetName(trackLookupServiceCombo, localization["Radio.Settings.TrackLookupService.Title"]);

                hotkeysCard.Title = localization["Radio.Settings.Hotkeys.Title"];
                hotkeysCard.Description = localization["Radio.Settings.Hotkeys.Desc"];
                AutomationProperties.SetName(hotkeysToggle, localization["Radio.Settings.Hotkeys.Title"]);

                hotkeysSubheading.Text = localization["Radio.Settings.Hotkeys.CustomizeHeading"];
                resetHotkeysBtn.Content = localization["Radio.Settings.Hotkeys.ResetDefaults"];
                AutomationProperties.SetName(resetHotkeysBtn, localization["Radio.Settings.Hotkeys.ResetDefaults"]);

                foreach (var row in hotkeyRows)
                {
                    var actionTitle = localization[row.Spec.TitleLocalizationKey];
                    row.Card.Title = actionTitle;
                    AutomationProperties.SetName(row.CtrlBtn, $"{actionTitle} Ctrl");
                    AutomationProperties.SetName(row.AltBtn, $"{actionTitle} Alt");
                    AutomationProperties.SetName(row.ShiftBtn, $"{actionTitle} Shift");
                    AutomationProperties.SetName(row.KeyCombo, $"{actionTitle} {localization["Radio.Settings.Hotkey.KeyLabel"]}");
                    AutomationProperties.SetName(row.RecordBtn, localization["Radio.Settings.Hotkey.RecordTooltip"]);
                    ToolTipService.SetToolTip(row.RecordBtn, localization["Radio.Settings.Hotkey.RecordTooltip"]);
                }

                isSyncing = false;
                SyncHotkeyRowStates();
            }

            void RefreshTheme()
            {
                var primary = theme.GetTextPrimaryBrush();
                var secondary = theme.GetTextSecondaryBrush();
                heading.Foreground = primary;
                hotkeysSubheading.Foreground = secondary;
                SyncHotkeyRowStates();
            }

            void OnLanguageChanged(string _) => RefreshText();

            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(heading);
            panel.Children.Add(autoplayCard);
            panel.Children.Add(bufferCard);
            panel.Children.Add(viewModeCard);
            panel.Children.Add(numbersCard);
            panel.Children.Add(alignCard);
            panel.Children.Add(sizeCard);
            panel.Children.Add(trackLookupCard);
            panel.Children.Add(trackLookupServiceCard);
            panel.Children.Add(hotkeysCard);
            panel.Children.Add(hotkeysHeaderRow);
            foreach (var row in hotkeyRows)
            {
                panel.Children.Add(row.Card);
            }

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
                recordingSpec = null;
            };

            return panel;
        }

        private static HotkeyModifiers ReadCurrentModifiers()
        {
            var mods = HotkeyModifiers.None;
            if (IsKeyDown(VirtualKey.Control)) mods |= HotkeyModifiers.Control;
            if (IsKeyDown(VirtualKey.Menu)) mods |= HotkeyModifiers.Alt;
            if (IsKeyDown(VirtualKey.Shift)) mods |= HotkeyModifiers.Shift;
            return mods;
        }

        private static bool IsKeyDown(VirtualKey key)
            => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

        private static uint MapCapturedVirtualKey(VirtualKey key) => key switch
        {
            >= VirtualKey.A and <= VirtualKey.Z => (uint)key,
            >= VirtualKey.Number0 and <= VirtualKey.Number9 => (uint)key,
            >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9 => (uint)('0' + (key - VirtualKey.NumberPad0)),
            VirtualKey.Space => 0x20u,
            _ => 0u
        };
    }
}

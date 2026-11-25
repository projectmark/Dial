namespace Dial.Modules.Radio;

using Chassis.Core.Hotkeys;
using Chassis.Core.Settings;

/// <summary>
/// Centralized module option keys stored inside <c>AppSettings.ModuleOptions</c>.
/// </summary>
public static class RadioOptions
{
    public const string LastStationId = "radio.last_station_id";
    public const string Volume = "radio.volume";
    public const string Muted = "radio.muted";
    public const string BufferSeconds = "radio.buffer_seconds";
    public const string AutoplayOnStart = "radio.autoplay_on_start";
    public const string ViewMode = "radio.view_mode";
    public const string ShowPositionNumbers = "radio.show_position_numbers";
    public const string ListAlignment = "radio.list_alignment";
    public const string ListFontSize = "radio.list_font_size";
    public const string MiniAlwaysOnTop = "radio.mini_always_on_top";
    public const string MinimizeToTray = "radio.minimize_to_tray";
    public const string HotkeyPlayPause = "radio.hotkey.play_pause";
    public const string HotkeyNextStation = "radio.hotkey.next";
    public const string HotkeyPreviousStation = "radio.hotkey.prev";
    public const string HotkeyMute = "radio.hotkey.mute";
    public const string DiscoverMinBitrate = "radio.discover_min_bitrate";
    public const string TrackLookupEnabled = "radio.track_lookup_enabled";
    public const string TrackLookupService = "radio.track_lookup_service";
}

/// <summary>
/// Builds search URLs to open the currently playing track in a user-selected streaming service.
/// </summary>
public static class MusicLookupHelper
{
    public const string SpotifyId = "spotify";
    public const string YouTubeMusicId = "youtube_music";
    public const string DeezerId = "deezer";
    public const string TidalId = "tidal";

    public static IReadOnlyList<(string Id, string DisplayName)> Services { get; } = ((string Id, string DisplayName)[])
    [
        (SpotifyId, "Spotify"),
        (YouTubeMusicId, "YouTube Music"),
        (DeezerId, "Deezer"),
        (TidalId, "Tidal")
    ];

    public static string NormalizeServiceId(string? serviceId)
    {
        if (string.IsNullOrWhiteSpace(serviceId))
        {
            return SpotifyId;
        }

        return serviceId.Trim().ToLowerInvariant() switch
        {
            YouTubeMusicId => YouTubeMusicId,
            DeezerId => DeezerId,
            TidalId => TidalId,
            _ => SpotifyId
        };
    }

    public static string GetDisplayName(string? serviceId)
        => NormalizeServiceId(serviceId) switch
        {
            YouTubeMusicId => "YouTube Music",
            DeezerId => "Deezer",
            TidalId => "Tidal",
            _ => "Spotify"
        };

    public static string? BuildSearchUrl(string? serviceId, string? trackTitle)
    {
        if (string.IsNullOrWhiteSpace(trackTitle))
        {
            return null;
        }

        var normalizedQuery = string.Join(
            ' ',
            trackTitle.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalizedQuery.Length == 0)
        {
            return null;
        }

        var encoded = Uri.EscapeDataString(normalizedQuery);
        return NormalizeServiceId(serviceId) switch
        {
            YouTubeMusicId => $"https://music.youtube.com/search?q={encoded}",
            DeezerId => $"https://www.deezer.com/search/{encoded}",
            TidalId => $"https://listen.tidal.com/search?q={encoded}",
            _ => $"https://open.spotify.com/search/{encoded}"
        };
    }
}

/// <summary>
/// Rotating startup catchphrases and titlebar live-status formatting for Dial.
/// </summary>
public static class DialCatchphrases
{
    public const string RareVibesCatchphrase = "100% Vibes, 100% Radio, 100% Dialed in!";

    private static readonly string[] StandardCatchphrases =
    [
        "Dialed in on every station.",
        "Live radio, dialed in.",
        "From airwaves to streams, dialed in.",
        "Every broadcast, dialed in.",
        "Always dialed in."
    ];

    public static IReadOnlyList<string> All { get; } =
        (string[])[.. StandardCatchphrases, RareVibesCatchphrase];

    /// <summary>
    /// Weighted rotation where each standard catchphrase appears twice and <see cref="RareVibesCatchphrase"/>
    /// appears once (half as often), arranged with no consecutive repeats even across wrap-around.
    /// </summary>
    public static IReadOnlyList<string> WeightedRotation { get; } =
        (string[])[.. StandardCatchphrases, RareVibesCatchphrase, .. StandardCatchphrases];

    public static string PickRandom(Random? random = null)
    {
        var rng = random ?? Random.Shared;
        return WeightedRotation[rng.Next(WeightedRotation.Count)];
    }

    public static string? FormatLiveTitleBarStatus(RadioStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.ActiveStation is null ||
            snapshot.State is not (RadioPlaybackState.Playing or RadioPlaybackState.Buffering))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(snapshot.NowPlaying)
            ? snapshot.ActiveStation.Name
            : $"{snapshot.ActiveStation.Name} — {snapshot.NowPlaying.Trim()}";
    }
}

/// <summary>
/// Canonical hotkey identifiers and default key chords for the Dial radio module.
/// </summary>
public static class RadioHotkeyDefaults
{
    public const string PlayPauseId = "dial.play_pause";
    public const string NextId = "dial.next";
    public const string PrevId = "dial.prev";
    public const string MuteId = "dial.mute";
    public const string ToggleWindowId = "dial.toggle_window";

    public const string PlayPause = "Ctrl+Alt+P";
    public const string Next = "Ctrl+Alt+N";
    public const string Prev = "Ctrl+Alt+B";
    public const string Mute = "Ctrl+Alt+M";
    public const string ToggleWindow = "Ctrl+Shift+Space";
}

/// <summary>
/// Declarative descriptor for a configurable global hotkey action in Dial.
/// </summary>
public sealed record RadioHotkeySpec(
    string Id,
    string Glyph,
    string TitleLocalizationKey,
    string? ModuleOptionKey,
    string DefaultChord);

/// <summary>
/// Shared catalog and persistence helpers for customizable Dial global hotkeys.
/// </summary>
public static class RadioHotkeyCatalog
{
    public static IReadOnlyList<RadioHotkeySpec> Actions { get; } = (RadioHotkeySpec[])
    [
        new(RadioHotkeyDefaults.PlayPauseId, "\uE768", "Radio.Settings.Hotkey.PlayPause", RadioOptions.HotkeyPlayPause, RadioHotkeyDefaults.PlayPause),
        new(RadioHotkeyDefaults.NextId, "\uE893", "Radio.Settings.Hotkey.Next", RadioOptions.HotkeyNextStation, RadioHotkeyDefaults.Next),
        new(RadioHotkeyDefaults.PrevId, "\uE892", "Radio.Settings.Hotkey.Prev", RadioOptions.HotkeyPreviousStation, RadioHotkeyDefaults.Prev),
        new(RadioHotkeyDefaults.MuteId, "\uE767", "Radio.Settings.Hotkey.Mute", RadioOptions.HotkeyMute, RadioHotkeyDefaults.Mute),
        new(RadioHotkeyDefaults.ToggleWindowId, "\uE8A7", "Radio.Settings.Hotkey.ToggleWindow", null, RadioHotkeyDefaults.ToggleWindow)
    ];

    public static string GetConfiguredChord(ISettingsService settings, RadioHotkeySpec spec)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(spec);

        var raw = spec.ModuleOptionKey is null
            ? settings.Current.ToggleWindowHotkey
            : settings.GetOption(spec.ModuleOptionKey, spec.DefaultChord);

        if (HotkeyService.TryParseHotkeyString(raw, out var mods, out var vk) &&
            HotkeyService.TryFormatNormalizedHotkey(mods, vk, requireModifier: true, out var normalized))
        {
            return normalized;
        }

        return spec.DefaultChord;
    }

    public static void SetConfiguredChord(ISettingsService settings, RadioHotkeySpec spec, string chord)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(spec);

        if (!HotkeyService.TryParseHotkeyString(chord, out var mods, out var vk) ||
            !HotkeyService.TryFormatNormalizedHotkey(mods, vk, requireModifier: true, out var normalized))
        {
            return;
        }

        if (spec.ModuleOptionKey is null)
        {
            settings.Update(s => s.ToggleWindowHotkey = normalized);
        }
        else
        {
            settings.SetOption(spec.ModuleOptionKey, normalized);
        }
    }

    public static void ResetAllToDefaults(ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        foreach (var spec in Actions)
        {
            if (spec.ModuleOptionKey is not null)
            {
                settings.SetOption(spec.ModuleOptionKey, spec.DefaultChord, autoSave: false);
            }
        }

        settings.Update(s => s.ToggleWindowHotkey = RadioHotkeyDefaults.ToggleWindow, autoSave: true);
    }

    public static bool AreAllDefaults(ISettingsService settings)
        => Actions.All(spec => string.Equals(GetConfiguredChord(settings, spec), spec.DefaultChord, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Represents a radio channel and its live stream endpoints.
/// </summary>
public sealed record RadioStation(
    string Id,
    string Name,
    string Tagline,
    string StreamUrl,
    string FallbackStreamUrl,
    string IconGlyph,
    string AccentHex,
    int BitrateKbps,
    string CountryCode = "NL",
    string Codec = "MP3",
    string Tags = "");

/// <summary>
/// Current playback lifecycle state for the radio player.
/// </summary>
public enum RadioPlaybackState
{
    Stopped = 0,
    Buffering = 1,
    Playing = 2,
    Error = 3
}

/// <summary>
/// Immutable snapshot of the current radio playback state.
/// </summary>
public sealed record RadioStateSnapshot(
    RadioStation? ActiveStation,
    RadioPlaybackState State,
    string? NowPlaying,
    double Volume,
    bool IsMuted,
    string? ErrorMessage,
    bool IsPreview = false,
    double BufferedSeconds = 0,
    int TargetBufferSeconds = 5);

/// <summary>
/// Country entry returned by the global station directory.
/// </summary>
public sealed record RadioBrowserCountry(string Name, string Code, int StationCount);

/// <summary>
/// Accumulated listening statistics per station stored in %AppData%/Dial/data/.
/// </summary>
public sealed record StationListeningStat(
    string StationId,
    string StationName,
    long TotalSeconds,
    DateTimeOffset LastPlayedUtc);

/// <summary>
/// Pure collection helpers for multi-select reordering and bulk removal of favorite stations.
/// </summary>
public static class StationListOperations
{
    /// <summary>
    /// Moves all stations matching <paramref name="stationIds"/> (preserving their relative order)
    /// to <paramref name="targetInsertionIndex"/> in the original list.
    /// </summary>
    public static List<RadioStation> ReorderStations(
        IReadOnlyList<RadioStation> stations,
        IReadOnlyCollection<string> stationIds,
        int targetInsertionIndex)
    {
        ArgumentNullException.ThrowIfNull(stations);
        ArgumentNullException.ThrowIfNull(stationIds);

        if (stations.Count == 0 || stationIds.Count == 0)
        {
            return stations.ToList();
        }

        var idSet = new HashSet<string>(stationIds, StringComparer.OrdinalIgnoreCase);
        var moving = new List<RadioStation>();
        var remaining = new List<RadioStation>();
        var removedBeforeTarget = 0;

        for (var i = 0; i < stations.Count; i++)
        {
            var item = stations[i];
            if (idSet.Contains(item.Id))
            {
                moving.Add(item);
                if (i < targetInsertionIndex)
                {
                    removedBeforeTarget++;
                }
            }
            else
            {
                remaining.Add(item);
            }
        }

        if (moving.Count == 0)
        {
            return stations.ToList();
        }

        var insertAt = Math.Clamp(targetInsertionIndex - removedBeforeTarget, 0, remaining.Count);
        remaining.InsertRange(insertAt, moving);
        return remaining;
    }

    /// <summary>
    /// Removes all stations matching <paramref name="stationIds"/> while ensuring at least one station remains.
    /// </summary>
    public static List<RadioStation> RemoveStations(
        IReadOnlyList<RadioStation> stations,
        IReadOnlyCollection<string> stationIds)
    {
        ArgumentNullException.ThrowIfNull(stations);
        ArgumentNullException.ThrowIfNull(stationIds);

        if (stations.Count <= 1 || stationIds.Count == 0)
        {
            return stations.ToList();
        }

        var idSet = new HashSet<string>(stationIds, StringComparer.OrdinalIgnoreCase);
        var remaining = stations.Where(s => !idSet.Contains(s.Id)).ToList();
        return remaining.Count == 0 ? stations.ToList() : remaining;
    }
}

/// <summary>
/// Official KINK Radio station definitions seeded as default favorites.
/// </summary>
public static class KinkStationCatalog
{
    public static IReadOnlyList<RadioStation> DefaultStations { get; } = new RadioStation[]
    {
        new RadioStation(
            Id: "kink",
            Name: "KINK",
            Tagline: "No Alternative — Alternative Rock & Indie",
            StreamUrl: "https://playerservices.streamtheworld.com/api/livestream-redirect/KINK.mp3",
            FallbackStreamUrl: "https://playerservices.streamtheworld.com/api/livestream-redirect/KINKAAC.aac",
            IconGlyph: "\uE7F6",
            AccentHex: "#E81123",
            BitrateKbps: 192,
            CountryCode: "NL",
            Codec: "MP3",
            Tags: "alternative, indie, rock"),
        new RadioStation(
            Id: "kink-distortion",
            Name: "KINK Distortion",
            Tagline: "Metal, Hardrock & Metalcore",
            StreamUrl: "https://playerservices.streamtheworld.com/api/livestream-redirect/KINK_DISTORTION.mp3",
            FallbackStreamUrl: "https://playerservices.streamtheworld.com/api/livestream-redirect/KINK_DISTORTIONAAC.aac",
            IconGlyph: "\uE945",
            AccentHex: "#D83B01",
            BitrateKbps: 192,
            CountryCode: "NL",
            Codec: "MP3",
            Tags: "metal, hardrock, metalcore"),
        new RadioStation(
            Id: "kink-80s",
            Name: "KINK 80's",
            Tagline: "Alternative Classics uit de jaren 80",
            StreamUrl: "https://playerservices.streamtheworld.com/api/livestream-redirect/KINK_DNA.mp3",
            FallbackStreamUrl: "https://playerservices.streamtheworld.com/api/livestream-redirect/KINK_DNAAAC.aac",
            IconGlyph: "\uEC4F",
            AccentHex: "#8764B8",
            BitrateKbps: 192,
            CountryCode: "NL",
            Codec: "MP3",
            Tags: "80s, alternative, classics"),
        new RadioStation(
            Id: "kink-90s",
            Name: "KINK 90's",
            Tagline: "Alternative Rock uit de jaren 90",
            StreamUrl: "https://playerservices.streamtheworld.com/api/livestream-redirect/KINK_90S.mp3",
            FallbackStreamUrl: "https://playerservices.streamtheworld.com/api/livestream-redirect/KINK_90SAAC.aac",
            IconGlyph: "\uE8D6",
            AccentHex: "#00B7C3",
            BitrateKbps: 192,
            CountryCode: "NL",
            Codec: "MP3",
            Tags: "90s, grunge, alternative")
    };
}

namespace Dial.Modules.Radio;

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

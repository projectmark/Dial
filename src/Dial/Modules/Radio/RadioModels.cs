namespace Dial.Modules.Radio;

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
    bool IsPreview = false);

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

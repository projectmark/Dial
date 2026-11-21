using Chassis.Core.Data;
using Chassis.Core.Paths;
using Dial.Modules.Radio;
using Xunit;

namespace Chassis.Tests;

public sealed class DialRadioTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"DialRadioTests_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public void KinkStationCatalog_ContainsAllFourStationsWithValidStreamTheWorldUrls()
    {
        var stations = KinkStationCatalog.DefaultStations;
        Assert.Equal(4, stations.Count);

        var expectedIds = new[] { "kink", "kink-distortion", "kink-80s", "kink-90s" };
        Assert.Equal(expectedIds, stations.Select(s => s.Id));

        foreach (var station in stations)
        {
            Assert.False(string.IsNullOrWhiteSpace(station.Name));
            Assert.False(string.IsNullOrWhiteSpace(station.Tagline));
            Assert.False(string.IsNullOrWhiteSpace(station.IconGlyph));
            Assert.StartsWith("https://playerservices.streamtheworld.com/", station.StreamUrl, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("https://playerservices.streamtheworld.com/", station.FallbackStreamUrl, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("NL", station.CountryCode);
            Assert.Equal("MP3", station.Codec);
        }
    }

    [Theory]
    [InlineData("StreamTitle='Foo Fighters - Everlong';", "Foo Fighters - Everlong")]
    [InlineData("StreamTitle='Guns N' Roses - Sweet Child O' Mine';StreamUrl='https://kink.nl';", "Guns N' Roses - Sweet Child O' Mine")]
    [InlineData("StreamTitle=\"Pearl Jam - Alive\";", "Pearl Jam - Alive")]
    [InlineData("StreamTitle='';", null)]
    [InlineData("StreamTitle='   ';", null)]
    [InlineData("StreamUrl='https://kink.nl';", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void IcyMetadataReader_ParseStreamTitle_ExtractsExpectedTitle(string? raw, string? expected)
    {
        var parsed = IcyMetadataReader.ParseStreamTitle(raw);
        Assert.Equal(expected, parsed);
    }

    [Fact]
    public async Task JsonDataStore_PersistsAndReloadsStationsAtomically()
    {
        var paths = new PathService(_tempRoot);
        var store = new JsonDataStore<RadioStation>(paths);

        await store.SaveAllAsync("stations", KinkStationCatalog.DefaultStations);
        var reloaded = await store.LoadAllAsync("stations");

        Assert.Equal(4, reloaded.Count);
        Assert.Equal(KinkStationCatalog.DefaultStations.Select(s => s.Id), reloaded.Select(s => s.Id));
        Assert.Equal(KinkStationCatalog.DefaultStations.Select(s => s.StreamUrl), reloaded.Select(s => s.StreamUrl));
    }

    [Fact]
    public void RadioBrowserService_ParsesAndFiltersStationsAndCountries()
    {
        const string stationsJson = """
            [
              {
                "stationuuid": "abc-123",
                "name": "KINK Test",
                "url": "http://example.invalid/raw.mp3",
                "url_resolved": "https://stream.example.invalid/live.mp3",
                "countrycode": "nl",
                "codec": "mp3",
                "bitrate": 192,
                "tags": "rock, alternative, indie, metal, extra"
              },
              {
                "stationuuid": "dup-123",
                "name": "Duplicate Stream",
                "url_resolved": "https://stream.example.invalid/live.mp3",
                "countrycode": "NL",
                "codec": "MP3",
                "bitrate": 128
              },
              {
                "stationuuid": "bad-url",
                "name": "Invalid Scheme",
                "url_resolved": "ftp://invalid.example/stream"
              }
            ]
            """;

        var stations = RadioBrowserService.ParseStationsJson(stationsJson);
        Assert.Single(stations);
        Assert.Equal("rb-abc-123", stations[0].Id);
        Assert.Equal("KINK Test", stations[0].Name);
        Assert.Equal("https://stream.example.invalid/live.mp3", stations[0].StreamUrl);
        Assert.Equal("http://example.invalid/raw.mp3", stations[0].FallbackStreamUrl);
        Assert.Equal("NL", stations[0].CountryCode);
        Assert.Equal("MP3", stations[0].Codec);
        Assert.Equal(192, stations[0].BitrateKbps);
        Assert.Equal("rock, alternative, indie, metal", stations[0].Tags);

        const string countriesJson = """
            [
              { "name": "The Netherlands", "iso_3166_1": "NL", "stationcount": 420 },
              { "name": "Belgium", "iso_3166_1": "be", "stationcount": 210 },
              { "name": "Empty Land", "iso_3166_1": "XX", "stationcount": 0 }
            ]
            """;

        var countries = RadioBrowserService.ParseCountriesJson(countriesJson);
        Assert.Equal(2, countries.Count);
        Assert.Equal("BE", countries[0].Code);
        Assert.Equal("NL", countries[1].Code);
    }

    [Fact]
    public void SleepTimerService_ClampsDurationAndCancelsCleanly()
    {
        using var timer = new SleepTimerService();
        TimeSpan? lastNotified = null;
        timer.RemainingChanged += rem => lastNotified = rem;

        timer.Start(TimeSpan.FromSeconds(10));
        Assert.True(timer.IsActive);
        Assert.True(lastNotified >= SleepTimerService.MinDuration);

        timer.Start(TimeSpan.FromHours(12));
        Assert.True(lastNotified <= SleepTimerService.MaxDuration);

        timer.Cancel();
        Assert.False(timer.IsActive);
        Assert.Null(lastNotified);
    }

    [Fact]
    public async Task ListeningStatsService_TracksSessionAndPersistsStats()
    {
        var paths = new PathService(_tempRoot);
        var store = new JsonDataStore<StationListeningStat>(paths);
        using var stats = new ListeningStatsService(store);
        await stats.InitializeAsync();

        var kink = KinkStationCatalog.DefaultStations[0];
        stats.OnPlaybackStateChanged(new RadioStateSnapshot(kink, RadioPlaybackState.Playing, null, 0.8, false, null));

        await Task.Delay(1300);

        stats.OnPlaybackStateChanged(new RadioStateSnapshot(kink, RadioPlaybackState.Stopped, null, 0.8, false, null));
        await stats.FlushAsync();

        var allStats = stats.GetStats();
        Assert.Single(allStats);
        Assert.Equal("kink", allStats[0].StationId);
        Assert.True(allStats[0].TotalSeconds >= 1);

        using var reloaded = new ListeningStatsService(store);
        await reloaded.InitializeAsync();
        Assert.Single(reloaded.GetStats());
        Assert.True(reloaded.GetStats()[0].TotalSeconds >= 1);
    }

    [Fact]
    public async Task PackageShareService_RoundTripsFavoritesWithDialPrefix()
    {
        var share = new PackageShareService<List<RadioStation>>("Dial");
        var favorites = KinkStationCatalog.DefaultStations.ToList();

        var code = share.ExportToShareCode(favorites);
        Assert.StartsWith("DIAL-v1:", code);

        var fromCode = share.ImportFromShareCode(code);
        Assert.Equal(4, fromCode.Count);
        Assert.Equal(favorites.Select(s => s.StreamUrl), fromCode.Select(s => s.StreamUrl));

        Directory.CreateDirectory(_tempRoot);
        var filePath = Path.Combine(_tempRoot, "favorites.chassispack");
        await share.ExportToFileAsync(favorites, filePath);
        var fromFile = await share.ImportFromFileAsync(filePath);
        Assert.Equal(4, fromFile.Count);
    }

    [Fact]
    public void AudioMeterService_ToVuLevel_MapsLogarithmicRangeCorrectly()
    {
        Assert.Equal(0f, AudioMeterService.ToVuLevel(0f));
        Assert.Equal(0f, AudioMeterService.ToVuLevel(0.0001f));
        Assert.Equal(1f, AudioMeterService.ToVuLevel(1f));

        var low = AudioMeterService.ToVuLevel(0.05f);
        var mid = AudioMeterService.ToVuLevel(0.25f);
        var high = AudioMeterService.ToVuLevel(0.75f);
        Assert.True(low > 0f && low < mid && mid < high && high < 1f);
    }
}

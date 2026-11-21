using System.Net;
using System.Text;
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

    [Fact]
    public void RadioBackupImporter_ParsesRadiOhBackupPreservingCustomNameSortingAndDeduplication()
    {
        const string radiOhJson = """
            [
              {
                "id": "second-id",
                "manualSortingId": 15,
                "name": "Radio Luxembourg 208",
                "url": "https:\/\/stream.zeno.fm\/3384cgvyk2zuv",
                "isPlaying": false,
                "faviconUrl": ""
              },
              {
                "id": "first-id",
                "manualSortingId": 0,
                "name": "Laser 558 Classic",
                "url": "https:\/\/s6.autopo.st\/proxy\/neffjohd?mp=\/stream",
                "isPlaying": false,
                "faviconUrl": "",
                "customName": "Laser 558 Classic #1"
              },
              {
                "id": "dup-id",
                "manualSortingId": 14,
                "name": "Radio Luxembourg Memories ",
                "url": "https:\/\/stream.zeno.fm\/3384cgvyk2zuv",
                "isPlaying": false,
                "faviconUrl": ""
              }
            ]
            """;

        var share = new PackageShareService<List<RadioStation>>("Dial");
        var imported = RadioBackupImporter.ParseAny(radiOhJson, share);

        Assert.Equal(2, imported.Count);
        Assert.Equal("Laser 558 Classic #1", imported[0].Name);
        Assert.Equal("https://s6.autopo.st/proxy/neffjohd?mp=/stream", imported[0].StreamUrl);
        Assert.Equal("Laser 558 Classic", imported[0].Tagline);

        Assert.Equal("Radio Luxembourg Memories", imported[1].Name);
        Assert.Equal("https://stream.zeno.fm/3384cgvyk2zuv", imported[1].StreamUrl);

        // Also ensure DIAL-v1 share codes still auto-detect cleanly
        var code = share.ExportToShareCode(KinkStationCatalog.DefaultStations.ToList());
        var fromCode = RadioBackupImporter.ParseAny(code, share);
        Assert.Equal(4, fromCode.Count);
    }

    [Fact]
    public void StationListOperations_ReordersMultipleSelectedAndRemovesSafely()
    {
        var items = new[] { "A", "B", "C", "D", "E" }
            .Select(id => new RadioStation(id, id, string.Empty, $"https://example.invalid/{id}", string.Empty, "\uE7F6", "#0078D4", 192))
            .ToList();

        // Move B and D before C (target index 2) -> [A, B, D, C, E]
        var grouped = StationListOperations.ReorderStations(items, ["B", "D"], 2);
        Assert.Equal(new[] { "A", "B", "D", "C", "E" }, grouped.Select(s => s.Id));

        // Move A and B to the end (target index 5) -> [C, D, E, A, B]
        var movedEnd = StationListOperations.ReorderStations(items, ["A", "B"], 5);
        Assert.Equal(new[] { "C", "D", "E", "A", "B" }, movedEnd.Select(s => s.Id));

        // Move C and E to the top (target index 0) -> [C, E, A, B, D]
        var movedTop = StationListOperations.ReorderStations(items, ["C", "E"], 0);
        Assert.Equal(new[] { "C", "E", "A", "B", "D" }, movedTop.Select(s => s.Id));

        // Bulk remove B and D -> [A, C, E]
        var removed = StationListOperations.RemoveStations(items, ["B", "D"]);
        Assert.Equal(new[] { "A", "C", "E" }, removed.Select(s => s.Id));

        // Guard: attempting to remove all items leaves list untouched (>=1 must remain)
        var guardAll = StationListOperations.RemoveStations(items, ["A", "B", "C", "D", "E"]);
        Assert.Equal(5, guardAll.Count);
    }

    [Fact]
    public void AudioBufferSizing_ScalesWithSecondsAndBitrate()
    {
        Assert.Equal(192, AudioBufferSizing.EstimateBitrateKbps(null));
        Assert.Equal(128, AudioBufferSizing.EstimateBitrateKbps("128, 128"));
        Assert.Equal(1411, AudioBufferSizing.EstimateBitrateKbps("9999"));

        var cap5s = AudioBufferSizing.CalculateCapacityBytes(5, 192);
        var cap30s = AudioBufferSizing.CalculateCapacityBytes(30, 192);
        Assert.Equal(120_000, cap5s);
        Assert.Equal(720_000, cap30s);

        Assert.True(AudioBufferSizing.ShouldBypassProxy("https://example.invalid/live/master.m3u8"));
        Assert.False(AudioBufferSizing.ShouldBypassProxy("https://playerservices.streamtheworld.com/api/livestream-redirect/KINKAAC.aac"));
    }

    [Fact]
    public void IcyStreamDemuxer_StripsMetadataAcrossChunkBoundariesWithoutAudioLoss()
    {
        const int metaInterval = 16;
        var audioA = "0123456789ABCDEF"u8.ToArray();
        var audioB = "ghijklmnopqrstuv"u8.ToArray();
        var metaPayload = Encoding.UTF8.GetBytes("StreamTitle='De Staat - Kitty';");
        var metaUnits = (int)Math.Ceiling(metaPayload.Length / 16.0);
        var paddedMeta = new byte[metaUnits * 16];
        Array.Copy(metaPayload, paddedMeta, metaPayload.Length);

        var combined = new List<byte>();
        combined.AddRange(audioA);
        combined.Add((byte)metaUnits);
        combined.AddRange(paddedMeta);
        combined.AddRange(audioB);
        combined.Add(0); // zero-length ICY heartbeat block

        var demuxer = new IcyStreamDemuxer(metaInterval);
        var extractedAudio = new List<byte>();
        var scratch = new byte[64];
        string? detectedTitle = null;

        // Feed irregular 5-byte TCP chunks to verify split-boundary handling
        for (var i = 0; i < combined.Count; i += 5)
        {
            var chunkLen = Math.Min(5, combined.Count - i);
            var span = combined.GetRange(i, chunkLen).ToArray();
            var written = demuxer.Process(span, scratch, title => detectedTitle = title);
            if (written > 0)
            {
                extractedAudio.AddRange(scratch[..written]);
            }
        }

        Assert.Equal("De Staat - Kitty", detectedTitle);
        Assert.Equal("0123456789ABCDEFghijklmnopqrstuv", Encoding.ASCII.GetString(extractedAudio.ToArray()));
    }

    [Fact]
    public async Task AudioRingBuffer_SupportsLiveCapacityExpansionWithoutDroppingData()
    {
        var ring = new AudioRingBuffer(16_384);
        var first = Enumerable.Range(0, 12_000).Select(i => (byte)(i & 0xFF)).ToArray();
        var second = Enumerable.Range(0, 10_000).Select(i => (byte)((i + 17) & 0xFF)).ToArray();

        await ring.WriteAsync(first, CancellationToken.None);
        ring.SetCapacity(32_768);
        await ring.WriteAsync(second, CancellationToken.None);
        ring.Complete();

        var drained = new MemoryStream();
        var buf = new byte[2048];
        while (true)
        {
            var n = await ring.ReadAsync(buf, CancellationToken.None);
            if (n == 0) break;
            drained.Write(buf, 0, n);
        }

        Assert.Equal(first.Concat(second).ToArray(), drained.ToArray());
    }

    [Fact]
    public async Task BufferedStreamProxy_DemuxesIcyAndServesCleanHttpOverLoopback()
    {
        const int metaInterval = 32;
        var audioPart1 = Enumerable.Repeat((byte)'A', 32).ToArray();
        var audioPart2 = Enumerable.Repeat((byte)'B', 32).ToArray();
        var metaBytes = new byte[32];
        Encoding.UTF8.GetBytes("StreamTitle='KINK - Test Track';").CopyTo(metaBytes, 0);

        var rawStreamBytes = new MemoryStream();
        rawStreamBytes.Write(audioPart1);
        rawStreamBytes.WriteByte(2); // 2 * 16 = 32 metadata bytes
        rawStreamBytes.Write(metaBytes);
        rawStreamBytes.Write(audioPart2);
        var payloadBytes = rawStreamBytes.ToArray();

        using var fakeUpstreamClient = new HttpClient(new StubStreamHandler(payloadBytes, metaInterval));
        using var proxy = new BufferedStreamProxy("https://radio.example.invalid/stream.mp3", 10, fakeUpstreamClient);

        string? receivedTitle = null;
        proxy.NowPlayingChanged += t => receivedTitle = t;

        var result = await proxy.StartAsync();
        Assert.Equal(BufferedProxyStartStatus.Started, result.Status);
        Assert.NotNull(result.LocalUri);
        Assert.Equal("127.0.0.1", result.LocalUri!.Host);

        using var playerClient = new HttpClient();

        // HEAD probe must succeed without draining any buffered audio bytes
        using var headResp = await playerClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, result.LocalUri));
        Assert.Equal(HttpStatusCode.OK, headResp.StatusCode);

        // GET stream receives exact clean audio frames stripped of ICY metadata
        var streamedAudio = await playerClient.GetByteArrayAsync(result.LocalUri);
        Assert.Equal(audioPart1.Concat(audioPart2).ToArray(), streamedAudio);
        Assert.Equal("KINK - Test Track", receivedTitle);
    }

    private sealed class StubStreamHandler(byte[] payload, int metaInterval) : HttpMessageHandler
    {
        private int _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) > 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Gone));
            }

            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
            resp.Content.Headers.TryAddWithoutValidation("Content-Type", "audio/mpeg");
            resp.Headers.TryAddWithoutValidation("icy-br", "192");
            resp.Headers.TryAddWithoutValidation("icy-metaint", metaInterval.ToString());
            return Task.FromResult(resp);
        }
    }
}

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
        Assert.True(AudioBufferSizing.CalculateInitialPrebufferBytes(15, 192) > AudioBufferSizing.CalculateInitialPrebufferBytes(5, 192));

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

    [Fact]
    public void AudioFrameParser_SkipsId3v2AndExtractsCompleteMp3AndAdtsFrames()
    {
        // ID3v2 header ("ID3", v2.3, flags 0, syncsafe size 12) + 12 bytes ID3 body = 22 bytes total
        var streamData = new List<byte>
        {
            (byte)'I', (byte)'D', (byte)'3', 3, 0, 0, 0, 0, 0, 12
        };
        streamData.AddRange(Enumerable.Repeat((byte)0x55, 12));
        // Random leading junk before valid frame sync (must be skipped cleanly)
        streamData.AddRange(new byte[] { 0x01, 0xFF, 0x00, 0x42 });

        // MPEG1 Layer III, 192 kbps (idx 11 => 0xB0), 44100 Hz (idx 0 => 0x00), no pad => frameLen = 626
        static byte[] MakeMp3Frame(byte fill)
        {
            var f = new byte[626];
            f[0] = 0xFF;
            f[1] = 0xFB;
            f[2] = 0xB0;
            f[3] = 0x00;
            Array.Fill(f, fill, 4, f.Length - 4);
            return f;
        }

        var mp3StartOffset = streamData.Count;
        streamData.AddRange(MakeMp3Frame(0xAA));
        streamData.AddRange(MakeMp3Frame(0xBB));
        // Add incomplete trailing third frame (100 bytes) simulating sudden TCP drop
        streamData.AddRange(MakeMp3Frame(0xCC).Take(100));

        var detected = AudioFrameParser.TryDetectFormat(streamData.ToArray(), "audio/mpeg", 192, out var format, out var firstOff);
        Assert.True(detected);
        Assert.Equal(AudioCodecKind.Mp3, format.Codec);
        Assert.Equal(44100, format.SampleRate);
        Assert.Equal(2, format.Channels);
        Assert.Equal(mp3StartOffset, firstOff);

        var accumulator = new AudioFrameAccumulator(AudioCodecKind.Mp3);
        var extracted = new List<AudioFrame>();
        var raw = streamData.ToArray();
        for (var i = 0; i < raw.Length; i += 37)
        {
            var len = Math.Min(37, raw.Length - i);
            accumulator.Append(raw.AsSpan(i, len));
            accumulator.ExtractFrames(extracted);
        }

        Assert.Equal(2, extracted.Count);
        Assert.Equal(626, extracted[0].Payload.Length);
        Assert.Equal(0xAA, extracted[0].Payload[4]);
        Assert.Equal(0xBB, extracted[1].Payload[4]);

        // Reconnect: discarding incomplete 100-byte fragment allows immediate clean sync on new frames
        accumulator.ResetForNewConnection();
        var afterReconnect = MakeMp3Frame(0xDD).Concat(MakeMp3Frame(0xEE)).ToArray();
        accumulator.Append(afterReconnect);
        extracted.Clear();
        accumulator.ExtractFrames(extracted);
        Assert.Equal(2, extracted.Count);
        Assert.Equal(0xDD, extracted[0].Payload[4]);

        // Verify ADTS AAC parsing (44100 Hz srIdx=4, chCfg=2, frameLen=128)
        static byte[] MakeAdtsFrame(int len, byte fill)
        {
            var f = new byte[len];
            f[0] = 0xFF;
            f[1] = 0xF1; // MPEG-4 ADTS, Layer 0
            f[2] = (byte)(0x40 | (4 << 2)); // AAC-LC, 44100Hz, ch bit0=0
            f[3] = (byte)((2 << 6) | ((len >> 11) & 0x03)); // ch=2
            f[4] = (byte)((len >> 3) & 0xFF);
            f[5] = (byte)(((len & 0x07) << 5) | 0x1F);
            f[6] = 0xFC; // 1 raw data block
            Array.Fill(f, fill, 7, len - 7);
            return f;
        }

        var adtsBytes = MakeAdtsFrame(128, 0x11).Concat(MakeAdtsFrame(128, 0x22)).ToArray();
        Assert.True(AudioFrameParser.TryDetectFormat(adtsBytes, "audio/aac", 128, out var aacFmt, out _));
        Assert.Equal(AudioCodecKind.AacAdts, aacFmt.Codec);
        Assert.Equal(44100, aacFmt.SampleRate);
        Assert.Equal(2, aacFmt.Channels);
    }

    [Fact]
    public async Task TimedAudioFrameQueue_EnforcesTargetPrebufferAndRebufferHysteresis()
    {
        var queue = new TimedAudioFrameQueue(2); // 2s startup prebuffer, 1.5s rebuffer refill
        var bufferingEvents = new List<bool>();
        queue.BufferingChanged += b => bufferingEvents.Add(b);

        var frameDur = TimeSpan.FromMilliseconds(500);
        AudioFrame MakeFrame(byte id) => new([id], frameDur, 44100, 2, 192);

        // 3 frames = 1.5s (< 2.0s target) -> initial prebuffer wait must still be pending
        await queue.EnqueueAsync(MakeFrame(1), CancellationToken.None);
        await queue.EnqueueAsync(MakeFrame(2), CancellationToken.None);
        await queue.EnqueueAsync(MakeFrame(3), CancellationToken.None);

        var prebufferTask = queue.WaitForInitialPrebufferAsync(CancellationToken.None);
        await Task.Delay(30);
        Assert.False(prebufferTask.IsCompleted);

        // Frame #4 hits 2.0s -> releases initial prebuffer
        await queue.EnqueueAsync(MakeFrame(4), CancellationToken.None);
        await prebufferTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(2), queue.BufferedDuration);

        // Drain all 4 frames -> next Dequeue triggers rebuffer hysteresis until 1.5s refills
        for (byte expected = 1; expected <= 4; expected++)
        {
            var f = await queue.DequeueAsync(CancellationToken.None);
            Assert.NotNull(f);
            Assert.Equal(expected, f!.Payload[0]);
        }

        var dequeueTask = queue.DequeueAsync(CancellationToken.None).AsTask();
        await Task.Delay(30);
        Assert.False(dequeueTask.IsCompleted);
        Assert.Equal([true], bufferingEvents);

        // 2 frames (1.0s < 1.5s rebuffer threshold) must still hold playback cleanly
        await queue.EnqueueAsync(MakeFrame(5), CancellationToken.None);
        await queue.EnqueueAsync(MakeFrame(6), CancellationToken.None);
        await Task.Delay(30);
        Assert.False(dequeueTask.IsCompleted);

        // 3rd frame hits 1.5s -> rebuffer ends and dequeue delivers frame #5 cleanly
        await queue.EnqueueAsync(MakeFrame(7), CancellationToken.None);
        var resumed = await dequeueTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.NotNull(resumed);
        Assert.Equal(5, resumed!.Payload[0]);
        Assert.Equal([true, false], bufferingEvents);

        // Live buffer setting change updates target immediately without dropping queued frames
        queue.UpdateBufferSeconds(15);
        Assert.Equal(TimeSpan.FromSeconds(15), queue.TargetPrebufferDuration);
        Assert.Equal(2, queue.FrameCount);

        queue.Complete();
    }

    [Fact]
    public void AppVersion_UserAgentAndRadioOptionsAreDynamicAndUnique()
    {
        var ua = Chassis.Core.Versioning.AppVersion.UserAgent;
        Assert.Contains('/', ua);
        Assert.StartsWith(Chassis.Core.Versioning.AppVersion.EntryAssemblyName, ua, StringComparison.OrdinalIgnoreCase);

        var allOptionKeys = typeof(RadioOptions)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.True(allOptionKeys.Count >= 10);
        Assert.Equal(allOptionKeys.Count, allOptionKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(allOptionKeys, k => Assert.StartsWith("radio.", k, StringComparison.Ordinal));
    }

    [Fact]
    public void LocaleFiles_HaveIdenticalKeysAcrossNlAndEn()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var nlPath = Path.Combine(repoRoot, "src", "Dial", "Assets", "Locales", "nl-NL.json");
        var enPath = Path.Combine(repoRoot, "src", "Dial", "Assets", "Locales", "en-US.json");

        Assert.True(File.Exists(nlPath), $"Missing {nlPath}");
        Assert.True(File.Exists(enPath), $"Missing {enPath}");

        var nlKeys = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(nlPath))!.Keys.ToHashSet(StringComparer.Ordinal);
        var enKeys = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(enPath))!.Keys.ToHashSet(StringComparer.Ordinal);

        Assert.Empty(nlKeys.Except(enKeys));
        Assert.Empty(enKeys.Except(nlKeys));
    }
}

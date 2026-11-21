using Chassis.Core.Data;
using Chassis.Core.Paths;
using KinkRadio.Modules.Radio;
using Xunit;

namespace Chassis.Tests;

public sealed class KinkRadioTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"KinkRadioTests_{Guid.NewGuid():N}");

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
            Assert.True(Uri.TryCreate(station.StreamUrl, UriKind.Absolute, out _));
            Assert.True(Uri.TryCreate(station.FallbackStreamUrl, UriKind.Absolute, out _));
            Assert.True(station.BitrateKbps > 0);
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
    public async Task JsonDataStore_PersistsAndReloadsKinkStationsAtomically()
    {
        var paths = new PathService(_tempRoot);
        var store = new JsonDataStore<RadioStation>(paths);

        await store.SaveAllAsync("stations", KinkStationCatalog.DefaultStations);
        var reloaded = await store.LoadAllAsync("stations");

        Assert.Equal(4, reloaded.Count);
        Assert.Equal(KinkStationCatalog.DefaultStations.Select(s => s.Id), reloaded.Select(s => s.Id));
        Assert.Equal(KinkStationCatalog.DefaultStations.Select(s => s.StreamUrl), reloaded.Select(s => s.StreamUrl));
    }
}

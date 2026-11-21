using Chassis.Core.Data;
using Chassis.Core.Paths;
using Chassis.Core.Versioning;
using Xunit;

namespace Chassis.Tests;

[Collection("AppVersionState")]
public sealed class DataAndShareTests : IDisposable
{
    private sealed record SampleRecord(string Id, string Label, int Priority);

    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"ChassisDataTests_{Guid.NewGuid():N}");

    public void Dispose()
    {
        AppVersion.SetEntryAssemblyOverride(null);
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task JsonDataStore_PerformsCrudAndPreventsPathTraversal()
    {
        var paths = new PathService(_tempRoot);
        var store = new JsonDataStore<SampleRecord>(paths);

        var first = new SampleRecord("1", "Initial", 10);
        await store.UpsertAsync("../items", first, r => r.Id);

        var loaded = await store.LoadAllAsync("../items");
        Assert.Single(loaded);
        Assert.Equal("Initial", loaded[0].Label);
        Assert.True(File.Exists(Path.Combine(paths.DataDirectory, "items.json")));

        var updated = new SampleRecord("1", "Updated", 20);
        await store.UpsertAsync("items", updated, r => r.Id);
        loaded = await store.LoadAllAsync("items");
        Assert.Single(loaded);
        Assert.Equal("Updated", loaded[0].Label);

        await store.DeleteAsync("items", "1", r => r.Id);
        Assert.Empty(await store.LoadAllAsync("items"));
    }

    [Fact]
    public async Task PackageShareService_RoundTripsFileAndShareCode()
    {
        AppVersion.SetEntryAssemblyOverride(typeof(DataAndShareTests).Assembly);

        var share = new PackageShareService<SampleRecord>();
        var sample = new SampleRecord("item-42", "Modular Config", 5);

        var code = share.ExportToShareCode(sample);
        Assert.StartsWith("CHASSISTESTS-v1:", code);

        var decoded = share.ImportFromShareCode(code);
        Assert.Equal(sample, decoded);

        Directory.CreateDirectory(_tempRoot);
        var packPath = Path.Combine(_tempRoot, "bundle.chassispack");
        await share.ExportToFileAsync(sample, packPath);
        var imported = await share.ImportFromFileAsync(packPath);
        Assert.Equal(sample, imported);

        Assert.Throws<InvalidDataException>(() => share.ImportFromShareCode("invalid-format"));
    }
}

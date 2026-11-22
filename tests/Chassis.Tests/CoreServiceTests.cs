using Chassis.Core.Changelog;
using Chassis.Core.KnowledgeBase;
using Chassis.Core.Localization;
using Chassis.Core.Modules;
using Chassis.Core.Paths;
using Chassis.Core.Settings;
using Chassis.Core.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chassis.Tests;

[CollectionDefinition("AppVersionState")]
public sealed class AppVersionStateCollection;

[Collection("AppVersionState")]
public sealed class CoreServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"ChassisTests_{Guid.NewGuid():N}");

    public void Dispose()
    {
        AppVersion.SetEntryAssemblyOverride(null);
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public void SemVersion_OrdersPreReleaseAndStableCorrectly()
    {
        var alpha = SemVersion.Parse("0.1.0-alpha");
        var beta = SemVersion.Parse("v0.1.0-beta+sha1234");
        var stable = SemVersion.Parse("0.1.0");
        var next = SemVersion.Parse("0.2.0");

        Assert.True(alpha < beta);
        Assert.True(beta < stable);
        Assert.True(next > stable);
        Assert.False(SemVersion.TryParse("invalid", out _));
    }

    [Fact]
    public void AppVersion_ResolvesDynamicMetadataWithoutHardcoding()
    {
        AppVersion.SetEntryAssemblyOverride(typeof(CoreServiceTests).Assembly);

        Assert.Equal("Chassis.Tests", AppVersion.EntryAssemblyName);
        Assert.Equal("Chassis_SingleInstance_Chassis.Tests", AppVersion.SingleInstanceKey);
        Assert.Equal("CHASSISTESTS", AppVersion.ShareCodeAppPrefix);
        Assert.StartsWith("v", AppVersion.ShortDisplayVersion);

        Assert.True(AppVersion.TryExtractGitHubSlug("https://github.com/projectmark/Dial.git", out var slug1));
        Assert.Equal("projectmark/Dial", slug1);
        Assert.True(AppVersion.TryExtractGitHubSlug("projectmark/Dial", out var slug2));
        Assert.Equal("projectmark/Dial", slug2);
        Assert.False(AppVersion.TryExtractGitHubSlug("https://evil.example/projectmark/Dial", out _));
    }

    [Fact]
    public void SettingsService_PersistsOptionsAndRecoversCorruptJson()
    {
        var paths = new PathService(_tempRoot);
        var settings = new SettingsService(paths);

        var eventFired = false;
        settings.SettingsChanged += _ => eventFired = true;

        settings.Update(s =>
        {
            s.Theme = ThemeMode.Dark;
            s.WindowWidth = 1280;
            s.WindowHeight = 760;
            s.WindowX = 120;
            s.WindowY = 80;
            s.IsWindowMaximized = true;
        });
        settings.SetOption("sample.flag", true);

        Assert.True(eventFired);
        Assert.True(File.Exists(paths.SettingsFilePath));

        var reloaded = new SettingsService(paths);
        Assert.Equal(ThemeMode.Dark, reloaded.Current.Theme);
        Assert.Equal(1280, reloaded.Current.WindowWidth);
        Assert.Equal(760, reloaded.Current.WindowHeight);
        Assert.Equal(120, reloaded.Current.WindowX);
        Assert.Equal(80, reloaded.Current.WindowY);
        Assert.True(reloaded.Current.IsWindowMaximized);
        Assert.True(reloaded.GetOption("sample.flag", false));

        // Simulate corrupt JSON -> recovers defaults cleanly
        File.WriteAllText(paths.SettingsFilePath, "{ broken json");
        var recovered = new SettingsService(paths);
        Assert.Equal(ThemeMode.System, recovered.Current.Theme);
    }

    [Fact]
    public void Localization_KnowledgeBase_Changelog_WorkEndToEnd()
    {
        AppVersion.SetEntryAssemblyOverride(typeof(CoreServiceTests).Assembly);

        var paths = new PathService(_tempRoot);
        var settings = new SettingsService(paths);
        var loc = new LocalizationService(paths, settings);

        loc.RegisterResources("en-US", new Dictionary<string, string>
        {
            ["Greeting"] = "Hello {0}",
            ["Cat.Core"] = "Core",
            ["Art.Title"] = "Zero Hardcoding",
            ["Art.Summary"] = "Dynamic identity",
            ["Art.Body"] = "Resolved at runtime",
            ["Art.Ref"] = "Settings -> Core"
        });
        loc.RegisterResources("nl-NL", new Dictionary<string, string>
        {
            ["Greeting"] = "Hallo {0}",
            ["Art.Badge"] = "NL BadgeToken"
        });

        loc.SetLanguage("nl-NL");
        Assert.Equal("Hallo Chassis", loc.GetString("Greeting", "Chassis"));
        Assert.Equal("Dynamic identity", loc["Art.Summary"]); // fallback to en-US

        var kb = new KnowledgeBaseService();
        kb.RegisterArticle(new KnowledgeArticle("a1", "Cat.Core", "Art.Title", "Art.Summary", "Art.Body", "v0.1.0", "AppVersion"));
        kb.RegisterArticle(new KnowledgeArticle("a2", "Cat.Core", "Art.Title", "Art.Summary", "Art.Body", Order: 2, BadgeKey: "Art.Badge", CopyableReferenceKey: "Art.Ref"));
        Assert.Equal(2, kb.Search("Hardcoding", "Cat.Core", loc).Count);
        Assert.Single(kb.Search("BadgeToken", "Cat.Core", loc));
        Assert.Single(kb.Search("Settings -> Core", "Cat.Core", loc));
        Assert.Empty(kb.Search("MissingTerm", "All", loc));

        var changelog = new ChangelogService(settings);
        changelog.RegisterRelease(new ChangelogRelease(AppVersion.InformationalVersion, DateTime.UtcNow,
            [new ChangelogChange("Added", "Art.Summary")], IsLatest: true));

        Assert.True(changelog.HasNewVersion);
        changelog.MarkCurrentAsViewed();
        Assert.False(changelog.HasNewVersion);
    }

    [Fact]
    public void ChassisModule_RegistersAndInitializesThroughDI()
    {
        var services = new ServiceCollection();
        var module = new TestProbeModule();

        module.RegisterServices(services);
        using var provider = services.BuildServiceProvider();
        module.Initialize(provider);

        Assert.True(module.Initialized);
        Assert.Equal("probe-ok", provider.GetRequiredService<ProbeDependency>().Value);
    }

    private sealed record ProbeDependency(string Value);

    private sealed class TestProbeModule : IChassisModule
    {
        public string ModuleId => "test.probe";
        public bool Initialized { get; private set; }

        public void RegisterServices(IServiceCollection services)
            => services.AddSingleton(new ProbeDependency("probe-ok"));

        public void Initialize(IServiceProvider services)
        {
            _ = services.GetRequiredService<ProbeDependency>();
            Initialized = true;
        }
    }
}

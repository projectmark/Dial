using System.Net;
using System.Security.Cryptography;
using System.Text;
using Chassis.Core.Diagnostics;
using Chassis.Core.Feedback;
using Chassis.Core.Paths;
using Chassis.Core.Settings;
using Chassis.Core.Updates;
using Chassis.Core.Versioning;
using Xunit;

namespace Chassis.Tests;

[Collection("AppVersionState")]
public sealed class DiagnosticsAndUpdateTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"ChassisDiagTests_{Guid.NewGuid():N}");

    public void Dispose()
    {
        AppVersion.SetEntryAssemblyOverride(null);
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public void CrashReportService_RetainsAtMostTwentyLogs()
    {
        var paths = new PathService(_tempRoot);
        var crash = new CrashReportService(paths);

        for (var i = 0; i < 25; i++)
        {
            crash.WriteCrashReport("TestSource", new InvalidOperationException($"Boom #{i}"));
        }

        var logs = crash.GetRecentLogFiles();
        Assert.Equal(20, logs.Count);
    }

    [Fact]
    public async Task FeedbackService_CreatesBundleWithDiagnosticsAndAttachment()
    {
        var paths = new PathService(_tempRoot);
        var settings = new SettingsService(paths);
        var crash = new CrashReportService(paths);
        crash.WriteCrashReport("UnitTest", new Exception("Sample crash"));

        Directory.CreateDirectory(_tempRoot);
        var attachment = Path.Combine(_tempRoot, "note.txt");
        await File.WriteAllTextAsync(attachment, "diagnostic context");

        var feedback = new FeedbackService(paths, settings, crash);
        var saved = await feedback.SaveFeedbackAsync("Bug", "Crash on test", "Detailed repro steps", true, attachment);

        Assert.True(File.Exists(saved));
        var json = await File.ReadAllTextAsync(saved);
        Assert.Contains("Crash on test", json);
        Assert.Contains("Sample crash", json);
    }

    [Fact]
    public async Task UpdateService_DetectsVersionsAndVerifiesSha256Payloads()
    {
        AppVersion.SetEntryAssemblyOverride(typeof(DiagnosticsAndUpdateTests).Assembly);
        var paths = new PathService(_tempRoot);
        var updates = new UpdateService(paths);

        Directory.CreateDirectory(_tempRoot);
        var samplePayload = Path.Combine(_tempRoot, "Dial-v99.0.0-win-x64.zip");
        await File.WriteAllBytesAsync(samplePayload, [1, 2, 3, 4]);
        var validSha = Convert.ToHexString(SHA256.HashData([1, 2, 3, 4])).ToLowerInvariant();

        var manifestPath = Path.Combine(_tempRoot, "releases.json");
        await File.WriteAllTextAsync(manifestPath, $$"""
            {
              "version": "99.0.0",
              "downloadUrl": "https://github.com/projectmark/Dial/releases/download/v99.0.0/Dial-v99.0.0-win-x64.zip",
              "releaseNotesUrl": "https://github.com/projectmark/Dial/releases/tag/v99.0.0",
              "sha256": "{{validSha}}"
            }
            """);

        var check = await updates.CheckForUpdatesAsync(manifestPath);
        Assert.True(check.IsUpdateAvailable);
        Assert.Equal("99.0.0", check.LatestVersion);
        Assert.NotNull(check.Manifest);
        Assert.Equal(validSha, check.Manifest!.Sha256);

        Assert.True(updates.VerifyPayloadHash(samplePayload, validSha));
        Assert.False(updates.VerifyPayloadHash(samplePayload, "0000"));
        Assert.False(updates.VerifyPayloadHash(samplePayload, new string('0', 64)));

        // Untrusted release URLs outside GitHub are rejected by TryOpenReleasePage
        var untrustedManifest = new UpdateManifest("99.0.0", "https://evil.example.invalid/Dial.zip", validSha, "https://evil.example.invalid/notes");
        Assert.False(updates.TryOpenReleasePage(untrustedManifest));
    }

    [Fact]
    public void UpdateService_EvaluatesAutoCheckFrequencyAndWhatsNewTriggers()
    {
        AppVersion.SetEntryAssemblyOverride(typeof(DiagnosticsAndUpdateTests).Assembly);
        var paths = new PathService(_tempRoot);
        var updates = new UpdateService(paths);
        var now = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

        // Default is Manual so no unsolicited startup network call happens unless opted in
        var defaultSettings = new AppSettings();
        Assert.Equal(UpdateCheckFrequency.Manual, defaultSettings.UpdateCheckFrequency);
        Assert.False(updates.ShouldAutoCheck(defaultSettings, now));

        var settings = new AppSettings { UpdateCheckFrequency = UpdateCheckFrequency.Startup };
        Assert.True(updates.ShouldAutoCheck(settings, now));

        settings.UpdateCheckFrequency = UpdateCheckFrequency.Daily;
        Assert.True(updates.ShouldAutoCheck(settings, now));
        settings.LastUpdateCheckUtc = now.AddHours(-12);
        Assert.False(updates.ShouldAutoCheck(settings, now));
        settings.LastUpdateCheckUtc = now.AddHours(-25);
        Assert.True(updates.ShouldAutoCheck(settings, now));

        settings.UpdateCheckFrequency = UpdateCheckFrequency.Weekly;
        settings.LastUpdateCheckUtc = now.AddDays(-3);
        Assert.False(updates.ShouldAutoCheck(settings, now));
        settings.LastUpdateCheckUtc = now.AddDays(-8);
        Assert.True(updates.ShouldAutoCheck(settings, now));

        settings.UpdateCheckFrequency = UpdateCheckFrequency.Manual;
        Assert.False(updates.ShouldAutoCheck(settings, now));

        var fresh = new AppSettings();
        Assert.False(updates.ShouldShowWhatsNew(fresh));

        fresh.PendingWhatsNewVersion = AppVersion.InformationalVersion;
        Assert.True(updates.ShouldShowWhatsNew(fresh));

        fresh.PendingWhatsNewVersion = null;
        fresh.LastSeenVersion = "0.0.1-alpha";
        Assert.True(updates.ShouldShowWhatsNew(fresh));

        fresh.LastSeenVersion = AppVersion.InformationalVersion;
        Assert.False(updates.ShouldShowWhatsNew(fresh));
    }

    [Fact]
    public async Task UpdateService_GitHubCheckResolvesManifestAndRejectsUntrustedRedirects()
    {
        AppVersion.SetEntryAssemblyOverride(typeof(DiagnosticsAndUpdateTests).Assembly);
        var paths = new PathService(_tempRoot);
        var validSha = Convert.ToHexString(SHA256.HashData("dial-0.1.4-release-zip"u8)).ToLowerInvariant();

        var handler = new StubGitHubUpdateHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("/repos/projectmark/Dial/releases", StringComparison.OrdinalIgnoreCase))
            {
                var json = $$"""
                    [
                      {
                        "tag_name": "v99.0.0-alpha",
                        "draft": false,
                        "prerelease": true,
                        "html_url": "https://github.com/projectmark/Dial/releases/tag/v99.0.0-alpha",
                        "assets": [
                          { "name": "Dial-v99.0.0-alpha-win-x64.zip", "browser_download_url": "https://github.com/projectmark/Dial/releases/download/v99.0.0-alpha/Dial-v99.0.0-alpha-win-x64.zip" },
                          { "name": "releases.json", "browser_download_url": "https://github.com/projectmark/Dial/releases/download/v99.0.0-alpha/releases.json" }
                        ]
                      }
                    ]
                    """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }

            if (url == "https://github.com/projectmark/Dial/releases/download/v99.0.0-alpha/releases.json")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://objects.githubusercontent.com/github-production-release-asset/releases.json");
                return redirect;
            }

            if (url == "https://objects.githubusercontent.com/github-production-release-asset/releases.json")
            {
                var manifestJson = $$"""
                    {
                      "version": "99.0.0-alpha",
                      "sha256": "{{validSha}}",
                      "downloadUrl": "https://github.com/projectmark/Dial/releases/download/v99.0.0-alpha/Dial-v99.0.0-alpha-win-x64.zip",
                      "releaseNotesUrl": "https://github.com/projectmark/Dial/releases/tag/v99.0.0-alpha"
                    }
                    """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(manifestJson, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var updates = new UpdateService(paths, handler);

        var stableCheck = await updates.CheckForGitHubUpdatesAsync("projectmark/Dial", includePreReleases: false);
        Assert.False(stableCheck.IsUpdateAvailable);

        var preCheck = await updates.CheckForGitHubUpdatesAsync("https://github.com/projectmark/Dial", includePreReleases: true);
        Assert.True(preCheck.IsUpdateAvailable);
        Assert.NotNull(preCheck.Manifest);
        Assert.Equal("99.0.0-alpha", preCheck.LatestVersion);
        Assert.Equal(validSha, preCheck.Manifest!.Sha256);
        Assert.EndsWith(".zip", preCheck.Manifest.DownloadUrl, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubGitHubUpdateHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}

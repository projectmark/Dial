using System.IO.Compression;
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
    public async Task UpdateService_DetectsVersionsVerifiesShaAndExtractsPackageSafely()
    {
        AppVersion.SetEntryAssemblyOverride(typeof(DiagnosticsAndUpdateTests).Assembly);
        var paths = new PathService(_tempRoot);
        var updates = new UpdateService(paths);

        Directory.CreateDirectory(_tempRoot);
        var manifestPath = Path.Combine(_tempRoot, "releases.json");
        await File.WriteAllTextAsync(manifestPath, """
            {
              "version": "99.0.0",
              "downloadUrl": "https://example.invalid/Dial-v99.0.0-win-x64.zip",
              "sha256": "deadbeef"
            }
            """);

        var check = await updates.CheckForUpdatesAsync(manifestPath);
        Assert.True(check.IsUpdateAvailable);
        Assert.Equal("99.0.0", check.LatestVersion);

        // 1. Valid ZIP package extracts cleanly into Unicode destination and deletes staged zip
        var stagedZip = Path.Combine(_tempRoot, "valid.zip");
        CreateTestZip(stagedZip, new Dictionary<string, string>
        {
            ["Dial.exe"] = "small-apphost",
            ["Dial.dll"] = "managed-assembly",
            ["Assets/Locales/nl-NL.json"] = "{}"
        });
        var validSha = ComputeSha256(stagedZip);

        Assert.True(updates.VerifyPayloadHash(stagedZip, validSha));
        Assert.False(updates.VerifyPayloadHash(stagedZip, "0000"));

        var unicodeDest = Path.Combine(_tempRoot, "Café_Üser", "Dial-v99.0.0");
        var extractedDir = updates.ExtractVerifiedPackage(stagedZip, validSha, "99.0.0-alpha", unicodeDest);
        Assert.Equal(Path.GetFullPath(unicodeDest), extractedDir);
        Assert.True(File.Exists(Path.Combine(extractedDir, "Dial.exe")));
        Assert.True(File.Exists(Path.Combine(extractedDir, "Dial.dll")));
        Assert.True(File.Exists(Path.Combine(extractedDir, "Assets", "Locales", "nl-NL.json")));
        Assert.Equal("small-apphost", await File.ReadAllTextAsync(Path.Combine(extractedDir, "Dial.exe")));
        Assert.False(File.Exists(stagedZip));

        // 2. Corrupt SHA256 rejects extraction
        var badHashZip = Path.Combine(_tempRoot, "badhash.zip");
        CreateTestZip(badHashZip, new Dictionary<string, string> { ["Dial.exe"] = "ok" });
        Assert.Throws<InvalidOperationException>(() => updates.ExtractVerifiedPackage(badHashZip, new string('0', 64), "99.0.0"));

        // 3. Zip Slip traversal entry is rejected and writes nothing outside extraction root
        var traversalZip = Path.Combine(_tempRoot, "zipslip.zip");
        CreateTestZip(traversalZip, new Dictionary<string, string> { ["../../escaped.txt"] = "pwn" });
        var traversalSha = ComputeSha256(traversalZip);
        var isolatedDest = Path.Combine(_tempRoot, "nested", "extract");
        Assert.Throws<InvalidDataException>(() => updates.ExtractVerifiedPackage(traversalZip, traversalSha, "99.0.0", isolatedDest));
        Assert.False(File.Exists(Path.Combine(_tempRoot, "escaped.txt")));

        // 4. Disallowed script entry (.cmd) inside update archive is rejected
        var scriptZip = Path.Combine(_tempRoot, "script.zip");
        CreateTestZip(scriptZip, new Dictionary<string, string> { ["evil.cmd"] = "@echo off" });
        var scriptSha = ComputeSha256(scriptZip);
        Assert.Throws<InvalidDataException>(() => updates.ExtractVerifiedPackage(scriptZip, scriptSha, "99.0.0"));
    }

    [Fact]
    public void UpdateService_EvaluatesAutoCheckFrequencyAndWhatsNewTriggers()
    {
        AppVersion.SetEntryAssemblyOverride(typeof(DiagnosticsAndUpdateTests).Assembly);
        var paths = new PathService(_tempRoot);
        var updates = new UpdateService(paths);
        var now = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

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

        // What's New triggers on PendingWhatsNewVersion match or version upgrade from prior LastSeenVersion
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
    public async Task UpdateService_GitHubCheckAndDownloadVerifyShaAndRejectUntrustedRedirects()
    {
        AppVersion.SetEntryAssemblyOverride(typeof(DiagnosticsAndUpdateTests).Assembly);
        var paths = new PathService(_tempRoot);
        Directory.CreateDirectory(_tempRoot);
        var sourceZip = Path.Combine(_tempRoot, "github-release.zip");
        CreateTestZip(sourceZip, new Dictionary<string, string>
        {
            ["Dial.exe"] = "small-apphost-0.1.4",
            ["Dial.dll"] = "dial-core"
        });
        var payloadBytes = await File.ReadAllBytesAsync(sourceZip);
        var validSha = Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant();

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

            if (url.EndsWith("/releases.json", StringComparison.OrdinalIgnoreCase))
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

            if (url == "https://github.com/projectmark/Dial/releases/download/v99.0.0-alpha/Dial-v99.0.0-alpha-win-x64.zip")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://objects.githubusercontent.com/github-production-release-asset/Dial-v99.0.0-alpha-win-x64.zip");
                return redirect;
            }

            if (url == "https://objects.githubusercontent.com/github-production-release-asset/Dial-v99.0.0-alpha-win-x64.zip")
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payloadBytes)
                };
                resp.Content.Headers.ContentLength = payloadBytes.Length;
                return resp;
            }

            if (url == "https://github.com/projectmark/Dial/releases/download/v99.0.0-alpha/EvilRedirect.zip")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://evil.example.invalid/Dial-v99.0.0-alpha-win-x64.zip");
                return redirect;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var updates = new UpdateService(paths, handler);

        // Stable-only channel ignores v99.0.0-alpha
        var stableCheck = await updates.CheckForGitHubUpdatesAsync("projectmark/Dial", includePreReleases: false);
        Assert.False(stableCheck.IsUpdateAvailable);

        // Pre-release channel finds v99.0.0-alpha and reads releases.json asset
        var preCheck = await updates.CheckForGitHubUpdatesAsync("https://github.com/projectmark/Dial", includePreReleases: true);
        Assert.True(preCheck.IsUpdateAvailable);
        Assert.NotNull(preCheck.Manifest);
        Assert.Equal("99.0.0-alpha", preCheck.LatestVersion);
        Assert.Equal(validSha, preCheck.Manifest!.Sha256);

        // Valid download across GitHub -> objects.githubusercontent.com redirect succeeds, passes SHA256, and extracts cleanly
        var stagedPath = await updates.DownloadAndVerifyUpdateAsync(preCheck.Manifest);
        Assert.True(File.Exists(stagedPath));
        Assert.EndsWith(".zip", stagedPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(updates.VerifyPayloadHash(stagedPath, validSha));

        var extractedDir = updates.ExtractVerifiedPackage(stagedPath, validSha, preCheck.Manifest.Version);
        Assert.True(File.Exists(Path.Combine(extractedDir, "Dial.exe")));
        Assert.True(File.Exists(Path.Combine(extractedDir, "Dial.dll")));

        // Corrupt SHA256 rejects download and deletes staged artifact
        var badShaManifest = preCheck.Manifest with { Sha256 = new string('0', 64) };
        await Assert.ThrowsAsync<InvalidDataException>(() => updates.DownloadAndVerifyUpdateAsync(badShaManifest));

        // Redirect outside GitHub/githubusercontent.com is blocked
        var evilManifest = preCheck.Manifest with
        {
            DownloadUrl = "https://github.com/projectmark/Dial/releases/download/v99.0.0-alpha/EvilRedirect.zip"
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => updates.DownloadAndVerifyUpdateAsync(evilManifest));
    }

    private static void CreateTestZip(string zipPath, Dictionary<string, string> entries)
    {
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(content);
        }
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed class StubGitHubUpdateHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}

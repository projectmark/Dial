using System.Security.Cryptography;
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
    public async Task UpdateService_DetectsVersionsVerifiesShaAndStagesScript()
    {
        AppVersion.SetEntryAssemblyOverride(typeof(DiagnosticsAndUpdateTests).Assembly);
        var paths = new PathService(_tempRoot);
        var updates = new UpdateService(paths);

        Directory.CreateDirectory(_tempRoot);
        var manifestPath = Path.Combine(_tempRoot, "releases.json");
        await File.WriteAllTextAsync(manifestPath, """
            {
              "version": "99.0.0",
              "downloadUrl": "https://example.invalid/app.exe",
              "sha256": "deadbeef"
            }
            """);

        var check = await updates.CheckForUpdatesAsync(manifestPath);
        Assert.True(check.IsUpdateAvailable);
        Assert.Equal("99.0.0", check.LatestVersion);

        var stagedExe = Path.Combine(_tempRoot, "new.exe");
        var targetExe = Path.Combine(_tempRoot, "current.exe");
        await File.WriteAllBytesAsync(stagedExe, [1, 2, 3, 4]);
        var validSha = Convert.ToHexString(SHA256.HashData([1, 2, 3, 4]));

        Assert.True(updates.VerifyPayloadHash(stagedExe, validSha));
        Assert.False(updates.VerifyPayloadHash(stagedExe, "0000"));

        var cmdPath = updates.StageUpdateScript(stagedExe, targetExe, validSha);
        Assert.True(File.Exists(cmdPath));
        var scriptText = await File.ReadAllTextAsync(cmdPath);
        Assert.Contains(Environment.ProcessId.ToString(), scriptText);
        Assert.Contains(targetExe, scriptText);
    }
}

using System.Globalization;
using System.Text.Json;

namespace Chassis.Core.Settings;

/// <summary>
/// Supported UI theme preferences.
/// </summary>
public enum ThemeMode
{
    System = 0,
    Light = 1,
    Dark = 2
}

/// <summary>
/// Supported automatic update check intervals.
/// </summary>
public enum UpdateCheckFrequency
{
    Startup = 0,
    Daily = 1,
    Weekly = 2,
    Manual = 3
}

/// <summary>
/// Persistent shell configuration serialized atomically to config/settings.json.
/// </summary>
public sealed class AppSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public bool UseSystemAccentColor { get; set; } = true;
    public string CustomAccentHex { get; set; } = "#0078D4";
    public string? PersonalAccentPreset1Hex { get; set; }
    public string? PersonalAccentPreset2Hex { get; set; }
    public bool AnimationsEnabled { get; set; } = true;
    public string Language { get; set; } = DetectDefaultLanguage();
    public bool CloseToTrayEnabled { get; set; }
    public bool ShowTrayNotifications { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool GlobalHotkeysEnabled { get; set; }
    public string ToggleWindowHotkey { get; set; } = "Ctrl+Shift+Space";
    public UpdateCheckFrequency UpdateCheckFrequency { get; set; } = UpdateCheckFrequency.Manual;
    public bool IncludePreReleases { get; set; } = !string.IsNullOrWhiteSpace(Versioning.AppVersion.PreReleaseTag);
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }
    public string? PendingWhatsNewVersion { get; set; }
    public string? LastSeenVersion { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public bool IsWindowMaximized { get; set; }
    public Dictionary<string, JsonElement> ModuleOptions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static string DetectDefaultLanguage()
        => CultureInfo.CurrentUICulture.Name.StartsWith("nl", StringComparison.OrdinalIgnoreCase)
            ? "nl-NL"
            : "en-US";
}

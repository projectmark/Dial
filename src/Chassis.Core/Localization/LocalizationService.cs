using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Chassis.Core.Paths;
using Chassis.Core.Settings;

namespace Chassis.Core.Localization;

public sealed record SupportedLanguage(string CultureCode, string DisplayName);

/// <summary>
/// Dynamic i18n service supporting runtime language switches and multi-source JSON dictionary discovery.
/// </summary>
public interface ILocalizationService
{
    string CurrentCulture { get; }
    IReadOnlyList<SupportedLanguage> SupportedLanguages { get; }
    event Action<string>? LanguageChanged;
    string this[string key] { get; }
    string GetString(string key, params object[] args);
    void SetLanguage(string cultureCode);
    void RegisterResources(string cultureCode, IReadOnlyDictionary<string, string> entries);
}

/// <summary>
/// JSON-driven localization provider discovering embedded locales, bundled Assets/Locales, and %AppData% overrides.
/// </summary>
public sealed class LocalizationService : ILocalizationService
{
    private const string DisplayNameMetaKey = "_meta.displayName";

    private readonly ISettingsService _settings;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _dictionaries =
        new(StringComparer.OrdinalIgnoreCase);

    public LocalizationService(IPathService paths, ISettingsService settings)
    {
        _settings = settings;

        DiscoverEmbeddedLocales();
        DiscoverDirectoryLocales(Path.Combine(AppContext.BaseDirectory, "Assets", "Locales"));
        DiscoverDirectoryLocales(Path.Combine(paths.ConfigDirectory, "locales"));

        CurrentCulture = ResolveInitialCulture(_settings.Current.Language);
    }

    public string CurrentCulture { get; private set; }

    public IReadOnlyList<SupportedLanguage> SupportedLanguages
        => _dictionaries.Keys
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .Select(c => new SupportedLanguage(c, ResolveDisplayName(c)))
            .ToList();

    public event Action<string>? LanguageChanged;

    public string this[string key] => GetString(key);

    /// <inheritdoc />
    public string GetString(string key, params object[] args)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        if (!TryLookup(CurrentCulture, key, out var raw))
        {
            if (!TryLookup("en-US", key, out raw))
            {
                raw = _dictionaries.Values
                    .Select(d => d.TryGetValue(key, out var val) ? val : null)
                    .FirstOrDefault(v => v is not null)
                    ?? key;
            }
        }

        return args is { Length: > 0 }
            ? string.Format(CultureInfo.CurrentCulture, raw, args)
            : raw;
    }

    /// <inheritdoc />
    public void SetLanguage(string cultureCode)
    {
        var resolved = ResolveInitialCulture(cultureCode);
        var changed = !string.Equals(CurrentCulture, resolved, StringComparison.OrdinalIgnoreCase);
        CurrentCulture = resolved;

        if (!string.Equals(_settings.Current.Language, resolved, StringComparison.OrdinalIgnoreCase))
        {
            _settings.Update(s => s.Language = resolved);
        }

        if (changed)
        {
            LanguageChanged?.Invoke(CurrentCulture);
        }
    }

    /// <inheritdoc />
    public void RegisterResources(string cultureCode, IReadOnlyDictionary<string, string> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cultureCode);
        ArgumentNullException.ThrowIfNull(entries);

        var dict = _dictionaries.GetOrAdd(
            cultureCode.Trim(),
            _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        foreach (var (k, v) in entries)
        {
            dict[k] = v;
        }
    }

    private bool TryLookup(string culture, string key, out string value)
    {
        if (_dictionaries.TryGetValue(culture, out var dict) && dict.TryGetValue(key, out var found))
        {
            value = found;
            return true;
        }

        var twoLetter = culture.Split('-')[0];
        var fallbackDict = _dictionaries
            .FirstOrDefault(kv => kv.Key.StartsWith(twoLetter, StringComparison.OrdinalIgnoreCase))
            .Value;

        if (fallbackDict is not null && fallbackDict.TryGetValue(key, out found))
        {
            value = found;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private string ResolveInitialCulture(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var exact = _dictionaries.Keys.FirstOrDefault(k => string.Equals(k, requested, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }

            var prefix = requested.Split('-')[0];
            var partial = _dictionaries.Keys.FirstOrDefault(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (partial is not null)
            {
                return partial;
            }

            return requested;
        }

        return _dictionaries.Keys.FirstOrDefault(k => string.Equals(k, "nl-NL", StringComparison.OrdinalIgnoreCase))
            ?? _dictionaries.Keys.FirstOrDefault(k => string.Equals(k, "en-US", StringComparison.OrdinalIgnoreCase))
            ?? _dictionaries.Keys.FirstOrDefault()
            ?? "en-US";
    }

    private string ResolveDisplayName(string cultureCode)
    {
        if (_dictionaries.TryGetValue(cultureCode, out var dict) &&
            dict.TryGetValue(DisplayNameMetaKey, out var customName) &&
            !string.IsNullOrWhiteSpace(customName))
        {
            return customName;
        }

        try
        {
            var info = CultureInfo.GetCultureInfo(cultureCode);
            var native = info.NativeName;
            return string.IsNullOrEmpty(native)
                ? cultureCode
                : char.ToUpper(native[0], CultureInfo.CurrentCulture) + native[1..];
        }
        catch
        {
            return cultureCode;
        }
    }

    private void DiscoverEmbeddedLocales()
    {
        var assemblies = new[] { Assembly.GetEntryAssembly(), Assembly.GetExecutingAssembly() }
            .OfType<Assembly>()
            .Distinct();

        foreach (var assembly in assemblies)
        {
            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (!name.Contains("Locales", StringComparison.OrdinalIgnoreCase) ||
                    !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var culture = ExtractCultureFromResourceName(name);
                if (string.IsNullOrEmpty(culture))
                {
                    continue;
                }

                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is not null)
                {
                    LoadJsonStream(culture, stream);
                }
            }
        }
    }

    private void DiscoverDirectoryLocales(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            var culture = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrWhiteSpace(culture))
            {
                continue;
            }

            using var stream = File.OpenRead(file);
            LoadJsonStream(culture, stream);
        }
    }

    private void LoadJsonStream(string cultureCode, Stream stream)
    {
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            if (map is not null)
            {
                RegisterResources(cultureCode, map);
            }
        }
        catch
        {
            // Skip malformed locale file so valid locales continue functioning.
        }
    }

    private static string ExtractCultureFromResourceName(string resourceName)
    {
        var noExt = resourceName[..^".json".Length];
        var lastDot = noExt.LastIndexOf('.');
        return lastDot >= 0 ? noExt[(lastDot + 1)..] : noExt;
    }
}

using System.Text.Json;
using Chassis.Core.Paths;

namespace Chassis.Core.Settings;

/// <summary>
/// Manages persistent shell settings and module-scoped option flags with atomic file writes.
/// </summary>
public interface ISettingsService
{
    AppSettings Current { get; }
    event Action<AppSettings>? SettingsChanged;
    void Save();
    void Update(Action<AppSettings> mutate, bool autoSave = true);
    T GetOption<T>(string key, T defaultValue);
    void SetOption<T>(string key, T value, bool autoSave = true);
}

/// <summary>
/// JSON-backed implementation of <see cref="ISettingsService"/>.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly IPathService _paths;
    private readonly Lock _gate = new();

    public SettingsService(IPathService paths)
    {
        _paths = paths;
        _paths.EnsureDirectoriesExist();
        Current = LoadOrCreate();
    }

    public AppSettings Current { get; private set; }
    public event Action<AppSettings>? SettingsChanged;

    private AppSettings LoadOrCreate()
    {
        if (!File.Exists(_paths.SettingsFilePath))
        {
            var defaults = new AppSettings();
            WriteAtomic(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(_paths.SettingsFilePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();
            loaded.ModuleOptions ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            return loaded;
        }
        catch (Exception)
        {
            try
            {
                var backup = _paths.SettingsFilePath + $".corrupt_{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
                File.Move(_paths.SettingsFilePath, backup, overwrite: true);
            }
            catch
            {
                // Best-effort backup; continue cleanly with defaults.
            }

            var defaults = new AppSettings();
            WriteAtomic(defaults);
            return defaults;
        }
    }

    /// <inheritdoc />
    public void Save()
    {
        lock (_gate)
        {
            WriteAtomic(Current);
        }
    }

    /// <inheritdoc />
    public void Update(Action<AppSettings> mutate, bool autoSave = true)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_gate)
        {
            mutate(Current);
            if (autoSave)
            {
                WriteAtomic(Current);
            }
        }

        SettingsChanged?.Invoke(Current);
    }

    /// <inheritdoc />
    public T GetOption<T>(string key, T defaultValue)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return defaultValue;
        }

        lock (_gate)
        {
            if (!Current.ModuleOptions.TryGetValue(key, out var element))
            {
                return defaultValue;
            }

            try
            {
                var deserialized = element.Deserialize<T>(SerializerOptions);
                return deserialized is null ? defaultValue : deserialized;
            }
            catch
            {
                return defaultValue;
            }
        }
    }

    /// <inheritdoc />
    public void SetOption<T>(string key, T value, bool autoSave = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_gate)
        {
            Current.ModuleOptions[key] = JsonSerializer.SerializeToElement(value, SerializerOptions);
            if (autoSave)
            {
                WriteAtomic(Current);
            }
        }

        SettingsChanged?.Invoke(Current);
    }

    private void WriteAtomic(AppSettings snapshot)
    {
        _paths.EnsureDirectoriesExist();
        var tmpPath = _paths.SettingsFilePath + ".tmp";
        var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, _paths.SettingsFilePath, overwrite: true);
    }
}

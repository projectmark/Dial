using System.Text.Json;
using Chassis.Core.Paths;

namespace Chassis.Core.Data;

/// <summary>
/// Generic domain collection store persisted atomically under %AppData%/&lt;App&gt;/data/.
/// </summary>
public interface IDataStore<T>
{
    Task<IReadOnlyList<T>> LoadAllAsync(string collectionName, CancellationToken ct = default);
    Task SaveAllAsync(string collectionName, IEnumerable<T> items, CancellationToken ct = default);
    Task UpsertAsync(string collectionName, T item, Func<T, string> keySelector, CancellationToken ct = default);
    Task DeleteAsync(string collectionName, string key, Func<T, string> keySelector, CancellationToken ct = default);
}

/// <summary>
/// JSON-backed collection store isolated per collection file with semaphore-guarded atomic writes.
/// </summary>
public sealed class JsonDataStore<T> : IDataStore<T>
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly IPathService _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonDataStore(IPathService paths)
    {
        _paths = paths;
        _paths.EnsureDirectoriesExist();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<T>> LoadAllAsync(string collectionName, CancellationToken ct = default)
    {
        var filePath = ResolveCollectionPath(collectionName);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ReadInternalAsync(filePath, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAllAsync(string collectionName, IEnumerable<T> items, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        var filePath = ResolveCollectionPath(collectionName);
        var snapshot = items as IReadOnlyList<T> ?? items.ToList();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteInternalAsync(filePath, snapshot, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task UpsertAsync(string collectionName, T item, Func<T, string> keySelector, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(keySelector);
        var filePath = ResolveCollectionPath(collectionName);
        var targetKey = keySelector(item);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = (await ReadInternalAsync(filePath, ct).ConfigureAwait(false)).ToList();
            var idx = list.FindIndex(existing => string.Equals(keySelector(existing), targetKey, StringComparison.Ordinal));
            if (idx >= 0)
            {
                list[idx] = item;
            }
            else
            {
                list.Add(item);
            }

            await WriteInternalAsync(filePath, list, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string collectionName, string key, Func<T, string> keySelector, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keySelector);
        var filePath = ResolveCollectionPath(collectionName);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = (await ReadInternalAsync(filePath, ct).ConfigureAwait(false))
                .Where(existing => !string.Equals(keySelector(existing), key, StringComparison.Ordinal))
                .ToList();

            await WriteInternalAsync(filePath, list, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string ResolveCollectionPath(string collectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);

        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(collectionName.Trim().Where(c => !invalid.Contains(c) && c != '.' && c != '/' && c != '\\').ToArray());
        if (string.IsNullOrWhiteSpace(clean))
        {
            throw new ArgumentException("Collection name must contain valid filename characters.", nameof(collectionName));
        }

        return Path.Combine(_paths.DataDirectory, $"{clean}.json");
    }

    private static async Task<IReadOnlyList<T>> ReadInternalAsync(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            return Array.Empty<T>();
        }

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length == 0)
        {
            return Array.Empty<T>();
        }

        return await JsonSerializer.DeserializeAsync<List<T>>(stream, SerializerOptions, ct).ConfigureAwait(false)
            ?? [];
    }

    private async Task WriteInternalAsync(string filePath, IReadOnlyList<T> items, CancellationToken ct)
    {
        _paths.EnsureDirectoriesExist();
        var tmpPath = filePath + ".tmp";
        await using (var stream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, items, SerializerOptions, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        File.Move(tmpPath, filePath, overwrite: true);
    }
}

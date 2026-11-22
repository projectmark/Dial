using System.Reflection;

namespace Chassis.Core.Versioning;

/// <summary>
/// Resolves application identity, versioning, mutex keys, and share-code prefixes dynamically from the entry assembly.
/// </summary>
public static class AppVersion
{
    private static Assembly? _entryAssemblyOverride;

    /// <summary>
    /// Overrides the entry assembly used for metadata resolution (intended for host startup or unit tests).
    /// </summary>
    public static void SetEntryAssemblyOverride(Assembly? assembly) => _entryAssemblyOverride = assembly;

    private static Assembly ResolveTargetAssembly()
        => _entryAssemblyOverride ?? Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

    /// <summary>
    /// Assembly simple name (used for mutex keys and %AppData% folder naming).
    /// </summary>
    public static string EntryAssemblyName
        => ResolveTargetAssembly().GetName().Name ?? "ChassisApp";

    /// <summary>
    /// Human-readable product name resolved from assembly attributes, falling back to <see cref="EntryAssemblyName"/>.
    /// </summary>
    public static string ProductTitle
    {
        get
        {
            var assembly = ResolveTargetAssembly();
            var product = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product;
            if (!string.IsNullOrWhiteSpace(product))
            {
                return product.Trim();
            }

            var title = assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title;
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title.Trim();
            }

            return EntryAssemblyName;
        }
    }

    /// <summary>
    /// Raw informational version without SourceLink '+commit' metadata (e.g. '0.1.0-alpha').
    /// </summary>
    public static string InformationalVersion
    {
        get
        {
            var assembly = ResolveTargetAssembly();
            var raw = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var plusIndex = raw.IndexOf('+');
                var cleaned = (plusIndex >= 0 ? raw[..plusIndex] : raw).Trim();
                if (!string.IsNullOrEmpty(cleaned))
                {
                    return cleaned;
                }
            }

            var ver = assembly.GetName().Version;
            return ver is null ? "0.1.0" : $"{ver.Major}.{ver.Minor}.{Math.Max(0, ver.Build)}";
        }
    }

    /// <summary>
    /// Numeric Major.Minor.Patch version without pre-release suffixes (e.g. '0.1.0').
    /// </summary>
    public static string BaseVersion
        => SemVersion.TryParse(InformationalVersion, out var parsed)
            ? $"{parsed.Major}.{parsed.Minor}.{parsed.Patch}"
            : InformationalVersion.Split('-', '+')[0];

    /// <summary>
    /// Pre-release channel suffix if defined (e.g. 'alpha'), otherwise empty string.
    /// </summary>
    public static string PreReleaseTag
        => SemVersion.TryParse(InformationalVersion, out var parsed) && !string.IsNullOrWhiteSpace(parsed.PreRelease)
            ? parsed.PreRelease
            : string.Empty;

    /// <summary>
    /// Canonical display version with leading 'v' (e.g. 'v0.1.0-alpha').
    /// </summary>
    public static string DisplayVersion
        => InformationalVersion.StartsWith('v') || InformationalVersion.StartsWith('V')
            ? InformationalVersion
            : $"v{InformationalVersion}";

    /// <summary>
    /// Human-friendly badge label (e.g. 'v0.1.0 (alpha)' or 'v0.1.0').
    /// </summary>
    public static string ShortDisplayVersion
        => string.IsNullOrWhiteSpace(PreReleaseTag)
            ? $"v{BaseVersion}"
            : $"v{BaseVersion} ({PreReleaseTag})";

    /// <summary>
    /// Single-instance activation key scoped to the active entry assembly.
    /// </summary>
    public static string SingleInstanceKey
        => $"Chassis_SingleInstance_{EntryAssemblyName}";

    /// <summary>
    /// Dynamic uppercase alphanumeric share-code prefix derived from the entry assembly name.
    /// </summary>
    public static string ShareCodeAppPrefix
    {
        get
        {
            var chars = EntryAssemblyName.Where(char.IsLetterOrDigit).ToArray();
            return chars.Length == 0 ? "CHASSIS" : new string(chars).ToUpperInvariant();
        }
    }

    /// <summary>
    /// Dynamic RFC-compliant HTTP User-Agent string derived from assembly identity and version (e.g. 'Dial/0.3.0').
    /// </summary>
    public static string UserAgent
    {
        get
        {
            var token = new string(EntryAssemblyName.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
            if (string.IsNullOrEmpty(token))
            {
                token = "ChassisApp";
            }

            return $"{token}/{BaseVersion}";
        }
    }

    /// <summary>
    /// Repository URL resolved dynamically from assembly metadata (<c>&lt;RepositoryUrl&gt;</c>), if configured.
    /// </summary>
    public static string? RepositoryUrl
    {
        get
        {
            var raw = ResolveTargetAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => string.Equals(a.Key, "RepositoryUrl", StringComparison.OrdinalIgnoreCase))
                ?.Value;

            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var clean = raw.Trim().TrimEnd('/');
            if (clean.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                clean = clean[..^4].TrimEnd('/');
            }

            return string.IsNullOrWhiteSpace(clean) ? null : clean;
        }
    }

    /// <summary>
    /// GitHub <c>owner/repo</c> slug derived from <see cref="RepositoryUrl"/>, if it points to github.com.
    /// </summary>
    public static string? GitHubRepositorySlug
        => TryExtractGitHubSlug(RepositoryUrl, out var slug) ? slug : null;

    /// <summary>
    /// Extracts a normalized <c>owner/repo</c> slug from a GitHub URL or slug string.
    /// </summary>
    public static bool TryExtractGitHubSlug(string? repositoryUrlOrSlug, out string slug)
    {
        slug = string.Empty;
        if (string.IsNullOrWhiteSpace(repositoryUrlOrSlug))
        {
            return false;
        }

        var trimmed = repositoryUrlOrSlug.Trim().TrimEnd('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4].TrimEnd('/');
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2)
            {
                slug = $"{segments[0]}/{segments[1]}";
                return true;
            }

            return false;
        }

        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 &&
            parts[0].All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.') &&
            parts[1].All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
        {
            slug = $"{parts[0]}/{parts[1]}";
            return true;
        }

        return false;
    }
}

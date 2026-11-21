using System.Diagnostics.CodeAnalysis;

namespace Chassis.Core.Versioning;

/// <summary>
/// Semantic version representation supporting SemVer 2.0 ordering with optional pre-release tags.
/// </summary>
public sealed record SemVersion(int Major, int Minor, int Patch, string? PreRelease = null)
    : IComparable<SemVersion>
{
    /// <summary>
    /// Attempts to parse a semantic version string (with optional leading 'v' and '+' metadata).
    /// </summary>
    public static bool TryParse(string? input, [NotNullWhen(true)] out SemVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        var buildIdx = trimmed.IndexOf('+');
        if (buildIdx >= 0)
        {
            trimmed = trimmed[..buildIdx];
        }

        string? preRelease = null;
        var preIdx = trimmed.IndexOf('-');
        if (preIdx >= 0)
        {
            preRelease = trimmed[(preIdx + 1)..].Trim();
            if (string.IsNullOrEmpty(preRelease))
            {
                preRelease = null;
            }
            trimmed = trimmed[..preIdx];
        }

        var parts = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 2 or > 4)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out var major) || major < 0 ||
            !int.TryParse(parts[1], out var minor) || minor < 0)
        {
            return false;
        }

        var patch = 0;
        if (parts.Length >= 3 && (!int.TryParse(parts[2], out patch) || patch < 0))
        {
            return false;
        }

        version = new SemVersion(major, minor, patch, preRelease);
        return true;
    }

    /// <summary>
    /// Parses a semantic version string or throws <see cref="FormatException"/>.
    /// </summary>
    public static SemVersion Parse(string input)
    {
        if (!TryParse(input, out var parsed))
        {
            throw new FormatException($"Invalid semantic version string: '{input}'.");
        }

        return parsed;
    }

    /// <inheritdoc />
    public int CompareTo(SemVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var cmp = Major.CompareTo(other.Major);
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = Minor.CompareTo(other.Minor);
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = Patch.CompareTo(other.Patch);
        if (cmp != 0)
        {
            return cmp;
        }

        var thisHasPre = !string.IsNullOrWhiteSpace(PreRelease);
        var otherHasPre = !string.IsNullOrWhiteSpace(other.PreRelease);

        if (!thisHasPre && !otherHasPre)
        {
            return 0;
        }

        if (!thisHasPre)
        {
            return 1;
        }

        if (!otherHasPre)
        {
            return -1;
        }

        var leftIds = PreRelease!.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var rightIds = other.PreRelease!.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var minLen = Math.Min(leftIds.Length, rightIds.Length);

        for (var i = 0; i < minLen; i++)
        {
            var leftIsNum = int.TryParse(leftIds[i], out var leftNum);
            var rightIsNum = int.TryParse(rightIds[i], out var rightNum);

            int idCmp;
            if (leftIsNum && rightIsNum)
            {
                idCmp = leftNum.CompareTo(rightNum);
            }
            else if (leftIsNum)
            {
                idCmp = -1;
            }
            else if (rightIsNum)
            {
                idCmp = 1;
            }
            else
            {
                idCmp = string.Compare(leftIds[i], rightIds[i], StringComparison.Ordinal);
            }

            if (idCmp != 0)
            {
                return idCmp;
            }
        }

        return leftIds.Length.CompareTo(rightIds.Length);
    }

    public static bool operator >(SemVersion left, SemVersion right) => left.CompareTo(right) > 0;
    public static bool operator <(SemVersion left, SemVersion right) => left.CompareTo(right) < 0;
    public static bool operator >=(SemVersion left, SemVersion right) => left.CompareTo(right) >= 0;
    public static bool operator <=(SemVersion left, SemVersion right) => left.CompareTo(right) <= 0;

    public override string ToString()
        => string.IsNullOrWhiteSpace(PreRelease)
            ? $"{Major}.{Minor}.{Patch}"
            : $"{Major}.{Minor}.{Patch}-{PreRelease}";
}

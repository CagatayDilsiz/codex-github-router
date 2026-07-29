using System.Text.RegularExpressions;

namespace ReleaseVersionTool;

public sealed record ResolvedReleaseVersion(string Version, string Tag, bool IsPrerelease, bool UsedBootstrapBaseline);

public static partial class ReleaseVersionResolver
{
    public static ResolvedReleaseVersion Resolve(IEnumerable<string> tags, string versionPart, string? suffix)
    {
        var tagList = tags.ToArray();
        if (!TryNormalizeVersionPart(versionPart, out var normalizedPart)) throw new ArgumentException("versionPart must be one of: major, minor, build.", nameof(versionPart));
        var normalizedSuffix = NormalizeSuffix(suffix);
        var latest = tagList.Select(tag => TryParseTag(tag, out var version) ? version : null).Where(version => version is not null).Cast<SemanticVersion>().OrderByDescending(version => version).FirstOrDefault();
        var usedBootstrapBaseline = latest is null;
        latest ??= new SemanticVersion(0, 0, 0, null);

        var (major, minor, patch) = normalizedPart switch
        {
            "major" => (latest.Major + 1, 0, 0),
            "minor" => (latest.Major, latest.Minor + 1, 0),
            _ => (latest.Major, latest.Minor, latest.Patch + 1)
        };
        var versionText = $"{major}.{minor}.{patch}" + (normalizedSuffix is null ? string.Empty : $"-{normalizedSuffix}");
        EnsureTargetDoesNotExist(tagList, "v" + versionText);
        return new ResolvedReleaseVersion(versionText, "v" + versionText, normalizedSuffix is not null, usedBootstrapBaseline);
    }

    public static string? NormalizeSuffix(string? suffix)
    {
        var normalized = suffix?.Trim();
        if (string.IsNullOrEmpty(normalized)) return null;
        if (normalized.StartsWith("-", StringComparison.Ordinal) || !PrereleaseRegex().IsMatch(normalized)) throw new ArgumentException("suffix must be valid SemVer prerelease identifiers without a leading '-'.", nameof(suffix));
        return normalized;
    }

    public static void EnsureTargetDoesNotExist(IEnumerable<string> tags, string tag)
    {
        if (tags.Contains(tag, StringComparer.Ordinal)) throw new InvalidOperationException($"The release tag {tag} already exists.");
    }

    private static bool TryNormalizeVersionPart(string? versionPart, out string normalizedPart)
    {
        normalizedPart = versionPart?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalizedPart is "major" or "minor" or "build";
    }

    private static bool TryParseTag(string tag, out SemanticVersion version)
    {
        version = default!;
        var match = TagRegex().Match(tag);
        if (!match.Success) return false;
        version = new SemanticVersion(int.Parse(match.Groups["major"].Value), int.Parse(match.Groups["minor"].Value), int.Parse(match.Groups["patch"].Value), match.Groups["prerelease"].Success ? match.Groups["prerelease"].Value : null);
        return true;
    }

    [GeneratedRegex("^(?:0|[1-9]\\d*|\\d*[A-Za-z-][0-9A-Za-z-]*)(?:\\.(?:0|[1-9]\\d*|\\d*[A-Za-z-][0-9A-Za-z-]*))*$")]
    private static partial Regex PrereleaseRegex();

    [GeneratedRegex("^v(?<major>0|[1-9]\\d*)\\.(?<minor>0|[1-9]\\d*)\\.(?<patch>0|[1-9]\\d*)(?:-(?<prerelease>(?:0|[1-9]\\d*|\\d*[A-Za-z-][0-9A-Za-z-]*)(?:\\.(?:0|[1-9]\\d*|\\d*[A-Za-z-][0-9A-Za-z-]*))*))?$")]
    private static partial Regex TagRegex();

    private sealed record SemanticVersion(int Major, int Minor, int Patch, string? Prerelease) : IComparable<SemanticVersion>
    {
        public int CompareTo(SemanticVersion? other)
        {
            if (other is null) return 1;
            var numeric = Major.CompareTo(other.Major); if (numeric != 0) return numeric;
            numeric = Minor.CompareTo(other.Minor); if (numeric != 0) return numeric;
            numeric = Patch.CompareTo(other.Patch); if (numeric != 0) return numeric;
            if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
            if (other.Prerelease is null) return -1;
            var left = Prerelease.Split('.'); var right = other.Prerelease.Split('.');
            for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
            {
                if (left[index] == right[index]) continue;
                var leftNumeric = int.TryParse(left[index], out var leftNumber); var rightNumeric = int.TryParse(right[index], out var rightNumber);
                if (leftNumeric && rightNumeric) return leftNumber.CompareTo(rightNumber);
                if (leftNumeric != rightNumeric) return leftNumeric ? -1 : 1;
                return string.CompareOrdinal(left[index], right[index]);
            }
            return left.Length.CompareTo(right.Length);
        }
    }
}

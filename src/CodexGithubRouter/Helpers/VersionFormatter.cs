namespace CodexGithubRouter.Helpers;

public static class VersionFormatter
{
    public static string Normalize(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return "Unknown";
        }

        var buildMetadataIndex = version.IndexOf('+', StringComparison.Ordinal);
        return buildMetadataIndex >= 0 ? version[..buildMetadataIndex] : version;
    }
}

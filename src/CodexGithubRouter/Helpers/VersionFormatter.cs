using System.Reflection;

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

    public static string GetVersion()
    {
        var assembly = typeof(VersionFormatter).Assembly;
        var infoVersion = assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
            .OfType<AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "Unknown";

        return Normalize(infoVersion);
    }
}

using System.Diagnostics;
using ReleaseVersionTool;

var versionPartIndex = Array.IndexOf(args, "--version-part");
var suffixIndex = Array.IndexOf(args, "--suffix");
if (versionPartIndex < 0 || versionPartIndex + 1 >= args.Length)
{
    Console.Error.WriteLine("Usage: ReleaseVersionTool --version-part <major|minor|build> [--suffix <suffix>]");
    return 2;
}

try
{
    var suffix = suffixIndex >= 0 && suffixIndex + 1 < args.Length ? args[suffixIndex + 1] : null;
    var release = ReleaseVersionResolver.Resolve(ReadGitTags(), args[versionPartIndex + 1], suffix);
    if (release.UsedBootstrapBaseline)
    {
        Console.Error.WriteLine("Warning: no valid SemVer release tag was found; using the temporary 0.0.0 bootstrap baseline.");
    }
    Console.WriteLine($"version={release.Version}");
    Console.WriteLine($"tag={release.Tag}");
    Console.WriteLine($"prerelease={release.IsPrerelease.ToString().ToLowerInvariant()}");
    Console.WriteLine($"bootstrap_baseline={release.UsedBootstrapBaseline.ToString().ToLowerInvariant()}");
    return 0;
}
catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static IEnumerable<string> ReadGitTags()
{
    var startInfo = new ProcessStartInfo("git", "tag --list") { RedirectStandardOutput = true, UseShellExecute = false };
    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git to read release tags.");
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0) throw new InvalidOperationException("Could not read release tags from git.");
    return output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

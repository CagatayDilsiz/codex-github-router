using CodexGithubRouter.Helpers;
namespace CodexGithubRouter.Git;

public static class GitRepositoryService
{
    public static async Task<string?> GetCommonDirectoryAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        var arguments = new string[] { "rev-parse", "--git-common-dir" };

        var process = await ProcessRunner.RunAsync(workingDirectory, "git", arguments, cancellationToken);
     
        if (process.ExitCode != 0)
        {
            await Console.Error.WriteLineAsync(
                $"Git command failed with exit code {process.ExitCode}: {process.Error}");

            return null;
        }

        var output = process.Output.Trim();

        if (string.IsNullOrWhiteSpace(output))
        {
            await Console.Error.WriteLineAsync(
                "Git command returned empty output for common directory.");

            return null;
        }

        if (Path.IsPathRooted(output))
        {
            return Path.GetFullPath(output);
        }

        return Path.GetFullPath(Path.Combine(workingDirectory, output));
    }
}
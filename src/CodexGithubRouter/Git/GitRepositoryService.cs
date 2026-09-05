using CodexGithubRouter.Helpers;
namespace CodexGithubRouter.Git;

public static class GitRepositoryService
{
    public static async Task<string?> GetRepositoryRootAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        var process = await ProcessRunner.RunAsync(workingDirectory, "git", new[] { "rev-parse", "--show-toplevel" }, cancellationToken);

        if (process.ExitCode != 0)
        {
            await Console.Error.WriteLineAsync($"Git command failed with exit code {process.ExitCode}: {process.Error}");
            return null;
        }

        var output = process.Output.Trim();
        if (string.IsNullOrWhiteSpace(output))
        {
            await Console.Error.WriteLineAsync("Git command returned empty output for repository root.");
            return null;
        }

        return Path.GetFullPath(output);
    }

    public static async Task<string?> GetWorktreeIdAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        var process = await ProcessRunner.RunAsync(workingDirectory, "git", new[] { "rev-parse", "--absolute-git-dir" }, cancellationToken);

        if (process.ExitCode != 0)
        {
            await Console.Error.WriteLineAsync($"Git command failed with exit code {process.ExitCode}: {process.Error}");
            return null;
        }

        var output = process.Output.Trim();

        if (string.IsNullOrWhiteSpace(output))
        {
            await Console.Error.WriteLineAsync("Git command returned empty output for the worktree git directory.");
            return null;
        }

        return Path.GetFullPath(output);
    }

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

    public static async Task<string?> GetConfigValueAsync(string workingDirectory, string key, CancellationToken cancellationToken = default) =>
        await GetConfigValueAsync(workingDirectory, key, environment: null, cancellationToken);

    public static async Task<string?> GetConfigValueAsync(string workingDirectory, string key, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken = default)
    {
        var process = await ProcessRunner.RunAsync(workingDirectory, "git", new[] { "config", "--get", key }, environment, cancellationToken);

        if (process.ExitCode != 0)
        {
            return null;
        }

        var output = process.Output?.Trim();
        return string.IsNullOrWhiteSpace(output) ? null : output;
    }
}

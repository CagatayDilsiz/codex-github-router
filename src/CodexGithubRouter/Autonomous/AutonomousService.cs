using CodexGithubRouter.Git;

namespace CodexGithubRouter.Autonomous;

public static class AutonomousService
{
    private const string AutonomousFileName = "codex-github-router.auto";

    public static async Task<bool> IsAutonomousAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return false;
        }

        try
        {
            var gitCommonDirectory = await GitRepositoryService.GetCommonDirectoryAsync(workingDirectory, cancellationToken);

            if (gitCommonDirectory is null)
            {
                return false;
            }

            var autonomousFilePath = Path.Combine(gitCommonDirectory, AutonomousFileName);

            return File.Exists(autonomousFilePath);
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(
                $"Error checking autonomous mode: {exception.Message}");

            return false;
        }
    }

    public static async Task EnableAutonomousAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            throw new ArgumentException("Invalid working directory.", nameof(workingDirectory));
        }

        var gitCommonDirectory = await GitRepositoryService.GetCommonDirectoryAsync(workingDirectory, cancellationToken);

        if (gitCommonDirectory is null)
        {
            throw new InvalidOperationException("Not a valid Git repository.");
        }

        var autonomousFilePath = Path.Combine(gitCommonDirectory, AutonomousFileName);

        if (!File.Exists(autonomousFilePath))
        {
            File.WriteAllText(autonomousFilePath, "Autonomous mode enabled.");
        }
    }

    public static async Task DisableAutonomousAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            throw new ArgumentException("Invalid working directory.", nameof(workingDirectory));
        }

        var gitCommonDirectory = await GitRepositoryService.GetCommonDirectoryAsync(workingDirectory, cancellationToken);

        if (gitCommonDirectory is null)
        {
            throw new InvalidOperationException("Not a valid Git repository.");
        }

        var autonomousFilePath = Path.Combine(gitCommonDirectory, AutonomousFileName);

        if (File.Exists(autonomousFilePath))
        {
            File.Delete(autonomousFilePath);
        }
    }

    public static async Task<bool> IsAutonomousEnabledAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        return await IsAutonomousAsync(workingDirectory, cancellationToken);
    }

}
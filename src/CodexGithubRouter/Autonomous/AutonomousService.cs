using CodexGithubRouter.Git;

namespace CodexGithubRouter.Autonomous;

public static class AutonomousService
{
    private const string AutonomousFileName = "codex-github-router.auto";

    public static async Task<bool> IsAutonomousAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        try
        {
            var autonomousFilePath = await GetAutonomousFilePathAsync(workingDirectory, cancellationToken);

            return File.Exists(autonomousFilePath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static async Task<bool> GetAutonomousStatusAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        var autonomousFilePath = await GetAutonomousFilePathAsync(workingDirectory, cancellationToken);
        return File.Exists(autonomousFilePath);
    }

    private static async Task<string> GetAutonomousFilePathAsync(string workingDirectory, CancellationToken cancellationToken = default)
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

        return Path.Combine(gitCommonDirectory, AutonomousFileName);
    }

    public static async Task EnableAutonomousAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        var autonomousFilePath = await GetAutonomousFilePathAsync(workingDirectory, cancellationToken);

        if (!File.Exists(autonomousFilePath))
        {
            File.WriteAllText(autonomousFilePath, "Autonomous mode enabled.");
        }
    }

    public static async Task DisableAutonomousAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        var autonomousFilePath = await GetAutonomousFilePathAsync(workingDirectory, cancellationToken);

        if (File.Exists(autonomousFilePath))
        {
            File.Delete(autonomousFilePath);
        }
    }

}
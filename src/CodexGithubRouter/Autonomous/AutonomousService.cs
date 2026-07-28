using CodexGithubRouter.Git;
using CodexGithubRouter.Configurations;
using CodexGithubRouter.GitHub;
using CodexGithubRouter.Workflow;
using System.Text.Json;

namespace CodexGithubRouter.Autonomous;

public static class AutonomousService
{
    private const string AutonomousFileName = "codex-github-router.auto";
    private const string AutonomousStateFileName = "codex-github-router.auto.json";
    private static IAutonomousBoundary DefaultBoundary => new GitHubAutonomousBoundary();

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

    public static async Task<bool> IsAutonomousAsync(string workingDirectory, IAutonomousBoundary boundary, CancellationToken cancellationToken = default)
    {
        try
        {
            return File.Exists(await GetAutonomousFilePathAsync(workingDirectory, boundary, cancellationToken));
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

    public static async Task<bool> GetAutonomousStatusAsync(string workingDirectory, IAutonomousBoundary boundary, CancellationToken cancellationToken = default)
        => File.Exists(await GetAutonomousFilePathAsync(workingDirectory, boundary, cancellationToken));

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

    public static async Task<AutonomousEnableResult> EnableAutonomousAsync(string workingDirectory, CancellationToken cancellationToken = default)
        => await EnableAutonomousAsync(workingDirectory, ConfigurationPaths.Default, DefaultBoundary, cancellationToken);

    public static async Task<AutonomousEnableResult> EnableAutonomousAsync(string workingDirectory, ConfigurationPathSet paths, CancellationToken cancellationToken = default)
        => await EnableAutonomousAsync(workingDirectory, paths, DefaultBoundary, cancellationToken);

    public static async Task<AutonomousEnableResult> EnableAutonomousAsync(string workingDirectory, ConfigurationPathSet paths, IAutonomousBoundary boundary, CancellationToken cancellationToken = default)
    {
        var autonomousFilePath = await GetAutonomousFilePathAsync(workingDirectory, boundary, cancellationToken);
        var configuration = await WorkflowConfigurationService.LoadOrCreateAsync(paths, cancellationToken);
        var requiredLabels = WorkflowLabelConfiguration.GetRequiredLabels(configuration);
        var existingLabels = await boundary.GetRepositoryLabelNamesAsync(workingDirectory, cancellationToken);

        var createdCount = 0;
        foreach (var label in requiredLabels)
        {
            if (existingLabels.Contains(label))
            {
                continue;
            }

            await boundary.CreateLabelAsync(workingDirectory, label, cancellationToken);
            existingLabels.Add(label);
            createdCount++;
        }

        var stateFilePath = Path.Combine(Path.GetDirectoryName(autonomousFilePath)!, AutonomousStateFileName);
        var fingerprint = WorkflowLabelConfiguration.GetFingerprint(configuration);
        var previousState = await ReadStateAsync(stateFilePath, cancellationToken);
        var state = new AutonomousState
        {
            ConfigurationFingerprint = fingerprint
        };

        await using (var stream = File.Create(stateFilePath))
        {
            await JsonSerializer.SerializeAsync(stream, state, cancellationToken: cancellationToken);
        }

        if (!File.Exists(autonomousFilePath))
        {
            File.WriteAllText(autonomousFilePath, "Autonomous mode enabled.");
        }

        return new AutonomousEnableResult
        {
            CreatedLabelCount = createdCount,
            ConfigurationChanged = previousState is not null && !string.Equals(previousState.ConfigurationFingerprint, fingerprint, StringComparison.Ordinal)
        };
    }

    private static async Task<string> GetAutonomousFilePathAsync(string workingDirectory, IAutonomousBoundary boundary, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            throw new ArgumentException("Invalid working directory.", nameof(workingDirectory));
        }

        var gitCommonDirectory = await boundary.GetGitCommonDirectoryAsync(workingDirectory, cancellationToken);
        if (gitCommonDirectory is null)
        {
            throw new InvalidOperationException("Not a valid Git repository.");
        }

        return Path.Combine(gitCommonDirectory, AutonomousFileName);
    }

    public static async Task DisableAutonomousAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        var autonomousFilePath = await GetAutonomousFilePathAsync(workingDirectory, cancellationToken);

        if (File.Exists(autonomousFilePath))
        {
            File.Delete(autonomousFilePath);
        }

        var stateFilePath = Path.Combine(Path.GetDirectoryName(autonomousFilePath)!, AutonomousStateFileName);
        if (File.Exists(stateFilePath))
        {
            File.Delete(stateFilePath);
        }
    }

    public static async Task DisableAutonomousAsync(string workingDirectory, IAutonomousBoundary boundary, CancellationToken cancellationToken = default)
    {
        var autonomousFilePath = await GetAutonomousFilePathAsync(workingDirectory, boundary, cancellationToken);
        if (File.Exists(autonomousFilePath)) File.Delete(autonomousFilePath);

        var stateFilePath = Path.Combine(Path.GetDirectoryName(autonomousFilePath)!, AutonomousStateFileName);
        if (File.Exists(stateFilePath)) File.Delete(stateFilePath);
    }

    private static async Task<AutonomousState?> ReadStateAsync(string stateFilePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(stateFilePath))
        {
            return null;
        }

        await using var stream = File.OpenRead(stateFilePath);
        return await JsonSerializer.DeserializeAsync<AutonomousState>(stream, cancellationToken: cancellationToken);
    }

}

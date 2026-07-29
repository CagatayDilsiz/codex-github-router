using CodexGithubRouter.Git;
using CodexGithubRouter.GitHub;

namespace CodexGithubRouter.Autonomous;

public interface IAutonomousBoundary
{
    Task<string?> GetRepositoryRootAsync(string workingDirectory, CancellationToken cancellationToken = default);
    Task<string?> GetGitCommonDirectoryAsync(string workingDirectory, CancellationToken cancellationToken = default);
    Task<HashSet<string>> GetRepositoryLabelNamesAsync(string workingDirectory, CancellationToken cancellationToken = default);
    Task CreateLabelAsync(string workingDirectory, string labelName, CancellationToken cancellationToken = default);
}

public sealed class GitHubAutonomousBoundary : IAutonomousBoundary
{
    public Task<string?> GetRepositoryRootAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
        GitRepositoryService.GetRepositoryRootAsync(workingDirectory, cancellationToken);

    public Task<string?> GetGitCommonDirectoryAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
        GitRepositoryService.GetCommonDirectoryAsync(workingDirectory, cancellationToken);

    public Task<HashSet<string>> GetRepositoryLabelNamesAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
        GitHubCliService.GetRepositoryLabelNamesAsync(workingDirectory, cancellationToken);

    public Task CreateLabelAsync(string workingDirectory, string labelName, CancellationToken cancellationToken = default) =>
        GitHubCliService.CreateLabelAsync(workingDirectory, labelName, cancellationToken);
}

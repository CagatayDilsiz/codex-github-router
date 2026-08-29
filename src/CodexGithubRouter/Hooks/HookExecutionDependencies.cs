using CodexGithubRouter.Autonomous;
using CodexGithubRouter.Configurations;
using CodexGithubRouter.Diagnostics;
using CodexGithubRouter.Git;
using CodexGithubRouter.GitHub;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Hooks;

public sealed class HookExecutionDependencies
{
    public Func<string, Task<bool>> IsAutonomousAsync { get; init; } = workingDirectory => AutonomousService.IsAutonomousAsync(workingDirectory);

    public Func<string, Task<RouterConfiguration>> LoadConfigurationAsync { get; init; } = workingDirectory => WorkflowConfigurationService.LoadEffectiveAsync(workingDirectory);

    public Func<string, Task<string?>> ResolveGitCommonDirectoryAsync { get; init; } = workingDirectory => GitRepositoryService.GetCommonDirectoryAsync(workingDirectory);

    public Func<string?, Task<DiagnosticsPolicy?>> ResolveDiagnosticsPolicyAsync { get; init; } = workingDirectory => WorkflowConfigurationService.TryResolveDiagnosticsPolicyAsync(workingDirectory);

    public Func<string, CancellationToken, Task<string?>> ResolveAuthenticatedGitHubLoginAsync { get; init; } = (workingDirectory, cancellationToken) => GitHubCliService.GetAuthenticatedUserAsync(workingDirectory, cancellationToken);
}

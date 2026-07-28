using CodexGithubRouter.Autonomous;
using CodexGithubRouter.Configurations;
using CodexGithubRouter.Git;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Hooks;

public sealed class HookExecutionDependencies
{
    public Func<string, Task<bool>> IsAutonomousAsync { get; init; } = workingDirectory => AutonomousService.IsAutonomousAsync(workingDirectory);

    public Func<Task<RouterConfiguration>> LoadConfigurationAsync { get; init; } = () => WorkflowConfigurationService.LoadOrCreateAsync();

    public Func<string, Task<string?>> ResolveGitCommonDirectoryAsync { get; init; } = workingDirectory => GitRepositoryService.GetCommonDirectoryAsync(workingDirectory);
}

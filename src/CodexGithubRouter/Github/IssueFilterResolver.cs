using CodexGithubRouter.Configurations;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.GitHub;

public static class IssueFilterResolver
{
    public static async Task<IssueFilters?> ByState(WorkflowState state = WorkflowState.Ready)
    {
        var configuration = await WorkflowConfigurationService.LoadOrCreateAsync();

        if (configuration is null)
        {
            Console.Error.WriteLine("Failed to load workflow configuration.");
            return null;
        }

        if (!configuration.States.TryGetValue(state, out var readyRules) || readyRules.Count == 0)
        {
            Console.Error.WriteLine($"The {state} workflow state must contain at least one rule.");
            return null;
        }

        var issueFilters = IssueFilterCompiler.Compile(readyRules, configuration.IssueSelection);

        issueFilters ??= new IssueFilters();

        issueFilters.RouterConfiguration = configuration;

        return issueFilters;
    }
}
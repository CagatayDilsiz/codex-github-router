using CodexGithubRouter.Configurations;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.GitHub;

public static class IssueFilterResolver
{
    public static IssueFilters ByState(RouterConfiguration configuration, WorkflowState state = WorkflowState.Ready)
    {      

        if (!configuration.States.TryGetValue(state, out var stateRules) || stateRules.Count == 0)
        {
            throw new InvalidOperationException($"No match rules found for workflow state '{state}'.");
        }

        var issueFilters = IssueFilterCompiler.Compile(stateRules, configuration.IssueSelection);

       
        return issueFilters;
    }
}
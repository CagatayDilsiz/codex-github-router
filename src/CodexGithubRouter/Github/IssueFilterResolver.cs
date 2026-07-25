using CodexGithubRouter.Configurations;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.GitHub;

public static class IssueFilterResolver
{
    public static IssueFilters ByState(RouterConfiguration configuration, WorkflowState state = WorkflowState.Ready, IssueSelectionConfiguration? issueSelection = null)
    {      

        if (!configuration.States.TryGetValue(state, out var stateRules) || stateRules.Count == 0)
        {
            throw new InvalidOperationException($"No match rules found for workflow state '{state}'.");
        }

        issueSelection ??= configuration.DefaultIssueSelection;

        var issueFilters = IssueFilterCompiler.Compile(stateRules, issueSelection);

       
        return issueFilters;
    }
}
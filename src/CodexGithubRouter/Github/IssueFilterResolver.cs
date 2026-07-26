using CodexGithubRouter.Configurations;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.GitHub;

public static class IssueFilterResolver
{
    public static IssueFilters ByState(RouterConfiguration configuration, WorkflowState state = WorkflowState.Ready, int? limitOverride = null)
    {
        var issueSelection = configuration.DefaultIssueSelection with
        {
            Limit = limitOverride ?? configuration.DefaultIssueSelection.Limit
        };

        if (issueSelection.Limit <= 0)
        {
            throw new InvalidOperationException("Limit must be a positive integer.");
        }

        if (!configuration.States.TryGetValue(state, out var stateRules) || stateRules.Count == 0)
        {
            throw new InvalidOperationException($"No match rules found for workflow state '{state}'.");
        }       

        var issueFilters = IssueFilterCompiler.Compile(stateRules, issueSelection);

       
        return issueFilters;
    }
}
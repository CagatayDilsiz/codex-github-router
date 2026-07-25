using CodexGithubRouter.GitHub;

namespace CodexGithubRouter.Workflow;

public static class PullRequestTransitionPlanner
{
    public static PullRequestTransition Plan(PullRequest pullRequest, PullRequestState targetState, RouterConfiguration configuration)
    {  

        if (!configuration.PullRequestStates.TryGetValue(targetState, out var stateRules) || stateRules.Count == 0)
        {
            throw new InvalidOperationException($"No match rules found for pull request state '{targetState}'.");
        }

        var targetLabels = stateRules.Where(rule => rule.Type == IssueMatchRuleType.Label).SelectMany(state => state.Values).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (targetLabels.Count == 0)
        {
            throw new InvalidOperationException($"No labels found for pull request state '{targetState}'.");
        }

        var otherLabels = configuration.PullRequestStates.Where(kvp => kvp.Key != targetState).SelectMany(kvp => kvp.Value).Where(rule => rule.Type == IssueMatchRuleType.Label).SelectMany(rule => rule.Values).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var currentLabels = pullRequest.Labels.Select(label => label.Name).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new PullRequestTransition
        {
            PullRequestNumber = pullRequest.Number,
            LabelsToAdd = targetLabels.Except(currentLabels, StringComparer.OrdinalIgnoreCase).ToList(),
            LabelsToRemove = currentLabels.Intersect(otherLabels, StringComparer.OrdinalIgnoreCase).ToList()
        };
           
    }
}
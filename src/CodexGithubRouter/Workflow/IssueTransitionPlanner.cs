using CodexGithubRouter.GitHub;

namespace CodexGithubRouter.Workflow;

public static class IssueTransitionPlanner
{
    public static IssueTransition Plan(Issue issue, WorkflowState targetState, RouterConfiguration configuration)
    {  

        if (!configuration.States.TryGetValue(targetState, out var stateRules) || stateRules.Count == 0)
        {
            throw new InvalidOperationException($"No match rules found for workflow state '{targetState}'.");
        }

        var targetLables = stateRules.Where(rule => rule.Type == IssueMatchRuleType.Label).SelectMany(state => state.Values).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (targetLables.Count == 0)
        {
            throw new InvalidOperationException($"No labels found for workflow state '{targetState}'.");
        }

        var otherLabels = configuration.States.Where(kvp => kvp.Key != targetState).SelectMany(kvp => kvp.Value).Where(rule => rule.Type == IssueMatchRuleType.Label).SelectMany(rule => rule.Values).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var currentLabels = issue.Labels.Select(label => label.Name).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new IssueTransition
        {
            IssueNumber = issue.Number,
            LabelsToAdd = targetLables.Except(currentLabels, StringComparer.OrdinalIgnoreCase).ToList(),
            LabelsToRemove = currentLabels.Intersect(otherLabels, StringComparer.OrdinalIgnoreCase).ToList()
        };
           
    }
}
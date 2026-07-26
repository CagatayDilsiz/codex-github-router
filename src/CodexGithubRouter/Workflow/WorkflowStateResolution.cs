namespace CodexGithubRouter.Workflow;

public sealed class WorkflowStateResolution<TState> where TState : struct, Enum
{
    public Dictionary<TState, List<string>> MatchedLabels { get; init; } = new();
    public bool IsAmbiguous => MatchedLabels.Count > 1;

    public string DescribeConflict(string domain) => $"Ambiguous {domain} workflow state: {string.Join("; ", MatchedLabels.OrderBy(entry => entry.Key.ToString()).Select(entry => $"{entry.Key} ({string.Join(", ", entry.Value.OrderBy(label => label, StringComparer.OrdinalIgnoreCase))})"))}.";
}

public static class WorkflowStateResolver
{
    public static WorkflowStateResolution<TState> Resolve<TState>(IEnumerable<string> itemLabels, Dictionary<TState, List<IssueMatchRule>> states) where TState : struct, Enum
    {
        var current = itemLabels.Where(label => !string.IsNullOrWhiteSpace(label)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matched = new Dictionary<TState, List<string>>();

        foreach (var state in states)
        {
            var labels = state.Value.Where(rule => rule.Type == IssueMatchRuleType.Label).SelectMany(rule => rule.Values).Where(label => !string.IsNullOrWhiteSpace(label)).Where(current.Contains).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(label => label, StringComparer.OrdinalIgnoreCase).ToList();
            if (labels.Count > 0)
            {
                matched[state.Key] = labels;
            }
        }

        return new WorkflowStateResolution<TState> { MatchedLabels = matched };
    }
}

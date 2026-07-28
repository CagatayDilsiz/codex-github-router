using CodexGithubRouter.GitHub;

namespace CodexGithubRouter.Workflow;

public static class RepositoryGateService
{
    public static async Task<List<Issue>> GetOpenGatedIssuesAsync(string workingDirectory, RouterConfiguration configuration, int scanLimit = 30)
    {
        var labels = GetLabels(configuration);
        if (labels.Count == 0)
        {
            return new List<Issue>();
        }

        return await GitHubCliService.GetIssuesAsync(workingDirectory, new IssueFilters
        {
            Labels = labels.ToList(),
            Limit = scanLimit
        }, addLinkedPRToSelection: true);
    }

    public static IReadOnlyList<string> GetLabels(RouterConfiguration configuration) =>
        configuration.Policies.RepositoryGate.Labels
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool IsGated(Issue issue, RouterConfiguration configuration)
    {
        var labels = issue.Labels.Select(label => label.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return GetLabels(configuration).Any(labels.Contains);
    }

    public static string FormatGateLabel(RouterConfiguration configuration) =>
        string.Join(" or ", GetLabels(configuration).Select(label => $"'{label}'"));
}

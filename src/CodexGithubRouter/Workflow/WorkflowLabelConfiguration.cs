using System.Security.Cryptography;
using System.Text;

namespace CodexGithubRouter.Workflow;

public static class WorkflowLabelConfiguration
{
    public static IReadOnlyList<string> GetRequiredLabels(RouterConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return GetLabelMappings(configuration.States)
            .Concat(GetLabelMappings(configuration.PullRequestStates))
            .Select(mapping => mapping.Label)
            .Concat(RepositoryGateService.GetLabels(configuration))
            .Select(label => label.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string GetFingerprint(RouterConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var content = string.Join("\n", GetLabelMappings(configuration.States)
            .Select(mapping => $"issue:{mapping.State}:{mapping.Label.Trim().ToLowerInvariant()}")
            .Concat(GetLabelMappings(configuration.PullRequestStates)
                .Select(mapping => $"pull-request:{mapping.State}:{mapping.Label.Trim().ToLowerInvariant()}"))
            .Concat(RepositoryGateService.GetLabels(configuration).Select(label => $"policy:repository-gate:{label.ToLowerInvariant()}"))
            .OrderBy(value => value, StringComparer.Ordinal));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    public static void ValidateNoConflictingLabels(RouterConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var issueLabels = GetLabelMappings(configuration.States).ToList();
        var pullRequestLabels = GetLabelMappings(configuration.PullRequestStates).ToList();

        ValidateNormalizedLabelNames(issueLabels);
        ValidateNormalizedLabelNames(pullRequestLabels);
        ValidateDomain("issue", issueLabels);
        ValidateDomain("pull request", pullRequestLabels);

        var workflowLabels = issueLabels.Concat(pullRequestLabels).Select(mapping => mapping.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gateLabels = RepositoryGateService.GetLabels(configuration);
        if (gateLabels.Count == 0)
        {
            throw new InvalidOperationException("At least one repository gate label must be configured.");
        }

        foreach (var gateLabel in gateLabels)
        {
            if (workflowLabels.Contains(gateLabel))
            {
                throw new InvalidOperationException($"Repository gate label '{gateLabel}' must not also be a workflow state label.");
            }

            if (!string.Equals(gateLabel, gateLabel.Trim(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Repository gate label '{gateLabel}' must not have leading or trailing whitespace.");
            }
        }
    }

    private static IEnumerable<(string State, string Label)> GetLabelMappings<TState>(Dictionary<TState, List<IssueMatchRule>> states)
        where TState : struct, Enum
    {
        return states.SelectMany(entry => entry.Value
            .Where(rule => rule.Type == IssueMatchRuleType.Label)
            .SelectMany(rule => rule.Values)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => (entry.Key.ToString(), label)));
    }

    private static void ValidateDomain(string domain, IEnumerable<(string State, string Label)> mappings)
    {
        foreach (var labels in mappings.GroupBy(mapping => mapping.Label, StringComparer.OrdinalIgnoreCase))
        {
            var states = labels.Select(mapping => mapping.State).Distinct(StringComparer.Ordinal).ToList();

            if (states.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Workflow label '{labels.Key}' is configured for multiple {domain} states: {string.Join(", ", states)}.");
            }
        }
    }

    private static void ValidateNormalizedLabelNames(IEnumerable<(string State, string Label)> mappings)
    {
        foreach (var mapping in mappings)
        {
            if (!string.Equals(mapping.Label, mapping.Label.Trim(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Workflow label '{mapping.Label}' for state '{mapping.State}' must not have leading or trailing whitespace.");
            }
        }
    }
}

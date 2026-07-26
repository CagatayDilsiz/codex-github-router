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
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string GetFingerprint(RouterConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var content = string.Join("\n", GetLabelMappings(configuration.States)
            .Select(mapping => $"issue:{mapping.State}:{mapping.Label.ToLowerInvariant()}")
            .Concat(GetLabelMappings(configuration.PullRequestStates)
                .Select(mapping => $"pull-request:{mapping.State}:{mapping.Label.ToLowerInvariant()}"))
            .OrderBy(value => value, StringComparer.Ordinal));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    public static void ValidateNoConflictingLabels(RouterConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        ValidateDomain("issue", GetLabelMappings(configuration.States));
        ValidateDomain("pull request", GetLabelMappings(configuration.PullRequestStates));
    }

    private static IEnumerable<(string State, string Label)> GetLabelMappings<TState>(Dictionary<TState, List<IssueMatchRule>> states)
        where TState : struct, Enum
    {
        return states.SelectMany(entry => entry.Value
            .Where(rule => rule.Type == IssueMatchRuleType.Label)
            .SelectMany(rule => rule.Values)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => (entry.Key.ToString(), label.Trim())));
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
}

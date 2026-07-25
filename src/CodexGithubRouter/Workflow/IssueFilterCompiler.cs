using CodexGithubRouter.GitHub;

namespace CodexGithubRouter.Workflow;

public static class IssueFilterCompiler
{
    public static IssueFilters Compile(List<IssueMatchRule> matchRules, IssueSelectionConfiguration selectionConfig)
    {
        if (matchRules == null || matchRules.Count == 0)
        {
            throw new ArgumentException("Match rules cannot be null or empty.", nameof(matchRules));
        }

        if (selectionConfig == null)
        {
            throw new ArgumentNullException(nameof(selectionConfig), "Selection configuration cannot be null.");
        }

        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var searchTerms = new List<string>();

        foreach (var rule in matchRules)
        {
            CompileRule(rule, labels, searchTerms);
        }

        return new IssueFilters
        {
            Labels = labels.ToList(),
            SearchTerms = searchTerms,
            Limit = selectionConfig.Limit,
            SortBy = selectionConfig.SortBy,
            SortDirection = selectionConfig.Direction
        };
    }

    private static void CompileRule(IssueMatchRule rule, ISet<string> labels, ICollection<string> searchTerms)
    {
        if (rule == null)
        {
            throw new ArgumentNullException(nameof(rule), "Match rule cannot be null.");
        }

        switch (rule.Type)
        {
            case IssueMatchRuleType.Label:
                AddLabels(rule.Values, labels);
                break;
            case IssueMatchRuleType.Assignee:
                AddQualifiedValues(rule.Values, "assignee", searchTerms);
                break;
            case IssueMatchRuleType.Milestone:
                AddQualifiedValues(rule.Values, "milestone", searchTerms);
                break;
            case IssueMatchRuleType.Search:
                AddRawSearch(rule.Query, searchTerms);
                break;
            case IssueMatchRuleType.BodyContains:
                AddQualifiedTextSearch(rule.Values, "in:body", searchTerms);
                break;
            case IssueMatchRuleType.TitleContains:
                AddQualifiedTextSearch(rule.Values, "in:title", searchTerms);
                break;
            default:
                throw new InvalidOperationException($"Unsupported match rule type: {rule.Type}");
        }
    }

    private static void AddLabels(IEnumerable<string> values, ISet<string> labels)
    {
        if (values == null)
        {
            throw new ArgumentNullException(nameof(values), "Label values cannot be null.");
        }

        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                labels.Add(value.Trim());
            }
        }
    }

    private static void AddRawSearch(string? query, ICollection<string> searchTerms)
    {
        if (!string.IsNullOrWhiteSpace(query))
        {
            searchTerms.Add(query.Trim());
        }
    }

    private static void AddQualifiedTextSearch(IEnumerable<string> values, string qualifier, ICollection<string> searchTerms)
    {
        if (values == null)
        {
            throw new ArgumentNullException(nameof(values), "Search values cannot be null.");
        }

        foreach (var value in NormalizeValues(values))
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                var quotedValue = QuoteSearchValue(value.Trim());
                searchTerms.Add($"{quotedValue} {qualifier}");
            }
        }
    }

    private static void AddQualifiedValues(IEnumerable<string> values, string qualifier, ICollection<string> searchTerms)
    {
        if (values == null)
        {
            throw new ArgumentNullException(nameof(values), "Search values cannot be null.");
        }

        foreach (var value in NormalizeValues(values))
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                var quotedValue = QuoteSearchValue(value.Trim());
                searchTerms.Add($"{qualifier}:{quotedValue}");
            }
        }
    }

    private static IEnumerable<string> NormalizeValues(IEnumerable<string>? values)
    {
        if (values is null)
        {
            yield break;
        }

        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value.Trim();
            }
        }
    }

    private static string QuoteSearchValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Search value cannot be null or whitespace.", nameof(value));
        }

        // If the value contains spaces or special characters, wrap it in quotes
        if (value.Contains(' ') || value.Contains('"') || value.Contains('\''))
        {
            return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""; // Escape double quotes
        }

        return value;
    }

    
}
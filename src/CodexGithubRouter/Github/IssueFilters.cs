using CodexGithubRouter.Configurations;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.GitHub;

public sealed class IssueFilters
{
    public List<string> Labels { get; init; } = [];

    public List<string> SearchTerms { get; init; } = [];

    public int? Limit { get; init; }

    public IssueSortField? SortBy { get; init; }

    public SortDirection? SortDirection { get; init; }

    /// <summary>
    /// Gets or sets the router configuration associated with the issue filters.
    /// This property is optional and can be null if no router configuration is provided.
    /// </summary>
    public RouterConfiguration? RouterConfiguration { get; set; }
}

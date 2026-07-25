using CodexGithubRouter.Configurations;

namespace CodexGithubRouter.GitHub;

public sealed class IssueFilters
{
    public List<string> Labels { get; init; } = [];

    public List<string> SearchTerms { get; init; } = [];

    public int? Limit { get; init; }

    public IssueSortField? SortBy { get; init; }

    public SortDirection? SortDirection { get; init; }
}

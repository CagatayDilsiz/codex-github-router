using CodexGithubRouter.Configurations;

namespace CodexGithubRouter.Workflow;

public sealed class IssueSelectionConfiguration
{
    public int? Limit { get; init; } = 1;

    public IssueSortField SortBy { get; init; } = IssueSortField.CreatedAt;

    public SortDirection Direction { get; init; } = SortDirection.Ascending;
}

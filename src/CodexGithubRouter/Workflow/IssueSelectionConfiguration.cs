using CodexGithubRouter.Configurations;

namespace CodexGithubRouter.Workflow;

public record IssueSelectionConfiguration(int Limit = 1, IssueSortField SortBy = IssueSortField.CreatedAt, SortDirection Direction = SortDirection.Ascending);
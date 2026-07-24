namespace CodexGithubRouter.GitHub;

public class IssueFilters
{
    public List<string> Labels { get; set; } = ["codex:ready"];
    public int? Limit { get; set; } = 1;
    public SearchFilters Search { get; set; } = new SearchFilters();
}

public class SearchFilters
{
    public bool? SortByCreationDate { get; set; } = true;
}
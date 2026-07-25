namespace CodexGithubRouter.Workflow;

public sealed class IssueMatchRule
{
    public IssueMatchRuleType Type { get; init; }
    public List<string> Values { get; init; } = new List<string>();
    public string? Query { get; init; }
}
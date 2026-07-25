namespace CodexGithubRouter.Workflow;

public sealed class WorkflowTask
{
    public TaskType Type { get; init; }
    public int IssueNumber { get; init; }
    public int? PullRequestNumber { get; init; }
}

public enum TaskType
{
    ChangeRequest = 1,
    LinkPullRequestsToIssues = 2,
    NewIssue = 3,    
    
}
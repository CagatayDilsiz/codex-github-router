namespace CodexGithubRouter.Workflow;

public sealed class WorkflowTask
{
    public TaskType Type { get; init; }
    public int IssueNumber { get; init; }
    public int? PullRequestNumber { get; init; }
    public WorkflowTaskStatus Status { get; init; } = new WorkflowTaskStatus();
    
}

public enum TaskType
{
    None = 0,
    ChangeRequest = 1,
    LinkPullRequestsToIssues = 2,
    NewIssue = 3,    
    
}

public class WorkflowTaskStatus
{
    public List<int> LinkedPullRequests { get; init; } = new List<int>();
    public bool HookBlocker { get; init; } = false;

    public string Message { get; init; } = "";
}
namespace CodexGithubRouter.Workflow;

public sealed class WorkflowItem
{
    public WorkflowItemType Type { get; init; }
    public int IssueNumber { get; init; }
    public int? PullRequestNumber { get; init; }
    public WorkflowTaskStatus Status { get; init; } = new WorkflowTaskStatus();
    
}

public enum WorkflowItemType
{
    Unknown = 0,
    ChangeRequest = 1,
    LinkPullRequestsToIssues = 2,
    NewIssue = 3,
    ResumeInProgressIssue,
    AwaitingReview,
    AwaitingMerge,
    Deferred,
    ClosedWithoutMerge,
    UnknownPullRequestState,
    CloseIssue 
}

public class WorkflowTaskStatus
{
    public List<int> LinkedPullRequests { get; init; } = new List<int>(); 

    public string Message { get; init; } = "";
}

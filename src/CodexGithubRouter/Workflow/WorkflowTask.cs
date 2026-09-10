namespace CodexGithubRouter.Workflow;

public sealed class WorkflowItem
{
    public WorkflowItemType Type { get; init; }
    public int IssueNumber { get; init; }
    public int? PullRequestNumber { get; init; }
    public WorkflowTaskStatus Status { get; init; } = new WorkflowTaskStatus();

    public int SelectionRank { get; set; }

    /// <summary>
    /// GitHub login of the requested reviewer. Present only for
    /// <see cref="WorkflowItemType.PullRequestReview"/> work items. The review claim identity is
    /// (PullRequestNumber, ReviewerLogin).
    /// </summary>
    public string? ReviewerLogin { get; init; }

    /// <summary>
    /// Stable review-cycle marker (the GitHub review-request node ID) captured from the pull
    /// request when the review work was discovered. Persisted on the review claim at acquisition
    /// so a submitted-then-re-requested review is recognized as a new cycle.
    /// </summary>
    public string? ReviewCycleId { get; init; }
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
    CloseIssue,
    RecoverCompletedIssue,
    RecoverCurrentPullRequest,
    RepositoryGateBlock,
    PullRequestReview
}

public class WorkflowTaskStatus
{
    public List<int> LinkedPullRequests { get; init; } = new List<int>(); 

    public string Message { get; init; } = "";
}

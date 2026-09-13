namespace CodexGithubRouter.Workflow;

public sealed class WorkflowItem
{
    public WorkflowItemType Type { get; init; }

    /// <summary>
    /// Issue number for issue-derived workflow items (new/in-progress/completed/recovery/gate
    /// tasks). Review work is PR-native and leaves this <c>null</c> when the pull request is not
    /// required to close an issue.
    /// </summary>
    public int? IssueNumber { get; init; }
    public int? PullRequestNumber { get; init; }
    public WorkflowTaskStatus Status { get; init; } = new WorkflowTaskStatus();

    /// <summary>
    /// Deterministic provenance of the workflow-state classification for pull-request-linked tasks.
    /// Carried into the routing plan so the read-only explanation can attribute a task to CGR labels,
    /// native GitHub signals, or label-less lifecycle recovery without parsing message text.
    /// <c>null</c> for structural tasks (merged/closed) that carry no evaluative classification.
    /// </summary>
    public WorkflowItemSource? Source { get; init; }

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

/// <summary>
/// The source that classified a pull-request-linked workflow task. Used by <c>cgr explain</c> to
/// report exactly why a task reached its state instead of inferring provenance from message text.
/// </summary>
public enum WorkflowItemSource
{
    /// <summary>CGR workflow labels classified the task; evaluative native signals did not override them.</summary>
    Labels,

    /// <summary>No CGR workflow label was present and GitHub-native signals classified the task.</summary>
    NativeSignals,

    /// <summary>Label-less work with no usable native signal data fell through to lifecycle recovery / unknown-state handling.</summary>
    Recovery
}

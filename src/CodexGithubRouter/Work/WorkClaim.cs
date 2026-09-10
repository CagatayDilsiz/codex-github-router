namespace CodexGithubRouter.Work;

public enum WorkClaimType
{
    Implementation,
    ChangeRequest,
    Review
}

public sealed class WorkClaim
{
    public Guid ClaimId { get; init; }
    public long Version { get; init; }

    /// <summary>
    /// Relocation-safe worktree identity: the main worktree is the stable sentinel
    /// <see cref="WorkClaimStore.MainWorktreeIdentity"/> and every linked worktree is identified
    /// relative to the Git common directory, so ownership survives repository relocation. Legacy
    /// claim files store absolute git-dir paths and are matched by resolving them against the
    /// current common directory.
    /// </summary>
    public string WorktreeId { get; init; } = string.Empty;

    /// <summary>
    /// Diagnostic-only absolute git-dir of the worktree at acquisition time. Never used for
    /// identity matching or staleness, so it may be stale after the repository is relocated.
    /// </summary>
    public string? WorktreePath { get; init; }

    public string OwnerSessionId { get; init; } = string.Empty;

    /// <summary>
    /// Issue number for implementation and change-request claims, where it is always required.
    /// Review work is PR-native: its claim is keyed on (PullRequestNumber, ReviewerLogin) and the
    /// issue number is optional/absent (<c>null</c>) when the review has no associated issue.
    /// </summary>
    public int? IssueNumber { get; init; }

    public int? PullRequestNumber { get; init; }
    public WorkClaimType WorkType { get; init; }
    public string? WorkerProfile { get; init; }
    public string? Model { get; init; }
    public DateTimeOffset ClaimedIssueUpdatedAt { get; init; }
    public DateTimeOffset ClaimedAt { get; init; }
    public DateTimeOffset LastUpdatedAt { get; init; }

    /// <summary>
    /// GitHub login of the requested reviewer. Present only for <see cref="WorkClaimType.Review"/>
    /// claims. The claim identity for review work is (PullRequestNumber, ReviewerLogin).
    /// </summary>
    public string? ReviewerLogin { get; init; }

    /// <summary>
    /// Stable marker captured at review-claim acquisition that identifies the review cycle.
    /// For example, this is the node ID of the reviewer's latest <em>submitted</em> review at
    /// acquisition time, or <c>null</c> when the reviewer has never submitted a review on the
    /// pull request. Used to distinguish an in-progress cycle from a later re-request after the
    /// reviewer submitted a review.
    /// </summary>
    public string? ReviewCycleId { get; init; }

    /// <summary>
    /// True when the review-cycle baseline was captured at claim acquisition. Distinguishes a
    /// freshly acquired claim with <em>no prior submitted review</em> (<c>true</c>,
    /// <see cref="ReviewCycleId"/> <c>null</c> — any first submitted review starts a new cycle)
    /// from a legacy claim that predates review-cycle baselines (<c>false</c> — kept
    /// conservatively current while the reviewer is requested). Existing stored claims without the
    /// field deserialize as <c>false</c>, preserving the legacy behavior.
    /// </summary>
    public bool ReviewBaselineCaptured { get; init; }
}

public sealed class WorkClaimAcquisitionResult
{
    public bool Acquired { get; init; }
    public WorkClaim? Claim { get; init; }
    public string? BlockReason { get; init; }
}

public sealed class WorkClaimSet
{
    public List<WorkClaim> Claims { get; init; } = new();
}

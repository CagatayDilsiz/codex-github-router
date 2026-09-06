namespace CodexGithubRouter.Work;

public enum WorkClaimType
{
    Implementation,
    ChangeRequest
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
    public int IssueNumber { get; init; }
    public int? PullRequestNumber { get; init; }
    public WorkClaimType WorkType { get; init; }
    public string? WorkerProfile { get; init; }
    public string? Model { get; init; }
    public DateTimeOffset ClaimedIssueUpdatedAt { get; init; }
    public DateTimeOffset ClaimedAt { get; init; }
    public DateTimeOffset LastUpdatedAt { get; init; }
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

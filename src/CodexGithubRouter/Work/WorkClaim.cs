namespace CodexGithubRouter.Work;

public enum WorkClaimType
{
    Implementation,
    ChangeRequest
}

public sealed class WorkClaim
{
    public string OwnerSessionId { get; init; } = string.Empty;
    public int IssueNumber { get; init; }
    public int? PullRequestNumber { get; init; }
    public WorkClaimType WorkType { get; init; }
    public DateTimeOffset ClaimedAt { get; init; }
    public DateTimeOffset LastUpdatedAt { get; init; }
}

public sealed class WorkClaimAcquisitionResult
{
    public bool Acquired { get; init; }
    public WorkClaim? Claim { get; init; }
    public string? BlockReason { get; init; }
}

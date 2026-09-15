using CodexGithubRouter.GitHub;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Work;

/// <summary>
/// Everything the shared acquisition primitive needs to claim the work item selected by routing.
/// The hook and the daemon both build one of these from their own plan and session identity so the
/// acquisition-time issue refresh, worker/assignment revalidation and claim metadata are identical
/// across hosts.
/// </summary>
public sealed class WorkClaimAcquisitionRequest
{
    public string WorkingDirectory { get; init; } = string.Empty;

    public string GitCommonDirectory { get; init; } = string.Empty;

    public string WorktreeId { get; init; } = string.Empty;

    public string OwnerSessionId { get; init; } = string.Empty;

    public RouterConfiguration Configuration { get; init; } = new();

    public AssignmentIdentity? AssignmentIdentity { get; init; }

    public bool HasRepositoryGate { get; init; }

    public WorkflowItem SelectedTask { get; init; } = new();

    public string? Model { get; init; }
}

public sealed class WorkClaimAcquisitionOutcome
{
    public bool Acquired { get; init; }

    public WorkClaim? Claim { get; init; }

    public string? BlockReason { get; init; }

    /// <summary>
    /// True when acquisition was blocked by a peer worktree claiming the selected work between
    /// routing and acquisition. The hook re-routes the next eligible item in that case; the daemon
    /// treats it as a benign skip and the peer worktree resumes supervision in the same tick.
    /// </summary>
    public bool ConflictWithAnotherWorktree =>
        BlockReason?.Contains("another Git worktree", StringComparison.Ordinal) == true;
}

public sealed class WorkClaimAcquisitionDependencies
{
    public Func<string, int, CancellationToken, Task<Issue>> FetchIssueAsync { get; init; }
        = (workingDirectory, issueNumber, cancellationToken) =>
            GitHubCliService.GetIssueByNumberAsync(workingDirectory, issueNumber, cancellationToken);

    public Func<string, string, WorkClaim, CancellationToken, Task<WorkClaimAcquisitionResult>> TryAcquireClaimAsync { get; init; }
        = (gitCommonDirectory, worktreeId, requested, cancellationToken) =>
            WorkClaimStore.TryAcquireAsync(gitCommonDirectory, worktreeId, requested, cancellationToken);
}

/// <summary>
/// The single production claim-acquisition path shared by the hook and the daemon. It refreshes the
/// issue from GitHub at acquisition time, revalidates worker and assignment eligibility against that
/// fresh issue, and records the shared claim metadata (worker profile, model and the GitHub-derived
/// issue <c>UpdatedAt</c> baseline) exactly once, so both hosts observe the same claim semantics.
/// This never mutates anything beyond the claim file itself.
/// </summary>
public static class WorkClaimAcquisitionService
{
    public static Task<WorkClaimAcquisitionOutcome> AcquireAsync(WorkClaimAcquisitionRequest request, CancellationToken cancellationToken = default) =>
        AcquireAsync(request, new WorkClaimAcquisitionDependencies(), cancellationToken);

    public static async Task<WorkClaimAcquisitionOutcome> AcquireAsync(
        WorkClaimAcquisitionRequest request,
        WorkClaimAcquisitionDependencies dependencies,
        CancellationToken cancellationToken)
    {
        var claimType = MapWorkType(request.SelectedTask.Type);
        var isReviewAcquisition = claimType == WorkClaimType.Review;

        Issue? claimedIssue = null;
        if (!isReviewAcquisition)
        {
            claimedIssue = await dependencies.FetchIssueAsync(
                request.WorkingDirectory, request.SelectedTask.IssueNumber!.Value, cancellationToken);
        }

        var eligibility = WorkerEligibility.Disabled;
        if (!request.HasRepositoryGate && !isReviewAcquisition)
        {
            // Repository-gate routing bypasses worker/assignment filtering, and review work carries
            // its own eligibility (the routing decision already resolved the authenticated reviewer
            // and the pull-request state). Both hosts revalidate ordinary developer work here so a
            // stale plan is never acted on at acquisition time.
            eligibility = WorkerRoutingService.Evaluate(request.Configuration, claimedIssue!, request.Model);
            if (eligibility.IsEnabled && !eligibility.IsEligible)
            {
                return new WorkClaimAcquisitionOutcome { BlockReason = eligibility.Message };
            }

            var assignmentEligibility = AssignmentRoutingService.Evaluate(request.Configuration, request.AssignmentIdentity, claimedIssue!);
            if (assignmentEligibility.IsEnabled && !assignmentEligibility.IsEligible)
            {
                return new WorkClaimAcquisitionOutcome { BlockReason = assignmentEligibility.Message };
            }
        }

        var acquisition = await dependencies.TryAcquireClaimAsync(
            request.GitCommonDirectory,
            request.WorktreeId,
            new WorkClaim
            {
                OwnerSessionId = request.OwnerSessionId,
                IssueNumber = request.SelectedTask.IssueNumber,
                PullRequestNumber = request.SelectedTask.PullRequestNumber,
                WorkType = claimType,
                ReviewerLogin = request.SelectedTask.ReviewerLogin,
                ReviewCycleId = request.SelectedTask.ReviewCycleId,
                ReviewBaselineCaptured = claimType == WorkClaimType.Review,
                WorkerProfile = eligibility.WorkerProfile,
                Model = request.Model,
                ClaimedIssueUpdatedAt = claimedIssue?.UpdatedAt ?? default
            },
            cancellationToken);

        if (!acquisition.Acquired)
        {
            return new WorkClaimAcquisitionOutcome
            {
                Claim = acquisition.Claim,
                BlockReason = acquisition.BlockReason ?? "Could not acquire the repository work claim."
            };
        }

        return new WorkClaimAcquisitionOutcome { Acquired = true, Claim = acquisition.Claim };
    }

    public static WorkClaimType MapWorkType(WorkflowItemType taskType) =>
        taskType == WorkflowItemType.ChangeRequest
            ? WorkClaimType.ChangeRequest
            : taskType == WorkflowItemType.PullRequestReview
                ? WorkClaimType.Review
                : WorkClaimType.Implementation;
}
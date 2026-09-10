using CodexGithubRouter.GitHub;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Work;

public enum WorkClaimReconciliationRecommendation
{
    WouldRelease,
    WouldKeep,
    UnableToDetermine
}

public static class WorkClaimReconciliationService
{
    /// <summary>
    /// The pull-request selection the reconciliation default fetch uses. It must be a superset of
    /// <see cref="ReviewRoutingService.Selection"/> (state, labels, title, isDraft, author,
    /// reviewRequests, reviews) plus the fields issue-claim evaluation needs (createdAt,
    /// headRefName, closingIssuesReferences). A default that lacked the review fields would see an
    /// empty requested-reviewer list and could release a live review claim.
    /// </summary>
    public static PullRequestSelection DefaultPullRequestSelection { get; } = new()
    {
        Number = true,
        State = true,
        Labels = true,
        Title = true,
        CreatedAt = true,
        HeadRefName = true,
        ClosingIssuesReferences = true,
        IsDraft = true,
        Author = true,
        ReviewRequests = true,
        Reviews = true
    };
    public static bool ShouldRelease(WorkClaim claim, Issue issue, PullRequest? claimedPullRequest, RouterConfiguration configuration)
    {
        if (string.Equals(issue.State, "closed", StringComparison.OrdinalIgnoreCase)) return true;

        var issueState = WorkflowStateResolver.Resolve(issue.Labels.Select(label => label.Name), configuration.States);
        if (!issueState.IsAmbiguous && (issueState.MatchedLabels.ContainsKey(WorkflowState.Blocked) || issueState.MatchedLabels.ContainsKey(WorkflowState.NeedsInfo) || issueState.MatchedLabels.ContainsKey(WorkflowState.Abandoned))) return true;

        return claim.PullRequestNumber.HasValue && claimedPullRequest is not null && IsPassiveOrTerminal(claimedPullRequest, configuration);
    }

    public static bool IsPassiveOrTerminal(PullRequest pullRequest, RouterConfiguration configuration)
    {
        if (string.Equals(pullRequest.State, "merged", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(pullRequest.State, "closed", StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.Equals(pullRequest.State, "open", StringComparison.OrdinalIgnoreCase)) return false;

        var state = WorkflowStateResolver.Resolve(pullRequest.Labels.Select(label => label.Name), configuration.PullRequestStates);
        return !state.IsAmbiguous && (state.MatchedLabels.ContainsKey(PullRequestState.ReviewRequested) || state.MatchedLabels.ContainsKey(PullRequestState.AwaitingMerge) || state.MatchedLabels.ContainsKey(PullRequestState.Deferred));
    }

    public static PullRequest? SelectClaimedPullRequest(WorkClaim claim, IEnumerable<PullRequest> linkedPullRequests) =>
        claim.PullRequestNumber.HasValue
            ? linkedPullRequests.SingleOrDefault(pullRequest => pullRequest.Number == claim.PullRequestNumber.Value)
            : null;

    public static async Task<bool> ReconcileAsync(string workingDirectory, string gitCommonDirectory, string worktreeId, RouterConfiguration configuration, CancellationToken cancellationToken = default)
    {
        // Stale recovery is repository-wide: a claim owned by a worktree whose git-dir
        // no longer exists is released before the current worktree's claim is evaluated.
        await WorkClaimStore.PruneStaleWorktreesAsync(gitCommonDirectory, Directory.Exists, cancellationToken);

        var claim = await WorkClaimStore.ReadAsync(gitCommonDirectory, worktreeId, cancellationToken);
        if (claim is null) return false;

        var recommendation = await DetermineAsync(workingDirectory, claim, configuration, cancellationToken: cancellationToken);
        if (recommendation == WorkClaimReconciliationRecommendation.UnableToDetermine)
        {
            // Production fails closed when the claim's GitHub state cannot be verified,
            // so a transient failure must surface instead of silently keeping the claim.
            throw new InvalidOperationException("Could not determine whether the active work claim should be released because its GitHub state could not be verified.");
        }

        return recommendation == WorkClaimReconciliationRecommendation.WouldRelease
            && await WorkClaimStore.ReleaseIfMatchesAsync(gitCommonDirectory, worktreeId, claim, cancellationToken);
    }

    /// <summary>
    /// Repository-wide reconciliation: prunes claims of removed worktrees, then evaluates every
    /// live worktree claim and guarded-releases each one production would drop. This is the manual
    /// <c>cgr work reconcile</c> path, so a passive or terminal claim owned by any worktree is
    /// released from any working directory. The hook continues to reconcile only its own worktree
    /// through <see cref="ReconcileAsync"/>. GitHub lookups may be injected for deterministic tests.
    /// </summary>
    public static async Task<WorkClaimReconcileAllResult> ReconcileAllAsync(
        string workingDirectory,
        string gitCommonDirectory,
        RouterConfiguration configuration,
        Func<int, Task<Issue>>? getIssue = null,
        Func<int, Task<PullRequest>>? getPullRequest = null,
        CancellationToken cancellationToken = default)
    {
        var pruned = await WorkClaimStore.PruneStaleWorktreesAsync(gitCommonDirectory, Directory.Exists, cancellationToken);
        var released = 0;

        foreach (var claim in await WorkClaimStore.ReadAllAsync(gitCommonDirectory, cancellationToken))
        {
            var recommendation = await DetermineAsync(
                workingDirectory, claim, configuration,
                getIssue: getIssue, getPullRequest: getPullRequest,
                cancellationToken: cancellationToken);
            if (recommendation == WorkClaimReconciliationRecommendation.UnableToDetermine)
            {
                // Same fail-closed contract as the hook: an unverifiable claim is never
                // silently dropped, and the manual reconcile surfaces the problem.
                throw new InvalidOperationException("Could not determine whether an active work claim should be released because its GitHub state could not be verified.");
            }

            if (recommendation == WorkClaimReconciliationRecommendation.WouldRelease &&
                await WorkClaimStore.ReleaseIfMatchesAsync(gitCommonDirectory, claim.WorktreeId, claim, cancellationToken))
            {
                released++;
            }
        }

        return new WorkClaimReconcileAllResult { PrunedCount = pruned, ReleasedCount = released };
    }

    /// <summary>
    /// Maps repository-wide claims owned by <em>other worktrees</em> (the caller excludes the
    /// current worktree's own claim) to workflow tasks they occupy. The routing plan treats these
    /// as a hard ineligibility ("occupied by another worktree") so a worktree routes the next
    /// eligible item instead of selecting occupied work and failing acquisition.
    /// </summary>
    public static IReadOnlyList<OccupiedWorkClaim> ResolveOccupiedClaims(
        IReadOnlyList<WorkClaim>? otherWorktreeClaims,
        IEnumerable<WorkflowItem> tasks)
    {
        if (otherWorktreeClaims is null || otherWorktreeClaims.Count == 0)
        {
            return Array.Empty<OccupiedWorkClaim>();
        }

        var taskList = tasks.ToList();
        var occupied = new List<OccupiedWorkClaim>();
        foreach (var claim in otherWorktreeClaims)
        {
            var conflicts = taskList.Any(task => OccupiesTask(claim, task));
            if (conflicts)
            {
                occupied.Add(new OccupiedWorkClaim
                {
                    IssueNumber = claim.IssueNumber,
                    PullRequestNumber = claim.PullRequestNumber,
                    ReviewerLogin = claim.ReviewerLogin,
                    WorkType = claim.WorkType,
                    WorktreeId = claim.WorktreeId,
                    WorktreePath = claim.WorktreePath,
                    OwnerSessionId = claim.OwnerSessionId
                });
            }
        }

        return occupied;
    }

    /// <summary>
    /// Occupancy is work-type aware: a review claim occupies only the same
    /// (pull request, reviewer) review work, and never implementation or change-request work on
    /// the same pull request (those may legitimately overlap review work). Non-review claims do not
    /// occupy review tasks — a review requested on a claimed implementation PR is distinct work.
    /// </summary>
    private static bool OccupiesTask(WorkClaim claim, WorkflowItem task)
    {
        if (claim.WorkType == WorkClaimType.Review || task.Type == WorkflowItemType.PullRequestReview)
        {
            return claim.WorkType == WorkClaimType.Review &&
                task.Type == WorkflowItemType.PullRequestReview &&
                claim.PullRequestNumber.HasValue &&
                task.PullRequestNumber == claim.PullRequestNumber.Value &&
                string.Equals(task.ReviewerLogin, claim.ReviewerLogin, StringComparison.OrdinalIgnoreCase);
        }

        return task.IssueNumber == claim.IssueNumber ||
            (claim.PullRequestNumber.HasValue && task.PullRequestNumber == claim.PullRequestNumber.Value);
    }

    /// <summary>
    /// Evaluates whether production reconciliation would release the claim without mutating
    /// anything. This is the shared read-only decision used by the hook's mutation path
    /// (<see cref="ReconcileAsync"/>), the diagnostic routing plan and tests. The release
    /// contract is broader than passive/terminal claimed-work tasks: the claim is released
    /// when the claimed issue is closed, blocked, needs-info or abandoned, when the claimed
    /// issue or claimed pull request can no longer be found, when the claimed pull request
    /// is passive/terminal, and when a claim without a pull request number resolves to a
    /// single passive/terminal current pull request on a completed issue.
    /// </summary>
    public static async Task<WorkClaimReconciliationRecommendation> DetermineAsync(
        string workingDirectory,
        WorkClaim claim,
        RouterConfiguration configuration,
        Func<int, Task<Issue>>? getIssue = null,
        Func<int, Task<PullRequest>>? getPullRequest = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new ArgumentException("A working directory is required to evaluate claim reconciliation.", nameof(workingDirectory));
        }

        getIssue ??= number => GitHubCliService.GetIssueByNumberAsync(workingDirectory, number, cancellationToken);
        getPullRequest ??= number => GitHubCliService.GetPullRequestByNumberAsync(workingDirectory, number, DefaultPullRequestSelection, cancellationToken);

        if (claim.WorkType == WorkClaimType.Review)
        {
            return await DetermineReviewAsync(claim, configuration, getPullRequest);
        }

        if (claim.IssueNumber is not { } claimIssueNumber)
        {
            // A non-review claim without an issue identity was written by a malformed client.
            return WorkClaimReconciliationRecommendation.WouldRelease;
        }

        Issue issue;
        try
        {
            issue = await getIssue(claimIssueNumber);
        }
        catch (GitHubItemNotFoundException)
        {
            return WorkClaimReconciliationRecommendation.WouldRelease;
        }
        catch
        {
            return WorkClaimReconciliationRecommendation.UnableToDetermine;
        }

        PullRequest? claimedPullRequest = null;
        if (claim.PullRequestNumber.HasValue)
        {
            try
            {
                claimedPullRequest = await getPullRequest(claim.PullRequestNumber.Value);
            }
            catch (GitHubItemNotFoundException)
            {
                return WorkClaimReconciliationRecommendation.WouldRelease;
            }
            catch
            {
                return WorkClaimReconciliationRecommendation.UnableToDetermine;
            }
        }
        else if (IsCompleted(issue, configuration) && issue.ClosingPullRequestsReferences.Count > 0)
        {
            var linkedPullRequests = new List<PullRequest>();
            foreach (var reference in issue.ClosingPullRequestsReferences)
            {
                try
                {
                    linkedPullRequests.Add(await getPullRequest(reference.Number));
                }
                catch (GitHubItemNotFoundException)
                {
                    // A missing historical reference is not a release signal.
                }
                catch
                {
                    return WorkClaimReconciliationRecommendation.UnableToDetermine;
                }
            }

            var currentPullRequests = SelectCurrentClaimPullRequests(claim, issue, linkedPullRequests);
            if (currentPullRequests.Count == 1 && IsPassiveOrTerminal(currentPullRequests[0], configuration))
            {
                return WorkClaimReconciliationRecommendation.WouldRelease;
            }
        }

        return ShouldRelease(claim, issue, claimedPullRequest, configuration)
            ? WorkClaimReconciliationRecommendation.WouldRelease
            : WorkClaimReconciliationRecommendation.WouldKeep;
    }

    /// <summary>
    /// Releases review claims whose pull request no longer requests the reviewer. Mirrors
    /// <see cref="WorkflowService.EvaluateClaimedReviewWork"/>: terminal/draft PRs, reviewer
    /// removals and stale review cycles release the claim, while CGR pull-request states that
    /// contradict review work (ChangesRequested, AwaitingMerge, Deferred) also release it.
    /// Unverifiable GitHub state returns <see cref="WorkClaimReconciliationRecommendation.UnableToDetermine"/>
    /// so the shared fail-closed contract is preserved.
    /// </summary>
    public static async Task<WorkClaimReconciliationRecommendation> DetermineReviewAsync(
        WorkClaim claim,
        RouterConfiguration configuration,
        Func<int, Task<PullRequest>> getPullRequest)
    {
        if (!claim.PullRequestNumber.HasValue)
        {
            // A review claim without a pull request identity was written by a malformed client.
            return WorkClaimReconciliationRecommendation.WouldRelease;
        }

        PullRequest pullRequest;
        try
        {
            pullRequest = await getPullRequest(claim.PullRequestNumber.Value);
        }
        catch (GitHubItemNotFoundException)
        {
            return WorkClaimReconciliationRecommendation.WouldRelease;
        }
        catch
        {
            return WorkClaimReconciliationRecommendation.UnableToDetermine;
        }

        var evaluated = WorkflowService.EvaluateClaimedReviewWork(configuration, claim, pullRequest);
        return evaluated.IsSuccessful && evaluated.Tasks.Any(task => task.Type == WorkflowItemType.AwaitingReview)
            ? WorkClaimReconciliationRecommendation.WouldRelease
            : evaluated.IsSuccessful
                ? WorkClaimReconciliationRecommendation.WouldKeep
                : WorkClaimReconciliationRecommendation.UnableToDetermine;
    }

    public static List<PullRequest> SelectCurrentClaimPullRequests(WorkClaim claim, Issue issue, IEnumerable<PullRequest> pullRequests) =>
        pullRequests.Where(pullRequest => WorkflowService.IsCurrentClaimPullRequest(claim, issue, pullRequest)).ToList();

    private static bool IsCompleted(Issue issue, RouterConfiguration configuration)
    {
        var resolution = WorkflowStateResolver.Resolve(issue.Labels.Select(label => label.Name), configuration.States);
        return !resolution.IsAmbiguous && resolution.MatchedLabels.ContainsKey(WorkflowState.Completed);
    }

    public static bool ShouldReleaseForPullRequestTransition(WorkClaim? claim, int pullRequestNumber, PullRequestState targetState) =>
        claim?.PullRequestNumber == pullRequestNumber && IsPassiveTarget(targetState);

    public static bool ShouldReleaseForIssueTransition(WorkClaim? claim, int issueNumber, WorkflowState targetState) =>
        claim?.IssueNumber == issueNumber && targetState is WorkflowState.Blocked or WorkflowState.NeedsInfo or WorkflowState.Abandoned;

    public static bool IsPassiveTarget(PullRequestState targetState) => targetState is PullRequestState.ReviewRequested or PullRequestState.AwaitingMerge or PullRequestState.Deferred;
}

public sealed class WorkClaimReconcileAllResult
{
    public int PrunedCount { get; init; }
    public int ReleasedCount { get; init; }
}

/// <summary>
/// Work owned by another Git worktree's active claim. The routing plan treats these as a hard
/// ineligibility ("occupied by another worktree") so the current worktree routes the next eligible
/// item instead of selecting occupied work and failing acquisition under the store lock.
/// </summary>
public sealed class OccupiedWorkClaim
{
    public int? IssueNumber { get; init; }
    public int? PullRequestNumber { get; init; }
    public string? ReviewerLogin { get; init; }
    public WorkClaimType WorkType { get; init; }
    public string WorktreeId { get; init; } = string.Empty;
    public string? WorktreePath { get; init; }
    public string OwnerSessionId { get; init; } = string.Empty;
}

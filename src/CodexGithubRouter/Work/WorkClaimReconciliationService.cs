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

    public static async Task<bool> ReconcileAsync(string workingDirectory, string gitCommonDirectory, RouterConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var claim = await WorkClaimStore.ReadAsync(gitCommonDirectory, cancellationToken);
        if (claim is null) return false;

        var recommendation = await DetermineAsync(workingDirectory, claim, configuration, cancellationToken: cancellationToken);
        if (recommendation == WorkClaimReconciliationRecommendation.UnableToDetermine)
        {
            // Production fails closed when the claim's GitHub state cannot be verified,
            // so a transient failure must surface instead of silently keeping the claim.
            throw new InvalidOperationException("Could not determine whether the active work claim should be released because its GitHub state could not be verified.");
        }

        return recommendation == WorkClaimReconciliationRecommendation.WouldRelease
            && await WorkClaimStore.ReleaseIfMatchesAsync(gitCommonDirectory, claim, cancellationToken);
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
        getPullRequest ??= number => GitHubCliService.GetPullRequestByNumberAsync(workingDirectory, number, new PullRequestSelection { Number = true, State = true, Labels = true, CreatedAt = true, HeadRefName = true, ClosingIssuesReferences = true }, cancellationToken);

        Issue issue;
        try
        {
            issue = await getIssue(claim.IssueNumber);
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

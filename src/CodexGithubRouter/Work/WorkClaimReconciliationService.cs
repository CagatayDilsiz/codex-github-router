using CodexGithubRouter.GitHub;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Work;

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

        Issue issue;
        try
        {
            issue = await GitHubCliService.GetIssueByNumberAsync(workingDirectory, claim.IssueNumber, cancellationToken);
        }
        catch (GitHubItemNotFoundException)
        {
            return await WorkClaimStore.ReleaseIfMatchesAsync(gitCommonDirectory, claim, cancellationToken);
        }

        PullRequest? claimedPullRequest = null;
        if (claim.PullRequestNumber.HasValue)
        {
            try
            {
                claimedPullRequest = await GitHubCliService.GetPullRequestByNumberAsync(workingDirectory, claim.PullRequestNumber.Value, new PullRequestSelection { Number = true, State = true, Labels = true }, cancellationToken);
            }
            catch (GitHubItemNotFoundException)
            {
                return await WorkClaimStore.ReleaseIfMatchesAsync(gitCommonDirectory, claim, cancellationToken);
            }
        }
        else if (IsCompleted(issue, configuration) && issue.ClosingPullRequestsReferences.Count > 0)
        {
            var linkedPullRequests = new List<PullRequest>();
            foreach (var reference in issue.ClosingPullRequestsReferences)
            {
                try
                {
                    linkedPullRequests.Add(await GitHubCliService.GetPullRequestByNumberAsync(
                        workingDirectory,
                        reference.Number,
                        new PullRequestSelection { Number = true, State = true, Labels = true, CreatedAt = true, HeadRefName = true, ClosingIssuesReferences = true },
                        cancellationToken));
                }
                catch (GitHubItemNotFoundException)
                {
                    // A missing historical reference is not a release signal.
                }
            }

            var currentPullRequests = SelectCurrentClaimPullRequests(claim, issue, linkedPullRequests);
            if (currentPullRequests.Count == 1 && IsPassiveOrTerminal(currentPullRequests[0], configuration))
            {
                return await WorkClaimStore.ReleaseIfMatchesAsync(gitCommonDirectory, claim, cancellationToken);
            }
        }

        return ShouldRelease(claim, issue, claimedPullRequest, configuration) && await WorkClaimStore.ReleaseIfMatchesAsync(gitCommonDirectory, claim, cancellationToken);
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

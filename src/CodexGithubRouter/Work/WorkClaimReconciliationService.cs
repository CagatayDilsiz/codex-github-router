using CodexGithubRouter.GitHub;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Work;

public static class WorkClaimReconciliationService
{
    public static bool ShouldRelease(WorkClaim claim, Issue issue, IEnumerable<PullRequest> pullRequests, RouterConfiguration configuration)
    {
        if (string.Equals(issue.State, "closed", StringComparison.OrdinalIgnoreCase)) return true;

        var issueState = WorkflowStateResolver.Resolve(issue.Labels.Select(label => label.Name), configuration.States);
        if (!issueState.IsAmbiguous && (issueState.MatchedLabels.ContainsKey(WorkflowState.Blocked) || issueState.MatchedLabels.ContainsKey(WorkflowState.NeedsInfo) || issueState.MatchedLabels.ContainsKey(WorkflowState.Abandoned))) return true;

        return pullRequests.Any(pullRequest => IsPassiveOrTerminal(pullRequest, configuration));
    }

    public static bool IsPassiveOrTerminal(PullRequest pullRequest, RouterConfiguration configuration)
    {
        if (string.Equals(pullRequest.State, "merged", StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.Equals(pullRequest.State, "open", StringComparison.OrdinalIgnoreCase)) return false;

        var state = WorkflowStateResolver.Resolve(pullRequest.Labels.Select(label => label.Name), configuration.PullRequestStates);
        return !state.IsAmbiguous && (state.MatchedLabels.ContainsKey(PullRequestState.ReviewRequested) || state.MatchedLabels.ContainsKey(PullRequestState.AwaitingMerge));
    }

    public static async Task<bool> ReconcileAsync(string workingDirectory, string gitCommonDirectory, RouterConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var claim = await WorkClaimStore.ReadAsync(gitCommonDirectory, cancellationToken);
        if (claim is null) return false;

        Issue issue;
        try
        {
            issue = await GitHubCliService.GetIssueByNumberAsync(workingDirectory, claim.IssueNumber, cancellationToken);
        }
        catch (InvalidOperationException exception) when (IsMissingGitHubItem(exception))
        {
            return await WorkClaimStore.ReleaseForIssueAsync(gitCommonDirectory, claim.IssueNumber, cancellationToken);
        }
        var pullRequestNumbers = issue.ClosingPullRequestsReferences.Select(reference => reference.Number).ToHashSet();
        if (claim.PullRequestNumber.HasValue) pullRequestNumbers.Add(claim.PullRequestNumber.Value);

        var pullRequests = new List<PullRequest>();
        foreach (var pullRequestNumber in pullRequestNumbers)
        {
            try
            {
                pullRequests.Add(await GitHubCliService.GetPullRequestByNumberAsync(workingDirectory, pullRequestNumber, new PullRequestSelection { Number = true, State = true, Labels = true }, cancellationToken));
            }
            catch (InvalidOperationException exception) when (IsMissingGitHubItem(exception))
            {
                return await WorkClaimStore.ReleaseForIssueAsync(gitCommonDirectory, claim.IssueNumber, cancellationToken);
            }
        }

        return ShouldRelease(claim, issue, pullRequests, configuration) && await WorkClaimStore.ReleaseForIssueAsync(gitCommonDirectory, claim.IssueNumber, cancellationToken);
    }

    private static bool IsMissingGitHubItem(InvalidOperationException exception) =>
        exception.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("could not resolve", StringComparison.OrdinalIgnoreCase);
}

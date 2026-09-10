using CodexGithubRouter.Prompts;
using CodexGithubRouter.Workflow;
using CodexGithubRouter.Work;

namespace CodexGithubRouter.Hooks;

public sealed class HookTaskDecision
{
    public string? BlockReason { get; init; }
    public string? AdditionalContext { get; init; }
    public WorkflowItem? SelectedTask { get; init; }
}

public static class HookTaskRouter
{
    public static bool RequiresWorkClaim(WorkflowItem task) => task.Type is
        WorkflowItemType.ChangeRequest or
        WorkflowItemType.ResumeInProgressIssue or
        WorkflowItemType.NewIssue or
        WorkflowItemType.PullRequestReview;

    public static bool IsReviewTask(WorkflowItem task) => task.Type == WorkflowItemType.PullRequestReview;

    public static HookTaskDecision Route(IReadOnlyList<WorkflowItem> actionableTasks)
    {
        var blockingTypes = new HashSet<WorkflowItemType>
        {
            WorkflowItemType.ClosedWithoutMerge,
            WorkflowItemType.UnknownPullRequestState,
            WorkflowItemType.Unknown,
            WorkflowItemType.RepositoryGateBlock
        };

        var blocker = actionableTasks.FirstOrDefault(task => blockingTypes.Contains(task.Type));
        if (blocker is not null)
        {
            return new HookTaskDecision { BlockReason = blocker.Status.Message };
        }

        var changeRequest = actionableTasks
            .Where(task => task.Type == WorkflowItemType.ChangeRequest && task.PullRequestNumber.HasValue)
            .OrderBy(task => task.SelectionRank)
            .FirstOrDefault();
        if (changeRequest is not null)
        {
            return new HookTaskDecision { SelectedTask = changeRequest, AdditionalContext = ContextPromptService.GetChangeRequestPrompt(changeRequest.IssueNumber, changeRequest.PullRequestNumber!.Value) };
        }

        var currentPullRequestRecovery = actionableTasks
            .Where(task => task.Type == WorkflowItemType.RecoverCurrentPullRequest && task.PullRequestNumber.HasValue)
            .OrderBy(task => task.SelectionRank)
            .FirstOrDefault();
        if (currentPullRequestRecovery is not null)
        {
            return new HookTaskDecision
            {
                SelectedTask = currentPullRequestRecovery,
                AdditionalContext = ContextPromptService.GetCurrentPullRequestRecoveryPrompt(currentPullRequestRecovery.IssueNumber, currentPullRequestRecovery.PullRequestNumber!.Value)
            };
        }

        var completedRecovery = actionableTasks
            .Where(task => task.Type == WorkflowItemType.RecoverCompletedIssue)
            .OrderBy(task => task.SelectionRank)
            .FirstOrDefault();
        if (completedRecovery is not null)
        {
            return new HookTaskDecision
            {
                SelectedTask = completedRecovery,
                AdditionalContext = ContextPromptService.GetCompletedIssueRecoveryPrompt(completedRecovery.IssueNumber)
            };
        }

        var issuesNeedingPRLink = actionableTasks.Where(task => task.Type == WorkflowItemType.LinkPullRequestsToIssues).Select(task => task.IssueNumber).ToList();
        if (issuesNeedingPRLink.Count > 0)
        {
            return new HookTaskDecision { SelectedTask = actionableTasks.First(task => task.Type == WorkflowItemType.LinkPullRequestsToIssues), AdditionalContext = ContextPromptService.GetIssuesNeedPRLinkPrompt(issuesNeedingPRLink.ToArray()) };
        }

        var inProgressIssue = actionableTasks
            .Where(task => task.Type == WorkflowItemType.ResumeInProgressIssue)
            .OrderBy(task => task.SelectionRank)
            .FirstOrDefault();
        if (inProgressIssue is not null)
        {
            return new HookTaskDecision { SelectedTask = inProgressIssue, AdditionalContext = ContextPromptService.GetInProgressIssuePrompt(inProgressIssue.IssueNumber) };
        }

        var review = actionableTasks
            .Where(task => task.Type == WorkflowItemType.PullRequestReview)
            .OrderBy(task => task.SelectionRank)
            .FirstOrDefault();
        if (review is not null)
        {
            return new HookTaskDecision
            {
                SelectedTask = review,
                AdditionalContext = ContextPromptService.GetPullRequestReviewPrompt(review.PullRequestNumber, review.ReviewerLogin)
            };
        }

        var newIssue = actionableTasks
            .Where(task => task.Type == WorkflowItemType.NewIssue)
            .OrderBy(task => task.SelectionRank)
            .FirstOrDefault();
        if (newIssue is not null)
        {
            return new HookTaskDecision { SelectedTask = newIssue, AdditionalContext = ContextPromptService.GetNewIssuePrompt(newIssue.IssueNumber) };
        }

        return new HookTaskDecision { BlockReason = "No actionable workflow tasks found." };
    }

    public static HookTaskDecision RouteClaimedWork(WorkClaim claim, string? sessionId, IReadOnlyList<WorkflowItem> actionableTasks)
    {
        if (!string.Equals(claim.OwnerSessionId, sessionId, StringComparison.Ordinal))
        {
            return new HookTaskDecision { BlockReason = $"Active work claim for {FormatWorkIdentity(claim)} is owned by another Codex session." };
        }

        var claimedTasks = actionableTasks.Where(task => MatchesClaim(claim, task)).ToList();
        if (claimedTasks.Count == 0)
        {
            return new HookTaskDecision { BlockReason = $"Active work claim for {FormatWorkIdentity(claim)} was not found in the current workflow discovery. No unrelated work will be routed." };
        }

        var discoveredPullRequests = claimedTasks.Where(task => task.PullRequestNumber.HasValue).Select(task => task.PullRequestNumber!.Value).Distinct().ToList();
        if (!claim.PullRequestNumber.HasValue && discoveredPullRequests.Count > 1)
        {
            return new HookTaskDecision { BlockReason = $"Active work claim for issue #{claim.IssueNumber} has multiple candidate pull requests ({string.Join(", ", discoveredPullRequests.Select(number => $"#{number}"))}). No work identity will be selected implicitly." };
        }

        var decision = Route(claimedTasks);
        return string.IsNullOrWhiteSpace(decision.BlockReason)
            ? decision
            : new HookTaskDecision { BlockReason = $"Active work claim for {FormatWorkIdentity(claim)} has no actionable matching task. No unrelated work will be routed." };
    }

    private static bool MatchesClaim(WorkClaim claim, WorkflowItem task)
    {
        if (claim.WorkType == WorkClaimType.Review || task.Type == WorkflowItemType.PullRequestReview)
        {
            return claim.WorkType == WorkClaimType.Review &&
                IsReviewTask(task) &&
                claim.PullRequestNumber.HasValue &&
                task.PullRequestNumber == claim.PullRequestNumber.Value &&
                string.Equals(task.ReviewerLogin, claim.ReviewerLogin, StringComparison.OrdinalIgnoreCase);
        }

        return task.IssueNumber == claim.IssueNumber &&
            (!claim.PullRequestNumber.HasValue || task.PullRequestNumber == claim.PullRequestNumber);
    }

    private static string FormatWorkIdentity(WorkClaim claim) =>
        claim.WorkType == WorkClaimType.Review
            ? $"review of pull request #{claim.PullRequestNumber} (reviewer '{claim.ReviewerLogin}')"
            : $"issue #{claim.IssueNumber}{FormatPullRequest(claim.PullRequestNumber)}";

    private static string FormatPullRequest(int? pullRequestNumber) => pullRequestNumber.HasValue ? $" / pull request #{pullRequestNumber.Value}" : string.Empty;
}

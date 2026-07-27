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
    public static bool RequiresWorkClaim(WorkflowItem task) => task.Type is WorkflowItemType.ChangeRequest or WorkflowItemType.ResumeInProgressIssue or WorkflowItemType.NewIssue;

    public static HookTaskDecision Route(IReadOnlyList<WorkflowItem> actionableTasks)
    {
        var blockingTypes = new HashSet<WorkflowItemType>
        {
            WorkflowItemType.ClosedWithoutMerge,
            WorkflowItemType.UnknownPullRequestState,
            WorkflowItemType.Unknown
        };

        var blocker = actionableTasks.FirstOrDefault(task => blockingTypes.Contains(task.Type));
        if (blocker is not null)
        {
            return new HookTaskDecision { BlockReason = blocker.Status.Message };
        }

        var changeRequest = actionableTasks.FirstOrDefault(task => task.Type == WorkflowItemType.ChangeRequest && task.PullRequestNumber.HasValue);
        if (changeRequest is not null)
        {
            return new HookTaskDecision { SelectedTask = changeRequest, AdditionalContext = ContextPromptService.GetChangeRequestPrompt(changeRequest.IssueNumber, changeRequest.PullRequestNumber!.Value) };
        }

        var currentPullRequestRecovery = actionableTasks.FirstOrDefault(task => task.Type == WorkflowItemType.RecoverCurrentPullRequest && task.PullRequestNumber.HasValue);
        if (currentPullRequestRecovery is not null)
        {
            return new HookTaskDecision
            {
                SelectedTask = currentPullRequestRecovery,
                AdditionalContext = ContextPromptService.GetCurrentPullRequestRecoveryPrompt(currentPullRequestRecovery.IssueNumber, currentPullRequestRecovery.PullRequestNumber!.Value)
            };
        }

        var completedRecovery = actionableTasks.FirstOrDefault(task => task.Type == WorkflowItemType.RecoverCompletedIssue);
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

        var inProgressIssue = actionableTasks.FirstOrDefault(task => task.Type == WorkflowItemType.ResumeInProgressIssue);
        if (inProgressIssue is not null)
        {
            return new HookTaskDecision { SelectedTask = inProgressIssue, AdditionalContext = ContextPromptService.GetInProgressIssuePrompt(inProgressIssue.IssueNumber) };
        }

        var newIssue = actionableTasks.FirstOrDefault(task => task.Type == WorkflowItemType.NewIssue);
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
            return new HookTaskDecision { BlockReason = $"Active work claim for issue #{claim.IssueNumber}{FormatPullRequest(claim.PullRequestNumber)} is owned by another Codex session." };
        }

        var claimedTasks = actionableTasks.Where(task =>
            task.IssueNumber == claim.IssueNumber &&
            (!claim.PullRequestNumber.HasValue || task.PullRequestNumber == claim.PullRequestNumber)).ToList();
        if (claimedTasks.Count == 0)
        {
            return new HookTaskDecision { BlockReason = $"Active work claim for issue #{claim.IssueNumber}{FormatPullRequest(claim.PullRequestNumber)} was not found in the current workflow discovery. No unrelated work will be routed." };
        }

        var discoveredPullRequests = claimedTasks.Where(task => task.PullRequestNumber.HasValue).Select(task => task.PullRequestNumber!.Value).Distinct().ToList();
        if (!claim.PullRequestNumber.HasValue && discoveredPullRequests.Count > 1)
        {
            return new HookTaskDecision { BlockReason = $"Active work claim for issue #{claim.IssueNumber} has multiple candidate pull requests ({string.Join(", ", discoveredPullRequests.Select(number => $"#{number}"))}). No work identity will be selected implicitly." };
        }

        var decision = Route(claimedTasks);
        return string.IsNullOrWhiteSpace(decision.BlockReason)
            ? decision
            : new HookTaskDecision { BlockReason = $"Active work claim for issue #{claim.IssueNumber}{FormatPullRequest(claim.PullRequestNumber)} has no actionable matching task. No unrelated work will be routed." };
    }

    private static string FormatPullRequest(int? pullRequestNumber) => pullRequestNumber.HasValue ? $" / pull request #{pullRequestNumber.Value}" : string.Empty;
}

using CodexGithubRouter.Prompts;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Hooks;

public sealed class HookTaskDecision
{
    public string? BlockReason { get; init; }
    public string? AdditionalContext { get; init; }
    public WorkflowItem? SelectedTask { get; init; }
}

public static class HookTaskRouter
{
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

        var issuesNeedingPRLink = actionableTasks.Where(task => task.Type == WorkflowItemType.LinkPullRequestsToIssues).Select(task => task.IssueNumber).ToList();
        if (issuesNeedingPRLink.Count > 0)
        {
            return new HookTaskDecision { AdditionalContext = ContextPromptService.GetIssuesNeedPRLinkPrompt(issuesNeedingPRLink.ToArray()) };
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
}

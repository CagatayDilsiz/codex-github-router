using CodexGithubRouter.Hooks;
using CodexGithubRouter.Work;
using CodexGithubRouter.Workflow;
using Xunit;

namespace CodexGithubRouter.Tests;

[Trait("Category", "Unit")]
public sealed class WorkflowRoutingTests
{
    [Fact]
    public void Claim_owner_continues_claimed_work_before_unrelated_change_request()
    {
        var claim = new WorkClaim { OwnerSessionId = "owner", IssueNumber = 2, WorkType = WorkClaimType.Implementation };
        var decision = HookTaskRouter.RouteClaimedWork(claim, "owner", new[] { new WorkflowItem { Type = WorkflowItemType.ChangeRequest, IssueNumber = 1, PullRequestNumber = 10 }, new WorkflowItem { Type = WorkflowItemType.ResumeInProgressIssue, IssueNumber = 2 } });
        Assert.Equal(2, decision.SelectedTask?.IssueNumber);
    }

    [Fact]
    public void Different_session_is_blocked_from_claimed_work()
    {
        var claim = new WorkClaim { OwnerSessionId = "owner", IssueNumber = 2, WorkType = WorkClaimType.Implementation };
        var decision = HookTaskRouter.RouteClaimedWork(claim, "other", new[] { new WorkflowItem { Type = WorkflowItemType.ResumeInProgressIssue, IssueNumber = 2 } });
        Assert.Contains("another Codex session", decision.BlockReason);
        Assert.Null(decision.AdditionalContext);
    }

    [Fact]
    public void Ambiguous_pull_request_candidates_block_pr_less_claim()
    {
        var claim = new WorkClaim { OwnerSessionId = "owner", IssueNumber = 2, WorkType = WorkClaimType.Implementation };
        var decision = HookTaskRouter.RouteClaimedWork(claim, "owner", new[] { new WorkflowItem { Type = WorkflowItemType.ChangeRequest, IssueNumber = 2, PullRequestNumber = 21 }, new WorkflowItem { Type = WorkflowItemType.ChangeRequest, IssueNumber = 2, PullRequestNumber = 22 } });
        Assert.Contains("multiple candidate pull requests", decision.BlockReason);
    }

    [Fact]
    public void Hook_blockers_precede_all_other_context()
    {
        var decision = HookTaskRouter.Route(new[] { new WorkflowItem { Type = WorkflowItemType.ClosedWithoutMerge, IssueNumber = 1, Status = new WorkflowTaskStatus { Message = "Closed without merge." } }, new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 2 } });
        Assert.Equal("Closed without merge.", decision.BlockReason);
    }

    [Fact]
    public void Claim_owner_without_matching_task_cannot_fall_through_to_unrelated_work()
    {
        var claim = new WorkClaim { OwnerSessionId = "owner", IssueNumber = 2, WorkType = WorkClaimType.Implementation };
        var decision = HookTaskRouter.RouteClaimedWork(claim, "owner", new[] { new WorkflowItem { Type = WorkflowItemType.ChangeRequest, IssueNumber = 1, PullRequestNumber = 10 } });
        Assert.Contains("No unrelated work will be routed", decision.BlockReason);
        Assert.Null(decision.AdditionalContext);
    }

    [Fact]
    public void Change_request_precedes_link_resume_and_new_context()
    {
        var decision = HookTaskRouter.Route(new[] { new WorkflowItem { Type = WorkflowItemType.ChangeRequest, IssueNumber = 2, PullRequestNumber = 20 }, new WorkflowItem { Type = WorkflowItemType.LinkPullRequestsToIssues, IssueNumber = 3 }, new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 5 } });
        Assert.Contains("pull request #20", decision.AdditionalContext);
    }

    [Fact]
    public void Current_pull_request_recovery_has_exact_context()
    {
        var decision = HookTaskRouter.Route(new[] { new WorkflowItem { Type = WorkflowItemType.RecoverCurrentPullRequest, IssueNumber = 6, PullRequestNumber = 21 }, new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 5 } });
        Assert.Contains("pull request #21", decision.AdditionalContext);
    }

    [Fact]
    public void Completed_recovery_has_runnable_context()
    {
        var decision = HookTaskRouter.Route(new[] { new WorkflowItem { Type = WorkflowItemType.RecoverCompletedIssue, IssueNumber = 6 }, new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 5 } });
        Assert.Contains("Recover the completed implementation for issue #6", decision.AdditionalContext);
    }

    [Fact]
    public void Pull_request_linking_precedes_resume_and_new_work()
    {
        var decision = HookTaskRouter.Route(new[] { new WorkflowItem { Type = WorkflowItemType.LinkPullRequestsToIssues, IssueNumber = 3 }, new WorkflowItem { Type = WorkflowItemType.ResumeInProgressIssue, IssueNumber = 4 }, new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 5 } });
        Assert.Contains("following issues: 3", decision.AdditionalContext);
    }

    [Fact]
    public void Resume_precedes_new_issue_context()
    {
        var decision = HookTaskRouter.Route(new[] { new WorkflowItem { Type = WorkflowItemType.ResumeInProgressIssue, IssueNumber = 4 }, new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 5 } });
        Assert.Contains("Issue #4 is already marked as working", decision.AdditionalContext);
    }

    [Fact]
    public void Empty_route_uses_safe_fallback()
    {
        Assert.Equal("No actionable workflow tasks found.", HookTaskRouter.Route(Array.Empty<WorkflowItem>()).BlockReason);
    }

    [Fact]
    public async Task Closed_unmerged_active_claim_releases_and_blocker_remains_visible()
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        var claim = new WorkClaim { ClaimId = Guid.NewGuid(), Version = 1, OwnerSessionId = "owner", IssueNumber = 4, PullRequestNumber = 21, WorkType = WorkClaimType.ChangeRequest, ClaimedIssueUpdatedAt = now, ClaimedAt = now, LastUpdatedAt = now };
        var service = new ActiveClaimRouteService(
            _ => Task.FromResult(new WorkflowResponse { Tasks = new List<WorkflowItem> { new() { Type = WorkflowItemType.ClosedWithoutMerge, IssueNumber = 4, PullRequestNumber = 21, Status = new WorkflowTaskStatus { Message = "Closed without merge." } } } }),
            () => Task.FromResult<WorkClaim?>(claim),
            _ => throw new InvalidOperationException("A closed claim must not be acquired again."),
            () => Task.FromResult(true));

        Assert.Null(await service.RouteAsync(claim, "owner"));
        Assert.Equal("Closed without merge.", HookTaskRouter.Route(new[] { new WorkflowItem { Type = WorkflowItemType.ClosedWithoutMerge, IssueNumber = 4, Status = new WorkflowTaskStatus { Message = "Closed without merge." } }, new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 5 } }).BlockReason);
    }
}

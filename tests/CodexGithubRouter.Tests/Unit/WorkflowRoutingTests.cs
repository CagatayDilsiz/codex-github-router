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
}

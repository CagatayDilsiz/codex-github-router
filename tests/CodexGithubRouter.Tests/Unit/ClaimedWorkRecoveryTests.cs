using CodexGithubRouter.GitHub;
using CodexGithubRouter.Work;
using CodexGithubRouter.Workflow;
using Xunit;

namespace CodexGithubRouter.Tests;

[Trait("Category", "Unit")]
public sealed class ClaimedWorkRecoveryTests
{
    [Fact]
    public async Task Ready_claim_recovers_as_new_issue()
    {
        var claim = Claim();
        var issue = new Issue { Number = 4, Labels = new List<GithubLabel> { new() { Name = "codex:ready" } } };
        var result = await WorkflowService.EvaluateClaimedWorkAsync(new RouterConfiguration(), claim, issue, _ => throw new InvalidOperationException());
        Assert.Equal(WorkflowItemType.NewIssue, result.Tasks.Single().Type);
    }

    [Fact]
    public async Task Working_claim_recovers_as_resume_without_current_pull_request()
    {
        var claim = Claim();
        var issue = new Issue { Number = 4, Labels = new List<GithubLabel> { new() { Name = "codex:working" } } };
        var result = await WorkflowService.EvaluateClaimedWorkAsync(new RouterConfiguration(), claim, issue, _ => throw new InvalidOperationException());
        Assert.Equal(WorkflowItemType.ResumeInProgressIssue, result.Tasks.Single().Type);
    }

    private static WorkClaim Claim()
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        return new WorkClaim { ClaimId = Guid.NewGuid(), Version = 1, OwnerSessionId = "owner", IssueNumber = 4, WorkType = WorkClaimType.Implementation, ClaimedIssueUpdatedAt = now, ClaimedAt = now, LastUpdatedAt = now };
    }
}

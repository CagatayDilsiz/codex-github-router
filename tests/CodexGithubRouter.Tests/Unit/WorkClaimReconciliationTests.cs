using CodexGithubRouter.GitHub;
using CodexGithubRouter.Work;
using CodexGithubRouter.Workflow;
using Xunit;

namespace CodexGithubRouter.Tests;

[Trait("Category", "Unit")]
public sealed class WorkClaimReconciliationTests
{
    [Fact]
    public void Older_passive_pull_request_does_not_release_claimed_change_request()
    {
        var configuration = new RouterConfiguration();
        var claim = new WorkClaim { OwnerSessionId = "a", IssueNumber = 4, PullRequestNumber = 22, WorkType = WorkClaimType.ChangeRequest };
        var issue = new Issue { Number = 4, Labels = new List<GithubLabel> { new() { Name = "codex:working" } } };
        var old = new PullRequest { Number = 8, State = "open", Labels = new List<GithubLabel> { new() { Name = "codex:rr" } } };
        var current = new PullRequest { Number = 22, State = "open", Labels = new List<GithubLabel> { new() { Name = "codex:cr" } } };
        Assert.Equal(22, WorkClaimReconciliationService.SelectClaimedPullRequest(claim, new[] { old, current })?.Number);
        Assert.False(WorkClaimReconciliationService.ShouldRelease(claim, issue, current, configuration));
    }

    [Fact]
    public void Matching_review_request_releases_claim()
    {
        var claim = new WorkClaim { OwnerSessionId = "a", IssueNumber = 4, PullRequestNumber = 22, WorkType = WorkClaimType.ChangeRequest };
        var issue = new Issue { Number = 4, Labels = new List<GithubLabel> { new() { Name = "codex:working" } } };
        var review = new PullRequest { Number = 22, State = "open", Labels = new List<GithubLabel> { new() { Name = "codex:rr" } } };
        Assert.True(WorkClaimReconciliationService.ShouldRelease(claim, issue, review, new RouterConfiguration()));
    }
}

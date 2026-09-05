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

    [Fact]
    public void Pull_request_less_implementation_claim_ignores_historical_passive_pull_request()
    {
        var claim = new WorkClaim { OwnerSessionId = "a", IssueNumber = 4, WorkType = WorkClaimType.Implementation };
        var issue = new Issue { Number = 4, Labels = new List<GithubLabel> { new() { Name = "codex:working" } } };
        var historical = new PullRequest { Number = 8, State = "open", Labels = new List<GithubLabel> { new() { Name = "codex:rr" } } };
        Assert.False(WorkClaimReconciliationService.ShouldRelease(claim, issue, historical, new RouterConfiguration()));
    }

    [Fact]
    public void Historical_and_current_passive_pull_requests_select_only_current_candidate()
    {
        var baseline = DateTimeOffset.UtcNow.AddMinutes(-10);
        var claim = new WorkClaim { OwnerSessionId = "a", IssueNumber = 4, WorkType = WorkClaimType.Implementation, ClaimedIssueUpdatedAt = baseline };
        var issue = CompletedIssue();
        var historical = Pull(claim, 21, "codex/issue-4-old", "codex:rr", baseline.AddMinutes(-1));
        var current = Pull(claim, 22, "codex/issue-4-current", "codex:rr", baseline.AddMinutes(1));
        var selected = WorkClaimReconciliationService.SelectCurrentClaimPullRequests(claim, issue, new[] { historical, current });
        Assert.Single(selected);
        Assert.Equal(22, selected[0].Number);
    }

    [Fact]
    public void Multiple_historical_pull_requests_do_not_hide_current_candidate()
    {
        var baseline = DateTimeOffset.UtcNow.AddMinutes(-10);
        var claim = new WorkClaim { OwnerSessionId = "a", IssueNumber = 4, WorkType = WorkClaimType.Implementation, ClaimedIssueUpdatedAt = baseline };
        var issue = CompletedIssue();
        var candidates = WorkClaimReconciliationService.SelectCurrentClaimPullRequests(claim, issue, new[]
        {
            Pull(claim, 21, "codex/issue-4-old", "codex:rr", baseline.AddMinutes(-2)),
            Pull(claim, 22, "codex/issue-4-old-2", "codex:deferred", baseline.AddMinutes(-1)),
            Pull(claim, 23, "codex/issue-4-current", "codex:rr", baseline.AddMinutes(1))
        });
        Assert.Equal(23, Assert.Single(candidates).Number);
    }

    [Fact]
    public void Two_current_pull_requests_remain_ambiguous()
    {
        var baseline = DateTimeOffset.UtcNow.AddMinutes(-10);
        var claim = new WorkClaim { OwnerSessionId = "a", IssueNumber = 4, WorkType = WorkClaimType.Implementation, ClaimedIssueUpdatedAt = baseline };
        var candidates = WorkClaimReconciliationService.SelectCurrentClaimPullRequests(claim, CompletedIssue(), new[]
        {
            Pull(claim, 21, "codex/issue-4-current-a", "codex:rr", baseline.AddMinutes(1)),
            Pull(claim, 22, "codex/issue-4-current-b", "codex:cr", baseline.AddMinutes(2))
        });
        Assert.Equal(2, candidates.Count);
    }

    [Fact]
    public void Historical_passive_plus_current_change_request_retains_claim()
    {
        var baseline = DateTimeOffset.UtcNow.AddMinutes(-10);
        var claim = new WorkClaim { OwnerSessionId = "a", IssueNumber = 4, WorkType = WorkClaimType.Implementation, ClaimedIssueUpdatedAt = baseline };
        var candidates = WorkClaimReconciliationService.SelectCurrentClaimPullRequests(claim, CompletedIssue(), new[]
        {
            Pull(claim, 21, "codex/issue-4-old", "codex:rr", baseline.AddMinutes(-1)),
            Pull(claim, 22, "codex/issue-4-current", "codex:cr", baseline.AddMinutes(1))
        });
        Assert.Single(candidates);
        Assert.False(WorkClaimReconciliationService.IsPassiveOrTerminal(candidates[0], new RouterConfiguration()));
    }

    [Fact]
    public void Blocked_issue_releases_claim()
    {
        var issue = new Issue { Number = 4, Labels = new List<GithubLabel> { new() { Name = "codex:blocked" } } };
        var claim = new WorkClaim { OwnerSessionId = "a", IssueNumber = 4, PullRequestNumber = 22, WorkType = WorkClaimType.ChangeRequest };
        Assert.True(WorkClaimReconciliationService.ShouldRelease(claim, issue, null, new RouterConfiguration()));
    }

    [Fact]
    public void Pull_request_transition_matches_claim_identity_only()
    {
        var claim = new WorkClaim { OwnerSessionId = "a", IssueNumber = 4, PullRequestNumber = 22, WorkType = WorkClaimType.ChangeRequest };
        Assert.False(WorkClaimReconciliationService.ShouldReleaseForPullRequestTransition(claim, 21, PullRequestState.ReviewRequested));
        Assert.True(WorkClaimReconciliationService.ShouldReleaseForPullRequestTransition(claim, 22, PullRequestState.ReviewRequested));
    }

    [Fact]
    public async Task DetermineAsync_releases_blocked_claimed_issue()
    {
        var claim = new WorkClaim { OwnerSessionId = "a", IssueNumber = 4, WorkType = WorkClaimType.Implementation };
        var blocked = new Issue { Number = 4, Labels = new List<GithubLabel> { new() { Name = "codex:blocked" } } };

        var recommendation = await WorkClaimReconciliationService.DetermineAsync(
            "wd", claim, new RouterConfiguration(),
            getIssue: _ => Task.FromResult(blocked),
            getPullRequest: number => Task.FromResult(Pull(claim, number, "codex/issue-4", "codex:rr", DateTimeOffset.UtcNow)));

        Assert.Equal(WorkClaimReconciliationRecommendation.WouldRelease, recommendation);
    }

    [Fact]
    public async Task DetermineAsync_releases_closed_claimed_issue()
    {
        var claim = new WorkClaim { OwnerSessionId = "a", IssueNumber = 4, WorkType = WorkClaimType.Implementation };
        var closed = new Issue { Number = 4, State = "closed", Labels = new List<GithubLabel> { new() { Name = "codex:working" } } };

        var recommendation = await WorkClaimReconciliationService.DetermineAsync(
            "wd", claim, new RouterConfiguration(),
            getIssue: _ => Task.FromResult(closed),
            getPullRequest: number => Task.FromResult(Pull(claim, number, "codex/issue-4", "codex:rr", DateTimeOffset.UtcNow)));

        Assert.Equal(WorkClaimReconciliationRecommendation.WouldRelease, recommendation);
    }

    [Fact]
    public async Task DetermineAsync_releases_missing_claimed_issue()
    {
        var claim = new WorkClaim { OwnerSessionId = "a", IssueNumber = 4, WorkType = WorkClaimType.Implementation };
        var recommendation = await WorkClaimReconciliationService.DetermineAsync(
            "wd", claim, new RouterConfiguration(),
            getIssue: _ => throw new GitHubItemNotFoundException("issue not found"),
            getPullRequest: number => throw new GitHubItemNotFoundException("pull request not found"));

        Assert.Equal(WorkClaimReconciliationRecommendation.WouldRelease, recommendation);
    }

    [Fact]
    public async Task DetermineAsync_releases_missing_claimed_pull_request()
    {
        var claim = new WorkClaim { OwnerSessionId = "a", IssueNumber = 4, PullRequestNumber = 22, WorkType = WorkClaimType.Implementation };
        var inProgress = new Issue { Number = 4, Labels = new List<GithubLabel> { new() { Name = "codex:working" } } };

        var recommendation = await WorkClaimReconciliationService.DetermineAsync(
            "wd", claim, new RouterConfiguration(),
            getIssue: _ => Task.FromResult(inProgress),
            getPullRequest: number => throw new GitHubItemNotFoundException("pull request not found"));

        Assert.Equal(WorkClaimReconciliationRecommendation.WouldRelease, recommendation);
    }

    [Fact]
    public async Task DetermineAsync_releases_passive_claimed_pull_request()
    {
        var claim = new WorkClaim { OwnerSessionId = "a", IssueNumber = 4, PullRequestNumber = 22, WorkType = WorkClaimType.ChangeRequest };
        var inProgress = new Issue { Number = 4, Labels = new List<GithubLabel> { new() { Name = "codex:working" } } };
        var review = new PullRequest { Number = 22, State = "open", Labels = new List<GithubLabel> { new() { Name = "codex:rr" } }, CreatedAt = DateTimeOffset.UtcNow, ClosingIssuesReferences = new List<ClosingIssueReference> { new() { Number = 4 } } };

        var recommendation = await WorkClaimReconciliationService.DetermineAsync(
            "wd", claim, new RouterConfiguration(),
            getIssue: _ => Task.FromResult(inProgress),
            getPullRequest: number => Task.FromResult(review));

        Assert.Equal(WorkClaimReconciliationRecommendation.WouldRelease, recommendation);
    }

    [Fact]
    public async Task DetermineAsync_keeps_actionable_change_request_claim()
    {
        var claim = new WorkClaim { OwnerSessionId = "a", IssueNumber = 4, PullRequestNumber = 22, WorkType = WorkClaimType.ChangeRequest };
        var inProgress = new Issue { Number = 4, Labels = new List<GithubLabel> { new() { Name = "codex:working" } } };
        var changes = new PullRequest { Number = 22, State = "open", Labels = new List<GithubLabel> { new() { Name = "codex:cr" } }, CreatedAt = DateTimeOffset.UtcNow, ClosingIssuesReferences = new List<ClosingIssueReference> { new() { Number = 4 } } };

        var recommendation = await WorkClaimReconciliationService.DetermineAsync(
            "wd", claim, new RouterConfiguration(),
            getIssue: _ => Task.FromResult(inProgress),
            getPullRequest: number => Task.FromResult(changes));

        Assert.Equal(WorkClaimReconciliationRecommendation.WouldKeep, recommendation);
    }

    [Fact]
    public async Task DetermineAsync_releases_completed_issue_with_single_passive_current_pull_request()
    {
        var baseline = DateTimeOffset.UtcNow.AddMinutes(-10);
        var claim = new WorkClaim { OwnerSessionId = "a", IssueNumber = 4, WorkType = WorkClaimType.Implementation, ClaimedIssueUpdatedAt = baseline };
        var issue = CompletedIssue();

        var recommendation = await WorkClaimReconciliationService.DetermineAsync(
            "wd", claim, new RouterConfiguration(),
            getIssue: _ => Task.FromResult(issue),
            getPullRequest: number => number switch
            {
                21 => throw new GitHubItemNotFoundException("historical reference not found"),
                22 => Task.FromResult(Pull(claim, 22, "codex/issue-4-current", "codex:rr", baseline.AddMinutes(1))),
                _ => Task.FromResult(Pull(claim, 23, "codex/issue-4-old", "codex:rr", baseline.AddMinutes(-1)))
            });

        Assert.Equal(WorkClaimReconciliationRecommendation.WouldRelease, recommendation);
    }

    [Fact]
    public async Task DetermineAsync_keeps_completed_issue_with_current_change_request()
    {
        var baseline = DateTimeOffset.UtcNow.AddMinutes(-10);
        var claim = new WorkClaim { OwnerSessionId = "a", IssueNumber = 4, WorkType = WorkClaimType.Implementation, ClaimedIssueUpdatedAt = baseline };
        var issue = CompletedIssue();

        var recommendation = await WorkClaimReconciliationService.DetermineAsync(
            "wd", claim, new RouterConfiguration(),
            getIssue: _ => Task.FromResult(issue),
            getPullRequest: number => number switch
            {
                21 => throw new GitHubItemNotFoundException("historical reference not found"),
                _ => Task.FromResult(Pull(claim, number, "codex/issue-4-current", "codex:cr", baseline.AddMinutes(1)))
            });

        Assert.Equal(WorkClaimReconciliationRecommendation.WouldKeep, recommendation);
    }

    [Fact]
    public async Task DetermineAsync_fails_closed_when_github_state_cannot_be_verified()
    {
        var claim = new WorkClaim { OwnerSessionId = "a", IssueNumber = 4, WorkType = WorkClaimType.Implementation };
        var issue = new Issue { Number = 4, Labels = new List<GithubLabel> { new() { Name = "codex:working" } } };
        Assert.Equal(
            WorkClaimReconciliationRecommendation.UnableToDetermine,
            await WorkClaimReconciliationService.DetermineAsync(
                "wd", claim, new RouterConfiguration(),
                getIssue: _ => throw new InvalidOperationException("transient failure"),
                getPullRequest: number => throw new InvalidOperationException("transient failure")));
        Assert.Equal(
            WorkClaimReconciliationRecommendation.UnableToDetermine,
            await WorkClaimReconciliationService.DetermineAsync(
                "wd", new WorkClaim { OwnerSessionId = "a", IssueNumber = 4, PullRequestNumber = 22, WorkType = WorkClaimType.Implementation }, new RouterConfiguration(),
                getIssue: _ => Task.FromResult(issue),
                getPullRequest: number => throw new InvalidOperationException("transient failure")));
    }

    private static Issue CompletedIssue() => new()
    {
        Number = 4,
        Labels = new List<GithubLabel> { new() { Name = "codex:done" } },
        ClosingPullRequestsReferences = new List<ClosingIssueReference> { new() { Number = 21 }, new() { Number = 22 }, new() { Number = 23 } }
    };

    private static PullRequest Pull(WorkClaim claim, int number, string branch, string label, DateTimeOffset createdAt) => new()
    {
        Number = number,
        State = "open",
        CreatedAt = createdAt,
        HeadRefName = branch,
        Labels = new List<GithubLabel> { new() { Name = label } },
        ClosingIssuesReferences = new List<ClosingIssueReference> { new() { Number = claim.IssueNumber } }
    };
}

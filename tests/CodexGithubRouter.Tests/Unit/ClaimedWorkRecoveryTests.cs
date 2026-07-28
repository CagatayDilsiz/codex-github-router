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

    [Fact]
    public async Task Historical_passive_pull_request_does_not_hide_working_recovery()
    {
        var claim = Claim();
        var issue = WorkingIssueWithPullRequestReference();
        var result = await WorkflowService.EvaluateClaimedWorkAsync(new RouterConfiguration(), claim, issue, _ => Task.FromResult(Pull(claim, "codex/issue-4-old", "codex:rr", claim.ClaimedIssueUpdatedAt.AddMinutes(-1))));
        Assert.Equal(WorkflowItemType.ResumeInProgressIssue, result.Tasks.Single().Type);
    }

    [Fact]
    public async Task Current_change_request_pull_request_is_recovered()
    {
        var claim = Claim();
        var result = await WorkflowService.EvaluateClaimedWorkAsync(new RouterConfiguration(), claim, WorkingIssueWithPullRequestReference(), _ => Task.FromResult(Pull(claim, "codex/issue-4-current", "codex:cr", claim.ClaimedIssueUpdatedAt.AddMinutes(1))));
        Assert.Equal(WorkflowItemType.ChangeRequest, result.Tasks.Single().Type);
    }

    [Fact]
    public async Task Current_unlabeled_pull_request_is_recovered_for_lifecycle_handling()
    {
        var claim = Claim();
        var result = await WorkflowService.EvaluateClaimedWorkAsync(new RouterConfiguration(), claim, WorkingIssueWithPullRequestReference(), _ => Task.FromResult(Pull(claim, "codex/issue-4-current", null, claim.ClaimedIssueUpdatedAt.AddMinutes(1))));
        Assert.Equal(WorkflowItemType.RecoverCurrentPullRequest, result.Tasks.Single().Type);
    }

    [Fact]
    public async Task Historical_unlabeled_pull_request_is_not_current_recovery()
    {
        var claim = Claim();
        var result = await WorkflowService.EvaluateClaimedWorkAsync(new RouterConfiguration(), claim, WorkingIssueWithPullRequestReference(), _ => Task.FromResult(Pull(claim, "codex/issue-4-old", null, claim.ClaimedIssueUpdatedAt.AddMinutes(-1))));
        Assert.Equal(WorkflowItemType.ResumeInProgressIssue, result.Tasks.Single().Type);
    }

    [Fact]
    public void Claim_baseline_is_conservative_for_clock_ahead_and_behind_cases()
    {
        var baseline = DateTimeOffset.UtcNow.AddMinutes(-10);
        var claim = Claim(baseline, baseline.AddDays(1));
        var issue = WorkingIssueWithPullRequestReference();
        Assert.True(WorkflowService.IsCurrentClaimPullRequest(claim, issue, Pull(claim, "codex/issue-4-current", "codex:cr", claim.ClaimedIssueUpdatedAt.AddMinutes(1))));
        Assert.False(WorkflowService.IsCurrentClaimPullRequest(claim, issue, Pull(claim, "codex/issue-4-old", "codex:cr", claim.ClaimedIssueUpdatedAt.AddMinutes(-1))));
    }

    [Fact]
    public async Task Completed_claim_without_pull_request_recovers_implementation()
    {
        var result = await WorkflowService.EvaluateClaimedWorkAsync(new RouterConfiguration(), Claim(), CompletedIssueWithoutPullRequest(), _ => throw new InvalidOperationException());
        Assert.Equal(WorkflowItemType.RecoverCompletedIssue, result.Tasks.Single().Type);
    }

    [Fact]
    public async Task Completed_claim_with_historical_only_pull_request_recovers_implementation()
    {
        var claim = Claim();
        var issue = CompletedIssue();
        var result = await WorkflowService.EvaluateClaimedWorkAsync(new RouterConfiguration(), claim, issue, _ => Task.FromResult(Pull(claim, "codex/issue-4-old", "codex:rr", claim.ClaimedIssueUpdatedAt.AddMinutes(-1))));
        Assert.Equal(WorkflowItemType.RecoverCompletedIssue, result.Tasks.Single().Type);
    }

    [Fact]
    public async Task Explicit_pull_request_claim_with_deferred_pull_request_returns_deferred()
    {
        var baseClaim = Claim();
        var claim = new WorkClaim
        {
            ClaimId = baseClaim.ClaimId,
            Version = baseClaim.Version,
            OwnerSessionId = baseClaim.OwnerSessionId,
            IssueNumber = baseClaim.IssueNumber,
            PullRequestNumber = 21,
            WorkType = WorkClaimType.ChangeRequest,
            ClaimedIssueUpdatedAt = baseClaim.ClaimedIssueUpdatedAt,
            ClaimedAt = baseClaim.ClaimedAt,
            LastUpdatedAt = baseClaim.LastUpdatedAt
        };
        var result = await WorkflowService.EvaluateClaimedWorkAsync(new RouterConfiguration(), claim, CompletedIssue(), _ => Task.FromResult(Pull(claim, "codex/issue-4-current", "codex:deferred", claim.ClaimedIssueUpdatedAt.AddMinutes(1))));
        Assert.Equal(WorkflowItemType.Deferred, result.Tasks.Single().Type);
    }

    [Theory]
    [InlineData("codex:cr", "ChangeRequest")]
    [InlineData(null, "RecoverCurrentPullRequest")]
    [InlineData("merged", "CloseIssue")]
    [InlineData("closed", "ClosedWithoutMerge")]
    [InlineData("codex:deferred", "Deferred")]
    public async Task Completed_claim_handles_current_pull_request_states(string? labelOrState, string expectedType)
    {
        var claim = Claim();
        var state = labelOrState is "merged" or "closed" ? labelOrState : "open";
        var result = await WorkflowService.EvaluateClaimedWorkAsync(new RouterConfiguration(), claim, CompletedIssue(), _ => Task.FromResult(Pull(claim, "codex/issue-4-current", labelOrState is "merged" or "closed" ? null : labelOrState, claim.ClaimedIssueUpdatedAt.AddMinutes(1), state)));
        Assert.Equal(Enum.Parse<WorkflowItemType>(expectedType), result.Tasks.Single().Type);
    }

    [Fact]
    public void Confirmed_not_found_classification_does_not_match_generic_failures()
    {
        Assert.False(GitHubCliService.IsConfirmedNotFound("HTTP 404: not found"));
        Assert.True(GitHubCliService.IsConfirmedNotFound("Could not resolve to an issue"));
        Assert.True(GitHubCliService.IsConfirmedNotFound("Could not resolve to a PullRequest"));
        Assert.False(GitHubCliService.IsConfirmedNotFound("Could not resolve host: api.github.com"));
        Assert.False(GitHubCliService.IsConfirmedNotFound("authentication required"));
        Assert.False(GitHubCliService.IsConfirmedNotFound("rate limit exceeded"));
    }

    private static WorkClaim Claim(DateTimeOffset? baseline = null, DateTimeOffset? claimedAt = null)
    {
        var now = baseline ?? DateTimeOffset.UtcNow.AddMinutes(-5);
        return new WorkClaim { ClaimId = Guid.NewGuid(), Version = 1, OwnerSessionId = "owner", IssueNumber = 4, WorkType = WorkClaimType.Implementation, ClaimedIssueUpdatedAt = now, ClaimedAt = claimedAt ?? now, LastUpdatedAt = now };
    }

    private static Issue WorkingIssueWithPullRequestReference() => new()
    {
        Number = 4,
        Labels = new List<GithubLabel> { new() { Name = "codex:working" } },
        ClosingPullRequestsReferences = new List<ClosingIssueReference> { new() { Number = 21 } }
    };

    private static Issue CompletedIssue() => new()
    {
        Number = 4,
        Labels = new List<GithubLabel> { new() { Name = "codex:done" } },
        ClosingPullRequestsReferences = new List<ClosingIssueReference> { new() { Number = 21 } }
    };

    private static Issue CompletedIssueWithoutPullRequest() => new()
    {
        Number = 4,
        Labels = new List<GithubLabel> { new() { Name = "codex:done" } }
    };

    private static PullRequest Pull(WorkClaim claim, string branch, string? label, DateTimeOffset createdAt, string state = "open") => new()
    {
        Number = 21,
        State = state,
        CreatedAt = createdAt,
        HeadRefName = branch,
        Labels = label is null ? new List<GithubLabel>() : new List<GithubLabel> { new() { Name = label } },
        ClosingIssuesReferences = new List<ClosingIssueReference> { new() { Number = claim.IssueNumber } }
    };
}

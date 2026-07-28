using CodexGithubRouter.Work;
using CodexGithubRouter.Workflow;
using Xunit;

namespace CodexGithubRouter.Tests;

[Trait("Category", "Integration")]
public sealed class WorkClaimLifecycleTests
{
    [Fact]
    public async Task Concurrent_claims_have_one_owner()
    {
        using var sandbox = new TestSandbox();
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, new WorkClaim { OwnerSessionId = $"session-{i}", IssueNumber = 4, WorkType = WorkClaimType.Implementation })));
        Assert.Equal(1, attempts.Count(a => a.Acquired));
    }

    [Fact]
    public async Task Stale_release_preserves_replacement_claim()
    {
        using var sandbox = new TestSandbox();
        var first = (await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, new WorkClaim { OwnerSessionId = "a", IssueNumber = 4, PullRequestNumber = 21, WorkType = WorkClaimType.ChangeRequest })).Claim!;
        await WorkClaimStore.ReleaseForIssueAsync(sandbox.GitCommonDirectory, 4);
        var replacement = (await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, new WorkClaim { OwnerSessionId = "b", IssueNumber = 4, PullRequestNumber = 22, WorkType = WorkClaimType.ChangeRequest })).Claim!;
        Assert.False(await WorkClaimStore.ReleaseIfMatchesAsync(sandbox.GitCommonDirectory, first));
        Assert.Equal(replacement.ClaimId, (await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory))!.ClaimId);
    }

    [Fact]
    public async Task Pull_request_transition_releases_only_matching_claim()
    {
        using var sandbox = new TestSandbox();
        var claim = (await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, new WorkClaim { OwnerSessionId = "a", IssueNumber = 4, PullRequestNumber = 21, WorkType = WorkClaimType.ChangeRequest })).Claim!;
        Assert.False(await WorkClaimStore.ReleaseForPullRequestTransitionAsync(sandbox.GitCommonDirectory, claim, 22, new[] { 4 }, true));
        Assert.True(await WorkClaimStore.ReleaseForPullRequestTransitionAsync(sandbox.GitCommonDirectory, claim, 21, new[] { 4 }, true));
    }

    [Fact]
    public async Task Missing_baseline_is_preserved_during_claim_storage()
    {
        using var sandbox = new TestSandbox();
        var requested = new WorkClaim { OwnerSessionId = "legacy", IssueNumber = 4, WorkType = WorkClaimType.Implementation };
        var acquired = (await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, requested)).Claim!;
        var read = await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory);
        Assert.Equal(default, acquired.ClaimedIssueUpdatedAt);
        Assert.Equal(default, read!.ClaimedIssueUpdatedAt);
    }

    [Fact]
    public async Task Winning_session_can_continue_same_claim()
    {
        using var sandbox = new TestSandbox();
        var first = (await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, new WorkClaim { OwnerSessionId = "owner", IssueNumber = 4, WorkType = WorkClaimType.Implementation })).Claim!;
        var continuation = await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, new WorkClaim { OwnerSessionId = "owner", IssueNumber = 4, WorkType = WorkClaimType.Implementation });
        Assert.True(continuation.Acquired);
        Assert.Equal(first.ClaimId, continuation.Claim!.ClaimId);
    }

    [Fact]
    public async Task Another_session_receives_expected_ownership_block()
    {
        using var sandbox = new TestSandbox();
        await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, new WorkClaim { OwnerSessionId = "owner", IssueNumber = 4, WorkType = WorkClaimType.Implementation });
        var result = await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, new WorkClaim { OwnerSessionId = "other", IssueNumber = 4, WorkType = WorkClaimType.Implementation });
        Assert.False(result.Acquired);
        Assert.Contains("another Codex session", result.BlockReason);
    }

    [Fact]
    public async Task Same_owner_cannot_replace_active_claim_with_another_issue()
    {
        using var sandbox = new TestSandbox();
        await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, new WorkClaim { OwnerSessionId = "owner", IssueNumber = 4, WorkType = WorkClaimType.Implementation });
        var result = await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, new WorkClaim { OwnerSessionId = "owner", IssueNumber = 5, WorkType = WorkClaimType.Implementation });
        Assert.False(result.Acquired);
    }

    [Fact]
    public async Task Explicit_release_permits_claiming_new_work()
    {
        using var sandbox = new TestSandbox();
        await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, new WorkClaim { OwnerSessionId = "owner", IssueNumber = 4, WorkType = WorkClaimType.Implementation });
        Assert.True(await WorkClaimStore.ReleaseForIssueAsync(sandbox.GitCommonDirectory, 4));
        Assert.True((await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, new WorkClaim { OwnerSessionId = "owner", IssueNumber = 5, WorkType = WorkClaimType.Implementation })).Acquired);
    }

    [Fact]
    public async Task Supplied_github_baseline_is_persisted_unchanged()
    {
        using var sandbox = new TestSandbox();
        var baseline = DateTimeOffset.UtcNow.AddHours(-2);
        var claim = (await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, new WorkClaim { OwnerSessionId = "owner", IssueNumber = 4, WorkType = WorkClaimType.Implementation, ClaimedIssueUpdatedAt = baseline })).Claim!;
        Assert.Equal(baseline, (await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory))!.ClaimedIssueUpdatedAt);
        Assert.Equal(baseline, claim.ClaimedIssueUpdatedAt);
    }

    [Fact]
    public async Task Same_issue_different_pull_request_is_a_different_work_identity()
    {
        using var sandbox = new TestSandbox();
        var first = await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, new WorkClaim { OwnerSessionId = "owner", IssueNumber = 4, PullRequestNumber = 21, WorkType = WorkClaimType.ChangeRequest });
        var second = await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, new WorkClaim { OwnerSessionId = "owner", IssueNumber = 4, PullRequestNumber = 22, WorkType = WorkClaimType.ChangeRequest });
        Assert.True(first.Acquired);
        Assert.False(second.Acquired);
        Assert.Equal(21, (await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory))!.PullRequestNumber);
    }

    [Fact]
    public async Task Same_owner_can_enrich_pr_less_claim_without_changing_work_type()
    {
        using var sandbox = new TestSandbox();
        await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, new WorkClaim { OwnerSessionId = "owner", IssueNumber = 4, WorkType = WorkClaimType.Implementation });
        var result = await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, new WorkClaim { OwnerSessionId = "owner", IssueNumber = 4, PullRequestNumber = 21, WorkType = WorkClaimType.ChangeRequest });
        Assert.True(result.Acquired);
        Assert.Equal(WorkClaimType.Implementation, result.Claim!.WorkType);
        Assert.Equal(21, result.Claim.PullRequestNumber);
    }

    [Fact]
    public async Task Stale_revision_cannot_delete_newer_continuation_of_same_claim()
    {
        using var sandbox = new TestSandbox();
        var first = (await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, new WorkClaim { OwnerSessionId = "owner", IssueNumber = 4, WorkType = WorkClaimType.Implementation })).Claim!;
        var continuation = (await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, new WorkClaim { OwnerSessionId = "owner", IssueNumber = 4, WorkType = WorkClaimType.Implementation })).Claim!;
        Assert.Equal(first.ClaimId, continuation.ClaimId);
        Assert.True(continuation.Version > first.Version);
        Assert.False(await WorkClaimStore.ReleaseIfMatchesAsync(sandbox.GitCommonDirectory, first));
        Assert.Equal(continuation.Version, (await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory))!.Version);
    }

    [Fact]
    public async Task Historical_pr_does_not_release_pr_less_claim_until_current_is_proven()
    {
        using var sandbox = new TestSandbox();
        var claim = (await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, new WorkClaim { OwnerSessionId = "owner", IssueNumber = 4, WorkType = WorkClaimType.Implementation })).Claim!;
        Assert.False(await WorkClaimStore.ReleaseForPullRequestTransitionAsync(sandbox.GitCommonDirectory, claim, 18, new[] { 4 }, true));
        Assert.NotNull(await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory));
        Assert.True(await WorkClaimStore.ReleaseForPullRequestTransitionAsync(sandbox.GitCommonDirectory, claim, 18, new[] { 4 }, true, true));
        Assert.Null(await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory));
    }

    [Fact]
    public async Task Pull_request_closing_another_issue_does_not_release_claim()
    {
        using var sandbox = new TestSandbox();
        var claim = (await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, new WorkClaim { OwnerSessionId = "owner", IssueNumber = 4, WorkType = WorkClaimType.Implementation })).Claim!;
        Assert.False(await WorkClaimStore.ReleaseForPullRequestTransitionAsync(sandbox.GitCommonDirectory, claim, 18, new[] { 5 }, true, true));
        Assert.NotNull(await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory));
    }

    [Fact]
    public void Terminal_issue_transition_remains_eligible_for_cleanup()
    {
        Assert.True(WorkClaimReconciliationService.ShouldReleaseForIssueTransition(new WorkClaim { IssueNumber = 4 }, 4, WorkflowState.Blocked));
    }
}

using CodexGithubRouter.Work;
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
}

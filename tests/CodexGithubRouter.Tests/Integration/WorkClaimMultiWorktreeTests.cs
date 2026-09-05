using CodexGithubRouter.Work;
using Xunit;

namespace CodexGithubRouter.Tests;

[Trait("Category", "Integration")]
public sealed class WorkClaimMultiWorktreeTests
{
    [Fact]
    public async Task Two_worktrees_each_claim_a_different_issue_in_the_same_repository()
    {
        using var sandbox = new TestSandbox();
        var linkedWorktree = sandbox.CreateLinkedWorktree("wt-a");

        var mainClaim = await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId, new WorkClaim
        {
            OwnerSessionId = "session-main",
            IssueNumber = 4,
            WorkType = WorkClaimType.Implementation
        });
        var linkedClaim = await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, linkedWorktree, new WorkClaim
        {
            OwnerSessionId = "session-a",
            IssueNumber = 5,
            WorkType = WorkClaimType.Implementation
        });

        Assert.True(mainClaim.Acquired);
        Assert.True(linkedClaim.Acquired);
        Assert.Equal(4, (await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId))!.IssueNumber);
        Assert.Equal(5, (await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, linkedWorktree))!.IssueNumber);

        var all = await WorkClaimStore.ReadAllAsync(sandbox.GitCommonDirectory);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, claim => claim.WorktreeId == Path.GetFullPath(sandbox.MainWorktreeId));
        Assert.Contains(all, claim => claim.WorktreeId == Path.GetFullPath(linkedWorktree));
    }

    [Fact]
    public async Task Second_worktree_cannot_claim_work_already_owned_by_another_worktree()
    {
        using var sandbox = new TestSandbox();
        var linkedWorktree = sandbox.CreateLinkedWorktree("wt-a");
        var owner = await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId, new WorkClaim
        {
            OwnerSessionId = "session-main",
            IssueNumber = 4,
            WorkType = WorkClaimType.Implementation
        });
        Assert.True(owner.Acquired);

        var blocked = await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, linkedWorktree, new WorkClaim
        {
            OwnerSessionId = "session-a",
            IssueNumber = 4,
            WorkType = WorkClaimType.Implementation
        });

        Assert.False(blocked.Acquired);
        Assert.Contains("another Git worktree", blocked.BlockReason);
        Assert.Equal(4, (await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId))!.IssueNumber);
        Assert.Null(await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, linkedWorktree));
    }

    [Fact]
    public async Task Worktree_cannot_acquire_a_second_active_work_item_while_it_owns_one()
    {
        using var sandbox = new TestSandbox();
        var linkedWorktree = sandbox.CreateLinkedWorktree("wt-a");
        var first = await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId, new WorkClaim
        {
            OwnerSessionId = "session-main",
            IssueNumber = 4,
            WorkType = WorkClaimType.Implementation
        });
        Assert.True(first.Acquired);

        var blocked = await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId, new WorkClaim
        {
            OwnerSessionId = "session-main",
            IssueNumber = 6,
            WorkType = WorkClaimType.Implementation
        });

        Assert.False(blocked.Acquired);
        Assert.Contains("a worktree can hold only one active work item", blocked.BlockReason);
        Assert.Equal(4, (await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId))!.IssueNumber);
    }

    [Fact]
    public async Task Release_in_one_worktree_preserves_claims_owned_by_other_worktrees()
    {
        using var sandbox = new TestSandbox();
        var linkedWorktree = sandbox.CreateLinkedWorktree("wt-a");
        Assert.True((await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId, new WorkClaim
        {
            OwnerSessionId = "session-main",
            IssueNumber = 4,
            WorkType = WorkClaimType.Implementation
        })).Acquired);
        Assert.True((await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, linkedWorktree, new WorkClaim
        {
            OwnerSessionId = "session-a",
            IssueNumber = 5,
            WorkType = WorkClaimType.Implementation
        })).Acquired);

        Assert.True(await WorkClaimStore.ReleaseForIssueAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId, 4));

        Assert.Null(await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId));
        Assert.Equal(5, (await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, linkedWorktree))!.IssueNumber);

        var all = await WorkClaimStore.ReadAllAsync(sandbox.GitCommonDirectory);
        var single = Assert.Single(all);
        Assert.Equal(5, single.IssueNumber);
    }

    [Fact]
    public async Task Every_worktree_observes_the_full_repository_wide_claim_set()
    {
        using var sandbox = new TestSandbox();
        var linkedWorktree = sandbox.CreateLinkedWorktree("wt-a");
        await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId, new WorkClaim
        {
            OwnerSessionId = "session-main",
            IssueNumber = 4,
            WorkType = WorkClaimType.Implementation
        });
        await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, linkedWorktree, new WorkClaim
        {
            OwnerSessionId = "session-a",
            IssueNumber = 5,
            WorkType = WorkClaimType.Implementation
        });

        var fromLinked = await WorkClaimStore.ReadAllAsync(sandbox.GitCommonDirectory);
        var fromMain = await WorkClaimStore.ReadAllAsync(sandbox.GitCommonDirectory);

        Assert.Equal(2, fromLinked.Count);
        Assert.Equal(fromMain.Select(claim => claim.ClaimId).OrderBy(id => id), fromLinked.Select(claim => claim.ClaimId).OrderBy(id => id));
    }

    [Fact]
    public async Task Prune_removes_claims_for_deleted_worktrees_and_preserves_live_worktrees()
    {
        using var sandbox = new TestSandbox();
        var linkedWorktree = sandbox.CreateLinkedWorktree("wt-a");
        await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId, new WorkClaim
        {
            OwnerSessionId = "session-main",
            IssueNumber = 4,
            WorkType = WorkClaimType.Implementation
        });
        await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, linkedWorktree, new WorkClaim
        {
            OwnerSessionId = "session-a",
            IssueNumber = 5,
            WorkType = WorkClaimType.Implementation
        });

        Directory.Delete(linkedWorktree);
        var removed = await WorkClaimStore.PruneStaleWorktreesAsync(sandbox.GitCommonDirectory, Directory.Exists);

        Assert.Equal(1, removed);
        Assert.Equal(4, (await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId))!.IssueNumber);
        Assert.Null(await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, linkedWorktree));
    }

    [Fact]
    public async Task Concurrency_across_worktrees_yields_exactly_one_claim_per_issue()
    {
        using var sandbox = new TestSandbox();
        var linkedWorktree = sandbox.CreateLinkedWorktree("wt-a");

        var attempts = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(index => WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId, new WorkClaim
            {
                OwnerSessionId = $"main-{index}",
                IssueNumber = 4,
                WorkType = WorkClaimType.Implementation
            }))
            .Concat(Enumerable.Range(0, 8).Select(index => WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, linkedWorktree, new WorkClaim
            {
                OwnerSessionId = $"linked-{index}",
                IssueNumber = 5,
                WorkType = WorkClaimType.Implementation
            }))));

        Assert.Equal(1, attempts.Count(attempt => attempt.Acquired && attempt.Claim!.IssueNumber == 4));
        Assert.Equal(1, attempts.Count(attempt => attempt.Acquired && attempt.Claim!.IssueNumber == 5));
        Assert.Equal(2, (await WorkClaimStore.ReadAllAsync(sandbox.GitCommonDirectory)).Count);
    }

    [Fact]
    public async Task Legacy_claim_migrates_to_the_main_worktree()
    {
        using var sandbox = new TestSandbox();
        var claimId = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(sandbox.GitCommonDirectory, "codex-github-router.work.json"), $$"""
        {
          "ClaimId": "{{claimId}}",
          "Version": 1,
          "OwnerSessionId": "legacy-session",
          "IssueNumber": 4,
          "WorkType": 0,
          "ClaimedAt": "2026-07-28T12:00:00+00:00",
          "LastUpdatedAt": "2026-07-28T12:00:00+00:00"
        }
        """);

        var claim = await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId);

        Assert.NotNull(claim);
        Assert.Equal(Path.GetFullPath(sandbox.GitCommonDirectory), claim!.WorktreeId);
        var content = await File.ReadAllTextAsync(Path.Combine(sandbox.GitCommonDirectory, "codex-github-router.work.json"));
        Assert.Contains("\"Claims\"", content);
    }
}
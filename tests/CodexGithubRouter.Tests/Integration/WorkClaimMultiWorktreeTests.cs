using CodexGithubRouter.Configurations;
using CodexGithubRouter.Work;
using CodexGithubRouter.GitHub;
using CodexGithubRouter.Workflow;
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
        Assert.Contains(all, claim => claim.WorktreeId == WorkClaimStore.MainWorktreeIdentity && claim.WorktreePath == Path.GetFullPath(sandbox.MainWorktreeId));
        Assert.Contains(all, claim => claim.WorktreeId == Path.GetRelativePath(sandbox.GitCommonDirectory, linkedWorktree) && claim.WorktreePath == Path.GetFullPath(linkedWorktree));
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
    public async Task Cross_worktree_claim_conflicts_by_issue_before_pull_request_enrichment()
    {
        using var sandbox = new TestSandbox();
        var linkedWorktree = sandbox.CreateLinkedWorktree("wt-a");
        Assert.True((await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId, new WorkClaim
        {
            OwnerSessionId = "session-main",
            IssueNumber = 4,
            WorkType = WorkClaimType.Implementation
        })).Acquired);

        // Worktree A holds issue #4 without a pull-request identity. Another worktree
        // attempting the same issue after it gained a linked pull request must still be
        // blocked; the enriched identity does not reopen the issue for a second worktree.
        var blocked = await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, linkedWorktree, new WorkClaim
        {
            OwnerSessionId = "session-a",
            IssueNumber = 4,
            PullRequestNumber = 20,
            WorkType = WorkClaimType.Implementation
        });

        Assert.False(blocked.Acquired);
        Assert.Contains("owned by another Git worktree", blocked.BlockReason);

        // The owning worktree may still enrich its own PR-less claim with the candidate PR.
        var enriched = await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId, new WorkClaim
        {
            OwnerSessionId = "session-main",
            IssueNumber = 4,
            PullRequestNumber = 20,
            WorkType = WorkClaimType.Implementation
        });
        Assert.True(enriched.Acquired);
        Assert.Equal(4, (await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId))!.IssueNumber);
        Assert.Equal(20, (await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId))!.PullRequestNumber);
        Assert.Single(await WorkClaimStore.ReadAllAsync(sandbox.GitCommonDirectory));
    }

    [Fact]
    public async Task Cross_worktree_claim_conflicts_by_pull_request_across_different_issues()
    {
        using var sandbox = new TestSandbox();
        var linkedWorktree = sandbox.CreateLinkedWorktree("wt-a");
        Assert.True((await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId, new WorkClaim
        {
            OwnerSessionId = "session-main",
            IssueNumber = 4,
            PullRequestNumber = 20,
            WorkType = WorkClaimType.Implementation
        })).Acquired);

        // The same pull request claimed under a different issue number is the same work item.
        var blocked = await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, linkedWorktree, new WorkClaim
        {
            OwnerSessionId = "session-a",
            IssueNumber = 9,
            PullRequestNumber = 20,
            WorkType = WorkClaimType.Implementation
        });

        Assert.False(blocked.Acquired);
        Assert.Contains("pull request #20", blocked.BlockReason);
        Assert.Contains("owned by another Git worktree", blocked.BlockReason);
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
    public async Task Reconcile_all_releases_passive_claims_across_multiple_live_worktrees()
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

        var result = await WorkClaimReconciliationService.ReconcileAllAsync(
            sandbox.RepositoryDirectory,
            sandbox.GitCommonDirectory,
            new RouterConfiguration(),
            getIssue: number => Task.FromResult(new Issue { Number = number, State = "closed", Labels = new List<GithubLabel>() }));

        Assert.Equal(2, result.ReleasedCount);
        Assert.Equal(0, result.PrunedCount);
        Assert.Null(await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId));
        Assert.Null(await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, linkedWorktree));
    }

    [Fact]
    public async Task Reconcile_all_fails_closed_when_claim_github_state_cannot_be_verified()
    {
        using var sandbox = new TestSandbox();
        Assert.True((await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId, new WorkClaim
        {
            OwnerSessionId = "session-main",
            IssueNumber = 4,
            WorkType = WorkClaimType.Implementation
        })).Acquired);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkClaimReconciliationService.ReconcileAllAsync(
                sandbox.RepositoryDirectory,
                sandbox.GitCommonDirectory,
                new RouterConfiguration(),
                getIssue: _ => throw new InvalidOperationException("transient GitHub failure")));

        Assert.Contains("could not be verified", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId));
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
        Assert.Equal(WorkClaimStore.MainWorktreeIdentity, claim!.WorktreeId);
        Assert.Equal(Path.GetFullPath(sandbox.GitCommonDirectory), claim.WorktreePath);
        var content = await File.ReadAllTextAsync(Path.Combine(sandbox.GitCommonDirectory, "codex-github-router.work.json"));
        Assert.Contains("\"Claims\"", content);
    }

    [Fact]
    public async Task Legacy_claim_read_from_a_linked_worktree_migrates_to_the_main_worktree()
    {
        using var sandbox = new TestSandbox();
        var linkedWorktree = sandbox.CreateLinkedWorktree("wt-a");
        var claimId = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(sandbox.GitCommonDirectory, "codex-github-router.work.json"), $$"""
        {
          "ClaimId": "{{claimId}}",
          "Version": 1,
          "OwnerSessionId": "legacy-session",
          "IssueNumber": 6,
          "WorkType": 0,
          "ClaimedAt": "2026-07-28T12:00:00+00:00",
          "LastUpdatedAt": "2026-07-28T12:00:00+00:00"
        }
        """);

        // A legacy (single-claim) file that predates worktree-scoped claims is shared
        // across the repository; reading it from a linked worktree migrates the claim to the
        // main worktree, never to the invoking linked worktree.
        var claim = await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId);

        Assert.NotNull(claim);
        Assert.Equal(WorkClaimStore.MainWorktreeIdentity, claim!.WorktreeId);
        Assert.Equal(Path.GetFullPath(sandbox.GitCommonDirectory), claim.WorktreePath);
        Assert.Null(await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, linkedWorktree));
    }

    [Fact]
    public async Task Worktree_path_normalization_treats_separator_variants_as_the_same_worktree()
    {
        using var sandbox = new TestSandbox();
        var linkedWorktree = sandbox.CreateLinkedWorktree("wt-a");
        var withTrailingSeparator = linkedWorktree.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        var claimed = await WorkClaimStore.TryAcquireAsync(
            sandbox.GitCommonDirectory,
            linkedWorktree,
            new WorkClaim
            {
                OwnerSessionId = "owner",
                IssueNumber = 9,
                WorkType = WorkClaimType.Implementation
            });
        Assert.True(claimed.Acquired);

        // The same worktree addressed via a trailing-separator variant must be recognized as
        // the same worktree (continuation), not as a conflicting peer.
        var releaseMatch = await WorkClaimStore.ReleaseIfMatchesAsync(
            sandbox.GitCommonDirectory,
            withTrailingSeparator,
            claimed.Claim!);

        Assert.True(releaseMatch);
        Assert.Null(await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, linkedWorktree));
    }

    [Fact]
    public async Task Deleted_peer_worktree_claim_is_excluded_by_read_only_diagnostics_without_writing()
    {
        using var sandbox = new TestSandbox();
        var linkedWorktree = sandbox.CreateLinkedWorktree("wt-a");
        var acquired = await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, linkedWorktree, new WorkClaim
        {
            OwnerSessionId = "session-a",
            IssueNumber = 4,
            WorkType = WorkClaimType.Implementation
        });
        Assert.True(acquired.Acquired);
        Assert.Equal(Path.GetRelativePath(sandbox.GitCommonDirectory, linkedWorktree), acquired.Claim!.WorktreeId);

        var claimFilePath = Path.Combine(sandbox.GitCommonDirectory, WorkClaimStore.ClaimFileName);
        Directory.Delete(linkedWorktree, recursive: true);

        // Read-only diagnostics apply the same stale evaluation production pruning uses but
        // must not write: the deleted worktree's claim is excluded while the file is untouched.
        var beforeBytes = await File.ReadAllBytesAsync(claimFilePath);
        var activeClaims = await WorkClaimStore.TryReadActiveClaimsAsync(sandbox.GitCommonDirectory);
        var afterBytes = await File.ReadAllBytesAsync(claimFilePath);

        Assert.Empty(activeClaims);
        Assert.Equal(beforeBytes, afterBytes);
        Assert.Single(await WorkClaimStore.TryReadAllAsync(sandbox.GitCommonDirectory));
        Assert.True(WorkClaimStore.IsStaleWorktree(sandbox.GitCommonDirectory, acquired.Claim));
    }

    [Fact]
    public async Task Deleted_peer_worktree_claim_is_pruned_by_production_mutation()
    {
        using var sandbox = new TestSandbox();
        var linkedWorktree = sandbox.CreateLinkedWorktree("wt-a");
        var acquired = await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, linkedWorktree, new WorkClaim
        {
            OwnerSessionId = "session-a",
            IssueNumber = 4,
            WorkType = WorkClaimType.Implementation
        });
        Assert.True(acquired.Acquired);
        Directory.Delete(linkedWorktree, recursive: true);

        Assert.Equal(1, await WorkClaimStore.PruneStaleWorktreesAsync(sandbox.GitCommonDirectory, Directory.Exists));
        Assert.Empty(await WorkClaimStore.ReadAllAsync(sandbox.GitCommonDirectory));
    }

    [Fact]
    public async Task Deleted_peer_worktree_claim_leaves_top_ready_issue_free_for_hook_and_diagnostics()
    {
        using var sandbox = new TestSandbox();
        var linkedWorktree = sandbox.CreateLinkedWorktree("wt-a");
        var acquired = await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, linkedWorktree, new WorkClaim
        {
            OwnerSessionId = "session-a",
            IssueNumber = 4,
            WorkType = WorkClaimType.Implementation
        });
        Assert.True(acquired.Acquired);
        Directory.Delete(linkedWorktree, recursive: true);

        // Diagnostics read only live claims; the hook prunes first and filters the same
        // evaluation as a defensive backstop. Both must treat issue #4 as free.
        var activeClaims = await WorkClaimStore.TryReadActiveClaimsAsync(sandbox.GitCommonDirectory);
        var hookView = (await WorkClaimStore.ReadAllAsync(sandbox.GitCommonDirectory))
            .Where(claim => !WorkClaimStore.IsStaleWorktree(sandbox.GitCommonDirectory, claim))
            .ToList();

        Assert.Empty(activeClaims);
        Assert.Empty(hookView);

        var plan = await RoutingEvaluationService.EvaluateAsync(
            new RouterConfiguration(),
            workingDirectory: "wd",
            otherWorktreeClaims: activeClaims,
            dependencies: new RoutingEvaluationDependencies
            {
                CheckRepositoryGateAsync = (_, _) => Task.FromResult(OkGate()),
                CheckCompletedIssuesAsync = (_, _, _, _) => Task.FromResult(Ok()),
                CheckInProgressIssuesAsync = (_, _, _, _) => Task.FromResult(Ok()),
                CheckNewIssuesAsync = (_, _, _, _) => Task.FromResult(Ok(new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 4, SelectionRank = 0 }))
            });

        Assert.True(plan.IsSuccessful);
        Assert.Empty(plan.IneligibleOccupiedClaims);
        Assert.NotNull(plan.Decision);
        Assert.Equal(4, plan.Decision!.SelectedTask!.IssueNumber);
        Assert.Equal(WorkflowItemType.NewIssue, plan.Decision.SelectedTask.Type);
    }

    [Fact]
    public async Task Main_worktree_claim_survives_repository_relocation()
    {
        using var sandbox = new TestSandbox();
        var mainClaim = await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId, new WorkClaim
        {
            OwnerSessionId = "session-main",
            IssueNumber = 11,
            WorkType = WorkClaimType.Implementation
        });
        Assert.True(mainClaim.Acquired);
        Assert.Equal(WorkClaimStore.MainWorktreeIdentity, mainClaim.Claim!.WorktreeId);

        var relocatedRoot = Path.Combine(Path.GetTempPath(), "codex-github-router-tests-relocated", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(relocatedRoot)!);
        Directory.Move(sandbox.Root, relocatedRoot);
        var relocatedCommon = Path.Combine(relocatedRoot, "git-common");

        // The main claim is keyed to the stable sentinel, never the old absolute git-dir, so it
        // still reads from the relocated repository and production pruning keeps it live.
        Assert.NotNull(await WorkClaimStore.ReadAsync(relocatedCommon, relocatedCommon));
        Assert.Equal(0, await WorkClaimStore.PruneStaleWorktreesAsync(relocatedCommon, Directory.Exists));
        Assert.NotNull(await WorkClaimStore.ReadAsync(relocatedCommon, relocatedCommon));

        var relocatedLinkedWorktree = WorkClaimStore.MainWorktreeIdentity;
        Assert.NotNull(await WorkClaimStore.TryReadAsync(relocatedCommon, relocatedLinkedWorktree));

        Directory.Delete(relocatedRoot, recursive: true);
    }

    [Fact]
    public async Task Linked_worktree_claim_identity_is_common_relative()
    {
        using var sandbox = new TestSandbox();
        var linkedWorktree = sandbox.CreateLinkedWorktree("wt-a");
        var acquired = await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, linkedWorktree, new WorkClaim
        {
            OwnerSessionId = "session-a",
            IssueNumber = 7,
            WorkType = WorkClaimType.Implementation
        });
        Assert.True(acquired.Acquired);

        // Linked worktrees are identified relative to the Git common directory, so ownership
        // survives repository relocation; the absolute git-dir is kept as diagnostic metadata.
        Assert.Equal(Path.GetRelativePath(sandbox.GitCommonDirectory, linkedWorktree), acquired.Claim!.WorktreeId);
        Assert.Equal(Path.GetFullPath(linkedWorktree), acquired.Claim.WorktreePath);
        Assert.Equal(7, (await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, linkedWorktree))!.IssueNumber);

        var storedClaim = await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, linkedWorktree);
        Assert.NotNull(storedClaim);
        Assert.False(WorkClaimStore.IsStaleWorktree(sandbox.GitCommonDirectory, storedClaim!));
    }

    private static WorkflowResponse OkGate() => new()
    {
        IsSuccessful = true,
        Tasks = new List<WorkflowItem>(),
        Message = "No blocking repository gates found.",
        ConsideredIssues = new List<Issue>()
    };

    private static WorkflowResponse Ok(params WorkflowItem[] tasks) => new() { IsSuccessful = true, Tasks = tasks.ToList() };
}
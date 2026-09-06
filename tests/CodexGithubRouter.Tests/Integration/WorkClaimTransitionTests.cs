using CodexGithubRouter.GitHub;
using CodexGithubRouter.Work;
using CodexGithubRouter.Workflow;
using Xunit;

namespace CodexGithubRouter.Tests;

[Trait("Category", "Integration")]
public sealed class WorkClaimTransitionTests
{
    [Fact]
    public async Task Issue_transition_from_a_different_worktree_releases_the_matching_claim()
    {
        using var sandbox = new TestSandbox();
        var linkedWorktree = sandbox.CreateLinkedWorktree("wt-a");

        // The linked worktree owns the claim; the command runs from the main worktree, which
        // is exactly the scenario that previously released nothing.
        Assert.True((await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, linkedWorktree, new WorkClaim
        {
            OwnerSessionId = "session-a",
            IssueNumber = 4,
            WorkType = WorkClaimType.Implementation
        })).Acquired);

        var transitioned = false;
        var dependencies = new IssueCommandDependencies
        {
            ResolveGitCommonDirectoryAsync = _ => Task.FromResult<string?>(sandbox.GitCommonDirectory),
            LoadConfigurationAsync = _ => Task.FromResult(new RouterConfiguration()),
            GetIssueByNumberAsync = (_, number) => Task.FromResult(Issue(number, "codex:working")),
            TransitionIssueAsync = (_, _) => { transitioned = true; return Task.CompletedTask; }
        };

        var result = await IssuesCommandHandler.HandleAsync(new[] { "transition", "4", "blocked", sandbox.RepositoryDirectory }, dependencies);

        Assert.Equal(0, result);
        Assert.True(transitioned);
        Assert.Null(await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, linkedWorktree));
        Assert.Empty(await WorkClaimStore.ReadAllAsync(sandbox.GitCommonDirectory));
    }

    [Fact]
    public async Task Issue_transition_no_op_branch_still_releases_claim_owned_by_another_worktree()
    {
        using var sandbox = new TestSandbox();
        var linkedWorktree = sandbox.CreateLinkedWorktree("wt-a");
        Assert.True((await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, linkedWorktree, new WorkClaim
        {
            OwnerSessionId = "session-a",
            IssueNumber = 4,
            WorkType = WorkClaimType.Implementation
        })).Acquired);

        var dependencies = new IssueCommandDependencies
        {
            ResolveGitCommonDirectoryAsync = _ => Task.FromResult<string?>(sandbox.GitCommonDirectory),
            LoadConfigurationAsync = _ => Task.FromResult(new RouterConfiguration()),
            GetIssueByNumberAsync = (_, number) => Task.FromResult(Issue(number, "codex:blocked")),
            TransitionIssueAsync = (_, _) => throw new InvalidOperationException("An already-blocked issue must not be transitioned again.")
        };

        var result = await IssuesCommandHandler.HandleAsync(new[] { "transition", "4", "blocked", sandbox.RepositoryDirectory }, dependencies);

        Assert.Equal(0, result);
        Assert.Null(await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, linkedWorktree));
        Assert.Empty(await WorkClaimStore.ReadAllAsync(sandbox.GitCommonDirectory));
    }

    [Fact]
    public async Task Issue_transition_does_not_release_a_claim_for_an_unrelated_issue()
    {
        using var sandbox = new TestSandbox();
        var linkedWorktree = sandbox.CreateLinkedWorktree("wt-a");
        Assert.True((await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, linkedWorktree, new WorkClaim
        {
            OwnerSessionId = "session-a",
            IssueNumber = 5,
            WorkType = WorkClaimType.Implementation
        })).Acquired);

        var dependencies = new IssueCommandDependencies
        {
            ResolveGitCommonDirectoryAsync = _ => Task.FromResult<string?>(sandbox.GitCommonDirectory),
            LoadConfigurationAsync = _ => Task.FromResult(new RouterConfiguration()),
            GetIssueByNumberAsync = (_, number) => Task.FromResult(Issue(number, number == 4 ? "codex:working" : "codex:blocked")),
            TransitionIssueAsync = (_, _) => Task.CompletedTask
        };

        var result = await IssuesCommandHandler.HandleAsync(new[] { "transition", "4", "blocked", sandbox.RepositoryDirectory }, dependencies);

        Assert.Equal(0, result);
        Assert.Equal(5, (await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, linkedWorktree))!.IssueNumber);
    }

    [Fact]
    public async Task Pull_request_transition_from_a_different_worktree_releases_the_matching_claim()
    {
        using var sandbox = new TestSandbox();
        var linkedWorktree = sandbox.CreateLinkedWorktree("wt-a");
        Assert.True((await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, linkedWorktree, new WorkClaim
        {
            OwnerSessionId = "session-a",
            IssueNumber = 4,
            PullRequestNumber = 20,
            WorkType = WorkClaimType.Implementation
        })).Acquired);

        var transitioned = false;
        var pullRequest = Pull(20, "codex:cr", 4);
        var dependencies = new PullRequestCommandDependencies
        {
            ResolveGitCommonDirectoryAsync = _ => Task.FromResult<string?>(sandbox.GitCommonDirectory),
            LoadConfigurationAsync = _ => Task.FromResult(new RouterConfiguration()),
            GetPullRequestAsync = (_, number, _) => Task.FromResult(pullRequest),
            GetIssueByNumberAsync = (_, number) => Task.FromResult(Issue(number, "codex:working")),
            TransitionPullRequestAsync = (_, _) => { transitioned = true; return Task.CompletedTask; }
        };

        var result = await PullRequestCommandHandler.HandleAsync(new[] { "transition", "20", "review-requested", sandbox.RepositoryDirectory }, dependencies);

        Assert.Equal(0, result);
        Assert.True(transitioned);
        Assert.Null(await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, linkedWorktree));
        Assert.Empty(await WorkClaimStore.ReadAllAsync(sandbox.GitCommonDirectory));
    }

    [Fact]
    public async Task Pull_request_transition_releases_pr_less_implementation_claim_of_another_worktree()
    {
        using var sandbox = new TestSandbox();
        var linkedWorktree = sandbox.CreateLinkedWorktree("wt-a");
        var claimedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        Assert.True((await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, linkedWorktree, new WorkClaim
        {
            OwnerSessionId = "session-a",
            IssueNumber = 4,
            WorkType = WorkClaimType.Implementation,
            ClaimedIssueUpdatedAt = claimedAt
        })).Acquired);

        var pullRequest = new PullRequest
        {
            Number = 20,
            State = "open",
            Labels = new List<GithubLabel> { new() { Name = "codex:cr" } },
            CreatedAt = claimedAt.AddMinutes(1),
            HeadRefName = "codex/issue-4",
            ClosingIssuesReferences = new List<ClosingIssueReference> { new() { Number = 4 } }
        };
        var dependencies = new PullRequestCommandDependencies
        {
            ResolveGitCommonDirectoryAsync = _ => Task.FromResult<string?>(sandbox.GitCommonDirectory),
            LoadConfigurationAsync = _ => Task.FromResult(new RouterConfiguration()),
            GetPullRequestAsync = (_, number, _) => Task.FromResult(pullRequest),
            GetIssueByNumberAsync = (_, number) => Task.FromResult(Issue(number, "codex:working")),
            TransitionPullRequestAsync = (_, _) => Task.CompletedTask
        };

        var result = await PullRequestCommandHandler.HandleAsync(new[] { "transition", "20", "review-requested", sandbox.RepositoryDirectory }, dependencies);

        Assert.Equal(0, result);
        Assert.Null(await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, linkedWorktree));
        Assert.Empty(await WorkClaimStore.ReadAllAsync(sandbox.GitCommonDirectory));
    }

    [Fact]
    public async Task Pull_request_transition_does_not_release_claims_for_unrelated_work()
    {
        using var sandbox = new TestSandbox();
        var linkedWorktree = sandbox.CreateLinkedWorktree("wt-a");
        Assert.True((await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, linkedWorktree, new WorkClaim
        {
            OwnerSessionId = "session-a",
            IssueNumber = 4,
            PullRequestNumber = 20,
            WorkType = WorkClaimType.ChangeRequest
        })).Acquired);
        Assert.True((await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId, new WorkClaim
        {
            OwnerSessionId = "session-main",
            IssueNumber = 5,
            PullRequestNumber = 21,
            WorkType = WorkClaimType.ChangeRequest
        })).Acquired);

        var dependencies = new PullRequestCommandDependencies
        {
            ResolveGitCommonDirectoryAsync = _ => Task.FromResult<string?>(sandbox.GitCommonDirectory),
            LoadConfigurationAsync = _ => Task.FromResult(new RouterConfiguration()),
            GetPullRequestAsync = (_, number, _) => Task.FromResult(number == 21
                ? Pull(21, "codex:cr", 5)
                : Pull(20, "codex:cr", 4)),
            GetIssueByNumberAsync = (_, number) => Task.FromResult(Issue(number, "codex:working")),
            TransitionPullRequestAsync = (_, _) => Task.CompletedTask
        };

        var result = await PullRequestCommandHandler.HandleAsync(new[] { "transition", "21", "review-requested", sandbox.RepositoryDirectory }, dependencies);

        Assert.Equal(0, result);
        Assert.Null(await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId));
        Assert.Equal(4, (await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, linkedWorktree))!.IssueNumber);
    }

    private static Issue Issue(int number, string label) => new()
    {
        Number = number,
        Labels = new List<GithubLabel> { new() { Name = label } },
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static PullRequest Pull(int number, string label, params int[] closingIssueNumbers) => new()
    {
        Number = number,
        State = "open",
        Labels = new List<GithubLabel> { new() { Name = label } },
        CreatedAt = DateTimeOffset.UtcNow,
        HeadRefName = $"codex/issue-{closingIssueNumbers.FirstOrDefault()}",
        ClosingIssuesReferences = closingIssueNumbers.Select(closingNumber => new ClosingIssueReference { Number = closingNumber }).ToList()
    };
}
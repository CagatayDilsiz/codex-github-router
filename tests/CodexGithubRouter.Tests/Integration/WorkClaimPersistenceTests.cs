using CodexGithubRouter.GitHub;
using CodexGithubRouter.Work;
using CodexGithubRouter.Workflow;
using Xunit;

namespace CodexGithubRouter.Tests;

[Trait("Category", "Integration")]
public sealed class WorkClaimPersistenceTests
{
    [Fact]
    public async Task Read_fails_explicitly_for_partially_written_claim()
    {
        using var sandbox = new TestSandbox();
        var claimPath = Path.Combine(sandbox.GitCommonDirectory, "codex-github-router.work.json");
        await File.WriteAllTextAsync(claimPath, "{\"ClaimId\":");

        await Assert.ThrowsAsync<WorkClaimFileException>(() => WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory));
    }

    [Fact]
    public async Task Read_rejects_null_claim_json()
    {
        using var sandbox = new TestSandbox();
        await File.WriteAllTextAsync(Path.Combine(sandbox.GitCommonDirectory, "codex-github-router.work.json"), "null");

        await Assert.ThrowsAsync<WorkClaimFileException>(() => WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory));
    }

    [Fact]
    public async Task Read_rejects_claim_with_missing_required_fields()
    {
        using var sandbox = new TestSandbox();
        await File.WriteAllTextAsync(Path.Combine(sandbox.GitCommonDirectory, "codex-github-router.work.json"), "{}");

        await Assert.ThrowsAsync<WorkClaimFileException>(() => WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory));
    }

    [Fact]
    public async Task Valid_claim_round_trips_through_the_filesystem()
    {
        using var sandbox = new TestSandbox();
        var acquired = await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, new WorkClaim
        {
            OwnerSessionId = "session-a",
            IssueNumber = 21,
            WorkType = WorkClaimType.Implementation
        });

        var read = await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory);

        Assert.True(acquired.Acquired);
        Assert.NotNull(read);
        Assert.Equal(acquired.Claim!.ClaimId, read!.ClaimId);
        Assert.Equal(acquired.Claim!.IssueNumber, read.IssueNumber);
    }

    [Fact]
    public async Task Worker_metadata_round_trips_through_the_filesystem()
    {
        using var sandbox = new TestSandbox();
        var acquired = await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, new WorkClaim
        {
            OwnerSessionId = "session-a",
            IssueNumber = 21,
            WorkType = WorkClaimType.Implementation,
            WorkerProfile = "terra",
            Model = "gpt-5-codex"
        });

        var read = await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory);

        Assert.True(acquired.Acquired);
        Assert.Equal("terra", read!.WorkerProfile);
        Assert.Equal("gpt-5-codex", read.Model);
    }

    [Fact]
    public async Task Legacy_claim_without_issue_baseline_loads_but_cannot_prove_current_pull_request()
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

        var claim = await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory);
        var issue = new Issue { Number = 4 };
        var pullRequest = new PullRequest
        {
            Number = 21,
            HeadRefName = "codex/issue-4-recovered",
            CreatedAt = DateTimeOffset.UtcNow,
            ClosingIssuesReferences = new List<ClosingIssueReference> { new() { Number = 4 } }
        };

        Assert.NotNull(claim);
        Assert.False(WorkflowService.IsCurrentClaimPullRequest(claim!, issue, pullRequest));
        Assert.NotNull(await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory));
    }
}

using System.Text.Json;
using CodexGithubRouter.Work;
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

        await Assert.ThrowsAsync<JsonException>(() => WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory));
    }

    [Fact]
    public async Task Read_rejects_null_claim_json()
    {
        using var sandbox = new TestSandbox();
        await File.WriteAllTextAsync(Path.Combine(sandbox.GitCommonDirectory, "codex-github-router.work.json"), "null");

        await Assert.ThrowsAsync<JsonException>(() => WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory));
    }

    [Fact]
    public async Task Read_rejects_claim_with_missing_required_fields()
    {
        using var sandbox = new TestSandbox();
        await File.WriteAllTextAsync(Path.Combine(sandbox.GitCommonDirectory, "codex-github-router.work.json"), "{}");

        await Assert.ThrowsAsync<JsonException>(() => WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory));
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
}

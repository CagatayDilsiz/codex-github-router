using System.Text.Json;
using CodexGithubRouter.Work;
using Xunit;

namespace CodexGithubRouter.Tests;

[Trait("Category", "Unit")]
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
}

using CodexGithubRouter.Work;
using Xunit;

namespace CodexGithubRouter.Tests;

[Trait("Category", "Integration")]
public sealed class WorkCommandTests
{
    [Fact]
    public async Task Status_returns_controlled_error_for_invalid_claim_file()
    {
        using var sandbox = new TestSandbox();
        await File.WriteAllTextAsync(Path.Combine(sandbox.GitCommonDirectory, "codex-github-router.work.json"), "{}");
        using var error = new StringWriter();
        var result = await WorkCommandHandler.HandleAsync(new[] { "status", sandbox.RepositoryDirectory }, _ => Task.FromResult<string?>(sandbox.GitCommonDirectory), error, worktreeIdResolver: _ => Task.FromResult<string?>(sandbox.MainWorktreeId));
        Assert.Equal(1, result);

        Assert.Contains("Invalid work-claim file", error.ToString());
        Assert.Contains("Repair the file or remove it", error.ToString());
    }

    [Fact]
    public void Status_format_includes_worker_metadata_when_present()
    {
        var claim = new WorkClaim
        {
            IssueNumber = 14,
            PullRequestNumber = 25,
            WorkType = WorkClaimType.ChangeRequest,
            OwnerSessionId = "session-a",
            WorkerProfile = "terra",
            Model = "gpt-5-codex",
            ClaimedAt = DateTimeOffset.UtcNow,
            LastUpdatedAt = DateTimeOffset.UtcNow
        };

        var status = WorkCommandHandler.FormatClaimStatus(claim);

        Assert.Contains("worker terra", status, StringComparison.Ordinal);
        Assert.Contains("model gpt-5-codex", status, StringComparison.Ordinal);
    }
}

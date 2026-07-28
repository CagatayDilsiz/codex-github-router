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
        var result = await WorkCommandHandler.HandleAsync(new[] { "status", sandbox.RepositoryDirectory }, _ => Task.FromResult<string?>(sandbox.GitCommonDirectory), error);
        Assert.Equal(1, result);

        Assert.Contains("Invalid work-claim file", error.ToString());
        Assert.Contains("Repair the file or remove it", error.ToString());
    }
}

using CodexGithubRouter.Autonomous;
using Xunit;

namespace CodexGithubRouter.Tests;

[Trait("Category", "Integration")]
public sealed class AutonomousSandboxTests
{
    [Fact]
    public async Task Enable_provisions_required_labels_through_fake_boundary()
    {
        using var sandbox = new TestSandbox();
        var fake = new FakeAutonomousBoundary(sandbox.GitCommonDirectory);

        var result = await AutonomousService.EnableAutonomousAsync(sandbox.RepositoryDirectory, sandbox.Paths, fake);

        Assert.Equal(fake.CreatedLabels.Count, result.CreatedLabelCount);
        Assert.True(result.CreatedLabelCount >= 2);
        Assert.Contains("codex:gate", fake.CreatedLabels);
        Assert.Contains("codex:ready", fake.CreatedLabels);
        Assert.True(File.Exists(Path.Combine(sandbox.GitCommonDirectory, "codex-github-router.auto")));
        Assert.Equal(1, fake.GetLabelsCalls);
    }

    private sealed class FakeAutonomousBoundary : IAutonomousBoundary
    {
        private readonly string commonDirectory;

        public FakeAutonomousBoundary(string commonDirectory) => this.commonDirectory = commonDirectory;

        public HashSet<string> CreatedLabels { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int GetLabelsCalls { get; private set; }

        public Task<string?> GetGitCommonDirectoryAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(commonDirectory);

        public Task<HashSet<string>> GetRepositoryLabelNamesAsync(string workingDirectory, CancellationToken cancellationToken = default)
        {
            GetLabelsCalls++;
            return Task.FromResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        public Task CreateLabelAsync(string workingDirectory, string labelName, CancellationToken cancellationToken = default)
        {
            CreatedLabels.Add(labelName);
            return Task.CompletedTask;
        }
    }
}

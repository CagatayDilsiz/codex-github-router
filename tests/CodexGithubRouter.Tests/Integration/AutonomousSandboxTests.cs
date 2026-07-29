using CodexGithubRouter.Autonomous;
using CodexGithubRouter.Configurations;
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
        Assert.False(File.Exists(sandbox.Paths.WorkflowFile));
        Assert.Equal(1, fake.GetLabelsCalls);
    }

    [Fact]
    public async Task Repeated_enable_reuses_labels_and_reports_unchanged_configuration()
    {
        using var sandbox = new TestSandbox();
        var fake = new FakeAutonomousBoundary(sandbox.GitCommonDirectory);

        var first = await AutonomousService.EnableAutonomousAsync(sandbox.RepositoryDirectory, sandbox.Paths, fake);
        var second = await AutonomousService.EnableAutonomousAsync(sandbox.RepositoryDirectory, sandbox.Paths, fake);

        Assert.False(first.ConfigurationChanged);
        Assert.False(second.ConfigurationChanged);
        Assert.Equal(fake.CreatedLabels.Count, first.CreatedLabelCount);
        Assert.Equal(0, second.CreatedLabelCount);
    }

    [Fact]
    public async Task Disable_removes_marker_and_state_and_status_uses_fake_boundary()
    {
        using var sandbox = new TestSandbox();
        var fake = new FakeAutonomousBoundary(sandbox.GitCommonDirectory);

        await AutonomousService.EnableAutonomousAsync(sandbox.RepositoryDirectory, sandbox.Paths, fake);
        Assert.True(await AutonomousService.IsAutonomousAsync(sandbox.RepositoryDirectory, fake));
        await AutonomousService.DisableAutonomousAsync(sandbox.RepositoryDirectory, fake);

        Assert.False(await AutonomousService.GetAutonomousStatusAsync(sandbox.RepositoryDirectory, fake));
        Assert.False(File.Exists(Path.Combine(sandbox.GitCommonDirectory, "codex-github-router.auto.json")));
    }

    [Fact]
    public async Task Enable_reports_changed_configuration_when_workflow_changes()
    {
        using var sandbox = new TestSandbox();
        var fake = new FakeAutonomousBoundary(sandbox.GitCommonDirectory);

        await WorkflowConfigurationService.WriteDefaultAsync(sandbox.Paths.WorkflowFile);
        await AutonomousService.EnableAutonomousAsync(sandbox.RepositoryDirectory, sandbox.Paths, fake);
        var workflow = await File.ReadAllTextAsync(sandbox.Paths.WorkflowFile);
        await File.WriteAllTextAsync(sandbox.Paths.WorkflowFile, workflow.Replace("codex:gate", "codex:critical", StringComparison.Ordinal));

        var changed = await AutonomousService.EnableAutonomousAsync(sandbox.RepositoryDirectory, sandbox.Paths, fake);

        Assert.True(changed.ConfigurationChanged);
        Assert.Contains("codex:critical", fake.CreatedLabels);
    }

    [Fact]
    public async Task Enable_uses_repository_override_for_labels_and_fingerprint()
    {
        using var sandbox = new TestSandbox();
        var fake = new FakeAutonomousBoundary(sandbox.GitCommonDirectory);
        var overridePath = Path.Combine(sandbox.RepositoryDirectory, ".codex-github-router", "workflow.json");
        Directory.CreateDirectory(Path.GetDirectoryName(overridePath)!);
        await File.WriteAllTextAsync(overridePath, """
            {
              "states": {
                "ready": [
                  { "type": "label", "values": ["project:ready"] }
                ]
              }
            }
            """);

        var first = await AutonomousService.EnableAutonomousAsync(sandbox.RepositoryDirectory, sandbox.Paths, fake);

        Assert.False(first.ConfigurationChanged);
        Assert.Contains("project:ready", fake.CreatedLabels);
        Assert.DoesNotContain("codex:ready", fake.CreatedLabels);

        await File.WriteAllTextAsync(overridePath, """
            {
              "states": {
                "ready": [
                  { "type": "label", "values": ["project:ready-v2"] }
                ]
              }
            }
            """);

        var second = await AutonomousService.EnableAutonomousAsync(sandbox.RepositoryDirectory, sandbox.Paths, fake);

        Assert.True(second.ConfigurationChanged);
        Assert.Contains("project:ready-v2", fake.CreatedLabels);
    }

    private sealed class FakeAutonomousBoundary : IAutonomousBoundary
    {
        private readonly string commonDirectory;

        public FakeAutonomousBoundary(string commonDirectory) => this.commonDirectory = commonDirectory;

        public HashSet<string> CreatedLabels { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int GetLabelsCalls { get; private set; }

        public Task<string?> GetGitCommonDirectoryAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(commonDirectory);

        public Task<string?> GetRepositoryRootAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(workingDirectory);

        public Task<HashSet<string>> GetRepositoryLabelNamesAsync(string workingDirectory, CancellationToken cancellationToken = default)
        {
            GetLabelsCalls++;
            return Task.FromResult(new HashSet<string>(CreatedLabels, StringComparer.OrdinalIgnoreCase));
        }

        public Task CreateLabelAsync(string workingDirectory, string labelName, CancellationToken cancellationToken = default)
        {
            CreatedLabels.Add(labelName);
            return Task.CompletedTask;
        }
    }
}

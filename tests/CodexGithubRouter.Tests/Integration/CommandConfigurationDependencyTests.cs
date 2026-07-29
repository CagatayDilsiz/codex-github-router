using CodexGithubRouter.GitHub;
using Xunit;

namespace CodexGithubRouter.Tests;

[Trait("Category", "Integration")]
public sealed class CommandConfigurationDependencyTests
{
    [Fact]
    public async Task Plain_issue_list_does_not_load_configuration()
    {
        using var sandbox = new TestSandbox();
        var configurationCalls = 0;

        var result = await IssuesCommandHandler.HandleAsync(new[] { "list", sandbox.RepositoryDirectory }, new IssueCommandDependencies
        {
            ResolveGitCommonDirectoryAsync = _ => Task.FromResult<string?>(sandbox.GitCommonDirectory),
            LoadConfigurationAsync = _ =>
            {
                configurationCalls++;
                throw new InvalidOperationException("Plain issue list should not load configuration.");
            },
            GetIssuesAsync = (_, _) => Task.FromResult(new List<Issue>())
        });

        Assert.Equal(0, result);
        Assert.Equal(0, configurationCalls);
        Assert.False(File.Exists(sandbox.Paths.WorkflowFile));
    }

    [Fact]
    public async Task Plain_pull_request_list_does_not_load_configuration()
    {
        using var sandbox = new TestSandbox();
        var configurationCalls = 0;

        var result = await PullRequestCommandHandler.HandleAsync(new[] { "list", sandbox.RepositoryDirectory }, new PullRequestCommandDependencies
        {
            ResolveGitCommonDirectoryAsync = _ => Task.FromResult<string?>(sandbox.GitCommonDirectory),
            LoadConfigurationAsync = _ =>
            {
                configurationCalls++;
                throw new InvalidOperationException("Plain pull-request list should not load configuration.");
            },
            GetPullRequestsAsync = (_, _) => Task.FromResult(new List<PullRequest>())
        });

        Assert.Equal(0, result);
        Assert.Equal(0, configurationCalls);
        Assert.False(File.Exists(sandbox.Paths.WorkflowFile));
    }
}

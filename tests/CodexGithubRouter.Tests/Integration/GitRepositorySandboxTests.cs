using CodexGithubRouter.Git;
using CodexGithubRouter.Helpers;
using Xunit;

namespace CodexGithubRouter.Tests;

[Trait("Category", "Integration")]
public sealed class GitRepositorySandboxTests
{
    [Fact]
    public async Task Worktree_and_main_checkout_resolve_the_same_git_common_directory()
    {
        using var sandbox = new TestSandbox();
        var filePath = Path.Combine(sandbox.RepositoryDirectory, "README.txt");
        await File.WriteAllTextAsync(filePath, "sandbox");
        Assert.Equal(0, (await ProcessRunner.RunAsync(sandbox.RepositoryDirectory, "git", new[] { "init", "-q" })).ExitCode);
        Assert.Equal(0, (await ProcessRunner.RunAsync(sandbox.RepositoryDirectory, "git", new[] { "add", "README.txt" })).ExitCode);
        Assert.Equal(0, (await ProcessRunner.RunAsync(sandbox.RepositoryDirectory, "git", new[] { "-c", "user.name=CGR Tests", "-c", "user.email=cgr-tests@example.invalid", "commit", "-qm", "initial" })).ExitCode);

        var worktree = Path.Combine(sandbox.Root, "worktree");
        try
        {
            var addWorktree = await ProcessRunner.RunAsync(sandbox.RepositoryDirectory, "git", new[] { "worktree", "add", "-q", worktree, "HEAD" });
            Assert.Equal(0, addWorktree.ExitCode);

            var mainCommonDirectory = await GitRepositoryService.GetCommonDirectoryAsync(sandbox.RepositoryDirectory);
            var worktreeCommonDirectory = await GitRepositoryService.GetCommonDirectoryAsync(worktree);

            Assert.NotNull(mainCommonDirectory);
            Assert.Equal(mainCommonDirectory, worktreeCommonDirectory);
            Assert.Equal(Path.GetFullPath(sandbox.RepositoryDirectory), await GitRepositoryService.GetRepositoryRootAsync(sandbox.RepositoryDirectory));
            Assert.Equal(Path.GetFullPath(worktree), await GitRepositoryService.GetRepositoryRootAsync(worktree));
        }
        finally
        {
            await ProcessRunner.RunAsync(sandbox.RepositoryDirectory, "git", new[] { "worktree", "remove", "--force", worktree });
            foreach (var entry in Directory.EnumerateFileSystemEntries(sandbox.Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(entry, FileAttributes.Normal);
            }
        }
    }
}

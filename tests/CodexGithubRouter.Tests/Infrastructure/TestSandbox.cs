using CodexGithubRouter.Configurations;

namespace CodexGithubRouter.Tests;

public sealed class TestSandbox : IDisposable
{
    public TestSandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "codex-github-router-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Paths = new ConfigurationPathSet(Root);
        RepositoryDirectory = Path.Combine(Root, "repository");
        GitCommonDirectory = Path.Combine(Root, "git-common");
        Directory.CreateDirectory(RepositoryDirectory);
        Directory.CreateDirectory(GitCommonDirectory);
        MainWorktreeId = GitCommonDirectory;
    }

    public string Root { get; }
    public ConfigurationPathSet Paths { get; }
    public string RepositoryDirectory { get; }
    public string GitCommonDirectory { get; }

    /// <summary>
    /// The main worktree's git-dir, which for the main worktree is identical to the
    /// Git common directory, used as its worktree identity.
    /// </summary>
    public string MainWorktreeId { get; }

    public string CreateLinkedWorktree(string name)
    {
        var gitDir = Path.Combine(GitCommonDirectory, "worktrees", name);
        Directory.CreateDirectory(gitDir);
        return gitDir;
    }

    public void Dispose()
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch when (!Directory.Exists(Root))
        {
        }
    }
}

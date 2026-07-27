namespace CodexGithubRouter.GitHub;

public sealed class GitHubItemNotFoundException : InvalidOperationException
{
    public GitHubItemNotFoundException(string message) : base(message)
    {
    }
}

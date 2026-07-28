namespace CodexGithubRouter.Configurations;

public sealed class ConfigurationPathSet
{
    public ConfigurationPathSet(string userProfileRoot)
    {
        if (string.IsNullOrWhiteSpace(userProfileRoot))
        {
            throw new ArgumentException("A user-profile root is required.", nameof(userProfileRoot));
        }

        UserProfileRoot = Path.GetFullPath(userProfileRoot);
    }

    public string UserProfileRoot { get; }
    public string ConfigurationDirectory => Path.Combine(UserProfileRoot, ".codex-github-router");
    public string CodexDirectory => Path.Combine(UserProfileRoot, ".codex");
    public string WorkflowFile => Path.Combine(ConfigurationDirectory, "workflow.json");
    public string CodexHooksFile => Path.Combine(CodexDirectory, "hooks.json");
}

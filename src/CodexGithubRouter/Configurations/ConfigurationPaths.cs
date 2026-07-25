namespace CodexGithubRouter.Configurations;

public static class ConfigurationPaths
{
    public static string ConfigurationDirectory
    {
        get
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex-github-router");
        }
    }

    public static string CodexDirectory
    {
        get
        {
             return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");
        }
    }

    public static string WorkflowFile
    {
        get
        {
            return Path.Combine(ConfigurationDirectory, "workflow.json");
        }
    }

    public static string CodexHooksFile
    {
        get
        {
            return Path.Combine(CodexDirectory, "hooks.json");
        }
    }
}
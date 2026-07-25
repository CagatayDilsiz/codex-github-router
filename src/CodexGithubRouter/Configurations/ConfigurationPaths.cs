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

    public static string WorkflowFile
    {
        get
        {
            return Path.Combine(ConfigurationDirectory, "workflow.json");
        }
    }
}
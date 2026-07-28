namespace CodexGithubRouter.Configurations;

public static class ConfigurationPaths
{
    public static ConfigurationPathSet Default => new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    public static string ConfigurationDirectory => Default.ConfigurationDirectory;
    public static string CodexDirectory => Default.CodexDirectory;
    public static string WorkflowFile => Default.WorkflowFile;
    public static string CodexHooksFile => Default.CodexHooksFile;
}

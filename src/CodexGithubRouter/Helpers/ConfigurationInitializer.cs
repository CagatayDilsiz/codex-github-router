using CodexGithubRouter.Configurations;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Helpers;

public static class ConfigurationInitializer
{
    public static async Task<int> InitAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var codexGithubRouterDir = Path.Combine(userHome, ".codex-github-router");
        Directory.CreateDirectory(codexGithubRouterDir);

        // check if workflow.json exists, if not create it with default content.
        var workflowFilePath = Path.Combine(codexGithubRouterDir, "workflow.json");
     
        var force = args.Contains("--force");   
        if (!File.Exists(workflowFilePath) || force)
        {
            var defaultWorkflow = new RouterConfiguration();
            var jsonContent = System.Text.Json.JsonSerializer.Serialize(defaultWorkflow, WorkflowJson.Options);
            await File.WriteAllTextAsync(workflowFilePath, jsonContent, cancellationToken);
        }
        return 0;
    }
}
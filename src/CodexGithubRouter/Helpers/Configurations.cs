using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Helpers;

public static class Configurations
{
    public static async Task<int> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var codexGithubRouterDir = Path.Combine(userHome, ".codex-github-router");
        Directory.CreateDirectory(codexGithubRouterDir);

        // check if workflow.json exists, if not create it with default content.
        var workflowFilePath = Path.Combine(codexGithubRouterDir, "workflow.json");

        if (!File.Exists(workflowFilePath))
        {
            var defaultWorkflow = new RouterConfiguration().States;
            var jsonContent = System.Text.Json.JsonSerializer.Serialize(defaultWorkflow, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(workflowFilePath, jsonContent, cancellationToken);
        }
        return 0;
    }
}
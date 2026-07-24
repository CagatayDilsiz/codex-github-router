namespace CodexGithubRouter.Helpers;

public static class Startup
{
    public static async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var codexGithubRouterDir = Path.Combine(userHome, ".codex-github-router");
        Directory.CreateDirectory(codexGithubRouterDir);

        // check if triggers.json exists, if not create it with default content.
        var triggerFilePath = Path.Combine(codexGithubRouterDir, "triggers.json");

        if (!File.Exists(triggerFilePath))
        {
            var defaultTriggers = new Triggers();
            var jsonContent = System.Text.Json.JsonSerializer.Serialize(defaultTriggers, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(triggerFilePath, jsonContent, cancellationToken);
        }
    }
}
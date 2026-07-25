using System.Text.Json;
using System.Text.Json.Nodes;
using CodexGithubRouter.Configurations;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Helpers;

public static class ConfigurationInitializer
{
    public static async Task<int> InitAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var force = args.Any(argument => string.Equals(argument, "--force", StringComparison.OrdinalIgnoreCase));
        
        if (await SetupCodexHooksAsync(force, cancellationToken) != 0)
        {
            return 1;
        }

        await SetupDefaultConfigurationAsync(force, cancellationToken);

        return 0;
    }

    private static async Task SetupDefaultConfigurationAsync(bool force, CancellationToken cancellationToken = default)
    {
        var path = ConfigurationPaths.WorkflowFile;

        if (File.Exists(path) && !force)
        {
            Console.WriteLine($"Configuration already exists: {path}");

            return;
        }

        await WorkflowConfigurationService.WriteDefaultAsync(path, cancellationToken);

        Console.WriteLine($"Default configuration written: {path}");
    }

    private static async Task<int> SetupCodexHooksAsync(bool force, CancellationToken cancellationToken = default)
    {
        var path = ConfigurationPaths.CodexDirectory;
        
        if (!Directory.Exists(path))
        {
           Console.Error.WriteLine($"Codex directory does not exist: {path}");
           return 1;
        }

        var hooksFilePath = ConfigurationPaths.CodexHooksFile;

        if (File.Exists(hooksFilePath))
        {
            await AppendToCodexHooksAsync(hooksFilePath, cancellationToken);
            Console.WriteLine($"Codex hooks configuration updated: {hooksFilePath}");
            return 0;
        }
        else
        {
            var hooksConfig = new JsonObject
            {
                ["description"] = "Codex hooks configuration",
                ["hooks"] = new JsonObject
                {
                    ["UserPromptSubmit"] = new JsonArray
                    {
                        GetUserPromptSubmitHookConfiguration()
                    }
                }
            };

            await File.WriteAllTextAsync(hooksFilePath, hooksConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);

            Console.WriteLine($"Codex hooks configuration created: {hooksFilePath}");
            return 0;
        }       
    }

    private static async Task AppendToCodexHooksAsync(string path, CancellationToken cancellationToken = default)
    {
       try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);

            var root = JsonNode.Parse(json);

            if (root is null)
            {                
                throw new InvalidOperationException("Codex hooks configuration is empty.");
            }

            if (root["hooks"] is null) 
            {
                throw new InvalidOperationException("Codex hooks configuration does not contain a 'hooks' array.");
            }

            var rootHooks = root["hooks"] as JsonObject;

            if (rootHooks is null)
            {
                throw new InvalidOperationException("Codex hooks configuration 'hooks' is not a valid JSON object.");
            }

            if (rootHooks["UserPromptSubmit"] is null)
            {
                rootHooks["UserPromptSubmit"] = new JsonArray() {
                    GetUserPromptSubmitHookConfiguration()
                };

                
                await File.WriteAllTextAsync(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            }
            else
            {
                // find if the command already exists
                var existingCommands = rootHooks["UserPromptSubmit"] as JsonArray;

                if (existingCommands is null)
                {
                    throw new InvalidOperationException("Codex hooks configuration 'UserPromptSubmit' is not a valid JSON array.");
                }

                var commandExists = existingCommands.Any(command =>
                    command is JsonObject obj &&
                    obj["type"]?.ToString() == "command" &&
                    obj["command"]?.ToString() == "cgr hook");

                if (!commandExists)
                {
                    existingCommands.Add(GetUserPromptSubmitHookConfiguration());

                    await File.WriteAllTextAsync(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
                }
            }

        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error updating Codex hooks configuration: {ex.Message}");
        }        
    }

    public static JsonObject GetUserPromptSubmitHookConfiguration()
    {
        var hookConfig = new JsonObject
        {
            ["type"] = "command",
            ["command"] = "cgr hook",
            ["commandWindow"] = "cgr hook",
            ["timeout"] = 120,
            ["statusMessage"] = "Running cgr hook"
        };

        return hookConfig;
    }
}
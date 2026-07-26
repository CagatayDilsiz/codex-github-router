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
            Directory.CreateDirectory(path);
        }

        var hooksFilePath = ConfigurationPaths.CodexHooksFile;

        if (File.Exists(hooksFilePath))
        {
            var result = await AppendToCodexHooksAsync(force, hooksFilePath, cancellationToken);          
            return result;
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
                       GetUserPromptSubmitHookGroup()
                    }
                }
            };

            await WriteJsonAtomicallyAsync(hooksFilePath, hooksConfig, cancellationToken);
            Console.WriteLine($"Codex hooks configuration created: {hooksFilePath}");
            return 0;
        }
    }

    private static async Task<int> AppendToCodexHooksAsync(bool force, string path, CancellationToken cancellationToken = default)
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
                rootHooks["UserPromptSubmit"] = new JsonArray()
                {
                    GetUserPromptSubmitHookGroup()
                };

                await WriteJsonAtomicallyAsync(path, root, cancellationToken);

                return 0;
            }
            else
            {
                var userPrompt = rootHooks["UserPromptSubmit"] as JsonArray;
                if (userPrompt is null)
                {
                    throw new InvalidOperationException("Codex hooks configuration 'UserPromptSubmit' is not a valid JSON array.");
                }

                var isUserPromptHookExists = ContainsCgrCommandBlock(userPrompt);

                if (isUserPromptHookExists)
                {
                    if (force)
                    {
                        RemoveAllCgrCommandBlocks(userPrompt);
                        userPrompt.Add(GetUserPromptSubmitHookGroup());
                        await WriteJsonAtomicallyAsync(path, root, cancellationToken);

                        return 0;
                    }
                    else
                    {
                        Console.WriteLine($"Codex hooks configuration already contains the 'cgr hook' command. Use --force to overwrite.");
                        return 0;
                    }
                }
                else
                {
                    userPrompt.Add(GetUserPromptSubmitHookGroup());

                    await WriteJsonAtomicallyAsync(path, root, cancellationToken);

                    return 0;
                }
            }

        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error updating Codex hooks configuration: {ex.Message}");
            return 1;
        }
    }

    private static JsonObject GetUserPromptSubmitHookGroup()
    {
        return new JsonObject
        {
            ["hooks"] = new JsonArray
            {
                GetCgrCommandBlock()
            }
        };
    }

    public static JsonObject GetCgrCommandBlock()
    {

        return new JsonObject
        {
            ["type"] = "command",
            ["command"] = "cgr hook",
            ["commandWindows"] = "cgr hook",
            ["timeout"] = 120,
            ["statusMessage"] = "Running cgr hook"
        };
    }

    private static bool ContainsCgrCommandBlock(JsonArray groups)
    {
        return groups
            .OfType<JsonObject>()
            .Select(group => group["hooks"] as JsonArray)
            .Where(handlers => handlers is not null)
            .SelectMany(handlers => handlers!.OfType<JsonObject>())
            .Any(handler =>
                string.Equals(handler["type"]?.GetValue<string>(), "command", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(handler["command"]?.GetValue<string>(), "cgr hook", StringComparison.OrdinalIgnoreCase));
    }

    private static JsonObject? GetExistingCgrCommandBlock(JsonArray groups, out JsonObject? existingCgrHook)
    {
        existingCgrHook = groups.FirstOrDefault(handler => handler is not null &&
                string.Equals(handler["type"]?.GetValue<string>(), "command", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(handler["command"]?.GetValue<string>(), "cgr hook", StringComparison.OrdinalIgnoreCase)) as JsonObject;

        return existingCgrHook;
    }

    private static IEnumerable<JsonArray> GetUserPromptHookGroup(JsonArray userPrompt)
    {
        return userPrompt
            .OfType<JsonObject>()
            .Select(group => group["hooks"] as JsonArray)
            .Where(handlers => handlers is not null)
            .Select(handlers => handlers!);
    }

    private static async Task WriteJsonAtomicallyAsync(string path, JsonNode root, CancellationToken cancellationToken)
    {
        var tempPath = path + ".tmp";
        var backupPath = path + ".bak";

        var json = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(tempPath, json, cancellationToken);

        _ = JsonNode.Parse(await File.ReadAllTextAsync(tempPath, cancellationToken))
            ?? throw new InvalidOperationException("Generated hooks configuration is invalid.");

        if (File.Exists(path))
        {
            File.Copy(path, backupPath, overwrite: true);
            File.Move(tempPath, path, overwrite: true);
        }
        else
        {
            File.Move(tempPath, path);
        }
    }

    private static void RemoveAllCgrCommandBlocks(JsonArray groups)
    {
        foreach (var group in groups.OfType<JsonObject>())
        {
            if (group["hooks"] is not JsonArray hooks)
            {
                continue;
            }

            var existingCgrHooks = hooks
                .OfType<JsonObject>()
                .Where(IsCgrCommandBlock)
                .ToList();

            foreach (var hook in existingCgrHooks)
            {
                hooks.Remove(hook);
            }
        }
    }

    private static bool IsCgrCommandBlock(JsonObject hook)
    {
        return
            string.Equals(
                hook["type"]?.GetValue<string>(),
                "command",
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                hook["command"]?.GetValue<string>(),
                "cgr hook",
                StringComparison.OrdinalIgnoreCase);
    }
}
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexGithubRouter.Configurations;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Helpers;

public static class ConfigurationInitializer
{
    public static async Task<int> InitAsync(string[] args, CancellationToken cancellationToken = default)
        => await InitAsync(args, ConfigurationPaths.Default, cancellationToken);

    public static async Task<int> InitAsync(string[] args, ConfigurationPathSet paths, CancellationToken cancellationToken = default)
    {
        var force = args.Any(argument => string.Equals(argument, "--force", StringComparison.OrdinalIgnoreCase));

        if (await SetupCodexHooksAsync(force, paths, cancellationToken) != 0)
        {
            return 1;
        }

        await SetupDefaultConfigurationAsync(force, paths, cancellationToken);

        return 0;
    }

    public static Task<int> UninstallHookAsync(string[] args, CancellationToken cancellationToken = default)
        => UninstallHookAsync(args, ConfigurationPaths.Default, cancellationToken);

    public static async Task<int> UninstallHookAsync(string[] args, ConfigurationPathSet paths, CancellationToken cancellationToken = default)
    {
        var hooksFilePath = paths.CodexHooksFile;

        if (!File.Exists(hooksFilePath))
        {
            Console.WriteLine($"No Codex hooks file found at: {hooksFilePath}");
            return 0;
        }

        try
        {
            var json = await File.ReadAllTextAsync(hooksFilePath, cancellationToken);

            var root = JsonNode.Parse(json);

            if (root is null)
            {
                throw new InvalidOperationException("Codex hooks configuration is empty.");
            }

            if (root["hooks"] is not JsonObject rootHooks)
            {
                throw new InvalidOperationException("Codex hooks configuration does not contain a valid 'hooks' object.");
            }

            if (rootHooks["UserPromptSubmit"] is not JsonArray userPrompt)
            {
                Console.WriteLine("No 'UserPromptSubmit' hook group found. Nothing to uninstall.");
                return 0;
            }

            var removedCount = RemoveAllCgrCommandBlocks(userPrompt);

            if (removedCount == 0)
            {
                Console.WriteLine("No CGR hook entries found. Nothing to uninstall.");
                return 0;
            }

            await WriteJsonAtomicallyAsync(hooksFilePath, root, cancellationToken);

            var entryWord = removedCount == 1 ? "entry" : "entries";
            Console.WriteLine($"Removed {removedCount} CGR hook {entryWord} from: {hooksFilePath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error uninstalling CGR hooks: {ex.Message}");
            return 1;
        }
    }

    private static async Task SetupDefaultConfigurationAsync(bool force, ConfigurationPathSet paths, CancellationToken cancellationToken = default)
    {
        var path = paths.WorkflowFile;

        if (File.Exists(path) && !force)
        {
            Console.WriteLine($"Configuration already exists: {path}");
            return;
        }

        await WorkflowConfigurationService.WriteDefaultAsync(path, cancellationToken);

        Console.WriteLine($"Default configuration written: {path}");
    }

    private static async Task<int> SetupCodexHooksAsync(bool force, ConfigurationPathSet paths, CancellationToken cancellationToken = default)
    {
        var path = paths.CodexDirectory;

        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        var hooksFilePath = paths.CodexHooksFile;

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

                Console.WriteLine($"Codex hooks configuration updated with 'cgr hook' command: {path}");

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

                        Console.WriteLine($"Codex hooks configuration updated with 'cgr hook' command: {path}");
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
                    Console.WriteLine($"Codex hooks configuration updated with 'cgr hook' command: {path}");
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

    private static JsonObject GetCgrCommandBlock()
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
            .Any(IsCgrCommandBlock);
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

    private static int RemoveAllCgrCommandBlocks(JsonArray groups)
    {
        var emptyGroups = new List<JsonObject>();
        var totalRemoved = 0;

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
                totalRemoved++;
            }

            if (hooks.Count == 0)
            {
                emptyGroups.Add(group);
            }
        }

        foreach (var emptyGroup in emptyGroups)
        {
            groups.Remove(emptyGroup);
        }

        return totalRemoved;
    }

    private static bool IsCgrCommandBlock(JsonObject hook)
    {
        if (!string.Equals(hook["type"]?.GetValue<string>(), "command", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var commandIsCgr = string.Equals(hook["command"]?.GetValue<string>(), "cgr hook", StringComparison.OrdinalIgnoreCase);
        var winIsCgr = string.Equals(hook["commandWindows"]?.GetValue<string>(), "cgr hook", StringComparison.OrdinalIgnoreCase);
        var commandExists = hook["command"] is not null;
        var winExists = hook["commandWindows"] is not null;

        if (commandExists && winExists)
        {
            return commandIsCgr && winIsCgr;
        }

        return commandIsCgr || winIsCgr;
    }
}

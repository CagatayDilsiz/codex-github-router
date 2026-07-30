using CodexGithubRouter.Configurations;
using CodexGithubRouter.Helpers;
using System.Text.Json.Nodes;
using Xunit;

namespace CodexGithubRouter.Tests;

[Trait("Category", "Integration")]
public sealed class ConfigurationSandboxTests
{
    [Fact]
    public async Task Configuration_and_hook_initialization_use_only_the_explicit_sandbox()
    {
        using var sandbox = new TestSandbox();

        var result = await ConfigurationInitializer.InitAsync(new[] { "--force" }, sandbox.Paths);

        Assert.Equal(0, result);
        Assert.True(File.Exists(sandbox.Paths.WorkflowFile));
        Assert.True(File.Exists(sandbox.Paths.CodexHooksFile));
        Assert.StartsWith(sandbox.Root, sandbox.Paths.WorkflowFile, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(sandbox.Root, sandbox.Paths.CodexHooksFile, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Default_configuration_creation_is_safe_under_concurrency()
    {
        using var sandbox = new TestSandbox();

        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => WorkflowConfigurationService.WriteDefaultAsync(sandbox.Paths.WorkflowFile)));

        var loaded = await WorkflowConfigurationService.LoadAsync(sandbox.Paths.WorkflowFile);
        Assert.Contains("codex:gate", loaded.Policies.RepositoryGate.Labels);
    }

    [Fact]
    public async Task Invalid_json_and_unsupported_versions_are_reported_without_fallback_creation()
    {
        using var sandbox = new TestSandbox();
        var invalidPath = Path.Combine(sandbox.Root, "invalid.json");
        await File.WriteAllTextAsync(invalidPath, "{ invalid json");

        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowConfigurationService.LoadAsync(invalidPath));

        var unsupportedPath = Path.Combine(sandbox.Root, "unsupported.json");
        await File.WriteAllTextAsync(unsupportedPath, "{\"version\":2}");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowConfigurationService.LoadAsync(unsupportedPath));
        Assert.Contains("Unsupported workflow configuration version", exception.Message);
    }

    [Fact]
    public async Task Hook_initialization_is_idempotent_and_preserves_unrelated_hooks()
    {
        using var sandbox = new TestSandbox();
        var hooks = """
            {
              "description": "existing",
              "hooks": {
                "SessionStart": [{ "hooks": [{ "type": "command", "command": "echo keep" }] }]
              }
            }
            """;
        Directory.CreateDirectory(sandbox.Paths.CodexDirectory);
        await File.WriteAllTextAsync(sandbox.Paths.CodexHooksFile, hooks);

        Assert.Equal(0, await ConfigurationInitializer.InitAsync(Array.Empty<string>(), sandbox.Paths));
        var first = await File.ReadAllTextAsync(sandbox.Paths.CodexHooksFile);
        Assert.Contains("echo keep", first);
        Assert.Contains("cgr hook", first);

        Assert.Equal(0, await ConfigurationInitializer.InitAsync(Array.Empty<string>(), sandbox.Paths));
        Assert.Equal(first, await File.ReadAllTextAsync(sandbox.Paths.CodexHooksFile));

        Assert.Equal(0, await ConfigurationInitializer.InitAsync(new[] { "--force" }, sandbox.Paths));
        Assert.True(File.Exists(sandbox.Paths.CodexHooksFile + ".bak"));
        Assert.Contains("echo keep", await File.ReadAllTextAsync(sandbox.Paths.CodexHooksFile + ".bak"));
    }

    [Fact]
    public async Task Hook_configuration_declares_native_commands_and_preserves_utf8()
    {
        using var sandbox = new TestSandbox();
        var hooks = """
            {
              "description": "Mevcut kanca 🚀",
              "hooks": {
                "SessionStart": [{ "hooks": [{ "type": "command", "command": "echo dünyâ" }] }]
              }
            }
            """;
        Directory.CreateDirectory(sandbox.Paths.CodexDirectory);
        await File.WriteAllTextAsync(sandbox.Paths.CodexHooksFile, hooks);

        Assert.Equal(0, await ConfigurationInitializer.InitAsync(Array.Empty<string>(), sandbox.Paths));

        var document = JsonNode.Parse(await File.ReadAllTextAsync(sandbox.Paths.CodexHooksFile))!.AsObject();
        var hook = document["hooks"]!["UserPromptSubmit"]![0]!["hooks"]![0]!.AsObject();
        Assert.Equal("cgr hook", hook["command"]!.GetValue<string>());
        Assert.Equal("cgr hook", hook["commandWindows"]!.GetValue<string>());
        Assert.Equal("Mevcut kanca 🚀", document["description"]!.GetValue<string>());
        var existingHook = document["hooks"]!["SessionStart"]![0]!["hooks"]![0]!["command"]!.GetValue<string>();
        Assert.Equal("echo dünyâ", existingHook);
    }

    [Fact]
    public async Task Invalid_existing_hooks_fail_without_corrupting_the_original_file()
    {
        using var sandbox = new TestSandbox();
        Directory.CreateDirectory(sandbox.Paths.CodexDirectory);
        const string invalid = "{ not valid json";
        await File.WriteAllTextAsync(sandbox.Paths.CodexHooksFile, invalid);

        Assert.Equal(1, await ConfigurationInitializer.InitAsync(new[] { "--force" }, sandbox.Paths));
        Assert.Equal(invalid, await File.ReadAllTextAsync(sandbox.Paths.CodexHooksFile));
    }

    [Fact]
    public async Task Hook_uninstall_removes_cgr_entry_and_creates_backup()
    {
        using var sandbox = new TestSandbox();

        Assert.Equal(0, await ConfigurationInitializer.InitAsync(new[] { "--force" }, sandbox.Paths));

        Assert.Equal(0, await ConfigurationInitializer.UninstallHookAsync(Array.Empty<string>(), sandbox.Paths));
        Assert.True(File.Exists(sandbox.Paths.CodexHooksFile + ".bak"));

        var document = JsonNode.Parse(await File.ReadAllTextAsync(sandbox.Paths.CodexHooksFile))!.AsObject();
        var userPrompt = document["hooks"]!["UserPromptSubmit"]!.AsArray();
        Assert.Empty(userPrompt);
    }

    [Fact]
    public async Task Hook_uninstall_preserves_unrelated_hooks()
    {
        using var sandbox = new TestSandbox();
        var hooks = """
            {
              "description": "existing",
              "hooks": {
                "UserPromptSubmit": [
                  { "hooks": [{ "type": "command", "command": "cgr hook", "timeout": 120 }] },
                  { "hooks": [{ "type": "command", "command": "echo custom" }] }
                ],
                "SessionStart": [{ "hooks": [{ "type": "command", "command": "echo keep" }] }]
              }
            }
            """;
        Directory.CreateDirectory(sandbox.Paths.CodexDirectory);
        await File.WriteAllTextAsync(sandbox.Paths.CodexHooksFile, hooks);

        Assert.Equal(0, await ConfigurationInitializer.UninstallHookAsync(Array.Empty<string>(), sandbox.Paths));

        var document = JsonNode.Parse(await File.ReadAllTextAsync(sandbox.Paths.CodexHooksFile))!.AsObject();
        var userPrompt = document["hooks"]!["UserPromptSubmit"]!.AsArray();
        Assert.Single(userPrompt);
        var remainingCommand = userPrompt[0]!["hooks"]![0]!["command"]!.GetValue<string>();
        Assert.Equal("echo custom", remainingCommand);

        var sessionStart = document["hooks"]!["SessionStart"]![0]!["hooks"]![0]!["command"]!.GetValue<string>();
        Assert.Equal("echo keep", sessionStart);
    }

    [Fact]
    public async Task Hook_uninstall_removes_multiple_stale_cgr_entries()
    {
        using var sandbox = new TestSandbox();
        var hooks = """
            {
              "description": "stale entries",
              "hooks": {
                "UserPromptSubmit": [
                  { "hooks": [{ "type": "command", "command": "cgr hook" }] },
                  { "hooks": [{ "type": "command", "command": "cgr hook", "commandWindows": "cgr hook" }] }
                ]
              }
            }
            """;
        Directory.CreateDirectory(sandbox.Paths.CodexDirectory);
        await File.WriteAllTextAsync(sandbox.Paths.CodexHooksFile, hooks);

        Assert.Equal(0, await ConfigurationInitializer.UninstallHookAsync(Array.Empty<string>(), sandbox.Paths));

        var document = JsonNode.Parse(await File.ReadAllTextAsync(sandbox.Paths.CodexHooksFile))!.AsObject();
        var userPrompt = document["hooks"]!["UserPromptSubmit"]!.AsArray();
        Assert.Empty(userPrompt);
    }

    [Fact]
    public async Task Hook_uninstall_no_cgr_entry_succeeds_idempotently()
    {
        using var sandbox = new TestSandbox();
        var hooks = """
            {
              "description": "no cgr",
              "hooks": {
                "UserPromptSubmit": [
                  { "hooks": [{ "type": "command", "command": "echo hello" }] }
                ]
              }
            }
            """;
        Directory.CreateDirectory(sandbox.Paths.CodexDirectory);
        await File.WriteAllTextAsync(sandbox.Paths.CodexHooksFile, hooks);

        Assert.Equal(0, await ConfigurationInitializer.UninstallHookAsync(Array.Empty<string>(), sandbox.Paths));
        Assert.Equal(0, await ConfigurationInitializer.UninstallHookAsync(Array.Empty<string>(), sandbox.Paths));

        var document = JsonNode.Parse(await File.ReadAllTextAsync(sandbox.Paths.CodexHooksFile))!.AsObject();
        var userPrompt = document["hooks"]!["UserPromptSubmit"]!.AsArray();
        Assert.Single(userPrompt);
    }

    [Fact]
    public async Task Hook_uninstall_no_hooks_file_succeeds()
    {
        using var sandbox = new TestSandbox();

        Assert.Equal(0, await ConfigurationInitializer.UninstallHookAsync(Array.Empty<string>(), sandbox.Paths));
    }

    [Fact]
    public async Task Hook_uninstall_invalid_json_fails_without_corrupting_original()
    {
        using var sandbox = new TestSandbox();
        Directory.CreateDirectory(sandbox.Paths.CodexDirectory);
        const string invalid = "{ not valid json";
        await File.WriteAllTextAsync(sandbox.Paths.CodexHooksFile, invalid);

        Assert.Equal(1, await ConfigurationInitializer.UninstallHookAsync(Array.Empty<string>(), sandbox.Paths));
        Assert.Equal(invalid, await File.ReadAllTextAsync(sandbox.Paths.CodexHooksFile));
    }

    [Fact]
    public async Task Hook_uninstall_recognizes_commandWindows_variant()
    {
        using var sandbox = new TestSandbox();
        var hooks = """
            {
              "description": "windows only",
              "hooks": {
                "UserPromptSubmit": [
                  { "hooks": [{ "type": "command", "commandWindows": "cgr hook", "timeout": 120 }] }
                ]
              }
            }
            """;
        Directory.CreateDirectory(sandbox.Paths.CodexDirectory);
        await File.WriteAllTextAsync(sandbox.Paths.CodexHooksFile, hooks);

        Assert.Equal(0, await ConfigurationInitializer.UninstallHookAsync(Array.Empty<string>(), sandbox.Paths));

        var document = JsonNode.Parse(await File.ReadAllTextAsync(sandbox.Paths.CodexHooksFile))!.AsObject();
        var userPrompt = document["hooks"]!["UserPromptSubmit"]!.AsArray();
        Assert.Empty(userPrompt);
    }

    [Fact]
    public async Task Hook_uninstall_preserves_conflicting_platform_hooks()
    {
        using var sandbox = new TestSandbox();
        var hooks = """
            {
              "description": "conflicting platforms",
              "hooks": {
                "UserPromptSubmit": [
                  { "hooks": [{ "type": "command", "command": "echo keep", "commandWindows": "cgr hook" }] },
                  { "hooks": [{ "type": "command", "command": "cgr hook", "commandWindows": "echo keep" }] }
                ]
              }
            }
            """;
        Directory.CreateDirectory(sandbox.Paths.CodexDirectory);
        await File.WriteAllTextAsync(sandbox.Paths.CodexHooksFile, hooks);

        Assert.Equal(0, await ConfigurationInitializer.UninstallHookAsync(Array.Empty<string>(), sandbox.Paths));

        var document = JsonNode.Parse(await File.ReadAllTextAsync(sandbox.Paths.CodexHooksFile))!.AsObject();
        var userPrompt = document["hooks"]!["UserPromptSubmit"]!.AsArray();
        Assert.Equal(2, userPrompt.Count);
    }

    [Fact]
    public async Task Hook_uninstall_recognizes_both_platform_fields()
    {
        using var sandbox = new TestSandbox();
        var hooks = """
            {
              "description": "both match",
              "hooks": {
                "UserPromptSubmit": [
                  { "hooks": [{ "type": "command", "command": "cgr hook", "commandWindows": "cgr hook" }] }
                ]
              }
            }
            """;
        Directory.CreateDirectory(sandbox.Paths.CodexDirectory);
        await File.WriteAllTextAsync(sandbox.Paths.CodexHooksFile, hooks);

        Assert.Equal(0, await ConfigurationInitializer.UninstallHookAsync(Array.Empty<string>(), sandbox.Paths));

        var document = JsonNode.Parse(await File.ReadAllTextAsync(sandbox.Paths.CodexHooksFile))!.AsObject();
        var userPrompt = document["hooks"]!["UserPromptSubmit"]!.AsArray();
        Assert.Empty(userPrompt);
    }

}

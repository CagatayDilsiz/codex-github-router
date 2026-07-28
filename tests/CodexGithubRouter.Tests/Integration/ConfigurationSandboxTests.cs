using CodexGithubRouter.Configurations;
using CodexGithubRouter.Helpers;
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
    public async Task Invalid_existing_hooks_fail_without_corrupting_the_original_file()
    {
        using var sandbox = new TestSandbox();
        Directory.CreateDirectory(sandbox.Paths.CodexDirectory);
        const string invalid = "{ not valid json";
        await File.WriteAllTextAsync(sandbox.Paths.CodexHooksFile, invalid);

        Assert.Equal(1, await ConfigurationInitializer.InitAsync(new[] { "--force" }, sandbox.Paths));
        Assert.Equal(invalid, await File.ReadAllTextAsync(sandbox.Paths.CodexHooksFile));
    }

}

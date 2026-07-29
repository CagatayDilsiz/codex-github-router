using System.Text.Json;
using CodexGithubRouter.Configurations;
using CodexGithubRouter.Helpers;
using CodexGithubRouter.Workflow;
using Xunit;

namespace CodexGithubRouter.Tests;

[Trait("Category", "Integration")]
public sealed class RepositoryConfigurationOverlayTests
{
    [Fact]
    public async Task Missing_override_preserves_global_configuration_and_does_not_create_repository_file()
    {
        using var sandbox = new TestSandbox();
        var nestedWorkingDirectory = await InitializeRepositoryAsync(sandbox);

        var effective = await WorkflowConfigurationService.LoadEffectiveAsync(nestedWorkingDirectory, sandbox.Paths);

        Assert.Contains("codex:ready", effective.States[WorkflowState.Ready].Single().Values);
        Assert.False(File.Exists(sandbox.Paths.WorkflowFile));
        Assert.False(File.Exists(GetRepositoryWorkflowPath(sandbox.RepositoryDirectory)));
    }

    [Fact]
    public async Task Override_merges_objects_replaces_arrays_and_inherits_omitted_values()
    {
        using var sandbox = new TestSandbox();
        var nestedWorkingDirectory = await InitializeRepositoryAsync(sandbox);
        await WriteGlobalConfigurationAsync(sandbox.Paths, CreateGlobalConfiguration());
        var repositoryWorkflowPath = GetRepositoryWorkflowPath(sandbox.RepositoryDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(repositoryWorkflowPath)!);
        const string overrideJson = """
            {
              "states": {
                "ready": [
                  { "type": "label", "values": ["project:ready"] }
                ]
              },
              "policies": {
                "repositoryGate": {
                  "labels": ["project:gate"]
                },
                "autonomousActivation": {
                  "prompts": ["project prompt"]
                }
              }
            }
            """;
        await File.WriteAllTextAsync(repositoryWorkflowPath, overrideJson);
        var before = await File.ReadAllTextAsync(repositoryWorkflowPath);

        var effective = await WorkflowConfigurationService.LoadEffectiveAsync(nestedWorkingDirectory, sandbox.Paths);

        Assert.Contains("project:ready", effective.States[WorkflowState.Ready].Single().Values);
        Assert.DoesNotContain("global:ready", effective.States[WorkflowState.Ready].Single().Values);
        Assert.Contains("global:working", effective.States[WorkflowState.InProgress].Single().Values);
        Assert.Equal(new[] { "project:gate" }, effective.Policies.RepositoryGate.Labels);
        Assert.Equal("prompt", effective.Policies.AutonomousActivation!.Mode);
        Assert.Equal(new[] { "project prompt" }, effective.Policies.AutonomousActivation.Prompts);
        Assert.Equal(before, await File.ReadAllTextAsync(repositoryWorkflowPath));
    }

    [Fact]
    public async Task Unknown_properties_are_ignored_without_changing_known_values()
    {
        using var sandbox = new TestSandbox();
        var nestedWorkingDirectory = await InitializeRepositoryAsync(sandbox);
        await WriteGlobalConfigurationAsync(sandbox.Paths, CreateGlobalConfiguration());
        var repositoryWorkflowPath = GetRepositoryWorkflowPath(sandbox.RepositoryDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(repositoryWorkflowPath)!);
        await File.WriteAllTextAsync(repositoryWorkflowPath, "{ \"unknownPolicy\": { \"value\": true } }");

        var effective = await WorkflowConfigurationService.LoadEffectiveAsync(nestedWorkingDirectory, sandbox.Paths);

        Assert.Contains("global:ready", effective.States[WorkflowState.Ready].Single().Values);
        Assert.Contains("global:gate", effective.Policies.RepositoryGate.Labels);
    }

    [Theory]
    [InlineData("{ \"policies\": { \"autonomousActivation\": null } }", "Explicit null")]
    [InlineData("{ invalid json", "not valid JSON")]
    [InlineData("{ \"states\": { \"ready\": [] } }", "invalid after applying")]
    public async Task Invalid_override_content_fails_with_repository_context(string overrideJson, string expectedMessage)
    {
        using var sandbox = new TestSandbox();
        var nestedWorkingDirectory = await InitializeRepositoryAsync(sandbox);
        await WriteGlobalConfigurationAsync(sandbox.Paths, CreateGlobalConfiguration());
        var repositoryWorkflowPath = GetRepositoryWorkflowPath(sandbox.RepositoryDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(repositoryWorkflowPath)!);
        await File.WriteAllTextAsync(repositoryWorkflowPath, overrideJson);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkflowConfigurationService.LoadEffectiveAsync(nestedWorkingDirectory, sandbox.Paths));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(repositoryWorkflowPath, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static RouterConfiguration CreateGlobalConfiguration()
    {
        var configuration = new RouterConfiguration
        {
            Policies = new RouterPolicies
            {
                RepositoryGate = new RepositoryGatePolicy { Labels = new List<string> { "global:gate" } },
                AutonomousActivation = new AutonomousActivationPolicy
                {
                    Mode = "prompt",
                    Prompts = new List<string> { "global prompt" }
                }
            }
        };
        configuration.States[WorkflowState.Ready] = new List<IssueMatchRule>
        {
            new() { Type = IssueMatchRuleType.Label, Values = new List<string> { "global:ready" } }
        };
        configuration.States[WorkflowState.InProgress] = new List<IssueMatchRule>
        {
            new() { Type = IssueMatchRuleType.Label, Values = new List<string> { "global:working" } }
        };
        return configuration;
    }

    private static async Task WriteGlobalConfigurationAsync(ConfigurationPathSet paths, RouterConfiguration configuration)
    {
        Directory.CreateDirectory(paths.ConfigurationDirectory);
        await File.WriteAllTextAsync(paths.WorkflowFile, JsonSerializer.Serialize(configuration, WorkflowJson.Options));
    }

    private static async Task<string> InitializeRepositoryAsync(TestSandbox sandbox)
    {
        var init = await ProcessRunner.RunAsync(sandbox.RepositoryDirectory, "git", new[] { "init", "-q" });
        Assert.Equal(0, init.ExitCode);
        var nested = Path.Combine(sandbox.RepositoryDirectory, "src", "nested");
        Directory.CreateDirectory(nested);
        return nested;
    }

    private static string GetRepositoryWorkflowPath(string repositoryRoot) =>
        Path.Combine(repositoryRoot, ".codex-github-router", "workflow.json");
}

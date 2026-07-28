using System.Text.Json;
using CodexGithubRouter.Autonomous;
using CodexGithubRouter.Configurations;
using CodexGithubRouter.Workflow;
using Xunit;

namespace CodexGithubRouter.Tests;

public sealed class AutonomousActivationTests
{
    [Fact]
    public void Missing_policy_keeps_always_on_behavior()
    {
        Assert.True(AutonomousActivationService.IsActivated(null, null));
        Assert.True(AutonomousActivationService.IsActivated(new AutonomousActivationPolicy { Mode = "always" }, "anything"));
    }

    [Theory]
    [InlineData("work on the next task", "  WORK ON\t the   next task.  ")]
    [InlineData("café", "café")]
    public void Prompt_matching_uses_normalized_ordinal_case_insensitive_full_strings(string configured, string submitted)
    {
        var policy = new AutonomousActivationPolicy
        {
            Mode = "prompt",
            Prompts = new List<string> { configured }
        };

        Assert.True(AutonomousActivationService.IsActivated(policy, submitted));
    }

    [Theory]
    [InlineData("")]
    [InlineData("work on the next task now")]
    [InlineData("work on the next task..")]
    [InlineData("prefix work on the next task")]
    public void Prompt_matching_requires_an_exact_normalized_string(string submitted)
    {
        var policy = new AutonomousActivationPolicy
        {
            Mode = "prompt",
            Prompts = new List<string> { "work on the next task" }
        };

        Assert.False(AutonomousActivationService.IsActivated(policy, submitted));
    }

    [Fact]
    public async Task Prompt_policy_requires_non_empty_prompts_and_known_mode()
    {
        using var directory = new TemporaryDirectory();

        var invalidModePath = Path.Combine(directory.Path, "invalid-mode.json");
        await File.WriteAllTextAsync(invalidModePath, JsonSerializer.Serialize(new RouterConfiguration
        {
            Policies = new RouterPolicies
            {
                AutonomousActivation = new AutonomousActivationPolicy { Mode = "sometimes" }
            }
        }, WorkflowJson.Options));

        var invalidPromptPath = Path.Combine(directory.Path, "invalid-prompt.json");
        await File.WriteAllTextAsync(invalidPromptPath, JsonSerializer.Serialize(new RouterConfiguration
        {
            Policies = new RouterPolicies
            {
                AutonomousActivation = new AutonomousActivationPolicy
                {
                    Mode = "prompt",
                    Prompts = new List<string> { "  " }
                }
            }
        }, WorkflowJson.Options));

        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowConfigurationService.LoadAsync(invalidModePath));
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowConfigurationService.LoadAsync(invalidPromptPath));
    }

    [Fact]
    public void Always_mode_ignores_prompt_values()
    {
        var policy = new AutonomousActivationPolicy
        {
            Mode = "always",
            Prompts = new List<string> { "", "   " }
        };

        AutonomousActivationService.Validate(policy);
        Assert.True(AutonomousActivationService.IsActivated(policy, null));
    }

    [Fact]
    public void Activation_policy_does_not_change_managed_label_fingerprint()
    {
        var withoutPolicy = new RouterConfiguration();
        var withPolicy = new RouterConfiguration
        {
            Policies = new RouterPolicies
            {
                AutonomousActivation = new AutonomousActivationPolicy
                {
                    Mode = "prompt",
                    Prompts = new List<string> { "work on the next task" }
                }
            }
        };

        Assert.Equal(WorkflowLabelConfiguration.GetFingerprint(withoutPolicy), WorkflowLabelConfiguration.GetFingerprint(withPolicy));
    }

    [Fact]
    public void Normalization_collapses_unicode_whitespace_and_ignores_one_trailing_period()
    {
        Assert.Equal("sıradaki görevi yapabiliriz", AutonomousActivationService.Normalize("\u00a0sıradaki\u2003görevi yapabiliriz.\t"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cgr-activation-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

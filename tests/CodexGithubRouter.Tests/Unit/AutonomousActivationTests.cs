using System.Text.Json;
using CodexGithubRouter.Autonomous;
using CodexGithubRouter.Configurations;
using CodexGithubRouter.Helpers;
using CodexGithubRouter.Hooks;
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
    public void Prompt_matching_extracts_exact_instruction_from_heartbeat_envelope()
    {
        var policy = new AutonomousActivationPolicy
        {
            Mode = "prompt",
            Prompts = new List<string> { "can we work on the next task" }
        };

        const string heartbeat = """
            <heartbeat>
              <automation_id>followapp-luna-g-rev</automation_id>
              <current_time_iso>2026-07-29T15:52:40.808Z</current_time_iso>
              <instructions>
            CAN WE WORK ON THE NEXT TASK.
              </instructions>
            </heartbeat>
            """;

        Assert.True(AutonomousActivationService.IsActivated(policy, heartbeat));
    }

    [Theory]
    [InlineData("<heartbeat><instructions>different task</instructions></heartbeat>")]
    [InlineData("<heartbeat><instructions>work on the next task</instructions><instructions>another task</instructions></heartbeat>")]
    [InlineData("<heartbeat><instructions><nested>work on the next task</nested></instructions></heartbeat>")]
    [InlineData("<heartbeat><instructions>work on the next task")]
    public void Invalid_or_mismatched_heartbeat_does_not_activate(string heartbeat)
    {
        var policy = new AutonomousActivationPolicy
        {
            Mode = "prompt",
            Prompts = new List<string> { "work on the next task" }
        };

        Assert.False(AutonomousActivationService.IsActivated(policy, heartbeat));
    }

    [Fact]
    public void Non_heartbeat_xml_is_not_treated_as_a_scheduled_task_envelope()
    {
        var policy = new AutonomousActivationPolicy
        {
            Mode = "prompt",
            Prompts = new List<string> { "work on the next task" }
        };

        Assert.False(AutonomousActivationService.IsActivated(policy, "<message>work on the next task</message>"));
    }

    [Fact]
    public void Always_mode_does_not_parse_malformed_heartbeat()
    {
        var policy = new AutonomousActivationPolicy { Mode = "always" };

        Assert.True(AutonomousActivationService.IsActivated(policy, "<heartbeat><instructions>"));
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
    public async Task Prompt_policy_rejects_normalized_duplicates_and_period_only_prompts()
    {
        using var directory = new TemporaryDirectory();
        var duplicatePath = Path.Combine(directory.Path, "duplicate.json");
        await File.WriteAllTextAsync(duplicatePath, JsonSerializer.Serialize(new RouterConfiguration
        {
            Policies = new RouterPolicies
            {
                AutonomousActivation = new AutonomousActivationPolicy
                {
                    Mode = "prompt",
                    Prompts = new List<string> { "Work on the next task", " work   on the next task. " }
                }
            }
        }, WorkflowJson.Options));

        var periodPath = Path.Combine(directory.Path, "period.json");
        await File.WriteAllTextAsync(periodPath, JsonSerializer.Serialize(new RouterConfiguration
        {
            Policies = new RouterPolicies
            {
                AutonomousActivation = new AutonomousActivationPolicy
                {
                    Mode = "prompt",
                    Prompts = new List<string> { "." }
                }
            }
        }, WorkflowJson.Options));

        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowConfigurationService.LoadAsync(duplicatePath));
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowConfigurationService.LoadAsync(periodPath));
    }

    [Fact]
    public void Matching_supports_multiple_prompts_and_ordinary_turkish_case_variation()
    {
        var policy = new AutonomousActivationPolicy
        {
            Mode = "prompt",
            Prompts = new List<string> { "first phrase", "sıradaki görev" }
        };

        Assert.True(AutonomousActivationService.IsActivated(policy, "Sıradaki görev"));
    }

    [Theory]
    [InlineData("work on the next task?")]
    [InlineData("work on the next task!")]
    [InlineData("please \"work on the next task\"")]
    public void Punctuation_and_quoted_phrases_inside_longer_text_do_not_match(string submitted)
    {
        var policy = new AutonomousActivationPolicy
        {
            Mode = "prompt",
            Prompts = new List<string> { "work on the next task" }
        };

        Assert.False(AutonomousActivationService.IsActivated(policy, submitted));
    }

    [Fact]
    public async Task Missing_configuration_uses_default_without_creating_a_file()
    {
        using var directory = new TemporaryDirectory();

        var configuration = await WorkflowConfigurationService.LoadOrDefaultAsync(directory.Paths);

        Assert.Null(configuration.Policies.AutonomousActivation);
        Assert.False(File.Exists(directory.Paths.WorkflowFile));
        Assert.Contains("Autonomous mode: enabled", AutonomousCommandHandler.FormatStatus(true, configuration.Policies.AutonomousActivation));
        Assert.Contains("Activation mode: always", AutonomousCommandHandler.FormatStatus(false, configuration.Policies.AutonomousActivation));
    }

    [Fact]
    public void Status_formats_enabled_and_disabled_prompt_policies()
    {
        var policy = new AutonomousActivationPolicy
        {
            Mode = "prompt",
            Prompts = new List<string> { "first phrase", "second phrase" }
        };

        var enabled = AutonomousCommandHandler.FormatStatus(true, policy);
        var disabled = AutonomousCommandHandler.FormatStatus(false, policy);

        Assert.Contains("Activation prompts:", enabled);
        Assert.Contains("  - first phrase", enabled);
        Assert.Contains("  - second phrase", enabled);
        Assert.Contains("Activation prompts: 2 configured", disabled);
    }

    [Fact]
    public async Task Auto_status_uses_read_only_default_without_creating_global_workflow_file()
    {
        using var directory = new TemporaryDirectory();
        var repositoryDirectory = Path.Combine(directory.Path, "repository");
        Directory.CreateDirectory(repositoryDirectory);
        var init = await ProcessRunner.RunAsync(repositoryDirectory, "git", new[] { "init", "-q" });
        Assert.Equal(0, init.ExitCode);

        var output = new StringWriter();
        var result = await AutonomousCommandHandler.HandleAsync(new[] { "status", repositoryDirectory }, new AutonomousCommandDependencies
        {
            GetStatusAsync = _ => Task.FromResult(false),
            LoadConfigurationAsync = workingDirectory => WorkflowConfigurationService.LoadEffectiveOrDefaultAsync(workingDirectory, directory.Paths),
            Output = output
        });

        Assert.Equal(0, result);
        Assert.Contains("Autonomous mode: disabled", output.ToString());
        Assert.Contains("Activation mode: always", output.ToString());
        Assert.False(File.Exists(directory.Paths.WorkflowFile));
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

        public ConfigurationPathSet Paths => new(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

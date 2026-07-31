using System.Text.Json;
using CodexGithubRouter.Configurations;
using CodexGithubRouter.Helpers;
using CodexGithubRouter.Hooks;
using CodexGithubRouter.Workflow;
using CodexGithubRouter.Work;
using Xunit;

namespace CodexGithubRouter.Tests;

[Trait("Category", "Integration")]
public sealed class HookActivationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unrelated prompt")]
    public async Task Non_matching_prompt_bypasses_another_sessions_active_claim_without_output(string? prompt)
    {
        using var sandbox = new TestSandbox();
        var claim = (await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, new WorkClaim
        {
            OwnerSessionId = "other-session",
            IssueNumber = 22,
            WorkType = WorkClaimType.Implementation
        })).Claim!;
        var configuration = new RouterConfiguration
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

        var originalIn = Console.In;
        var originalOut = Console.Out;
        var output = new StringWriter();
        var resolverCalls = 0;
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["cwd"] = sandbox.RepositoryDirectory,
                ["hook_event_name"] = "UserPromptSubmit",
                ["model"] = "test-model",
                ["session_id"] = "current-session"
            };
            if (prompt is not null)
            {
                payload["prompt"] = prompt;
            }

            Console.SetIn(new StringReader(JsonSerializer.Serialize(payload)));
            Console.SetOut(output);

            var result = await HookService.RunAsync(new HookExecutionDependencies
            {
                IsAutonomousAsync = _ => Task.FromResult(true),
                LoadConfigurationAsync = _ => Task.FromResult(configuration),
                ResolveGitCommonDirectoryAsync = _ =>
                {
                    resolverCalls++;
                    throw new InvalidOperationException("Routing boundary should not be entered.");
                }
            });

            Assert.Equal(0, result);
            // The diagnostic scope best-effort resolves the git common directory to persist
            // the bypass record; the resolver failure is swallowed and must not change output.
            Assert.Equal(1, resolverCalls);
            Assert.DoesNotContain("\"decision\"", output.ToString());
            Assert.DoesNotContain("\"hookSpecificOutput\"", output.ToString());
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }

        var unchangedClaim = await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory);
        Assert.NotNull(unchangedClaim);
        Assert.Equal(claim.ClaimId, unchangedClaim!.ClaimId);
        Assert.Equal(claim.Version, unchangedClaim.Version);
        Assert.Equal(claim.OwnerSessionId, unchangedClaim.OwnerSessionId);
    }

    [Fact]
    public async Task Matching_heartbeat_prompt_enters_the_real_hook_activation_boundary()
    {
        using var sandbox = new TestSandbox();
        var configuration = new RouterConfiguration
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

        var originalIn = Console.In;
        var originalOut = Console.Out;
        var output = new StringWriter();
        var resolverCalls = 0;
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["cwd"] = sandbox.RepositoryDirectory,
                ["hook_event_name"] = "UserPromptSubmit",
                ["model"] = "test-model",
                ["session_id"] = "current-session",
                ["prompt"] = """
                    <heartbeat>
                      <automation_id>scheduled-task</automation_id>
                      <current_time_iso>2026-07-29T15:52:40.808Z</current_time_iso>
                      <instructions>work on the next task</instructions>
                    </heartbeat>
                    """
            };

            Console.SetIn(new StringReader(JsonSerializer.Serialize(payload)));
            Console.SetOut(output);

            var result = await HookService.RunAsync(new HookExecutionDependencies
            {
                IsAutonomousAsync = _ => Task.FromResult(true),
                LoadConfigurationAsync = _ => Task.FromResult(configuration),
                ResolveGitCommonDirectoryAsync = _ =>
                {
                    resolverCalls++;
                    throw new InvalidOperationException("Activation boundary reached.");
                }
            });

            Assert.Equal(0, result);
            // The resolver runs once in the routing boundary and again for the best-effort
            // diagnostic resolution; the thrown exception is swallowed in both cases.
            Assert.Equal(2, resolverCalls);
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task Repository_override_changes_real_hook_activation_before_routing_boundaries()
    {
        using var sandbox = new TestSandbox();
        var init = await ProcessRunner.RunAsync(sandbox.RepositoryDirectory, "git", new[] { "init", "-q" });
        Assert.Equal(0, init.ExitCode);

        var overridePath = Path.Combine(sandbox.RepositoryDirectory, ".codex-github-router", "workflow.json");
        Directory.CreateDirectory(Path.GetDirectoryName(overridePath)!);
        await File.WriteAllTextAsync(overridePath, """
            {
              "policies": {
                "autonomousActivation": {
                  "mode": "prompt",
                  "prompts": ["activate repository routing"]
                }
              }
            }
            """);

        var originalIn = Console.In;
        var originalOut = Console.Out;
        var output = new StringWriter();
        var resolverCalls = 0;
        try
        {
            Console.SetIn(new StringReader("{\"cwd\":\"" + sandbox.RepositoryDirectory.Replace("\\", "\\\\", StringComparison.Ordinal) + "\",\"hook_event_name\":\"UserPromptSubmit\",\"session_id\":\"current-session\",\"prompt\":\"not the activation phrase\"}"));
            Console.SetOut(output);

            var result = await HookService.RunAsync(new HookExecutionDependencies
            {
                IsAutonomousAsync = _ => Task.FromResult(true),
                LoadConfigurationAsync = workingDirectory => WorkflowConfigurationService.LoadEffectiveAsync(workingDirectory, sandbox.Paths),
                ResolveGitCommonDirectoryAsync = _ =>
                {
                    resolverCalls++;
                    throw new InvalidOperationException("Routing boundary should not be entered.");
                }
            });

            Assert.Equal(0, result);
            // The diagnostic scope best-effort resolves the git common directory to persist
            // the bypass record; the resolver failure is swallowed and must not change output.
            Assert.Equal(1, resolverCalls);
            Assert.DoesNotContain("\"decision\"", output.ToString());
            Assert.DoesNotContain("\"hookSpecificOutput\"", output.ToString());
            Assert.False(File.Exists(sandbox.Paths.WorkflowFile));
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }
    }
}

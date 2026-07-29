using System.Text.Json;
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
            Assert.Equal(0, resolverCalls);
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
}

using System.Text.Json;
using CodexGithubRouter.Configurations;
using CodexGithubRouter.Diagnostics;
using CodexGithubRouter.Hooks;
using CodexGithubRouter.Work;
using CodexGithubRouter.Workflow;
using Xunit;

namespace CodexGithubRouter.Tests;

[Trait("Category", "Integration")]
public sealed class HookDiagnosticsTests
{
    [Fact]
    public async Task Write_creates_machine_readable_diagnostic_record()
    {
        using var sandbox = new TestSandbox();
        var invocationId = Guid.NewGuid();
        var timestamp = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        await HookDiagnosticStore.WriteAsync(sandbox.GitCommonDirectory, new HookDiagnosticEvent
        {
            EventName = "hook.invocation",
            InvocationId = invocationId,
            TimestampUtc = timestamp,
            DurationMs = 42,
            RepositoryIdentity = sandbox.GitCommonDirectory,
            AutonomousEnabled = true,
            ActivationMode = "always",
            ActivationResult = true,
            WorkflowItemType = "NewIssue",
            IssueNumber = 5,
            PullRequestNumber = 12,
            Worker = "terra",
            Model = "gpt-5-codex",
            ClaimId = "aaaaaaaa",
            Result = "context",
            BlockReason = null,
            ErrorType = null,
            ErrorMessage = null
        });

        var file = Assert.Single(Directory.GetFiles(GetDiagnosticsDirectory(sandbox), "invocation-*.json"));
        var record = await ReadRecordAsync(file);

        Assert.Equal(invocationId, record.InvocationId);
        Assert.Equal(timestamp, record.TimestampUtc);
        Assert.Equal(42, record.DurationMs);
        Assert.Equal(sandbox.GitCommonDirectory, record.RepositoryIdentity);
        Assert.True(record.AutonomousEnabled);
        Assert.Equal("always", record.ActivationMode);
        Assert.True(record.ActivationResult);
        Assert.Equal("NewIssue", record.WorkflowItemType);
        Assert.Equal(5, record.IssueNumber);
        Assert.Equal(12, record.PullRequestNumber);
        Assert.Equal("terra", record.Worker);
        Assert.Equal("gpt-5-codex", record.Model);
        Assert.Equal("aaaaaaaa", record.ClaimId);
        Assert.Equal("context", record.Result);
    }

    [Fact]
    public async Task Concurrent_writes_produce_distinct_uncorrupted_records()
    {
        using var sandbox = new TestSandbox();
        var invocationIds = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray();

        await Task.WhenAll(invocationIds.Select(invocationId =>
            HookDiagnosticStore.WriteAsync(sandbox.GitCommonDirectory, new HookDiagnosticEvent
            {
                InvocationId = invocationId,
                Result = "bypass"
            })));

        var files = Directory.GetFiles(GetDiagnosticsDirectory(sandbox), "invocation-*.json");
        Assert.Equal(invocationIds.Length, files.Length);
        Assert.Empty(Directory.GetFiles(GetDiagnosticsDirectory(sandbox), "*.tmp"));

        var recordedIds = new HashSet<Guid>();
        foreach (var file in files)
        {
            var record = await ReadRecordAsync(file);
            Assert.True(recordedIds.Add(record.InvocationId), "Concurrent writes must not duplicate an invocation.");
        }

        Assert.Equal(invocationIds.Length, recordedIds.Count);
    }

    [Fact]
    public async Task Write_to_unusable_location_is_best_effort()
    {
        using var sandbox = new TestSandbox();
        var blockerPath = Path.Combine(sandbox.Root, "blocker-file");
        await File.WriteAllTextAsync(blockerPath, "not a directory");

        await HookDiagnosticStore.WriteAsync(blockerPath, new HookDiagnosticEvent { InvocationId = Guid.NewGuid() });
    }

    [Fact]
    public async Task Prune_removes_expired_records_and_keeps_fresh_records()
    {
        using var sandbox = new TestSandbox();
        await HookDiagnosticStore.WriteAsync(sandbox.GitCommonDirectory, new HookDiagnosticEvent { InvocationId = Guid.NewGuid(), Result = "bypass" });
        await HookDiagnosticStore.WriteAsync(sandbox.GitCommonDirectory, new HookDiagnosticEvent { InvocationId = Guid.NewGuid(), Result = "bypass" });

        var files = Directory.GetFiles(GetDiagnosticsDirectory(sandbox), "invocation-*.json");
        Assert.Equal(2, files.Length);
        File.SetLastWriteTimeUtc(files[0], DateTime.UtcNow.AddDays(-10));

        await HookDiagnosticStore.PruneAsync(sandbox.GitCommonDirectory, retentionDays: 1);

        files = Directory.GetFiles(GetDiagnosticsDirectory(sandbox), "invocation-*.json");
        var remaining = Assert.Single(files);
        Assert.Equal(files[0], remaining);
    }

    [Fact]
    public async Task Scope_writes_record_with_context_outcome_and_shortened_claim()
    {
        using var sandbox = new TestSandbox();
        var fullClaimId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var scope = new HookDiagnosticScope(
            sandbox.RepositoryDirectory,
            _ => Task.FromResult<string?>(sandbox.GitCommonDirectory),
            model: "gpt-5-codex");
        scope.SetAutonomous(true);
        scope.SetActivation("always", true);
        scope.SetRepository(sandbox.GitCommonDirectory);
        scope.SetClaim(new WorkClaim
        {
            ClaimId = fullClaimId,
            IssueNumber = 5,
            PullRequestNumber = 12,
            WorkerProfile = "terra",
            Model = "gpt-5-codex"
        });
        scope.SetSelectedTask(new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 5 });
        scope.Context(new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 5, PullRequestNumber = 12 });

        await scope.CompleteAsync();

        var file = Assert.Single(Directory.GetFiles(GetDiagnosticsDirectory(sandbox), "invocation-*.json"));
        var record = await ReadRecordAsync(file);

        Assert.Equal(scope.InvocationId, record.InvocationId);
        Assert.True(record.DurationMs >= 0);
        Assert.Equal(sandbox.GitCommonDirectory, record.RepositoryIdentity);
        Assert.True(record.AutonomousEnabled);
        Assert.Equal("always", record.ActivationMode);
        Assert.True(record.ActivationResult);
        Assert.Equal("NewIssue", record.WorkflowItemType);
        Assert.Equal(5, record.IssueNumber);
        Assert.Equal(12, record.PullRequestNumber);
        Assert.Equal("terra", record.Worker);
        Assert.Equal("gpt-5-codex", record.Model);
        Assert.Equal("context", record.Result);

        var content = await File.ReadAllTextAsync(file);
        Assert.Contains(fullClaimId.ToString("N")[..8], content);
        Assert.DoesNotContain(fullClaimId.ToString("D"), content);
        Assert.DoesNotContain(fullClaimId.ToString("N"), content);
    }

    [Fact]
    public async Task Scope_skips_writing_when_disabled()
    {
        using var sandbox = new TestSandbox();
        var scope = new HookDiagnosticScope(
            sandbox.RepositoryDirectory,
            _ => Task.FromResult<string?>(sandbox.GitCommonDirectory));
        scope.SetDiagnosticsPolicy(new DiagnosticsPolicy { Enabled = false });
        scope.SetAutonomous(true);
        scope.Block("a block reason");

        await scope.CompleteAsync();

        Assert.Empty(Directory.GetFiles(sandbox.GitCommonDirectory, "invocation-*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Scope_swallows_resolution_failures()
    {
        using var sandbox = new TestSandbox();
        var scope = new HookDiagnosticScope(
            sandbox.RepositoryDirectory,
            _ => throw new InvalidOperationException("Resolution failed."));

        await scope.CompleteAsync();

        Assert.Empty(Directory.GetFiles(sandbox.GitCommonDirectory, "invocation-*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Scope_skips_writing_when_repository_cannot_be_resolved()
    {
        using var sandbox = new TestSandbox();
        var scope = new HookDiagnosticScope(
            sandbox.RepositoryDirectory,
            _ => Task.FromResult<string?>(null));

        await scope.CompleteAsync();

        Assert.Empty(Directory.GetFiles(sandbox.GitCommonDirectory, "invocation-*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Prompt_bypass_writes_bypass_diagnostic_without_changing_hook_output()
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
        await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, new WorkClaim
        {
            OwnerSessionId = "other-session",
            IssueNumber = 22,
            WorkType = WorkClaimType.Implementation
        });

        var originalIn = Console.In;
        var originalOut = Console.Out;
        var output = new StringWriter();
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["cwd"] = sandbox.RepositoryDirectory,
                ["hook_event_name"] = "UserPromptSubmit",
                ["model"] = "test-model",
                ["session_id"] = "current-session",
                ["prompt"] = "unrelated prompt"
            };

            Console.SetIn(new StringReader(JsonSerializer.Serialize(payload)));
            Console.SetOut(output);

            var result = await HookService.RunAsync(new HookExecutionDependencies
            {
                IsAutonomousAsync = _ => Task.FromResult(true),
                LoadConfigurationAsync = _ => Task.FromResult(configuration),
                ResolveGitCommonDirectoryAsync = _ => Task.FromResult<string?>(sandbox.GitCommonDirectory)
            });

            Assert.Equal(0, result);
            Assert.DoesNotContain("\"decision\"", output.ToString());
            Assert.DoesNotContain("\"hookSpecificOutput\"", output.ToString());
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }

        var file = Assert.Single(Directory.GetFiles(GetDiagnosticsDirectory(sandbox), "invocation-*.json"));
        var record = await ReadRecordAsync(file);

        Assert.Equal("bypass", record.Result);
        Assert.True(record.AutonomousEnabled);
        Assert.Equal("prompt", record.ActivationMode);
        Assert.False(record.ActivationResult);
        Assert.Equal("test-model", record.Model);
        Assert.Null(record.IssueNumber);
        Assert.Null(record.PullRequestNumber);
        Assert.Null(record.ClaimId);
    }

    [Fact]
    public async Task Config_with_default_diagnostics_policy_is_valid()
    {
        using var sandbox = new TestSandbox();
        await WorkflowConfigurationService.WriteDefaultAsync(sandbox.Paths.WorkflowFile);

        var configuration = await WorkflowConfigurationService.LoadOrDefaultAsync(sandbox.Paths);

        Assert.True(configuration.Policies.Diagnostics.Enabled);
        Assert.Equal(7, configuration.Policies.Diagnostics.RetentionDays);
    }

    [Fact]
    public async Task Config_validation_rejects_nonpositive_retention_days()
    {
        using var sandbox = new TestSandbox();
        await WorkflowConfigurationService.WriteDefaultAsync(sandbox.Paths.WorkflowFile);
        await File.WriteAllTextAsync(sandbox.Paths.WorkflowFile, """{"version":1,"policies":{"diagnostics":{"retentionDays":0}}}""");

        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowConfigurationService.LoadOrDefaultAsync(sandbox.Paths));
    }

    private static string GetDiagnosticsDirectory(TestSandbox sandbox) =>
        Path.Combine(sandbox.GitCommonDirectory, HookDiagnosticStore.DiagnosticsDirectoryName);

    private static async Task<HookDiagnosticEvent> ReadRecordAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var record = await JsonSerializer.DeserializeAsync<HookDiagnosticEvent>(stream, HookDiagnosticStore.JsonOptions);
        Assert.NotNull(record);
        return record!;
    }
}

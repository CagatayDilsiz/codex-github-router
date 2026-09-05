using System.Text.Json;
using CodexGithubRouter.Configurations;
using CodexGithubRouter.Doctor;
using CodexGithubRouter.Helpers;
using CodexGithubRouter.Work;
using CodexGithubRouter.Workflow;
using Xunit;

namespace CodexGithubRouter.Tests;

[Trait("Category", "Integration")]
public sealed class DoctorCommandTests
{
    [Fact]
    public async Task Doctor_healthy_environment_returns_zero()
    {
        using var sandbox = new TestSandbox();
        await WriteHooksAsync(sandbox, """{"hooks":{"UserPromptSubmit":[{"hooks":[{"type":"command","command":"cgr hook"}]}]}}""");
        await WorkflowConfigurationService.WriteDefaultAsync(sandbox.Paths.WorkflowFile);
        var deps = HealthyDependencies(sandbox);

        var result = await DoctorCommandHandler.HandleAsync(new[] { sandbox.RepositoryDirectory }, deps);

        Assert.Equal(0, result);
        Assert.Contains("[PASS] .NET Runtime", Output(deps));
        Assert.Contains("[PASS] Git", Output(deps));
        Assert.Contains("[PASS] GitHub CLI", Output(deps));
        Assert.Contains("[PASS] Global Workflow Configuration", Output(deps));
        Assert.Contains("[PASS] Git Repository", Output(deps));
        Assert.Contains("[PASS] Required GitHub Labels", Output(deps));
        Assert.Contains("Summary: 17 passed, 0 warning(s), 0 failed.", Output(deps));
    }

    [Fact]
    public async Task Doctor_is_read_only_and_creates_nothing_with_default_wiring()
    {
        using var sandbox = new TestSandbox();
        var deps = HealthyDependencies(sandbox);

        var result = await DoctorCommandHandler.HandleAsync(new[] { sandbox.RepositoryDirectory }, deps);

        Assert.Equal(1, result);
        Assert.Contains("[FAIL] CGR Hook Entry", Output(deps));
        Assert.False(File.Exists(sandbox.Paths.WorkflowFile));
        Assert.False(File.Exists(sandbox.Paths.CodexHooksFile));
        Assert.False(File.Exists(Path.Combine(sandbox.GitCommonDirectory, "codex-github-router.work.json")));
        Assert.False(File.Exists(Path.Combine(sandbox.GitCommonDirectory, "codex-github-router.work.lock")));
        Assert.False(File.Exists(Path.Combine(sandbox.GitCommonDirectory, "codex-github-router.auto")));
        Assert.Empty(Directory.GetFiles(sandbox.GitCommonDirectory));
        Assert.Empty(Directory.GetDirectories(sandbox.GitCommonDirectory));
    }

    [Fact]
    public async Task Doctor_outside_git_repository_reports_failure_and_still_runs_user_checks()
    {
        using var sandbox = new TestSandbox();
        var deps = HealthyDependencies(sandbox);
        deps = WithRepository(deps, repositoryRoot: null);

        var result = await DoctorCommandHandler.HandleAsync(new[] { sandbox.RepositoryDirectory }, deps);

        Assert.Equal(1, result);
        Assert.Contains("[FAIL] Git Repository", Output(deps));
        Assert.Contains("Not a valid Git repository.", Output(deps));
        Assert.Contains("[PASS] CGR Version", Output(deps));
        Assert.Contains("[PASS] .NET Runtime", Output(deps));
        Assert.Contains("Skipped: run cgr doctor from within a Git repository.", Output(deps));
    }

    [Fact]
    public async Task Doctor_missing_executables_do_not_hide_other_check_results()
    {
        using var sandbox = new TestSandbox();
        await WriteHooksAsync(sandbox, """{"hooks":{"UserPromptSubmit":[{"hooks":[{"type":"command","command":"cgr hook"}]}]}}""");
        await WorkflowConfigurationService.WriteDefaultAsync(sandbox.Paths.WorkflowFile);

        var deps = HealthyDependencies(sandbox);
        deps = WithMissingExecutables(deps);

        var result = await DoctorCommandHandler.HandleAsync(new[] { sandbox.RepositoryDirectory }, deps);

        Assert.Equal(1, result);
        var output = Output(deps);
        Assert.Contains("[FAIL] .NET Runtime", output);
        Assert.Contains("[FAIL] Git", output);
        Assert.Contains("[FAIL] GitHub CLI", output);
        Assert.Contains("[PASS] Codex Hooks Configuration", output);
        Assert.Contains("[PASS] CGR Hook Entry", output);
        Assert.Contains("[PASS] Global Workflow Configuration", output);
        Assert.Contains("[PASS] Git Repository", output);
    }

    [Fact]
    public async Task Doctor_dotnet_runtime_without_net10_returns_failure()
    {
        using var sandbox = new TestSandbox();
        var deps = WithDotNetRuntimes(HealthyDependencies(sandbox), "Microsoft.NETCore.App 9.0.0 [C:\\dotnet\\shared\\Microsoft.NETCore.App]");

        var result = await DoctorCommandHandler.HandleAsync(new[] { sandbox.RepositoryDirectory }, deps);

        Assert.Equal(1, result);
        var output = Output(deps);
        Assert.Contains("[FAIL] .NET Runtime", output);
        Assert.Contains("No .NET 10 runtime found", output);
    }

    [Fact]
    public async Task Doctor_dotnet_runtime_with_net10_returns_pass()
    {
        using var sandbox = new TestSandbox();
        await WriteHooksAsync(sandbox, """{"hooks":{"UserPromptSubmit":[{"hooks":[{"type":"command","command":"cgr hook"}]}]}}""");
        var deps = WithDotNetRuntimes(HealthyDependencies(sandbox), "Microsoft.NETCore.App 10.0.1 [C:\\dotnet\\shared\\Microsoft.NETCore.App]");

        var result = await DoctorCommandHandler.HandleAsync(new[] { sandbox.RepositoryDirectory }, deps);

        Assert.Equal(0, result);
        Assert.Contains("[PASS] .NET Runtime", Output(deps));
        Assert.Contains("supports net10.0", Output(deps));
    }

    [Fact]
    public async Task Doctor_dotnet_runtime_with_future_major_returns_failure()
    {
        using var sandbox = new TestSandbox();
        var deps = WithDotNetRuntimes(HealthyDependencies(sandbox), "Microsoft.NETCore.App 11.0.0 [C:\\dotnet\\shared\\Microsoft.NETCore.App]");

        var result = await DoctorCommandHandler.HandleAsync(new[] { sandbox.RepositoryDirectory }, deps);

        Assert.Equal(1, result);
        var output = Output(deps);
        Assert.Contains("[FAIL] .NET Runtime", output);
        Assert.Contains("No .NET 10 runtime found", output);
    }

    [Fact]
    public async Task Doctor_invalid_hooks_json_returns_failure()
    {
        using var sandbox = new TestSandbox();
        await WriteHooksAsync(sandbox, "not json");

        var deps = HealthyDependencies(sandbox);

        var result = await DoctorCommandHandler.HandleAsync(new[] { sandbox.RepositoryDirectory }, deps);

        Assert.Equal(1, result);
        Assert.Contains("[FAIL] Codex Hooks Configuration", Output(deps));
        Assert.Contains("Not valid JSON", Output(deps));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[1, 2]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("true")]
    public async Task Doctor_hooks_file_with_wrong_json_shape_reports_controlled_failure(string json)
    {
        using var sandbox = new TestSandbox();
        await WriteHooksAsync(sandbox, json);

        var deps = HealthyDependencies(sandbox);

        var result = await DoctorCommandHandler.HandleAsync(new[] { sandbox.RepositoryDirectory }, deps);

        Assert.Equal(1, result);
        var output = Output(deps);
        Assert.Contains("[FAIL] Codex Hooks Configuration", output);
        Assert.Contains("Not a valid JSON object", output);
        Assert.Contains("[PASS] .NET Runtime", output);
    }

    [Fact]
    public async Task Doctor_missing_hook_entry_reports_failure()
    {
        using var sandbox = new TestSandbox();
        await WriteHooksAsync(sandbox, """{"hooks":{"UserPromptSubmit":[{"hooks":[{"type":"command","command":"other"}]}]}}""");

        var deps = HealthyDependencies(sandbox);

        var result = await DoctorCommandHandler.HandleAsync(new[] { sandbox.RepositoryDirectory }, deps);

        Assert.Equal(1, result);
        Assert.Contains("[FAIL] CGR Hook Entry", Output(deps));
        Assert.Contains("No 'cgr hook' entry found", Output(deps));
    }

    [Fact]
    public async Task Doctor_missing_hooks_file_reports_failure_with_init_guidance()
    {
        using var sandbox = new TestSandbox();
        var deps = HealthyDependencies(sandbox);

        var result = await DoctorCommandHandler.HandleAsync(new[] { sandbox.RepositoryDirectory }, deps);

        Assert.Equal(1, result);
        var output = Output(deps);
        Assert.Contains("[WARN] Codex Hooks Configuration", output);
        Assert.Contains("Run 'cgr init'", output);
        Assert.Contains("[FAIL] CGR Hook Entry", output);
    }

    [Fact]
    public async Task Doctor_duplicate_hook_entries_reports_warning()
    {
        using var sandbox = new TestSandbox();
        await WriteHooksAsync(sandbox,
            """{"hooks":{"UserPromptSubmit":[{"hooks":[{"type":"command","command":"cgr hook"}]},{"hooks":[{"type":"command","command":"cgr hook"}]}]}}""");

        var deps = HealthyDependencies(sandbox);

        var result = await DoctorCommandHandler.HandleAsync(new[] { sandbox.RepositoryDirectory }, deps);

        Assert.Equal(0, result);
        var output = Output(deps);
        Assert.Contains("[WARN] CGR Hook Entry", output);
        Assert.Contains("2 'cgr hook' entries found", output);
    }

    [Fact]
    public async Task Doctor_invalid_work_claim_file_returns_failure()
    {
        using var sandbox = new TestSandbox();
        await File.WriteAllTextAsync(Path.Combine(sandbox.GitCommonDirectory, "codex-github-router.work.json"), "garbage");

        var deps = HealthyDependencies(sandbox);

        var result = await DoctorCommandHandler.HandleAsync(new[] { sandbox.RepositoryDirectory }, deps);

        Assert.Equal(1, result);
        Assert.Contains("[FAIL] Active Work Claim", Output(deps));
        Assert.Contains("Repair or remove the work-claim file", Output(deps));
    }

    [Fact]
    public async Task Doctor_active_work_claim_shows_summarized_identity()
    {
        using var sandbox = new TestSandbox();
        await WriteHooksAsync(sandbox, """{"hooks":{"UserPromptSubmit":[{"hooks":[{"type":"command","command":"cgr hook"}]}]}}""");
        var claim = new WorkClaim
        {
            ClaimId = Guid.NewGuid(),
            Version = 1,
            OwnerSessionId = "super-secret-session",
            IssueNumber = 5,
            WorkType = WorkClaimType.Implementation,
            WorkerProfile = "primary",
            Model = "gpt-x",
            ClaimedAt = DateTimeOffset.UtcNow,
            LastUpdatedAt = DateTimeOffset.UtcNow
        };
        await File.WriteAllTextAsync(Path.Combine(sandbox.GitCommonDirectory, "codex-github-router.work.json"), JsonSerializer.Serialize(claim));

        var deps = HealthyDependencies(sandbox);

        var result = await DoctorCommandHandler.HandleAsync(new[] { sandbox.RepositoryDirectory }, deps);

        Assert.Equal(0, result);
        var output = Output(deps);
        Assert.Contains("[PASS] Active Work Claim", output);
        Assert.Contains("issue #5", output);
        Assert.DoesNotContain("super-secret-session", output);
    }

    [Fact]
    public async Task Doctor_missing_required_labels_reports_warning_not_failure()
    {
        using var sandbox = new TestSandbox();
        await WriteHooksAsync(sandbox, """{"hooks":{"UserPromptSubmit":[{"hooks":[{"type":"command","command":"cgr hook"}]}]}}""");
        var deps = HealthyDependencies(sandbox);
        deps = WithLabels(deps, sandbox, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var result = await DoctorCommandHandler.HandleAsync(new[] { sandbox.RepositoryDirectory }, deps);

        Assert.Equal(0, result);
        Assert.Contains("[WARN] Required GitHub Labels", Output(deps));
        Assert.Contains("Missing label(s):", Output(deps));
        Assert.Contains("Run 'cgr auto on' to create them.", Output(deps));
    }

    [Fact]
    public async Task Doctor_worker_routing_model_resolves_worker()
    {
        using var sandbox = new TestSandbox();
        await WriteHooksAsync(sandbox, """{"hooks":{"UserPromptSubmit":[{"hooks":[{"type":"command","command":"cgr hook"}]}]}}""");
        await WriteWorkerRoutingConfigurationAsync(sandbox);

        var deps = HealthyDependencies(sandbox);

        var result = await DoctorCommandHandler.HandleAsync(new[] { "--model", "gpt-x", sandbox.RepositoryDirectory }, deps);

        Assert.Equal(0, result);
        var output = Output(deps);
        Assert.Contains("[PASS] Worker Routing", output);
        Assert.Contains("resolves to worker 'primary'", output);
    }

    [Fact]
    public async Task Doctor_worker_routing_unknown_model_reports_warning()
    {
        using var sandbox = new TestSandbox();
        await WriteHooksAsync(sandbox, """{"hooks":{"UserPromptSubmit":[{"hooks":[{"type":"command","command":"cgr hook"}]}]}}""");
        await WriteWorkerRoutingConfigurationAsync(sandbox);

        var deps = HealthyDependencies(sandbox);

        var result = await DoctorCommandHandler.HandleAsync(new[] { "--model", "unknown-model", sandbox.RepositoryDirectory }, deps);

        Assert.Equal(0, result);
        var output = Output(deps);
        Assert.Contains("[WARN] Worker Routing", output);
        Assert.Contains("resolves to worker '<none>'", output);
    }

    [Fact]
    public async Task Doctor_worker_routing_disabled_reports_pass()
    {
        using var sandbox = new TestSandbox();
        await WriteHooksAsync(sandbox, """{"hooks":{"UserPromptSubmit":[{"hooks":[{"type":"command","command":"cgr hook"}]}]}}""");
        var deps = HealthyDependencies(sandbox);

        var result = await DoctorCommandHandler.HandleAsync(new[] { "--model", "gpt-x", sandbox.RepositoryDirectory }, deps);

        Assert.Equal(0, result);
        Assert.Contains("[PASS] Worker Routing", Output(deps));
        Assert.Contains("Disabled; the default worker resolution is not active.", Output(deps));
    }

    [Fact]
    public async Task Doctor_invalid_global_configuration_reports_failure()
    {
        using var sandbox = new TestSandbox();
        await WriteWorkflowAsync(sandbox, """{"version":99}""");

        var deps = HealthyDependencies(sandbox);

        var result = await DoctorCommandHandler.HandleAsync(new[] { sandbox.RepositoryDirectory }, deps);

        Assert.Equal(1, result);
        Assert.Contains("[FAIL] Global Workflow Configuration", Output(deps));
        Assert.Contains("Unsupported workflow configuration version: 99", Output(deps));
    }

    [Fact]
    public async Task Doctor_invalid_repository_override_reports_failure()
    {
        using var sandbox = new TestSandbox();
        await WorkflowConfigurationService.WriteDefaultAsync(sandbox.Paths.WorkflowFile);
        var repoConfigDir = Path.Combine(sandbox.RepositoryDirectory, ".codex-github-router");
        Directory.CreateDirectory(repoConfigDir);
        await File.WriteAllTextAsync(Path.Combine(repoConfigDir, "workflow.json"), "not json");

        var deps = HealthyDependencies(sandbox);

        var result = await DoctorCommandHandler.HandleAsync(new[] { sandbox.RepositoryDirectory }, deps);

        Assert.Equal(1, result);
        var output = Output(deps);
        Assert.Contains("[FAIL] Repository Workflow Configuration", output);
        Assert.Contains("Not valid JSON", output);
    }

    [Fact]
    public async Task Doctor_no_arguments_uses_current_directory_and_returns_zero()
    {
        using var sandbox = new TestSandbox();
        await WriteHooksAsync(sandbox, """{"hooks":{"UserPromptSubmit":[{"hooks":[{"type":"command","command":"cgr hook"}]}]}}""");
        var deps = HealthyDependencies(sandbox);

        var result = await DoctorCommandHandler.HandleAsync(Array.Empty<string>(), deps);

        Assert.Equal(0, result);
        Assert.Contains("[PASS] Git Repository", Output(deps));
    }

    [Fact]
    public async Task Doctor_too_many_positional_arguments_returns_usage_error()
    {
        var deps = new DoctorCommandDependencies();
        var result = await DoctorCommandHandler.HandleAsync(new[] { "dir1", "dir2" }, deps);
        Assert.Equal(2, result);
    }

    [Fact]
    public async Task Doctor_unknown_option_returns_usage_error()
    {
        var deps = new DoctorCommandDependencies();
        var result = await DoctorCommandHandler.HandleAsync(new[] { "--unknown" }, deps);
        Assert.Equal(2, result);
    }

    [Fact]
    public async Task Doctor_model_option_without_value_returns_usage_error()
    {
        var deps = new DoctorCommandDependencies();
        var result = await DoctorCommandHandler.HandleAsync(new[] { "--model" }, deps);
        Assert.Equal(2, result);
    }

    [Fact]
    public void FormatClaimSummary_never_contains_owner_session_id()
    {
        var claim = new WorkClaim
        {
            ClaimId = Guid.NewGuid(),
            Version = 1,
            OwnerSessionId = "session-secret-abc",
            IssueNumber = 42,
            PullRequestNumber = 7,
            WorkType = WorkClaimType.ChangeRequest,
            ClaimedAt = DateTimeOffset.UtcNow,
            LastUpdatedAt = DateTimeOffset.UtcNow
        };

        var summary = DoctorCommandHandler.FormatClaimSummary(claim);

        Assert.Contains("issue #42 / pull request #7", summary);
        Assert.Contains("ChangeRequest", summary);
        Assert.DoesNotContain("session-secret-abc", summary);
    }

    private static string Output(DoctorCommandDependencies deps) => ((StringWriter)deps.Output).ToString();

    private static DoctorCommandDependencies HealthyDependencies(TestSandbox sandbox)
    {
        return new DoctorCommandDependencies
        {
            Paths = sandbox.Paths,
            Output = new StringWriter(),
            Error = new StringWriter(),
            GetRepositoryRootAsync = (_, _) => Task.FromResult<string?>(sandbox.RepositoryDirectory),
            GetGitCommonDirectoryAsync = (_, _) => Task.FromResult<string?>(sandbox.GitCommonDirectory),
            RunVersionProcessAsync = (executable, _) => Task.FromResult<ProcessResult?>(new ProcessResult { ExitCode = 0, Output = $"{executable} version 1.0" }),
            RunDotNetRuntimesProcessAsync = _ => Task.FromResult<ProcessResult?>(new ProcessResult { ExitCode = 0, Output = "Microsoft.NETCore.App 10.0.0 [C:\\dotnet\\shared\\Microsoft.NETCore.App]" }),
            RunGitHubAuthStatusProcessAsync = _ => Task.FromResult<ProcessResult?>(new ProcessResult { ExitCode = 0, Output = "Logged in to github.com as cagatay" }),
            LoadGlobalConfigurationAsync = cancellationToken => WorkflowConfigurationService.LoadOrDefaultAsync(sandbox.Paths, cancellationToken),
            LoadEffectiveConfigurationAsync = (repositoryRoot, cancellationToken) => WorkflowConfigurationService.LoadEffectiveFromRepositoryRootAsync(repositoryRoot, sandbox.Paths, cancellationToken),
            GetRepositoryLabelNamesAsync = async (repositoryRoot, cancellationToken) =>
            {
                var configuration = await WorkflowConfigurationService.LoadEffectiveFromRepositoryRootAsync(repositoryRoot, sandbox.Paths, cancellationToken);
                return WorkflowLabelConfiguration.GetRequiredLabels(configuration).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
        };
    }

    private static DoctorCommandDependencies WithRepository(DoctorCommandDependencies deps, string? repositoryRoot)
    {
        deps = new DoctorCommandDependencies
        {
            Paths = deps.Paths,
            Output = deps.Output,
            Error = deps.Error,
            GetRepositoryRootAsync = (_, _) => Task.FromResult(repositoryRoot),
            GetGitCommonDirectoryAsync = deps.GetGitCommonDirectoryAsync,
            RunVersionProcessAsync = deps.RunVersionProcessAsync,
            RunDotNetRuntimesProcessAsync = deps.RunDotNetRuntimesProcessAsync,
            RunGitHubAuthStatusProcessAsync = deps.RunGitHubAuthStatusProcessAsync,
            LoadGlobalConfigurationAsync = deps.LoadGlobalConfigurationAsync,
            LoadEffectiveConfigurationAsync = deps.LoadEffectiveConfigurationAsync,
            ReadWorkClaimsAsync = deps.ReadWorkClaimsAsync,
            GetRepositoryLabelNamesAsync = deps.GetRepositoryLabelNamesAsync
        };
        return deps;
    }

    private static DoctorCommandDependencies WithMissingExecutables(DoctorCommandDependencies deps)
    {
        deps = new DoctorCommandDependencies
        {
            Paths = deps.Paths,
            Output = deps.Output,
            Error = deps.Error,
            GetRepositoryRootAsync = deps.GetRepositoryRootAsync,
            GetGitCommonDirectoryAsync = deps.GetGitCommonDirectoryAsync,
            RunVersionProcessAsync = (_, _) => Task.FromResult<ProcessResult?>(null),
            RunDotNetRuntimesProcessAsync = deps.RunDotNetRuntimesProcessAsync,
            RunGitHubAuthStatusProcessAsync = deps.RunGitHubAuthStatusProcessAsync,
            LoadGlobalConfigurationAsync = deps.LoadGlobalConfigurationAsync,
            LoadEffectiveConfigurationAsync = deps.LoadEffectiveConfigurationAsync,
            ReadWorkClaimsAsync = deps.ReadWorkClaimsAsync,
            GetRepositoryLabelNamesAsync = deps.GetRepositoryLabelNamesAsync
        };
        return deps;
    }

    private static DoctorCommandDependencies WithLabels(DoctorCommandDependencies deps, TestSandbox sandbox, HashSet<string> labels)
    {
        deps = new DoctorCommandDependencies
        {
            Paths = deps.Paths,
            Output = deps.Output,
            Error = deps.Error,
            GetRepositoryRootAsync = deps.GetRepositoryRootAsync,
            GetGitCommonDirectoryAsync = deps.GetGitCommonDirectoryAsync,
            RunVersionProcessAsync = deps.RunVersionProcessAsync,
            RunDotNetRuntimesProcessAsync = deps.RunDotNetRuntimesProcessAsync,
            RunGitHubAuthStatusProcessAsync = deps.RunGitHubAuthStatusProcessAsync,
            LoadGlobalConfigurationAsync = deps.LoadGlobalConfigurationAsync,
            LoadEffectiveConfigurationAsync = deps.LoadEffectiveConfigurationAsync,
            ReadWorkClaimsAsync = deps.ReadWorkClaimsAsync,
            GetRepositoryLabelNamesAsync = (_, _) => Task.FromResult(labels)
        };
        return deps;
    }

    private static DoctorCommandDependencies WithDotNetRuntimes(DoctorCommandDependencies deps, string runtimesOutput)
    {
        deps = new DoctorCommandDependencies
        {
            Paths = deps.Paths,
            Output = deps.Output,
            Error = deps.Error,
            GetRepositoryRootAsync = deps.GetRepositoryRootAsync,
            GetGitCommonDirectoryAsync = deps.GetGitCommonDirectoryAsync,
            RunVersionProcessAsync = deps.RunVersionProcessAsync,
            RunDotNetRuntimesProcessAsync = _ => Task.FromResult<ProcessResult?>(new ProcessResult { ExitCode = 0, Output = runtimesOutput }),
            RunGitHubAuthStatusProcessAsync = deps.RunGitHubAuthStatusProcessAsync,
            LoadGlobalConfigurationAsync = deps.LoadGlobalConfigurationAsync,
            LoadEffectiveConfigurationAsync = deps.LoadEffectiveConfigurationAsync,
            ReadWorkClaimsAsync = deps.ReadWorkClaimsAsync,
            GetRepositoryLabelNamesAsync = deps.GetRepositoryLabelNamesAsync
        };
        return deps;
    }

    private static async Task WriteHooksAsync(TestSandbox sandbox, string json)
    {
        var path = sandbox.Paths.CodexHooksFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, json);
    }

    private static async Task WriteWorkflowAsync(TestSandbox sandbox, string json)
    {
        var path = sandbox.Paths.WorkflowFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, json);
    }

    private static async Task WriteWorkerRoutingConfigurationAsync(TestSandbox sandbox)
    {
        var configuration = new RouterConfiguration
        {
            Policies = new RouterPolicies
            {
                WorkerRouting = new WorkerRoutingPolicy
                {
                    DefaultWorker = "primary",
                    Workers = new Dictionary<string, WorkerProfileConfiguration>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["primary"] = new WorkerProfileConfiguration
                        {
                            Labels = new List<string> { "codex:worker:primary" },
                            Models = new List<string> { "gpt-x" }
                        }
                    }
                }
            }
        };
        await WriteWorkflowAsync(sandbox, JsonSerializer.Serialize(configuration, WorkflowJson.Options));
    }
}

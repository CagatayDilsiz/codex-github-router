using CodexGithubRouter.Configurations;
using CodexGithubRouter.Explain;
using CodexGithubRouter.GitHub;
using CodexGithubRouter.Work;
using CodexGithubRouter.Workflow;
using Xunit;

namespace CodexGithubRouter.Tests;

[Trait("Category", "Integration")]
public sealed class ExplainCommandTests
{
    [Fact]
    public async Task Explain_without_issue_explains_all_workflow_issues()
    {
        using var sandbox = new TestSandbox();
        var output = new StringWriter();
        var error = new StringWriter();
        var deps = Dependencies(sandbox, output, error, issues: new List<Issue> { ReadyIssue(1), ReadyIssue(2) });

        var result = await ExplainCommandHandler.HandleAsync(Array.Empty<string>(), deps);

        Assert.Equal(0, result);
        var text = output.ToString();
        Assert.Contains("Routing explanations:", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#1", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#2", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ELIGIBLE", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Explain_single_issue_shows_detailed_explanation()
    {
        using var sandbox = new TestSandbox();
        var output = new StringWriter();
        var error = new StringWriter();
        var deps = Dependencies(sandbox, output, error, issueByNumber: number => number == 1 ? ReadyIssue(1) : null);

        var result = await ExplainCommandHandler.HandleAsync(new[] { "--issue", "1" }, deps);

        Assert.Equal(0, result);
        var text = output.ToString();
        Assert.Contains("Issue #1", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Eligible:", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Workflow State", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Explain_returns_error_for_unknown_issue()
    {
        using var sandbox = new TestSandbox();
        var output = new StringWriter();
        var error = new StringWriter();
        var deps = Dependencies(sandbox, output, error);

        var result = await ExplainCommandHandler.HandleAsync(new[] { "--issue", "999" }, deps);

        Assert.Equal(1, result);
        Assert.Contains("not found", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Explain_is_read_only_and_writes_no_files()
    {
        using var sandbox = new TestSandbox();
        var output = new StringWriter();
        var error = new StringWriter();
        var deps = Dependencies(sandbox, output, error, issues: new List<Issue> { ReadyIssue(1) });

        await ExplainCommandHandler.HandleAsync(Array.Empty<string>(), deps);

        Assert.False(File.Exists(Path.Combine(sandbox.GitCommonDirectory, "codex-github-router.work.json")));
        Assert.False(File.Exists(Path.Combine(sandbox.GitCommonDirectory, "codex-github-router.work.lock")));
        Assert.False(File.Exists(Path.Combine(sandbox.GitCommonDirectory, "codex-github-router.auto")));
    }

    [Fact]
    public async Task Explain_with_invalid_args_returns_usage_error()
    {
        using var sandbox = new TestSandbox();
        var output = new StringWriter();
        var error = new StringWriter();
        var deps = Dependencies(sandbox, output, error);

        var result = await ExplainCommandHandler.HandleAsync(new[] { "--unknown" }, deps);

        Assert.Equal(2, result);
        Assert.Contains("unknown option", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Explain_with_issue_requires_numeric_value()
    {
        using var sandbox = new TestSandbox();
        var output = new StringWriter();
        var error = new StringWriter();
        var deps = Dependencies(sandbox, output, error);

        var result = await ExplainCommandHandler.HandleAsync(new[] { "--issue", "abc" }, deps);

        Assert.Equal(2, result);
        Assert.Contains("numeric", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Explain_shows_worker_routing_stage_when_configured()
    {
        using var sandbox = new TestSandbox();
        var output = new StringWriter();
        var error = new StringWriter();
        var deps = Dependencies(sandbox, output, error,
            configuration: WorkerConfiguration(),
            issueByNumber: number => number == 1 ? ReadyIssue(1, "codex:worker:luna") : null);

        var result = await ExplainCommandHandler.HandleAsync(new[] { "--issue", "1", "--model", "luna-model" }, deps);

        Assert.Equal(0, result);
        var text = output.ToString();
        Assert.Contains("Worker Routing", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Explain_shows_assignment_routing_stage_when_configured()
    {
        using var sandbox = new TestSandbox();
        var output = new StringWriter();
        var error = new StringWriter();
        var deps = Dependencies(sandbox, output, error,
            configuration: AssignmentConfiguration("prefer", "allow"),
            gitIdentity: "alice",
            issueByNumber: number => number == 1 ? IssueWithAssignee(1, "alice") : null);

        var result = await ExplainCommandHandler.HandleAsync(new[] { "--issue", "1" }, deps);

        Assert.Equal(0, result);
        var text = output.ToString();
        Assert.Contains("Assignment Routing", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Explain_shows_ineligible_issue_with_blocked_verdicts()
    {
        using var sandbox = new TestSandbox();
        var output = new StringWriter();
        var error = new StringWriter();
        var deps = Dependencies(sandbox, output, error, issueByNumber: number => number == 1 ? BlockedIssue(1) : null);

        var result = await ExplainCommandHandler.HandleAsync(new[] { "--issue", "1" }, deps);

        Assert.Equal(0, result);
        var text = output.ToString();
        Assert.Contains("BLOCKED", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Eligible: no", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Explain_no_issues_found_shows_message()
    {
        using var sandbox = new TestSandbox();
        var output = new StringWriter();
        var error = new StringWriter();
        var deps = Dependencies(sandbox, output, error, issues: new List<Issue>());

        var result = await ExplainCommandHandler.HandleAsync(Array.Empty<string>(), deps);

        Assert.Equal(0, result);
        Assert.Contains("No issues found", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Explain_all_shows_rank_ordering()
    {
        using var sandbox = new TestSandbox();
        var output = new StringWriter();
        var error = new StringWriter();
        var deps = Dependencies(sandbox, output, error,
            configuration: AssignmentConfiguration("prefer", "allow"),
            gitIdentity: "alice",
            issues: new List<Issue>
            {
                IssueWithAssignee(1, "alice"),
                IssueWithAssignee(2, "bob"),
                IssueWithAssignee(3)
            });

        var result = await ExplainCommandHandler.HandleAsync(Array.Empty<string>(), deps);

        Assert.Equal(0, result);
        var text = output.ToString();
        Assert.Contains("ELIGIBLE", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Explain_read_only_under_error_paths()
    {
        using var sandbox = new TestSandbox();
        var output = new StringWriter();
        var error = new StringWriter();
        var deps = Dependencies(sandbox, output, error);

        await ExplainCommandHandler.HandleAsync(new[] { "--issue", "999" }, deps);

        Assert.False(File.Exists(Path.Combine(sandbox.GitCommonDirectory, "codex-github-router.work.json")));
        Assert.False(File.Exists(Path.Combine(sandbox.GitCommonDirectory, "codex-github-router.work.lock")));
    }

    [Fact]
    public async Task Explain_reports_routing_evaluation_failure()
    {
        using var sandbox = new TestSandbox();
        var output = new StringWriter();
        var error = new StringWriter();
        var deps = Dependencies(sandbox, output, error, evaluationFails: true);

        var result = await ExplainCommandHandler.HandleAsync(Array.Empty<string>(), deps);

        Assert.Equal(1, result);
        Assert.Contains("Routing evaluation failed", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Explain_fails_closed_when_assignment_identity_cannot_be_resolved()
    {
        using var sandbox = new TestSandbox();
        var output = new StringWriter();
        var error = new StringWriter();
        var deps = Dependencies(sandbox, output, error,
            configuration: AssignmentConfiguration("require", "allow"),
            issues: new List<Issue> { ReadyIssue(1) });

        var result = await ExplainCommandHandler.HandleAsync(Array.Empty<string>(), deps);

        Assert.Equal(1, result);
        Assert.Contains("could not be resolved", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ELIGIBLE", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static ExplainCommandDependencies Dependencies(
        TestSandbox sandbox,
        TextWriter output,
        TextWriter error,
        RouterConfiguration? configuration = null,
        IReadOnlyList<Issue>? issues = null,
        Func<int, Issue?>? issueByNumber = null,
        string? gitIdentity = null,
        bool evaluationFails = false) => new()
    {
        Output = output,
        Error = error,
        GetGitCommonDirectoryAsync = (_, _) => Task.FromResult<string?>(sandbox.GitCommonDirectory),
        GetWorktreeIdAsync = (_, _) => Task.FromResult<string?>(sandbox.MainWorktreeId),
        LoadEffectiveConfigurationAsync = (_, _) => Task.FromResult(configuration ?? new RouterConfiguration()),
        GetIssueByNumberAsync = (_, number, _) => Task.FromResult(issueByNumber?.Invoke(number)),
        RoutingEvaluation = new RoutingEvaluationDependencies
        {
            CheckRepositoryGateAsync = (_, _) => evaluationFails
                ? Task.FromResult(new WorkflowResponse { IsSuccessful = false, Message = "Gate evaluation failed." })
                : Task.FromResult(OkGate()),
            CheckCompletedIssuesAsync = (_, _, _, _) => Task.FromResult(Ok()),
            CheckInProgressIssuesAsync = (_, _, _, _) => Task.FromResult(Ok()),
            CheckNewIssuesAsync = (_, _, _, _) => Task.FromResult(Ok(issues ?? new List<Issue>()))
        },
        ReadWorkClaimAsync = (_, _, _) => Task.FromResult<WorkClaim?>(null),
        ResolveLocalIdentityAsync = (_, _) => Task.FromResult(gitIdentity),
        ResolveAuthenticatedGitHubLoginAsync = (_, _) => Task.FromResult<string?>(null)
    };

    private static WorkflowResponse OkGate(params WorkflowItem[] tasks) => new()
    {
        IsSuccessful = true,
        Tasks = tasks.ToList(),
        Message = tasks.Length == 0 ? "No blocking repository gates found." : "Repository gate evaluation completed."
    };

    private static WorkflowResponse Ok() => new() { IsSuccessful = true, Tasks = new List<WorkflowItem>() };

    private static WorkflowResponse Ok(IReadOnlyList<Issue> issues) => new()
    {
        IsSuccessful = true,
        Tasks = issues.Select(issue => new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = issue.Number }).ToList(),
        ConsideredIssues = issues.ToList()
    };

    private static RouterConfiguration WorkerConfiguration() => new()
    {
        Policies = new RouterPolicies
        {
            WorkerRouting = new WorkerRoutingPolicy
            {
                DefaultWorker = "luna",
                Workers = new Dictionary<string, WorkerProfileConfiguration>(StringComparer.OrdinalIgnoreCase)
                {
                    ["luna"] = new() { Labels = new() { "codex:worker:luna" }, Models = new() { "luna-model" } },
                    ["terra"] = new() { Labels = new() { "codex:worker:terra" }, Models = new() { "terra-model" } }
                }
            }
        }
    };

    private static RouterConfiguration AssignmentConfiguration(string mode, string unassigned) => new()
    {
        Policies = new RouterPolicies
        {
            AssignmentRouting = new AssignmentRoutingPolicy
            {
                Mode = mode,
                Unassigned = unassigned
            }
        }
    };

    private static Issue ReadyIssue(int number) => new()
    {
        Number = number,
        Labels = new List<GithubLabel> { new() { Name = "codex:ready" } }
    };

    private static Issue BlockedIssue(int number) => new()
    {
        Number = number,
        Labels = new List<GithubLabel> { new() { Name = "codex:blocked" } }
    };

    private static Issue ReadyIssue(int number, params string[] workerLabels) => new()
    {
        Number = number,
        Labels = new List<GithubLabel> { new() { Name = "codex:ready" } }
            .Concat(workerLabels.Select(label => new GithubLabel { Name = label }))
            .ToList()
    };

    private static Issue IssueWithAssignee(int number, params string[] assignees) => new()
    {
        Number = number,
        Labels = new List<GithubLabel> { new() { Name = "codex:ready" } },
        Assignees = assignees.Select(login => new GithubUser { Login = login }).ToList()
    };
}
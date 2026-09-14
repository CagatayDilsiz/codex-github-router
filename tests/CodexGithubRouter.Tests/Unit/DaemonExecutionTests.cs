using CodexGithubRouter.Daemon;
using CodexGithubRouter.Hooks;
using CodexGithubRouter.Work;
using CodexGithubRouter.Workflow;
using Xunit;

namespace CodexGithubRouter.Tests.Unit;

public class DaemonExecutionTests
{
    [Fact]
    public async Task RunOnce_AcquiresClaimAndLaunchesSession_WhenPlanSelectsUsableWork()
    {
        using var sandbox = new TestSandbox();
        var context = new DaemonTestContext(sandbox);
        context.Plan = SuccessfulPlan(new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 12 });

        var tick = await DaemonExecutionService.RunOnceAsync(sandbox.RepositoryDirectory, context.Dependencies, CancellationToken.None);

        Assert.False(tick.StopRequested);
        Assert.StartsWith("Launched session for issue #12", tick.Summary, StringComparison.Ordinal);
        Assert.Single(context.SessionHost.Launches);
        Assert.Equal(1, context.AcquireCalls);
        Assert.Contains("Next task is to work on issue #12.", context.SessionHost.Launches[0].Prompt, StringComparison.Ordinal);
        Assert.Equal(sandbox.RepositoryDirectory, context.SessionHost.Launches[0].Session.WorktreeDirectory);
        Assert.Equal(WorkClaimType.Implementation, context.SessionHost.Launches[0].Claim.WorkType);

        var state = await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory);
        Assert.NotNull(state);
        Assert.NotNull(state!.ActiveSession);
        Assert.Equal(context.SessionHost.Launches[0].Session.ClaimId, state.ActiveSession.ClaimId);

        var claim = await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId);
        Assert.NotNull(claim);
        Assert.Equal(12, claim!.IssueNumber);
        Assert.Equal(state.DaemonSessionId, claim.OwnerSessionId);
        Assert.Single(await WorkClaimStore.ReadAllAsync(sandbox.GitCommonDirectory));
    }

    [Fact]
    public async Task RunOnce_RefusesToRun_WhenExecutionModeIsNotDaemon()
    {
        using var sandbox = new TestSandbox();
        var context = new DaemonTestContext(sandbox);
        context.Configuration = new RouterConfiguration
        {
            Policies = new RouterPolicies { Execution = new ExecutionPolicy { Mode = ExecutionMode.Hook } }
        };

        var tick = await DaemonExecutionService.RunOnceAsync(sandbox.RepositoryDirectory, context.Dependencies, CancellationToken.None);

        Assert.True(tick.StopRequested);
        Assert.Equal("Execution mode is not daemon.", tick.Summary);
        Assert.Empty(context.SessionHost.Launches);
        Assert.Null(await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory));
    }

    [Fact]
    public async Task RunOnce_RefusesToRun_WhenAutonomousModeIsDisabled()
    {
        using var sandbox = new TestSandbox();
        var context = new DaemonTestContext(sandbox) { AutonomousEnabled = false };

        var tick = await DaemonExecutionService.RunOnceAsync(sandbox.RepositoryDirectory, context.Dependencies, CancellationToken.None);

        Assert.True(tick.StopRequested);
        Assert.Contains("Autonomous mode is not enabled", tick.Summary, StringComparison.Ordinal);
        Assert.Empty(context.SessionHost.Launches);
    }

    [Fact]
    public async Task RunOnce_ResumesClaimWithoutReacquiring_WhenSessionExitsUnexpectedly()
    {
        using var sandbox = new TestSandbox();
        var context = new DaemonTestContext(sandbox);
        context.Plan = SuccessfulPlan(new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 12 });
        context.ClaimedWork = new WorkflowResponse
        {
            Tasks = { new WorkflowItem { Type = WorkflowItemType.ResumeInProgressIssue, IssueNumber = 12 } }
        };

        var first = await DaemonExecutionService.RunOnceAsync(sandbox.RepositoryDirectory, context.Dependencies, CancellationToken.None);
        Assert.Equal(1, context.AcquireCalls);
        var originalClaimId = context.SessionHost.Launches[0].Claim.ClaimId;

        context.SessionHost.Alive = false;

        var second = await DaemonExecutionService.RunOnceAsync(sandbox.RepositoryDirectory, context.Dependencies, CancellationToken.None);

        Assert.False(second.StopRequested);
        Assert.StartsWith("Resumed session for issue #12", second.Summary, StringComparison.Ordinal);
        Assert.Equal(2, context.SessionHost.Launches.Count);
        Assert.Equal(originalClaimId, context.SessionHost.Launches[1].Claim.ClaimId);
        Assert.Equal(1, context.AcquireCalls);

        var claims = await WorkClaimStore.ReadAllAsync(sandbox.GitCommonDirectory);
        Assert.Single(claims);
        Assert.Equal(originalClaimId, claims[0].ClaimId);
    }

    [Fact]
    public async Task RunOnce_ReleasesClaim_WhenEndedSessionReachesPassiveOrTerminalState()
    {
        using var sandbox = new TestSandbox();
        var context = new DaemonTestContext(sandbox);
        context.Plan = SuccessfulPlan(new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 12 });

        await DaemonExecutionService.RunOnceAsync(sandbox.RepositoryDirectory, context.Dependencies, CancellationToken.None);

        context.SessionHost.Alive = false;
        context.ClaimedWork = new WorkflowResponse
        {
            Tasks = { new WorkflowItem { Type = WorkflowItemType.AwaitingReview } }
        };

        var second = await DaemonExecutionService.RunOnceAsync(sandbox.RepositoryDirectory, context.Dependencies, CancellationToken.None);

        Assert.Contains("claim released", second.Summary, StringComparison.Ordinal);
        Assert.Null(await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId));

        var state = await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory);
        Assert.NotNull(state);
        Assert.Null(state!.ActiveSession);
    }

    [Fact]
    public async Task RunOnce_DoesNotDuplicateWork_WhenAnotherSessionOwnsTheClaim()
    {
        using var sandbox = new TestSandbox();
        var context = new DaemonTestContext(sandbox);
        context.Plan = SuccessfulPlan(new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 12 });

        // A separate session (a different worktree or a prior hook claim) already owns the work.
        await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId, new WorkClaim
        {
            OwnerSessionId = "other-session",
            IssueNumber = 12,
            WorkType = WorkClaimType.Implementation
        });

        var tick = await DaemonExecutionService.RunOnceAsync(sandbox.RepositoryDirectory, context.Dependencies, CancellationToken.None);

        Assert.Contains("another Codex session", tick.Summary, StringComparison.Ordinal);
        Assert.Empty(context.SessionHost.Launches);

        var claims = await WorkClaimStore.ReadAllAsync(sandbox.GitCommonDirectory);
        Assert.Single(claims);
        Assert.Equal("other-session", claims[0].OwnerSessionId);
    }

    [Fact]
    public async Task RunOnce_MarksUnhealthy_AfterConfiguredFailureThreshold()
    {
        using var sandbox = new TestSandbox();
        var context = new DaemonTestContext(sandbox)
        {
            Configuration = new RouterConfiguration
            {
                Policies = new RouterPolicies
                {
                    Execution = new ExecutionPolicy { Mode = ExecutionMode.Daemon },
                    Daemon = new DaemonPolicy { IntervalSeconds = 5, FailureThreshold = 2 }
                }
            }
        };
        context.Plan = RoutingEvaluationResult.Failure("GitHub CLI is unavailable.", new RouterConfiguration(), sandbox.RepositoryDirectory, null, null, null);

        var first = await DaemonExecutionService.RunOnceAsync(sandbox.RepositoryDirectory, context.Dependencies, CancellationToken.None);
        var second = await DaemonExecutionService.RunOnceAsync(sandbox.RepositoryDirectory, context.Dependencies, CancellationToken.None);

        Assert.Equal(1, first.ConsecutiveFailures);
        Assert.False(first.Unhealthy);
        Assert.Equal(2, second.ConsecutiveFailures);
        Assert.True(second.Unhealthy);

        var state = await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory);
        Assert.NotNull(state);
        Assert.True(state!.Unhealthy);
        Assert.Equal(2, state.ConsecutiveFailures);
        Assert.Empty(context.SessionHost.Launches);
    }

    [Fact]
    public async Task RunOnce_HonorsGracefulStopRequest()
    {
        using var sandbox = new TestSandbox();
        var context = new DaemonTestContext(sandbox);
        context.Plan = SuccessfulPlan(new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 12 });
        await DaemonStateStore.WriteAsync(sandbox.GitCommonDirectory, new DaemonExecutionState
        {
            DaemonSessionId = "stopped-session",
            StopRequested = true
        });

        var tick = await DaemonExecutionService.RunOnceAsync(sandbox.RepositoryDirectory, context.Dependencies, CancellationToken.None);

        Assert.True(tick.StopRequested);
        Assert.Contains("shut down cleanly", tick.Summary, StringComparison.Ordinal);
        Assert.Empty(context.SessionHost.Launches);

        var state = await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory);
        Assert.NotNull(state);
        Assert.True(state!.StoppedAt.HasValue);
    }

    [Fact]
    public async Task RunOnce_ReusesStableDaemonSessionId_AcrossRestarts()
    {
        using var sandbox = new TestSandbox();
        var context = new DaemonTestContext(sandbox);
        context.Plan = SuccessfulPlan(new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 12 });

        await DaemonExecutionService.RunOnceAsync(sandbox.RepositoryDirectory, context.Dependencies, CancellationToken.None);
        var originalSessionId = (await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory))!.DaemonSessionId;

        // Simulate a process restart: a new dependency set but the same persisted state file.
        var restartContext = new DaemonTestContext(sandbox);
        restartContext.Plan = SuccessfulPlan(new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 12 });
        await DaemonExecutionService.RunOnceAsync(sandbox.RepositoryDirectory, restartContext.Dependencies, CancellationToken.None);

        var restartedSessionId = (await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory))!.DaemonSessionId;
        Assert.Equal(originalSessionId, restartedSessionId);
    }

    [Fact]
    public async Task RunOnce_ExecutesCloseIssueActions_AsDeterministicMechanicalSteps()
    {
        using var sandbox = new TestSandbox();
        var context = new DaemonTestContext(sandbox);
        context.Plan = SuccessfulPlan(
            new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 12 },
            new WorkflowItem { Type = WorkflowItemType.CloseIssue, IssueNumber = 9 });

        var tick = await DaemonExecutionService.RunOnceAsync(sandbox.RepositoryDirectory, context.Dependencies, CancellationToken.None);

        Assert.False(tick.StopRequested);
        Assert.Contains(9, context.ClosedIssueNumbers);
        Assert.DoesNotContain(12, context.ClosedIssueNumbers);
        Assert.Single(context.SessionHost.Launches);
    }

    private static RoutingEvaluationResult SuccessfulPlan(params WorkflowItem[] tasks)
    {
        var selected = tasks.FirstOrDefault(task => task.Type != WorkflowItemType.CloseIssue) ?? tasks[0];
        return new RoutingEvaluationResult
        {
            IsSuccessful = true,
            Decision = new HookTaskDecision { SelectedTask = selected },
            ActionableTasks = tasks
        };
    }

    private sealed class DaemonTestContext
    {
        private readonly TestSandbox _sandbox;

        public DaemonTestContext(TestSandbox sandbox)
        {
            _sandbox = sandbox;
            Configuration = new RouterConfiguration
            {
                Policies = new RouterPolicies
                {
                    Execution = new ExecutionPolicy { Mode = ExecutionMode.Daemon },
                    Daemon = new DaemonPolicy { IntervalSeconds = 60, FailureThreshold = 5 }
                }
            };
        }

        public RouterConfiguration Configuration { get; set; }
        public bool AutonomousEnabled { get; set; } = true;
        public RoutingEvaluationResult Plan { get; set; } = new()
        {
            IsSuccessful = true,
            Decision = new HookTaskDecision(),
            ActionableTasks = Array.Empty<WorkflowItem>()
        };

        public WorkflowResponse ClaimedWork { get; set; } = new()
        {
            Tasks = { new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 12 } }
        };

        public FakeSessionHost SessionHost { get; } = new();
        public int AcquireCalls { get; private set; }
        public List<int> ClosedIssueNumbers { get; } = new();

        public DaemonExecutionDependencies Dependencies => new()
        {
            LoadConfigurationAsync = _ => Task.FromResult(Configuration),
            ResolveGitCommonDirectoryAsync = _ => Task.FromResult<string?>(_sandbox.GitCommonDirectory),
            ResolveWorktreeIdAsync = _ => Task.FromResult<string?>(_sandbox.MainWorktreeId),
            IsAutonomousAsync = _ => Task.FromResult(AutonomousEnabled),
            TryAcquireClaimAsync = (gitCommonDir, worktreeId, claimed) =>
            {
                AcquireCalls++;
                return WorkClaimStore.TryAcquireAsync(gitCommonDir, worktreeId, claimed);
            },
            ReadClaimAsync = (gitCommonDir, worktreeId) => WorkClaimStore.ReadAsync(gitCommonDir, worktreeId),
            ReleaseClaimIfMatchesAsync = (gitCommonDir, worktreeId, expected) => WorkClaimStore.ReleaseIfMatchesAsync(gitCommonDir, worktreeId, expected),
            ReadAllClaimsAsync = gitCommonDir => WorkClaimStore.ReadAllAsync(gitCommonDir),
            ReconcileAsync = (_, _, _, _) => Task.FromResult(true),
            EvaluatePlanAsync = (_, _, _, _) => Task.FromResult(Plan),
            CheckClaimedWorkAsync = (_, _, _, _) => Task.FromResult(ClaimedWork),
            CloseIssueAsync = (_, issueNumber) =>
            {
                ClosedIssueNumbers.Add(issueNumber);
                return Task.CompletedTask;
            },
            SessionHost = SessionHost,
            ReadStateAsync = gitCommonDir => DaemonStateStore.ReadAsync(gitCommonDir),
            WriteStateAsync = (gitCommonDir, state) => DaemonStateStore.WriteAsync(gitCommonDir, state),
            DelayAsync = (_, _) => Task.CompletedTask
        };
    }

    private sealed class FakeSessionHost : ISessionHost
    {
        public List<(ActiveDaemonSession Session, WorkClaim Claim, string Prompt, string DaemonSessionId)> Launches { get; } = new();
        public bool Alive { get; set; } = true;

        public Task<ActiveDaemonSession> LaunchAsync(
            string worktreeDirectory,
            WorkClaim claim,
            string workIdentity,
            WorkflowItemType workItemType,
            string prompt,
            DaemonPolicy daemonPolicy,
            string daemonSessionId,
            CancellationToken cancellationToken)
        {
            var session = new ActiveDaemonSession
            {
                ClaimId = claim.ClaimId,
                WorktreeId = claim.WorktreeId,
                WorktreeDirectory = worktreeDirectory,
                ProcessId = 4000 + Launches.Count,
                WorkIdentity = workIdentity,
                WorkItemType = workItemType.ToString(),
                StartedAt = DateTimeOffset.UtcNow
            };
            Launches.Add((session, claim, prompt, daemonSessionId));
            return Task.FromResult(session);
        }

        public Task<bool> IsAliveAsync(ActiveDaemonSession session, CancellationToken cancellationToken) =>
            Task.FromResult(Alive);

        public Task StopAsync(ActiveDaemonSession session, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
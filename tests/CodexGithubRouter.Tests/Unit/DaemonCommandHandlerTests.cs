using CodexGithubRouter.Daemon;
using CodexGithubRouter.Work;
using CodexGithubRouter.Workflow;
using Xunit;

namespace CodexGithubRouter.Tests.Unit;

public class DaemonCommandHandlerTests
{
    [Fact]
    public async Task Stop_GracefullyStopsSessions_AndTerminates_IdentityVerifiedPoller()
    {
        using var sandbox = new TestSandbox();
        var pollerPid = PickUnusedPid();
        await SeedStateAsync(sandbox, pollerPid, sessionWorkIdentity: "issue #12");

        var fakeHost = new FakeSessionHost();
        var terminatedPids = new List<int>();
        var handlerDependencies = HandlerDependencies(
            sandbox,
            fakeHost,
            isProcessAliveAsync: (_, _) => Task.FromResult(true),
            terminateProcessAsync: (pid, _) =>
            {
                terminatedPids.Add(pid);
                return Task.CompletedTask;
            });

        var exitCode = await DaemonCommandHandler.HandleAsync(["stop", sandbox.RepositoryDirectory], handlerDependencies);

        Assert.Equal(0, exitCode);
        Assert.Contains(pollerPid, terminatedPids);
        Assert.Equal(new[] { "issue #12" }, fakeHost.Stops);

        var state = await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory);
        Assert.NotNull(state);
        Assert.True(state!.StopRequested);
        Assert.True(state.StoppedAt.HasValue);
    }

    [Fact]
    public async Task Stop_StopsOrphanedSessions_WithoutTerminating_ARecycledOrAbsentPoller()
    {
        using var sandbox = new TestSandbox();
        var pollerPid = PickUnusedPid();
        await SeedStateAsync(sandbox, pollerPid, sessionWorkIdentity: "issue #12");

        var fakeHost = new FakeSessionHost();
        var terminatedPids = new List<int>();
        var handlerDependencies = HandlerDependencies(
            sandbox,
            fakeHost,
            // The recorded PID is gone or was recycled to an unrelated process (identity mismatch).
            isProcessAliveAsync: (_, _) => Task.FromResult(false),
            terminateProcessAsync: (pid, _) =>
            {
                terminatedPids.Add(pid);
                return Task.CompletedTask;
            });

        var exitCode = await DaemonCommandHandler.HandleAsync(["stop", sandbox.RepositoryDirectory], handlerDependencies);

        Assert.Equal(0, exitCode);
        Assert.Empty(terminatedPids);
        Assert.Single(fakeHost.Stops);

        var state = await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory);
        Assert.NotNull(state);
        Assert.True(state!.StopRequested);
        Assert.True(state.StoppedAt.HasValue);
    }

    [Fact]
    public async Task Start_Refuses_WhenPersistedPollerIdentity_IsStillAlive()
    {
        using var sandbox = new TestSandbox();
        var pollerPid = PickUnusedPid();
        await SeedStateAsync(sandbox, pollerPid, sessionWorkIdentity: null);

        var handlerDependencies = HandlerDependencies(
            sandbox,
            new FakeSessionHost(),
            isProcessAliveAsync: (_, _) => Task.FromResult(true));

        var exitCode = await DaemonCommandHandler.HandleAsync(["start", sandbox.RepositoryDirectory], handlerDependencies);

        Assert.Equal(1, exitCode);
        var state = await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory);
        Assert.Equal(pollerPid, state!.Pid);
    }

    [Fact]
    public async Task Restart_TerminatesPreviousPoller_KeepsSession_AndRecordsFreshProcessIdentity()
    {
        using var sandbox = new TestSandbox();
        var pollerPid = PickUnusedPid();
        await SeedStateAsync(sandbox, pollerPid, sessionWorkIdentity: "issue #12");

        var fakeHost = new FakeSessionHost();
        var terminatedPids = new List<int>();
        using var cancellation = new CancellationTokenSource();
        var delayCount = 0;

        var handlerDependencies = HandlerDependencies(
            sandbox,
            fakeHost,
            isProcessAliveAsync: (_, _) => Task.FromResult(true),
            terminateProcessAsync: (pid, _) =>
            {
                terminatedPids.Add(pid);
                return Task.CompletedTask;
            },
            delayAsync: (_, _) =>
            {
                delayCount++;
                if (delayCount >= 1)
                {
                    cancellation.Cancel();
                }

                return Task.CompletedTask;
            });

        var exitCode = await DaemonCommandHandler.HandleAsync(["restart", sandbox.RepositoryDirectory], handlerDependencies, cancellation.Token);

        Assert.Equal(0, exitCode);
        Assert.Contains(pollerPid, terminatedPids);
        Assert.Single(fakeHost.Stops);

        var state = await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory);
        Assert.NotNull(state);
        Assert.Equal("stable-daemon-session", state!.DaemonSessionId);
        Assert.Equal(Environment.ProcessId, state.Pid);
        Assert.NotNull(state.PidStartTimeUtc);
        Assert.True(state.StopRequested);
        Assert.True(state.StoppedAt.HasValue);
    }

    private static DaemonExecutionDependencies HandlerDependencies(
        TestSandbox sandbox,
        FakeSessionHost sessionHost,
        Func<int, DateTimeOffset?, Task<bool>>? isProcessAliveAsync = null,
        Func<int, DateTimeOffset?, Task>? terminateProcessAsync = null,
        Func<int, CancellationToken, Task>? delayAsync = null) => new()
        {
            LoadConfigurationAsync = _ => Task.FromResult(new RouterConfiguration
            {
                Policies = new RouterPolicies
                {
                    Execution = new ExecutionPolicy { Mode = ExecutionMode.Daemon },
                    Daemon = new DaemonPolicy { IntervalSeconds = 60, FailureThreshold = 5 }
                }
            }),
            ResolveGitCommonDirectoryAsync = _ => Task.FromResult<string?>(sandbox.GitCommonDirectory),
            ResolveWorktreeDirectoriesAsync = _ => Task.FromResult<IReadOnlyList<string>>(new[] { sandbox.RepositoryDirectory }),
            ResolveWorktreeIdAsync = _ => Task.FromResult<string?>(sandbox.MainWorktreeId),
            IsAutonomousAsync = _ => Task.FromResult(true),
            IsProcessAliveAsync = isProcessAliveAsync ?? ((_, _) => Task.FromResult(false)),
            TerminateProcessAsync = terminateProcessAsync ?? ((_, _) => Task.CompletedTask),
            ReconcileAsync = (_, _, _, _) => Task.FromResult(true),
            ReadClaimAsync = (_, _) => Task.FromResult<WorkClaim?>(null),
            ReadAllClaimsAsync = _ => Task.FromResult<IReadOnlyList<WorkClaim>>(Array.Empty<WorkClaim>()),
            ReleaseClaimIfMatchesAsync = (_, _, _) => Task.FromResult(true),
            AcquireClaimAsync = _ => Task.FromResult(new WorkClaimAcquisitionOutcome()),
            EvaluatePlanAsync = (_, _, _, _) => Task.FromResult(new RoutingEvaluationResult
            {
                IsSuccessful = true,
                Decision = null,
                ActionableTasks = Array.Empty<WorkflowItem>()
            }),
            CheckClaimedWorkAsync = (_, _, _, _) => Task.FromResult(new WorkflowResponse()),
            CloseIssueAsync = (_, _) => Task.CompletedTask,
            ReadStateAsync = gitCommonDir => DaemonStateStore.ReadAsync(gitCommonDir),
            WriteStateAsync = (gitCommonDir, state) => DaemonStateStore.WriteAsync(gitCommonDir, state),
            SessionHost = sessionHost,
            DelayAsync = delayAsync ?? ((_, _) => Task.CompletedTask)
        };

    private static async Task SeedStateAsync(TestSandbox sandbox, int pollerPid, string? sessionWorkIdentity)
    {
        var activeSessions = sessionWorkIdentity is null
            ? new Dictionary<string, ActiveDaemonSession>()
            : new Dictionary<string, ActiveDaemonSession>
            {
                [WorkClaimStore.MainWorktreeIdentity] = new ActiveDaemonSession
                {
                    ClaimId = Guid.NewGuid(),
                    WorkIdentity = sessionWorkIdentity,
                    StartedAt = DateTimeOffset.UtcNow
                }
            };
        await DaemonStateStore.WriteAsync(sandbox.GitCommonDirectory, new DaemonExecutionState
        {
            DaemonSessionId = "stable-daemon-session",
            Pid = pollerPid,
            PidStartTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-30),
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            ActiveSessions = activeSessions
        });
    }

    private static int PickUnusedPid()
    {
        var pid = Environment.ProcessId == 98765 ? 98764 : 98765;
        return pid;
    }

    private sealed class FakeSessionHost : ISessionHost
    {
        public List<string> Stops { get; } = new();

        public Task<ActiveDaemonSession> LaunchAsync(
            string worktreeDirectory,
            WorkClaim claim,
            string workIdentity,
            WorkflowItemType workItemType,
            string prompt,
            DaemonPolicy daemonPolicy,
            string daemonSessionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("No session launches are expected in command-handler tests.");

        public Task<bool> IsAliveAsync(ActiveDaemonSession session, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task StopAsync(ActiveDaemonSession session, CancellationToken cancellationToken)
        {
            Stops.Add(session.WorkIdentity);
            return Task.CompletedTask;
        }
    }
}
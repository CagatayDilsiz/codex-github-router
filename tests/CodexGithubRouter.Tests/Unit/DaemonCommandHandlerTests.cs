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
        // The known session is stopped both when the acknowledgement window expires and again by the
        // final post-window sweep that guarantees nothing materialized around the boundary survives.
        Assert.Equal(2, fakeHost.Stops.Count(stop => stop == "issue #12"));

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

        var handlerDependencies = HandlerDependencies(
            sandbox,
            fakeHost,
            isProcessAliveAsync: (_, _) => Task.FromResult(true),
            terminateProcessAsync: (pid, _) =>
            {
                terminatedPids.Add(pid);
                return Task.CompletedTask;
            },
            // Cancel only once the new poller reaches its own polling-interval delay; the stop/restart
            // acknowledgement window polls at a sub-second interval and must not be mistaken for it.
            delayAsync: (milliseconds, _) =>
            {
                if (milliseconds > 1000)
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

    [Fact]
    public async Task Start_Refuses_WhenSupervisorLease_IsHeldByAnotherLiveDaemon()
    {
        using var sandbox = new TestSandbox();
        await SeedLeaseAsync(sandbox, 98765, "other-session");

        var handlerDependencies = HandlerDependencies(
            sandbox,
            new FakeSessionHost(),
            isProcessAliveAsync: (_, _) => Task.FromResult(true));

        var exitCode = await DaemonCommandHandler.HandleAsync(["start", sandbox.RepositoryDirectory], handlerDependencies);

        Assert.Equal(1, exitCode);
        Assert.Null(await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory));
    }

    [Fact]
    public async Task RunOnceCommand_Refuses_WhenSupervisorLease_IsHeldByALiveDaemon()
    {
        using var sandbox = new TestSandbox();
        await SeedLeaseAsync(sandbox, 98765, "other-session");

        var handlerDependencies = HandlerDependencies(
            sandbox,
            new FakeSessionHost(),
            isProcessAliveAsync: (_, _) => Task.FromResult(true));

        var exitCode = await DaemonCommandHandler.HandleAsync(["run", "--once", sandbox.RepositoryDirectory], handlerDependencies);

        Assert.Equal(1, exitCode);
        Assert.Null(await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory));
    }

    [Fact]
    public async Task Stop_ReleasesTheSupervisorLease()
    {
        using var sandbox = new TestSandbox();
        var pollerPid = PickUnusedPid();
        await SeedStateAsync(sandbox, pollerPid, sessionWorkIdentity: null);
        await SeedLeaseAsync(sandbox, pollerPid, "stable-daemon-session");

        var handlerDependencies = HandlerDependencies(
            sandbox,
            new FakeSessionHost(),
            isProcessAliveAsync: (_, _) => Task.FromResult(true));

        var exitCode = await DaemonCommandHandler.HandleAsync(["stop", sandbox.RepositoryDirectory], handlerDependencies);

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(DaemonSupervisorLeaseStore.GetLeasePath(sandbox.GitCommonDirectory)));
    }

    [Fact]
    public async Task Stop_DuringPendingLaunch_RequestsStop_AndReleasesLease()
    {
        using var sandbox = new TestSandbox();
        var pollerPid = PickUnusedPid();
        await SeedStateAsync(sandbox, pollerPid, sessionWorkIdentity: "issue #12", launchState: SessionLaunchState.Launching);
        await SeedLeaseAsync(sandbox, pollerPid, "stable-daemon-session");

        var handlerDependencies = HandlerDependencies(
            sandbox,
            new FakeSessionHost(),
            isProcessAliveAsync: (_, _) => Task.FromResult(true));

        var exitCode = await DaemonCommandHandler.HandleAsync(["stop", sandbox.RepositoryDirectory], handlerDependencies);

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(DaemonSupervisorLeaseStore.GetLeasePath(sandbox.GitCommonDirectory)));

        var state = await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory);
        Assert.NotNull(state);
        Assert.True(state!.StopRequested);
        Assert.True(state.StoppedAt.HasValue);
        // The interrupted-launch marker is preserved so a later start fails closed on it.
        Assert.Single(state.ActiveSessions);
        Assert.Equal(SessionLaunchState.Launching, state.ActiveSessions.Values.Single().LaunchState);
    }

    [Fact]
    public async Task Stop_DoesNotForceTerminate_WhenSupervisorAcknowledges_DuringSpawnBoundary()
    {
        using var sandbox = new TestSandbox();
        var pollerPid = PickUnusedPid();
        var childPid = pollerPid == 98765 ? 98766 : 98767;
        await SeedStateAsync(sandbox, pollerPid, sessionWorkIdentity: "issue #12", launchState: SessionLaunchState.Launching);

        var fakeHost = new FakeSessionHost();
        var terminatedPids = new List<int>();
        var simulationRan = false;

        // Simulate the supervisor finishing the spawn boundary deterministically on the first durable
        // read that observes the stop request: it materializes the child as a Running record (Phase 2),
        // stops that boundary child itself, then acknowledges by recording its own StoppedAt — all of
        // which fits inside the bounded acknowledgement window.
        var handlerDependencies = HandlerDependencies(
            sandbox,
            fakeHost,
            isProcessAliveAsync: (_, _) => Task.FromResult(true),
            terminateProcessAsync: (pid, _) =>
            {
                terminatedPids.Add(pid);
                return Task.CompletedTask;
            },
            readStateAsync: async gitCommonDir =>
            {
                var current = await DaemonStateStore.ReadAsync(gitCommonDir);
                if (current is { StopRequested: true, StoppedAt: null } && !simulationRan)
                {
                    simulationRan = true;
                    var materialized = current.ActiveSessions[WorkClaimStore.MainWorktreeIdentity] with
                    {
                        LaunchState = SessionLaunchState.Running,
                        ProcessId = childPid,
                        ProcessStartTimeUtc = DateTimeOffset.UtcNow
                    };
                    var sessions = new Dictionary<string, ActiveDaemonSession>(current.ActiveSessions)
                    {
                        [WorkClaimStore.MainWorktreeIdentity] = materialized
                    };
                    // The supervisor's Phase 2 writes the durable running record...
                    await DaemonStateStore.WriteAsync(gitCommonDir, current with { ActiveSessions = sessions });
                    // ...stops the boundary child that appeared in the unavoidable spawn boundary...
                    await fakeHost.StopAsync(materialized, CancellationToken.None);
                    // ...and acknowledges by recording its own graceful shutdown.
                    var acknowledged = current with { ActiveSessions = sessions, StoppedAt = DateTimeOffset.UtcNow };
                    await DaemonStateStore.WriteAsync(gitCommonDir, acknowledged);
                    return acknowledged;
                }

                return current;
            });

        var exitCode = await DaemonCommandHandler.HandleAsync(["stop", sandbox.RepositoryDirectory], handlerDependencies);

        Assert.Equal(0, exitCode);
        // The supervisor acknowledged within the bounded window, so it was never force-terminated.
        Assert.Empty(terminatedPids);
        // The child that appeared in the spawn boundary was deterministically stopped: no orphan survives.
        Assert.Contains("issue #12", fakeHost.Stops);

        var state = await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory);
        Assert.NotNull(state);
        Assert.True(state!.StopRequested);
        Assert.True(state.StoppedAt.HasValue);
        // The durable running record survives so a later restart can resolve/resume the session.
        Assert.Equal(SessionLaunchState.Running, state.ActiveSessions.Values.Single().LaunchState);
        Assert.Equal(childPid, state.ActiveSessions.Values.Single().ProcessId);
    }

    [Fact]
    public async Task Stop_ForceTerminatesSupervisor_WhenNoAcknowledgement_WithinBoundedWindow()
    {
        using var sandbox = new TestSandbox();
        var pollerPid = PickUnusedPid();
        await SeedStateAsync(sandbox, pollerPid, sessionWorkIdentity: "issue #12", launchState: SessionLaunchState.Launching);

        var fakeHost = new FakeSessionHost();
        var terminatedPids = new List<int>();
        var acknowledgementPollDelays = 0;

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
                acknowledgementPollDelays++;
                return Task.CompletedTask;
            });

        var exitCode = await DaemonCommandHandler.HandleAsync(["stop", sandbox.RepositoryDirectory], handlerDependencies);

        Assert.Equal(0, exitCode);
        // The supervisor never acknowledged, so the stop waited out a non-empty bounded window before
        // force-terminating it (rather than killing it immediately while it was inside the spawn
        // boundary and could still fork a child).
        Assert.True(acknowledgementPollDelays > 0, "stop must wait for an acknowledgement before force-terminating");
        Assert.Contains(pollerPid, terminatedPids);
        Assert.Contains("issue #12", fakeHost.Stops);

        var state = await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory);
        Assert.NotNull(state);
        Assert.True(state!.StopRequested);
        Assert.True(state.StoppedAt.HasValue);
        Assert.Equal(SessionLaunchState.Launching, state.ActiveSessions.Values.Single().LaunchState);
    }

    private static DaemonExecutionDependencies HandlerDependencies(
        TestSandbox sandbox,
        FakeSessionHost sessionHost,
        Func<int, DateTimeOffset?, Task<bool>>? isProcessAliveAsync = null,
        Func<int, DateTimeOffset?, Task>? terminateProcessAsync = null,
        Func<int, CancellationToken, Task>? delayAsync = null,
        Func<string, Task<DaemonExecutionState?>>? readStateAsync = null,
        Func<string, DaemonExecutionState, Task>? writeStateAsync = null,
        TimeSpan? supervisorShutdownAckTimeout = null,
        TimeSpan? supervisorShutdownAckPollInterval = null) => new()
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
            ReadStateAsync = readStateAsync ?? (gitCommonDir => DaemonStateStore.ReadAsync(gitCommonDir)),
            WriteStateAsync = writeStateAsync ?? ((gitCommonDir, state) => DaemonStateStore.WriteAsync(gitCommonDir, state)),
            SessionHost = sessionHost,
            DelayAsync = delayAsync ?? ((_, _) => Task.CompletedTask),
            SupervisorShutdownAckTimeout = supervisorShutdownAckTimeout ?? TimeSpan.FromMilliseconds(500),
            SupervisorShutdownAckPollInterval = supervisorShutdownAckPollInterval ?? TimeSpan.FromMilliseconds(10)
        };

    private static async Task SeedStateAsync(TestSandbox sandbox, int pollerPid, string? sessionWorkIdentity,
        SessionLaunchState launchState = SessionLaunchState.Running)
    {
        var activeSessions = sessionWorkIdentity is null
            ? new Dictionary<string, ActiveDaemonSession>()
            : new Dictionary<string, ActiveDaemonSession>
            {
                [WorkClaimStore.MainWorktreeIdentity] = new ActiveDaemonSession
                {
                    ClaimId = Guid.NewGuid(),
                    WorkIdentity = sessionWorkIdentity,
                    StartedAt = DateTimeOffset.UtcNow,
                    LaunchState = launchState
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

    private static async Task SeedLeaseAsync(TestSandbox sandbox, int pid, string sessionId)
    {
        await DaemonSupervisorLeaseStore.TryAcquireAsync(
            sandbox.GitCommonDirectory,
            new DaemonSupervisorLease
            {
                DaemonSessionId = sessionId,
                Pid = pid,
                PidStartTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-30),
                AcquiredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-30)
            },
            (_, _) => Task.FromResult(true));
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
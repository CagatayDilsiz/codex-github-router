using CodexGithubRouter.Autonomous;
using CodexGithubRouter.Configurations;
using CodexGithubRouter.Git;
using CodexGithubRouter.GitHub;
using CodexGithubRouter.Hooks;
using CodexGithubRouter.Prompts;
using CodexGithubRouter.Work;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Daemon;

public sealed record DaemonTickResult
{
    public string Summary { get; init; } = string.Empty;
    public int ConsecutiveFailures { get; init; }
    public bool Unhealthy { get; init; }
    public bool StopRequested { get; init; }
}

public sealed class DaemonExecutionDependencies
{
    public Func<string, Task<RouterConfiguration>> LoadConfigurationAsync { get; init; }
        = workingDirectory => WorkflowConfigurationService.LoadEffectiveOrDefaultAsync(workingDirectory);

    public Func<string, Task<string?>> ResolveGitCommonDirectoryAsync { get; init; }
        = workingDirectory => GitRepositoryService.GetCommonDirectoryAsync(workingDirectory);

    public Func<string, Task<string?>> ResolveWorktreeIdAsync { get; init; }
        = workingDirectory => GitRepositoryService.GetWorktreeIdAsync(workingDirectory);

    /// <summary>
    /// Resolves the working directories of every Git worktree the supervisor should drive. The
    /// production default enumerates the repository worktrees so the daemon preserves (rather than
    /// collapses) the parallel-work contract for linked worktrees.
    /// </summary>
    public Func<string, Task<IReadOnlyList<string>>> ResolveWorktreeDirectoriesAsync { get; init; }
        = workingDirectory => GitRepositoryService.ListWorktreesAsync(workingDirectory);

    public Func<string, Task<bool>> IsAutonomousAsync { get; init; }
        = workingDirectory => AutonomousService.IsAutonomousAsync(workingDirectory);

    /// <summary>
    /// Shared acquisition path (issue refresh + worker/assignment revalidation + claim metadata)
    /// used identically by the hook and the daemon.
    /// </summary>
    public Func<WorkClaimAcquisitionRequest, Task<WorkClaimAcquisitionOutcome>> AcquireClaimAsync { get; init; }
        = request => WorkClaimAcquisitionService.AcquireAsync(request, CancellationToken.None);

    public Func<string, string, Task<WorkClaim?>> ReadClaimAsync { get; init; }
        = (gitCommonDir, worktreeId) => WorkClaimStore.ReadAsync(gitCommonDir, worktreeId);

    public Func<string, string, WorkClaim, Task<bool>> ReleaseClaimIfMatchesAsync { get; init; }
        = (gitCommonDir, worktreeId, expected) => WorkClaimStore.ReleaseIfMatchesAsync(gitCommonDir, worktreeId, expected);

    public Func<string, Task<IReadOnlyList<WorkClaim>>> ReadAllClaimsAsync { get; init; }
        = gitCommonDir => WorkClaimStore.ReadAllAsync(gitCommonDir);

    public Func<string, string, string, RouterConfiguration, Task<bool>> ReconcileAsync { get; init; }
        = (workingDirectory, gitCommonDir, worktreeId, configuration) =>
            WorkClaimReconciliationService.ReconcileAsync(workingDirectory, gitCommonDir, worktreeId, configuration);

    public Func<RouterConfiguration, string, string?, IReadOnlyList<WorkClaim>, Task<RoutingEvaluationResult>> EvaluatePlanAsync { get; init; }
        = EvaluatePlanDefaultAsync;

    public Func<RouterConfiguration, string, WorkClaim, string?, Task<WorkflowResponse>> CheckClaimedWorkAsync { get; init; }
        = (configuration, workingDirectory, claim, model) => WorkflowService.CheckClaimedWorkAsync(configuration, workingDirectory, claim, model);

    public Func<string, int, Task> CloseIssueAsync { get; init; }
        = (workingDirectory, issueNumber) => GitHubCliService.CloseIssueAsync(workingDirectory, issueNumber);

    public ISessionHost SessionHost { get; init; } = new CodexSessionHost();

    public Func<string, Task<DaemonExecutionState?>> ReadStateAsync { get; init; }
        = gitCommonDir => DaemonStateStore.ReadAsync(gitCommonDir);

    public Func<string, DaemonExecutionState, Task> WriteStateAsync { get; init; }
        = (gitCommonDir, state) => DaemonStateStore.WriteAsync(gitCommonDir, state);

    /// <summary>PID-liveness check that also verifies the expected process start time (identity).</summary>
    public Func<int, DateTimeOffset?, Task<bool>> IsProcessAliveAsync { get; init; }
        = (pid, startTimeUtc) => Task.FromResult(ProcessIdentity.IsAlive(pid, startTimeUtc));

    /// <summary>Releases the repository supervisor lease held by the given session/process identity.</summary>
    public Func<string, string, int, Task<bool>> ReleaseSupervisorLeaseAsync { get; init; }
        = (gitCommonDir, daemonSessionId, ownerPid) =>
            DaemonSupervisorLeaseStore.ReleaseAsync(gitCommonDir, daemonSessionId, ownerPid);

    /// <summary>Identity-verified termination of a daemon supervisor process (never a kill-tree).</summary>
    public Func<int, DateTimeOffset?, Task> TerminateProcessAsync { get; init; }
        = (pid, startTimeUtc) =>
        {
            if (!ProcessIdentity.IsAlive(pid, startTimeUtc))
            {
                return Task.CompletedTask;
            }

            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                if (!process.HasExited)
                {
                    // The supervisor is terminated directly; its sessions are stopped gracefully
                    // via SessionHost.StopAsync first, so we never kill-tree unrelated work.
                    process.Kill();
                }
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
            catch (InvalidOperationException)
            {
                // Exited while being inspected.
            }

            return Task.CompletedTask;
        };

    public Func<int, CancellationToken, Task> DelayAsync { get; init; }
        = (milliseconds, cancellationToken) => Task.Delay(milliseconds, cancellationToken);

    /// <summary>
    /// Launch-critical-path seam invoked after the durable launch intent is persisted and before
    /// the durable stop-request re-check that precedes spawning a session process. Production is a
    /// no-op; tests pause here to interleave a concurrent stop/restart deterministically against an
    /// in-flight launch.
    /// </summary>
    public Func<Task> BeforeSessionLaunchAsync { get; init; } = () => Task.CompletedTask;

    private static async Task<RoutingEvaluationResult> EvaluatePlanDefaultAsync(
        RouterConfiguration configuration, string workingDirectory, string? currentModel, IReadOnlyList<WorkClaim> otherWorktreeClaims)
    {
        AssignmentIdentity? assignmentIdentity = null;
        if (AssignmentRoutingService.RequiresLocalIdentity(configuration))
        {
            var resolved = await HookService.ResolveAssignmentIdentityAsync(
                configuration, workingDirectory, new HookExecutionDependencies(), CancellationToken.None);
            assignmentIdentity = resolved.Identity;
        }

        return await RoutingEvaluationService.EvaluateAsync(
            configuration, workingDirectory,
            currentModel: currentModel,
            assignmentIdentity: assignmentIdentity,
            otherWorktreeClaims: otherWorktreeClaims);
    }
}

public static class DaemonExecutionService
{
    public static async Task<DaemonTickResult> RunOnceAsync(string workingDirectory, DaemonExecutionDependencies dependencies, CancellationToken cancellationToken)
    {
        var configuration = await dependencies.LoadConfigurationAsync(workingDirectory);
        if (!ExecutionModeService.IsDaemonOwned(configuration))
        {
            return new DaemonTickResult { Summary = "Execution mode is not daemon.", StopRequested = true };
        }

        if (!await dependencies.IsAutonomousAsync(workingDirectory))
        {
            return new DaemonTickResult { Summary = "Autonomous mode is not enabled; daemon requires autonomous mode.", StopRequested = true };
        }

        var gitCommonDir = await dependencies.ResolveGitCommonDirectoryAsync(workingDirectory)
            ?? throw new InvalidOperationException("Not a valid Git repository.");

        var readState = await dependencies.ReadStateAsync(gitCommonDir);
        var daemonSessionId = string.IsNullOrWhiteSpace(readState?.DaemonSessionId)
            ? Guid.NewGuid().ToString("N")
            : readState!.DaemonSessionId;

        readState ??= new DaemonExecutionState
        {
            DaemonSessionId = daemonSessionId,
            Pid = Environment.ProcessId,
            PidStartTimeUtc = ProcessIdentity.GetCurrentProcessStartTimeUtc(),
            StartedAt = DateTimeOffset.UtcNow
        };
        readState = readState with
        {
            DaemonSessionId = string.IsNullOrWhiteSpace(readState.DaemonSessionId) ? daemonSessionId : readState.DaemonSessionId,
            Pid = Environment.ProcessId,
            PidStartTimeUtc = ProcessIdentity.GetCurrentProcessStartTimeUtc(),
            ActiveSessions = readState.ActiveSessions ?? new Dictionary<string, ActiveDaemonSession>()
        };

        if (readState.StopRequested)
        {
            await dependencies.WriteStateAsync(gitCommonDir, readState with { StoppedAt = DateTimeOffset.UtcNow });
            return new DaemonTickResult { Summary = "Stop requested; daemon shut down cleanly.", StopRequested = true };
        }

        var worktreeDirectories = await dependencies.ResolveWorktreeDirectoriesAsync(workingDirectory);
        if (worktreeDirectories.Count == 0)
        {
            var noWorktrees = readState with
            {
                LastTickAt = DateTimeOffset.UtcNow,
                LastTickSummary = "No Git worktrees were discovered for this repository."
            };
            await dependencies.WriteStateAsync(gitCommonDir, noWorktrees);
            return new DaemonTickResult { Summary = noWorktrees.LastTickSummary };
        }

        // Resolve each worktree's OWN effective configuration (the checked-out working tree is the
        // source of the repository override) and validate repository-consistent daemon ownership
        // before supervising any of them. A linked worktree that resolves to hook execution is the
        // hook's territory: routing it from this repository daemon would race that worktree's hook,
        // which is exactly the ownership conflict #54 exists to prevent. Fail closed instead.
        var entries = new List<(string Directory, RouterConfiguration Configuration)>();
        foreach (var worktreeDirectory in worktreeDirectories)
        {
            RouterConfiguration worktreeConfiguration;
            try
            {
                worktreeConfiguration = await dependencies.LoadConfigurationAsync(worktreeDirectory);
            }
            catch (Exception exception)
            {
                return new DaemonTickResult
                {
                    Summary = $"Could not resolve effective configuration for worktree '{worktreeDirectory}' ({exception.Message}); refusing to supervise until ownership is clear."
                };
            }

            if (!ExecutionModeService.IsDaemonOwned(worktreeConfiguration))
            {
                return new DaemonTickResult
                {
                    Summary = $"Stop requested: worktree '{worktreeDirectory}' resolves to hook execution while this daemon handles '{workingDirectory}'. " +
                        "Refusing to route work that the hook owns; make execution ownership repository-consistent.",
                    StopRequested = true
                };
            }

            entries.Add((worktreeDirectory, worktreeConfiguration));
        }

        var currentState = readState;
        var summaries = new List<string>();
        var failuresThisTick = 0;
        foreach (var (worktreeDirectory, worktreeConfiguration) in entries)
        {
            var pass = await RunWorktreePassAsync(
                dependencies, worktreeDirectory, gitCommonDir, worktreeConfiguration, daemonSessionId, currentState, cancellationToken);
            currentState = pass.State;
            failuresThisTick += pass.IsFailure ? 1 : 0;
            if (!string.IsNullOrWhiteSpace(pass.Summary))
            {
                summaries.Add(pass.Summary);
            }

            if (pass.StopRequested)
            {
                // Shutdown coordination on the launch critical path: once a worktree observed a
                // concurrent stop, no further worktree may spawn a session. Acknowledge the stop,
                // persist the tick health, and let the poller shut down.
                if (summaries.Count == 0)
                {
                    summaries.Add("Stop requested during polling; no further work was started.");
                }

                return await FinalizeTickAsync(
                    dependencies, gitCommonDir, configuration, readState, currentState, summaries, failuresThisTick);
            }
        }

        return await FinalizeTickAsync(
            dependencies, gitCommonDir, configuration, readState, currentState, summaries, failuresThisTick);
    }

    private static async Task<DaemonTickResult> FinalizeTickAsync(
        DaemonExecutionDependencies dependencies,
        string gitCommonDir,
        RouterConfiguration configuration,
        DaemonExecutionState readState,
        DaemonExecutionState currentState,
        List<string> summaries,
        int failuresThisTick)
    {
        var consecutiveFailures = failuresThisTick > 0 ? readState.ConsecutiveFailures + failuresThisTick : 0;
        var finalState = currentState with
        {
            ConsecutiveFailures = consecutiveFailures,
            Unhealthy = consecutiveFailures >= configuration.Policies.Daemon.FailureThreshold,
            LastTickAt = DateTimeOffset.UtcNow,
            LastTickSummary = summaries.Count == 0 ? "No actionable workflow tasks found." : string.Join("; ", summaries)
        };
        await dependencies.WriteStateAsync(gitCommonDir, finalState);

        return new DaemonTickResult
        {
            Summary = finalState.LastTickSummary,
            ConsecutiveFailures = finalState.ConsecutiveFailures,
            Unhealthy = finalState.Unhealthy,
            StopRequested = finalState.StopRequested
        };
    }

    public static async Task RunAsync(string workingDirectory, DaemonExecutionDependencies dependencies, CancellationToken cancellationToken)
    {
        var shouldShutdown = false;
        while (!shouldShutdown && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var tick = await RunOnceAsync(workingDirectory, dependencies, cancellationToken);
                if (tick.StopRequested)
                {
                    shouldShutdown = true;
                    continue;
                }

                await dependencies.DelayAsync(await ResolveIntervalAsync(workingDirectory, dependencies) * 1000, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                shouldShutdown = true;
            }
            catch (Exception exception)
            {
                // Bounded exception-path polling: an unexpected tick failure is surfaced in
                // persisted health (so status never hides it) and the poller obeys the configured
                // cadence/backoff before retrying instead of hot-looping. The backoff is also
                // cancellation-aware: Ctrl+C during it interrupts promptly instead of waiting out
                // the full interval, and the cancellation is caught locally so the OperationCanceledException
                // cannot escape this catch block; the loop then exits and falls through to the
                // single graceful-shutdown path below.
                Console.Error.WriteLine($"Daemon tick failed: {exception.Message}");
                await PublishTickFailureAsync(workingDirectory, dependencies, exception);
                try
                {
                    await dependencies.DelayAsync(await ResolveIntervalAsync(workingDirectory, dependencies) * 1000, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    shouldShutdown = true;
                }
            }
        }

        await ShutdownGracefullyAsync(workingDirectory, dependencies, CancellationToken.None);
    }

    /// <summary>
    /// Graceful daemon shutdown: stops every supervised Codex session through the session host
    /// (never a kill-tree of the daemon process) and records the clean stop in persisted state.
    /// Claims are deliberately preserved so a later restart resumes, rather than duplicates, the
    /// interrupted work. Best-effort: an exiting daemon must never throw on bookkeeping.
    /// </summary>
    public static async Task ShutdownGracefullyAsync(string workingDirectory, DaemonExecutionDependencies dependencies, CancellationToken cancellationToken)
    {
        try
        {
            var gitCommonDir = await dependencies.ResolveGitCommonDirectoryAsync(workingDirectory);
            if (gitCommonDir is null)
            {
                return;
            }

            var state = await dependencies.ReadStateAsync(gitCommonDir);
            if (state is null)
            {
                return;
            }

            foreach (var session in state.ActiveSessions.Values)
            {
                try
                {
                    await dependencies.SessionHost.StopAsync(session, cancellationToken);
                }
                catch
                {
                    // Best-effort per-session shutdown; one stubborn process must not abort the rest.
                }
            }

            await dependencies.WriteStateAsync(gitCommonDir, state with { StopRequested = true, StoppedAt = DateTimeOffset.UtcNow });
        }
        catch
        {
            // The daemon is exiting anyway; never let shutdown bookkeeping throw.
        }
    }

    private static async Task<int> ResolveIntervalAsync(string workingDirectory, DaemonExecutionDependencies dependencies)
    {
        try
        {
            var configuration = await dependencies.LoadConfigurationAsync(workingDirectory);
            return Math.Clamp(configuration.Policies.Daemon.IntervalSeconds, 1, 3600);
        }
        catch
        {
            return 60;
        }
    }

    private static async Task PublishTickFailureAsync(string workingDirectory, DaemonExecutionDependencies dependencies, Exception exception)
    {
        try
        {
            var gitCommonDir = await dependencies.ResolveGitCommonDirectoryAsync(workingDirectory);
            if (gitCommonDir is null)
            {
                return;
            }

            var readState = await dependencies.ReadStateAsync(gitCommonDir);
            readState ??= new DaemonExecutionState
            {
                DaemonSessionId = Guid.NewGuid().ToString("N"),
                Pid = Environment.ProcessId,
                PidStartTimeUtc = ProcessIdentity.GetCurrentProcessStartTimeUtc(),
                StartedAt = DateTimeOffset.UtcNow
            };

            var threshold = 5;
            try
            {
                var configuration = await dependencies.LoadConfigurationAsync(workingDirectory);
                threshold = configuration.Policies.Daemon.FailureThreshold;
            }
            catch
            {
                // Default threshold applies when configuration cannot be reloaded.
            }

            var failures = readState.ConsecutiveFailures + 1;
            await dependencies.WriteStateAsync(gitCommonDir, readState with
            {
                Pid = Environment.ProcessId,
                PidStartTimeUtc = ProcessIdentity.GetCurrentProcessStartTimeUtc(),
                ConsecutiveFailures = failures,
                Unhealthy = failures >= threshold,
                LastTickAt = DateTimeOffset.UtcNow,
                LastTickSummary = $"Unexpected tick failure: {exception.Message}"
            });
        }
        catch
        {
            // Best-effort: never let health bookkeeping obscure the original tick failure.
        }
    }

    /// <summary>
    /// <paramref name="configuration"/> is the worktree's OWN effective configuration, resolved
    /// per worktree so a linked worktree's repository override (which may legitimately differ from
    /// the daemon's starting worktree) governs its routing, acquisition and session launch.
    /// </summary>
    private static async Task<WorktreePassResult> RunWorktreePassAsync(
        DaemonExecutionDependencies dependencies,
        string worktreeDirectory,
        string gitCommonDir,
        RouterConfiguration configuration,
        string daemonSessionId,
        DaemonExecutionState baseState,
        CancellationToken cancellationToken)
    {
        var worktreeId = await dependencies.ResolveWorktreeIdAsync(worktreeDirectory);
        if (string.IsNullOrWhiteSpace(worktreeId))
        {
            return new WorktreePassResult
            {
                State = baseState,
                Summary = $"Worktree '{worktreeDirectory}' could not be identified; skipped this cycle."
            };
        }

        try
        {
            await dependencies.ReconcileAsync(worktreeDirectory, gitCommonDir, worktreeId, configuration);
        }
        catch
        {
            // Non-destructive: a failed reconciliation must never block the poller.
        }

        var key = WorkClaimStore.NormalizeWorktreeId(gitCommonDir, worktreeId);
        var claim = await dependencies.ReadClaimAsync(gitCommonDir, worktreeId);
        var session = baseState.ActiveSessions.TryGetValue(key, out var existing) ? existing : null;

        if (claim is not null && IsDaemonOwnedClaim(claim, daemonSessionId) &&
            session is { LaunchState: SessionLaunchState.Launching })
        {
            // The daemon exited between intending to launch the process and durably recording its
            // identity, so the spawn may or may not have happened. Relaunching could duplicate the
            // claimed work, so fail closed: never auto-resume an ambiguous launch. The operator
            // repairs the state (or releases the claim) and the work resumes on a later cycle.
            return new WorktreePassResult
            {
                State = baseState,
                Summary = $"Session for {FormatWorkIdentity(claim)} is in an interrupted-launch state and was not resumed automatically to avoid duplicating the work. Repair the daemon state to continue."
            };
        }

        if (claim is not null && IsDaemonOwnedClaim(claim, daemonSessionId))
        {
            if (session is null || session.ClaimId != claim.ClaimId)
            {
                // A stale session record (the claim was released externally and re-acquired, or a
                // crash happened between acquisition and record) is cleared and the daemon-owned
                // claim resumes without re-acquiring it.
                var withoutStaleRecord = session is null
                    ? baseState
                    : baseState with { ActiveSessions = RemoveSession(baseState.ActiveSessions, key) };
                return await HandleOwnerlessClaimAsync(
                    dependencies, worktreeDirectory, gitCommonDir, worktreeId, configuration, daemonSessionId, withoutStaleRecord, claim, cancellationToken);
            }

            return await HandleActiveSessionAsync(
                dependencies, worktreeDirectory, gitCommonDir, worktreeId, configuration, daemonSessionId, baseState, claim, session, cancellationToken);
        }

        if (claim is not null)
        {
            // Another owner holds the claim for this worktree: never touch its work, but drop any
            // stale daemon session record so this worktree's slot stays clean.
            var cleared = baseState with { ActiveSessions = RemoveSession(baseState.ActiveSessions, key) };
            return new WorktreePassResult
            {
                State = cleared,
                Summary = $"Active work claim for {FormatWorkIdentity(claim)} is owned by another Codex session."
            };
        }

        if (session is not null)
        {
            // The claim was released (GitHub state went passive/terminal or it was released
            // externally) while a session record remained; clear the stale record and let ordinary
            // routing decide on the next cycle.
            var cleared = baseState with { ActiveSessions = RemoveSession(baseState.ActiveSessions, key) };
            return new WorktreePassResult
            {
                State = cleared,
                Summary = "Active session claim is no longer owned by daemon; cleared active session."
            };
        }

        return await EvaluateAndAcquireAsync(
            dependencies, worktreeDirectory, gitCommonDir, worktreeId, configuration, daemonSessionId, baseState, cancellationToken);
    }

    private static async Task<WorktreePassResult> HandleOwnerlessClaimAsync(
        DaemonExecutionDependencies dependencies,
        string worktreeDirectory,
        string gitCommonDir,
        string worktreeId,
        RouterConfiguration configuration,
        string daemonSessionId,
        DaemonExecutionState baseState,
        WorkClaim claim,
        CancellationToken cancellationToken)
    {
        var claimedWork = await dependencies.CheckClaimedWorkAsync(configuration, worktreeDirectory, claim, configuration.Policies.Daemon.Model);
        if (!claimedWork.IsSuccessful)
        {
            return new WorktreePassResult
            {
                State = baseState,
                Summary = claimedWork.Message ?? "Claimed-work refresh failed; retrying next cycle."
            };
        }

        if (IsReleaseCandidate(claimedWork))
        {
            if (!await dependencies.ReleaseClaimIfMatchesAsync(gitCommonDir, worktreeId, claim))
            {
                return new WorktreePassResult
                {
                    State = baseState,
                    Summary = $"Claim for {FormatWorkIdentity(claim)} ended in a passive/terminal state but could not be released safely."
                };
            }

            return new WorktreePassResult
            {
                State = baseState,
                Summary = $"Claim for {FormatWorkIdentity(claim)} ended in a passive/terminal state; released."
            };
        }

        var launched = await LaunchSessionAsync(
            dependencies, worktreeDirectory, gitCommonDir, baseState, configuration, daemonSessionId, claim, claimedWork, cancellationToken);
        if (launched.StopRequested || launched.Session is null)
        {
            return new WorktreePassResult
            {
                State = launched.State,
                Summary = "Stop requested while resuming the claim; the session was not launched.",
                StopRequested = true
            };
        }

        return new WorktreePassResult
        {
            State = launched.State,
            Summary = $"Resumed session for {launched.Session.WorkIdentity}."
        };
    }

    private static async Task<WorktreePassResult> HandleActiveSessionAsync(
        DaemonExecutionDependencies dependencies,
        string worktreeDirectory,
        string gitCommonDir,
        string worktreeId,
        RouterConfiguration configuration,
        string daemonSessionId,
        DaemonExecutionState baseState,
        WorkClaim claim,
        ActiveDaemonSession activeSession,
        CancellationToken cancellationToken)
    {
        var isAlive = await dependencies.SessionHost.IsAliveAsync(activeSession, cancellationToken);
        if (isAlive)
        {
            return new WorktreePassResult
            {
                State = baseState,
                Summary = $"Supervising active session for {activeSession.WorkIdentity}."
            };
        }

        var claimedWork = await dependencies.CheckClaimedWorkAsync(configuration, worktreeDirectory, claim, configuration.Policies.Daemon.Model);
        if (!claimedWork.IsSuccessful)
        {
            return new WorktreePassResult
            {
                State = baseState,
                Summary = claimedWork.Message ?? "Claimed-work refresh failed; retrying next cycle."
            };
        }

        if (IsReleaseCandidate(claimedWork))
        {
            if (!await dependencies.ReleaseClaimIfMatchesAsync(gitCommonDir, worktreeId, claim))
            {
                return new WorktreePassResult
                {
                    State = baseState,
                    Summary = $"Session for {activeSession.WorkIdentity} ended in a passive/terminal state but the claim could not be released safely."
                };
            }

            var released = baseState with { ActiveSessions = RemoveSession(baseState.ActiveSessions, WorkClaimStore.NormalizeWorktreeId(gitCommonDir, worktreeId)) };
            return new WorktreePassResult
            {
                State = released,
                Summary = $"Session for {activeSession.WorkIdentity} ended in a passive/terminal state; claim released."
            };
        }

        var resumed = await LaunchSessionAsync(
            dependencies, worktreeDirectory, gitCommonDir, baseState, configuration, daemonSessionId, claim, claimedWork, cancellationToken);
        if (resumed.StopRequested || resumed.Session is null)
        {
            return new WorktreePassResult
            {
                State = resumed.State,
                Summary = "Stop requested while resuming the session; the session was not launched.",
                StopRequested = true
            };
        }

        return new WorktreePassResult
        {
            State = resumed.State,
            Summary = $"Resumed session for {resumed.Session.WorkIdentity} after unexpected session exit."
        };
    }

    private static async Task<WorktreePassResult> EvaluateAndAcquireAsync(
        DaemonExecutionDependencies dependencies,
        string worktreeDirectory,
        string gitCommonDir,
        string worktreeId,
        RouterConfiguration configuration,
        string daemonSessionId,
        DaemonExecutionState baseState,
        CancellationToken cancellationToken)
    {
        var key = WorkClaimStore.NormalizeWorktreeId(gitCommonDir, worktreeId);
        var allClaims = await dependencies.ReadAllClaimsAsync(gitCommonDir);
        var otherWorktreeClaims = allClaims
            .Where(claim => !string.Equals(WorkClaimStore.NormalizeWorktreeId(gitCommonDir, claim.WorktreeId), key, StringComparison.Ordinal))
            .ToList();

        var plan = await dependencies.EvaluatePlanAsync(configuration, worktreeDirectory, configuration.Policies.Daemon.Model, otherWorktreeClaims);
        if (!plan.IsSuccessful)
        {
            return new WorktreePassResult
            {
                State = baseState,
                Summary = plan.DiscoveryFailureMessage ?? "Routing evaluation failed.",
                IsFailure = true
            };
        }

        if (plan.Decision?.SelectedTask is null || !string.IsNullOrWhiteSpace(plan.BlockReason))
        {
            return new WorktreePassResult
            {
                State = baseState,
                Summary = string.IsNullOrWhiteSpace(plan.BlockReason) ? "No actionable workflow tasks found." : plan.BlockReason
            };
        }

        // Idempotent mechanical actions: close issues marked for closure before evaluating claims
        // so newly-closed work never stalls routing.
        foreach (var closingTask in plan.ActionableTasks.Where(task => task.Type == WorkflowItemType.CloseIssue && task.IssueNumber.HasValue))
        {
            try
            {
                await dependencies.CloseIssueAsync(worktreeDirectory, closingTask.IssueNumber!.Value);
            }
            catch
            {
                // Non-destructive: a failing close must not prevent the daemon from routing other work.
            }
        }

        var selectedTask = plan.Decision.SelectedTask;
        if (!HookTaskRouter.RequiresWorkClaim(selectedTask))
        {
            return new WorktreePassResult
            {
                State = baseState,
                Summary = $"Actionable task ({selectedTask.Type}) does not require a claim; skipped for safety."
            };
        }

        var acquisition = await dependencies.AcquireClaimAsync(new WorkClaimAcquisitionRequest
        {
            WorkingDirectory = worktreeDirectory,
            GitCommonDirectory = gitCommonDir,
            WorktreeId = worktreeId,
            OwnerSessionId = daemonSessionId,
            Configuration = configuration,
            AssignmentIdentity = plan.AssignmentIdentity,
            HasRepositoryGate = plan.HasRepositoryGate,
            SelectedTask = selectedTask,
            Model = configuration.Policies.Daemon.Model
        });
        if (!acquisition.Acquired || acquisition.Claim is null)
        {
            return new WorktreePassResult
            {
                State = baseState,
                Summary = acquisition.BlockReason ?? "Could not acquire the work claim."
            };
        }

        var claim = acquisition.Claim;
        var refreshedWork = await dependencies.CheckClaimedWorkAsync(configuration, worktreeDirectory, claim, configuration.Policies.Daemon.Model);
        if (!refreshedWork.IsSuccessful)
        {
            _ = await dependencies.ReleaseClaimIfMatchesAsync(gitCommonDir, worktreeId, claim);
            return new WorktreePassResult
            {
                State = baseState,
                Summary = refreshedWork.Message ?? "Claimed-work refresh failed after acquiring the claim; re-acquisition allowed on the next cycle."
            };
        }

        if (IsReleaseCandidate(refreshedWork))
        {
            _ = await dependencies.ReleaseClaimIfMatchesAsync(gitCommonDir, worktreeId, claim);
            return new WorktreePassResult
            {
                State = baseState,
                Summary = "Acquired work became passive or terminal during refresh and was released."
            };
        }

        var launched = await LaunchSessionAsync(
            dependencies, worktreeDirectory, gitCommonDir, baseState, configuration, daemonSessionId, claim, refreshedWork, cancellationToken);
        if (launched.StopRequested || launched.Session is null)
        {
            return new WorktreePassResult
            {
                State = launched.State,
                Summary = "Stop requested after acquiring the claim; the session was not launched.",
                StopRequested = true
            };
        }

        return new WorktreePassResult
        {
            State = launched.State,
            Summary = $"Launched session for {launched.Session.WorkIdentity}."
        };
    }

    private static async Task<(DaemonExecutionState State, ActiveDaemonSession? Session, bool StopRequested)> LaunchSessionAsync(
        DaemonExecutionDependencies dependencies,
        string worktreeDirectory,
        string gitCommonDir,
        DaemonExecutionState baseState,
        RouterConfiguration configuration,
        string daemonSessionId,
        WorkClaim claim,
        WorkflowResponse claimedWork,
        CancellationToken cancellationToken)
    {
        var workIdentity = FormatWorkIdentity(claim);
        var selectedTask = claimedWork.Tasks.FirstOrDefault();
        var prompt = selectedTask is not null ? ContextPromptService.GetPromptForTask(selectedTask) : string.Empty;
        var model = configuration.Policies.Daemon.Model;
        var key = WorkClaimStore.NormalizeWorktreeId(gitCommonDir, claim.WorktreeId);
        var launchingSession = new ActiveDaemonSession
        {
            ClaimId = claim.ClaimId,
            WorktreeId = claim.WorktreeId,
            WorktreeDirectory = worktreeDirectory,
            WorkIdentity = workIdentity,
            WorkItemType = (selectedTask?.Type ?? WorkflowItemType.Unknown).ToString(),
            Model = model,
            StartedAt = DateTimeOffset.UtcNow,
            LaunchState = SessionLaunchState.Launching
        };

        // Phase 1: persist the launch INTENT before the process exists. We merge into the durable
        // state as it is on disk rather than overwriting from the tick-start snapshot, so a concurrent
        // stop's StopRequested flag is never lost under the intent write. A crash after acquisition
        // but before the spawn leaves a plain daemon-owned claim (safe to resume on restart), while
        // a crash between the spawn and the durable running record below leaves this ambiguous
        // Launching marker, so a restart can never launch a duplicate process for the same claim.
        var beforeIntent = await dependencies.ReadStateAsync(gitCommonDir) ?? baseState;
        var intentSessions = new Dictionary<string, ActiveDaemonSession>(beforeIntent.ActiveSessions)
        {
            [key] = launchingSession
        };
        var intentState = beforeIntent with { ActiveSessions = intentSessions };
        await dependencies.WriteStateAsync(gitCommonDir, intentState);

        // Launch-critical-path seam (production no-op, testable pause).
        await dependencies.BeforeSessionLaunchAsync();

        // Shutdown coordination on the launch critical path: request stop → the supervisor observes
        // the stop before any further spawn. Re-read durable state immediately before spawning and
        // abort when a concurrent stop/restart persisted StopRequested after the intent write above.
        // This guarantees no child can be created after a stop has been durably requested, except in
        // the unavoidable spawn boundary between the check and SessionHost.LaunchAsync, where a
        // child that does appear is deterministically stopped in Phase 2 below.
        var preSpawn = await dependencies.ReadStateAsync(gitCommonDir) ?? intentState;
        if (preSpawn.StopRequested)
        {
            return (preSpawn, null, StopRequested: true);
        }

        var session = await dependencies.SessionHost.LaunchAsync(
            worktreeDirectory,
            claim,
            workIdentity,
            selectedTask?.Type ?? WorkflowItemType.Unknown,
            prompt,
            configuration.Policies.Daemon,
            daemonSessionId,
            cancellationToken);

        // Phase 2: persist the durable running record (PID + start time) once the spawn succeeded.
        // We re-read and merge into the current durable state so concurrent state (most importantly
        // a StopRequested that arrived while the child was starting) is never lost under this write;
        // if a stop did arrive, the child was created in the unavoidable spawn boundary and is
        // stopped deterministically before the supervisor can exit.
        var postSpawn = await dependencies.ReadStateAsync(gitCommonDir) ?? preSpawn;
        var runningSessions = new Dictionary<string, ActiveDaemonSession>(postSpawn.ActiveSessions)
        {
            [key] = session with { LaunchState = SessionLaunchState.Running }
        };
        var runningState = postSpawn with { ActiveSessions = runningSessions };
        await dependencies.WriteStateAsync(gitCommonDir, runningState);

        if (runningState.StopRequested)
        {
            try
            {
                await dependencies.SessionHost.StopAsync(session, cancellationToken);
            }
            catch
            {
                // Best-effort: stopping the child in the shutdown boundary must not fail the launch path.
            }
        }

        return (runningState, session, StopRequested: false);
    }

    private static bool IsDaemonOwnedClaim(WorkClaim claim, string daemonSessionId) =>
        string.Equals(claim.OwnerSessionId, daemonSessionId, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, ActiveDaemonSession> RemoveSession(IReadOnlyDictionary<string, ActiveDaemonSession> sessions, string key)
    {
        if (!sessions.ContainsKey(key))
        {
            return sessions;
        }

        var updated = new Dictionary<string, ActiveDaemonSession>(sessions);
        updated.Remove(key);
        return updated;
    }

    private static bool IsReleaseCandidate(WorkflowResponse response) =>
        response.Tasks.Count == 1 && response.Tasks[0].Type is
            WorkflowItemType.AwaitingReview or
            WorkflowItemType.AwaitingMerge or
            WorkflowItemType.Deferred or
            WorkflowItemType.CloseIssue or
            WorkflowItemType.ClosedWithoutMerge;

    internal static string FormatWorkIdentity(WorkClaim claim) =>
        claim.WorkType == WorkClaimType.Review
            ? $"review of pull request #{claim.PullRequestNumber} (reviewer '{claim.ReviewerLogin}')"
            : $"issue #{claim.IssueNumber}{(claim.PullRequestNumber.HasValue ? $" / pull request #{claim.PullRequestNumber.Value}" : string.Empty)}";

    private sealed record WorktreePassResult
    {
        public DaemonExecutionState State { get; init; } = null!;
        public string Summary { get; init; } = string.Empty;
        public bool IsFailure { get; init; }
        public bool StopRequested { get; init; }
    }
}
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

        var currentState = readState;
        var summaries = new List<string>();
        var failuresThisTick = 0;
        foreach (var worktreeDirectory in worktreeDirectories)
        {
            var pass = await RunWorktreePassAsync(
                dependencies, worktreeDirectory, gitCommonDir, configuration, daemonSessionId, currentState, cancellationToken);
            currentState = pass.State;
            failuresThisTick += pass.IsFailure ? 1 : 0;
            if (!string.IsNullOrWhiteSpace(pass.Summary))
            {
                summaries.Add(pass.Summary);
            }
        }

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
            Unhealthy = finalState.Unhealthy
        };
    }

    public static async Task RunAsync(string workingDirectory, DaemonExecutionDependencies dependencies, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var tick = await RunOnceAsync(workingDirectory, dependencies, cancellationToken);
                if (tick.StopRequested || cancellationToken.IsCancellationRequested)
                {
                    await ShutdownGracefullyAsync(workingDirectory, dependencies, cancellationToken);
                    break;
                }

                await dependencies.DelayAsync(await ResolveIntervalAsync(workingDirectory, dependencies) * 1000, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await ShutdownGracefullyAsync(workingDirectory, dependencies, CancellationToken.None);
                break;
            }
            catch (Exception exception)
            {
                // Bounded exception-path polling: an unexpected tick failure is surfaced in
                // persisted health (so status never hides it) and the poller always obeys the
                // configured cadence/backoff before retrying instead of hot-looping.
                Console.Error.WriteLine($"Daemon tick failed: {exception.Message}");
                await PublishTickFailureAsync(workingDirectory, dependencies, exception);
                await dependencies.DelayAsync(await ResolveIntervalAsync(workingDirectory, dependencies) * 1000, cancellationToken);
            }
        }
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
        return new WorktreePassResult
        {
            State = launched.State,
            Summary = $"Launched session for {launched.Session.WorkIdentity}."
        };
    }

    private static async Task<(DaemonExecutionState State, ActiveDaemonSession Session)> LaunchSessionAsync(
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
        var session = await dependencies.SessionHost.LaunchAsync(
            worktreeDirectory,
            claim,
            workIdentity,
            selectedTask?.Type ?? WorkflowItemType.Unknown,
            prompt,
            configuration.Policies.Daemon,
            daemonSessionId,
            cancellationToken);

        var key = WorkClaimStore.NormalizeWorktreeId(gitCommonDir, claim.WorktreeId);
        var sessions = new Dictionary<string, ActiveDaemonSession>(baseState.ActiveSessions) { [key] = session };
        return (baseState with { ActiveSessions = sessions }, session);
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
    }
}
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

    public Func<string, Task<bool>> IsAutonomousAsync { get; init; }
        = workingDirectory => AutonomousService.IsAutonomousAsync(workingDirectory);

    public Func<string, string, WorkClaim, Task<WorkClaimAcquisitionResult>> TryAcquireClaimAsync { get; init; }
        = (gitCommonDir, worktreeId, claimed) => WorkClaimStore.TryAcquireAsync(gitCommonDir, worktreeId, claimed);

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
        var worktreeId = await dependencies.ResolveWorktreeIdAsync(workingDirectory)
            ?? throw new InvalidOperationException("Not a valid Git repository.");

        try
        {
            await dependencies.ReconcileAsync(workingDirectory, gitCommonDir, worktreeId, configuration);
        }
        catch
        {
            // Non-destructive: a failed reconciliation must never block the poller.
        }

        var readState = await dependencies.ReadStateAsync(gitCommonDir);
        var daemonSessionId = readState?.DaemonSessionId;
        if (string.IsNullOrWhiteSpace(daemonSessionId))
        {
            daemonSessionId = Guid.NewGuid().ToString("N");
        }

        readState = readState is null
            ? new DaemonExecutionState
            {
                DaemonSessionId = daemonSessionId,
                Pid = Environment.ProcessId,
                StartedAt = DateTimeOffset.UtcNow
            }
            : string.IsNullOrWhiteSpace(readState.DaemonSessionId)
                ? readState with { DaemonSessionId = daemonSessionId }
                : readState;

        if (readState.StopRequested)
        {
            await dependencies.WriteStateAsync(gitCommonDir, readState with { StoppedAt = DateTimeOffset.UtcNow });
            return new DaemonTickResult { Summary = "Stop requested; daemon shut down cleanly.", StopRequested = true };
        }

        if (readState.ActiveSession is { } activeSession)
        {
            return await HandleActiveSessionAsync(
                dependencies, workingDirectory, gitCommonDir, worktreeId, configuration, daemonSessionId, readState, activeSession, cancellationToken);
        }

        var allClaims = await dependencies.ReadAllClaimsAsync(gitCommonDir);
        var otherWorktreeClaims = allClaims
            .Where(claim => !IsDaemonOwnedClaim(claim, gitCommonDir, worktreeId, daemonSessionId))
            .ToList();

        var plan = await dependencies.EvaluatePlanAsync(configuration, workingDirectory, configuration.Policies.Daemon.Model, otherWorktreeClaims);
        if (!plan.IsSuccessful)
        {
            var failed = readState with
            {
                ConsecutiveFailures = readState.ConsecutiveFailures + 1,
                Unhealthy = readState.ConsecutiveFailures + 1 >= configuration.Policies.Daemon.FailureThreshold,
                LastTickAt = DateTimeOffset.UtcNow,
                LastTickSummary = plan.DiscoveryFailureMessage ?? "Routing evaluation failed."
            };
            await dependencies.WriteStateAsync(gitCommonDir, failed);
            return new DaemonTickResult { Summary = failed.LastTickSummary, ConsecutiveFailures = failed.ConsecutiveFailures, Unhealthy = failed.Unhealthy };
        }

        if (plan.Decision?.SelectedTask is null || !string.IsNullOrWhiteSpace(plan.BlockReason))
        {
            var idle = readState with
            {
                ConsecutiveFailures = 0,
                Unhealthy = false,
                LastTickAt = DateTimeOffset.UtcNow,
                LastTickSummary = string.IsNullOrWhiteSpace(plan.BlockReason) ? "No actionable workflow tasks found." : plan.BlockReason
            };
            await dependencies.WriteStateAsync(gitCommonDir, idle);
            return new DaemonTickResult { Summary = idle.LastTickSummary };
        }

        // Idempotent mechanical actions: close issues marked for closure before evaluating claims
        // so newly-closed work never stalls routing.
        foreach (var closingTask in plan.ActionableTasks.Where(task => task.Type == WorkflowItemType.CloseIssue && task.IssueNumber.HasValue))
        {
            try
            {
                await dependencies.CloseIssueAsync(workingDirectory, closingTask.IssueNumber!.Value);
            }
            catch
            {
                // Non-destructive: a failing close must not prevent the daemon from routing other work.
            }
        }

        var selectedTask = plan.Decision.SelectedTask;
        if (!HookTaskRouter.RequiresWorkClaim(selectedTask))
        {
            var skipped = readState with
            {
                ConsecutiveFailures = 0,
                Unhealthy = false,
                LastTickAt = DateTimeOffset.UtcNow,
                LastTickSummary = $"Actionable task ({selectedTask.Type}) does not require a claim; skipped for safety."
            };
            await dependencies.WriteStateAsync(gitCommonDir, skipped);
            return new DaemonTickResult { Summary = skipped.LastTickSummary };
        }

        return await AcquireAndLaunchAsync(
            dependencies, workingDirectory, gitCommonDir, worktreeId, configuration, daemonSessionId, readState, selectedTask, cancellationToken);
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
                    break;
                }

                var intervalSeconds = 60;
                try
                {
                    var configuration = await dependencies.LoadConfigurationAsync(workingDirectory);
                    intervalSeconds = Math.Clamp(configuration.Policies.Daemon.IntervalSeconds, 1, 3600);
                }
                catch
                {
                    // Best-effort: fall back to the default interval when configuration cannot be reloaded.
                }

                await dependencies.DelayAsync(intervalSeconds * 1000, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Daemon tick failed: {exception.Message}");
            }
        }
    }

    private static async Task<DaemonTickResult> HandleActiveSessionAsync(
        DaemonExecutionDependencies dependencies,
        string workingDirectory,
        string gitCommonDir,
        string worktreeId,
        RouterConfiguration configuration,
        string daemonSessionId,
        DaemonExecutionState readState,
        ActiveDaemonSession activeSession,
        CancellationToken cancellationToken)
    {
        var claim = await dependencies.ReadClaimAsync(gitCommonDir, worktreeId);
        var claimOwnedByDaemon = claim is not null &&
            string.Equals(claim.ClaimId.ToString("N"), activeSession.ClaimId.ToString("N"), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(claim.OwnerSessionId, daemonSessionId, StringComparison.OrdinalIgnoreCase);

        if (claim is null || !claimOwnedByDaemon)
        {
            var cleared = readState with
            {
                ActiveSession = null,
                ConsecutiveFailures = 0,
                Unhealthy = false,
                LastTickAt = DateTimeOffset.UtcNow,
                LastTickSummary = "Active session claim is no longer owned by daemon; cleared active session."
            };
            await dependencies.WriteStateAsync(gitCommonDir, cleared);
            return new DaemonTickResult { Summary = cleared.LastTickSummary };
        }

        var isAlive = await dependencies.SessionHost.IsAliveAsync(activeSession, cancellationToken);
        if (isAlive)
        {
            var supervising = readState with
            {
                LastTickAt = DateTimeOffset.UtcNow,
                LastTickSummary = $"Supervising active session for {activeSession.WorkIdentity}."
            };
            await dependencies.WriteStateAsync(gitCommonDir, supervising);
            return new DaemonTickResult { Summary = supervising.LastTickSummary };
        }

        var claimedWork = await dependencies.CheckClaimedWorkAsync(configuration, workingDirectory, claim, configuration.Policies.Daemon.Model);
        if (!claimedWork.IsSuccessful)
        {
            var unknown = readState with
            {
                LastTickAt = DateTimeOffset.UtcNow,
                LastTickSummary = claimedWork.Message ?? "Claimed-work refresh failed; retrying next cycle."
            };
            await dependencies.WriteStateAsync(gitCommonDir, unknown);
            return new DaemonTickResult { Summary = unknown.LastTickSummary };
        }

        if (IsReleaseCandidate(claimedWork))
        {
            if (!await dependencies.ReleaseClaimIfMatchesAsync(gitCommonDir, worktreeId, claim))
            {
                var failedRelease = readState with
                {
                    LastTickAt = DateTimeOffset.UtcNow,
                    LastTickSummary = $"Session for {activeSession.WorkIdentity} ended in a passive/terminal state but the claim could not be released safely."
                };
                await dependencies.WriteStateAsync(gitCommonDir, failedRelease);
                return new DaemonTickResult { Summary = failedRelease.LastTickSummary };
            }

            var released = readState with
            {
                ActiveSession = null,
                ConsecutiveFailures = 0,
                Unhealthy = false,
                LastTickAt = DateTimeOffset.UtcNow,
                LastTickSummary = $"Session for {activeSession.WorkIdentity} ended in a passive/terminal state; claim released."
            };
            await dependencies.WriteStateAsync(gitCommonDir, released);
            return new DaemonTickResult { Summary = released.LastTickSummary };
        }

        var resumed = await LaunchSessionAsync(dependencies, workingDirectory, gitCommonDir, configuration, daemonSessionId, claim, claimedWork, cancellationToken);
        return new DaemonTickResult { Summary = $"Resumed session for {resumed.ActiveSession!.WorkIdentity} after unexpected session exit." };
    }

    private static async Task<DaemonTickResult> AcquireAndLaunchAsync(
        DaemonExecutionDependencies dependencies,
        string workingDirectory,
        string gitCommonDir,
        string worktreeId,
        RouterConfiguration configuration,
        string daemonSessionId,
        DaemonExecutionState readState,
        WorkflowItem selectedTask,
        CancellationToken cancellationToken)
    {
        var claimType = selectedTask.Type == WorkflowItemType.ChangeRequest
            ? WorkClaimType.ChangeRequest
            : selectedTask.Type == WorkflowItemType.PullRequestReview
                ? WorkClaimType.Review
                : WorkClaimType.Implementation;

        var acquisition = await dependencies.TryAcquireClaimAsync(
            gitCommonDir,
            worktreeId,
            new WorkClaim
            {
                OwnerSessionId = daemonSessionId,
                IssueNumber = selectedTask.IssueNumber,
                PullRequestNumber = selectedTask.PullRequestNumber,
                WorkType = claimType,
                ReviewerLogin = selectedTask.ReviewerLogin,
                ReviewCycleId = selectedTask.ReviewCycleId,
                ReviewBaselineCaptured = claimType == WorkClaimType.Review,
                ClaimedIssueUpdatedAt = DateTimeOffset.UtcNow
            });

        if (!acquisition.Acquired)
        {
            var blocked = readState with
            {
                ConsecutiveFailures = 0,
                Unhealthy = false,
                LastTickAt = DateTimeOffset.UtcNow,
                LastTickSummary = acquisition.BlockReason ?? "Could not acquire the work claim."
            };
            await dependencies.WriteStateAsync(gitCommonDir, blocked);
            return new DaemonTickResult { Summary = blocked.LastTickSummary };
        }

        var claim = acquisition.Claim!;
        var refreshedWork = await dependencies.CheckClaimedWorkAsync(configuration, workingDirectory, claim, configuration.Policies.Daemon.Model);
        if (!refreshedWork.IsSuccessful)
        {
            _ = await dependencies.ReleaseClaimIfMatchesAsync(gitCommonDir, worktreeId, claim);
            var refreshFailed = readState with
            {
                ConsecutiveFailures = 0,
                Unhealthy = false,
                LastTickAt = DateTimeOffset.UtcNow,
                LastTickSummary = refreshedWork.Message ?? "Claimed-work refresh failed after acquiring the claim; re-acquisition allowed on the next cycle."
            };
            await dependencies.WriteStateAsync(gitCommonDir, refreshFailed);
            return new DaemonTickResult { Summary = refreshFailed.LastTickSummary };
        }

        if (IsReleaseCandidate(refreshedWork))
        {
            _ = await dependencies.ReleaseClaimIfMatchesAsync(gitCommonDir, worktreeId, claim);
            var released = readState with
            {
                ConsecutiveFailures = 0,
                Unhealthy = false,
                LastTickAt = DateTimeOffset.UtcNow,
                LastTickSummary = "Acquired work became passive or terminal during refresh and was released."
            };
            await dependencies.WriteStateAsync(gitCommonDir, released);
            return new DaemonTickResult { Summary = released.LastTickSummary };
        }

        var launched = await LaunchSessionAsync(dependencies, workingDirectory, gitCommonDir, configuration, daemonSessionId, claim, refreshedWork, cancellationToken);
        return new DaemonTickResult { Summary = $"Launched session for {launched.ActiveSession!.WorkIdentity}." };
    }

    private static async Task<DaemonExecutionState> LaunchSessionAsync(
        DaemonExecutionDependencies dependencies,
        string workingDirectory,
        string gitCommonDir,
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
            workingDirectory,
            claim,
            workIdentity,
            selectedTask?.Type ?? WorkflowItemType.Unknown,
            prompt,
            configuration.Policies.Daemon,
            daemonSessionId,
            cancellationToken);

        var state = new DaemonExecutionState
        {
            DaemonSessionId = daemonSessionId,
            Pid = Environment.ProcessId,
            StartedAt = DateTimeOffset.UtcNow,
            ConsecutiveFailures = 0,
            Unhealthy = false,
            StopRequested = false,
            ActiveSession = session,
            LastTickAt = DateTimeOffset.UtcNow
        };
        await dependencies.WriteStateAsync(gitCommonDir, state);
        return state;
    }

    private static bool IsDaemonOwnedClaim(WorkClaim claim, string gitCommonDir, string worktreeId, string daemonSessionId) =>
        string.Equals(WorkClaimStore.NormalizeWorktreeId(gitCommonDir, claim.WorktreeId), WorkClaimStore.NormalizeWorktreeId(gitCommonDir, worktreeId), StringComparison.Ordinal) &&
        string.Equals(claim.OwnerSessionId, daemonSessionId, StringComparison.OrdinalIgnoreCase);

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
}
using CodexGithubRouter.Daemon;
using CodexGithubRouter.GitHub;
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
        Assert.Single(state!.ActiveSessions);
        Assert.Equal(context.SessionHost.Launches[0].Session.ClaimId, state.ActiveSessions.Values.Single().ClaimId);
        Assert.Contains(WorkClaimStore.MainWorktreeIdentity, state.ActiveSessions.Keys);

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
    public async Task RunOnce_ResumesDaemonOwnedClaim_WithoutReacquiring_WhenSessionRecordIsMissing()
    {
        using var sandbox = new TestSandbox();
        var context = new DaemonTestContext(sandbox);
        context.ClaimedWork = new WorkflowResponse
        {
            Tasks = { new WorkflowItem { Type = WorkflowItemType.ResumeInProgressIssue, IssueNumber = 12 } }
        };

        // Crash between acquisition and record: the daemon owns the claim but has no session record.
        await DaemonStateStore.WriteAsync(sandbox.GitCommonDirectory, new DaemonExecutionState { DaemonSessionId = "owning-daemon" });
        await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId, new WorkClaim
        {
            OwnerSessionId = "owning-daemon",
            IssueNumber = 12,
            WorkType = WorkClaimType.Implementation
        });

        var tick = await DaemonExecutionService.RunOnceAsync(sandbox.RepositoryDirectory, context.Dependencies, CancellationToken.None);

        Assert.False(tick.StopRequested);
        Assert.StartsWith("Resumed session for issue #12", tick.Summary, StringComparison.Ordinal);
        Assert.Single(context.SessionHost.Launches);
        Assert.Equal(0, context.AcquireCalls);

        var claims = await WorkClaimStore.ReadAllAsync(sandbox.GitCommonDirectory);
        Assert.Single(claims);
        Assert.Equal("owning-daemon", claims[0].OwnerSessionId);
        var state = await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory);
        Assert.Single(state!.ActiveSessions);
        Assert.Equal("owning-daemon", state.DaemonSessionId);
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
        Assert.Empty(state!.ActiveSessions);
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
    public async Task RunOnce_DispatchesIndependentWork_AcrossLinkedWorktrees()
    {
        using var sandbox = new TestSandbox();
        var linkedGitDirectory = sandbox.CreateLinkedWorktree("linked");
        var linkedDirectory = Path.Combine(sandbox.Root, "linked");
        Directory.CreateDirectory(linkedDirectory);

        var context = new DaemonTestContext(sandbox);
        context.WorktreeDirectories = new List<string> { sandbox.RepositoryDirectory, linkedDirectory };
        context.WorktreeIdentityByDirectory[sandbox.RepositoryDirectory] = sandbox.MainWorktreeId;
        context.WorktreeIdentityByDirectory[linkedDirectory] = linkedGitDirectory;
        context.PlanResolver = directory =>
            string.Equals(directory, sandbox.RepositoryDirectory, StringComparison.Ordinal)
                ? SuccessfulPlan(new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 10 })
                : SuccessfulPlan(new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 20 });

        var tick = await DaemonExecutionService.RunOnceAsync(sandbox.RepositoryDirectory, context.Dependencies, CancellationToken.None);

        Assert.False(tick.StopRequested);
        Assert.Equal(2, context.AcquireCalls);
        Assert.Equal(2, context.SessionHost.Launches.Count);
        Assert.Contains("issue #10", tick.Summary, StringComparison.Ordinal);
        Assert.Contains("issue #20", tick.Summary, StringComparison.Ordinal);
        Assert.Equal(
            new[] { 10, 20 },
            context.SessionHost.Launches.Select(launch => launch.Claim.IssueNumber!.Value).OrderBy(number => number).ToArray());
        Assert.Equal(2, context.SessionHost.Launches.Select(launch => launch.Session.WorktreeDirectory).Distinct().Count());

        var claims = await WorkClaimStore.ReadAllAsync(sandbox.GitCommonDirectory);
        Assert.Equal(2, claims.Count);
        Assert.Equal(2, claims.Select(claim => claim.WorktreeId).Distinct().Count());

        var state = await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory);
        Assert.NotNull(state);
        Assert.Equal(2, state!.ActiveSessions.Count);
        Assert.Equal(2, state.ActiveSessions.Values.Select(session => session.ClaimId).Distinct().Count());
    }

    [Fact]
    public async Task RunOnce_UsesEachWorktrees_OwnEffectiveConfiguration()
    {
        using var sandbox = new TestSandbox();
        var linkedGitDirectory = sandbox.CreateLinkedWorktree("linked");
        var linkedDirectory = Path.Combine(sandbox.Root, "linked");
        Directory.CreateDirectory(linkedDirectory);

        var context = new DaemonTestContext(sandbox);
        context.WorktreeDirectories = new List<string> { sandbox.RepositoryDirectory, linkedDirectory };
        context.WorktreeIdentityByDirectory[sandbox.RepositoryDirectory] = sandbox.MainWorktreeId;
        context.WorktreeIdentityByDirectory[linkedDirectory] = linkedGitDirectory;
        context.PlanResolver = directory =>
            string.Equals(directory, sandbox.RepositoryDirectory, StringComparison.Ordinal)
                ? SuccessfulPlan(new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 10 })
                : SuccessfulPlan(new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 20 });
        context.ConfigurationResolver = directory =>
        {
            var model = string.Equals(directory, sandbox.RepositoryDirectory, StringComparison.Ordinal) ? "codex-a" : "codex-b";
            return new RouterConfiguration
            {
                Policies = new RouterPolicies
                {
                    Execution = new ExecutionPolicy { Mode = ExecutionMode.Daemon },
                    Daemon = new DaemonPolicy { IntervalSeconds = 60, FailureThreshold = 5, Model = model }
                }
            };
        };

        var tick = await DaemonExecutionService.RunOnceAsync(sandbox.RepositoryDirectory, context.Dependencies, CancellationToken.None);

        Assert.False(tick.StopRequested);
        Assert.Equal(2, context.SessionHost.Launches.Count);
        var modelByIssue = context.SessionHost.Launches.ToDictionary(launch => launch.Claim.IssueNumber!.Value, launch => launch.Session.Model);
        Assert.Equal(new[] { 10, 20 }, modelByIssue.Keys.OrderBy(number => number).ToArray());
        Assert.Equal("codex-a", modelByIssue[10]);
        Assert.Equal("codex-b", modelByIssue[20]);

        var claims = await WorkClaimStore.ReadAllAsync(sandbox.GitCommonDirectory);
        Assert.Equal(2, claims.Count);
        Assert.Equal("codex-a", claims.Single(claim => claim.IssueNumber == 10).Model);
        Assert.Equal("codex-b", claims.Single(claim => claim.IssueNumber == 20).Model);
    }

    [Fact]
    public async Task RunOnce_StopsTheDaemon_WhenAWorktreeResolvesToHookExecution()
    {
        using var sandbox = new TestSandbox();
        var linkedGitDirectory = sandbox.CreateLinkedWorktree("linked");
        var linkedDirectory = Path.Combine(sandbox.Root, "linked");
        Directory.CreateDirectory(linkedDirectory);

        var context = new DaemonTestContext(sandbox);
        context.WorktreeDirectories = new List<string> { sandbox.RepositoryDirectory, linkedDirectory };
        context.WorktreeIdentityByDirectory[sandbox.RepositoryDirectory] = sandbox.MainWorktreeId;
        context.WorktreeIdentityByDirectory[linkedDirectory] = linkedGitDirectory;
        context.ConfigurationResolver = directory =>
        {
            var mode = string.Equals(directory, linkedDirectory, StringComparison.Ordinal)
                // The linked worktree's repository override hands it to the hook while the daemon's
                // own worktree stays daemon-owned: the two hosts would otherwise both route it.
                ? ExecutionMode.Hook
                : ExecutionMode.Daemon;
            return new RouterConfiguration
            {
                Policies = new RouterPolicies
                {
                    Execution = new ExecutionPolicy { Mode = mode },
                    Daemon = new DaemonPolicy { IntervalSeconds = 60, FailureThreshold = 5 }
                }
            };
        };

        var tick = await DaemonExecutionService.RunOnceAsync(sandbox.RepositoryDirectory, context.Dependencies, CancellationToken.None);

        Assert.True(tick.StopRequested);
        Assert.Contains("hook execution", tick.Summary, StringComparison.Ordinal);
        Assert.Empty(context.SessionHost.Launches);
        Assert.Equal(0, context.AcquireCalls);
    }

    [Fact]
    public async Task RunOnce_FailsClosed_OnInterruptedLaunch_WithoutRelaunching()
    {
        using var sandbox = new TestSandbox();
        var context = new DaemonTestContext(sandbox);
        var claimId = Guid.NewGuid();

        // The daemon died after persisting the launch intent but before the durable running record:
        // the claim is daemon-owned and the session marker is ambiguous (no verified identity).
        await DaemonStateStore.WriteAsync(sandbox.GitCommonDirectory, new DaemonExecutionState
        {
            DaemonSessionId = "owning-daemon",
            ActiveSessions = new Dictionary<string, ActiveDaemonSession>
            {
                [WorkClaimStore.MainWorktreeIdentity] = new ActiveDaemonSession
                {
                    ClaimId = claimId,
                    WorkIdentity = "issue #12",
                    StartedAt = DateTimeOffset.UtcNow,
                    LaunchState = SessionLaunchState.Launching
                }
            }
        });
        await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId, new WorkClaim
        {
            ClaimId = claimId,
            OwnerSessionId = "owning-daemon",
            IssueNumber = 12,
            WorkType = WorkClaimType.Implementation
        });

        var tick = await DaemonExecutionService.RunOnceAsync(sandbox.RepositoryDirectory, context.Dependencies, CancellationToken.None);

        Assert.False(tick.StopRequested);
        Assert.Contains("interrupted-launch", tick.Summary, StringComparison.Ordinal);
        Assert.Empty(context.SessionHost.Launches);
        Assert.Equal(0, context.AcquireCalls);

        var state = await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory);
        Assert.NotNull(state);
        var pending = state!.ActiveSessions[WorkClaimStore.MainWorktreeIdentity];
        Assert.Equal(SessionLaunchState.Launching, pending.LaunchState);
        Assert.Null(pending.ProcessId);
    }

    [Fact]
    public async Task RunOnce_DoesNotRelaunch_WhenLaunchFinishedButStateWriteFailed()
    {
        using var sandbox = new TestSandbox();
        var context = new DaemonTestContext(sandbox);
        context.Plan = SuccessfulPlan(new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 12 });
        // First daemon-state write = durable launch intent; second = the durable Running record
        // written after the real process was spawned. Failing it simulates the exact crash window
        // from the review: the process is running but its identity was never persisted.
        context.WriteStateInterceptor = (call, gitCommonDir, state) =>
            call == 2
                ? Task.FromException(new IOException("state write failed after spawn"))
                : DaemonStateStore.WriteAsync(gitCommonDir, state);

        await Assert.ThrowsAsync<IOException>(
            () => DaemonExecutionService.RunOnceAsync(sandbox.RepositoryDirectory, context.Dependencies, CancellationToken.None));

        // On disk the claim is daemon-owned but only the Launching intent was ever recorded.
        var afterCrash = await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory);
        Assert.NotNull(afterCrash);
        var pending = afterCrash!.ActiveSessions[WorkClaimStore.MainWorktreeIdentity];
        Assert.Equal(SessionLaunchState.Launching, pending.LaunchState);
        Assert.Null(pending.ProcessId);

        // A fresh daemon process (clean dependencies) must fail closed instead of relaunching.
        var restartContext = new DaemonTestContext(sandbox);
        var tick = await DaemonExecutionService.RunOnceAsync(sandbox.RepositoryDirectory, restartContext.Dependencies, CancellationToken.None);

        Assert.Contains("interrupted-launch", tick.Summary, StringComparison.Ordinal);
        Assert.Single(context.SessionHost.Launches);
        Assert.Empty(restartContext.SessionHost.Launches);
        Assert.Equal(1, context.AcquireCalls);
        Assert.Equal(0, restartContext.AcquireCalls);
    }

    [Fact]
    public async Task RunOnce_RecordsSharedAcquisitionMetadata_OnClaimAndSession()
    {
        using var sandbox = new TestSandbox();
        var context = new DaemonTestContext(sandbox)
        {
            Configuration = new RouterConfiguration
            {
                Policies = new RouterPolicies
                {
                    Execution = new ExecutionPolicy { Mode = ExecutionMode.Daemon },
                    Daemon = new DaemonPolicy { IntervalSeconds = 60, FailureThreshold = 5, Model = "codex" },
                    WorkerRouting = new WorkerRoutingPolicy
                    {
                        DefaultWorker = "alice",
                        Workers = new Dictionary<string, WorkerProfileConfiguration>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["alice"] = new WorkerProfileConfiguration { Labels = { "codex:worker:alice" }, Models = { "codex" } }
                        }
                    }
                }
            }
        };
        context.SandboxIssueLabels.Add(new GithubLabel { Name = "codex:worker:alice" });
        context.Plan = SuccessfulPlan(new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 12 });

        var tick = await DaemonExecutionService.RunOnceAsync(sandbox.RepositoryDirectory, context.Dependencies, CancellationToken.None);

        Assert.False(tick.StopRequested);
        var claim = await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId);
        Assert.NotNull(claim);
        Assert.NotEqual(default, claim!.ClaimedIssueUpdatedAt);
        Assert.Equal(context.SandboxIssueUpdatedAt, claim.ClaimedIssueUpdatedAt);
        Assert.Equal("alice", claim.WorkerProfile);
        Assert.Equal("codex", claim.Model);
        Assert.Equal("codex", context.SessionHost.Launches[0].Session.Model);
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

    [Fact]
    public async Task ShutdownGracefully_StopsActiveSessions_AndRecordsCleanStop()
    {
        using var sandbox = new TestSandbox();
        var context = new DaemonTestContext(sandbox);
        await DaemonStateStore.WriteAsync(sandbox.GitCommonDirectory, new DaemonExecutionState
        {
            DaemonSessionId = "shutdown-session",
            ActiveSessions = new Dictionary<string, ActiveDaemonSession>
            {
                [WorkClaimStore.MainWorktreeIdentity] = new ActiveDaemonSession
                {
                    ClaimId = Guid.NewGuid(),
                    WorkIdentity = "issue #12",
                    StartedAt = DateTimeOffset.UtcNow
                }
            }
        });

        await DaemonExecutionService.ShutdownGracefullyAsync(sandbox.RepositoryDirectory, context.Dependencies, CancellationToken.None);

        Assert.Single(context.SessionHost.Stops);
        Assert.Equal("issue #12", context.SessionHost.Stops[0]);

        var state = await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory);
        Assert.NotNull(state);
        Assert.True(state!.StopRequested);
        Assert.True(state.StoppedAt.HasValue);
    }

    [Fact]
    public async Task RunAsync_BoundsUnexpectedTickFailures_InPersistedHealth()
    {
        using var sandbox = new TestSandbox();
        var context = new DaemonTestContext(sandbox)
        {
            Configuration = new RouterConfiguration
            {
                Policies = new RouterPolicies
                {
                    Execution = new ExecutionPolicy { Mode = ExecutionMode.Daemon },
                    Daemon = new DaemonPolicy { IntervalSeconds = 1, FailureThreshold = 2 }
                }
            },
            ResolveWorktreesException = new InvalidOperationException("worktree discovery failed")
        };

        using var cancellation = new CancellationTokenSource();
        context.AfterDelay = () =>
        {
            if (context.DelayCount >= 2)
            {
                cancellation.Cancel();
            }
        };

        await DaemonExecutionService.RunAsync(sandbox.RepositoryDirectory, context.Dependencies, cancellation.Token);

        var state = await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory);
        Assert.NotNull(state);
        Assert.Equal(2, state!.ConsecutiveFailures);
        Assert.True(state.Unhealthy);
        Assert.StartsWith("Unexpected tick failure: worktree discovery failed", state.LastTickSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_RunsGracefulShutdown_WhenCancellationArrives_DuringFailureBackoff()
    {
        using var sandbox = new TestSandbox();
        var context = new DaemonTestContext(sandbox)
        {
            Configuration = new RouterConfiguration
            {
                Policies = new RouterPolicies
                {
                    Execution = new ExecutionPolicy { Mode = ExecutionMode.Daemon },
                    Daemon = new DaemonPolicy { IntervalSeconds = 1, FailureThreshold = 5 }
                }
            },
            ResolveWorktreesException = new InvalidOperationException("worktree discovery failed")
        };

        // Cancel while the poller is inside the exception-path retry delay, which previously let the
        // OperationCanceledException escape the catch block and skip graceful shutdown.
        using var cancellation = new CancellationTokenSource();
        context.AfterDelay = () => cancellation.Cancel();

        await DaemonExecutionService.RunAsync(sandbox.RepositoryDirectory, context.Dependencies, cancellation.Token);

        var state = await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory);
        Assert.NotNull(state);
        Assert.True(state!.ConsecutiveFailures >= 1);
        // Graceful shutdown still ran: sessions stopped and the clean stop was recorded.
        Assert.True(state.StopRequested);
        Assert.True(state.StoppedAt.HasValue);
    }

    [Fact]
    public async Task RunOnce_AbortsInFlightLaunch_WhenStopIsRequested_BetweenIntentAndSpawn()
    {
        using var sandbox = new TestSandbox();
        var context = new DaemonTestContext(sandbox);
        context.Plan = SuccessfulPlan(new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 12 });

        var launchGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var intentPersisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.BeforeSessionLaunchAsync = () => launchGate.Task;
        context.WriteStateInterceptor = async (_, gitCommonDir, state) =>
        {
            await DaemonStateStore.WriteAsync(gitCommonDir, state);
            if (state.ActiveSessions.Values.Any(session => session.LaunchState == SessionLaunchState.Launching))
            {
                intentPersisted.TrySetResult();
            }
        };

        var tickTask = Task.Run(() =>
            DaemonExecutionService.RunOnceAsync(sandbox.RepositoryDirectory, context.Dependencies, CancellationToken.None));

        // Wait until the launch intent is durably persisted, then have a stop arrive while the
        // launch is held between the intent write and the spawn decision.
        await intentPersisted.Task;
        var snapshot = (await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory))!;
        await DaemonStateStore.WriteAsync(sandbox.GitCommonDirectory, snapshot with { StopRequested = true, StoppedAt = DateTimeOffset.UtcNow });

        launchGate.SetResult();
        var tick = await tickTask;

        // The launch was aborted before spawning: no child process was ever created.
        Assert.True(tick.StopRequested);
        Assert.Empty(context.SessionHost.Launches);

        var state = await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory);
        Assert.NotNull(state);
        Assert.True(state!.StopRequested);
        // The interrupted-launch marker is preserved so a later start fails closed on it.
        Assert.Single(state.ActiveSessions);
        Assert.Equal(SessionLaunchState.Launching, state.ActiveSessions.Values.Single().LaunchState);
    }

    [Fact]
    public async Task RunOnce_AbortsInFlightLaunch_WhenStopCommandRunsConcurrently()
    {
        using var sandbox = new TestSandbox();
        var context = new DaemonTestContext(sandbox);
        context.Plan = SuccessfulPlan(new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 12 });

        var launchGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var intentPersisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.BeforeSessionLaunchAsync = () => launchGate.Task;
        context.WriteStateInterceptor = async (_, gitCommonDir, state) =>
        {
            await DaemonStateStore.WriteAsync(gitCommonDir, state);
            if (state.ActiveSessions.Values.Any(session => session.LaunchState == SessionLaunchState.Launching))
            {
                intentPersisted.TrySetResult();
            }
        };

        var tickTask = Task.Run(() =>
            DaemonExecutionService.RunOnceAsync(sandbox.RepositoryDirectory, context.Dependencies, CancellationToken.None));

        await intentPersisted.Task;

        // Exercise the production stop command against the in-flight launch. Because the poller
        // runs inside this test process, the command writes the durable stop request and sweeps
        // sessions without terminating its own process, matching the "exit on next cycle" path.
        var stopDependencies = new DaemonExecutionDependencies
        {
            LoadConfigurationAsync = _ => Task.FromResult(context.Configuration),
            ResolveGitCommonDirectoryAsync = _ => Task.FromResult<string?>(sandbox.GitCommonDirectory),
            ResolveWorktreeDirectoriesAsync = _ => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()),
            ResolveWorktreeIdAsync = _ => Task.FromResult<string?>(null),
            IsAutonomousAsync = _ => Task.FromResult(true),
            IsProcessAliveAsync = (_, _) => Task.FromResult(true),
            TerminateProcessAsync = (pid, _) => Task.CompletedTask,
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
            SessionHost = new FakeSessionHost(),
            ReadStateAsync = gitCommonDir => DaemonStateStore.ReadAsync(gitCommonDir),
            WriteStateAsync = (gitCommonDir, state) => DaemonStateStore.WriteAsync(gitCommonDir, state),
            ReleaseSupervisorLeaseAsync = (gitCommonDir, sessionId, pid) => DaemonSupervisorLeaseStore.ReleaseAsync(gitCommonDir, sessionId, pid),
            DelayAsync = (_, token) => Task.Delay(TimeSpan.FromSeconds(60), token)
        };

        var stopExit = await DaemonCommandHandler.HandleAsync(["stop", sandbox.RepositoryDirectory], stopDependencies);
        Assert.Equal(0, stopExit);

        launchGate.SetResult();
        var tick = await tickTask;

        // The in-flight launch observed the stop before spawning: no child was created concurrently.
        Assert.True(tick.StopRequested);
        Assert.Empty(context.SessionHost.Launches);

        var state = await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory);
        Assert.NotNull(state);
        Assert.True(state!.StopRequested);
        Assert.True(state.StoppedAt.HasValue);
    }

    [Fact]
    public async Task RunOnce_StopsChild_WhenStopArrives_DuringTheSpawnBoundary()
    {
        using var sandbox = new TestSandbox();
        var context = new DaemonTestContext(sandbox);
        context.Plan = SuccessfulPlan(new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 12 });

        var spawnBoundary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var spawnStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.SessionHost.BeforeLaunchAsync = () =>
        {
            spawnStarted.TrySetResult();
            return spawnBoundary.Task;
        };

        var tickTask = Task.Run(() =>
            DaemonExecutionService.RunOnceAsync(sandbox.RepositoryDirectory, context.Dependencies, CancellationToken.None));

        // A stop lands after the process has begun spawning but before its durable record is written.
        await spawnStarted.Task;
        var snapshot = (await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory))!;
        await DaemonStateStore.WriteAsync(sandbox.GitCommonDirectory, snapshot with { StopRequested = true, StoppedAt = DateTimeOffset.UtcNow });

        spawnBoundary.SetResult();
        var tick = await tickTask;

        // The child that appeared in the unavoidable boundary was deterministically stopped.
        Assert.Single(context.SessionHost.Launches);
        Assert.Contains(context.SessionHost.Launches[0].Session.WorkIdentity, context.SessionHost.Stops);
        // The stop observed by the boundary child's Phase 2 must terminate the tick immediately: the
        // worktree pass propagates StopRequested so no further worktree is started in the same tick.
        Assert.True(tick.StopRequested);

        var state = await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory);
        Assert.NotNull(state);
        Assert.True(state!.StopRequested);
        Assert.True(state.StoppedAt.HasValue);
        // The durable Running record exists so a later restart can resolve/resume the session.
        Assert.Equal(SessionLaunchState.Running, state.ActiveSessions.Values.Single().LaunchState);
    }

    [Fact]
    public async Task RunOnce_StopDuringSpawnBoundary_DoesNotStartLaterWorktrees()
    {
        using var sandbox = new TestSandbox();
        var linkedGitDirectory = sandbox.CreateLinkedWorktree("linked");
        var linkedDirectory = Path.Combine(sandbox.Root, "linked");
        Directory.CreateDirectory(linkedDirectory);

        var context = new DaemonTestContext(sandbox);
        context.WorktreeDirectories = new List<string> { sandbox.RepositoryDirectory, linkedDirectory };
        context.WorktreeIdentityByDirectory[sandbox.RepositoryDirectory] = sandbox.MainWorktreeId;
        context.WorktreeIdentityByDirectory[linkedDirectory] = linkedGitDirectory;
        context.PlanResolver = directory =>
            string.Equals(directory, sandbox.RepositoryDirectory, StringComparison.Ordinal)
                ? SuccessfulPlan(new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 10 })
                : SuccessfulPlan(new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 20 });

        var spawnBoundary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var spawnStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstLaunch = true;
        // Only the first worktree's launch pauses inside the spawn boundary; a launch for a later
        // worktree (the bug this guards against) would pass straight through.
        context.SessionHost.BeforeLaunchAsync = () =>
        {
            if (!firstLaunch)
            {
                return Task.CompletedTask;
            }

            firstLaunch = false;
            spawnStarted.TrySetResult();
            return spawnBoundary.Task;
        };

        var tickTask = Task.Run(() =>
            DaemonExecutionService.RunOnceAsync(sandbox.RepositoryDirectory, context.Dependencies, CancellationToken.None));

        // A stop lands while the first worktree's child is inside the spawn boundary.
        await spawnStarted.Task;
        var snapshot = (await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory))!;
        await DaemonStateStore.WriteAsync(sandbox.GitCommonDirectory, snapshot with { StopRequested = true, StoppedAt = DateTimeOffset.UtcNow });

        spawnBoundary.SetResult();
        var tick = await tickTask;

        // The boundary child was stopped and the StopRequested propagated out of LaunchSessionAsync
        // terminated the tick, so the later worktree was never evaluated or started.
        Assert.True(tick.StopRequested);
        Assert.Single(context.SessionHost.Launches);
        Assert.Equal(10, context.SessionHost.Launches[0].Claim.IssueNumber);
        Assert.Contains(context.SessionHost.Launches[0].Session.WorkIdentity, context.SessionHost.Stops);
        Assert.Equal(1, context.AcquireCalls);

        var claims = await WorkClaimStore.ReadAllAsync(sandbox.GitCommonDirectory);
        Assert.Single(claims);
        Assert.Equal(10, claims[0].IssueNumber);
    }

    [Fact]
    public async Task RunAsync_CancellationDuringFailureBackoff_IsPromptAndStillShutsDown()
    {
        using var sandbox = new TestSandbox();
        var context = new DaemonTestContext(sandbox)
        {
            Configuration = new RouterConfiguration
            {
                Policies = new RouterPolicies
                {
                    Execution = new ExecutionPolicy { Mode = ExecutionMode.Daemon },
                    Daemon = new DaemonPolicy { IntervalSeconds = 60, FailureThreshold = 5 }
                }
            },
            ResolveWorktreesException = new InvalidOperationException("worktree discovery failed")
        };

        using var cancellation = new CancellationTokenSource();
        // The failure-path backoff must actually observe the cancellation token: with a 60s interval
        // the old token-less delay would make Ctrl+C wait the full interval before shutting down.
        context.Delay = (_, token) => Task.Delay(TimeSpan.FromSeconds(60), token);

        var cancelTask = Task.Run(async () =>
        {
            await Task.Delay(100);
            cancellation.Cancel();
        });

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await DaemonExecutionService.RunAsync(sandbox.RepositoryDirectory, context.Dependencies, cancellation.Token);
        stopwatch.Stop();

        await cancelTask;
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"Shutdown should interrupt the backoff promptly; took {stopwatch.Elapsed}.");

        var state = await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory);
        Assert.NotNull(state);
        Assert.True(state!.ConsecutiveFailures >= 1);
        // The single graceful-shutdown path still ran regardless of where cancellation arrived.
        Assert.True(state.StopRequested);
        Assert.True(state.StoppedAt.HasValue);
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
            WorktreeDirectories = new List<string> { sandbox.RepositoryDirectory };
            WorktreeIdentityByDirectory = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [sandbox.RepositoryDirectory] = sandbox.MainWorktreeId
            };
        }

        public RouterConfiguration Configuration { get; set; }
        public bool AutonomousEnabled { get; set; } = true;
        public List<string> WorktreeDirectories { get; set; }
        public Dictionary<string, string> WorktreeIdentityByDirectory { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Resolves the effective configuration per worktree directory, mirroring the production
        /// contract that a linked worktree's repository override governs its own evaluation. When
        /// unset every directory receives the single <see cref="Configuration"/>.
        /// </summary>
        public Func<string, RouterConfiguration>? ConfigurationResolver { get; set; }

        /// <summary>
        /// Optional interceptor for daemon-state writes, used to simulate a crash after the session
        /// process was spawned but before its durable running record was persisted.
        /// </summary>
        public Func<int, string, DaemonExecutionState, Task>? WriteStateInterceptor { get; set; }
        public int WriteStateCalls { get; private set; }

        public RoutingEvaluationResult Plan { get; set; } = new()
        {
            IsSuccessful = true,
            Decision = new HookTaskDecision(),
            ActionableTasks = Array.Empty<WorkflowItem>()
        };

        public Func<string, RoutingEvaluationResult>? PlanResolver { get; set; }

        public WorkflowResponse ClaimedWork { get; set; } = new()
        {
            Tasks = { new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 12 } }
        };

        public FakeSessionHost SessionHost { get; } = new();
        public int AcquireCalls { get; private set; }
        public List<int> ClosedIssueNumbers { get; } = new();

        public DateTimeOffset SandboxIssueUpdatedAt { get; set; } = DateTimeOffset.UtcNow.AddMinutes(-10);
        public List<GithubLabel> SandboxIssueLabels { get; } = new();

        public Exception? ResolveWorktreesException { get; set; }
        public int DelayCount { get; private set; }
        public Action? AfterDelay { get; set; }

        /// <summary>Takes over the poller delay when set (used to make backoff actually cancellable).</summary>
        public Func<int, CancellationToken, Task>? Delay { get; set; }

        /// <summary>Optional pause on the launch critical path between the intent write and the spawn decision.</summary>
        public Func<Task>? BeforeSessionLaunchAsync { get; set; }

        public DaemonExecutionDependencies Dependencies => new()
        {
            LoadConfigurationAsync = directory => Task.FromResult(
                ConfigurationResolver?.Invoke(directory) ?? Configuration),
            ResolveGitCommonDirectoryAsync = _ => Task.FromResult<string?>(_sandbox.GitCommonDirectory),
            ResolveWorktreeIdAsync = directory => Task.FromResult<string?>(
                WorktreeIdentityByDirectory.TryGetValue(directory, out var identity) ? identity : null),
            ResolveWorktreeDirectoriesAsync = _ =>
                ResolveWorktreesException is not null
                    ? Task.FromException<IReadOnlyList<string>>(ResolveWorktreesException)
                    : Task.FromResult<IReadOnlyList<string>>(WorktreeDirectories),
            IsAutonomousAsync = _ => Task.FromResult(AutonomousEnabled),
            AcquireClaimAsync = request =>
            {
                AcquireCalls++;
                return WorkClaimAcquisitionService.AcquireAsync(request, AcquisitionDependencies, CancellationToken.None);
            },
            ReadClaimAsync = (gitCommonDir, worktreeId) => WorkClaimStore.ReadAsync(gitCommonDir, worktreeId),
            ReleaseClaimIfMatchesAsync = (gitCommonDir, worktreeId, expected) => WorkClaimStore.ReleaseIfMatchesAsync(gitCommonDir, worktreeId, expected),
            ReadAllClaimsAsync = gitCommonDir => WorkClaimStore.ReadAllAsync(gitCommonDir),
            ReconcileAsync = (_, _, _, _) => Task.FromResult(true),
            EvaluatePlanAsync = (_, directory, _, _) =>
                Task.FromResult(PlanResolver?.Invoke(directory) ?? Plan),
            CheckClaimedWorkAsync = (_, _, _, _) => Task.FromResult(ClaimedWork),
            CloseIssueAsync = (_, issueNumber) =>
            {
                ClosedIssueNumbers.Add(issueNumber);
                return Task.CompletedTask;
            },
            SessionHost = SessionHost,
            ReadStateAsync = gitCommonDir => DaemonStateStore.ReadAsync(gitCommonDir),
            WriteStateAsync = (gitCommonDir, state) =>
            {
                WriteStateCalls++;
                return WriteStateInterceptor is null
                    ? DaemonStateStore.WriteAsync(gitCommonDir, state)
                    : WriteStateInterceptor(WriteStateCalls, gitCommonDir, state);
            },
            BeforeSessionLaunchAsync = BeforeSessionLaunchAsync ?? (() => Task.CompletedTask),
            DelayAsync = (milliseconds, cancellationToken) =>
                Delay?.Invoke(milliseconds, cancellationToken)
                ?? DefaultDelay(milliseconds, cancellationToken)
        };

        private Task DefaultDelay(int milliseconds, CancellationToken cancellationToken)
        {
            DelayCount++;
            AfterDelay?.Invoke();
            return Task.CompletedTask;
        }

        public WorkClaimAcquisitionDependencies AcquisitionDependencies => new()
        {
            FetchIssueAsync = (_, number, _) => Task.FromResult(new Issue
            {
                Number = number,
                UpdatedAt = SandboxIssueUpdatedAt,
                Labels = SandboxIssueLabels
            }),
            TryAcquireClaimAsync = (gitCommonDir, worktreeId, requested, cancellationToken) =>
                WorkClaimStore.TryAcquireAsync(gitCommonDir, worktreeId, requested, cancellationToken)
        };
    }

    private sealed class FakeSessionHost : ISessionHost
    {
        public List<(ActiveDaemonSession Session, WorkClaim Claim, string Prompt, string DaemonSessionId)> Launches { get; } = new();
        public List<string> Stops { get; } = new();
        public bool Alive { get; set; } = true;

        /// <summary>Optional pause inside the spawn itself, used to race a stop against the boundary.</summary>
        public Func<Task>? BeforeLaunchAsync { get; set; }

        public async Task<ActiveDaemonSession> LaunchAsync(
            string worktreeDirectory,
            WorkClaim claim,
            string workIdentity,
            WorkflowItemType workItemType,
            string prompt,
            DaemonPolicy daemonPolicy,
            string daemonSessionId,
            CancellationToken cancellationToken)
        {
            if (BeforeLaunchAsync is not null)
            {
                await BeforeLaunchAsync();
            }

            var session = new ActiveDaemonSession
            {
                ClaimId = claim.ClaimId,
                WorktreeId = claim.WorktreeId,
                WorktreeDirectory = worktreeDirectory,
                ProcessId = 4000 + Launches.Count,
                ProcessStartTimeUtc = DateTimeOffset.UtcNow,
                WorkIdentity = workIdentity,
                WorkItemType = workItemType.ToString(),
                Model = daemonPolicy.Model,
                StartedAt = DateTimeOffset.UtcNow
            };
            Launches.Add((session, claim, prompt, daemonSessionId));
            return session;
        }

        public Task<bool> IsAliveAsync(ActiveDaemonSession session, CancellationToken cancellationToken) =>
            Task.FromResult(Alive);

        public Task StopAsync(ActiveDaemonSession session, CancellationToken cancellationToken)
        {
            Stops.Add(session.WorkIdentity);
            return Task.CompletedTask;
        }
    }
}
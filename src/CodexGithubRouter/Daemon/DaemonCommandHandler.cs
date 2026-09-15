using CodexGithubRouter.Autonomous;
using CodexGithubRouter.Configurations;
using CodexGithubRouter.Git;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Daemon;

public static class DaemonCommandHandler
{
    public static Task<int> HandleAsync(string[] args) => HandleAsync(args, null);

    public static Task<int> HandleAsync(string[] args, DaemonExecutionDependencies? dependencies)
        => HandleAsync(args, dependencies, CancellationToken.None);

    /// <summary>
    /// Entry point with an externally supplied cancellation token. Production passes
    /// <see cref="CancellationToken.None"/> and relies on Ctrl+C; automation (or a hosting
    /// process) can inject a token to bound the poller loop deterministically.
    /// </summary>
    public static async Task<int> HandleAsync(string[] args, DaemonExecutionDependencies? dependencies, CancellationToken cancellationToken)
    {
        dependencies ??= new DaemonExecutionDependencies();
        if (args.Length == 0)
        {
            return Usage();
        }

        var command = args[0].ToLowerInvariant();
        var workingDirectory = ResolveWorkingDirectory(args) ?? Environment.CurrentDirectory;

        try
        {
            return command switch
            {
                "start" => await StartAsync(workingDirectory, dependencies, cancellationToken),
                "stop" => await StopAsync(workingDirectory, dependencies),
                "status" => await StatusAsync(workingDirectory, dependencies),
                "restart" => await RestartAsync(workingDirectory, dependencies, cancellationToken),
                "run" => await RunAsync(workingDirectory, dependencies, args),
                _ => Usage()
            };
        }
        catch (DaemonStateFileException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine("Run 'cgr daemon status' after repairing or deleting the state file to resume supervision.");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Daemon error: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> StartAsync(string workingDirectory, DaemonExecutionDependencies dependencies, CancellationToken cancellationToken)
    {
        var enforce = await EnforceDaemonPreconditionsAsync(workingDirectory, dependencies);
        if (enforce != 0)
        {
            return enforce;
        }

        var gitCommonDir = await dependencies.ResolveGitCommonDirectoryAsync(workingDirectory)
            ?? throw new InvalidOperationException("Not a valid Git repository.");
        var existing = await dependencies.ReadStateAsync(gitCommonDir);
        if (existing is { Pid: > 0 } && existing.Pid != Environment.ProcessId &&
            await dependencies.IsProcessAliveAsync(existing.Pid, existing.PidStartTimeUtc))
        {
            Console.Error.WriteLine($"Daemon is already running with pid {existing.Pid}. Stop it first with 'cgr daemon stop'.");
            return 1;
        }

        var daemonSessionId = existing?.DaemonSessionId ?? Guid.NewGuid().ToString("N");

        // Exclusive repository supervisor lease: a concurrent start (or a live daemon that raced
        // this PID check / state write) must not also become the supervisor.
        if (!await TryAcquireSupervisorLeaseAsync(gitCommonDir, daemonSessionId, dependencies))
        {
            Console.Error.WriteLine("Another daemon supervisor already owns this repository, or a single-cycle run is in progress. Stop it first with 'cgr daemon stop'.");
            return 1;
        }

        try
        {
            var startState = new DaemonExecutionState
            {
                DaemonSessionId = daemonSessionId,
                Pid = Environment.ProcessId,
                PidStartTimeUtc = ProcessIdentity.GetCurrentProcessStartTimeUtc(),
                StartedAt = DateTimeOffset.UtcNow,
                StopRequested = false,
                StoppedAt = null,
                ActiveSessions = existing?.ActiveSessions ?? new Dictionary<string, ActiveDaemonSession>()
            };
            await dependencies.WriteStateAsync(gitCommonDir, startState);

            Console.WriteLine($"Starting daemon poller (session {startState.DaemonSessionId}, pid {Environment.ProcessId}). Press Ctrl+C to stop.");
            await RunPollerAsync(workingDirectory, dependencies, cancellationToken);
            return 0;
        }
        finally
        {
            await ReleaseSupervisorLeaseAsync(gitCommonDir, daemonSessionId, dependencies);
        }
    }

    private static async Task<int> StopAsync(string workingDirectory, DaemonExecutionDependencies dependencies)
    {
        var gitCommonDir = await dependencies.ResolveGitCommonDirectoryAsync(workingDirectory);
        if (gitCommonDir is null)
        {
            Console.Error.WriteLine("Not a valid Git repository.");
            return 1;
        }

        var state = await dependencies.ReadStateAsync(gitCommonDir);
        if (state is null)
        {
            Console.WriteLine("The daemon has never run in this repository.");
            return 0;
        }

        var stopped = state with { StopRequested = true, StoppedAt = DateTimeOffset.UtcNow };
        await dependencies.WriteStateAsync(gitCommonDir, stopped);

        var pollerAlive = state.Pid > 0 &&
            await dependencies.IsProcessAliveAsync(state.Pid, state.PidStartTimeUtc);
        if (pollerAlive && state.Pid != Environment.ProcessId)
        {
            await StopActiveSessionsAsync(state, dependencies);
            await dependencies.TerminateProcessAsync(state.Pid, state.PidStartTimeUtc);
            Console.WriteLine($"Stop requested; stopped daemon session(s) and terminated poller process {state.Pid}.");
        }
        else if (!pollerAlive && state.ActiveSessions.Count > 0)
        {
            // The poller is gone (crashed or its PID was reused) but orphaned sessions may remain;
            // stop them so no Codex process keeps running outside a supervisor.
            await StopActiveSessionsAsync(state, dependencies);
            Console.WriteLine("Stop requested; the poller is not running, but orphaned Codex session(s) were stopped.");
        }
        else
        {
            Console.WriteLine("Stop requested; the daemon will exit on its next polling cycle.");
        }

        // The supervisor is (requested to be) gone, so the repository lease is free for a later
        // start or single-cycle run; releasing with the recorded owner identity never deletes a
        // lease a different supervisor has since taken.
        await dependencies.ReleaseSupervisorLeaseAsync(gitCommonDir, state.DaemonSessionId, state.Pid);

        return 0;
    }

    private static async Task<int> StatusAsync(string workingDirectory, DaemonExecutionDependencies dependencies)
    {
        var gitCommonDir = await dependencies.ResolveGitCommonDirectoryAsync(workingDirectory);
        if (gitCommonDir is null)
        {
            Console.Error.WriteLine("Not a valid Git repository.");
            return 1;
        }

        var state = await dependencies.ReadStateAsync(gitCommonDir);
        if (state is null)
        {
            Console.WriteLine("The daemon has never run in this repository.");
            return 0;
        }

        Console.WriteLine($"Daemon session: {state.DaemonSessionId}");
        var pidRunning = state.Pid > 0 && await dependencies.IsProcessAliveAsync(state.Pid, state.PidStartTimeUtc);
        Console.WriteLine($"Process: {(state.Pid == 0 ? "not recorded" : state.Pid.ToString())}{(pidRunning ? " (running)" : string.Empty)}");
        Console.WriteLine($"Started: {state.StartedAt:O}");
        if (state.StoppedAt is { } stopTime)
        {
            Console.WriteLine($"Stopped: {stopTime:O}");
        }

        Console.WriteLine($"Stop requested: {(state.StopRequested ? "yes" : "no")}");
        Console.WriteLine($"Active sessions: {state.ActiveSessions.Count}");
        if (state.ActiveSessions.Count > 0)
        {
            foreach (var entry in state.ActiveSessions)
            {
                var session = entry.Value;
                var sessionAlive = await dependencies.SessionHost.IsAliveAsync(session, CancellationToken.None);
                Console.WriteLine($"  - [{entry.Key}] {session.WorkIdentity} (pid {session.ProcessId?.ToString() ?? "n/a"}), started {session.StartedAt:O}, running {sessionAlive}");
            }
        }

        Console.WriteLine($"Consecutive failures: {state.ConsecutiveFailures}");
        Console.WriteLine($"Unhealthy: {(state.Unhealthy ? "yes" : "no")}");
        Console.WriteLine($"Last tick: {state.LastTickAt?.ToString("O") ?? "never"}");
        if (!string.IsNullOrWhiteSpace(state.LastTickSummary))
        {
            Console.WriteLine($"Last summary: {state.LastTickSummary}");
        }

        try
        {
            var configuration = await dependencies.LoadConfigurationAsync(workingDirectory);
            if (!ExecutionModeService.IsDaemonOwned(configuration))
            {
                Console.WriteLine("Warning: execution mode is not configured as 'daemon', so the poller is disabled in this configuration.");
            }
        }
        catch
        {
            // Configuration load is best-effort; the persisted state is the canonical health source.
        }

        return 0;
    }

    private static async Task<int> RestartAsync(string workingDirectory, DaemonExecutionDependencies dependencies, CancellationToken cancellationToken)
    {
        var enforce = await EnforceDaemonPreconditionsAsync(workingDirectory, dependencies);
        if (enforce != 0)
        {
            return enforce;
        }

        var gitCommonDir = await dependencies.ResolveGitCommonDirectoryAsync(workingDirectory)
            ?? throw new InvalidOperationException("Not a valid Git repository.");
        var state = await dependencies.ReadStateAsync(gitCommonDir);

        var pollerAlive = state is { Pid: > 0 } && state.Pid != Environment.ProcessId &&
            await dependencies.IsProcessAliveAsync(state.Pid, state.PidStartTimeUtc);
        if (pollerAlive)
        {
            // Graceful restart: request the stop, stop active sessions, then terminate the poller
            // so the previous supervisor cannot race the new one. Claims are preserved and resumed.
            await dependencies.WriteStateAsync(gitCommonDir, state! with { StopRequested = true, StoppedAt = DateTimeOffset.UtcNow });
            await StopActiveSessionsAsync(state!, dependencies);
            await dependencies.TerminateProcessAsync(state!.Pid, state.PidStartTimeUtc);
            await dependencies.ReleaseSupervisorLeaseAsync(gitCommonDir, state.DaemonSessionId, state.Pid);
            Console.WriteLine($"Terminated previous daemon process {state.Pid}.");
        }

        var daemonSessionId = state?.DaemonSessionId ?? Guid.NewGuid().ToString("N");

        // The previous supervisor's lease is gone (or stale), so the successor may take ownership.
        if (!await TryAcquireSupervisorLeaseAsync(gitCommonDir, daemonSessionId, dependencies))
        {
            Console.Error.WriteLine("Another daemon supervisor owns this repository; cannot restart it.");
            return 1;
        }

        try
        {
            var startState = new DaemonExecutionState
            {
                DaemonSessionId = daemonSessionId,
                Pid = Environment.ProcessId,
                PidStartTimeUtc = ProcessIdentity.GetCurrentProcessStartTimeUtc(),
                StartedAt = DateTimeOffset.UtcNow,
                StopRequested = false,
                StoppedAt = null,
                ActiveSessions = state?.ActiveSessions ?? new Dictionary<string, ActiveDaemonSession>()
            };
            await dependencies.WriteStateAsync(gitCommonDir, startState);

            Console.WriteLine($"Restarting daemon poller (session {startState.DaemonSessionId}, pid {Environment.ProcessId}). Press Ctrl+C to stop.");
            await RunPollerAsync(workingDirectory, dependencies, cancellationToken);
            return 0;
        }
        finally
        {
            await ReleaseSupervisorLeaseAsync(gitCommonDir, daemonSessionId, dependencies);
        }
    }

    private static async Task RunPollerAsync(string workingDirectory, DaemonExecutionDependencies dependencies, CancellationToken externalCancellation)
    {
        CancellationTokenSource? ctrlC = null;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
            ctrlC = linked;
            Console.CancelKeyPress += OnCancelKeyPress;
            await DaemonExecutionService.RunAsync(workingDirectory, dependencies, linked.Token);
            await DaemonExecutionService.ShutdownGracefullyAsync(workingDirectory, dependencies, CancellationToken.None);
            Console.WriteLine("Daemon stopped.");
        }
        finally
        {
            if (ctrlC is not null)
            {
                Console.CancelKeyPress -= OnCancelKeyPress;
            }
        }

        void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            ctrlC?.Cancel();
        }
    }

    private static async Task<int> RunAsync(string workingDirectory, DaemonExecutionDependencies dependencies, string[] args)
    {
        var enforce = await EnforceDaemonPreconditionsAsync(workingDirectory, dependencies);
        if (enforce != 0)
        {
            return enforce;
        }

        if (Array.Exists(args, argument => string.Equals(argument, "--once", StringComparison.OrdinalIgnoreCase)))
        {
            var gitCommonDir = await dependencies.ResolveGitCommonDirectoryAsync(workingDirectory)
                ?? throw new InvalidOperationException("Not a valid Git repository.");
            var current = await dependencies.ReadStateAsync(gitCommonDir);
            var daemonSessionId = current?.DaemonSessionId ?? Guid.NewGuid().ToString("N");

            // Refuse a single-cycle run while a live daemon supervisor holds the repository: the
            // lease serializes supervisors so a --once run and the daemon can never evaluate/launch
            // the same claim concurrently or overwrite each other's state.
            if (!await TryAcquireSupervisorLeaseAsync(gitCommonDir, daemonSessionId, dependencies))
            {
                Console.Error.WriteLine("A daemon supervisor is already running for this repository; refusing a concurrent single-cycle run.");
                return 1;
            }

            try
            {
                var tick = await DaemonExecutionService.RunOnceAsync(workingDirectory, dependencies, CancellationToken.None);
                Console.WriteLine(tick.Summary);
                if (tick.Unhealthy)
                {
                    Console.WriteLine($"Warning: daemon marked unhealthy after {tick.ConsecutiveFailures} consecutive failure(s).");
                }

                return 0;
            }
            finally
            {
                await ReleaseSupervisorLeaseAsync(gitCommonDir, daemonSessionId, dependencies);
            }
        }

        Console.Error.WriteLine("Usage: cgr daemon run --once [working-directory]");
        return 1;
    }

    private static async Task StopActiveSessionsAsync(DaemonExecutionState state, DaemonExecutionDependencies dependencies)
    {
        foreach (var session in state.ActiveSessions.Values)
        {
            try
            {
                await dependencies.SessionHost.StopAsync(session, CancellationToken.None);
            }
            catch
            {
                // Best-effort: an already-exited or unknown session must not abort the shutdown.
            }
        }
    }

    private static async Task<bool> TryAcquireSupervisorLeaseAsync(string gitCommonDir, string daemonSessionId, DaemonExecutionDependencies dependencies)
    {
        var lease = new DaemonSupervisorLease
        {
            DaemonSessionId = daemonSessionId,
            Pid = Environment.ProcessId,
            PidStartTimeUtc = ProcessIdentity.GetCurrentProcessStartTimeUtc(),
            AcquiredAtUtc = DateTimeOffset.UtcNow
        };
        return await DaemonSupervisorLeaseStore.TryAcquireAsync(
            gitCommonDir, lease, (pid, startTimeUtc) => dependencies.IsProcessAliveAsync(pid, startTimeUtc));
    }

    private static Task<bool> ReleaseSupervisorLeaseAsync(string gitCommonDir, string daemonSessionId, DaemonExecutionDependencies dependencies)
        => DaemonSupervisorLeaseStore.ReleaseAsync(gitCommonDir, daemonSessionId, Environment.ProcessId);

    private static async Task<int> EnforceDaemonPreconditionsAsync(string workingDirectory, DaemonExecutionDependencies dependencies)
    {
        var configuration = await dependencies.LoadConfigurationAsync(workingDirectory);
        if (!ExecutionModeService.IsDaemonOwned(configuration))
        {
            Console.Error.WriteLine("Execution mode is not 'daemon'. Set policies.execution.mode to \"daemon\" in the workflow configuration to enable the daemon poller.");
            return 1;
        }

        if (!await dependencies.IsAutonomousAsync(workingDirectory))
        {
            Console.Error.WriteLine("Autonomous mode is not enabled. Run 'cgr auto on' for this repository before starting the daemon.");
            return 1;
        }

        if (configuration.Policies.Daemon.IntervalSeconds <= 0)
        {
            Console.Error.WriteLine("Daemon polling interval must be greater than zero.");
            return 1;
        }

        return 0;
    }

    private static string? ResolveWorkingDirectory(string[] args)
    {
        string? workingDirectory = null;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                if (string.Equals(argument, "--once", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(argument, "--working-directory", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(argument, "--working-directory", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
                    {
                        workingDirectory = args[index + 1];
                        index++;
                    }

                    continue;
                }

                continue;
            }

            if (index == 0)
            {
                continue;
            }

            workingDirectory = argument;
        }

        return workingDirectory;
    }

    private static int Usage()
    {
        Console.WriteLine(
            """
            Usage:
              cgr daemon start [working-directory]
              cgr daemon stop [working-directory]
              cgr daemon status [working-directory]
              cgr daemon restart [working-directory]
              cgr daemon run --once [working-directory]

            Commands:
              start     Run the daemon poller in the foreground until stop is requested.
              stop      Gracefully stop active Codex sessions and request a clean daemon shutdown.
              status    Report daemon health, session and active work state.
              restart   Gracefully stop any running poller and its sessions, then start again.
              run       Execute a single polling cycle (useful for cron or debugging).
            """);
        return 1;
    }
}
using System.Diagnostics;
using CodexGithubRouter.Autonomous;
using CodexGithubRouter.Configurations;
using CodexGithubRouter.Git;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Daemon;

public static class DaemonCommandHandler
{
    public static Task<int> HandleAsync(string[] args) => HandleAsync(args, null);

    public static async Task<int> HandleAsync(string[] args, DaemonExecutionDependencies? dependencies)
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
                "start" => await StartAsync(workingDirectory, dependencies),
                "stop" => await StopAsync(workingDirectory, dependencies),
                "status" => await StatusAsync(workingDirectory, dependencies),
                "restart" => await RestartAsync(workingDirectory, dependencies),
                "run" => await RunAsync(workingDirectory, dependencies, args),
                _ => Usage()
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Daemon error: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> StartAsync(string workingDirectory, DaemonExecutionDependencies dependencies)
    {
        var enforce = await EnforceDaemonPreconditionsAsync(workingDirectory, dependencies);
        if (enforce != 0)
        {
            return enforce;
        }

        var gitCommonDir = await dependencies.ResolveGitCommonDirectoryAsync(workingDirectory)
            ?? throw new InvalidOperationException("Not a valid Git repository.");
        var existing = await dependencies.ReadStateAsync(gitCommonDir);
        if (IsProcessRunning(existing?.Pid) && existing?.Pid != Environment.ProcessId)
        {
            Console.Error.WriteLine($"Daemon is already running with pid {existing!.Pid}. Stop it first with 'cgr daemon stop'.");
            return 1;
        }

        var startState = new DaemonExecutionState
        {
            DaemonSessionId = existing?.DaemonSessionId ?? Guid.NewGuid().ToString("N"),
            Pid = Environment.ProcessId,
            StartedAt = DateTimeOffset.UtcNow,
            StopRequested = false,
            StoppedAt = null,
            ActiveSession = existing?.ActiveSession
        };
        await dependencies.WriteStateAsync(gitCommonDir, startState);

        Console.WriteLine($"Starting daemon poller (session {startState.DaemonSessionId}, pid {Environment.ProcessId}). Press Ctrl+C to stop.");
        using var cancellationSource = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };

        await DaemonExecutionService.RunAsync(workingDirectory, dependencies, cancellationSource.Token);
        Console.WriteLine("Daemon stopped.");
        return 0;
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

        if (state.Pid > 0 && state.Pid != Environment.ProcessId && IsProcessRunning(state.Pid))
        {
            TerminateProcess(state.Pid);
            Console.WriteLine($"Stop requested; terminated daemon process {state.Pid}.");
        }
        else
        {
            Console.WriteLine("Stop requested; the daemon will exit on its next polling cycle.");
        }

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
        var pidRunning = state.Pid > 0 && IsProcessRunning(state.Pid);
        Console.WriteLine($"Process: {(state.Pid == 0 ? "not recorded" : state.Pid.ToString())}{(pidRunning ? " (running)" : string.Empty)}");
        Console.WriteLine($"Started: {state.StartedAt:O}");
        if (state.StoppedAt is { } stopTime)
        {
            Console.WriteLine($"Stopped: {stopTime:O}");
        }

        Console.WriteLine($"Stop requested: {(state.StopRequested ? "yes" : "no")}");
        if (state.ActiveSession is { } session)
        {
            var sessionAlive = await dependencies.SessionHost.IsAliveAsync(session, CancellationToken.None);
            Console.WriteLine($"Active session: {session.WorkIdentity} (pid {session.ProcessId?.ToString() ?? "n/a"}), started {session.StartedAt:O}, running {sessionAlive}");
        }
        else
        {
            Console.WriteLine("Active session: none");
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

    private static async Task<int> RestartAsync(string workingDirectory, DaemonExecutionDependencies dependencies)
    {
        var enforce = await EnforceDaemonPreconditionsAsync(workingDirectory, dependencies);
        if (enforce != 0)
        {
            return enforce;
        }

        var gitCommonDir = await dependencies.ResolveGitCommonDirectoryAsync(workingDirectory)
            ?? throw new InvalidOperationException("Not a valid Git repository.");
        var state = await dependencies.ReadStateAsync(gitCommonDir);
        if (state is { Pid: > 0 } && state.Pid != Environment.ProcessId && IsProcessRunning(state.Pid))
        {
            TerminateProcess(state.Pid);
            Console.WriteLine($"Terminated previous daemon process {state.Pid}.");
        }

        var startState = new DaemonExecutionState
        {
            DaemonSessionId = state?.DaemonSessionId ?? Guid.NewGuid().ToString("N"),
            Pid = Environment.ProcessId,
            StartedAt = DateTimeOffset.UtcNow,
            StopRequested = false,
            StoppedAt = null,
            ActiveSession = state?.ActiveSession
        };
        await dependencies.WriteStateAsync(gitCommonDir, startState);

        Console.WriteLine($"Restarting daemon poller (session {startState.DaemonSessionId}, pid {Environment.ProcessId}). Press Ctrl+C to stop.");
        using var cancellationSource = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };

        await DaemonExecutionService.RunAsync(workingDirectory, dependencies, cancellationSource.Token);
        Console.WriteLine("Daemon stopped.");
        return 0;
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
            var tick = await DaemonExecutionService.RunOnceAsync(workingDirectory, dependencies, CancellationToken.None);
            Console.WriteLine(tick.Summary);
            if (tick.Unhealthy)
            {
                Console.WriteLine($"Warning: daemon marked unhealthy after {tick.ConsecutiveFailures} consecutive failure(s).");
            }

            return 0;
        }

        Console.Error.WriteLine("Usage: cgr daemon run --once [working-directory]");
        return 1;
    }

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

    private static bool IsProcessRunning(int? pid) =>
        pid is > 0 && IsProcessRunning(pid.Value);

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void TerminateProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (ArgumentException)
        {
            // The process no longer exists; the stop state is already recorded.
        }
        catch (InvalidOperationException)
        {
            // The process already exited while being inspected.
        }
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
              stop      Request a clean daemon shutdown and terminate the running poller process.
              status    Report daemon health, session and active work state.
              restart   Stop any running poller then start again in the foreground.
              run       Execute a single polling cycle (useful for cron or debugging).
            """);
        return 1;
    }
}
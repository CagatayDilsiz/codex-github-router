using System.Diagnostics;
using CodexGithubRouter.Helpers;
using CodexGithubRouter.Work;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Daemon;

/// <summary>
/// Production session host that spawns the Codex CLI (<c>codex exec</c> by default) in the
/// claim's working directory. The host starts the process without waiting for exit so the daemon
/// supervisor can continue polling, and the resulting <see cref="ActiveDaemonSession"/> carries the
/// PID for lightweight cross-restart health checks.
/// </summary>
public sealed class CodexSessionHost : ISessionHost
{
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
        var arguments = new List<string>(daemonPolicy.Args);
        arguments.Add(prompt);

        var startInfo = new ProcessStartInfo
        {
            FileName = daemonPolicy.Command,
            WorkingDirectory = worktreeDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["CODEX_DANGEROUSLY_DISABLE_NONINTERACTIVE_EXPERIMENTAL"] = "1";

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start daemon session: {daemonPolicy.Command}");
        }

        // Allow the process to run asynchronously; capturing stdout/stderr in the background so
        // the file descriptors are not closed while the process runs, without blocking the daemon
        // poller.
        _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
        _ = process.StandardError.ReadToEndAsync(cancellationToken);

        return new ActiveDaemonSession
        {
            ClaimId = claim.ClaimId,
            WorktreeId = claim.WorktreeId,
            WorktreeDirectory = worktreeDirectory,
            ProcessId = process.Id,
            WorkIdentity = workIdentity,
            WorkItemType = workItemType.ToString(),
            StartedAt = DateTimeOffset.UtcNow
        };
    }

    public Task<bool> IsAliveAsync(ActiveDaemonSession session, CancellationToken cancellationToken)
    {
        if (session.ProcessId is null)
        {
            return Task.FromResult(false);
        }

        try
        {
            using var process = Process.GetProcessById(session.ProcessId.Value);
            return Task.FromResult(!process.HasExited);
        }
        catch (InvalidOperationException)
        {
            // The process has exited or the id does not exist on this host.
            return Task.FromResult(false);
        }
        catch (ArgumentException)
        {
            return Task.FromResult(false);
        }
    }

    public Task StopAsync(ActiveDaemonSession session, CancellationToken cancellationToken)
    {
        if (session.ProcessId is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            using var process = Process.GetProcessById(session.ProcessId.Value);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (ArgumentException)
        {
            // Process does not exist.
        }

        return Task.CompletedTask;
    }
}
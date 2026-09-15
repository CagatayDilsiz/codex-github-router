using System.ComponentModel;
using System.Diagnostics;
using CodexGithubRouter.Helpers;
using CodexGithubRouter.Work;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Daemon;

/// <summary>
/// Production session host that spawns the Codex CLI (<c>codex exec</c> by default) in the
/// claim's working directory. The host starts the process without waiting for exit so the daemon
/// supervisor can continue polling, and the resulting <see cref="ActiveDaemonSession"/> carries the
/// PID and the process start time so a restart can verify process identity instead of trusting a
/// possibly-recycled PID. The configured <see cref="DaemonPolicy.Model"/> is forwarded to the
/// launched process (as <c>--model &lt;model&gt;</c>) so the session never runs with an inferred
/// model different from the one routing and the claim recorded.
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
        var model = daemonPolicy.Model?.Trim();
        if (!string.IsNullOrWhiteSpace(model))
        {
            arguments.Add("--model");
            arguments.Add(model);
        }

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

        DateTimeOffset processStartTimeUtc;
        try
        {
            processStartTimeUtc = new DateTimeOffset(process.StartTime.ToUniversalTime());
        }
        catch (Win32Exception)
        {
            processStartTimeUtc = DateTimeOffset.UtcNow;
        }

        return new ActiveDaemonSession
        {
            ClaimId = claim.ClaimId,
            WorktreeId = claim.WorktreeId,
            WorktreeDirectory = worktreeDirectory,
            ProcessId = process.Id,
            ProcessStartTimeUtc = processStartTimeUtc,
            WorkIdentity = workIdentity,
            WorkItemType = workItemType.ToString(),
            Model = model,
            StartedAt = DateTimeOffset.UtcNow
        };
    }

    public Task<bool> IsAliveAsync(ActiveDaemonSession session, CancellationToken cancellationToken)
    {
        if (session.ProcessId is null)
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(ProcessIdentity.IsAlive(session.ProcessId.Value, session.ProcessStartTimeUtc));
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
            if (process.HasExited)
            {
                return Task.CompletedTask;
            }

            if (session.ProcessStartTimeUtc is { } expected &&
                (new DateTimeOffset(process.StartTime.ToUniversalTime()) - expected).Duration() > TimeSpan.FromSeconds(5))
            {
                // The PID was recycled by an unrelated process since this session was launched;
                // never kill a process we did not start.
                return Task.CompletedTask;
            }

            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (ArgumentException)
        {
            // Process does not exist.
        }
        catch (Win32Exception)
        {
            // The process exited while its identity was being verified.
        }

        return Task.CompletedTask;
    }
}
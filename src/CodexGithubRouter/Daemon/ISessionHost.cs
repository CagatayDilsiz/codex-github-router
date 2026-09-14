using CodexGithubRouter.Work;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Daemon;

/// <summary>
/// Launches and supervises Codex execution sessions for the daemon. The daemon calls
/// <see cref="LaunchAsync"/> after acquiring a claim and the resulting handle to detect whether the
/// session is still running (crash recovery), extract an exit code (finalize the claim) and
/// terminate (graceful shutdown) — all without embedding agent-runtime logic in the routing
/// engine. This is the single integration point between the daemon supervisor and the Codex CLI.
/// </summary>
public interface ISessionHost
{
    /// <summary>
    /// Spawns a new execution session in the given working directory for the claimed work item.
    /// The implementation is responsible for starting the process (non-blocking) and returning a
    /// lightweight handle the caller can poll; the session id is not managed here because the
    /// daemon supervisor owns the session lifecycle.
    /// </summary>
    Task<ActiveDaemonSession> LaunchAsync(
        string worktreeDirectory,
        WorkClaim claim,
        string workIdentity,
        WorkflowItemType workItemType,
        string prompt,
        DaemonPolicy daemonPolicy,
        string daemonSessionId,
        CancellationToken cancellationToken);

    /// <summary>Detects whether the session process is still alive.</summary>
    Task<bool> IsAliveAsync(ActiveDaemonSession session, CancellationToken cancellationToken);

    /// <summary>
    /// Attempts to stop the session process (best-effort, may be a no-op for already-stopped
    /// processes). Must not throw on cross-platform process resolution failures.
    /// </summary>
    Task StopAsync(ActiveDaemonSession session, CancellationToken cancellationToken);
}
namespace CodexGithubRouter.Daemon;

/// <summary>
/// Persisted daemon execution state for a repository. Because the daemon has no in-memory
/// counterpart across a process restart, this file is the single source of truth the daemon
/// supervisor, status command and shutdown flow share: the stable daemon session id, the last poll
/// cycle, health, shutdown coordination, and the active Codex session per Git worktree. It lives in
/// the Git common directory next to the work-claim file so it relocates with the repository and is
/// visible to every worktree.
/// </summary>
public sealed record DaemonExecutionState
{
    public int Version { get; init; } = 1;

    /// <summary>
    /// Stable identity the daemon uses as its Codex-session-facing claim owner. Persisted so a
    /// restart reuses the same owner and continues (never duplicates) claims it already holds.
    /// </summary>
    public string DaemonSessionId { get; init; } = string.Empty;

    /// <summary>PID of the daemon supervisor process from the last poll cycle.</summary>
    public int Pid { get; init; }

    /// <summary>
    /// Start time of the daemon supervisor process, captured (with the PID) so persisted process
    /// ownership survives PID reuse after a restart. A stale state file whose PID happens to be
    /// alive again must not be treated as the running daemon.
    /// </summary>
    public DateTimeOffset? PidStartTimeUtc { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? LastTickAt { get; init; }

    public string LastTickSummary { get; init; } = string.Empty;

    public int ConsecutiveFailures { get; init; }

    public bool Unhealthy { get; init; }

    public bool StopRequested { get; init; }

    public DateTimeOffset? StoppedAt { get; init; }

    /// <summary>
    /// The active Codex execution session for every Git worktree the daemon supervises, keyed by
    /// the worktree's normalized identity (<see cref="WorkClaimStore.NormalizeWorktreeId"/>). A
    /// repository supervisor therefore preserves the parallel-work contract from the work-claim
    /// store: each worktree owns its own claim and may run its own session independently.
    /// </summary>
    public IReadOnlyDictionary<string, ActiveDaemonSession> ActiveSessions { get; init; }
        = new Dictionary<string, ActiveDaemonSession>();
}

/// <summary>
/// A Codex execution session the daemon launched for a claim it owns in a specific worktree.
/// Persisted so a restart can tell whether the session is still running (continue supervising),
/// finished cleanly (finalize the claim) or died unexpectedly (resume the claimed work without
/// re-acquiring it). The recorded start time lets the daemon verify process identity instead of
/// trusting a recycled PID.
/// </summary>
public sealed class ActiveDaemonSession
{
    public Guid ClaimId { get; init; }

    public string WorktreeId { get; init; } = string.Empty;

    public string WorktreeDirectory { get; init; } = string.Empty;

    public int? ProcessId { get; init; }

    /// <summary>
    /// Start time of the launched session process. Combined with <see cref="ProcessId"/>, this is
    /// the identity the daemon verifies before treating a session as running or stopping it, so a
    /// PID reused by an unrelated process after a crash is never mistaken for or killed as daemon
    /// work.
    /// </summary>
    public DateTimeOffset? ProcessStartTimeUtc { get; init; }

    public string WorkIdentity { get; init; } = string.Empty;

    public string WorkItemType { get; init; } = string.Empty;

    /// <summary>Model recorded on the claim and passed to the launched session, if configured.</summary>
    public string? Model { get; init; }

    public DateTimeOffset StartedAt { get; init; }
}
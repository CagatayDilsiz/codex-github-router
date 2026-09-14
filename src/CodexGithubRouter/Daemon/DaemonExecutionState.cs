namespace CodexGithubRouter.Daemon;

/// <summary>
/// Persisted daemon execution state for a repository. Because the daemon has no in-memory
/// counterpart across a process restart, this file is the single source of truth the daemon
/// supervisor, status command and shutdown flow share: the stable daemon session id, the last poll
/// cycle, health, shutdown coordination, and the currently supervised Codex session. It lives in
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

    public int Pid { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? LastTickAt { get; init; }

    public string LastTickSummary { get; init; } = string.Empty;

    public int ConsecutiveFailures { get; init; }

    public bool Unhealthy { get; init; }

    public bool StopRequested { get; init; }

    public DateTimeOffset? StoppedAt { get; init; }

    public ActiveDaemonSession? ActiveSession { get; init; }
}

/// <summary>
/// A Codex execution session the daemon launched for a claim it owns. Persisted so a restart can
/// tell whether the session is still running (continue supervising), finished cleanly (finalize the
/// claim) or died unexpectedly (resume the claimed work without re-acquiring it).
/// </summary>
public sealed class ActiveDaemonSession
{
    public Guid ClaimId { get; init; }

    public string WorktreeId { get; init; } = string.Empty;

    public string WorktreeDirectory { get; init; } = string.Empty;

    public int? ProcessId { get; init; }

    public string WorkIdentity { get; init; } = string.Empty;

    public string WorkItemType { get; init; } = string.Empty;

    public DateTimeOffset StartedAt { get; init; }
}
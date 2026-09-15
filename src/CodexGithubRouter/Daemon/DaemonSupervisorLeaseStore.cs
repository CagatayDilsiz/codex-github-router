using System.Text.Json;

namespace CodexGithubRouter.Daemon;

/// <summary>
/// The owner identity recorded in the exclusive repository supervisor lease. The lease file is a
/// lock in the Git common directory: only the process that created it without contention may run
/// the repository's daemon supervisor (or a single <c>--once</c> cycle). The persisted PID plus
/// process start time let a second starter tell a live owner from the stale lease a crashed process
/// left behind, and let a shut-down supervisor release only the lease it actually owns.
/// </summary>
public sealed record DaemonSupervisorLease
{
    public string DaemonSessionId { get; init; } = string.Empty;

    public int Pid { get; init; }

    public DateTimeOffset? PidStartTimeUtc { get; init; }

    public DateTimeOffset AcquiredAtUtc { get; init; }
}

/// <summary>
/// Atomic, identity-verified supervisor lease stored next to the daemon state file. <c>start</c>,
/// <c>restart</c> and <c>run --once</c> all go through <see cref="TryAcquireAsync"/> so a live
/// daemon and a concurrent single-cycle run (or two near-simultaneous starts) can never both become
/// the repository supervisor: acquisition is exclusive (create-new), and a lease whose owner is no
/// longer alive is reclaimed so a crashed supervisor does not deadlock the repository.
/// </summary>
public static class DaemonSupervisorLeaseStore
{
    public const string LeaseFileName = "codex-github-router.daemon.lock";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static string GetLeasePath(string gitCommonDirectory) =>
        Path.Combine(gitCommonDirectory, LeaseFileName);

    public static Task<DaemonSupervisorLease?> ReadAsync(string gitCommonDirectory, CancellationToken cancellationToken = default)
        => ReadFileAsync(GetLeasePath(gitCommonDirectory), cancellationToken);

    /// <summary>
    /// Atomically creates the lease file. When a lease already exists its owner is checked with
    /// <paramref name="isProcessAliveAsync"/>: a live owner means another supervisor owns the
    /// repository (the caller must refuse), while a dead owner means the lease is stale and is
    /// reclaimed. Concurrent reclaims are resolved by re-running the check-write cycle a bounded
    /// number of times.
    /// </summary>
    public static async Task<bool> TryAcquireAsync(
        string gitCommonDirectory,
        DaemonSupervisorLease lease,
        Func<int, DateTimeOffset?, Task<bool>> isProcessAliveAsync,
        CancellationToken cancellationToken = default)
    {
        var path = GetLeasePath(gitCommonDirectory);
        Directory.CreateDirectory(gitCommonDirectory);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(stream, lease, Options, cancellationToken);
                }

                return true;
            }
            catch (IOException) when (File.Exists(path))
            {
                var existing = await ReadFileAsync(path, cancellationToken);
                if (existing is { Pid: > 0 } && await isProcessAliveAsync(existing.Pid, existing.PidStartTimeUtc))
                {
                    return false;
                }

                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // Another contender reclaimed the lease between the check and the delete; the
                    // next iteration re-reads the (possibly new) owner before writing.
                }

                if (attempt < 2)
                {
                    await Task.Delay(25, cancellationToken);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Removes the lease only if it is still owned by the given session/process; a lease that was
    /// already replaced by another supervisor (or reclaimed) is left untouched so a shut-down
    /// process can never delete a live successor's lease.
    /// </summary>
    public static async Task<bool> ReleaseAsync(string gitCommonDirectory, string daemonSessionId, int ownerPid, CancellationToken cancellationToken = default)
    {
        var existing = await ReadAsync(gitCommonDirectory, cancellationToken);
        if (existing is null)
        {
            return true;
        }

        if (!string.Equals(existing.DaemonSessionId, daemonSessionId, StringComparison.Ordinal) ||
            existing.Pid != ownerPid)
        {
            return false;
        }

        try
        {
            File.Delete(GetLeasePath(gitCommonDirectory));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static async Task<DaemonSupervisorLease?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        return JsonSerializer.Deserialize<DaemonSupervisorLease>(content, Options);
    }
}
using System.ComponentModel;
using System.Diagnostics;

namespace CodexGithubRouter.Daemon;

/// <summary>
/// Verifies the identity of a persisted process before the daemon treats it as itself or as an
/// owned session. A PID alone is not a safe ownership token: after a crash or restart the OS may
/// recycle a PID for an unrelated process. Pairing the PID with the process start time (with a
/// small clock tolerance) makes the ownership check deterministic for the common "reused PID"
/// failure mode.
/// </summary>
public static class ProcessIdentity
{
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(5);

    public static DateTimeOffset GetCurrentProcessStartTimeUtc() =>
        new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime());

    /// <summary>
    /// True when a process with <paramref name="pid"/> exists, has not exited and (when supplied)
    /// started within the tolerance of <paramref name="expectedStartTimeUtc"/>. A missing
    /// expected start time skips the start-time comparison for legacy state but still requires a
    /// live process. Any failure to read the process (it may have exited between the lookup and the
    /// identity read on a busy system) is treated as "not alive", never as evidence of identity.
    /// </summary>
    public static bool IsAlive(int pid, DateTimeOffset? expectedStartTimeUtc)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return false;
            }

            if (expectedStartTimeUtc is not { } expected)
            {
                return true;
            }

            var actual = new DateTimeOffset(process.StartTime.ToUniversalTime());
            return (actual - expected).Duration() <= StartTimeTolerance;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            // The process exited between the lookup and the identity read.
            return false;
        }
    }

    /// <summary>
    /// Best-effort resolution of a live process's start time. Returns false when the process is
    /// unknown or already exited, mirroring <see cref="IsAlive"/> without an identity comparison.
    /// </summary>
    public static bool TryGetStartTimeUtc(int pid, out DateTimeOffset startTimeUtc)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                startTimeUtc = default;
                return false;
            }

            startTimeUtc = new DateTimeOffset(process.StartTime.ToUniversalTime());
            return true;
        }
        catch (ArgumentException)
        {
            startTimeUtc = default;
            return false;
        }
        catch (InvalidOperationException)
        {
            startTimeUtc = default;
            return false;
        }
        catch (Win32Exception)
        {
            startTimeUtc = default;
            return false;
        }
    }
}
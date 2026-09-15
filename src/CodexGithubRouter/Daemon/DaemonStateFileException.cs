namespace CodexGithubRouter.Daemon;

public sealed class DaemonStateFileException : InvalidOperationException
{
    public string StatePath { get; }

    public DaemonStateFileException(string statePath, Exception innerException)
        : base(
            $"The daemon state file at '{statePath}' is corrupt and must be repaired before the daemon can continue. " +
            "Delete the file to reset daemon state (claims are unaffected and will be resumed or re-acquired on the next cycle).",
            innerException)
    {
        StatePath = statePath;
    }
}
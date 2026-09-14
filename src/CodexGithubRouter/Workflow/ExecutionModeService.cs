namespace CodexGithubRouter.Workflow;

/// <summary>
/// Resolves repository execution ownership from the effective configuration. Execution ownership is
/// a single, named actor (the hook or the daemon) that decides when CGR acquires work for a
/// repository, so the two hosts can never independently race for the same work item.
/// </summary>
public static class ExecutionModeService
{
    public const string InvalidExecutionModeMessage = "Unsupported execution mode. Expected either \"hook\" or \"daemon\".";

    /// <summary>
    /// Resolves the declared execution mode from the effective configuration. A missing or empty
    /// value falls back to the hook, preserving historical behavior for hook-only repositories.
    /// </summary>
    public static ExecutionMode Resolve(RouterConfiguration configuration) =>
        configuration.Policies.Execution.Mode;

    public static bool IsDaemonOwned(RouterConfiguration configuration) =>
        Resolve(configuration) == ExecutionMode.Daemon;

    public static bool IsHookOwned(RouterConfiguration configuration) =>
        Resolve(configuration) == ExecutionMode.Hook;

    public static string Name(ExecutionMode mode) => mode switch
    {
        ExecutionMode.Daemon => "daemon",
        _ => "hook"
    };

    /// <summary>
    /// Maps a configuration string (for example a JSON <c>policies.execution.mode</c> value) to the
    /// supported execution modes. Returns <c>null</c> for anything else so callers can fail closed
    /// with a deterministic message.
    /// </summary>
    public static ExecutionMode? Parse(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? ExecutionMode.Hook
            : value.Trim().ToLowerInvariant() switch
            {
                "hook" => ExecutionMode.Hook,
                "daemon" => ExecutionMode.Daemon,
                _ => null
            };

    public static string? Validate(ExecutionMode mode) =>
        mode is ExecutionMode.Hook or ExecutionMode.Daemon
            ? null
            : InvalidExecutionModeMessage;
}
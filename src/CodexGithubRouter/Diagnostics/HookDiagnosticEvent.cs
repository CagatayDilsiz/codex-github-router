namespace CodexGithubRouter.Diagnostics;

public sealed class HookDiagnosticEvent
{
    public string EventName { get; init; } = "hook.invocation";

    public Guid InvocationId { get; init; }

    public DateTimeOffset TimestampUtc { get; init; }

    public long DurationMs { get; init; }

    public string? RepositoryIdentity { get; init; }

    public bool? AutonomousEnabled { get; init; }

    public string? ActivationMode { get; init; }

    public bool? ActivationResult { get; init; }

    public string? WorkflowItemType { get; init; }

    public int? IssueNumber { get; init; }

    public int? PullRequestNumber { get; init; }

    public string? Worker { get; init; }

    public string? Model { get; init; }

    public string? Identity { get; init; }

    public string? ClaimId { get; init; }

    public string Result { get; init; } = "bypass";

    public string? BlockReason { get; init; }

    public string? ErrorType { get; init; }

    public string? ErrorMessage { get; init; }
}

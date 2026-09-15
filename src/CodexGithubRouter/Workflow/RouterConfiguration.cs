namespace CodexGithubRouter.Workflow;

public sealed class RouterConfiguration
{
    public int Version { get; init; } = 1;

    public Dictionary<WorkflowState, List<IssueMatchRule>> States { get; init; } = GetDefaultStates();

    public Dictionary<PullRequestState, List<IssueMatchRule>> PullRequestStates { get; init; } = new Dictionary<PullRequestState, List<IssueMatchRule>>
    {
        [PullRequestState.ReviewRequested] = new List<IssueMatchRule>
        {
            new IssueMatchRule
            {
                Type = IssueMatchRuleType.Label,
                Values = new List<string> { "codex:rr" }
            }
        },
        [PullRequestState.ChangesRequested] = new List<IssueMatchRule>
        {
            new IssueMatchRule
            {
                Type = IssueMatchRuleType.Label,
                Values = new List<string> { "codex:cr" }
            }
        },
        [PullRequestState.AwaitingMerge] = new List<IssueMatchRule>
        {
            new IssueMatchRule
            {
                Type = IssueMatchRuleType.Label,
                Values = new List<string> { "codex:merge-ready" }
            }
        },
        [PullRequestState.Deferred] = new List<IssueMatchRule>
        {
            new IssueMatchRule
            {
                Type = IssueMatchRuleType.Label,
                Values = new List<string> { "codex:deferred" }
            }
        }
    };

    public RouterPolicies Policies { get; init; } = new();

    public IssueSelectionConfiguration DefaultIssueSelection { get; init; } = new IssueSelectionConfiguration();    

    private static Dictionary<WorkflowState, List<IssueMatchRule>> GetDefaultStates()
    {
        return new Dictionary<WorkflowState, List<IssueMatchRule>>
        {
            [WorkflowState.Ready] = new List<IssueMatchRule>
           {
               new IssueMatchRule
               {
                   Type = IssueMatchRuleType.Label,
                   Values = new List<string> { "codex:ready" }
               }
           },
            [WorkflowState.InProgress] = new List<IssueMatchRule>
           {
               new IssueMatchRule
               {
                   Type = IssueMatchRuleType.Label,
                   Values = new List<string> { "codex:working" }
               }
           },
            [WorkflowState.Completed] = new List<IssueMatchRule>
            {
            new IssueMatchRule
            {
                    Type = IssueMatchRuleType.Label,
                    Values = new List<string> { "codex:done" }
            }
            },
            [WorkflowState.Blocked] = new List<IssueMatchRule>
            {
                new IssueMatchRule
                {
                    Type = IssueMatchRuleType.Label,
                    Values = new List<string> { "codex:blocked" }
                }
            },
            [WorkflowState.NeedsInfo] = new List<IssueMatchRule>
            {
                new IssueMatchRule
                {
                    Type = IssueMatchRuleType.Label,
                    Values = new List<string> { "codex:needs-info" }
                }
            },
            [WorkflowState.Abandoned] = new List<IssueMatchRule>
            {
                new IssueMatchRule
                {
                    Type = IssueMatchRuleType.Label,
                    Values = new List<string> { "codex:abandoned" }
                }
            }
        };
    }

}

public sealed class RouterPolicies
{
    public RepositoryGatePolicy RepositoryGate { get; init; } = new();

    public WorkerRoutingPolicy? WorkerRouting { get; init; }

    public AssignmentRoutingPolicy? AssignmentRouting { get; init; }

    public AutonomousActivationPolicy? AutonomousActivation { get; init; }

    public DiagnosticsPolicy Diagnostics { get; init; } = new();

    public ReviewRoutingPolicy? ReviewRouting { get; init; }

    public NativeSignalsPolicy NativeSignals { get; init; } = new();

    public ExecutionPolicy Execution { get; init; } = new();

    public DaemonPolicy Daemon { get; init; } = new();
}

/// <summary>
/// Named execution ownership of a repository. Exactly one owner decides when CGR acquires work:
/// the interactive hook (running inside a Codex session) or the daemon (a background poller). A
/// future hybrid mode is intentionally out of scope; the first daemon design avoids two independent
/// actors racing to acquire work for the same repository.
/// </summary>
public enum ExecutionMode
{
    /// <summary>Hook-driven execution (the default, and the only historical behavior).</summary>
    Hook,

    /// <summary>Daemon-driven execution: a background poller detects, claims and runs eligible work.</summary>
    Daemon
}

public sealed class ExecutionPolicy
{
    public ExecutionMode Mode { get; init; } = ExecutionMode.Hook;
}

/// <summary>
/// Polling and session-launch behavior for daemon execution ownership. Only consumed when
/// <c>policies.execution.mode</c> is <see cref="ExecutionMode.Daemon"/>; ignored otherwise.
/// </summary>
public sealed class DaemonPolicy
{
    /// <summary>Seconds between polling cycles. Must be &gt; 0.</summary>
    public int IntervalSeconds { get; init; } = 60;

    /// <summary>Optional model label recorded on daemon claims and passed to launched sessions.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>Executable used to launch Codex execution sessions. Defaults to the Codex CLI.</summary>
    public string Command { get; init; } = "codex";

    /// <summary>Arguments passed before the generated work prompt when launching a session.</summary>
    public List<string> Args { get; init; } = new() { "exec", "--skip-git-repo-check" };

    /// <summary>Consecutive failed polling cycles before the daemon reports itself unhealthy.</summary>
    public int FailureThreshold { get; init; } = 5;
}

public sealed class ReviewRoutingPolicy
{
    public bool Enabled { get; init; }
}

/// <summary>
/// Enables GitHub-native review/check/mergeability signals in workflow evaluation. Disabled by
/// default so repositories that want label-driven workflows only keep the historical behavior:
/// without this policy, workflow state is derived exclusively from CGR workflow labels.
/// </summary>
public sealed class NativeSignalsPolicy
{
    public bool Enabled { get; init; }
}

public sealed class DiagnosticsPolicy
{
    public bool Enabled { get; init; } = true;

    public int RetentionDays { get; init; } = 7;
}

public sealed class AutonomousActivationPolicy
{
    public string Mode { get; init; } = "always";

    public List<string>? Prompts { get; init; } = new();
}

public sealed class RepositoryGatePolicy
{
    public List<string> Labels { get; init; } = new() { "codex:gate" };
}

public sealed class WorkerRoutingPolicy
{
    public string DefaultWorker { get; init; } = string.Empty;

    public Dictionary<string, WorkerProfileConfiguration> Workers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class WorkerProfileConfiguration
{
    public List<string> Labels { get; init; } = new();

    public List<string> Models { get; init; } = new();
}

public sealed class AssignmentRoutingPolicy
{
    public string Mode { get; init; } = "ignore";

    public string Unassigned { get; init; } = "allow";
}

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

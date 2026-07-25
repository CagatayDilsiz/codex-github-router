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
        [PullRequestState.ChangeRequested] = new List<IssueMatchRule>
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
        }
    };

    public IssueSelectionConfiguration IssueSelection { get; init; } = new IssueSelectionConfiguration();

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
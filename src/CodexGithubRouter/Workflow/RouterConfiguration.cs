namespace CodexGithubRouter.Workflow;

public sealed class RouterConfiguration
{
    public int Version { get; init; } = 1;

    public Dictionary<WorkflowState, List<IssueMatchRule>> States { get; init; } = GetDefaultStates();

    public Dictionary<WorkflowState, List<TransitionTypeState>> Transitions { get; init; } = GetDefaultTransitions();

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

    private static Dictionary<WorkflowState, List<TransitionTypeState>> GetDefaultTransitions()
    {
        return new Dictionary<WorkflowState, List<TransitionTypeState>>
        {
            [WorkflowState.Ready] = new List<TransitionTypeState>
            {
                new TransitionTypeState
                {
                    FromType = IssueMatchRuleType.Label,
                    ToType = IssueMatchRuleType.Label                  
                }
            },
            [WorkflowState.InProgress] = new List<TransitionTypeState>
            {
                new TransitionTypeState
                {
                    FromType = IssueMatchRuleType.Label,
                    ToType = IssueMatchRuleType.Label,                   
                }
            },
            [WorkflowState.Blocked] = new List<TransitionTypeState>
            {
                new TransitionTypeState
                {
                    FromType = IssueMatchRuleType.Label,
                    ToType = IssueMatchRuleType.Label
                    
                }
            },
            [WorkflowState.NeedsInfo] = new List<TransitionTypeState>
            {
                new TransitionTypeState
                {
                    FromType = IssueMatchRuleType.Label,
                    ToType = IssueMatchRuleType.Label                    
                }
            },
            [WorkflowState.Abandoned] = new List<TransitionTypeState>
            {
                new TransitionTypeState
                {
                    FromType = IssueMatchRuleType.Label,
                    ToType = IssueMatchRuleType.Label                    
                }
            }
        };
    }

}
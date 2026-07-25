namespace CodexGithubRouter.Workflow;
public sealed class TransitionTypeState
{
    public IssueMatchRuleType FromType { get; init; }
    public IssueMatchRuleType ToType { get; init; }  

}
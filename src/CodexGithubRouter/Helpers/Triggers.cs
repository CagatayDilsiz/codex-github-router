namespace CodexGithubRouter.Helpers;


public class Triggers
{
    public IssueReadyTrigger IssueReady { get; set; } = new IssueReadyTrigger();
    public IssueWorkInProgressTrigger IssueWorkInProgress { get; set; } = new IssueWorkInProgressTrigger();
    public IssueWorkCompletedTrigger IssueWorkCompleted { get; set; } = new IssueWorkCompletedTrigger();
    public IssueWorkAbandonedTrigger IssueWorkAbandoned { get; set; } = new IssueWorkAbandonedTrigger();
    public IssueWorkBlockedTrigger IssueWorkBlocked { get; set; } = new IssueWorkBlockedTrigger();
    public IssueWorkInfoNeededTrigger IssueWorkInfoNeeded { get; set; } = new IssueWorkInfoNeededTrigger();
}

public class IssueReadyTrigger
{
    public string Type { get; set; } = "label";
    public List<string> Verbs { get; set; } = ["codex:ready"]; 
}

public class IssueWorkInProgressTrigger
{
    public string Type { get; set; } = "label";
    public List<string> Verbs { get; set; } = ["codex:wip"]; 
}

public class IssueWorkCompletedTrigger
{
    public string Type { get; set; } = "label";
    public List<string> Verbs { get; set; } = ["codex:completed"]; 
}

public class IssueWorkAbandonedTrigger
{
    public string Type { get; set; } = "label";
    public List<string> Verbs { get; set; } = ["codex:abandoned"]; 
}

public class IssueWorkBlockedTrigger
{
    public string Type { get; set; } = "label";
    public List<string> Verbs { get; set; } = ["codex:blocked"]; 
}

public class IssueWorkInfoNeededTrigger
{
    public string Type { get; set; } = "label";
    public List<string> Verbs { get; set; } = ["codex:info-needed"]; 
}
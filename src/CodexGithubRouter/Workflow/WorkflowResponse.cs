namespace CodexGithubRouter.Workflow;

public class WorkflowResponse
{
    public List<WorkflowItem> Tasks { get; set; } = new List<WorkflowItem>();

    public string Message { get; set; } = string.Empty;

    public bool IsSuccessful { get; set; } = true;
}
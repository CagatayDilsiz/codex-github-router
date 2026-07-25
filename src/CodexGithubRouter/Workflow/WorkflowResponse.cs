namespace CodexGithubRouter.Workflow;

public class WorkflowResponse
{
    public List<WorkflowTask> Tasks { get; set; } = new List<WorkflowTask>();

    public string Message { get; set; } = string.Empty;

    public bool IsSuccessful { get; set; } = true;
}
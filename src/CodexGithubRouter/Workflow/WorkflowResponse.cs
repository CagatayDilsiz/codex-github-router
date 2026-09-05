using CodexGithubRouter.GitHub;

namespace CodexGithubRouter.Workflow;

public class WorkflowResponse
{
    public List<WorkflowItem> Tasks { get; set; } = new List<WorkflowItem>();

    public string Message { get; set; } = string.Empty;

    public bool IsSuccessful { get; set; } = true;

    public bool NoEligibleWork { get; init; }

    public IReadOnlyList<WorkerEligibility> IneligibleWorkerIssues { get; init; } = Array.Empty<WorkerEligibility>();

    public IReadOnlyList<AssignmentEligibility> IneligibleAssignmentIssues { get; init; } = Array.Empty<AssignmentEligibility>();

    public List<Issue> ConsideredIssues { get; set; } = new List<Issue>();
}

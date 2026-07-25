using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.GitHub;

public sealed class IssueTransition
{
    public int IssueNumber { get; init; }

    public List<string> LabelsToAdd { get; init; } = new();

    public List<string> LabelsToRemove { get; init; } = new();

    // will add other properties as needed, such as assignees, milestones, etc.
}
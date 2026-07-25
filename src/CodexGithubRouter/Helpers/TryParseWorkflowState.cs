using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Helpers;

public static class WorkflowStateParser
{
    public static bool TryParse(string value, out WorkflowState state)
    {
        state = value.Trim().ToLowerInvariant() switch
        {
            "ready" or "ready-to-start" or "begin" => WorkflowState.Ready,
            "working" or "in-progress" or "inprogress" => WorkflowState.InProgress,
            "completed" or "done" => WorkflowState.Completed,
            "blocked" => WorkflowState.Blocked,
            "needs-info" or "need-info" or "needsinfo" => WorkflowState.NeedsInfo,
            "abandoned" => WorkflowState.Abandoned,
            _ => default
        };

        return value.Trim().ToLowerInvariant() is
            "ready" or "ready-to-start" or "begin" or
            "working" or "in-progress" or "inprogress" or
            "completed" or "done" or
            "blocked" or
            "needs-info" or "need-info" or "needsinfo" or
            "abandoned";
    }
}
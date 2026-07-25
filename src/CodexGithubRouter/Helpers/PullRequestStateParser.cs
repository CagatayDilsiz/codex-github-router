using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Helpers;

public static class PullRequestStateParser
{
    public static bool TryParse(string value, out PullRequestState state)
    {
        state = value.Trim().ToLowerInvariant() switch
        {
            "review-requested" or "ready-for-review" => PullRequestState.ReviewRequested,
            "change-requested" => PullRequestState.ChangeRequested,
            "awaiting-merge" => PullRequestState.AwaitingMerge,
            _ => default
        };

        return value.Trim().ToLowerInvariant() is
            "review-requested" or "ready-for-review" or
            "change-requested" or
            "awaiting-merge";
    }
}
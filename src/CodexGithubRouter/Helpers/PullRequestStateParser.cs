using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Helpers;

public static class PullRequestStateParser
{
    public static bool TryParse(string value, out PullRequestState state)
    {
        state = value.Trim().ToLowerInvariant() switch
        {
            "review-requested" or "ready-for-review" => PullRequestState.ReviewRequested,
            "changes-requested" => PullRequestState.ChangesRequested,
            "awaiting-merge" => PullRequestState.AwaitingMerge,
            "deferred" => PullRequestState.Deferred,
            _ => default
        };

        return value.Trim().ToLowerInvariant() is
            "review-requested" or "ready-for-review" or
            "changes-requested" or
            "awaiting-merge" or
            "deferred";
    }
}
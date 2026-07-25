namespace CodexGithubRouter.Prompts;

public static class ContextPromptService
{
    public static string GetNewIssuePrompt(int number)
    {
        return $"""
            Next task is to work on issue #{number}.

            First, please use `gh issue view {number} --comments` to view the details of the issue, additional comments and understand what needs to be done.

            1. If the issue is not clear, please ask for clarification in the comments of the issue. Do not make any assumptions about the issue, run `cgr issue transition {number} needs-info` after leaving comment. Wait for the issue author to provide clarification before proceeding.             

            2. Before starting to work on the issue, check if there are unstaged or uncommitted changes in the working directory. If there are, do not proceed with the issue until the changes are either committed or stashed. This is to ensure that the working directory is clean and does not interfere with the work on the issue. run `cgr issue transition {number} blocked` to block the issue and do not start working on it until the changes are resolved by the user.

            3. if there are no changes in the working directory, make sure to create a new branch for the issue from up-to-date default branch.   

            4. When you start working on the issue, run `cgr issue transition {number} working` to indicate that work is in progress. This will help other contributors know that the issue is being worked on and avoid duplicate work.

            5. Once you have completed the work on the issue, please submit a pull request and link it to the issue. Do not close the issue yourself, as it will be closed automatically when the pull request is merged. run `cgr issue transition {number} completed` to indicate that the work is complete and run `cgr pr transition <newly-created-pr-number> ready-for-review` to indicate that the issue/PR is ready for review.

            6. Pull request title and description should be in same language as the issue so that the issue author can understand it. If the issue is in a different language, please provide a translation of the pull request title and description in the same language as the issue.

        """;
    }

    public static string GetReviewPRForOpenIssuesPrompt(int[] issueNumbers)
    {
        return $"""
            Next task is to review open pull requests against the following issues: {string.Join(", ", issueNumbers)}.

            1. Please use `gh pr list --state open` to view the list of open pull requests in the repository.

            2. For each open pull request, please review the changes and see if any given issue is addressed in the pull request. If the pull request addresses any of the issues, please edit the pull request body and add closing issue references for the issues that are addressed in the pull request. This will help the issue author know that their issue is being addressed and avoid duplicate work.

            3. Do not code, do not merge the pull request, do not close the issue, do not leave any comments on the pull request or the issue. Just edit the pull request body and add closing issue references for the issues that are addressed in the pull request otherwise leave it as is. 
        """;
    }

    public static string GetChangeRequestPrompt(int issueNumber, int pullRequestNumber)
    {
        return $"""
            Next task is to continue working on the pull request #{pullRequestNumber} for issue #{issueNumber} as there are changes requested by the reviewer whether it is the issue author's comment or another contributor.

            1. Please use `gh pr view {pullRequestNumber} --comments` to view the details of the pull request, additional comments and understand what changes are requested.

            2. If there are any issues with the changes requested, please leave a comment on the pull request and run `cgr issue transition {issueNumber} needs-info` to indicate that the request needs more information before any work can be done.

            3. If the changes requested are clear, please make the necessary changes to the pull request and run `cgr issue transition {issueNumber} working` to indicate that work is in progress.

            4. Make sure to work on the same branch as the pull request and push the changes to the same branch. Do not create a new pull request for the changes.

            5. If the local working directory has uncommitted changes that are not related to the pull request, do not proceed with the changes until the changes are either committed or stashed. This is to ensure that the working directory is clean and does not interfere with the work on the pull request. run `cgr issue transition {issueNumber} blocked` to block the issue and do not start working on it until the changes are resolved by the user.

            6. Once you have completed the changes requested, please push the changes to the same branch and run `cgr issue transition {issueNumber} completed` to indicate that the work is complete and run `cgr pull-request transition {pullRequestNumber} ready-for-review` to indicate that the pull request is ready for review again.

        """;
    }
}

/*2. If there is a pull request associated with the issue, please review the pull request and provide feedback on whether it can be merged or not. Do not merge it. please just leave a review comment and run `cgr issue transition {number} reviewed` to indicate that the pull request has been reviewed.*/
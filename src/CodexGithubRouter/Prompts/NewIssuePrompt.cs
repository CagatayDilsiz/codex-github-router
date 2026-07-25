namespace CodexGithubRouter.Prompts;

public static class NewIssuePrompt
{
    public static string GetPrompt(int number) // PR review label for now hardcoded, we can make it configurable later.
    {
        return $"""
            Next task is to work on issue #{number}.

            First, please use `gh issue view {number} --comments` to view the details of the issue, additional comments and understand what needs to be done.

            1. If the issue is not clear, please ask for clarification in the comments of the issue. Do not make any assumptions about the issue, run `cgr issue transition {number} needs-info` after leaving comment. Wait for the issue author to provide clarification before proceeding.

            2. If there is a pull request associated with the issue, please review the pull request and provide feedback on whether it can be merged or not. Do not merge it. please just leave a review comment and run `cgr issue transition {number} reviewed` to indicate that the pull request has been reviewed. 

            3. Before starting to work on the issue, check if there are unstaged or uncommitted changes in the working directory. If there are, do not proceed with the issue until the changes are either committed or stashed. This is to ensure that the working directory is clean and does not interfere with the work on the issue. run `cgr issue transition {number} blocked` to block the issue and do not start working on it until the changes are resolved by the user.

            4. if there are no changes in the working directory, make sure to create a new branch for the issue from up-to-date default branch.   

            5. When you start working on the issue, run `cgr issue transition {number} working` to indicate that work is in progress. This will help other contributors know that the issue is being worked on and avoid duplicate work.

            6. Once you have completed the work on the issue, please submit a pull request and link it to the issue. Do not close the issue yourself, as it will be closed automatically when the pull request is merged. run `cgr issue transition {number} completed` to indicate that the work is complete and the issue is ready for review.

            7. Pull request title and description should be in same language as the issue so that the issue author can understand it. If the issue is in a different language, please provide a translation of the pull request title and description in the same language as the issue.

        """;
    }
}
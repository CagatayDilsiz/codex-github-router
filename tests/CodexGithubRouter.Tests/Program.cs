using CodexGithubRouter.GitHub;
using CodexGithubRouter.Prompts;
using CodexGithubRouter.Workflow;

await AssertSingleWorkingIssueWithoutPullRequestAsync();
await AssertMultipleWorkingIssuesBlockAsync();
await AssertWorkingIssueWithOpenPullRequestAsync();
AssertResumePromptIsSafe();

Console.WriteLine("All working-issue workflow tests passed.");

static async Task AssertSingleWorkingIssueWithoutPullRequestAsync()
{
    var result = await WorkflowService.EvaluateInProgressIssuesAsync(new RouterConfiguration(), new[] { WorkingIssue(4) }, _ => throw new InvalidOperationException("No pull request should be requested."));

    Assert(result.IsSuccessful, "A single working issue without a pull request should be actionable.");
    Assert(result.Tasks.Single().Type == WorkflowItemType.ResumeInProgressIssue, "A single working issue should resume instead of starting a ready issue.");
}

static async Task AssertMultipleWorkingIssuesBlockAsync()
{
    var result = await WorkflowService.EvaluateInProgressIssuesAsync(new RouterConfiguration(), new[] { WorkingIssue(4), WorkingIssue(5) }, _ => throw new InvalidOperationException("No pull request should be requested."));

    Assert(!result.IsSuccessful, "Multiple working issues must block the workflow.");
    Assert(result.Message.Contains("#4", StringComparison.Ordinal) && result.Message.Contains("#5", StringComparison.Ordinal), "The ambiguity diagnostic should identify every working issue.");
}

static async Task AssertWorkingIssueWithOpenPullRequestAsync()
{
    var issue = WorkingIssue(4);
    issue.ClosingPullRequestsReferences.Add(new ClosingIssueReference { Number = 8 });

    var result = await WorkflowService.EvaluateInProgressIssuesAsync(
        new RouterConfiguration(),
        new[] { issue },
        _ => Task.FromResult(new PullRequest
        {
            Number = 8,
            State = "open",
            Labels = new List<GithubLabel> { new() { Name = "codex:rr" } }
        }));

    Assert(result.IsSuccessful, "A working issue with an open pull request should be evaluated.");
    Assert(result.Tasks.Single().Type == WorkflowItemType.AwaitingReview, "An open linked pull request should reuse its pull-request workflow state.");
}

static Issue WorkingIssue(int number) => new()
{
    Number = number,
    Labels = new List<GithubLabel> { new() { Name = "codex:working" } }
};

static void AssertResumePromptIsSafe()
{
    var prompt = ContextPromptService.GetInProgressIssuePrompt(4);

    Assert(prompt.Contains("git branch --all", StringComparison.Ordinal), "The resume prompt should inspect local branches.");
    Assert(prompt.Contains("git ls-remote --heads origin", StringComparison.Ordinal), "The resume prompt should inspect remote branches.");
    Assert(prompt.Contains("exists locally", StringComparison.Ordinal) && prompt.Contains("only on origin", StringComparison.Ordinal) && prompt.Contains("only locally", StringComparison.Ordinal) && prompt.Contains("no matching local or remote branch", StringComparison.Ordinal), "The resume prompt should cover local, remote, and missing-branch recovery cases.");
    Assert(prompt.Contains("Do not create a new branch or pull request", StringComparison.Ordinal), "The resume prompt must prevent duplicate work.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

using CodexGithubRouter.GitHub;
using CodexGithubRouter.Hooks;
using CodexGithubRouter.Prompts;
using CodexGithubRouter.Workflow;

await AssertSingleWorkingIssueWithoutPullRequestAsync();
await AssertMultipleWorkingIssuesBlockAsync();
await AssertWorkingIssueWithOpenPullRequestAsync();
AssertResumePromptIsSafe();
AssertHookOutputResumesWorkingIssueBeforeReadyIssue();

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

    Assert(prompt.Contains("codex/issue-4-*", StringComparison.Ordinal), "The resume prompt should use the exact issue branch prefix.");
    Assert(prompt.Contains("zero or multiple candidates", StringComparison.Ordinal), "The resume prompt should block ambiguous branch recovery.");
    Assert(prompt.Contains("gh pr list --head <candidate-branch> --state all", StringComparison.Ordinal), "The resume prompt should inspect pull requests for the recovered branch.");
    Assert(prompt.Contains("One open pull request", StringComparison.Ordinal) && prompt.Contains("One closed pull request", StringComparison.Ordinal) && prompt.Contains("Multiple pull requests", StringComparison.Ordinal), "The resume prompt should cover every recovered-branch pull request state.");
    Assert(prompt.Contains("Do not recreate the branch", StringComparison.Ordinal), "The resume prompt must prevent duplicate work.");
}

static void AssertHookOutputResumesWorkingIssueBeforeReadyIssue()
{
    var decision = HookTaskRouter.Route(new List<WorkflowItem>
    {
        new() { Type = WorkflowItemType.ResumeInProgressIssue, IssueNumber = 4 },
        new() { Type = WorkflowItemType.NewIssue, IssueNumber = 5 }
    });

    Assert(decision.BlockReason is null, "A single working issue should not be blocked by routing.");
    Assert(decision.AdditionalContext?.Contains("Issue #4 is already marked as working", StringComparison.Ordinal) == true, "Hook routing should emit resume context before ready-issue context.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

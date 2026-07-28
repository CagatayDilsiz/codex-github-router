using CodexGithubRouter.GitHub;
using CodexGithubRouter.Helpers;
using CodexGithubRouter.Hooks;
using CodexGithubRouter.Prompts;
using CodexGithubRouter.Work;
using CodexGithubRouter.Workflow;
using Xunit;

namespace CodexGithubRouter.Tests;

[Trait("Category", "Unit")]
public sealed class WorkflowBehaviorTests
{
[Fact]
public async Task AssertSingleWorkingIssueWithoutPullRequestAsync()
{
    var result = await WorkflowService.EvaluateInProgressIssuesAsync(new RouterConfiguration(), new[] { WorkingIssue(4) }, _ => throw new InvalidOperationException("No pull request should be requested."));

    Xunit.Assert.True(result.IsSuccessful, "A single working issue without a pull request should be actionable.");
    Xunit.Assert.True(result.Tasks.Single().Type == WorkflowItemType.ResumeInProgressIssue, "A single working issue should resume instead of starting a ready issue.");
}

[Fact]
public async Task AssertMultipleWorkingIssuesBlockAsync()
{
    var result = await WorkflowService.EvaluateInProgressIssuesAsync(new RouterConfiguration(), new[] { WorkingIssue(4), WorkingIssue(5) }, _ => throw new InvalidOperationException("No pull request should be requested."));

    Xunit.Assert.True(!result.IsSuccessful, "Multiple working issues must block the workflow.");
    Xunit.Assert.True(result.Message.Contains("#4", StringComparison.Ordinal) && result.Message.Contains("#5", StringComparison.Ordinal), "The ambiguity diagnostic should identify every working issue.");
}

[Fact]
public async Task AssertWorkingIssueWithOpenPullRequestAsync()
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

    Xunit.Assert.True(result.IsSuccessful, "A working issue with an open pull request should be evaluated.");
    Xunit.Assert.True(result.Tasks.Single().Type == WorkflowItemType.AwaitingReview, "An open linked pull request should reuse its pull-request workflow state.");
}

static Issue WorkingIssue(int number) => new()
{
    Number = number,
    Labels = new List<GithubLabel> { new() { Name = "codex:working" } }
};

[Fact]
public void AssertResumePromptIsSafe()
{
    var prompt = ContextPromptService.GetInProgressIssuePrompt(4);

    Xunit.Assert.True(prompt.Contains("codex/issue-4-*", StringComparison.Ordinal), "The resume prompt should use the exact issue branch prefix.");
    Xunit.Assert.True(prompt.Contains("zero or multiple candidates", StringComparison.Ordinal), "The resume prompt should block ambiguous branch recovery.");
    Xunit.Assert.True(prompt.Contains("gh pr list --head <candidate-branch> --state all", StringComparison.Ordinal), "The resume prompt should inspect pull requests for the recovered branch.");
    Xunit.Assert.True(prompt.Contains("Zero pull requests means interrupted work", StringComparison.Ordinal), "The resume prompt should resume interrupted branch work without an early pull request.");
    Xunit.Assert.True(prompt.Contains("Fixes #4", StringComparison.Ordinal) && prompt.Contains("next hook invocation", StringComparison.Ordinal), "The resume prompt should link a discovered open pull request and defer further handling to the normal workflow.");
    Xunit.Assert.True(prompt.Contains("One closed pull request", StringComparison.Ordinal) && prompt.Contains("Multiple pull requests", StringComparison.Ordinal), "The resume prompt should cover closed and ambiguous recovered-branch pull request states.");
    Xunit.Assert.True(prompt.Contains("Do not recreate the branch", StringComparison.Ordinal), "The resume prompt must prevent duplicate work.");

    var completedRecoveryPrompt = ContextPromptService.GetCompletedIssueRecoveryPrompt(4);
    Xunit.Assert.True(completedRecoveryPrompt.Contains("git branch --all --list \"codex/issue-4-*\"", StringComparison.Ordinal) && completedRecoveryPrompt.Contains("gh pr list --head <candidate-branch> --state all", StringComparison.Ordinal), "Completed recovery must inspect exact branch candidates and every pull-request state.");
    Xunit.Assert.True(completedRecoveryPrompt.Contains("create exactly one pull request", StringComparison.Ordinal) && completedRecoveryPrompt.Contains("Fixes #4", StringComparison.Ordinal), "Completed recovery must provide executable single-PR linking instructions.");
    var currentPullRequestRecoveryPrompt = ContextPromptService.GetCurrentPullRequestRecoveryPrompt(4, 21);
    Xunit.Assert.True(currentPullRequestRecoveryPrompt.Contains("pull request #21", StringComparison.Ordinal) && currentPullRequestRecoveryPrompt.Contains("cgr pr transition 21 ready-for-review", StringComparison.Ordinal) && currentPullRequestRecoveryPrompt.Contains("cgr issue transition 4 completed", StringComparison.Ordinal), "An unlabeled current PR must receive exact lifecycle recovery instructions.");
}

[Fact]
public void AssertClarificationPromptIsLanguageIndependent()
{
    var prompt = ContextPromptService.GetNewIssuePrompt(12);
    const string canonicalNotice = "> 🤖 This clarification request was generated automatically by a Codex session through CGR. After providing the requested information, transition this issue back to `ready`.";
    var noticeInstruction = prompt.IndexOf(canonicalNotice, StringComparison.Ordinal);
    var questionInstruction = prompt.IndexOf("actual clarification question must follow the notice", StringComparison.Ordinal);

    Xunit.Assert.True(noticeInstruction >= 0 && questionInstruction > noticeInstruction, "Clarification comments must start with a blockquote notice before the question.");
    Xunit.Assert.True(prompt.Contains("visible Markdown blockquote notice", StringComparison.Ordinal), "The notice must be a visible Markdown blockquote.");
    Xunit.Assert.True(prompt.Contains("translate it naturally when the issue or clarification conversation is not in English", StringComparison.Ordinal), "Non-English clarification notices must be translated naturally.");
    Xunit.Assert.True(prompt.Contains("Keep `Codex`, `CGR`, and `ready` unchanged", StringComparison.Ordinal), "The canonical identifiers must remain unchanged when translating the notice.");
    Xunit.Assert.True(prompt.Contains("cgr issue transition 12 needs-info", StringComparison.Ordinal), "The existing needs-info transition must remain in the clarification instructions.");
}

[Fact]
public void AssertHookOutputResumesWorkingIssueBeforeReadyIssue()
{
    var decision = HookTaskRouter.Route(new List<WorkflowItem>
    {
        new() { Type = WorkflowItemType.ResumeInProgressIssue, IssueNumber = 4 },
        new() { Type = WorkflowItemType.NewIssue, IssueNumber = 5 }
    });

    Xunit.Assert.True(decision.BlockReason is null, "A single working issue should not be blocked by routing.");
    Xunit.Assert.True(decision.AdditionalContext?.Contains("Issue #4 is already marked as working", StringComparison.Ordinal) == true, "Hook routing should emit resume context before ready-issue context.");
}

[Fact]
public void AssertRepositoryGateConfiguration()
{
    var configuration = new RouterConfiguration();
    Xunit.Assert.True(RepositoryGateService.GetLabels(configuration).SequenceEqual(new[] { "codex:gate" }), "The default repository gate must use codex:gate.");
    Xunit.Assert.True(WorkflowLabelConfiguration.GetRequiredLabels(configuration).Contains("codex:gate", StringComparer.OrdinalIgnoreCase), "The repository gate label must be provisioned with the managed labels.");

    var issue = new Issue
    {
        Number = 13,
        Labels = new List<GithubLabel>
        {
            new() { Name = "codex:ready" },
            new() { Name = "codex:gate" }
        }
    };
    var transition = IssueTransitionPlanner.Plan(issue, WorkflowState.Completed, configuration);
    Xunit.Assert.True(!transition.LabelsToRemove.Contains("codex:gate", StringComparer.OrdinalIgnoreCase), "Issue transitions must preserve the repository gate label.");

    configuration.Policies.RepositoryGate.Labels.Add("codex:critical");
    Xunit.Assert.True(RepositoryGateService.IsGated(new Issue { Labels = new List<GithubLabel> { new() { Name = "codex:critical" } } }, configuration), "Configured repository gate aliases must use OR semantics.");

    configuration.Policies.RepositoryGate.Labels.Clear();
    configuration.Policies.RepositoryGate.Labels.Add(" codex:gate ");
    try
    {
        WorkflowLabelConfiguration.ValidateNoConflictingLabels(configuration);
        throw new InvalidOperationException("Whitespace around repository gate labels must be rejected.");
    }
    catch (InvalidOperationException exception) when (exception.Message.Contains("leading or trailing whitespace", StringComparison.Ordinal))
    {
    }
}

[Fact]
public async Task Gated_review_request_blocks_with_context()
{
    var result = await WorkflowService.EvaluateRepositoryGateAsync(new RouterConfiguration(), new[] { GatedIssue(10, WorkflowState.Completed, 20) }, _ => Task.FromResult(new PullRequest { Number = 20, State = "open", Labels = new List<GithubLabel> { new() { Name = "codex:rr" } } }));
    Xunit.Assert.True(result.Tasks.Single().Type == WorkflowItemType.RepositoryGateBlock && result.Tasks.Single().Status.Message.Contains("issue #10", StringComparison.Ordinal) && result.Tasks.Single().Status.Message.Contains("Pull request #20", StringComparison.Ordinal));
}

[Fact]
public async Task Gated_change_request_remains_actionable()
{
    var result = await WorkflowService.EvaluateRepositoryGateAsync(new RouterConfiguration(), new[] { GatedIssue(11, WorkflowState.Completed, 21) }, _ => Task.FromResult(new PullRequest { Number = 21, State = "open", Labels = new List<GithubLabel> { new() { Name = "codex:cr" } } }));
    Xunit.Assert.True(result.Tasks.Single().Type == WorkflowItemType.ChangeRequest);
}

[Fact]
public async Task Gated_ready_issue_is_prioritized_without_pull_request_lookup()
{
    var result = await WorkflowService.EvaluateRepositoryGateAsync(new RouterConfiguration(), new[] { GatedIssue(12, WorkflowState.Ready) }, _ => throw new InvalidOperationException());
    Xunit.Assert.True(result.Tasks.Single().Type == WorkflowItemType.NewIssue);
}

[Fact]
public async Task Merged_gated_pull_request_is_terminal()
{
    var result = await WorkflowService.EvaluateRepositoryGateAsync(new RouterConfiguration(), new[] { GatedIssue(14, WorkflowState.Completed, 22) }, _ => Task.FromResult(new PullRequest { Number = 22, State = "merged", Labels = new List<GithubLabel>() }));
    Xunit.Assert.Empty(result.Tasks);
}

[Fact]
public async Task Historical_merged_pull_request_does_not_hide_current_change_request()
{
    var issue = GatedIssue(18, WorkflowState.Completed, 23);
    issue.ClosingPullRequestsReferences.Add(new ClosingIssueReference { Number = 24 });
    var result = await WorkflowService.EvaluateRepositoryGateAsync(new RouterConfiguration(), new[] { issue }, number => Task.FromResult(number == 23 ? new PullRequest { Number = 23, State = "merged" } : new PullRequest { Number = 24, State = "open", Labels = new List<GithubLabel> { new() { Name = "codex:cr" } } }));
    Xunit.Assert.True(result.Tasks.Single().Type == WorkflowItemType.ChangeRequest && result.Tasks.Single().PullRequestNumber == 24);
}

[Fact]
public async Task Historical_merged_pull_request_does_not_hide_current_review_request()
{
    var issue = GatedIssue(18, WorkflowState.Completed, 23);
    issue.ClosingPullRequestsReferences.Add(new ClosingIssueReference { Number = 24 });
    var result = await WorkflowService.EvaluateRepositoryGateAsync(new RouterConfiguration(), new[] { issue }, number => Task.FromResult(number == 23 ? new PullRequest { Number = 23, State = "merged" } : new PullRequest { Number = 24, State = "open", Labels = new List<GithubLabel> { new() { Name = "codex:rr" } } }));
    Xunit.Assert.True(result.Tasks.Single().Type == WorkflowItemType.RepositoryGateBlock && result.Tasks.Single().Status.Message.Contains("Pull request #24", StringComparison.Ordinal));
}

[Fact]
public async Task Interrupted_gated_work_recovers_after_historical_merge()
{
    var result = await WorkflowService.EvaluateRepositoryGateAsync(new RouterConfiguration(), new[] { GatedIssue(19, WorkflowState.InProgress, 25) }, _ => Task.FromResult(new PullRequest { Number = 25, State = "merged" }));
    Xunit.Assert.Equal(WorkflowItemType.ResumeInProgressIssue, result.Tasks.Single().Type);
}

[Fact]
public async Task Closed_or_removed_gate_issue_is_ignored()
{
    var closed = GatedIssue(26, WorkflowState.Ready); closed.State = "closed";
    var removed = GatedIssue(27, WorkflowState.Ready); removed.Labels.RemoveAll(label => label.Name == "codex:gate");
    var configuration = new RouterConfiguration();
    Xunit.Assert.Empty((await WorkflowService.EvaluateRepositoryGateAsync(configuration, new[] { closed }, _ => throw new InvalidOperationException())).Tasks);
    Xunit.Assert.Empty((await WorkflowService.EvaluateRepositoryGateAsync(configuration, new[] { removed }, _ => throw new InvalidOperationException())).Tasks);
}

[Fact]
public void Gate_routing_precedes_ordinary_ready_work()
{
    var decision = HookTaskRouter.Route(new[] { new WorkflowItem { Type = WorkflowItemType.ChangeRequest, IssueNumber = 11, PullRequestNumber = 21 }, new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 15 } });
    Xunit.Assert.Equal(11, decision.SelectedTask?.IssueNumber);
}

[Fact]
public void Blocked_gate_and_gate_task_selection_preserve_precedence()
{
    var blocked = HookTaskRouter.Route(new[] { new WorkflowItem { Type = WorkflowItemType.RepositoryGateBlock, IssueNumber = 10, Status = new WorkflowTaskStatus { Message = "Repository workflow is gated by issue #10." } }, new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 15 } });
    Xunit.Assert.Equal("Repository workflow is gated by issue #10.", blocked.BlockReason);
    var selected = HookService.SelectWorkflowTasks(new[] { new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 12 } }, new[] { new WorkflowItem { Type = WorkflowItemType.ChangeRequest, IssueNumber = 16, PullRequestNumber = 30 }, new WorkflowItem { Type = WorkflowItemType.ClosedWithoutMerge, IssueNumber = 17 } });
    Xunit.Assert.Equal(12, selected.Single().IssueNumber);
}

static Issue GatedIssue(int number, WorkflowState state, int? pullRequestNumber = null)
{
    var stateLabel = new RouterConfiguration().States[state].Single().Values.Single();
    var issue = new Issue
    {
        Number = number,
        State = "open",
        Labels = new List<GithubLabel>
        {
            new() { Name = stateLabel },
            new() { Name = "codex:gate" }
        }
    };
    if (pullRequestNumber.HasValue)
    {
        issue.ClosingPullRequestsReferences.Add(new ClosingIssueReference { Number = pullRequestNumber.Value });
    }
    return issue;
}

[Fact]
public void Hook_route_precedence_prefers_blockers_then_actionable_work()
{
    var blocker = new WorkflowItem { Type = WorkflowItemType.ClosedWithoutMerge, IssueNumber = 1, Status = new WorkflowTaskStatus { Message = "Closed without merge." } };
    var changeRequest = new WorkflowItem { Type = WorkflowItemType.ChangeRequest, IssueNumber = 2, PullRequestNumber = 20 };
    var linkPullRequest = new WorkflowItem { Type = WorkflowItemType.LinkPullRequestsToIssues, IssueNumber = 3 };
    var resume = new WorkflowItem { Type = WorkflowItemType.ResumeInProgressIssue, IssueNumber = 4 };
    var newIssue = new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 5 };

    Xunit.Assert.True(HookTaskRouter.Route(new[] { blocker, changeRequest, linkPullRequest, resume, newIssue }).BlockReason == "Closed without merge.", "Blockers must take precedence over every hook context.");
    Xunit.Assert.True(HookTaskRouter.Route(new[] { changeRequest, linkPullRequest, resume, newIssue }).AdditionalContext?.Contains("pull request #20", StringComparison.Ordinal) == true, "Change requests must take precedence after blockers.");
    var currentPullRequestRecovery = new WorkflowItem { Type = WorkflowItemType.RecoverCurrentPullRequest, IssueNumber = 6, PullRequestNumber = 21 };
    Xunit.Assert.True(HookTaskRouter.Route(new[] { currentPullRequestRecovery, newIssue }).AdditionalContext?.Contains("pull request #21", StringComparison.Ordinal) == true, "Current unlabeled PR recovery must return exact pull-request context.");
    var completedRecovery = new WorkflowItem { Type = WorkflowItemType.RecoverCompletedIssue, IssueNumber = 6 };
    Xunit.Assert.True(HookTaskRouter.Route(new[] { completedRecovery, newIssue }).AdditionalContext?.Contains("Recover the completed implementation for issue #6", StringComparison.Ordinal) == true, "Completed recovery must return runnable additional context instead of a blocker.");
    Xunit.Assert.True(HookTaskRouter.Route(new[] { linkPullRequest, resume, newIssue }).AdditionalContext?.Contains("following issues: 3", StringComparison.Ordinal) == true, "PR-linking work must take precedence over resume and new work.");
    Xunit.Assert.True(HookTaskRouter.Route(new[] { resume, newIssue }).AdditionalContext?.Contains("Issue #4 is already marked as working", StringComparison.Ordinal) == true, "Resume work must take precedence over new work.");
    Xunit.Assert.True(HookTaskRouter.Route(new[] { newIssue }).AdditionalContext?.Contains("issue #5", StringComparison.Ordinal) == true, "A ready issue should produce new-issue context when no higher-priority work exists.");
    Xunit.Assert.True(HookTaskRouter.Route(Array.Empty<WorkflowItem>()).BlockReason == "No actionable workflow tasks found.", "Empty work must use the safe fallback.");
}

[Fact]
public void AssertWorkflowLabelConflictResolution()
{
    var configuration = new RouterConfiguration();
    configuration.States[WorkflowState.Ready][0].Values.Add("codex:queued");

    var oneState = WorkflowStateResolver.Resolve(new[] { "unrelated", "codex:ready", "codex:queued" }, configuration.States);
    Xunit.Assert.True(!oneState.IsAmbiguous && oneState.MatchedLabels[WorkflowState.Ready].Count == 2, "Multiple labels configured for one state must be valid OR matches.");

    var conflict = WorkflowStateResolver.Resolve(new[] { "codex:ready", "codex:working" }, configuration.States);
    Xunit.Assert.True(conflict.IsAmbiguous && conflict.DescribeConflict("issue #4").Contains("codex:ready", StringComparison.Ordinal) && conflict.DescribeConflict("issue #4").Contains("codex:working", StringComparison.Ordinal), "Different issue states must be reported as an order-independent conflict.");

    var transition = IssueTransitionPlanner.Plan(new Issue { Number = 4, Labels = new List<GithubLabel> { new() { Name = "codex:ready" }, new() { Name = "codex:working" }, new() { Name = "unrelated" } } }, WorkflowState.Completed, configuration);
    Xunit.Assert.True(transition.LabelsToAdd.SequenceEqual(new[] { "codex:done" }) && transition.LabelsToRemove.OrderBy(label => label).SequenceEqual(new[] { "codex:ready", "codex:working" }), "A transition must repair a conflicting workflow label set without touching unrelated labels.");

    var reverseConflict = WorkflowStateResolver.Resolve(new[] { "codex:working", "codex:ready" }, configuration.States);
    Xunit.Assert.True(conflict.DescribeConflict("issue #4") == reverseConflict.DescribeConflict("issue #4"), "Conflict diagnostics must not depend on label order.");

    var pullRequestResolution = WorkflowStateResolver.Resolve(new[] { "codex:rr" }, configuration.PullRequestStates);
    Xunit.Assert.True(!pullRequestResolution.IsAmbiguous && !oneState.IsAmbiguous, "Issue and pull-request label domains must be resolved independently.");
}

[Fact]
public async Task AssertPullRequestLabelConflictHandlingAsync()
{
    var issue = new Issue { Number = 4 };
    issue.ClosingPullRequestsReferences.Add(new ClosingIssueReference { Number = 8 });
    var configuration = new RouterConfiguration();

    var openConflict = await WorkflowService.CheckIssueLinkedPullRequestsAsync(configuration, new[] { issue }, _ => Task.FromResult(new PullRequest { Number = 8, State = "open", Labels = new List<GithubLabel> { new() { Name = "codex:rr" }, new() { Name = "codex:cr" } } }));
    Xunit.Assert.True(openConflict.Tasks.Single().Type == WorkflowItemType.UnknownPullRequestState, "Conflicting labels on an open pull request must block routing.");

    var mergedStale = await WorkflowService.CheckIssueLinkedPullRequestsAsync(configuration, new[] { issue }, _ => Task.FromResult(new PullRequest { Number = 8, State = "merged", Labels = new List<GithubLabel> { new() { Name = "codex:rr" }, new() { Name = "codex:cr" } } }));
    Xunit.Assert.True(mergedStale.Tasks.Single().Type == WorkflowItemType.CloseIssue, "A merged pull request must close its issue despite stale conflicting labels.");
}

[Fact]
public void AssertIssueAliasSearchUsesOrSemantics()
{
    var query = GitHubCliService.BuildSearchQuery(new IssueFilters
    {
        Labels = new List<string> { "codex:ready", "codex:queued" },
        SearchTerms = new List<string> { "is:open" }
    });

    Xunit.Assert.True(query == "label:\"codex:ready\",\"codex:queued\" is:open", "Configured state-label aliases must be sent as one GitHub search OR group, not separate AND label filters.");
}

[Fact]
public void AssertVersionNormalization()
{
    Xunit.Assert.True(VersionFormatter.Normalize("0.0.1-alpha+23523532463463463") == "0.0.1-alpha", "Build metadata must be removed while preserving prerelease versions.");
    Xunit.Assert.True(VersionFormatter.Normalize("1.2.3") == "1.2.3", "Stable versions must remain unchanged.");
    Xunit.Assert.True(VersionFormatter.Normalize(null) == "Unknown", "Missing version metadata must use the safe fallback.");
}

[Fact]
public void AssertPassiveReviewDoesNotBlockNewWork()
{
    var decision = HookTaskRouter.Route(new[]
    {
        new WorkflowItem { Type = WorkflowItemType.AwaitingReview, IssueNumber = 1 },
        new WorkflowItem { Type = WorkflowItemType.AwaitingMerge, IssueNumber = 2 },
        new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 3 }
    });
    Xunit.Assert.True(decision.BlockReason is null && decision.SelectedTask?.IssueNumber == 3, "Passive review and merge states must not block unrelated ready work.");
}

[Fact]
public async Task Passive_current_claim_releases_and_allows_next_ready_work()
{
    var claim = ClaimForRoute();
    var enriched = ClaimForRoute(21);
    var result = await new ActiveClaimRouteService(_ => Task.FromResult(ClaimedResponse(WorkflowItemType.AwaitingReview, 21)), () => Task.FromResult<WorkClaim?>(enriched), _ => Task.FromResult(new WorkClaimAcquisitionResult { Acquired = true, Claim = enriched }), () => Task.FromResult(true)).RouteAsync(claim, "session-a");
    Xunit.Assert.Null(result);
    Xunit.Assert.Equal(5, HookTaskRouter.Route(new[] { ClaimedTask(WorkflowItemType.AwaitingReview, 21), new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 5 } }).SelectedTask?.IssueNumber);
}

[Fact]
public async Task Historical_claimed_pull_request_keeps_implementation_recovery_active()
{
    var claim = ClaimForRoute();
    var result = await new ActiveClaimRouteService(_ => Task.FromResult(ClaimedResponse(WorkflowItemType.ResumeInProgressIssue)), () => Task.FromResult<WorkClaim?>(claim), _ => Task.FromResult(new WorkClaimAcquisitionResult { Acquired = true, Claim = claim }), () => Task.FromResult(false)).RouteAsync(claim, "session-a");
    Xunit.Assert.Equal(WorkflowItemType.ResumeInProgressIssue, result?.SelectedTask?.Type);
}

[Fact]
public async Task Current_claimed_change_request_wins_after_state_race()
{
    var claim = ClaimForRoute();
    var enriched = ClaimForRoute(21);
    var result = await new ActiveClaimRouteService(current => Task.FromResult(current.PullRequestNumber is null ? ClaimedResponse(WorkflowItemType.AwaitingReview, 21) : ClaimedResponse(WorkflowItemType.ChangeRequest, 21)), () => Task.FromResult<WorkClaim?>(enriched), _ => Task.FromResult(new WorkClaimAcquisitionResult { Acquired = true, Claim = enriched }), () => Task.FromResult(false)).RouteAsync(claim, "session-a");
    Xunit.Assert.Equal(WorkflowItemType.ChangeRequest, result?.SelectedTask?.Type);
}

[Fact]
public async Task Generic_github_failure_preserves_claim_without_reconciliation()
{
    var called = false;
    var claim = ClaimForRoute();
    await Xunit.Assert.ThrowsAsync<InvalidOperationException>(() => new ActiveClaimRouteService(_ => throw new InvalidOperationException("HTTP 404: not found"), () => Task.FromResult<WorkClaim?>(claim), _ => throw new InvalidOperationException("must not change claim"), () => { called = true; return Task.FromResult(true); }).RouteAsync(claim, "session-a"));
    Xunit.Assert.False(called);
    Xunit.Assert.False(GitHubCliService.IsConfirmedNotFound("HTTP 404: not found"));
}

static WorkClaim ClaimForRoute(int? pullRequestNumber = null)
{
    var now = DateTimeOffset.UtcNow.AddMinutes(-5);
    return new WorkClaim { ClaimId = Guid.NewGuid(), Version = 1, OwnerSessionId = "session-a", IssueNumber = 4, PullRequestNumber = pullRequestNumber, WorkType = WorkClaimType.Implementation, ClaimedIssueUpdatedAt = now, ClaimedAt = now, LastUpdatedAt = now };
}

static WorkflowItem ClaimedTask(WorkflowItemType type, int? pullRequestNumber = null, int issueNumber = 4, string? message = null) => new()
{
    Type = type,
    IssueNumber = issueNumber,
    PullRequestNumber = pullRequestNumber,
    Status = new WorkflowTaskStatus { Message = message ?? $"Claimed task {type}." }
};

static WorkflowResponse ClaimedResponse(WorkflowItemType type, int? pullRequestNumber = null, int issueNumber = 4, string? message = null) => new()
{
    Tasks = new List<WorkflowItem> { ClaimedTask(type, pullRequestNumber, issueNumber, message) }
};

static PullRequest PullRequestForClaim(WorkClaim claim, string branch, string state, string? label = null, DateTimeOffset? createdAt = null) => new()
{
    Number = claim.PullRequestNumber ?? 21,
    State = state,
    CreatedAt = createdAt ?? claim.ClaimedAt.AddMinutes(1),
    HeadRefName = branch,
    Labels = label is null ? new List<GithubLabel>() : new List<GithubLabel> { new() { Name = label } },
    ClosingIssuesReferences = new List<ClosingIssueReference> { new() { Number = claim.IssueNumber } }
};

}

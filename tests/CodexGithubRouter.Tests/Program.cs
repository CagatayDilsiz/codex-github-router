using CodexGithubRouter.GitHub;
using CodexGithubRouter.Helpers;
using CodexGithubRouter.Hooks;
using CodexGithubRouter.Prompts;
using CodexGithubRouter.Work;
using CodexGithubRouter.Workflow;

await AssertSingleWorkingIssueWithoutPullRequestAsync();
await AssertMultipleWorkingIssuesBlockAsync();
await AssertWorkingIssueWithOpenPullRequestAsync();
AssertResumePromptIsSafe();
AssertHookOutputResumesWorkingIssueBeforeReadyIssue();
AssertHookRoutePrecedence();
AssertWorkflowLabelConflictResolution();
await AssertPullRequestLabelConflictHandlingAsync();
AssertIssueAliasSearchUsesOrSemantics();
AssertVersionNormalization();
await AssertWorkClaimsAsync();
AssertPassiveReviewDoesNotBlockNewWork();
AssertWorkClaimReconciliation();
AssertClaimRoutingAuthority();
await AssertPullRequestTransitionLifecycleAsync();
await AssertClaimedWorkRecoveryAsync();
await AssertActiveClaimRouteOrchestrationAsync();
AssertClarificationPromptIsLanguageIndependent();
AssertRepositoryGateConfiguration();
await AssertRepositoryGateEvaluationAsync();

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
    Assert(prompt.Contains("Zero pull requests means interrupted work", StringComparison.Ordinal), "The resume prompt should resume interrupted branch work without an early pull request.");
    Assert(prompt.Contains("Fixes #4", StringComparison.Ordinal) && prompt.Contains("next hook invocation", StringComparison.Ordinal), "The resume prompt should link a discovered open pull request and defer further handling to the normal workflow.");
    Assert(prompt.Contains("One closed pull request", StringComparison.Ordinal) && prompt.Contains("Multiple pull requests", StringComparison.Ordinal), "The resume prompt should cover closed and ambiguous recovered-branch pull request states.");
    Assert(prompt.Contains("Do not recreate the branch", StringComparison.Ordinal), "The resume prompt must prevent duplicate work.");

    var completedRecoveryPrompt = ContextPromptService.GetCompletedIssueRecoveryPrompt(4);
    Assert(completedRecoveryPrompt.Contains("git branch --all --list \"codex/issue-4-*\"", StringComparison.Ordinal) && completedRecoveryPrompt.Contains("gh pr list --head <candidate-branch> --state all", StringComparison.Ordinal), "Completed recovery must inspect exact branch candidates and every pull-request state.");
    Assert(completedRecoveryPrompt.Contains("create exactly one pull request", StringComparison.Ordinal) && completedRecoveryPrompt.Contains("Fixes #4", StringComparison.Ordinal), "Completed recovery must provide executable single-PR linking instructions.");
    var currentPullRequestRecoveryPrompt = ContextPromptService.GetCurrentPullRequestRecoveryPrompt(4, 21);
    Assert(currentPullRequestRecoveryPrompt.Contains("pull request #21", StringComparison.Ordinal) && currentPullRequestRecoveryPrompt.Contains("cgr pr transition 21 ready-for-review", StringComparison.Ordinal) && currentPullRequestRecoveryPrompt.Contains("cgr issue transition 4 completed", StringComparison.Ordinal), "An unlabeled current PR must receive exact lifecycle recovery instructions.");
}

static void AssertClarificationPromptIsLanguageIndependent()
{
    var prompt = ContextPromptService.GetNewIssuePrompt(12);
    const string canonicalNotice = "> 🤖 This clarification request was generated automatically by a Codex session through CGR. After providing the requested information, transition this issue back to `ready`.";
    var noticeInstruction = prompt.IndexOf(canonicalNotice, StringComparison.Ordinal);
    var questionInstruction = prompt.IndexOf("actual clarification question must follow the notice", StringComparison.Ordinal);

    Assert(noticeInstruction >= 0 && questionInstruction > noticeInstruction, "Clarification comments must start with a blockquote notice before the question.");
    Assert(prompt.Contains("visible Markdown blockquote notice", StringComparison.Ordinal), "The notice must be a visible Markdown blockquote.");
    Assert(prompt.Contains("translate it naturally when the issue or clarification conversation is not in English", StringComparison.Ordinal), "Non-English clarification notices must be translated naturally.");
    Assert(prompt.Contains("Keep `Codex`, `CGR`, and `ready` unchanged", StringComparison.Ordinal), "The canonical identifiers must remain unchanged when translating the notice.");
    Assert(prompt.Contains("cgr issue transition 12 needs-info", StringComparison.Ordinal), "The existing needs-info transition must remain in the clarification instructions.");
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

static void AssertRepositoryGateConfiguration()
{
    var configuration = new RouterConfiguration();
    Assert(RepositoryGateService.GetLabels(configuration).SequenceEqual(new[] { "codex:gate" }), "The default repository gate must use codex:gate.");
    Assert(WorkflowLabelConfiguration.GetRequiredLabels(configuration).Contains("codex:gate", StringComparer.OrdinalIgnoreCase), "The repository gate label must be provisioned with the managed labels.");

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
    Assert(!transition.LabelsToRemove.Contains("codex:gate", StringComparer.OrdinalIgnoreCase), "Issue transitions must preserve the repository gate label.");

    configuration.Policies.RepositoryGate.Labels.Add("codex:critical");
    Assert(RepositoryGateService.IsGated(new Issue { Labels = new List<GithubLabel> { new() { Name = "codex:critical" } } }, configuration), "Configured repository gate aliases must use OR semantics.");
}

static async Task AssertRepositoryGateEvaluationAsync()
{
    var configuration = new RouterConfiguration();
    var reviewIssue = GatedIssue(10, WorkflowState.Completed, 20);
    var review = await WorkflowService.EvaluateRepositoryGateAsync(configuration, new[] { reviewIssue }, _ => Task.FromResult(new PullRequest
    {
        Number = 20,
        State = "open",
        Labels = new List<GithubLabel> { new() { Name = "codex:rr" } }
    }));
    Assert(review.Tasks.Single().Type == WorkflowItemType.RepositoryGateBlock && review.Tasks.Single().Status.Message.Contains("issue #10", StringComparison.Ordinal) && review.Tasks.Single().Status.Message.Contains("Pull request #20", StringComparison.Ordinal), "A gated review-requested workstream must block unrelated work with exact issue and pull-request context.");

    var changes = await WorkflowService.EvaluateRepositoryGateAsync(configuration, new[] { GatedIssue(11, WorkflowState.Completed, 21) }, _ => Task.FromResult(new PullRequest
    {
        Number = 21,
        State = "open",
        Labels = new List<GithubLabel> { new() { Name = "codex:cr" } }
    }));
    Assert(changes.Tasks.Single().Type == WorkflowItemType.ChangeRequest, "A gated change request must remain actionable and claimable.");

    var ready = await WorkflowService.EvaluateRepositoryGateAsync(configuration, new[] { GatedIssue(12, WorkflowState.Ready) }, _ => throw new InvalidOperationException("A gated ready issue should not query pull requests."));
    Assert(ready.Tasks.Single().Type == WorkflowItemType.NewIssue, "A gated ready issue must be prioritized as new work.");

    var merged = await WorkflowService.EvaluateRepositoryGateAsync(configuration, new[] { GatedIssue(14, WorkflowState.Completed, 22) }, _ => Task.FromResult(new PullRequest
    {
        Number = 22,
        State = "merged",
        Labels = new List<GithubLabel>()
    }));
    Assert(merged.Tasks.Count == 0, "A merged pull request must make its repository gate terminal.");

    var gateDecision = HookTaskRouter.Route(new[]
    {
        new WorkflowItem { Type = WorkflowItemType.ChangeRequest, IssueNumber = 11, PullRequestNumber = 21 },
        new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 15 }
    });
    Assert(gateDecision.SelectedTask?.IssueNumber == 11, "Gated actionable work must win over ordinary ready work when it is evaluated first.");

    var blockedGateDecision = HookTaskRouter.Route(new[]
    {
        new WorkflowItem { Type = WorkflowItemType.RepositoryGateBlock, IssueNumber = 10, Status = new WorkflowTaskStatus { Message = "Repository workflow is gated by issue #10." } },
        new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 15 }
    });
    Assert(blockedGateDecision.BlockReason == "Repository workflow is gated by issue #10.", "A gated waiting workstream must block ordinary ready work.");
}

static Issue GatedIssue(int number, WorkflowState state, int? pullRequestNumber = null)
{
    var stateLabel = new RouterConfiguration().States[state].Single().Values.Single();
    var issue = new Issue
    {
        Number = number,
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

static void AssertHookRoutePrecedence()
{
    var blocker = new WorkflowItem { Type = WorkflowItemType.ClosedWithoutMerge, IssueNumber = 1, Status = new WorkflowTaskStatus { Message = "Closed without merge." } };
    var changeRequest = new WorkflowItem { Type = WorkflowItemType.ChangeRequest, IssueNumber = 2, PullRequestNumber = 20 };
    var linkPullRequest = new WorkflowItem { Type = WorkflowItemType.LinkPullRequestsToIssues, IssueNumber = 3 };
    var resume = new WorkflowItem { Type = WorkflowItemType.ResumeInProgressIssue, IssueNumber = 4 };
    var newIssue = new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 5 };

    Assert(HookTaskRouter.Route(new[] { blocker, changeRequest, linkPullRequest, resume, newIssue }).BlockReason == "Closed without merge.", "Blockers must take precedence over every hook context.");
    Assert(HookTaskRouter.Route(new[] { changeRequest, linkPullRequest, resume, newIssue }).AdditionalContext?.Contains("pull request #20", StringComparison.Ordinal) == true, "Change requests must take precedence after blockers.");
    var currentPullRequestRecovery = new WorkflowItem { Type = WorkflowItemType.RecoverCurrentPullRequest, IssueNumber = 6, PullRequestNumber = 21 };
    Assert(HookTaskRouter.Route(new[] { currentPullRequestRecovery, newIssue }).AdditionalContext?.Contains("pull request #21", StringComparison.Ordinal) == true, "Current unlabeled PR recovery must return exact pull-request context.");
    var completedRecovery = new WorkflowItem { Type = WorkflowItemType.RecoverCompletedIssue, IssueNumber = 6 };
    Assert(HookTaskRouter.Route(new[] { completedRecovery, newIssue }).AdditionalContext?.Contains("Recover the completed implementation for issue #6", StringComparison.Ordinal) == true, "Completed recovery must return runnable additional context instead of a blocker.");
    Assert(HookTaskRouter.Route(new[] { linkPullRequest, resume, newIssue }).AdditionalContext?.Contains("following issues: 3", StringComparison.Ordinal) == true, "PR-linking work must take precedence over resume and new work.");
    Assert(HookTaskRouter.Route(new[] { resume, newIssue }).AdditionalContext?.Contains("Issue #4 is already marked as working", StringComparison.Ordinal) == true, "Resume work must take precedence over new work.");
    Assert(HookTaskRouter.Route(new[] { newIssue }).AdditionalContext?.Contains("issue #5", StringComparison.Ordinal) == true, "A ready issue should produce new-issue context when no higher-priority work exists.");
    Assert(HookTaskRouter.Route(Array.Empty<WorkflowItem>()).BlockReason == "No actionable workflow tasks found.", "Empty work must use the safe fallback.");
}

static void AssertWorkflowLabelConflictResolution()
{
    var configuration = new RouterConfiguration();
    configuration.States[WorkflowState.Ready][0].Values.Add("codex:queued");

    var oneState = WorkflowStateResolver.Resolve(new[] { "unrelated", "codex:ready", "codex:queued" }, configuration.States);
    Assert(!oneState.IsAmbiguous && oneState.MatchedLabels[WorkflowState.Ready].Count == 2, "Multiple labels configured for one state must be valid OR matches.");

    var conflict = WorkflowStateResolver.Resolve(new[] { "codex:ready", "codex:working" }, configuration.States);
    Assert(conflict.IsAmbiguous && conflict.DescribeConflict("issue #4").Contains("codex:ready", StringComparison.Ordinal) && conflict.DescribeConflict("issue #4").Contains("codex:working", StringComparison.Ordinal), "Different issue states must be reported as an order-independent conflict.");

    var transition = IssueTransitionPlanner.Plan(new Issue { Number = 4, Labels = new List<GithubLabel> { new() { Name = "codex:ready" }, new() { Name = "codex:working" }, new() { Name = "unrelated" } } }, WorkflowState.Completed, configuration);
    Assert(transition.LabelsToAdd.SequenceEqual(new[] { "codex:done" }) && transition.LabelsToRemove.OrderBy(label => label).SequenceEqual(new[] { "codex:ready", "codex:working" }), "A transition must repair a conflicting workflow label set without touching unrelated labels.");

    var reverseConflict = WorkflowStateResolver.Resolve(new[] { "codex:working", "codex:ready" }, configuration.States);
    Assert(conflict.DescribeConflict("issue #4") == reverseConflict.DescribeConflict("issue #4"), "Conflict diagnostics must not depend on label order.");

    var pullRequestResolution = WorkflowStateResolver.Resolve(new[] { "codex:rr" }, configuration.PullRequestStates);
    Assert(!pullRequestResolution.IsAmbiguous && !oneState.IsAmbiguous, "Issue and pull-request label domains must be resolved independently.");
}

static async Task AssertPullRequestLabelConflictHandlingAsync()
{
    var issue = new Issue { Number = 4 };
    issue.ClosingPullRequestsReferences.Add(new ClosingIssueReference { Number = 8 });
    var configuration = new RouterConfiguration();

    var openConflict = await WorkflowService.CheckIssueLinkedPullRequestsAsync(configuration, new[] { issue }, _ => Task.FromResult(new PullRequest { Number = 8, State = "open", Labels = new List<GithubLabel> { new() { Name = "codex:rr" }, new() { Name = "codex:cr" } } }));
    Assert(openConflict.Tasks.Single().Type == WorkflowItemType.UnknownPullRequestState, "Conflicting labels on an open pull request must block routing.");

    var mergedStale = await WorkflowService.CheckIssueLinkedPullRequestsAsync(configuration, new[] { issue }, _ => Task.FromResult(new PullRequest { Number = 8, State = "merged", Labels = new List<GithubLabel> { new() { Name = "codex:rr" }, new() { Name = "codex:cr" } } }));
    Assert(mergedStale.Tasks.Single().Type == WorkflowItemType.CloseIssue, "A merged pull request must close its issue despite stale conflicting labels.");
}

static void AssertIssueAliasSearchUsesOrSemantics()
{
    var query = GitHubCliService.BuildSearchQuery(new IssueFilters
    {
        Labels = new List<string> { "codex:ready", "codex:queued" },
        SearchTerms = new List<string> { "is:open" }
    });

    Assert(query == "label:\"codex:ready\",\"codex:queued\" is:open", "Configured state-label aliases must be sent as one GitHub search OR group, not separate AND label filters.");
}

static void AssertVersionNormalization()
{
    Assert(VersionFormatter.Normalize("0.0.1-alpha+23523532463463463") == "0.0.1-alpha", "Build metadata must be removed while preserving prerelease versions.");
    Assert(VersionFormatter.Normalize("1.2.3") == "1.2.3", "Stable versions must remain unchanged.");
    Assert(VersionFormatter.Normalize(null) == "Unknown", "Missing version metadata must use the safe fallback.");
}

static async Task AssertWorkClaimsAsync()
{
    var directory = Path.Combine(Path.GetTempPath(), $"cgr-work-claim-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(number => WorkClaimStore.TryAcquireAsync(directory, new WorkClaim { OwnerSessionId = $"session-{number}", IssueNumber = 4, WorkType = WorkClaimType.Implementation })));
    Assert(attempts.Count(result => result.Acquired) == 1, "Simultaneous claim attempts must produce exactly one owner.");
    var owner = attempts.Single(result => result.Acquired).Claim!.OwnerSessionId;
    var continuation = await WorkClaimStore.TryAcquireAsync(directory, new WorkClaim { OwnerSessionId = owner, IssueNumber = 4, WorkType = WorkClaimType.Implementation });
    Assert(continuation.Acquired, "The owning session must be able to continue its existing claim.");
    var otherSession = await WorkClaimStore.TryAcquireAsync(directory, new WorkClaim { OwnerSessionId = "session-other", IssueNumber = 4, WorkType = WorkClaimType.Implementation });
    Assert(!otherSession.Acquired && otherSession.BlockReason?.Contains("another Codex session", StringComparison.Ordinal) == true, "A different session must be blocked from the same claim.");
    var otherIssue = await WorkClaimStore.TryAcquireAsync(directory, new WorkClaim { OwnerSessionId = owner, IssueNumber = 5, WorkType = WorkClaimType.Implementation });
    Assert(!otherIssue.Acquired, "A repository may have only one active claim.");
    Assert(await WorkClaimStore.ReleaseForIssueAsync(directory, 4), "An explicit release must remove the current claim.");
    Assert((await WorkClaimStore.TryAcquireAsync(directory, new WorkClaim { OwnerSessionId = owner, IssueNumber = 5, WorkType = WorkClaimType.Implementation })).Acquired, "The same session must be able to claim new work after release.");
    Directory.Delete(directory, true);

    var baselineDirectory = Path.Combine(Path.GetTempPath(), $"cgr-work-baseline-{Guid.NewGuid():N}");
    Directory.CreateDirectory(baselineDirectory);
    var issueUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
    var baselineClaim = (await WorkClaimStore.TryAcquireAsync(baselineDirectory, new WorkClaim
    {
        OwnerSessionId = "session-a",
        IssueNumber = 4,
        WorkType = WorkClaimType.Implementation,
        ClaimedIssueUpdatedAt = issueUpdatedAt
    })).Claim!;
    Assert(baselineClaim.ClaimedIssueUpdatedAt == issueUpdatedAt, "A newly acquired claim must persist the GitHub-derived issue updatedAt baseline.");
    Directory.Delete(baselineDirectory, true);

    var identityDirectory = Path.Combine(Path.GetTempPath(), $"cgr-work-identity-{Guid.NewGuid():N}");
    Directory.CreateDirectory(identityDirectory);
    var first = await WorkClaimStore.TryAcquireAsync(identityDirectory, new WorkClaim { OwnerSessionId = "session-a", IssueNumber = 4, PullRequestNumber = 21, WorkType = WorkClaimType.ChangeRequest });
    var differentPullRequest = await WorkClaimStore.TryAcquireAsync(identityDirectory, new WorkClaim { OwnerSessionId = "session-a", IssueNumber = 4, PullRequestNumber = 22, WorkType = WorkClaimType.ChangeRequest });
    Assert(first.Acquired && !differentPullRequest.Acquired && (await WorkClaimStore.ReadAsync(identityDirectory))?.PullRequestNumber == 21, "Same-issue claims with different pull requests must remain different work identities.");
    var staleClaim = (await WorkClaimStore.ReadAsync(identityDirectory))!;
    await WorkClaimStore.ReleaseForIssueAsync(identityDirectory, 4);
    var replacement = (await WorkClaimStore.TryAcquireAsync(identityDirectory, new WorkClaim { OwnerSessionId = "session-b", IssueNumber = 4, PullRequestNumber = 22, WorkType = WorkClaimType.ChangeRequest })).Claim!;
    Assert(!await WorkClaimStore.ReleaseIfMatchesAsync(identityDirectory, staleClaim) && (await WorkClaimStore.ReadAsync(identityDirectory))?.ClaimId == replacement.ClaimId, "Stale reconciliation must not delete a replacement claim.");
    Directory.Delete(identityDirectory, true);

    var enrichmentDirectory = Path.Combine(Path.GetTempPath(), $"cgr-work-enrichment-{Guid.NewGuid():N}");
    Directory.CreateDirectory(enrichmentDirectory);
    await WorkClaimStore.TryAcquireAsync(enrichmentDirectory, new WorkClaim { OwnerSessionId = "session-a", IssueNumber = 4, WorkType = WorkClaimType.Implementation });
    var enriched = await WorkClaimStore.TryAcquireAsync(enrichmentDirectory, new WorkClaim { OwnerSessionId = "session-a", IssueNumber = 4, PullRequestNumber = 21, WorkType = WorkClaimType.ChangeRequest });
    Assert(enriched.Acquired && enriched.Claim?.PullRequestNumber == 21 && enriched.Claim.WorkType == WorkClaimType.Implementation, "A same-owner claim may enrich a missing pull request without changing work type.");
    Directory.Delete(enrichmentDirectory, true);
}

static void AssertPassiveReviewDoesNotBlockNewWork()
{
    var decision = HookTaskRouter.Route(new[]
    {
        new WorkflowItem { Type = WorkflowItemType.AwaitingReview, IssueNumber = 1 },
        new WorkflowItem { Type = WorkflowItemType.AwaitingMerge, IssueNumber = 2 },
        new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 3 }
    });
    Assert(decision.BlockReason is null && decision.SelectedTask?.IssueNumber == 3, "Passive review and merge states must not block unrelated ready work.");
}

static void AssertWorkClaimReconciliation()
{
    var configuration = new RouterConfiguration();
    var claim = new WorkClaim { OwnerSessionId = "session-a", IssueNumber = 4, PullRequestNumber = 22, WorkType = WorkClaimType.ChangeRequest };
    var working = WorkingIssue(4);
    var oldReviewRequested = new PullRequest { Number = 8, State = "open", Labels = new List<GithubLabel> { new() { Name = "codex:rr" } } };
    var changeRequested = new PullRequest { Number = 22, State = "open", Labels = new List<GithubLabel> { new() { Name = "codex:cr" } } };
    var claimedPullRequest = WorkClaimReconciliationService.SelectClaimedPullRequest(claim, new[] { oldReviewRequested, changeRequested });
    Assert(claimedPullRequest?.Number == 22 && !WorkClaimReconciliationService.ShouldRelease(claim, working, claimedPullRequest, configuration), "An older passive pull request must not release an active claim for another pull request on the same issue.");
    var reviewRequested = new PullRequest { Number = 22, State = "open", Labels = new List<GithubLabel> { new() { Name = "codex:rr" } } };
    Assert(WorkClaimReconciliationService.ShouldRelease(claim, working, reviewRequested, configuration), "The claimed review-requested pull request must release its claim.");
    var implementationClaim = new WorkClaim { OwnerSessionId = "session-a", IssueNumber = 4, WorkType = WorkClaimType.Implementation };
    Assert(!WorkClaimReconciliationService.ShouldRelease(implementationClaim, working, oldReviewRequested, configuration), "A pull-request-less implementation claim must ignore older passive linked pull requests.");
    var baseline = DateTimeOffset.UtcNow.AddMinutes(-10);
    var baselineClaim = new WorkClaim { OwnerSessionId = "session-a", IssueNumber = 4, WorkType = WorkClaimType.Implementation, ClaimedIssueUpdatedAt = baseline, ClaimedAt = baseline.AddDays(1) };
    var completedIssue = new Issue
    {
        Number = 4,
        Labels = new List<GithubLabel> { new() { Name = "codex:done" } },
        ClosingPullRequestsReferences = new List<ClosingIssueReference> { new() { Number = 21 }, new() { Number = 22 }, new() { Number = 23 }, new() { Number = 24 } }
    };
    var historicalPassive = PullRequestForClaim(baselineClaim, "codex/issue-4-old", "open", "codex:rr", baseline.AddMinutes(-1));
    var currentPassive = PullRequestForClaim(baselineClaim, "codex/issue-4-current", "open", "codex:rr", baseline.AddMinutes(1));
    var secondHistoricalPassive = PullRequestForClaim(baselineClaim, "codex/issue-4-old-2", "open", "codex:deferred", baseline.AddMinutes(-2));
    var currentChangesRequested = PullRequestForClaim(baselineClaim, "codex/issue-4-current-cr", "open", "codex:cr", baseline.AddMinutes(2));
    var currentCandidates = WorkClaimReconciliationService.SelectCurrentClaimPullRequests(baselineClaim, completedIssue, new[] { historicalPassive, currentPassive });
    Assert(currentCandidates.Count == 1 && WorkClaimReconciliationService.IsPassiveOrTerminal(currentCandidates[0], configuration), "Historical plus one current passive PR must select exactly the current PR for release evaluation.");
    var currentWithMultipleHistorical = WorkClaimReconciliationService.SelectCurrentClaimPullRequests(baselineClaim, completedIssue, new[] { historicalPassive, secondHistoricalPassive, currentPassive });
    Assert(currentWithMultipleHistorical.Count == 1, "Multiple historical PR references must not hide one current passive PR.");
    var twoCurrentCandidates = WorkClaimReconciliationService.SelectCurrentClaimPullRequests(baselineClaim, completedIssue, new[] { currentPassive, currentChangesRequested });
    Assert(twoCurrentCandidates.Count == 2, "Two current candidate PRs must remain ambiguous instead of releasing the claim.");
    var currentActiveOnly = WorkClaimReconciliationService.SelectCurrentClaimPullRequests(baselineClaim, completedIssue, new[] { historicalPassive, currentChangesRequested });
    Assert(currentActiveOnly.Count == 1 && !WorkClaimReconciliationService.IsPassiveOrTerminal(currentActiveOnly[0], configuration), "A historical passive PR plus a current active CR must retain the claim.");
    var terminal = WorkingIssue(4);
    terminal.Labels = new List<GithubLabel> { new() { Name = "codex:blocked" } };
    Assert(WorkClaimReconciliationService.ShouldRelease(claim, terminal, null, configuration), "Blocked work must release its claim immediately.");
    Assert(WorkClaimReconciliationService.ShouldReleaseForPullRequestTransition(claim, 22, PullRequestState.ReviewRequested) && !WorkClaimReconciliationService.ShouldReleaseForPullRequestTransition(claim, 21, PullRequestState.ReviewRequested), "A pull-request transition must release only the matching claimed pull request.");
}

static void AssertClaimRoutingAuthority()
{
    var claim = new WorkClaim { OwnerSessionId = "session-b", IssueNumber = 2, WorkType = WorkClaimType.Implementation };
    var unrelatedChangeRequest = new WorkflowItem { Type = WorkflowItemType.ChangeRequest, IssueNumber = 1, PullRequestNumber = 10 };
    var claimedResume = new WorkflowItem { Type = WorkflowItemType.ResumeInProgressIssue, IssueNumber = 2 };
    var ownerDecision = HookTaskRouter.RouteClaimedWork(claim, "session-b", new[] { unrelatedChangeRequest, claimedResume });
    Assert(ownerDecision.SelectedTask?.IssueNumber == 2, "An active claim owner must continue claimed work before unrelated change requests.");
    var otherSessionDecision = HookTaskRouter.RouteClaimedWork(claim, "session-a", new[] { new WorkflowItem { Type = WorkflowItemType.LinkPullRequestsToIssues, IssueNumber = 1 }, claimedResume });
    Assert(otherSessionDecision.BlockReason?.Contains("another Codex session", StringComparison.Ordinal) == true && otherSessionDecision.AdditionalContext is null, "A different session must not receive PR-link context while a claim is active.");
    var missingDecision = HookTaskRouter.RouteClaimedWork(claim, "session-b", new[] { unrelatedChangeRequest });
    Assert(missingDecision.BlockReason?.Contains("No unrelated work will be routed", StringComparison.Ordinal) == true && missingDecision.AdditionalContext is null, "Missing claimed work must not fall through to unrelated workflow tasks.");
    var ambiguousClaim = new WorkClaim { OwnerSessionId = "session-b", IssueNumber = 2, WorkType = WorkClaimType.Implementation };
    var ambiguousDecision = HookTaskRouter.RouteClaimedWork(ambiguousClaim, "session-b", new[] { new WorkflowItem { Type = WorkflowItemType.ChangeRequest, IssueNumber = 2, PullRequestNumber = 21 }, new WorkflowItem { Type = WorkflowItemType.ChangeRequest, IssueNumber = 2, PullRequestNumber = 22 } });
    Assert(ambiguousDecision.BlockReason?.Contains("multiple candidate pull requests", StringComparison.Ordinal) == true, "A PR-less claim must not implicitly choose between distinct pull-request identities.");
}

static async Task AssertPullRequestTransitionLifecycleAsync()
{
    var directory = Path.Combine(Path.GetTempPath(), $"cgr-work-transition-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    var initial = await WorkClaimStore.TryAcquireAsync(directory, new WorkClaim { OwnerSessionId = "session-a", IssueNumber = 4, WorkType = WorkClaimType.Implementation });
    Assert(!await WorkClaimStore.ReleaseForPullRequestTransitionAsync(directory, initial.Claim!, 18, new[] { 4 }, true) && await WorkClaimStore.ReadAsync(directory) is not null, "A PR-less implementation claim must retain ownership when a passive historical pull request has not been proven current.");
    Assert(await WorkClaimStore.ReleaseForPullRequestTransitionAsync(directory, initial.Claim!, 18, new[] { 4 }, true, true) && await WorkClaimStore.ReadAsync(directory) is null, "A PR-less implementation claim must release only after the transitioned pull request has been proven current.");

    var unrelated = await WorkClaimStore.TryAcquireAsync(directory, new WorkClaim { OwnerSessionId = "session-a", IssueNumber = 4, WorkType = WorkClaimType.Implementation });
    Assert(!await WorkClaimStore.ReleaseForPullRequestTransitionAsync(directory, unrelated.Claim!, 19, new[] { 5 }, true) && await WorkClaimStore.ReadAsync(directory) is not null, "An unrelated pull-request transition must retain a PR-less implementation claim.");
    await WorkClaimStore.ReleaseForIssueAsync(directory, 4);

    var claimedPullRequest = await WorkClaimStore.TryAcquireAsync(directory, new WorkClaim { OwnerSessionId = "session-a", IssueNumber = 4, PullRequestNumber = 21, WorkType = WorkClaimType.ChangeRequest });
    Assert(!await WorkClaimStore.ReleaseForPullRequestTransitionAsync(directory, claimedPullRequest.Claim!, 22, new[] { 4 }, true) && await WorkClaimStore.ReadAsync(directory) is not null, "A transition for a different pull request must retain the claimed pull request.");
    Assert(await WorkClaimStore.ReleaseForPullRequestTransitionAsync(directory, claimedPullRequest.Claim!, 21, new[] { 4 }, true), "A retry after a passive matching pull-request transition must clean up the matching claim even when labels are already correct.");
    Assert(WorkClaimReconciliationService.ShouldReleaseForIssueTransition(new WorkClaim { IssueNumber = 4 }, 4, WorkflowState.Blocked), "A no-op terminal issue transition must remain eligible for cleanup.");
    Directory.Delete(directory, true);
}

static async Task AssertClaimedWorkRecoveryAsync()
{
    var configuration = new RouterConfiguration();
    var claimedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
    var claim = new WorkClaim { ClaimId = Guid.NewGuid(), Version = 1, OwnerSessionId = "session-a", IssueNumber = 4, WorkType = WorkClaimType.Implementation, ClaimedIssueUpdatedAt = claimedAt, ClaimedAt = claimedAt, LastUpdatedAt = claimedAt };
    var ready = new Issue { Number = 4, Labels = new List<GithubLabel> { new() { Name = "codex:ready" } } };
    Assert((await WorkflowService.EvaluateClaimedWorkAsync(configuration, claim, ready, _ => throw new InvalidOperationException())).Tasks.Single().Type == WorkflowItemType.NewIssue, "A PR-less claim must recover a ready issue through the new-issue flow.");
    var working = WorkingIssue(4);
    Assert((await WorkflowService.EvaluateClaimedWorkAsync(configuration, claim, working, _ => throw new InvalidOperationException())).Tasks.Single().Type == WorkflowItemType.ResumeInProgressIssue, "A working PR-less claim without a linked pull request must resume implementation.");
    working.ClosingPullRequestsReferences.Add(new ClosingIssueReference { Number = 21 });
    var historicalPassivePullRequest = PullRequestForClaim(claim, "codex/issue-4-old", "open", "codex:rr", createdAt: claimedAt.AddMinutes(-1));
    Assert((await WorkflowService.EvaluateClaimedWorkAsync(configuration, claim, working, _ => Task.FromResult(historicalPassivePullRequest))).Tasks.Single().Type == WorkflowItemType.ResumeInProgressIssue, "A new PR-less implementation claim must ignore an old passive linked pull request while the issue is working.");
    var currentWorkingPullRequest = PullRequestForClaim(claim, "codex/issue-4-current", "open", "codex:cr");
    Assert((await WorkflowService.EvaluateClaimedWorkAsync(configuration, claim, working, _ => Task.FromResult(currentWorkingPullRequest))).Tasks.Single().Type == WorkflowItemType.ChangeRequest, "A PR-less working claim must recover a proven current linked pull request after a crash.");
    var currentUnlabeledPullRequest = PullRequestForClaim(claim, "codex/issue-4-current-unlabeled", "open");
    Assert((await WorkflowService.EvaluateClaimedWorkAsync(configuration, claim, working, _ => Task.FromResult(currentUnlabeledPullRequest))).Tasks.Single().Type == WorkflowItemType.RecoverCurrentPullRequest, "A proven current unlabeled PR must return runnable lifecycle recovery while working.");
    var historicalUnlabeledPullRequest = PullRequestForClaim(claim, "codex/issue-4-historical-unlabeled", "open", createdAt: claimedAt.AddMinutes(-1));
    Assert((await WorkflowService.EvaluateClaimedWorkAsync(configuration, claim, working, _ => Task.FromResult(historicalUnlabeledPullRequest))).Tasks.Single().Type == WorkflowItemType.ResumeInProgressIssue, "A historical unlabeled PR must not be selected as current recovery work.");
    var clockAheadClaim = new WorkClaim { ClaimId = claim.ClaimId, Version = claim.Version, OwnerSessionId = claim.OwnerSessionId, IssueNumber = claim.IssueNumber, WorkType = claim.WorkType, ClaimedIssueUpdatedAt = claimedAt, ClaimedAt = claimedAt.AddDays(1), LastUpdatedAt = claim.LastUpdatedAt };
    var clockAheadPullRequest = PullRequestForClaim(clockAheadClaim, "codex/issue-4-clock-ahead", "open", "codex:cr", claimedAt.AddMinutes(1));
    Assert(WorkflowService.IsCurrentClaimPullRequest(clockAheadClaim, working, clockAheadPullRequest), "A local clock ahead of GitHub must not reject a PR created after the GitHub claim baseline.");
    var clockBehindPullRequest = PullRequestForClaim(claim, "codex/issue-4-clock-behind", "open", "codex:cr", claimedAt.AddMinutes(-1));
    Assert(!WorkflowService.IsCurrentClaimPullRequest(claim, working, clockBehindPullRequest), "A local clock behind GitHub must not accept a PR created before the GitHub claim baseline.");
    var completed = new Issue { Number = 4, Labels = new List<GithubLabel> { new() { Name = "codex:done" } } };
    Assert((await WorkflowService.EvaluateClaimedWorkAsync(configuration, claim, completed, _ => throw new InvalidOperationException())).Tasks.Single().Type == WorkflowItemType.RecoverCompletedIssue, "A completed PR-less claim without a linked pull request must recover executable branch and PR work.");
    completed.ClosingPullRequestsReferences.Add(new ClosingIssueReference { Number = 21 });
    var historicalCompleted = await WorkflowService.EvaluateClaimedWorkAsync(configuration, claim, completed, _ => Task.FromResult(historicalPassivePullRequest));
    Assert(historicalCompleted.IsSuccessful && historicalCompleted.Tasks.Single().Type == WorkflowItemType.RecoverCompletedIssue, "A completed claim with only historical linked pull requests must return executable recovery context.");
    var changeRequest = PullRequestForClaim(claim, "codex/issue-4-current", "open", "codex:cr");
    Assert((await WorkflowService.EvaluateClaimedWorkAsync(configuration, claim, completed, _ => Task.FromResult(changeRequest))).Tasks.Single().Type == WorkflowItemType.ChangeRequest, "A completed PR-less claim with a linked active pull request must evaluate that pull request.");
    Assert((await WorkflowService.EvaluateClaimedWorkAsync(configuration, claim, completed, _ => Task.FromResult(currentUnlabeledPullRequest))).Tasks.Single().Type == WorkflowItemType.RecoverCurrentPullRequest, "A completed claim with a current unlabeled PR must return runnable lifecycle recovery.");
    var mergedPullRequest = PullRequestForClaim(claim, "codex/issue-4-current", "merged");
    Assert((await WorkflowService.EvaluateClaimedWorkAsync(configuration, claim, completed, _ => Task.FromResult(mergedPullRequest))).Tasks.Single().Type == WorkflowItemType.CloseIssue, "A completed PR-less claim with a merged linked pull request must become terminal cleanup work.");
    var closedPullRequest = PullRequestForClaim(claim, "codex/issue-4-current", "closed");
    Assert((await WorkflowService.EvaluateClaimedWorkAsync(configuration, claim, completed, _ => Task.FromResult(closedPullRequest))).Tasks.Single().Type == WorkflowItemType.ClosedWithoutMerge, "A completed PR-less claim with a closed-unmerged linked pull request must become terminal cleanup work.");
    var prClaim = new WorkClaim { ClaimId = Guid.NewGuid(), Version = 1, OwnerSessionId = "session-a", IssueNumber = 4, PullRequestNumber = 21, WorkType = WorkClaimType.ChangeRequest, ClaimedAt = claimedAt, LastUpdatedAt = claimedAt };
    var deferred = PullRequestForClaim(prClaim, "codex/issue-4-current", "open", "codex:deferred");
    Assert((await WorkflowService.EvaluateClaimedWorkAsync(configuration, prClaim, completed, _ => Task.FromResult(deferred))).Tasks.Single().Type == WorkflowItemType.Deferred, "A deferred claimed pull request must be represented as passive work.");
    Assert(!GitHubCliService.IsConfirmedNotFound("HTTP 404: not found") && GitHubCliService.IsConfirmedNotFound("Could not resolve to an issue") && GitHubCliService.IsConfirmedNotFound("Could not resolve to a PullRequest") && !GitHubCliService.IsConfirmedNotFound("Could not resolve host: api.github.com") && !GitHubCliService.IsConfirmedNotFound("authentication required") && !GitHubCliService.IsConfirmedNotFound("rate limit exceeded"), "Only command-specific confirmed missing GitHub items may trigger claim release.");

    var directory = Path.Combine(Path.GetTempPath(), $"cgr-work-version-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    var first = (await WorkClaimStore.TryAcquireAsync(directory, claim)).Claim!;
    var continuation = (await WorkClaimStore.TryAcquireAsync(directory, claim)).Claim!;
    Assert(continuation.Version > first.Version && !await WorkClaimStore.ReleaseIfMatchesAsync(directory, first) && (await WorkClaimStore.ReadAsync(directory))?.Version == continuation.Version, "A stale same-claim revision must not delete a newer owner continuation.");
    Directory.Delete(directory, true);
}

static async Task AssertActiveClaimRouteOrchestrationAsync()
{
    var claimedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
    var claim = new WorkClaim { ClaimId = Guid.NewGuid(), Version = 1, OwnerSessionId = "session-a", IssueNumber = 4, WorkType = WorkClaimType.Implementation, ClaimedAt = claimedAt, LastUpdatedAt = claimedAt };
    var enrichedClaim = new WorkClaim { ClaimId = claim.ClaimId, Version = 2, OwnerSessionId = claim.OwnerSessionId, IssueNumber = claim.IssueNumber, PullRequestNumber = 21, WorkType = claim.WorkType, ClaimedAt = claim.ClaimedAt, LastUpdatedAt = DateTimeOffset.UtcNow };

    var passiveChecks = 0;
    var passiveRelease = await new ActiveClaimRouteService(
        current => Task.FromResult(passiveChecks++ == 0 ? ClaimedResponse(WorkflowItemType.AwaitingReview, 21) : ClaimedResponse(WorkflowItemType.AwaitingReview, 21)),
        () => Task.FromResult<WorkClaim?>(enrichedClaim),
        _ => Task.FromResult(new WorkClaimAcquisitionResult { Acquired = true, Claim = enrichedClaim }),
        () => Task.FromResult(true))
        .RouteAsync(claim, "session-a");
    Assert(passiveRelease is null, "A proven current passive PR must release the claim through the orchestration service.");
    var nextReady = HookTaskRouter.Route(new[] { ClaimedTask(WorkflowItemType.AwaitingReview, 21), new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 5 } });
    Assert(nextReady.SelectedTask?.IssueNumber == 5, "After claim release, the same invocation must allow the next ready issue to route.");

    var historicalChecks = 0;
    var historicalDecision = await new ActiveClaimRouteService(
        current => Task.FromResult(historicalChecks++ == 0 ? ClaimedResponse(WorkflowItemType.ResumeInProgressIssue) : ClaimedResponse(WorkflowItemType.ResumeInProgressIssue)),
        () => Task.FromResult<WorkClaim?>(claim),
        _ => Task.FromResult(new WorkClaimAcquisitionResult { Acquired = true, Claim = claim }),
        () => Task.FromResult(false))
        .RouteAsync(claim, "session-a");
    Assert(historicalDecision?.SelectedTask?.Type == WorkflowItemType.ResumeInProgressIssue, "A historical PR must leave the new claim active and continue implementation recovery.");

    var workingCurrentDecision = await new ActiveClaimRouteService(
        current => Task.FromResult(ClaimedResponse(WorkflowItemType.ChangeRequest, 21)),
        () => Task.FromResult<WorkClaim?>(enrichedClaim),
        _ => Task.FromResult(new WorkClaimAcquisitionResult { Acquired = true, Claim = enrichedClaim }),
        () => Task.FromResult(false))
        .RouteAsync(claim, "session-a");
    Assert(workingCurrentDecision?.SelectedTask?.Type == WorkflowItemType.ChangeRequest && workingCurrentDecision.SelectedTask.PullRequestNumber == 21, "A proven current PR linked while working must route only its claimed change request.");

    var closedDecision = await new ActiveClaimRouteService(
        current => Task.FromResult(ClaimedResponse(WorkflowItemType.ClosedWithoutMerge, 21)),
        () => Task.FromResult<WorkClaim?>(enrichedClaim),
        _ => throw new InvalidOperationException("A claimed PR should not be enriched twice."),
        () => Task.FromResult(true))
        .RouteAsync(enrichedClaim, "session-a");
    Assert(closedDecision is null, "A proven current closed-unmerged PR must release the coding claim.");
    var closedBlocker = HookTaskRouter.Route(new[] { ClaimedTask(WorkflowItemType.ClosedWithoutMerge, 21, 4, "Closed without merge."), new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 5 } });
    Assert(closedBlocker.BlockReason?.Contains("Closed without merge", StringComparison.Ordinal) == true, "After releasing a closed-unmerged claim, the normal blocker must remain visible.");

    var stateRaceDecision = await new ActiveClaimRouteService(
        current => Task.FromResult(current.PullRequestNumber is null ? ClaimedResponse(WorkflowItemType.AwaitingReview, 21) : ClaimedResponse(WorkflowItemType.ChangeRequest, 21)),
        () => Task.FromResult<WorkClaim?>(enrichedClaim),
        _ => Task.FromResult(new WorkClaimAcquisitionResult { Acquired = true, Claim = enrichedClaim }),
        () => Task.FromResult(false))
        .RouteAsync(claim, "session-a");
    Assert(stateRaceDecision?.SelectedTask?.Type == WorkflowItemType.ChangeRequest, "A passive-to-changes-requested race must refresh and route the current claimed CR.");

    var reconcileCalled = false;
    var genericFailure = false;
    try
    {
        await new ActiveClaimRouteService(
            _ => throw new InvalidOperationException("GitHub CLI command failed with exit code 1: HTTP 404: not found"),
            () => Task.FromResult<WorkClaim?>(claim),
            _ => throw new InvalidOperationException("Claim must not be changed after a GitHub failure."),
            () => { reconcileCalled = true; return Task.FromResult(true); })
            .RouteAsync(claim, "session-a");
    }
    catch (InvalidOperationException exception)
    {
        genericFailure = exception.Message.Contains("HTTP 404", StringComparison.Ordinal);
    }

    Assert(genericFailure && !reconcileCalled && !GitHubCliService.IsConfirmedNotFound("HTTP 404: not found"), "A generic HTTP 404 must preserve claim ownership and must not trigger reconciliation release.");
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

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

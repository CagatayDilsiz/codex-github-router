using System.Text.Json;
using CodexGithubRouter.GitHub;
using CodexGithubRouter.Work;
using CodexGithubRouter.Workflow;
using Xunit;

namespace CodexGithubRouter.Tests;

[Trait("Category", "Unit")]
public sealed class GitHubSignalEvaluationTests
{
    // --------------------------------------------------------------------------------------------
    // Native signal evaluation
    // --------------------------------------------------------------------------------------------

    [Fact]
    public void Evaluate_absent_signal_data_is_unknown_and_never_advances()
    {
        var pullRequest = Pull();
        var signals = GitHubSignalEvaluationService.Evaluate(pullRequest);

        Assert.Equal(NativeReviewState.None, signals.ReviewState);
        Assert.Equal(NativeCheckState.Unknown, signals.CheckState);
        Assert.Equal(NativeMergeState.Unknown, signals.MergeState);
        Assert.False(signals.HasSignalData);
        Assert.Null(NativeClassify(pullRequest));
    }

    [Fact]
    public void Evaluate_changes_requested_is_change_request()
    {
        var pullRequest = Pull(reviewDecision: "CHANGES_REQUESTED");
        var signals = GitHubSignalEvaluationService.Evaluate(pullRequest);

        Assert.Equal(NativeReviewState.ChangesRequested, signals.ReviewState);
        Assert.True(signals.HasSignalData);
        Assert.Equal(WorkflowItemType.ChangeRequest, NativeClassify(pullRequest));
    }

    [Fact]
    public void Evaluate_approved_that_is_mergeable_with_passing_checks_is_awaiting_merge()
    {
        var pullRequest = Pull(
            reviewDecision: "APPROVED",
            mergeable: "MERGEABLE",
            checks: Completed("build", "SUCCESS"));
        var signals = GitHubSignalEvaluationService.Evaluate(pullRequest);

        Assert.Equal(NativeReviewState.Approved, signals.ReviewState);
        Assert.Equal(NativeCheckState.Passed, signals.CheckState);
        Assert.Equal(NativeMergeState.Mergeable, signals.MergeState);
        Assert.Equal(WorkflowItemType.AwaitingMerge, NativeClassify(pullRequest));
    }

    [Theory]
    [InlineData("FAILURE")]
    [InlineData("TIMED_OUT")]
    [InlineData("ACTION_REQUIRED")]
    [InlineData("ERROR")]
    public void Evaluate_failed_conclusion_is_change_request(string conclusion)
    {
        var pullRequest = Pull(checks: Completed("build", conclusion));
        var signals = GitHubSignalEvaluationService.Evaluate(pullRequest);

        Assert.Equal(NativeCheckState.Failed, signals.CheckState);
        Assert.Equal(WorkflowItemType.ChangeRequest, NativeClassify(pullRequest));
    }

    [Theory]
    [InlineData("NEUTRAL")]
    [InlineData("SKIPPED")]
    public void Evaluate_informational_conclusions_count_as_passing(string conclusion)
    {
        var signals = GitHubSignalEvaluationService.Evaluate(Pull(checks: Completed("build", conclusion)));

        Assert.Equal(NativeCheckState.Passed, signals.CheckState);
    }

    [Theory]
    [InlineData("CANCELLED")]
    public void Evaluate_cancelled_conclusion_is_failing_like_gh(string conclusion)
    {
        var pullRequest = Pull(mergeable: "MERGEABLE", checks: Completed("build", conclusion));

        var signals = GitHubSignalEvaluationService.Evaluate(pullRequest);

        Assert.Equal(NativeCheckState.Failed, signals.CheckState);
        Assert.Equal(WorkflowItemType.ChangeRequest, NativeClassify(pullRequest));
    }

    [Theory]
    [InlineData("STALE")]
    [InlineData("STARTUP_FAILURE")]
    [InlineData("UNRECOGNIZED-CONCLUSION")]
    public void Evaluate_unreliable_or_unrecognized_conclusions_stay_pending(string conclusion)
    {
        var pullRequest = Pull(mergeable: "MERGEABLE", checks: Completed("build", conclusion));

        var signals = GitHubSignalEvaluationService.Evaluate(pullRequest);

        Assert.Equal(NativeCheckState.Pending, signals.CheckState);
        Assert.Equal(WorkflowItemType.AwaitingReview, NativeClassify(pullRequest));
    }

    [Theory]
    [InlineData("QUEUED", "")]
    [InlineData("IN_PROGRESS", "")]
    [InlineData("COMPLETED", "")]
    public void Evaluate_unfinished_run_is_pending_and_stays_passive(string status, string conclusion)
    {
        var pullRequest = Pull(checks: new CheckRun { Name = "build", Status = status, Conclusion = conclusion });
        var signals = GitHubSignalEvaluationService.Evaluate(pullRequest);

        Assert.Equal(NativeCheckState.Pending, signals.CheckState);
        Assert.Equal(WorkflowItemType.AwaitingReview, NativeClassify(pullRequest));
    }

    [Fact]
    public void Evaluate_pending_check_dominates_a_failed_check()
    {
        var pullRequest = Pull(rollup: new[]
        {
            Completed("lint", "FAILURE"),
            new CheckRun { Name = "deploy", Status = "IN_PROGRESS", Conclusion = "" }
        });
        var signals = GitHubSignalEvaluationService.Evaluate(pullRequest);

        Assert.Equal(NativeCheckState.Pending, signals.CheckState);
        Assert.Equal(WorkflowItemType.AwaitingReview, NativeClassify(pullRequest));
    }

    [Fact]
    public void Evaluate_empty_rollup_is_unknown_and_stays_passive()
    {
        var pullRequest = Pull(rollup: Array.Empty<CheckRun>());
        var signals = GitHubSignalEvaluationService.Evaluate(pullRequest);

        Assert.Equal(NativeCheckState.Unknown, signals.CheckState);
        Assert.Null(NativeClassify(pullRequest));
    }

    [Fact]
    public void Evaluate_review_required_is_passive()
    {
        var pullRequest = Pull(reviewDecision: "REVIEW_REQUIRED");
        var signals = GitHubSignalEvaluationService.Evaluate(pullRequest);

        Assert.Equal(NativeReviewState.ReviewRequired, signals.ReviewState);
        Assert.Equal(WorkflowItemType.AwaitingReview, NativeClassify(pullRequest));
    }

    [Fact]
    public void Evaluate_passed_checks_that_are_mergeable_is_awaiting_merge()
    {
        var pullRequest = Pull(mergeable: "MERGEABLE", checks: Completed("build", "SUCCESS"));

        var signals = GitHubSignalEvaluationService.Evaluate(pullRequest);
        Assert.Equal(NativeCheckState.Passed, signals.CheckState);
        Assert.Equal(NativeMergeState.Mergeable, signals.MergeState);
        Assert.Equal(WorkflowItemType.AwaitingMerge, NativeClassify(pullRequest));
    }

    [Fact]
    public void Evaluate_passed_checks_with_conflicts_is_change_request()
    {
        var pullRequest = Pull(mergeable: "UNMERGEABLE", checks: Completed("build", "SUCCESS"));
        var signals = GitHubSignalEvaluationService.Evaluate(pullRequest);

        Assert.Equal(NativeCheckState.Passed, signals.CheckState);
        Assert.Equal(NativeMergeState.NotMergeable, signals.MergeState);
        Assert.Equal(WorkflowItemType.ChangeRequest, NativeClassify(pullRequest));
    }

    [Fact]
    public void Evaluate_passed_checks_with_unknown_mergeability_stays_passive()
    {
        var pullRequest = Pull(mergeable: "", checks: Completed("build", "SUCCESS"));
        var signals = GitHubSignalEvaluationService.Evaluate(pullRequest);

        Assert.Equal(NativeCheckState.Passed, signals.CheckState);
        Assert.Equal(NativeMergeState.Unknown, signals.MergeState);
        Assert.Equal(WorkflowItemType.AwaitingReview, NativeClassify(pullRequest));
    }

    [Fact]
    public void Evaluate_unknown_merge_string_is_conservative()
    {
        var signals = GitHubSignalEvaluationService.Evaluate(Pull(mergeable: "UNKNOWN", checks: Completed("build", "SUCCESS")));

        Assert.Equal(NativeMergeState.Unknown, signals.MergeState);
    }

    [Fact]
    public void Evaluate_unrecognized_review_decision_is_no_signal()
    {
        var signals = GitHubSignalEvaluationService.Evaluate(Pull(reviewDecision: "anything-unrecognized"));

        Assert.Equal(NativeReviewState.None, signals.ReviewState);
    }

    // --------------------------------------------------------------------------------------------
    // Classification gating
    // --------------------------------------------------------------------------------------------

    [Fact]
    public void Classify_is_null_when_native_signals_are_disabled()
    {
        Assert.Null(GitHubSignalEvaluationService.ClassifyWorkflowType(new RouterConfiguration(), Pull(reviewDecision: "CHANGES_REQUESTED")));
    }

    [Fact]
    public void Classify_is_null_for_draft_pull_requests()
    {
        Assert.Null(GitHubSignalEvaluationService.ClassifyWorkflowType(NativeConfiguration(), Pull(reviewDecision: "CHANGES_REQUESTED", isDraft: true)));
    }

    [Fact]
    public void Classify_is_null_for_non_open_pull_requests()
    {
        Assert.Null(GitHubSignalEvaluationService.ClassifyWorkflowType(NativeConfiguration(), Pull(reviewDecision: "CHANGES_REQUESTED", state: "closed")));
    }

    [Fact]
    public void Classify_is_null_when_no_signal_data_exists()
    {
        Assert.Null(GitHubSignalEvaluationService.ClassifyWorkflowType(NativeConfiguration(), Pull()));
    }

    [Fact]
    public void CreateWorkflowItem_carries_the_issue_and_pull_request_identity()
    {
        var item = GitHubSignalEvaluationService.CreateWorkflowItem(
            NativeConfiguration(),
            Pull(reviewDecision: "CHANGES_REQUESTED", checks: Completed("build", "SUCCESS")),
            issueNumber: 4);

        Assert.NotNull(item);
        Assert.Equal(WorkflowItemType.ChangeRequest, item!.Type);
        Assert.Equal(4, item.IssueNumber);
        Assert.Equal(21, item.PullRequestNumber);
        Assert.False(string.IsNullOrWhiteSpace(item.Status.Message));
    }

    [Fact]
    public void CreateWorkflowItem_is_null_when_classification_is_null()
    {
        Assert.Null(GitHubSignalEvaluationService.CreateWorkflowItem(NativeConfiguration(), Pull()));
    }

    // --------------------------------------------------------------------------------------------
    // statusCheckRollup JSON converter
    // --------------------------------------------------------------------------------------------

    [Fact]
    public void StatusCheckRollup_converter_accepts_check_run_entries()
    {
        var pullRequest = JsonSerializer.Deserialize<PullRequest>("""
        {
          "number": 41,
          "statusCheckRollup": [
            { "__typename": "CheckRun", "name": "build", "status": "COMPLETED", "conclusion": "SUCCESS", "isRequired": true },
            { "__typename": "CheckRun", "name": "lint", "status": "IN_PROGRESS", "conclusion": null }
          ],
          "mergeable": "MERGEABLE",
          "reviewDecision": "APPROVED"
        }
        """);

        Assert.NotNull(pullRequest);
        Assert.Equal(2, pullRequest!.StatusCheckRollup.Count);
        Assert.Equal("build", pullRequest.StatusCheckRollup[0].Name);
        Assert.Equal("COMPLETED", pullRequest.StatusCheckRollup[0].Status);
        Assert.Equal("SUCCESS", pullRequest.StatusCheckRollup[0].Conclusion);
        Assert.True(pullRequest.StatusCheckRollup[0].IsRequired);
        Assert.Equal("IN_PROGRESS", pullRequest.StatusCheckRollup[1].Status);
        Assert.Equal("MERGEABLE", pullRequest.Mergeable);
        Assert.Equal("APPROVED", pullRequest.ReviewDecision);
    }

    [Fact]
    public void StatusCheckRollup_converter_accepts_status_context_entries()
    {
        var pullRequest = JsonSerializer.Deserialize<PullRequest>("""
        {
          "number": 41,
          "statusCheckRollup": [
            { "__typename": "StatusContext", "context": "continuous-integration", "state": "SUCCESS", "isRequired": true },
            { "__typename": "StatusContext", "context": "docs-check", "state": "PENDING", "isRequired": false },
            { "__typename": "StatusContext", "context": "axe-check", "state": "FAILURE", "isRequired": false }
          ]
        }
        """);

        Assert.NotNull(pullRequest);
        Assert.Equal(3, pullRequest!.StatusCheckRollup.Count);
        Assert.Equal("SUCCESS", pullRequest.StatusCheckRollup[0].Conclusion);
        Assert.Equal("COMPLETED", pullRequest.StatusCheckRollup[0].Status);
        Assert.Equal("QUEUED", pullRequest.StatusCheckRollup[1].Status);
        Assert.Equal("FAILURE", pullRequest.StatusCheckRollup[2].Conclusion);
    }

    [Fact]
    public void StatusCheckRollup_converter_accepts_the_graphql_nodes_shape()
    {
        var pullRequest = JsonSerializer.Deserialize<PullRequest>("""
        {
          "number": 41,
          "statusCheckRollup": {
            "nodes": [
              { "__typename": "CheckRun", "name": "build", "status": "COMPLETED", "conclusion": "SUCCESS", "isRequired": true }
            ]
          }
        }
        """);

        Assert.NotNull(pullRequest);
        var run = Assert.Single(pullRequest!.StatusCheckRollup);
        Assert.Equal("build", run.Name);
        Assert.Equal("SUCCESS", run.Conclusion);
    }

    [Fact]
    public void StatusContext_unknown_state_is_queue_pending_in_the_one_evaluator_shape()
    {
        var pullRequest = JsonSerializer.Deserialize<PullRequest>("""
        {
          "number": 41,
          "statusCheckRollup": [
            { "__typename": "StatusContext", "context": "check-that-does-not-pass", "state": "EXPECTED", "isRequired": false }
          ]
        }
        """);

        var signals = GitHubSignalEvaluationService.Evaluate(pullRequest!);
        Assert.Equal(NativeCheckState.Pending, signals.CheckState);
    }

    // --------------------------------------------------------------------------------------------
    // WorkflowService integration: claimed work
    // --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Claimed_label_less_pull_request_with_changes_requested_is_change_request_when_native_enabled()
    {
        var claim = Claim();
        var issue = WorkingIssueWithPullRequestReference();
        var pullRequest = Pull(
            branch: "codex/issue-4-current",
            claim: claim,
            reviewDecision: "CHANGES_REQUESTED",
            checks: Completed("build", "SUCCESS"));

        var result = await WorkflowService.EvaluateClaimedWorkAsync(
            NativeConfiguration(),
            claim,
            issue,
            _ => Task.FromResult(pullRequest));

        var task = Assert.Single(result.Tasks);
        Assert.Equal(WorkflowItemType.ChangeRequest, task.Type);
        Assert.Equal(WorkflowItemSource.NativeSignals, task.Source);
        Assert.Contains(GitHubSignalEvaluationService.NoLabelNativeClassificationMarker, task.Status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Claimed_label_less_pull_request_with_pending_checks_stays_passive_when_native_enabled()
    {
        var claim = Claim();
        var issue = WorkingIssueWithPullRequestReference();

        var result = await WorkflowService.EvaluateClaimedWorkAsync(
            NativeConfiguration(),
            claim,
            issue,
            _ => Task.FromResult(Pull(
                branch: "codex/issue-4-current",
                claim: claim,
                checks: new CheckRun { Name = "build", Status = "IN_PROGRESS", Conclusion = "" })));

        var task = Assert.Single(result.Tasks);
        Assert.Equal(WorkflowItemType.AwaitingReview, task.Type);
        Assert.Equal(WorkflowItemSource.NativeSignals, task.Source);
    }

    [Fact]
    public async Task Claimed_label_less_pull_request_still_recovers_when_native_signals_are_disabled()
    {
        var claim = Claim();
        var issue = WorkingIssueWithPullRequestReference();

        var result = await WorkflowService.EvaluateClaimedWorkAsync(
            new RouterConfiguration(),
            claim,
            issue,
            _ => Task.FromResult(Pull(branch: "codex/issue-4-current", claim: claim, reviewDecision: "CHANGES_REQUESTED", checks: Completed("build", "SUCCESS"))));

        var task = Assert.Single(result.Tasks);
        Assert.Equal(WorkflowItemType.RecoverCurrentPullRequest, task.Type);
    }

    [Fact]
    public async Task Claimed_label_less_pull_request_without_signal_data_recovers_lifecycle()
    {
        var claim = Claim();
        var issue = WorkingIssueWithPullRequestReference();

        var result = await WorkflowService.EvaluateClaimedWorkAsync(
            NativeConfiguration(),
            claim,
            issue,
            _ => Task.FromResult(Pull(branch: "codex/issue-4-current", claim: claim)));

        var task = Assert.Single(result.Tasks);
        Assert.Equal(WorkflowItemType.RecoverCurrentPullRequest, task.Type);
        Assert.Equal(WorkflowItemSource.Recovery, task.Source);
    }

    [Fact]
    public async Task Claimed_draft_pull_request_is_never_native_classified()
    {
        var claim = Claim();
        var issue = WorkingIssueWithPullRequestReference();

        var result = await WorkflowService.EvaluateClaimedWorkAsync(
            NativeConfiguration(),
            claim,
            issue,
            _ => Task.FromResult(Pull(
                branch: "codex/issue-4-current",
                claim: claim,
                isDraft: true,
                reviewDecision: "CHANGES_REQUESTED",
                mergeable: "MERGEABLE",
                checks: Completed("build", "SUCCESS"))));

        var task = Assert.Single(result.Tasks);
        Assert.Equal(WorkflowItemType.RecoverCurrentPullRequest, task.Type);
    }

    [Fact]
    public async Task Linked_draft_pull_request_is_never_native_classified()
    {
        var issue = CompletedIssue();

        var result = await WorkflowService.CheckIssueLinkedPullRequestsAsync(
            NativeConfiguration(),
            new[] { issue },
            _ => Task.FromResult(Pull(claim: Claim(), isDraft: true, reviewDecision: "APPROVED", mergeable: "MERGEABLE", checks: Completed("build", "SUCCESS"))));

        var task = Assert.Single(result.Tasks);
        Assert.Equal(WorkflowItemType.UnknownPullRequestState, task.Type);
    }

    // --------------------------------------------------------------------------------------------
    // WorkflowService integration: linked pull requests
    // --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Linked_label_less_pull_request_with_failed_checks_is_change_request_when_native_enabled()
    {
        var issue = CompletedIssue();
        var pullRequest = Pull(claim: Claim(), reviewDecision: "APPROVED", mergeable: "MERGEABLE", checks: Completed("build", "FAILURE"));

        var result = await WorkflowService.CheckIssueLinkedPullRequestsAsync(
            NativeConfiguration(),
            new[] { issue },
            _ => Task.FromResult(pullRequest));

        var task = Assert.Single(result.Tasks);
        Assert.Equal(WorkflowItemType.ChangeRequest, task.Type);
        Assert.Contains(GitHubSignalEvaluationService.NoLabelNativeClassificationMarker, task.Status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Linked_label_less_pull_request_that_passed_and_is_mergeable_is_awaiting_merge_when_native_enabled()
    {
        var issue = CompletedIssue();
        var pullRequest = Pull(claim: Claim(), reviewDecision: "APPROVED", mergeable: "MERGEABLE", checks: Completed("build", "SUCCESS"));

        var result = await WorkflowService.CheckIssueLinkedPullRequestsAsync(
            NativeConfiguration(),
            new[] { issue },
            _ => Task.FromResult(pullRequest));

        var task = Assert.Single(result.Tasks);
        Assert.Equal(WorkflowItemType.AwaitingMerge, task.Type);
    }

    [Fact]
    public async Task Linked_label_less_pull_request_without_signal_data_is_unknown_state()
    {
        var issue = CompletedIssue();

        var result = await WorkflowService.CheckIssueLinkedPullRequestsAsync(
            NativeConfiguration(),
            new[] { issue },
            _ => Task.FromResult(Pull(claim: Claim())));

        var task = Assert.Single(result.Tasks);
        Assert.Equal(WorkflowItemType.UnknownPullRequestState, task.Type);
        Assert.Equal(WorkflowItemSource.Recovery, task.Source);
    }

    [Fact]
    public async Task Cgr_label_wins_over_native_signals()
    {
        var issue = CompletedIssue();
        var pullRequest = Pull(
            claim: Claim(),
            label: "codex:deferred",
            reviewDecision: "CHANGES_REQUESTED",
            checks: Completed("build", "FAILURE"));

        var result = await WorkflowService.CheckIssueLinkedPullRequestsAsync(
            NativeConfiguration(),
            new[] { issue },
            _ => Task.FromResult(pullRequest));

        var task = Assert.Single(result.Tasks);
        Assert.Equal(WorkflowItemType.Deferred, task.Type);
        Assert.Equal(WorkflowItemSource.Labels, task.Source);
    }

    [Fact]
    public async Task Linked_labeled_pull_request_is_excluded_from_native_classification()
    {
        var issue = MultiPullRequestIssue(new[] { 21, 22 });
        var deferred = Pull(number: 21, claim: Claim(), label: "codex:deferred", reviewDecision: "CHANGES_REQUESTED", checks: Completed("build", "FAILURE"));
        var labelLess = Pull(number: 22, claim: Claim(), reviewDecision: "CHANGES_REQUESTED", checks: Completed("build", "SUCCESS"));
        var byNumber = new Dictionary<int, PullRequest> { [21] = deferred, [22] = labelLess };

        var result = await WorkflowService.CheckIssueLinkedPullRequestsAsync(
            NativeConfiguration(),
            new[] { issue },
            number => Task.FromResult(byNumber[number]));

        var task = Assert.Single(result.Tasks);
        Assert.Equal(WorkflowItemType.ChangeRequest, task.Type);
        Assert.Equal(22, task.PullRequestNumber);
    }

    [Theory]
    [InlineData(new[] { 21, 22 })]
    [InlineData(new[] { 22, 21 })]
    public async Task Linked_native_preference_is_order_independent_and_prioritizes_change_request(int[] references)
    {
        var issue = MultiPullRequestIssue(references);
        var passive = Pull(number: 21, claim: Claim(), reviewDecision: "APPROVED", mergeable: "MERGEABLE", checks: Completed("build", "SUCCESS"));
        var actionable = Pull(number: 22, claim: Claim(), reviewDecision: "CHANGES_REQUESTED", checks: Completed("build", "FAILURE"));
        var byNumber = new Dictionary<int, PullRequest> { [21] = passive, [22] = actionable };

        var result = await WorkflowService.CheckIssueLinkedPullRequestsAsync(
            NativeConfiguration(),
            new[] { issue },
            number => Task.FromResult(byNumber[number]));

        var task = Assert.Single(result.Tasks);
        Assert.Equal(WorkflowItemType.ChangeRequest, task.Type);
        Assert.Equal(22, task.PullRequestNumber);
    }

    [Fact]
    public async Task Gated_label_less_pull_request_with_failed_checks_is_actionable_when_native_enabled()
    {
        var issue = GatedIssue(11, WorkflowState.Completed, 21);

        var result = await WorkflowService.EvaluateRepositoryGateAsync(
            NativeConfiguration(),
            new[] { issue },
            _ => Task.FromResult(Pull(claim: Claim(), reviewDecision: "CHANGES_REQUESTED", checks: Completed("build", "FAILURE"))));

        var task = Assert.Single(result.Tasks);
        Assert.Equal(WorkflowItemType.ChangeRequest, task.Type);
        Assert.Equal(WorkflowItemSource.NativeSignals, task.Source);
        Assert.Contains(GitHubSignalEvaluationService.NoLabelNativeClassificationMarker, task.Status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gated_label_less_pull_request_with_pending_checks_stays_a_gate_block_when_native_enabled()
    {
        var issue = GatedIssue(12, WorkflowState.Completed, 21);

        var result = await WorkflowService.EvaluateRepositoryGateAsync(
            NativeConfiguration(),
            new[] { issue },
            _ => Task.FromResult(Pull(claim: Claim(), reviewDecision: "REVIEW_REQUIRED", checks: new CheckRun { Name = "build", Status = "IN_PROGRESS", Conclusion = "" })));

        var task = Assert.Single(result.Tasks);
        Assert.Equal(WorkflowItemType.RepositoryGateBlock, task.Type);
    }

    // --------------------------------------------------------------------------------------------
    // Explanation stage
    // --------------------------------------------------------------------------------------------

    [Fact]
    public void Explain_reports_native_signals_disabled()
    {
        var issue = ReadyIssue(4);
        var explanation = RoutingExplanationService.Explain(Plan(issue), issue);

        var stage = Assert.Single(explanation.Stages, s => s.Name == "Native GitHub Signals");
        Assert.Equal(RoutingVerdict.Disabled, stage.Verdict);
    }

    [Fact]
    public void Explain_attributes_a_task_to_native_signal_classification()
    {
        var issue = ReadyIssue(4);
        var task = new WorkflowItem
        {
            Type = WorkflowItemType.ChangeRequest,
            IssueNumber = 4,
            PullRequestNumber = 21,
            Source = WorkflowItemSource.NativeSignals,
            Status = new WorkflowTaskStatus { Message = "GitHub checks on pull request #21 have failed." }
        };
        var explanation = RoutingExplanationService.Explain(Plan(issue, configuration: NativeConfiguration(), workItems: new[] { task }), issue);

        var stage = Assert.Single(explanation.Stages, s => s.Name == "Native GitHub Signals");
        Assert.Equal(RoutingVerdict.Pass, stage.Verdict);
        Assert.Contains("native GitHub signals classified", stage.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("failed", stage.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Explain_reports_label_driven_routing_not_overridden()
    {
        var issue = ReadyIssue(4);
        var task = new WorkflowItem { Type = WorkflowItemType.AwaitingMerge, IssueNumber = 4, PullRequestNumber = 21, Source = WorkflowItemSource.Labels };
        var explanation = RoutingExplanationService.Explain(Plan(issue, configuration: NativeConfiguration(), workItems: new[] { task }), issue);

        var stage = Assert.Single(explanation.Stages, s => s.Name == "Native GitHub Signals");
        Assert.Equal(RoutingVerdict.Pass, stage.Verdict);
        Assert.Contains("CGR workflow labels", stage.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explain_reports_no_usable_native_signal_data_from_production_linked_evaluation()
    {
        var issue = CompletedIssue();
        var result = await WorkflowService.CheckIssueLinkedPullRequestsAsync(
            NativeConfiguration(),
            new[] { issue },
            _ => Task.FromResult(Pull(claim: Claim())));

        var recoveryTask = Assert.Single(result.Tasks);
        Assert.Equal(WorkflowItemSource.Recovery, recoveryTask.Source);
        Assert.Equal(WorkflowItemType.UnknownPullRequestState, recoveryTask.Type);

        var explanation = RoutingExplanationService.Explain(Plan(issue, configuration: NativeConfiguration(), workItems: result.Tasks), issue);

        var stage = Assert.Single(explanation.Stages, s => s.Name == "Native GitHub Signals");
        Assert.Equal(RoutingVerdict.Pass, stage.Verdict);
        Assert.Contains("no usable native signal data", stage.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Explain_reports_native_signals_do_not_participate_without_linked_pull_requests()
    {
        var issue = ReadyIssue(4);
        var explanation = RoutingExplanationService.Explain(Plan(issue, configuration: NativeConfiguration()), issue);

        var stage = Assert.Single(explanation.Stages, s => s.Name == "Native GitHub Signals");
        Assert.Equal(RoutingVerdict.Pass, stage.Verdict);
        Assert.Contains("no linked pull request", stage.Message, StringComparison.Ordinal);
    }

    // --------------------------------------------------------------------------------------------
    // Helpers
    // --------------------------------------------------------------------------------------------

    private static WorkflowItemType? NativeClassify(PullRequest pullRequest) =>
        GitHubSignalEvaluationService.ClassifyWorkflowType(NativeConfiguration(), pullRequest);

    private static CheckRun Completed(string name, string conclusion) =>
        new() { Name = name, Status = "COMPLETED", Conclusion = conclusion };

    private static PullRequest Pull(
        WorkClaim? claim = null,
        string branch = "codex/issue-4-current",
        string? label = null,
        string state = "open",
        bool isDraft = false,
        int? number = null,
        string? reviewDecision = null,
        string? mergeable = null,
        CheckRun? checks = null,
        IReadOnlyList<CheckRun>? rollup = null)
    {
        claim ??= Claim();
        var statusCheckRollup = new List<CheckRun>();
        if (checks is not null)
        {
            statusCheckRollup.Add(checks);
        }

        if (rollup is not null)
        {
            statusCheckRollup.AddRange(rollup);
        }

        return new PullRequest
        {
            Number = number ?? 21,
            State = state,
            IsDraft = isDraft,
            CreatedAt = claim.ClaimedIssueUpdatedAt,
            HeadRefName = branch,
            Labels = label is null ? new List<GithubLabel>() : new List<GithubLabel> { new() { Name = label } },
            ClosingIssuesReferences = new List<ClosingIssueReference> { new() { Number = claim.IssueNumber!.Value } },
            ReviewDecision = reviewDecision ?? string.Empty,
            Mergeable = mergeable ?? string.Empty,
            StatusCheckRollup = statusCheckRollup
        };
    }

    private static WorkClaim Claim(DateTimeOffset? baseline = null)
    {
        var now = baseline ?? DateTimeOffset.UtcNow.AddMinutes(-5);
        return new WorkClaim
        {
            ClaimId = Guid.NewGuid(),
            Version = 1,
            OwnerSessionId = "owner",
            IssueNumber = 4,
            WorkType = WorkClaimType.Implementation,
            ClaimedIssueUpdatedAt = now,
            ClaimedAt = now,
            LastUpdatedAt = now
        };
    }

    private static Issue WorkingIssueWithPullRequestReference() => new()
    {
        Number = 4,
        Labels = new List<GithubLabel> { new() { Name = "codex:working" } },
        ClosingPullRequestsReferences = new List<ClosingIssueReference> { new() { Number = 21 } }
    };

    private static Issue CompletedIssue() => new()
    {
        Number = 4,
        Labels = new List<GithubLabel> { new() { Name = "codex:done" } },
        ClosingPullRequestsReferences = new List<ClosingIssueReference> { new() { Number = 21 } }
    };

    private static Issue ReadyIssue(int number) => new()
    {
        Number = number,
        Labels = new List<GithubLabel> { new() { Name = "codex:ready" } }
    };

    private static Issue MultiPullRequestIssue(IReadOnlyList<int> referenceNumbers) => new()
    {
        Number = 4,
        Labels = new List<GithubLabel> { new() { Name = "codex:done" } },
        ClosingPullRequestsReferences = referenceNumbers.Select(number => new ClosingIssueReference { Number = number }).ToList()
    };

    private static Issue GatedIssue(int number, WorkflowState state, int pullRequestNumber) => new()
    {
        Number = number,
        State = "open",
        Labels = new List<GithubLabel>
        {
            new() { Name = new RouterConfiguration().States[state].Single().Values.Single() },
            new() { Name = "codex:gate" }
        },
        ClosingPullRequestsReferences = new List<ClosingIssueReference> { new() { Number = pullRequestNumber } }
    };

    private static RouterConfiguration NativeConfiguration() => new()
    {
        Policies = new RouterPolicies
        {
            NativeSignals = new NativeSignalsPolicy { Enabled = true }
        }
    };

    private static RoutingEvaluationResult Plan(
        Issue issue,
        RouterConfiguration? configuration = null,
        IReadOnlyList<WorkflowItem>? workItems = null) => new()
        {
            Configuration = configuration ?? new RouterConfiguration(),
            ConsideredIssues = new[] { issue },
            WorkflowTasks = workItems ?? Array.Empty<WorkflowItem>(),
            ActionableTasks = workItems ?? Array.Empty<WorkflowItem>()
        };
}
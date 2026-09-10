using System.Text.Json;
using CodexGithubRouter.GitHub;
using CodexGithubRouter.Hooks;
using CodexGithubRouter.Work;
using CodexGithubRouter.Workflow;
using Xunit;

namespace CodexGithubRouter.Tests;

[Trait("Category", "Unit")]
public sealed class ReviewRoutingTests
{
    private static RouterConfiguration EnabledConfiguration => new()
    {
        Policies = new RouterPolicies
        {
            ReviewRouting = new ReviewRoutingPolicy { Enabled = true }
        }
    };

    // --------------------------------------------------------------------------------------------
    // ReviewRoutingService evaluation stages
    // --------------------------------------------------------------------------------------------

    [Fact]
    public void Direct_requested_reviewer_is_eligible()
    {
        var pullRequest = Pr(41, "alice", ("n1", "bob", false, string.Empty));

        var stages = ReviewRoutingService.EvaluateStages(EnabledConfiguration, pullRequest, "bob", null, null);
        var eligible = ReviewRoutingService.IsEligible(stages);

        Assert.True(eligible);
        Assert.All(stages, stage => Assert.NotEqual(RoutingVerdict.HardIneligible, stage.Verdict));
        Assert.Contains(stages, stage => stage.Name == "Review Routing / Requested Reviewer" && stage.Verdict == RoutingVerdict.Pass);
    }

    [Fact]
    public void Not_directly_requested_reviewer_is_ineligible_and_exposes_team_request()
    {
        var pullRequest = Pr(41, "alice", ("n1", "bob", false, string.Empty));

        var stages = ReviewRoutingService.EvaluateStages(EnabledConfiguration, pullRequest, "carol", null, null);

        Assert.False(ReviewRoutingService.IsEligible(stages));
        var requestedStage = Assert.Single(stages, stage => stage.Name == "Review Routing / Requested Reviewer");
        Assert.Equal(RoutingVerdict.HardIneligible, requestedStage.Verdict);
        Assert.DoesNotContain("team review", requestedStage.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Team_review_request_is_not_claimable_directly()
    {
        var pullRequest = Pr(41, "alice", ("t1", string.Empty, true, "codex-reviewers"));

        var stages = ReviewRoutingService.EvaluateStages(EnabledConfiguration, pullRequest, "carol", null, null);

        Assert.False(ReviewRoutingService.IsEligible(stages));
        var requestedStage = Assert.Single(stages, stage => stage.Name == "Review Routing / Requested Reviewer");
        Assert.Equal(RoutingVerdict.HardIneligible, requestedStage.Verdict);
        Assert.Contains("@codex-reviewers", requestedStage.Message);
    }

    [Fact]
    public void Draft_pull_request_is_not_claimable()
    {
        var pullRequest = Pr(41, "alice", isDraft: true, requests: ("n1", "bob", false, string.Empty));

        var stages = ReviewRoutingService.EvaluateStages(EnabledConfiguration, pullRequest, "bob", null, null);

        Assert.False(ReviewRoutingService.IsEligible(stages));
        Assert.Contains(stages, stage => stage.Name == "Draft Check" && stage.Verdict == RoutingVerdict.HardIneligible);
    }

    [Theory]
    [InlineData("merged")]
    [InlineData("closed")]
    [InlineData("superseded")]
    public void Terminal_pull_request_states_are_not_claimable(string state)
    {
        var pullRequest = Pr(41, "alice", state: state, requests: ("n1", "bob", false, string.Empty));

        var stages = ReviewRoutingService.EvaluateStages(EnabledConfiguration, pullRequest, "bob", null, null);

        Assert.False(ReviewRoutingService.IsEligible(stages));
        var stateStage = Assert.Single(stages, stage => stage.Name == "Pull Request State");
        Assert.Equal(RoutingVerdict.HardIneligible, stateStage.Verdict);
    }

    [Fact]
    public void Author_cannot_review_their_own_pull_request()
    {
        var pullRequest = Pr(41, "bob", ("n1", "bob", false, string.Empty));

        var stages = ReviewRoutingService.EvaluateStages(EnabledConfiguration, pullRequest, "bob", null, null);

        Assert.False(ReviewRoutingService.IsEligible(stages));
        Assert.Contains(stages, stage => stage.Name == "Reviewer / Author" && stage.Verdict == RoutingVerdict.HardIneligible);
    }

    [Theory]
    [InlineData("codex:cr", PullRequestState.ChangesRequested)]
    [InlineData("codex:merge-ready", PullRequestState.AwaitingMerge)]
    [InlineData("codex:deferred", PullRequestState.Deferred)]
    public void Cgr_state_contradicting_review_work_is_not_claimable(string label, PullRequestState expectedState)
    {
        var pullRequest = Pr(41, "alice", label: label, requests: ("n1", "bob", false, string.Empty));

        var stages = ReviewRoutingService.EvaluateStages(EnabledConfiguration, pullRequest, "bob", null, null);

        Assert.False(ReviewRoutingService.IsEligible(stages));
        var stateStage = Assert.Single(stages, stage => stage.Name == "CGR PR State Compatibility");
        Assert.Equal(RoutingVerdict.HardIneligible, stateStage.Verdict);
        Assert.Contains(expectedState.ToString(), stateStage.Message);
    }

    [Fact]
    public void Conflicting_cgr_states_fail_closed()
    {
        var pullRequest = Pr(41, "alice", labels: new[] { "codex:cr", "codex:merge-ready" }, requests: ("n1", "bob", false, string.Empty));

        var stages = ReviewRoutingService.EvaluateStages(EnabledConfiguration, pullRequest, "bob", null, null);

        Assert.False(ReviewRoutingService.IsEligible(stages));
        var stateStage = Assert.Single(stages, stage => stage.Name == "CGR PR State Compatibility");
        Assert.Equal(RoutingVerdict.HardIneligible, stateStage.Verdict);
    }

    [Fact]
    public void Disabled_review_routing_is_not_claimable()
    {
        var pullRequest = Pr(41, "alice", ("n1", "bob", false, string.Empty));

        var stages = ReviewRoutingService.EvaluateStages(new RouterConfiguration(), pullRequest, "bob", null, null);

        Assert.False(ReviewRoutingService.IsEligible(stages));
        var requestedStage = Assert.Single(stages, stage => stage.Name == "Review Routing / Requested Reviewer");
        Assert.Equal(RoutingVerdict.HardIneligible, requestedStage.Verdict);
    }

    // --------------------------------------------------------------------------------------------
    // Review cycles and claim release decisions
    // --------------------------------------------------------------------------------------------

    [Fact]
    public void Matching_review_cycle_is_current()
    {
        var pullRequest = Pr(41, "alice", ("n1", "bob", false, string.Empty));
        var claim = ReviewClaim(41, "bob", "n1");

        Assert.True(ReviewRoutingService.IsReviewCycleCurrent(pullRequest, "bob", claim));
    }

    [Fact]
    public void New_review_cycle_is_not_current_for_an_older_claim()
    {
        var pullRequest = Pr(41, "alice", ("n2", "bob", false, string.Empty));
        var claim = ReviewClaim(41, "bob", "n1");

        Assert.False(ReviewRoutingService.IsReviewCycleCurrent(pullRequest, "bob", claim));
    }

    [Fact]
    public void Legacy_claim_without_a_cycle_marker_stays_current_while_requested()
    {
        var pullRequest = Pr(41, "alice", ("n2", "bob", false, string.Empty));
        var claim = ReviewClaim(41, "bob", reviewCycleId: null);

        Assert.True(ReviewRoutingService.IsReviewCycleCurrent(pullRequest, "bob", claim));
    }

    [Fact]
    public void EvaluateClaimRelease_keeps_the_current_cycle()
    {
        var pullRequest = Pr(41, "alice", ("n1", "bob", false, string.Empty));

        var decision = ReviewRoutingService.EvaluateClaimRelease(EnabledConfiguration, pullRequest, ReviewClaim(41, "bob", "n1"));

        Assert.Equal(ReviewClaimReleaseDecision.WouldKeep, decision);
    }

    [Fact]
    public void EvaluateClaimRelease_releases_a_stale_cycle_after_re_request()
    {
        var pullRequest = Pr(41, "alice", ("n2", "bob", false, string.Empty));

        var decision = ReviewRoutingService.EvaluateClaimRelease(EnabledConfiguration, pullRequest, ReviewClaim(41, "bob", "n1"));

        Assert.Equal(ReviewClaimReleaseDecision.WouldRelease, decision);
    }

    [Fact]
    public void EvaluateClaimRelease_releases_terminal_draft_and_contradictory_states()
    {
        var terminal = Pr(41, "alice", state: "merged", requests: ("n1", "bob", false, string.Empty));
        var draft = Pr(42, "alice", isDraft: true, requests: ("n2", "bob", false, string.Empty));
        var changesRequested = Pr(43, "alice", label: "codex:cr", requests: ("n3", "bob", false, string.Empty));

        Assert.Equal(ReviewClaimReleaseDecision.WouldRelease, ReviewRoutingService.EvaluateClaimRelease(EnabledConfiguration, terminal, ReviewClaim(41, "bob", "n1")));
        Assert.Equal(ReviewClaimReleaseDecision.WouldRelease, ReviewRoutingService.EvaluateClaimRelease(EnabledConfiguration, draft, ReviewClaim(42, "bob", "n2")));
        Assert.Equal(ReviewClaimReleaseDecision.WouldRelease, ReviewRoutingService.EvaluateClaimRelease(EnabledConfiguration, changesRequested, ReviewClaim(43, "bob", "n3")));
    }

    [Fact]
    public void EvaluateClaimRelease_cannot_determine_ambiguous_state()
    {
        var pullRequest = Pr(41, "alice", labels: new[] { "codex:cr", "codex:merge-ready" }, requests: ("n1", "bob", false, string.Empty));

        var decision = ReviewRoutingService.EvaluateClaimRelease(EnabledConfiguration, pullRequest, ReviewClaim(41, "bob", "n1"));

        Assert.Equal(ReviewClaimReleaseDecision.CannotDetermine, decision);
    }

    // --------------------------------------------------------------------------------------------
    // WorkflowService.CheckReviewWorkAsync (discovery)
    // --------------------------------------------------------------------------------------------

    [Fact]
    public async Task CheckReviewWorkAsync_disabled_returns_empty_success_without_calling_github()
    {
        var response = await WorkflowService.CheckReviewWorkAsync(
            new RouterConfiguration(),
            "wd",
            getAuthenticatedLogin: (_, _) => throw new InvalidOperationException("Must not resolve the authenticated account when review routing is disabled."));

        Assert.True(response.IsSuccessful);
        Assert.Empty(response.Tasks);
    }

    [Fact]
    public async Task CheckReviewWorkAsync_fails_closed_when_authenticated_account_cannot_be_resolved()
    {
        var response = await WorkflowService.CheckReviewWorkAsync(
            EnabledConfiguration,
            "wd",
            getAuthenticatedLogin: (_, _) => Task.FromResult<string?>(null));

        Assert.False(response.IsSuccessful);
        Assert.Contains("authenticated GitHub account", response.Message);
    }

    [Fact]
    public async Task CheckReviewWorkAsync_builds_a_review_task_for_the_requested_reviewer()
    {
        var pullRequest = Pr(41, "alice", ("n1", "bob", false, string.Empty));

        var response = await WorkflowService.CheckReviewWorkAsync(
            EnabledConfiguration,
            "wd",
            getAuthenticatedLogin: (_, _) => Task.FromResult<string?>("bob"),
            getReviewRequestedPullRequestNumbers: (_, login, _) => Task.FromResult(login == "bob" ? new List<int> { 41 } : new List<int>()),
            getPullRequest: (_, number, _) => Task.FromResult(pullRequest));

        Assert.True(response.IsSuccessful);
        var task = Assert.Single(response.Tasks);
        Assert.Equal(WorkflowItemType.PullRequestReview, task.Type);
        Assert.Equal(41, task.PullRequestNumber);
        Assert.Equal("bob", task.ReviewerLogin);
        Assert.Equal("n1", task.ReviewCycleId);
        Assert.Equal(0, task.IssueNumber);
        var consideredPullRequest = Assert.Single(response.ConsideredPullRequests);
        Assert.Equal(41, consideredPullRequest.Number);
    }

    [Fact]
    public async Task CheckReviewWorkAsync_skips_ineligible_pull_requests()
    {
        var draft = Pr(41, "alice", isDraft: true, requests: ("n1", "bob", false, string.Empty));
        var ready = Pr(42, "alice", ("n2", "bob", false, string.Empty));

        var response = await WorkflowService.CheckReviewWorkAsync(
            EnabledConfiguration,
            "wd",
            getAuthenticatedLogin: (_, _) => Task.FromResult<string?>("bob"),
            getReviewRequestedPullRequestNumbers: (_, login, _) => Task.FromResult(login == "bob" ? new List<int> { 41, 42 } : new List<int>()),
            getPullRequest: (_, number, _) => Task.FromResult(number == 41 ? draft : ready));

        Assert.True(response.IsSuccessful);
        var task = Assert.Single(response.Tasks);
        Assert.Equal(42, task.PullRequestNumber);
        Assert.Contains(response.ConsideredPullRequests, pullRequest => pullRequest.Number == 41);
        Assert.Contains(response.ConsideredPullRequests, pullRequest => pullRequest.Number == 42);
    }

    [Fact]
    public async Task CheckReviewWorkAsync_skips_pull_requests_that_are_no_longer_open()
    {
        var response = await WorkflowService.CheckReviewWorkAsync(
            EnabledConfiguration,
            "wd",
            getAuthenticatedLogin: (_, _) => Task.FromResult<string?>("bob"),
            getReviewRequestedPullRequestNumbers: (_, login, _) => Task.FromResult(login == "bob" ? new List<int> { 41 } : new List<int>()),
            getPullRequest: (_, number, _) => throw new GitHubItemNotFoundException($"Pull request #{number} was not found."));

        Assert.True(response.IsSuccessful);
        Assert.Empty(response.Tasks);
        Assert.Contains("No claimable review work", response.Message);
    }

    [Fact]
    public async Task CheckReviewWorkAsync_pull_request_fetch_failure_fails_closed()
    {
        var response = await WorkflowService.CheckReviewWorkAsync(
            EnabledConfiguration,
            "wd",
            getAuthenticatedLogin: (_, _) => Task.FromResult<string?>("bob"),
            getReviewRequestedPullRequestNumbers: (_, login, _) => Task.FromResult(login == "bob" ? new List<int> { 41 } : new List<int>()),
            getPullRequest: (_, number, _) => throw new InvalidOperationException("transient GitHub failure"));

        Assert.False(response.IsSuccessful);
        Assert.Contains("transient GitHub failure", response.Message);
    }

    [Fact]
    public async Task CheckReviewWorkAsync_review_request_discovery_failure_fails_closed()
    {
        var response = await WorkflowService.CheckReviewWorkAsync(
            EnabledConfiguration,
            "wd",
            getAuthenticatedLogin: (_, _) => Task.FromResult<string?>("bob"),
            getReviewRequestedPullRequestNumbers: (_, _, _) => throw new InvalidOperationException("gh failed"));

        Assert.False(response.IsSuccessful);
        Assert.Contains("gh failed", response.Message);
    }

    [Fact]
    public async Task CheckReviewWorkAsync_considers_assignment_identity_logins_and_the_authenticated_login()
    {
        var identity = new AssignmentIdentity { GitHubUsernames = new List<string> { "alias-1", "alias-2" } };
        var queriedLogins = new List<string>();

        await WorkflowService.CheckReviewWorkAsync(
            EnabledConfiguration,
            "wd",
            assignmentIdentity: identity,
            getAuthenticatedLogin: (_, _) => Task.FromResult<string?>("authenticated-reviewer"),
            getReviewRequestedPullRequestNumbers: (_, login, _) =>
            {
                queriedLogins.Add(login);
                return Task.FromResult(new List<int>());
            },
            getPullRequest: (_, number, _) => throw new InvalidOperationException("No pull requests were requested."));

        Assert.Equal(3, queriedLogins.Count);
        Assert.Contains("alias-1", queriedLogins);
        Assert.Contains("alias-2", queriedLogins);
        Assert.Contains("authenticated-reviewer", queriedLogins);
    }

    [Fact]
    public async Task CheckReviewWorkAsync_deduplicates_pull_requests_across_candidate_logins()
    {
        var pullRequest = Pr(41, "alice", ("n1", "bob", false, string.Empty));
        var fetched = new List<int>();

        var response = await WorkflowService.CheckReviewWorkAsync(
            EnabledConfiguration,
            "wd",
            assignmentIdentity: new AssignmentIdentity { GitHubUsernames = new List<string> { "alias-1" } },
            getAuthenticatedLogin: (_, _) => Task.FromResult<string?>("bob"),
            getReviewRequestedPullRequestNumbers: (_, login, _) => Task.FromResult(login == "bob" ? new List<int> { 41 } : new List<int> { 41 }),
            getPullRequest: (_, number, _) =>
            {
                fetched.Add(number);
                return Task.FromResult(pullRequest);
            });

        Assert.True(response.IsSuccessful);
        Assert.Single(response.Tasks);
        Assert.Single(fetched);
    }

    // --------------------------------------------------------------------------------------------
    // EvaluateClaimedReviewWork / CheckClaimedReviewWorkAsync
    // --------------------------------------------------------------------------------------------

    [Fact]
    public void EvaluateClaimedReviewWork_current_cycle_continues_the_review_task()
    {
        var pullRequest = Pr(41, "alice", ("n1", "bob", false, string.Empty));

        var response = WorkflowService.EvaluateClaimedReviewWork(EnabledConfiguration, ReviewClaim(41, "bob", "n1"), pullRequest);

        Assert.True(response.IsSuccessful);
        var task = Assert.Single(response.Tasks);
        Assert.Equal(WorkflowItemType.PullRequestReview, task.Type);
        Assert.Equal(41, task.PullRequestNumber);
        Assert.Equal("bob", task.ReviewerLogin);
        Assert.Equal("n1", task.ReviewCycleId);
    }

    [Theory]
    [InlineData("merged")]
    [InlineData("closed")]
    public void EvaluateClaimedReviewWork_terminal_pull_requests_release_the_claim(string state)
    {
        var pullRequest = Pr(41, "alice", state: state, requests: ("n1", "bob", false, string.Empty));

        var response = WorkflowService.EvaluateClaimedReviewWork(EnabledConfiguration, ReviewClaim(41, "bob", "n1"), pullRequest);

        Assert.True(response.IsSuccessful);
        Assert.Contains(response.Tasks, task => task.Type == WorkflowItemType.AwaitingReview);
    }

    [Fact]
    public void EvaluateClaimedReviewWork_draft_pull_requests_release_the_claim()
    {
        var pullRequest = Pr(41, "alice", isDraft: true, requests: ("n1", "bob", false, string.Empty));

        var response = WorkflowService.EvaluateClaimedReviewWork(EnabledConfiguration, ReviewClaim(41, "bob", "n1"), pullRequest);

        Assert.True(response.IsSuccessful);
        Assert.Contains(response.Tasks, task => task.Type == WorkflowItemType.AwaitingReview);
    }

    [Fact]
    public void EvaluateClaimedReviewWork_removed_reviewer_releases_the_claim()
    {
        var pullRequest = Pr(41, "alice");

        var response = WorkflowService.EvaluateClaimedReviewWork(EnabledConfiguration, ReviewClaim(41, "bob", "n1"), pullRequest);

        Assert.True(response.IsSuccessful);
        Assert.Contains(response.Tasks, task => task.Type == WorkflowItemType.AwaitingReview);
    }

    [Fact]
    public void EvaluateClaimedReviewWork_stale_cycle_releases_the_claim_for_re_request()
    {
        var pullRequest = Pr(41, "alice", ("n2", "bob", false, string.Empty));

        var response = WorkflowService.EvaluateClaimedReviewWork(EnabledConfiguration, ReviewClaim(41, "bob", "n1"), pullRequest);

        Assert.True(response.IsSuccessful);
        Assert.Contains(response.Tasks, task => task.Type == WorkflowItemType.AwaitingReview);
    }

    [Fact]
    public void EvaluateClaimedReviewWork_contradictory_cgr_state_releases_the_claim()
    {
        var pullRequest = Pr(41, "alice", label: "codex:cr", requests: ("n1", "bob", false, string.Empty));

        var response = WorkflowService.EvaluateClaimedReviewWork(EnabledConfiguration, ReviewClaim(41, "bob", "n1"), pullRequest);

        Assert.True(response.IsSuccessful);
        Assert.Contains(response.Tasks, task => task.Type == WorkflowItemType.AwaitingReview);
    }

    [Fact]
    public void EvaluateClaimedReviewWork_ambiguous_cgr_state_fails_closed()
    {
        var pullRequest = Pr(41, "alice", labels: new[] { "codex:cr", "codex:merge-ready" }, requests: ("n1", "bob", false, string.Empty));

        var response = WorkflowService.EvaluateClaimedReviewWork(EnabledConfiguration, ReviewClaim(41, "bob", "n1"), pullRequest);

        Assert.False(response.IsSuccessful);
        Assert.DoesNotContain(response.Tasks, task => task.Type == WorkflowItemType.AwaitingReview);
    }

    [Fact]
    public async Task CheckClaimedReviewWorkAsync_missing_pull_request_identity_fails_closed()
    {
        var response = await WorkflowService.CheckClaimedReviewWorkAsync(EnabledConfiguration, "wd", new WorkClaim
        {
            ClaimId = Guid.NewGuid(),
            Version = 1,
            WorktreeId = WorkClaimStore.MainWorktreeIdentity,
            OwnerSessionId = "session-a",
            IssueNumber = 0,
            WorkType = WorkClaimType.Review,
            ClaimedAt = DateTimeOffset.UtcNow,
            LastUpdatedAt = DateTimeOffset.UtcNow,
            ReviewerLogin = "bob"
        });

        Assert.False(response.IsSuccessful);
        Assert.Contains("missing a pull request identity", response.Message);
    }

    [Fact]
    public async Task CheckClaimedReviewWorkAsync_missing_pull_request_is_a_release_candidate()
    {
        var response = await WorkflowService.CheckClaimedReviewWorkAsync(
            EnabledConfiguration,
            "wd",
            ReviewClaim(41, "bob", "n1"),
            getPullRequest: (_, number, _) => throw new GitHubItemNotFoundException($"Pull request #{number} was not found."));

        Assert.True(response.IsSuccessful);
        Assert.Contains(response.Tasks, task => task.Type == WorkflowItemType.AwaitingReview);
    }

    [Fact]
    public async Task CheckClaimedReviewWorkAsync_transient_failure_fails_closed()
    {
        var response = await WorkflowService.CheckClaimedReviewWorkAsync(
            EnabledConfiguration,
            "wd",
            ReviewClaim(41, "bob", "n1"),
            getPullRequest: (_, _, _) => throw new InvalidOperationException("transient GitHub failure"));

        Assert.False(response.IsSuccessful);
        Assert.Contains("transient GitHub failure", response.Message);
    }

    // --------------------------------------------------------------------------------------------
    // HookTaskRouter routing and claim matching
    // --------------------------------------------------------------------------------------------

    [Fact]
    public void Pull_request_review_tasks_require_a_work_claim()
    {
        var review = new WorkflowItem { Type = WorkflowItemType.PullRequestReview, PullRequestNumber = 41, ReviewerLogin = "bob" };

        Assert.True(HookTaskRouter.RequiresWorkClaim(review));
    }

    [Fact]
    public void Review_task_follows_resume_but_precedes_new_issues()
    {
        var inProgress = new WorkflowItem { Type = WorkflowItemType.ResumeInProgressIssue, IssueNumber = 3, SelectionRank = 0 };
        var review = new WorkflowItem { Type = WorkflowItemType.PullRequestReview, PullRequestNumber = 41, ReviewerLogin = "bob", SelectionRank = 0 };
        var newIssue = new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 4, SelectionRank = 0 };

        var inProgressFirst = HookTaskRouter.Route(new[] { review, inProgress, newIssue });
        Assert.Equal(WorkflowItemType.ResumeInProgressIssue, inProgressFirst.SelectedTask!.Type);

        var reviewFirst = HookTaskRouter.Route(new[] { review, newIssue });
        Assert.Equal(WorkflowItemType.PullRequestReview, reviewFirst.SelectedTask!.Type);
        Assert.Equal(41, reviewFirst.SelectedTask.PullRequestNumber);
        Assert.Contains("review", reviewFirst.AdditionalContext, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RouteClaimedWork_selects_the_matching_review_task()
    {
        var review = new WorkflowItem { Type = WorkflowItemType.PullRequestReview, PullRequestNumber = 41, ReviewerLogin = "bob", SelectionRank = 0 };
        var claim = ReviewClaim(41, "bob", "n1");

        var decision = HookTaskRouter.RouteClaimedWork(claim, "session-a", new[] { review });

        Assert.Null(decision.BlockReason);
        Assert.Equal(WorkflowItemType.PullRequestReview, decision.SelectedTask!.Type);
        Assert.Equal(41, decision.SelectedTask.PullRequestNumber);
    }

    [Fact]
    public void RouteClaimedWork_blocks_a_review_claim_for_a_different_reviewer()
    {
        var review = new WorkflowItem { Type = WorkflowItemType.PullRequestReview, PullRequestNumber = 41, ReviewerLogin = "carol", SelectionRank = 0 };

        var decision = HookTaskRouter.RouteClaimedWork(ReviewClaim(41, "bob", "n1"), "session-a", new[] { review });

        Assert.NotNull(decision.BlockReason);
        Assert.Contains("was not found in the current workflow discovery", decision.BlockReason);
    }

    [Fact]
    public void RouteClaimedWork_does_not_match_review_work_to_an_implementation_claim()
    {
        var review = new WorkflowItem { Type = WorkflowItemType.PullRequestReview, PullRequestNumber = 41, ReviewerLogin = "bob", SelectionRank = 0 };
        var claim = new WorkClaim
        {
            ClaimId = Guid.NewGuid(),
            Version = 1,
            WorktreeId = WorkClaimStore.MainWorktreeIdentity,
            OwnerSessionId = "session-a",
            IssueNumber = 7,
            PullRequestNumber = 41,
            WorkType = WorkClaimType.Implementation,
            ClaimedAt = DateTimeOffset.UtcNow,
            LastUpdatedAt = DateTimeOffset.UtcNow
        };

        var decision = HookTaskRouter.RouteClaimedWork(claim, "session-a", new[] { review });

        Assert.NotNull(decision.BlockReason);
        Assert.Contains("was not found in the current workflow discovery", decision.BlockReason);
    }

    // --------------------------------------------------------------------------------------------
    // WorkClaimReconciliationService.DetermineReviewAsync
    // --------------------------------------------------------------------------------------------

    [Fact]
    public async Task DetermineReviewAsync_keeps_a_current_cycle_request()
    {
        var pullRequest = Pr(41, "alice", ("n1", "bob", false, string.Empty));

        var recommendation = await WorkClaimReconciliationService.DetermineReviewAsync(
            ReviewClaim(41, "bob", "n1"), EnabledConfiguration, _ => Task.FromResult(pullRequest));

        Assert.Equal(WorkClaimReconciliationRecommendation.WouldKeep, recommendation);
    }

    [Fact]
    public async Task DetermineReviewAsync_releases_when_the_reviewer_was_removed()
    {
        var pullRequest = Pr(41, "alice");

        var recommendation = await WorkClaimReconciliationService.DetermineReviewAsync(
            ReviewClaim(41, "bob", "n1"), EnabledConfiguration, _ => Task.FromResult(pullRequest));

        Assert.Equal(WorkClaimReconciliationRecommendation.WouldRelease, recommendation);
    }

    [Fact]
    public async Task DetermineReviewAsync_releases_a_stale_cycle_after_re_request()
    {
        var pullRequest = Pr(41, "alice", ("n2", "bob", false, string.Empty));

        var recommendation = await WorkClaimReconciliationService.DetermineReviewAsync(
            ReviewClaim(41, "bob", "n1"), EnabledConfiguration, _ => Task.FromResult(pullRequest));

        Assert.Equal(WorkClaimReconciliationRecommendation.WouldRelease, recommendation);
    }

    [Fact]
    public async Task DetermineReviewAsync_releases_a_merged_pull_request()
    {
        var pullRequest = Pr(41, "alice", state: "merged", requests: ("n1", "bob", false, string.Empty));

        var recommendation = await WorkClaimReconciliationService.DetermineReviewAsync(
            ReviewClaim(41, "bob", "n1"), EnabledConfiguration, _ => Task.FromResult(pullRequest));

        Assert.Equal(WorkClaimReconciliationRecommendation.WouldRelease, recommendation);
    }

    [Fact]
    public async Task DetermineReviewAsync_releases_contradictory_cgr_state()
    {
        var pullRequest = Pr(41, "alice", label: "codex:deferred", requests: ("n1", "bob", false, string.Empty));

        var recommendation = await WorkClaimReconciliationService.DetermineReviewAsync(
            ReviewClaim(41, "bob", "n1"), EnabledConfiguration, _ => Task.FromResult(pullRequest));

        Assert.Equal(WorkClaimReconciliationRecommendation.WouldRelease, recommendation);
    }

    [Fact]
    public async Task DetermineReviewAsync_releases_a_missing_pull_request()
    {
        var recommendation = await WorkClaimReconciliationService.DetermineReviewAsync(
            ReviewClaim(41, "bob", "n1"), EnabledConfiguration, _ => throw new GitHubItemNotFoundException("Pull request #41 was not found."));

        Assert.Equal(WorkClaimReconciliationRecommendation.WouldRelease, recommendation);
    }

    [Fact]
    public async Task DetermineReviewAsync_releases_a_claim_without_a_pull_request_identity()
    {
        var recommendation = await WorkClaimReconciliationService.DetermineReviewAsync(
            ReviewClaim(0, "bob", "n1"), EnabledConfiguration, _ => throw new InvalidOperationException("A PR-less review claim must not fetch anything."));

        Assert.Equal(WorkClaimReconciliationRecommendation.WouldRelease, recommendation);
    }

    [Fact]
    public async Task DetermineReviewAsync_unable_to_determine_on_transient_failure()
    {
        var recommendation = await WorkClaimReconciliationService.DetermineReviewAsync(
            ReviewClaim(41, "bob", "n1"), EnabledConfiguration, _ => throw new InvalidOperationException("transient GitHub failure"));

        Assert.Equal(WorkClaimReconciliationRecommendation.UnableToDetermine, recommendation);
    }

    // --------------------------------------------------------------------------------------------
    // RoutingEvaluationService review integration
    // --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Review_discovery_failure_fails_the_plan()
    {
        var dependencies = new RoutingEvaluationDependencies
        {
            CheckRepositoryGateAsync = (_, _) => Task.FromResult(OkGate()),
            CheckCompletedIssuesAsync = (_, _, _, _) => Task.FromResult(Ok()),
            CheckInProgressIssuesAsync = (_, _, _, _) => Task.FromResult(Ok()),
            CheckNewIssuesAsync = (_, _, _, _) => Task.FromResult(Ok()),
            CheckReviewWorkAsync = (_, _, _) => Task.FromResult(new WorkflowResponse { IsSuccessful = false, Message = "Review scan failed." })
        };

        var plan = await RoutingEvaluationService.EvaluateAsync(new RouterConfiguration(), "wd", dependencies: dependencies);

        Assert.False(plan.IsSuccessful);
        Assert.Equal("Review scan failed.", plan.DiscoveryFailureMessage);
    }

    [Fact]
    public async Task Review_task_is_selected_over_a_new_issue()
    {
        var review = new WorkflowItem { Type = WorkflowItemType.PullRequestReview, PullRequestNumber = 41, ReviewerLogin = "bob", SelectionRank = 0 };
        var newIssue = new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 4, SelectionRank = 0 };
        var reviewedPullRequest = Pr(41, "alice", ("n1", "bob", false, string.Empty));
        var dependencies = new RoutingEvaluationDependencies
        {
            CheckRepositoryGateAsync = (_, _) => Task.FromResult(OkGate()),
            CheckCompletedIssuesAsync = (_, _, _, _) => Task.FromResult(Ok()),
            CheckInProgressIssuesAsync = (_, _, _, _) => Task.FromResult(Ok()),
            CheckNewIssuesAsync = (_, _, _, _) => Task.FromResult(new WorkflowResponse
            {
                IsSuccessful = true,
                Tasks = new List<WorkflowItem> { newIssue },
                ConsideredIssues = new List<Issue> { new() { Number = 4 } }
            }),
            CheckReviewWorkAsync = (_, _, _) => Task.FromResult(new WorkflowResponse
            {
                IsSuccessful = true,
                Tasks = new List<WorkflowItem> { review },
                ConsideredPullRequests = new List<PullRequest> { reviewedPullRequest }
            })
        };

        var plan = await RoutingEvaluationService.EvaluateAsync(EnabledConfiguration, "wd", dependencies: dependencies);

        Assert.True(plan.IsSuccessful);
        Assert.Equal(WorkflowItemType.PullRequestReview, plan.Decision!.SelectedTask!.Type);
        Assert.Equal(41, plan.Decision.SelectedTask.PullRequestNumber);
        Assert.Equal(41, HookTaskRouter.Route(plan.ActionableTasks).SelectedTask!.PullRequestNumber);
        Assert.Equal(4, Assert.Single(plan.ConsideredIssues).Number);
        Assert.Single(plan.ConsideredPullRequests);

        var reviewExplanation = Assert.Single(RoutingExplanationService.ExplainReviewAll(plan, "bob"));
        Assert.True(reviewExplanation.IsEligible);
        Assert.True(reviewExplanation.IsSelected);
    }

    // --------------------------------------------------------------------------------------------
    // WorkClaimStore claim conflicts (integration)
    // --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Review_claims_conflict_on_the_same_pr_and_reviewer_across_worktrees()
    {
        using var sandbox = new TestSandbox();
        var linkedWorktree = sandbox.CreateLinkedWorktree("wt-a");
        Assert.True((await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId, ReviewClaim(41, "bob", "n1"))).Acquired);

        var blocked = await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, linkedWorktree, ReviewClaim(41, "bob", "n2"));

        Assert.False(blocked.Acquired);
        Assert.Contains("owned by another Git worktree", blocked.BlockReason);
        Assert.Equal(41, (await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId))!.PullRequestNumber);
        Assert.Null(await WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, linkedWorktree));
    }

    [Fact]
    public async Task Review_claims_for_different_reviewers_do_not_conflict_on_the_same_pr()
    {
        using var sandbox = new TestSandbox();
        var linkedWorktree = sandbox.CreateLinkedWorktree("wt-a");
        Assert.True((await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId, ReviewClaim(41, "bob", "n1"))).Acquired);

        var carolClaim = await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, linkedWorktree, ReviewClaim(41, "carol", "n3"));

        Assert.True(carolClaim.Acquired);
        Assert.Equal(2, (await WorkClaimStore.ReadAllAsync(sandbox.GitCommonDirectory)).Count);
    }

    [Fact]
    public async Task Review_and_implementation_claims_do_not_conflict_on_the_same_pr()
    {
        using var sandbox = new TestSandbox();
        var linkedWorktree = sandbox.CreateLinkedWorktree("wt-a");
        Assert.True((await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId, ReviewClaim(41, "bob", "n1"))).Acquired);

        var implementation = await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, linkedWorktree, new WorkClaim
        {
            OwnerSessionId = "session-implementer",
            IssueNumber = 7,
            PullRequestNumber = 41,
            WorkType = WorkClaimType.Implementation
        });

        Assert.True(implementation.Acquired);
        Assert.Equal(2, (await WorkClaimStore.ReadAllAsync(sandbox.GitCommonDirectory)).Count);
    }

    [Fact]
    public async Task A_worktree_can_enrich_its_review_claim_with_a_newer_review_cycle()
    {
        using var sandbox = new TestSandbox();
        Assert.True((await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId, ReviewClaim(41, "bob", "n1"))).Acquired);

        var refreshed = await WorkClaimStore.TryAcquireAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId, ReviewClaim(41, "bob", "n2"));

        Assert.True(refreshed.Acquired);
        Assert.Equal("n2", refreshed.Claim!.ReviewCycleId);
        Assert.Single(await WorkClaimStore.ReadAllAsync(sandbox.GitCommonDirectory));
    }

    [Fact]
    public async Task Read_rejects_a_review_claim_without_a_pull_request_identity()
    {
        using var sandbox = new TestSandbox();
        var claimId = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(sandbox.GitCommonDirectory, "codex-github-router.work.json"), $$"""
        {
          "ClaimId": "{{claimId}}",
          "Version": 1,
          "OwnerSessionId": "session-a",
          "IssueNumber": 0,
          "WorkType": 2,
          "ReviewerLogin": "bob",
          "ClaimedAt": "2026-07-28T12:00:00+00:00",
          "LastUpdatedAt": "2026-07-28T12:00:00+00:00"
        }
        """);

        await Assert.ThrowsAsync<WorkClaimFileException>(() => WorkClaimStore.ReadAsync(sandbox.GitCommonDirectory, sandbox.MainWorktreeId));
    }

    // --------------------------------------------------------------------------------------------
    // ReviewRequests JSON converter tolerance
    // --------------------------------------------------------------------------------------------

    [Fact]
    public void ReviewRequests_converter_accepts_the_graphql_nodes_shape()
    {
        var pullRequest = JsonSerializer.Deserialize<PullRequest>("""
        {
          "number": 41,
          "reviewRequests": {
            "nodes": [
              { "id": "n1", "requestedReviewer": { "__typename": "User", "login": "bob" } },
              { "id": "t1", "requestedReviewer": { "__typename": "Team", "slug": "codex-reviewers" } }
            ]
          }
        }
        """);

        Assert.NotNull(pullRequest);
        Assert.Equal(41, pullRequest!.Number);
        Assert.Equal(2, pullRequest.ReviewRequests.Count);
        Assert.Contains(pullRequest.ReviewRequests, request => request.Id == "n1" && request.ReviewerLogin == "bob" && !request.IsTeam);
        Assert.Contains(pullRequest.ReviewRequests, request => request.Id == "t1" && request.ReviewerSlug == "codex-reviewers" && request.IsTeam);
    }

    [Fact]
    public void ReviewRequests_converter_accepts_the_plain_array_shape()
    {
        var pullRequest = JsonSerializer.Deserialize<PullRequest>("""
        {
          "number": 41,
          "reviewRequests": [
            { "id": "n1", "requestedReviewer": { "login": "bob" } }
          ]
        }
        """);

        Assert.NotNull(pullRequest);
        var request = Assert.Single(pullRequest!.ReviewRequests);
        Assert.Equal("bob", request.ReviewerLogin);
        Assert.False(request.IsTeam);
    }

    private static WorkClaim ReviewClaim(int pullRequestNumber, string reviewerLogin, string? reviewCycleId)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkClaim
        {
            ClaimId = Guid.NewGuid(),
            Version = 1,
            WorktreeId = WorkClaimStore.MainWorktreeIdentity,
            OwnerSessionId = "session-a",
            PullRequestNumber = pullRequestNumber == 0 ? null : pullRequestNumber,
            WorkType = WorkClaimType.Review,
            ReviewerLogin = reviewerLogin,
            ReviewCycleId = reviewCycleId,
            ClaimedAt = now,
            LastUpdatedAt = now
        };
    }

    private static PullRequest Pr(
        int number,
        string authorLogin,
        params (string Id, string ReviewerLogin, bool IsTeam, string TeamSlug)[] requests) =>
        Pr(number, authorLogin, isDraft: false, state: "open", label: null, requests);

    private static PullRequest Pr(
        int number,
        string authorLogin,
        bool isDraft = false,
        string state = "open",
        string? label = null,
        params (string Id, string ReviewerLogin, bool IsTeam, string TeamSlug)[] requests)
    {
        return new PullRequest
        {
            Number = number,
            State = state,
            Title = $"Title {number}",
            IsDraft = isDraft,
            Author = new GithubUser { Login = authorLogin },
            Labels = label is null ? new List<GithubLabel>() : new List<GithubLabel> { new() { Name = label } },
            ReviewRequests = requests
                .Select(request => new PullRequestReviewRequest
                {
                    Id = request.Id,
                    ReviewerLogin = request.ReviewerLogin,
                    ReviewerSlug = request.TeamSlug,
                    IsTeam = request.IsTeam
                })
                .ToList()
        };
    }

    private static PullRequest Pr(int number, string authorLogin, string[] labels, params (string Id, string ReviewerLogin, bool IsTeam, string TeamSlug)[] requests)
    {
        var pullRequest = Pr(number, authorLogin, requests);
        return new PullRequest
        {
            Number = pullRequest.Number,
            State = pullRequest.State,
            Title = pullRequest.Title,
            IsDraft = pullRequest.IsDraft,
            Author = pullRequest.Author,
            Labels = labels.Select(label => new GithubLabel { Name = label }).ToList(),
            ReviewRequests = pullRequest.ReviewRequests
        };
    }

    private static WorkflowResponse OkGate() => new()
    {
        IsSuccessful = true,
        Tasks = new List<WorkflowItem>(),
        Message = "No blocking repository gates found.",
        ConsideredIssues = new List<Issue>()
    };

    private static WorkflowResponse Ok(params WorkflowItem[] tasks) => new() { IsSuccessful = true, Tasks = tasks.ToList() };
}
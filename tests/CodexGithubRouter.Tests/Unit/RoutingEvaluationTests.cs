using CodexGithubRouter.GitHub;
using CodexGithubRouter.Hooks;
using CodexGithubRouter.Work;
using CodexGithubRouter.Workflow;
using Xunit;

namespace CodexGithubRouter.Tests;

[Trait("Category", "Unit")]
public sealed class RoutingEvaluationTests
{
    [Fact]
    public async Task Decision_matches_production_route()
    {
        var changeRequest = new WorkflowItem { Type = WorkflowItemType.ChangeRequest, IssueNumber = 2, PullRequestNumber = 20, SelectionRank = 0 };
        var newIssue = new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 3, SelectionRank = 0 };
        var dependencies = new RoutingEvaluationDependencies
        {
            CheckRepositoryGateAsync = (_, _) => Task.FromResult(OkGate()),
            CheckCompletedIssuesAsync = (_, _, _, _) => Task.FromResult(Ok(changeRequest)),
            CheckInProgressIssuesAsync = (_, _, _, _) => Task.FromResult(Ok()),
            CheckNewIssuesAsync = (_, _, _, _) => Task.FromResult(Ok(newIssue))
        };

        var plan = await RoutingEvaluationService.EvaluateAsync(new RouterConfiguration(), "wd", dependencies: dependencies);

        Assert.True(plan.IsSuccessful);
        Assert.Equal(changeRequest.IssueNumber, plan.Decision!.SelectedTask!.IssueNumber);
        Assert.Equal(changeRequest.IssueNumber, HookTaskRouter.Route(plan.ActionableTasks).SelectedTask!.IssueNumber);
        Assert.Equal(HookService.ResolveRoutingBlockReason(plan.Decision, plan.NoEligibleWorkResponse), plan.BlockReason);
    }

    [Fact]
    public async Task Change_request_precedes_new_issue_context()
    {
        var changeRequest = new WorkflowItem { Type = WorkflowItemType.ChangeRequest, IssueNumber = 2, PullRequestNumber = 20, SelectionRank = 0 };
        var newIssue = new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 3, SelectionRank = 0 };
        var dependencies = new RoutingEvaluationDependencies
        {
            CheckRepositoryGateAsync = (_, _) => Task.FromResult(OkGate()),
            CheckCompletedIssuesAsync = (_, _, _, _) => Task.FromResult(Ok(changeRequest)),
            CheckInProgressIssuesAsync = (_, _, _, _) => Task.FromResult(Ok()),
            CheckNewIssuesAsync = (_, _, _, _) => Task.FromResult(Ok(newIssue))
        };

        var plan = await RoutingEvaluationService.EvaluateAsync(new RouterConfiguration(), "wd", dependencies: dependencies);

        Assert.Equal(changeRequest.IssueNumber, plan.Decision!.SelectedTask!.IssueNumber);
        Assert.Equal(WorkflowItemType.ChangeRequest, plan.Decision.SelectedTask.Type);
    }

    [Fact]
    public async Task Deferred_tasks_are_excluded_from_decision()
    {
        var deferred = new WorkflowItem { Type = WorkflowItemType.Deferred, IssueNumber = 4 };
        var dependencies = new RoutingEvaluationDependencies
        {
            CheckRepositoryGateAsync = (_, _) => Task.FromResult(OkGate()),
            CheckCompletedIssuesAsync = (_, _, _, _) => Task.FromResult(Ok()),
            CheckInProgressIssuesAsync = (_, _, _, _) => Task.FromResult(Ok()),
            CheckNewIssuesAsync = (_, _, _, _) => Task.FromResult(Ok(deferred))
        };

        var plan = await RoutingEvaluationService.EvaluateAsync(new RouterConfiguration(), "wd", dependencies: dependencies);

        Assert.Empty(plan.ActionableTasks);
        Assert.Contains("deferred", plan.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Gate_tasks_short_circuit_ordinary_discovery()
    {
        var gatedTask = new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 7, Status = new WorkflowTaskStatus { Message = "gated" } };
        var dependencies = new RoutingEvaluationDependencies
        {
            CheckRepositoryGateAsync = (_, _) => Task.FromResult(OkGate(gatedTask)),
            CheckCompletedIssuesAsync = (_, _, _, _) => throw new InvalidOperationException("Completed discovery must be short-circuited by the repository gate."),
            CheckInProgressIssuesAsync = (_, _, _, _) => throw new InvalidOperationException("In-progress discovery must be short-circuited by the repository gate."),
            CheckNewIssuesAsync = (_, _, _, _) => throw new InvalidOperationException("New-issue discovery must be short-circuited by the repository gate.")
        };

        var plan = await RoutingEvaluationService.EvaluateAsync(new RouterConfiguration(), "wd", dependencies: dependencies);

        Assert.True(plan.HasRepositoryGate);
        Assert.Equal(gatedTask.IssueNumber, plan.Decision!.SelectedTask!.IssueNumber);
        Assert.Equal(gatedTask.IssueNumber, HookTaskRouter.Route(plan.ActionableTasks).SelectedTask!.IssueNumber);
    }

    [Fact]
    public async Task Gate_failure_returns_failure_plan()
    {
        var dependencies = new RoutingEvaluationDependencies
        {
            CheckRepositoryGateAsync = (_, _) => Task.FromResult(new WorkflowResponse { IsSuccessful = false, Message = "Could not resolve issue filters from workflow configuration." })
        };

        var plan = await RoutingEvaluationService.EvaluateAsync(new RouterConfiguration(), "wd", dependencies: dependencies);

        Assert.False(plan.IsSuccessful);
        Assert.Equal("Could not resolve issue filters from workflow configuration.", plan.DiscoveryFailureMessage);
    }

    [Fact]
    public async Task Ordinary_discovery_failure_short_circuits_remaining_stages()
    {
        var inProgressCalled = false;
        var dependencies = new RoutingEvaluationDependencies
        {
            CheckRepositoryGateAsync = (_, _) => Task.FromResult(OkGate()),
            CheckCompletedIssuesAsync = (_, _, _, _) => Task.FromResult(new WorkflowResponse { IsSuccessful = false, Message = "Completed scan failed." }),
            CheckInProgressIssuesAsync = (_, _, _, _) => { inProgressCalled = true; return Task.FromResult(Ok()); },
            CheckNewIssuesAsync = (_, _, _, _) => { throw new InvalidOperationException("New-issue discovery must not run after completed discovery fails."); }
        };

        var plan = await RoutingEvaluationService.EvaluateAsync(new RouterConfiguration(), "wd", dependencies: dependencies);

        Assert.False(plan.IsSuccessful);
        Assert.Equal("Completed scan failed.", plan.DiscoveryFailureMessage);
        Assert.False(inProgressCalled);
    }

    [Fact]
    public async Task Candidate_universe_aggregates_all_three_stages()
    {
        var completed = new WorkflowItem { Type = WorkflowItemType.RecoverCompletedIssue, IssueNumber = 1, SelectionRank = 0 };
        var inProgress = new WorkflowItem { Type = WorkflowItemType.ResumeInProgressIssue, IssueNumber = 2, SelectionRank = 0 };
        var newIssue = new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 3, SelectionRank = 0 };
        var dependencies = new RoutingEvaluationDependencies
        {
            CheckRepositoryGateAsync = (_, _) => Task.FromResult(OkGate()),
            CheckCompletedIssuesAsync = (_, _, _, _) => Task.FromResult(Ok(completed, issueNumbers: new int[] { 1 })),
            CheckInProgressIssuesAsync = (_, _, _, _) => Task.FromResult(Ok(inProgress, issueNumbers: new int[] { 2 })),
            CheckNewIssuesAsync = (_, _, _, _) => Task.FromResult(Ok(newIssue, issueNumbers: new int[] { 3 }))
        };

        var plan = await RoutingEvaluationService.EvaluateAsync(new RouterConfiguration(), "wd", dependencies: dependencies);

        Assert.Equal(new[] { 1, 2, 3 }, plan.ConsideredIssues.Select(issue => issue.Number));
    }

    [Fact]
    public async Task No_eligible_work_preserves_aggregated_message()
    {
        var dependencies = new RoutingEvaluationDependencies
        {
            CheckRepositoryGateAsync = (_, _) => Task.FromResult(OkGate()),
            CheckCompletedIssuesAsync = (_, _, _, _) => Task.FromResult(new WorkflowResponse
            {
                IsSuccessful = true,
                Tasks = new List<WorkflowItem>(),
                IneligibleWorkerIssues = new[]
                {
                    new WorkerEligibility { IsEnabled = true, IsEligible = false, Message = "Issue #4 does not match the current worker." }
                }
            }),
            CheckInProgressIssuesAsync = (_, _, _, _) => Task.FromResult(Ok()),
            CheckNewIssuesAsync = (_, _, _, _) => Task.FromResult(new WorkflowResponse
            {
                IsSuccessful = true,
                Tasks = new List<WorkflowItem>(),
                IneligibleAssignmentIssues = new[]
                {
                    new AssignmentEligibility { IsEnabled = true, IsEligible = false, Message = "Issue #5 is assigned to another developer and assignment routing requires the current identity." }
                }
            })
        };

        var plan = await RoutingEvaluationService.EvaluateAsync(new RouterConfiguration(), "wd", dependencies: dependencies);

        Assert.False(string.IsNullOrWhiteSpace(plan.BlockReason));
        Assert.Contains("No eligible work", plan.BlockReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Issue #4", plan.BlockReason, StringComparison.Ordinal);
        Assert.Contains("Issue #5", plan.BlockReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gate_never_produces_plan_when_repository_gate_is_disabled()
    {
        var dependencies = new RoutingEvaluationDependencies
        {
            CheckRepositoryGateAsync = (_, _) => Task.FromResult(OkGate()),
            CheckCompletedIssuesAsync = (_, _, _, _) => Task.FromResult(Ok()),
            CheckInProgressIssuesAsync = (_, _, _, _) => Task.FromResult(Ok()),
            CheckNewIssuesAsync = (_, _, _, _) => Task.FromResult(Ok(new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 1 }))
        };

        var configuration = new RouterConfiguration
        {
            Policies = new RouterPolicies
            {
                RepositoryGate = new RepositoryGatePolicy { Labels = new List<string>() }
            }
        };

        var plan = await RoutingEvaluationService.EvaluateAsync(configuration, "wd", dependencies: dependencies);

        Assert.True(plan.IsSuccessful);
        Assert.False(plan.HasRepositoryGate);
        Assert.Equal(1, plan.Decision!.SelectedTask!.IssueNumber);
    }

    [Fact]
    public async Task Candidate_universe_is_populated_under_default_policies_via_production_evaluators()
    {
        var inProgress = new List<Issue> { new() { Number = 2, Labels = new List<GithubLabel> { new() { Name = "codex:working" } } } };
        var completed = new List<Issue> { new() { Number = 1, Labels = new List<GithubLabel> { new() { Name = "codex:done" } } } };
        var ready = new List<Issue> { new() { Number = 3, Labels = new List<GithubLabel> { new() { Name = "codex:ready" } } } };
        Func<int, Task<PullRequest>> unreachablePullRequest = _ => throw new InvalidOperationException("No pull request fetch is expected for issues without linked pull requests.");

        var configuration = new RouterConfiguration();
        var dependencies = new RoutingEvaluationDependencies
        {
            CheckRepositoryGateAsync = (_, _) => Task.FromResult(OkGate()),
            CheckCompletedIssuesAsync = (_, _, _, _) => WorkflowService.CheckIssueLinkedPullRequestsAsync(configuration, completed, unreachablePullRequest),
            CheckInProgressIssuesAsync = (_, _, _, _) => WorkflowService.EvaluateInProgressIssuesAsync(configuration, inProgress, unreachablePullRequest),
            CheckNewIssuesAsync = (_, _, _, _) => NewIssueProductionResponse(configuration, ready)
        };

        var plan = await RoutingEvaluationService.EvaluateAsync(configuration, "wd", dependencies: dependencies);

        Assert.True(plan.IsSuccessful);
        Assert.Equal(new[] { 1, 2, 3 }, plan.ConsideredIssues.Select(issue => issue.Number).OrderBy(number => number));
    }

    [Fact]
    public async Task Active_claim_short_circuits_ordinary_discovery_and_routes_claim()
    {
        var claim = new WorkClaim { OwnerSessionId = "owner", IssueNumber = 20, WorkType = WorkClaimType.ChangeRequest };
        var claimedTask = new WorkflowItem { Type = WorkflowItemType.ChangeRequest, IssueNumber = 20, PullRequestNumber = 200, SelectionRank = 0 };
        var dependencies = new RoutingEvaluationDependencies
        {
            EvaluateClaimReconciliationAsync = (_, _, _) => Task.FromResult(WorkClaimReconciliationRecommendation.WouldKeep),
            CheckClaimedWorkAsync = (_, _, _, _) => Task.FromResult(new WorkflowResponse
            {
                IsSuccessful = true,
                Tasks = new List<WorkflowItem> { claimedTask },
                ConsideredIssues = new List<Issue> { new() { Number = 20 } }
            }),
            CheckRepositoryGateAsync = (_, _) => { throw new InvalidOperationException("The repository gate must not run while a work claim is active."); },
            CheckCompletedIssuesAsync = (_, _, _, _) => { throw new InvalidOperationException("Ordinary discovery must not run while a work claim is active."); },
            CheckInProgressIssuesAsync = (_, _, _, _) => { throw new InvalidOperationException("Ordinary discovery must not run while a work claim is active."); },
            CheckNewIssuesAsync = (_, _, _, _) => { throw new InvalidOperationException("Ordinary discovery must not run while a work claim is active."); }
        };

        var plan = await RoutingEvaluationService.EvaluateAsync(new RouterConfiguration(), "wd", activeClaim: claim, dependencies: dependencies);

        Assert.True(plan.ClaimRoutingActive);
        Assert.NotNull(plan.ActiveClaim);
        Assert.Equal(200, plan.ActiveClaim!.PullRequestNumber);
        Assert.Equal(claim.IssueNumber, plan.Decision!.SelectedTask!.IssueNumber);
        Assert.Empty(plan.RepositoryGateTasks);
        Assert.Empty(plan.OrdinaryTasks);
        Assert.Equal(new[] { 20 }, plan.ConsideredIssues.Select(issue => issue.Number));
    }

    [Fact]
    public async Task Reconciled_claim_release_continues_ordinary_routing_after_simulated_release()
    {
        var claim = new WorkClaim { OwnerSessionId = "owner", IssueNumber = 9, WorkType = WorkClaimType.Implementation };
        var newIssue = new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 5, SelectionRank = 0 };
        var dependencies = new RoutingEvaluationDependencies
        {
            EvaluateClaimReconciliationAsync = (_, _, _) => Task.FromResult(WorkClaimReconciliationRecommendation.WouldRelease),
            CheckClaimedWorkAsync = (_, _, _, _) => { throw new InvalidOperationException("Claimed-work evaluation must not run when reconciliation releases the claim before routing."); },
            CheckRepositoryGateAsync = (_, _) => Task.FromResult(OkGate()),
            CheckCompletedIssuesAsync = (_, _, _, _) => Task.FromResult(Ok()),
            CheckInProgressIssuesAsync = (_, _, _, _) => Task.FromResult(Ok()),
            CheckNewIssuesAsync = (_, _, _, _) => Task.FromResult(Ok(newIssue))
        };

        var plan = await RoutingEvaluationService.EvaluateAsync(new RouterConfiguration(), "wd", activeClaim: claim, dependencies: dependencies);

        Assert.True(plan.IsSuccessful);
        Assert.False(plan.ClaimRoutingActive);
        Assert.Null(plan.ActiveClaim);
        Assert.Equal(claim.IssueNumber, plan.ReleasedClaim!.IssueNumber);
        Assert.Equal(newIssue.IssueNumber, plan.Decision!.SelectedTask!.IssueNumber);
    }

    [Fact]
    public async Task Blocked_claimed_issue_releases_and_ordinary_routing_continues()
    {
        var claim = new WorkClaim { OwnerSessionId = "owner", IssueNumber = 9, WorkType = WorkClaimType.Implementation };
        var newIssue = new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 5, SelectionRank = 0 };
        var plan = await RoutingEvaluationService.EvaluateAsync(
            new RouterConfiguration(),
            "wd",
            activeClaim: claim,
            dependencies: new RoutingEvaluationDependencies
            {
                EvaluateClaimReconciliationAsync = (_, _, _) => Task.FromResult(WorkClaimReconciliationRecommendation.WouldRelease),
                CheckClaimedWorkAsync = (_, _, _, _) => { throw new InvalidOperationException("A blocked claimed issue is released by reconciliation before claimed-work evaluation."); },
                CheckRepositoryGateAsync = (_, _) => Task.FromResult(OkGate()),
                CheckCompletedIssuesAsync = (_, _, _, _) => Task.FromResult(Ok()),
                CheckInProgressIssuesAsync = (_, _, _, _) => Task.FromResult(Ok()),
                CheckNewIssuesAsync = (_, _, _, _) => Task.FromResult(Ok(newIssue))
            });

        Assert.True(plan.IsSuccessful);
        Assert.Equal(claim.IssueNumber, plan.ReleasedClaim!.IssueNumber);
        Assert.Null(plan.ActiveClaim);
        Assert.Equal(newIssue.IssueNumber, plan.Decision!.SelectedTask!.IssueNumber);
    }

    [Fact]
    public async Task No_pr_claim_with_single_passive_candidate_pull_request_enriches_and_releases()
    {
        var claim = new WorkClaim { OwnerSessionId = "owner", IssueNumber = 9, WorkType = WorkClaimType.Implementation };
        var passiveTask = new WorkflowItem { Type = WorkflowItemType.AwaitingMerge, IssueNumber = 9, PullRequestNumber = 200 };
        var newIssue = new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 5, SelectionRank = 0 };
        var dependencies = new RoutingEvaluationDependencies
        {
            EvaluateClaimReconciliationAsync = (_, _, evaluated) => Task.FromResult(
                evaluated.PullRequestNumber.HasValue
                    ? WorkClaimReconciliationRecommendation.WouldRelease
                    : WorkClaimReconciliationRecommendation.WouldKeep),
            CheckClaimedWorkAsync = (_, _, _, _) => Task.FromResult(new WorkflowResponse
            {
                IsSuccessful = true,
                Tasks = new List<WorkflowItem> { passiveTask },
                ConsideredIssues = new List<Issue> { new() { Number = 9 } }
            }),
            CheckRepositoryGateAsync = (_, _) => Task.FromResult(OkGate()),
            CheckCompletedIssuesAsync = (_, _, _, _) => Task.FromResult(Ok()),
            CheckInProgressIssuesAsync = (_, _, _, _) => Task.FromResult(Ok()),
            CheckNewIssuesAsync = (_, _, _, _) => Task.FromResult(Ok(newIssue))
        };

        var plan = await RoutingEvaluationService.EvaluateAsync(new RouterConfiguration(), "wd", activeClaim: claim, dependencies: dependencies);

        Assert.True(plan.IsSuccessful);
        Assert.False(plan.ClaimRoutingActive);
        Assert.Null(plan.ActiveClaim);
        Assert.Equal(claim.IssueNumber, plan.ReleasedClaim!.IssueNumber);
        Assert.Equal(newIssue.IssueNumber, plan.Decision!.SelectedTask!.IssueNumber);
    }

    [Fact]
    public async Task Multiple_candidate_pull_requests_block_implicit_identity_selection()
    {
        var claim = new WorkClaim { OwnerSessionId = "owner", IssueNumber = 9, WorkType = WorkClaimType.Implementation };
        var dependencies = new RoutingEvaluationDependencies
        {
            EvaluateClaimReconciliationAsync = (_, _, _) => Task.FromResult(WorkClaimReconciliationRecommendation.WouldKeep),
            CheckClaimedWorkAsync = (_, _, _, _) => Task.FromResult(new WorkflowResponse
            {
                IsSuccessful = true,
                Tasks = new List<WorkflowItem>
                {
                    new() { Type = WorkflowItemType.AwaitingReview, IssueNumber = 9, PullRequestNumber = 201 },
                    new() { Type = WorkflowItemType.AwaitingReview, IssueNumber = 9, PullRequestNumber = 202 }
                },
                ConsideredIssues = new List<Issue> { new() { Number = 9 } }
            }),
            CheckRepositoryGateAsync = (_, _) => { throw new InvalidOperationException("The repository gate must not run while a work claim is active."); }
        };

        var plan = await RoutingEvaluationService.EvaluateAsync(new RouterConfiguration(), "wd", activeClaim: claim, dependencies: dependencies);

        Assert.False(plan.IsSuccessful);
        Assert.Null(plan.ReleasedClaim);
        Assert.Contains("multiple candidate pull requests", plan.DiscoveryFailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#201", plan.DiscoveryFailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#202", plan.DiscoveryFailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Would_keep_release_candidate_claim_blocks_instead_of_rerouting()
    {
        var claim = new WorkClaim { OwnerSessionId = "owner", IssueNumber = 9, PullRequestNumber = 200, WorkType = WorkClaimType.Implementation };
        var passiveTask = new WorkflowItem { Type = WorkflowItemType.AwaitingMerge, IssueNumber = 9, PullRequestNumber = 200 };
        var dependencies = new RoutingEvaluationDependencies
        {
            EvaluateClaimReconciliationAsync = (_, _, _) => Task.FromResult(WorkClaimReconciliationRecommendation.WouldKeep),
            CheckClaimedWorkAsync = (_, _, _, _) => Task.FromResult(new WorkflowResponse
            {
                IsSuccessful = true,
                Tasks = new List<WorkflowItem> { passiveTask },
                ConsideredIssues = new List<Issue> { new() { Number = 9 } }
            }),
            CheckRepositoryGateAsync = (_, _) => { throw new InvalidOperationException("A kept release-candidate claim must not continue ordinary routing."); },
            CheckCompletedIssuesAsync = (_, _, _, _) => { throw new InvalidOperationException("A kept release-candidate claim must not continue ordinary routing."); },
            CheckInProgressIssuesAsync = (_, _, _, _) => { throw new InvalidOperationException("A kept release-candidate claim must not continue ordinary routing."); },
            CheckNewIssuesAsync = (_, _, _, _) => { throw new InvalidOperationException("A kept release-candidate claim must not continue ordinary routing."); }
        };

        var plan = await RoutingEvaluationService.EvaluateAsync(new RouterConfiguration(), "wd", activeClaim: claim, dependencies: dependencies);

        Assert.False(plan.IsSuccessful);
        Assert.Null(plan.ReleasedClaim);
        Assert.Contains("could not be released safely", plan.DiscoveryFailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pull request #200", plan.DiscoveryFailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unable_to_determine_reconciliation_fails_closed()
    {
        var claim = new WorkClaim { OwnerSessionId = "owner", IssueNumber = 9, WorkType = WorkClaimType.Implementation };
        var dependencies = new RoutingEvaluationDependencies
        {
            EvaluateClaimReconciliationAsync = (_, _, _) => Task.FromResult(WorkClaimReconciliationRecommendation.UnableToDetermine),
            CheckClaimedWorkAsync = (_, _, _, _) => { throw new InvalidOperationException("Claimed-work evaluation must not run when reconciliation cannot be determined."); },
            CheckRepositoryGateAsync = (_, _) => { throw new InvalidOperationException("Ordinary routing must not run when reconciliation cannot be determined."); }
        };

        var plan = await RoutingEvaluationService.EvaluateAsync(new RouterConfiguration(), "wd", activeClaim: claim, dependencies: dependencies);

        Assert.False(plan.IsSuccessful);
        Assert.Null(plan.ReleasedClaim);
        Assert.Contains("could not be reconciled", plan.DiscoveryFailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Claim_path_guarantees_claimed_issue_in_candidate_universe()
    {
        var claim = new WorkClaim { OwnerSessionId = "owner", IssueNumber = 8, WorkType = WorkClaimType.Implementation };
        var claimedTask = new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 8, SelectionRank = 0 };
        var dependencies = new RoutingEvaluationDependencies
        {
            EvaluateClaimReconciliationAsync = (_, _, _) => Task.FromResult(WorkClaimReconciliationRecommendation.WouldKeep),
            CheckClaimedWorkAsync = (_, _, _, _) => Task.FromResult(new WorkflowResponse
            {
                IsSuccessful = true,
                Tasks = new List<WorkflowItem> { claimedTask }
            }),
            CheckRepositoryGateAsync = (_, _) => { throw new InvalidOperationException("The repository gate must not run while a work claim is active."); }
        };

        var plan = await RoutingEvaluationService.EvaluateAsync(new RouterConfiguration(), "wd", activeClaim: claim, dependencies: dependencies);

        Assert.True(plan.ClaimRoutingActive);
        Assert.Contains(plan.ConsideredIssues, issue => issue.Number == claim.IssueNumber);
        Assert.Equal(claim.IssueNumber, plan.Decision!.SelectedTask!.IssueNumber);
    }

    [Fact]
    public async Task Gate_short_circuits_identity_resolution()
    {
        var configuration = new RouterConfiguration
        {
            Policies = new RouterPolicies
            {
                AssignmentRouting = new AssignmentRoutingPolicy { Mode = "require", Unassigned = "allow" }
            }
        };
        var gatedTask = new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 7, Status = new WorkflowTaskStatus { Message = "gated" } };
        var dependencies = new RoutingEvaluationDependencies
        {
            CheckRepositoryGateAsync = (_, _) => Task.FromResult(OkGate(gatedTask)),
            ResolveAssignmentIdentityAsync = (_, _) => { throw new InvalidOperationException("Identity resolution must not run when the repository gate short-circuits routing."); },
            CheckCompletedIssuesAsync = (_, _, _, _) => { throw new InvalidOperationException("Ordinary discovery must not run when the repository gate short-circuits routing."); },
            CheckInProgressIssuesAsync = (_, _, _, _) => { throw new InvalidOperationException("Ordinary discovery must not run when the repository gate short-circuits routing."); },
            CheckNewIssuesAsync = (_, _, _, _) => { throw new InvalidOperationException("Ordinary discovery must not run when the repository gate short-circuits routing."); }
        };

        var plan = await RoutingEvaluationService.EvaluateAsync(configuration, "wd", dependencies: dependencies);

        Assert.True(plan.HasRepositoryGate);
        Assert.False(plan.IdentityResolution.IsEnabled);
        Assert.Null(plan.AssignmentIdentity);
        Assert.Equal(gatedTask.IssueNumber, plan.Decision!.SelectedTask!.IssueNumber);
    }

    [Fact]
    public async Task Ordinary_identity_resolution_failure_fails_closed_before_discovery()
    {
        var configuration = new RouterConfiguration
        {
            Policies = new RouterPolicies
            {
                AssignmentRouting = new AssignmentRoutingPolicy { Mode = "require", Unassigned = "allow" }
            }
        };
        const string identityMessage = "Assignment routing is enabled, but the current identity could not be resolved: no CGR Git identity is configured and the authenticated GitHub account is unavailable.";
        var dependencies = new RoutingEvaluationDependencies
        {
            CheckRepositoryGateAsync = (_, _) => Task.FromResult(OkGate()),
            ResolveAssignmentIdentityAsync = (_, _) => Task.FromResult(AssignmentIdentityResolution.Failure(identityMessage)),
            CheckCompletedIssuesAsync = (_, _, _, _) => { throw new InvalidOperationException("Discovery must not run after identity resolution fails."); },
            CheckInProgressIssuesAsync = (_, _, _, _) => { throw new InvalidOperationException("Discovery must not run after identity resolution fails."); },
            CheckNewIssuesAsync = (_, _, _, _) => { throw new InvalidOperationException("Discovery must not run after identity resolution fails."); }
        };

        var plan = await RoutingEvaluationService.EvaluateAsync(configuration, "wd", dependencies: dependencies);

        Assert.False(plan.IsSuccessful);
        Assert.Equal(identityMessage, plan.DiscoveryFailureMessage);
        Assert.True(plan.IdentityResolution.IsEnabled);
        Assert.False(plan.IdentityResolution.IsResolved);
    }

    [Fact]
    public async Task Ordinary_path_resolves_identity_before_discovery()
    {
        var configuration = new RouterConfiguration
        {
            Policies = new RouterPolicies
            {
                AssignmentRouting = new AssignmentRoutingPolicy { Mode = "prefer", Unassigned = "allow" }
            }
        };
        AssignmentIdentity? observedIdentity = null;
        var newIssue = new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 3, SelectionRank = 0 };
        var dependencies = new RoutingEvaluationDependencies
        {
            CheckRepositoryGateAsync = (_, _) => Task.FromResult(OkGate()),
            ResolveAssignmentIdentityAsync = (_, _) => Task.FromResult(new AssignmentIdentityResolution
            {
                IsEnabled = true,
                IsResolved = true,
                Identity = new AssignmentIdentity { Name = "alice", GitHubUsernames = new[] { "alice" } }
            }),
            CheckCompletedIssuesAsync = (_, _, _, _) => Task.FromResult(Ok()),
            CheckInProgressIssuesAsync = (_, _, _, _) => Task.FromResult(Ok()),
            CheckNewIssuesAsync = (_, _, _, identity) => { observedIdentity = identity; return Task.FromResult(Ok(newIssue)); }
        };

        var plan = await RoutingEvaluationService.EvaluateAsync(configuration, "wd", dependencies: dependencies);

        Assert.Equal("alice", plan.AssignmentIdentity?.Name);
        Assert.Equal("alice", observedIdentity?.Name);
        Assert.True(plan.IdentityResolution.IsResolved);
    }

    private static Task<WorkflowResponse> NewIssueProductionResponse(RouterConfiguration configuration, IReadOnlyList<Issue> openIssues)
    {
        var workflowTasks = openIssues.Select(issue => new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = issue.Number }).ToList();
        var response = new WorkflowResponse
        {
            Tasks = workflowTasks,
            IsSuccessful = true,
            Message = "New issues found.",
            ConsideredIssues = openIssues.ToList()
        };
        var workerFiltered = WorkerRoutingService.FilterCodingTasks(configuration, openIssues, response, currentModel: null);
        return Task.FromResult(AssignmentRoutingService.FilterCodingTasks(configuration, identity: null, openIssues, workerFiltered));
    }

    private static WorkflowResponse OkGate(params WorkflowItem[] tasks) => new()
    {
        IsSuccessful = true,
        Tasks = tasks.ToList(),
        Message = tasks.Length == 0 ? "No blocking repository gates found." : "Repository gate evaluation completed.",
        ConsideredIssues = tasks.Select(task => new Issue { Number = task.IssueNumber }).ToList()
    };

    private static WorkflowResponse Ok(params WorkflowItem[] tasks) => new() { IsSuccessful = true, Tasks = tasks.ToList() };

    private static WorkflowResponse Ok(WorkflowItem task, int[] issueNumbers) => new()
    {
        IsSuccessful = true,
        Tasks = new List<WorkflowItem> { task },
        ConsideredIssues = issueNumbers.Select(number => new Issue { Number = number }).ToList()
    };
}
using CodexGithubRouter.GitHub;
using CodexGithubRouter.Hooks;
using CodexGithubRouter.Work;
using CodexGithubRouter.Workflow;
using Xunit;

namespace CodexGithubRouter.Tests;

[Trait("Category", "Unit")]
public sealed class RoutingExplanationTests
{
    [Fact]
    public void Disabled_routing_leaves_ready_issue_eligible()
    {
        var issue = ReadyIssue(1);
        var plan = Plan(consideredIssues: new[] { issue });

        var explanation = RoutingExplanationService.Explain(plan, issue);

        Assert.True(explanation.IsEligible);
        Assert.Equal(int.MaxValue, explanation.SelectionRank);
        Assert.DoesNotContain(explanation.Stages, stage => stage.Verdict == RoutingVerdict.HardIneligible);
    }

    [Fact]
    public void Workflow_state_ready_is_eligible()
    {
        var issue = ReadyIssue(1);
        var plan = Plan(consideredIssues: new[] { issue });

        var explanation = RoutingExplanationService.Explain(plan, issue);

        var workflowStage = Assert.Single(explanation.Stages, s => s.Name == "Workflow State");
        Assert.Equal(RoutingVerdict.Pass, workflowStage.Verdict);
        Assert.Contains("Ready", workflowStage.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Considered_issue_reports_production_discovery()
    {
        var issue = ReadyIssue(1);
        var plan = Plan(consideredIssues: new[] { issue });

        var explanation = RoutingExplanationService.Explain(plan, issue);

        var discoveryStage = Assert.Single(explanation.Stages, s => s.Name == "Candidate Discovery");
        Assert.Equal(RoutingVerdict.Pass, discoveryStage.Verdict);
        Assert.Contains("production routing scan", discoveryStage.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unconsidered_issue_reports_out_of_candidate_universe()
    {
        var issue = ReadyIssue(1);
        var considered = ReadyIssue(2);
        var plan = Plan(consideredIssues: new[] { considered });

        var explanation = RoutingExplanationService.Explain(plan, issue);

        var discoveryStage = Assert.Single(explanation.Stages, s => s.Name == "Candidate Discovery");
        Assert.Equal(RoutingVerdict.SoftIneligible, discoveryStage.Verdict);
        Assert.Contains("not part of the production candidate discovery", discoveryStage.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(explanation.IsEligible);
    }

    [Fact]
    public void Gated_unrelated_issue_reports_gate_short_circuit()
    {
        var unrelated = ReadyIssue(3);
        var gatedReady = GatedIssue(1);
        var gatedTask = new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 1, Status = new WorkflowTaskStatus { Message = "Repository workflow is gated by issue #1." } };
        var plan = Plan(
            consideredIssues: new[] { gatedReady },
            repositoryGateTasks: new[] { gatedTask },
            actionableTasks: new[] { gatedTask },
            decision: new HookTaskDecision { SelectedTask = gatedTask, AdditionalContext = "gated" });

        var explanation = RoutingExplanationService.Explain(plan, unrelated);

        var discoveryStage = Assert.Single(explanation.Stages, s => s.Name == "Candidate Discovery");
        Assert.Equal(RoutingVerdict.SoftIneligible, discoveryStage.Verdict);
        Assert.Contains("short-circuits unrelated discovery", discoveryStage.Message, StringComparison.OrdinalIgnoreCase);
        var gateStage = Assert.Single(explanation.Stages, s => s.Name == "Repository Gate");
        Assert.Equal(RoutingVerdict.Pass, gateStage.Verdict);
        Assert.Contains("short-circuits", gateStage.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Workflow_state_blocked_is_hard_ineligible()
    {
        var issue = BlockedIssue(1);
        var plan = Plan(consideredIssues: new[] { issue });

        var explanation = RoutingExplanationService.Explain(plan, issue);

        Assert.False(explanation.IsEligible);
        var workflowStage = Assert.Single(explanation.Stages, s => s.Name == "Workflow State");
        Assert.Equal(RoutingVerdict.HardIneligible, workflowStage.Verdict);
        Assert.Contains("Blocked", workflowStage.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_workflow_labels_is_hard_ineligible()
    {
        var issue = new Issue { Number = 1, Labels = new List<GithubLabel>() };
        var plan = Plan(consideredIssues: new[] { issue });

        var explanation = RoutingExplanationService.Explain(plan, issue);

        Assert.False(explanation.IsEligible);
        var workflowStage = Assert.Single(explanation.Stages, s => s.Name == "Workflow State");
        Assert.Equal(RoutingVerdict.HardIneligible, workflowStage.Verdict);
        Assert.Contains("no recognized workflow labels", workflowStage.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Worker_routing_disabled_stage_is_disabled()
    {
        var issue = ReadyIssue(1);
        var plan = Plan(consideredIssues: new[] { issue });

        var explanation = RoutingExplanationService.Explain(plan, issue);

        var workerStage = Assert.Single(explanation.Stages, s => s.Name == "Worker Routing");
        Assert.Equal(RoutingVerdict.Disabled, workerStage.Verdict);
    }

    [Fact]
    public void Worker_routing_matching_worker_is_pass()
    {
        var configuration = WorkerConfiguration();
        var issue = ReadyIssue(1, "codex:worker:luna");
        var plan = Plan(configuration, currentModel: "luna-model", consideredIssues: new[] { issue });

        var explanation = RoutingExplanationService.Explain(plan, issue);

        var workerStage = Assert.Single(explanation.Stages, s => s.Name == "Worker Routing");
        Assert.Equal(RoutingVerdict.Pass, workerStage.Verdict);
        Assert.Contains("luna", workerStage.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Worker_routing_mismatched_worker_is_hard_ineligible()
    {
        var configuration = WorkerConfiguration();
        var issue = ReadyIssue(1, "codex:worker:luna");
        var plan = Plan(configuration, currentModel: "terra-model", consideredIssues: new[] { issue });

        var explanation = RoutingExplanationService.Explain(plan, issue);

        Assert.False(explanation.IsEligible);
        var workerStage = Assert.Single(explanation.Stages, s => s.Name == "Worker Routing");
        Assert.Equal(RoutingVerdict.HardIneligible, workerStage.Verdict);
    }

    [Fact]
    public void Assignment_routing_disabled_stage_is_disabled()
    {
        var issue = ReadyIssue(1);
        var plan = Plan(consideredIssues: new[] { issue });

        var explanation = RoutingExplanationService.Explain(plan, issue);

        var assignmentStage = Assert.Single(explanation.Stages, s => s.Name == "Assignment Routing");
        Assert.Equal(RoutingVerdict.Disabled, assignmentStage.Verdict);
    }

    [Fact]
    public void Assignment_routing_assigned_to_current_identity_is_soft_prefer()
    {
        var configuration = AssignmentConfiguration("prefer", "allow");
        var identity = Identity("alice");
        var issue = IssueWithAssignee(1, "alice");
        var plan = Plan(configuration, identity: identity, consideredIssues: new[] { issue });

        var explanation = RoutingExplanationService.Explain(plan, issue);

        var assignmentStage = Assert.Single(explanation.Stages, s => s.Name == "Assignment Routing");
        Assert.Equal(RoutingVerdict.SoftPrefer, assignmentStage.Verdict);
        Assert.Equal(0, explanation.SelectionRank);
    }

    [Fact]
    public void Assignment_routing_unassigned_is_soft_ineligible_rank_1()
    {
        var configuration = AssignmentConfiguration("prefer", "allow");
        var identity = Identity("alice");
        var issue = IssueWithAssignee(1);
        var plan = Plan(configuration, identity: identity, consideredIssues: new[] { issue });

        var explanation = RoutingExplanationService.Explain(plan, issue);

        var assignmentStage = Assert.Single(explanation.Stages, s => s.Name == "Assignment Routing");
        Assert.Equal(RoutingVerdict.SoftIneligible, assignmentStage.Verdict);
        Assert.Equal(1, explanation.SelectionRank);
    }

    [Fact]
    public void Assignment_routing_other_developer_is_soft_ineligible_rank_2()
    {
        var configuration = AssignmentConfiguration("prefer", "allow");
        var identity = Identity("alice");
        var issue = IssueWithAssignee(1, "bob");
        var plan = Plan(configuration, identity: identity, consideredIssues: new[] { issue });

        var explanation = RoutingExplanationService.Explain(plan, issue);

        var assignmentStage = Assert.Single(explanation.Stages, s => s.Name == "Assignment Routing");
        Assert.Equal(RoutingVerdict.SoftIneligible, assignmentStage.Verdict);
        Assert.Equal(2, explanation.SelectionRank);
    }

    [Fact]
    public void Assignment_routing_require_mode_other_developer_is_hard_ineligible()
    {
        var configuration = AssignmentConfiguration("require", "allow");
        var identity = Identity("alice");
        var issue = IssueWithAssignee(1, "bob");
        var plan = Plan(configuration, identity: identity, consideredIssues: new[] { issue });

        var explanation = RoutingExplanationService.Explain(plan, issue);

        Assert.False(explanation.IsEligible);
        var assignmentStage = Assert.Single(explanation.Stages, s => s.Name == "Assignment Routing");
        Assert.Equal(RoutingVerdict.HardIneligible, assignmentStage.Verdict);
    }

    [Fact]
    public void Repository_gate_not_configured_stage_is_disabled()
    {
        var configuration = new RouterConfiguration
        {
            Policies = new RouterPolicies
            {
                RepositoryGate = new RepositoryGatePolicy { Labels = new List<string>() }
            }
        };
        var issue = ReadyIssue(1);
        var plan = Plan(configuration, consideredIssues: new[] { issue });

        var explanation = RoutingExplanationService.Explain(plan, issue);

        var gateStage = Assert.Single(explanation.Stages, s => s.Name == "Repository Gate");
        Assert.Equal(RoutingVerdict.Disabled, gateStage.Verdict);
    }

    [Fact]
    public void Gated_ready_issue_is_the_routed_gate_work_and_selected()
    {
        var issue = GatedIssue(1);
        var gatedTask = new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 1, Status = new WorkflowTaskStatus { Message = "Repository workflow is gated by issue #1." } };
        var plan = Plan(
            consideredIssues: new[] { issue },
            repositoryGateTasks: new[] { gatedTask },
            actionableTasks: new[] { gatedTask },
            decision: new HookTaskDecision { SelectedTask = gatedTask, AdditionalContext = "gated" });

        var explanation = RoutingExplanationService.Explain(plan, issue);

        Assert.True(explanation.IsEligible);
        var gateStage = Assert.Single(explanation.Stages, s => s.Name == "Repository Gate");
        Assert.Equal(RoutingVerdict.Pass, gateStage.Verdict);
        Assert.Contains("gate short-circuit work", gateStage.Message, StringComparison.OrdinalIgnoreCase);
        var outcomeStage = Assert.Single(explanation.Stages, s => s.Name == "Routing Outcome");
        Assert.Equal(RoutingVerdict.Selected, outcomeStage.Verdict);
        Assert.True(explanation.IsSelected);
    }

    [Fact]
    public void Gated_blocked_issue_is_hard_ineligible()
    {
        var issue = new Issue { Number = 2, Labels = new List<GithubLabel> { new() { Name = "codex:blocked" }, new() { Name = "codex:gate" } } };
        const string gateBlock = "Repository workflow is gated by issue #2, which is blocked or needs information. Remove codex:gate from issue #2 to allow unrelated work.";
        var gatedTask = new WorkflowItem { Type = WorkflowItemType.RepositoryGateBlock, IssueNumber = 2, Status = new WorkflowTaskStatus { Message = gateBlock } };
        var plan = Plan(
            consideredIssues: new[] { issue },
            repositoryGateTasks: new[] { gatedTask },
            actionableTasks: new[] { gatedTask },
            blockReason: gateBlock);

        var explanation = RoutingExplanationService.Explain(plan, issue);

        Assert.False(explanation.IsEligible);
        var gateStage = Assert.Single(explanation.Stages, s => s.Name == "Repository Gate");
        Assert.Equal(RoutingVerdict.HardIneligible, gateStage.Verdict);
        Assert.Contains("gated by issue #2", gateStage.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Non_gated_issue_is_pass()
    {
        var issue = ReadyIssue(1);
        var plan = Plan(consideredIssues: new[] { issue });

        var explanation = RoutingExplanationService.Explain(plan, issue);

        var gateStage = Assert.Single(explanation.Stages, s => s.Name == "Repository Gate");
        Assert.Equal(RoutingVerdict.Pass, gateStage.Verdict);
    }

    [Fact]
    public void No_active_claim_stage_is_pass()
    {
        var issue = ReadyIssue(1);
        var plan = Plan(consideredIssues: new[] { issue });

        var explanation = RoutingExplanationService.Explain(plan, issue);

        var claimStage = Assert.Single(explanation.Stages, s => s.Name == "Work Claim");
        Assert.Equal(RoutingVerdict.Pass, claimStage.Verdict);
        Assert.Contains("No active work claim", claimStage.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Active_claim_for_same_issue_is_soft_prefer()
    {
        var issue = ReadyIssue(1);
        var claim = new WorkClaim { OwnerSessionId = "owner", IssueNumber = 1, WorkType = WorkClaimType.Implementation };
        var plan = Plan(consideredIssues: new[] { issue }, claim: claim);

        var explanation = RoutingExplanationService.Explain(plan, issue);

        var claimStage = Assert.Single(explanation.Stages, s => s.Name == "Work Claim");
        Assert.Equal(RoutingVerdict.SoftPrefer, claimStage.Verdict);
        Assert.Contains("Active work claim exists", claimStage.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Active_claim_for_different_issue_is_hard_ineligible()
    {
        var issue = ReadyIssue(2);
        var claim = new WorkClaim { OwnerSessionId = "owner", IssueNumber = 1, WorkType = WorkClaimType.Implementation };
        var plan = Plan(consideredIssues: new[] { issue }, claim: claim);

        var explanation = RoutingExplanationService.Explain(plan, issue);

        var claimStage = Assert.Single(explanation.Stages, s => s.Name == "Work Claim");
        Assert.Equal(RoutingVerdict.HardIneligible, claimStage.Verdict);
        Assert.Contains("Active work claim is held", claimStage.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Conflicting_active_claim_propagates_to_overall_ineligibility()
    {
        var issue = ReadyIssue(2);
        var claim = new WorkClaim { OwnerSessionId = "owner", IssueNumber = 1, WorkType = WorkClaimType.Implementation };
        var plan = Plan(consideredIssues: new[] { issue }, claim: claim);

        var explanation = RoutingExplanationService.Explain(plan, issue);

        Assert.False(explanation.IsEligible);
        Assert.False(explanation.IsSelected);
    }

    [Fact]
    public void Explanation_uses_same_worker_routing_evaluation_as_production()
    {
        var configuration = WorkerConfiguration();
        var issue = ReadyIssue(1, "codex:worker:luna");
        var production = WorkerRoutingService.Evaluate(configuration, issue, "terra-model");
        var plan = Plan(configuration, currentModel: "terra-model", consideredIssues: new[] { issue });

        var explanation = RoutingExplanationService.Explain(plan, issue);

        var workerStage = Assert.Single(explanation.Stages, s => s.Name == "Worker Routing");
        Assert.Equal(production.IsEnabled, workerStage.Verdict != RoutingVerdict.Disabled);
        Assert.Equal(production.IsEligible, workerStage.Verdict is RoutingVerdict.Pass);
    }

    [Fact]
    public void Explanation_uses_same_assignment_routing_evaluation_as_production()
    {
        var configuration = AssignmentConfiguration("require", "allow");
        var identity = Identity("alice");
        var issue = IssueWithAssignee(1, "bob");
        var production = AssignmentRoutingService.Evaluate(configuration, identity, issue);
        var plan = Plan(configuration, identity: identity, consideredIssues: new[] { issue });

        var explanation = RoutingExplanationService.Explain(plan, issue);

        var assignmentStage = Assert.Single(explanation.Stages, s => s.Name == "Assignment Routing");
        Assert.Equal(production.IsEligible, assignmentStage.Verdict is RoutingVerdict.Pass or RoutingVerdict.SoftPrefer or RoutingVerdict.SoftIneligible);
    }

    [Fact]
    public void Explanation_uses_same_worker_and_assignment_combined_as_production()
    {
        var configuration = WorkerAndAssignmentConfiguration("prefer", "allow");
        var identity = Identity("alice");
        var issue = WorkerIssue(1, "codex:worker:luna", "alice");
        var workerProduction = WorkerRoutingService.Evaluate(configuration, issue, "luna-model");
        var assignmentProduction = AssignmentRoutingService.Evaluate(configuration, identity, issue);
        var plan = Plan(configuration, currentModel: "luna-model", identity: identity, consideredIssues: new[] { issue });

        var explanation = RoutingExplanationService.Explain(plan, issue);

        var workerStage = Assert.Single(explanation.Stages, s => s.Name == "Worker Routing");
        var assignmentStage = Assert.Single(explanation.Stages, s => s.Name == "Assignment Routing");
        Assert.Equal(workerProduction.IsEligible, workerStage.Verdict is RoutingVerdict.Pass);
        Assert.Equal(assignmentProduction.SelectionRank, explanation.SelectionRank);
        Assert.Equal(explanation.IsEligible, workerProduction.IsEligible && (assignmentProduction.IsEligible || !assignmentProduction.IsEnabled));
    }

    [Fact]
    public void Explanation_all_returns_one_per_considered_issue()
    {
        var issues = new[] { ReadyIssue(1), ReadyIssue(2), ReadyIssue(3) };
        var plan = Plan(consideredIssues: issues);

        var explanations = RoutingExplanationService.ExplainAll(plan);

        Assert.Equal(3, explanations.Count);
        Assert.Equal(new[] { 1, 2, 3 }, explanations.Select(e => e.IssueNumber));
    }

    [Fact]
    public void Selected_issue_is_ordered_first_by_production_decision()
    {
        var selectedTask = new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 5, SelectionRank = 0 };
        var otherTask = new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 1, SelectionRank = 0 };
        var issues = new[] { ReadyIssue(1), ReadyIssue(5) };
        var plan = Plan(
            consideredIssues: issues,
            actionableTasks: new[] { otherTask, selectedTask },
            decision: new HookTaskDecision { SelectedTask = selectedTask, AdditionalContext = "issue 5" });

        var explanations = RoutingExplanationService.ExplainAll(plan);

        Assert.Equal(5, explanations[0].IssueNumber);
        Assert.True(explanations[0].IsSelected);
    }

    [Fact]
    public void Routing_outcome_prefers_priority_then_rank_ties_break_by_discovery()
    {
        var changeRequest = new WorkflowItem { Type = WorkflowItemType.ChangeRequest, IssueNumber = 2, PullRequestNumber = 20, SelectionRank = 0 };
        var newIssue = new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 3, SelectionRank = 0 };
        var issues = new[] { ReadyIssue(2, "codex:worker:luna"), ReadyIssue(3, "codex:worker:luna") };
        var plan = Plan(
            configuration: WorkerConfiguration(),
            currentModel: "luna-model",
            consideredIssues: issues,
            actionableTasks: new[] { changeRequest, newIssue },
            decision: new HookTaskDecision { SelectedTask = changeRequest, AdditionalContext = "change" });

        var rankTwoExplanation = RoutingExplanationService.Explain(plan, issues[0]);
        var newIssueExplanation = RoutingExplanationService.Explain(plan, issues[1]);

        var changeOutcome = Assert.Single(rankTwoExplanation.Stages, s => s.Name == "Routing Outcome");
        var newOutcome = Assert.Single(newIssueExplanation.Stages, s => s.Name == "Routing Outcome");
        Assert.Equal(RoutingVerdict.Selected, changeOutcome.Verdict);
        Assert.Equal(RoutingVerdict.SoftIneligible, newOutcome.Verdict);
        Assert.Contains("change-request", newOutcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Blocked_decision_reports_issue_level_blocking_task()
    {
        var issue = CompletedWithoutMergeIssue(1);
        var blockingTask = new WorkflowItem { Type = WorkflowItemType.ClosedWithoutMerge, IssueNumber = 1, Status = new WorkflowTaskStatus { Message = "Closed without merge." } };
        var plan = Plan(
            consideredIssues: new[] { issue },
            actionableTasks: new[] { blockingTask },
            blockReason: "Closed without merge.");

        var explanation = RoutingExplanationService.Explain(plan, issue);

        Assert.False(explanation.IsEligible);
        var outcomeStage = Assert.Single(explanation.Stages, s => s.Name == "Routing Outcome");
        Assert.Equal(RoutingVerdict.HardIneligible, outcomeStage.Verdict);
        Assert.Contains("Closed without merge", outcomeStage.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Selection_rank_orders_preferred_over_unassigned_over_other()
    {
        var configuration = AssignmentConfiguration("prefer", "allow");
        var identity = Identity("alice");

        var mine = RoutingExplanationService.Explain(Plan(configuration, identity: identity, consideredIssues: new[] { IssueWithAssignee(1, "alice") }), IssueWithAssignee(1, "alice"));
        var unassigned = RoutingExplanationService.Explain(Plan(configuration, identity: identity, consideredIssues: new[] { IssueWithAssignee(2) }), IssueWithAssignee(2));
        var other = RoutingExplanationService.Explain(Plan(configuration, identity: identity, consideredIssues: new[] { IssueWithAssignee(3, "bob") }), IssueWithAssignee(3, "bob"));

        Assert.True(mine.SelectionRank < unassigned.SelectionRank);
        Assert.True(unassigned.SelectionRank < other.SelectionRank);
    }

    [Fact]
    public void Format_single_explanation_includes_eligibility_and_stages()
    {
        var issue = ReadyIssue(1);
        var plan = Plan(consideredIssues: new[] { issue });
        var explanation = RoutingExplanationService.Explain(plan, issue);

        var output = RoutingExplanationService.FormatSingleExplanation(explanation);

        Assert.Contains("Issue #1", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Eligible: yes", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Workflow State", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Format_single_explanation_shows_ineligible_for_blocked_issue()
    {
        var issue = BlockedIssue(1);
        var plan = Plan(consideredIssues: new[] { issue });
        var explanation = RoutingExplanationService.Explain(plan, issue);

        var output = RoutingExplanationService.FormatSingleExplanation(explanation);

        Assert.Contains("Eligible: no", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BLOCKED", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Format_explanations_shows_all_issues()
    {
        var issues = new[] { ReadyIssue(1), ReadyIssue(2) };
        var plan = Plan(consideredIssues: issues);
        var explanations = RoutingExplanationService.ExplainAll(plan);
        var output = RoutingExplanationService.FormatExplanations(explanations);

        Assert.Contains("#1", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#2", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ELIGIBLE", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Format_explanations_marks_selected_issue()
    {
        var gatedIssue = GatedIssue(1);
        var gatedTask = new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = 1, Status = new WorkflowTaskStatus { Message = "gated" } };
        var other = ReadyIssue(2);
        var plan = Plan(
            consideredIssues: new[] { gatedIssue, other },
            repositoryGateTasks: new[] { gatedTask },
            actionableTasks: new[] { gatedTask },
            decision: new HookTaskDecision { SelectedTask = gatedTask, AdditionalContext = "gated" });

        var explanations = RoutingExplanationService.ExplainAll(plan);
        var output = RoutingExplanationService.FormatExplanations(explanations);

        Assert.Contains("#1", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SELECTED", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Summary_describes_eligible_issue_with_rank_zero()
    {
        var configuration = AssignmentConfiguration("prefer", "allow");
        var identity = Identity("alice");
        var issue = IssueWithAssignee(1, "alice");
        var plan = Plan(configuration, identity: identity, consideredIssues: new[] { issue });
        var explanation = RoutingExplanationService.Explain(plan, issue);

        Assert.Contains("highest-priority candidate", explanation.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Summary_describes_ineligible_issue_with_blocker_reason()
    {
        var issue = BlockedIssue(1);
        var plan = Plan(consideredIssues: new[] { issue });
        var explanation = RoutingExplanationService.Explain(plan, issue);

        Assert.Contains("ineligible", explanation.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Worker_and_assignment_routing_coordinate_for_eligibility()
    {
        var configuration = WorkerAndAssignmentConfiguration("require", "allow");
        var identity = Identity("alice");
        var matchingWorkerAndAssignment = WorkerIssue(1, "codex:worker:luna", "alice");
        var matchingWorkerOtherAssignment = WorkerIssue(2, "codex:worker:luna", "bob");
        var plan1 = Plan(configuration, currentModel: "luna-model", identity: identity, consideredIssues: new[] { matchingWorkerAndAssignment });
        var plan2 = Plan(configuration, currentModel: "luna-model", identity: identity, consideredIssues: new[] { matchingWorkerOtherAssignment });

        var explanation1 = RoutingExplanationService.Explain(plan1, matchingWorkerAndAssignment);
        var explanation2 = RoutingExplanationService.Explain(plan2, matchingWorkerOtherAssignment);

        Assert.True(explanation1.IsEligible);
        Assert.False(explanation2.IsEligible);
    }

    private static RoutingEvaluationResult Plan(
        RouterConfiguration? configuration = null,
        string? currentModel = null,
        AssignmentIdentity? identity = null,
        WorkClaim? claim = null,
        IReadOnlyList<Issue>? consideredIssues = null,
        IReadOnlyList<WorkflowItem>? repositoryGateTasks = null,
        IReadOnlyList<WorkflowItem>? actionableTasks = null,
        IReadOnlyList<WorkflowItem>? workflowTasks = null,
        HookTaskDecision? decision = null,
        string? blockReason = null) => new()
        {
            Configuration = configuration ?? new RouterConfiguration(),
            CurrentModel = currentModel,
            AssignmentIdentity = identity,
            ActiveClaim = claim,
            ConsideredIssues = consideredIssues ?? new List<Issue>(),
            RepositoryGateTasks = repositoryGateTasks ?? new List<WorkflowItem>(),
            ActionableTasks = actionableTasks ?? new List<WorkflowItem>(),
            WorkflowTasks = workflowTasks ?? (actionableTasks ?? new List<WorkflowItem>()),
            Decision = decision,
            BlockReason = blockReason
        };

    private static RouterConfiguration WorkerConfiguration() => new()
    {
        Policies = new RouterPolicies
        {
            WorkerRouting = new WorkerRoutingPolicy
            {
                DefaultWorker = "luna",
                Workers = new Dictionary<string, WorkerProfileConfiguration>(StringComparer.OrdinalIgnoreCase)
                {
                    ["luna"] = new() { Labels = new() { "codex:worker:luna" }, Models = new() { "luna-model" } },
                    ["terra"] = new() { Labels = new() { "codex:worker:terra" }, Models = new() { "terra-model" } }
                }
            }
        }
    };

    private static RouterConfiguration AssignmentConfiguration(string mode, string unassigned) => new()
    {
        Policies = new RouterPolicies
        {
            AssignmentRouting = new AssignmentRoutingPolicy
            {
                Mode = mode,
                Unassigned = unassigned
            }
        }
    };

    private static RouterConfiguration WorkerAndAssignmentConfiguration(string assignmentMode, string unassigned) => new()
    {
        Policies = new RouterPolicies
        {
            WorkerRouting = new WorkerRoutingPolicy
            {
                DefaultWorker = "luna",
                Workers = new Dictionary<string, WorkerProfileConfiguration>(StringComparer.OrdinalIgnoreCase)
                {
                    ["luna"] = new() { Labels = new() { "codex:worker:luna" }, Models = new() { "luna-model" } },
                    ["terra"] = new() { Labels = new() { "codex:worker:terra" }, Models = new() { "terra-model" } }
                }
            },
            AssignmentRouting = new AssignmentRoutingPolicy
            {
                Mode = assignmentMode,
                Unassigned = unassigned
            }
        }
    };

    private static AssignmentIdentity Identity(string name) => new()
    {
        Name = name,
        GitHubUsernames = new[] { name }
    };

    private static Issue ReadyIssue(int number, params string[] workerLabels) => new()
    {
        Number = number,
        Labels = new List<GithubLabel> { new() { Name = "codex:ready" } }
            .Concat(workerLabels.Select(label => new GithubLabel { Name = label }))
            .ToList()
    };

    private static Issue BlockedIssue(int number) => new()
    {
        Number = number,
        Labels = new List<GithubLabel> { new() { Name = "codex:blocked" } }
    };

    private static Issue CompletedWithoutMergeIssue(int number) => new()
    {
        Number = number,
        Labels = new List<GithubLabel> { new() { Name = "codex:completed" } }
    };

    private static Issue GatedIssue(int number) => new()
    {
        Number = number,
        Labels = new List<GithubLabel> { new() { Name = "codex:ready" }, new() { Name = "codex:gate" } }
    };

    private static Issue IssueWithAssignee(int number, params string[] assignees) => new()
    {
        Number = number,
        Labels = new List<GithubLabel> { new() { Name = "codex:ready" } },
        Assignees = assignees.Select(login => new GithubUser { Login = login }).ToList()
    };

    private static Issue WorkerIssue(int number, string workerLabel, params string[] assignees) => new()
    {
        Number = number,
        Labels = new List<GithubLabel> { new() { Name = "codex:ready" }, new() { Name = workerLabel } },
        Assignees = assignees.Select(login => new GithubUser { Login = login }).ToList()
    };
}
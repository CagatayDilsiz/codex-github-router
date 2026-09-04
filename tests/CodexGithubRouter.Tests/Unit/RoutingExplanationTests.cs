using CodexGithubRouter.GitHub;
using CodexGithubRouter.Work;
using CodexGithubRouter.Workflow;
using Xunit;

namespace CodexGithubRouter.Tests;

[Trait("Category", "Unit")]
public sealed class RoutingExplanationTests
{
    [Fact]
    public void Disabled_routing_passes_all_issues()
    {
        var configuration = new RouterConfiguration();
        var issue = ReadyIssue(1);

        var explanation = RoutingExplanationService.Explain(configuration, issue);

        Assert.True(explanation.IsEligible);
        Assert.Equal(int.MaxValue, explanation.SelectionRank);
        Assert.All(explanation.Stages, stage =>
            Assert.True(stage.Verdict is RoutingVerdict.Pass or RoutingVerdict.Disabled));
    }

    [Fact]
    public void Workflow_state_ready_is_eligible()
    {
        var configuration = new RouterConfiguration();
        var issue = ReadyIssue(1);

        var explanation = RoutingExplanationService.Explain(configuration, issue);

        var workflowStage = Assert.Single(explanation.Stages, s => s.Name == "Workflow State");
        Assert.Equal(RoutingVerdict.Pass, workflowStage.Verdict);
        Assert.Contains("Ready", workflowStage.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Workflow_state_blocked_is_hard_ineligible()
    {
        var configuration = new RouterConfiguration();
        var issue = BlockedIssue(1);

        var explanation = RoutingExplanationService.Explain(configuration, issue);

        Assert.False(explanation.IsEligible);
        var workflowStage = Assert.Single(explanation.Stages, s => s.Name == "Workflow State");
        Assert.Equal(RoutingVerdict.HardIneligible, workflowStage.Verdict);
        Assert.Contains("Blocked", workflowStage.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_workflow_labels_is_hard_ineligible()
    {
        var configuration = new RouterConfiguration();
        var issue = new Issue { Number = 1, Labels = new List<GithubLabel>() };

        var explanation = RoutingExplanationService.Explain(configuration, issue);

        Assert.False(explanation.IsEligible);
        var workflowStage = Assert.Single(explanation.Stages, s => s.Name == "Workflow State");
        Assert.Equal(RoutingVerdict.HardIneligible, workflowStage.Verdict);
        Assert.Contains("no recognized workflow labels", workflowStage.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Worker_routing_disabled_stage_is_disabled()
    {
        var configuration = new RouterConfiguration();
        var issue = ReadyIssue(1);

        var explanation = RoutingExplanationService.Explain(configuration, issue);

        var workerStage = Assert.Single(explanation.Stages, s => s.Name == "Worker Routing");
        Assert.Equal(RoutingVerdict.Disabled, workerStage.Verdict);
    }

    [Fact]
    public void Worker_routing_matching_worker_is_pass()
    {
        var configuration = WorkerConfiguration();
        var issue = ReadyIssue(1, "codex:worker:luna");

        var explanation = RoutingExplanationService.Explain(configuration, issue, "luna-model");

        var workerStage = Assert.Single(explanation.Stages, s => s.Name == "Worker Routing");
        Assert.Equal(RoutingVerdict.Pass, workerStage.Verdict);
        Assert.Contains("luna", workerStage.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Worker_routing_mismatched_worker_is_hard_ineligible()
    {
        var configuration = WorkerConfiguration();
        var issue = ReadyIssue(1, "codex:worker:luna");

        var explanation = RoutingExplanationService.Explain(configuration, issue, "terra-model");

        Assert.False(explanation.IsEligible);
        var workerStage = Assert.Single(explanation.Stages, s => s.Name == "Worker Routing");
        Assert.Equal(RoutingVerdict.HardIneligible, workerStage.Verdict);
    }

    [Fact]
    public void Assignment_routing_disabled_stage_is_disabled()
    {
        var configuration = new RouterConfiguration();
        var issue = ReadyIssue(1);

        var explanation = RoutingExplanationService.Explain(configuration, issue);

        var assignmentStage = Assert.Single(explanation.Stages, s => s.Name == "Assignment Routing");
        Assert.Equal(RoutingVerdict.Disabled, assignmentStage.Verdict);
    }

    [Fact]
    public void Assignment_routing_assigned_to_current_identity_is_soft_prefer()
    {
        var configuration = AssignmentConfiguration("prefer", "allow");
        var identity = Identity("alice");

        var explanation = RoutingExplanationService.Explain(configuration, IssueWithAssignee(1, "alice"), null, identity);

        var assignmentStage = Assert.Single(explanation.Stages, s => s.Name == "Assignment Routing");
        Assert.Equal(RoutingVerdict.SoftPrefer, assignmentStage.Verdict);
        Assert.Equal(0, explanation.SelectionRank);
    }

    [Fact]
    public void Assignment_routing_unassigned_is_soft_ineligible_rank_1()
    {
        var configuration = AssignmentConfiguration("prefer", "allow");
        var identity = Identity("alice");

        var explanation = RoutingExplanationService.Explain(configuration, IssueWithAssignee(1), null, identity);

        var assignmentStage = Assert.Single(explanation.Stages, s => s.Name == "Assignment Routing");
        Assert.Equal(RoutingVerdict.SoftIneligible, assignmentStage.Verdict);
        Assert.Equal(1, explanation.SelectionRank);
    }

    [Fact]
    public void Assignment_routing_other_developer_is_soft_ineligible_rank_2()
    {
        var configuration = AssignmentConfiguration("prefer", "allow");
        var identity = Identity("alice");

        var explanation = RoutingExplanationService.Explain(configuration, IssueWithAssignee(1, "bob"), null, identity);

        var assignmentStage = Assert.Single(explanation.Stages, s => s.Name == "Assignment Routing");
        Assert.Equal(RoutingVerdict.SoftIneligible, assignmentStage.Verdict);
        Assert.Equal(2, explanation.SelectionRank);
    }

    [Fact]
    public void Assignment_routing_require_mode_other_developer_is_hard_ineligible()
    {
        var configuration = AssignmentConfiguration("require", "allow");
        var identity = Identity("alice");

        var explanation = RoutingExplanationService.Explain(configuration, IssueWithAssignee(1, "bob"), null, identity);

        Assert.False(explanation.IsEligible);
        var assignmentStage = Assert.Single(explanation.Stages, s => s.Name == "Assignment Routing");
        Assert.Equal(RoutingVerdict.HardIneligible, assignmentStage.Verdict);
    }

    [Fact]
    public void Repository_gate_not_configured_passes_all_issues()
    {
        var configuration = new RouterConfiguration
        {
            Policies = new RouterPolicies
            {
                RepositoryGate = new RepositoryGatePolicy { Labels = new List<string>() }
            }
        };
        var issue = ReadyIssue(1);

        var explanation = RoutingExplanationService.Explain(configuration, issue);

        var gateStage = Assert.Single(explanation.Stages, s => s.Name == "Repository Gate");
        Assert.Equal(RoutingVerdict.Disabled, gateStage.Verdict);
    }

    [Fact]
    public void Repository_gate_active_issue_is_hard_ineligible()
    {
        var configuration = new RouterConfiguration();
        var issue = GatedIssue(1);

        var explanation = RoutingExplanationService.Explain(configuration, issue);

        Assert.False(explanation.IsEligible);
        var gateStage = Assert.Single(explanation.Stages, s => s.Name == "Repository Gate");
        Assert.Equal(RoutingVerdict.HardIneligible, gateStage.Verdict);
        Assert.Contains("gate", gateStage.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Repository_gate_inactive_issue_is_pass()
    {
        var configuration = new RouterConfiguration();
        var issue = ReadyIssue(1);

        var explanation = RoutingExplanationService.Explain(configuration, issue);

        var gateStage = Assert.Single(explanation.Stages, s => s.Name == "Repository Gate");
        Assert.Equal(RoutingVerdict.Pass, gateStage.Verdict);
    }

    [Fact]
    public void No_active_claim_stage_is_pass()
    {
        var configuration = new RouterConfiguration();
        var issue = ReadyIssue(1);

        var explanation = RoutingExplanationService.Explain(configuration, issue);

        var claimStage = Assert.Single(explanation.Stages, s => s.Name == "Work Claim");
        Assert.Equal(RoutingVerdict.Pass, claimStage.Verdict);
        Assert.Contains("No active work claim", claimStage.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Active_claim_for_same_issue_is_soft_prefer()
    {
        var configuration = new RouterConfiguration();
        var issue = ReadyIssue(1);
        var claim = new WorkClaim { OwnerSessionId = "owner", IssueNumber = 1, WorkType = WorkClaimType.Implementation };

        var explanation = RoutingExplanationService.Explain(configuration, issue, null, null, claim);

        var claimStage = Assert.Single(explanation.Stages, s => s.Name == "Work Claim");
        Assert.Equal(RoutingVerdict.SoftPrefer, claimStage.Verdict);
        Assert.Contains("Active work claim exists", claimStage.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Active_claim_for_different_issue_marks_claim_stage_as_hard_ineligible()
    {
        var configuration = new RouterConfiguration();
        var issue = ReadyIssue(2);
        var claim = new WorkClaim { OwnerSessionId = "owner", IssueNumber = 1, WorkType = WorkClaimType.Implementation };

        var explanation = RoutingExplanationService.Explain(configuration, issue, null, null, claim);

        var claimStage = Assert.Single(explanation.Stages, s => s.Name == "Work Claim");
        Assert.Equal(RoutingVerdict.HardIneligible, claimStage.Verdict);
        Assert.Contains("Active work claim is held", claimStage.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Explanation_uses_same_worker_routing_evaluation_as_production()
    {
        var configuration = WorkerConfiguration();
        var issue = ReadyIssue(1, "codex:worker:luna");
        var production = WorkerRoutingService.Evaluate(configuration, issue, "terra-model");
        var explanation = RoutingExplanationService.Explain(configuration, issue, "terra-model");

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
        var explanation = RoutingExplanationService.Explain(configuration, issue, null, identity);

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
        var explanation = RoutingExplanationService.Explain(configuration, issue, "luna-model", identity);

        var workerStage = Assert.Single(explanation.Stages, s => s.Name == "Worker Routing");
        var assignmentStage = Assert.Single(explanation.Stages, s => s.Name == "Assignment Routing");
        Assert.Equal(workerProduction.IsEligible, workerStage.Verdict is RoutingVerdict.Pass);
        Assert.Equal(assignmentProduction.SelectionRank, explanation.SelectionRank);
        Assert.Equal(explanation.IsEligible, workerProduction.IsEligible && (assignmentProduction.IsEligible || !assignmentProduction.IsEnabled));
    }

    [Fact]
    public void Explanation_all_returns_one_per_issue()
    {
        var configuration = new RouterConfiguration();
        var issues = new[] { ReadyIssue(1), ReadyIssue(2), ReadyIssue(3) };

        var explanations = RoutingExplanationService.ExplainAll(configuration, issues);

        Assert.Equal(3, explanations.Count);
        Assert.Equal(new[] { 1, 2, 3 }, explanations.Select(e => e.IssueNumber));
    }

    [Fact]
    public void Selection_rank_orders_preferred_over_unassigned_over_other()
    {
        var configuration = AssignmentConfiguration("prefer", "allow");
        var identity = Identity("alice");

        var mine = RoutingExplanationService.Explain(configuration, IssueWithAssignee(1, "alice"), null, identity);
        var unassigned = RoutingExplanationService.Explain(configuration, IssueWithAssignee(2), null, identity);
        var other = RoutingExplanationService.Explain(configuration, IssueWithAssignee(3, "bob"), null, identity);

        Assert.True(mine.SelectionRank < unassigned.SelectionRank);
        Assert.True(unassigned.SelectionRank < other.SelectionRank);
    }

    [Fact]
    public void Format_single_explanation_includes_eligibility_and_stages()
    {
        var configuration = new RouterConfiguration();
        var issue = ReadyIssue(1);
        var explanation = RoutingExplanationService.Explain(configuration, issue);

        var output = RoutingExplanationService.FormatSingleExplanation(explanation);

        Assert.Contains("Issue #1", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Eligible: yes", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Workflow State", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Format_single_explanation_shows_ineligible_for_blocked_issue()
    {
        var configuration = new RouterConfiguration();
        var issue = BlockedIssue(1);
        var explanation = RoutingExplanationService.Explain(configuration, issue);

        var output = RoutingExplanationService.FormatSingleExplanation(explanation);

        Assert.Contains("Eligible: no", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BLOCKED", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Format_explanations_shows_all_issues()
    {
        var configuration = new RouterConfiguration();
        var issues = new[] { ReadyIssue(1), ReadyIssue(2) };

        var explanations = RoutingExplanationService.ExplainAll(configuration, issues);
        var output = RoutingExplanationService.FormatExplanations(explanations);

        Assert.Contains("#1", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#2", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ELIGIBLE", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Summary_describes_eligible_issue_with_rank_zero()
    {
        var configuration = AssignmentConfiguration("prefer", "allow");
        var identity = Identity("alice");
        var explanation = RoutingExplanationService.Explain(configuration, IssueWithAssignee(1, "alice"), null, identity);

        Assert.Contains("highest-priority candidate", explanation.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Summary_describes_ineligible_issue_with_blocker_reason()
    {
        var configuration = new RouterConfiguration();
        var issue = BlockedIssue(1);
        var explanation = RoutingExplanationService.Explain(configuration, issue);

        Assert.Contains("ineligible", explanation.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Worker_and_assignment_routing_coordinate_for_eligibility()
    {
        var configuration = WorkerAndAssignmentConfiguration("require", "allow");
        var identity = Identity("alice");
        var matchingWorkerAndAssignment = WorkerIssue(1, "codex:worker:luna", "alice");
        var matchingWorkerOtherAssignment = WorkerIssue(2, "codex:worker:luna", "bob");

        var explanation1 = RoutingExplanationService.Explain(configuration, matchingWorkerAndAssignment, "luna-model", identity);
        var explanation2 = RoutingExplanationService.Explain(configuration, matchingWorkerOtherAssignment, "luna-model", identity);

        Assert.True(explanation1.IsEligible);
        Assert.False(explanation2.IsEligible);
    }

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

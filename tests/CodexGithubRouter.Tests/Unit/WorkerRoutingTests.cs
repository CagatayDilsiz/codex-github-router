using CodexGithubRouter.GitHub;
using CodexGithubRouter.Work;
using CodexGithubRouter.Workflow;
using Xunit;

namespace CodexGithubRouter.Tests;

[Trait("Category", "Unit")]
public sealed class WorkerRoutingTests
{
    [Fact]
    public void Routing_is_disabled_when_no_policy_is_configured()
    {
        var issue = ReadyIssue(1, "codex:worker:luna");

        var result = WorkerRoutingService.Evaluate(new RouterConfiguration(), issue, "terra-model");

        Assert.False(result.IsEnabled);
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void Matching_worker_is_eligible_and_mismatched_worker_is_filtered()
    {
        var configuration = WorkerConfiguration();
        var issues = new[] { ReadyIssue(1, "codex:worker:luna"), ReadyIssue(2, "codex:worker:terra") };

        var result = WorkerRoutingService.FilterIssues(configuration, issues, "terra-model");

        Assert.Single(result.EligibleIssues);
        Assert.Equal(2, result.EligibleIssues[0].Number);
        Assert.Single(result.IneligibleIssues);
        Assert.Contains("luna", result.IneligibleIssues[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unlabeled_work_uses_the_configured_default_worker()
    {
        var configuration = WorkerConfiguration();

        var luna = WorkerRoutingService.Evaluate(configuration, ReadyIssue(1), "luna-model");
        var terra = WorkerRoutingService.Evaluate(configuration, ReadyIssue(2), "terra-model");

        Assert.True(luna.IsEligible);
        Assert.Equal("luna", luna.WorkerProfile);
        Assert.False(terra.IsEligible);
        Assert.Equal("luna", terra.WorkerProfile);
    }

    [Fact]
    public void Unknown_and_conflicting_worker_labels_fail_safely()
    {
        var configuration = WorkerConfiguration();

        var unknown = WorkerRoutingService.Evaluate(configuration, ReadyIssue(1, "codex:worker:sol"), "terra-model");
        var conflicting = WorkerRoutingService.Evaluate(configuration, ReadyIssue(2, "codex:worker:luna", "codex:worker:terra"), "terra-model");

        Assert.False(unknown.IsEligible);
        Assert.Contains("unknown worker label", unknown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(conflicting.IsEligible);
        Assert.Contains("conflicting worker labels", conflicting.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ineligible_higher_priority_work_is_skipped_for_an_eligible_candidate()
    {
        var configuration = WorkerConfiguration();
        var result = await WorkflowService.EvaluateInProgressIssuesAsync(
            configuration,
            new[] { WorkingIssue(1, "codex:worker:luna"), WorkingIssue(2, "codex:worker:terra") },
            _ => throw new InvalidOperationException("No pull request should be requested."),
            "terra-model");

        Assert.True(result.IsSuccessful);
        Assert.Equal(WorkflowItemType.ResumeInProgressIssue, result.Tasks.Single().Type);
        Assert.Equal(2, result.Tasks.Single().IssueNumber);
    }

    [Fact]
    public async Task Candidate_discovery_expands_when_the_initial_window_has_no_eligible_work()
    {
        var configuration = WorkerConfiguration();
        var calls = new List<int>();
        var result = await WorkerRoutingService.DiscoverCandidatesAsync(
            configuration,
            2,
            "terra-model",
            async limit =>
            {
                calls.Add(limit);
                await Task.Yield();
                return limit == 2
                    ? new[] { ReadyIssue(1, "codex:worker:luna"), ReadyIssue(2, "codex:worker:luna") }
                    : new[] { ReadyIssue(1, "codex:worker:luna"), ReadyIssue(2, "codex:worker:luna"), ReadyIssue(3, "codex:worker:terra") };
            });

        Assert.Equal(new[] { 2, 4 }, calls);
        Assert.Contains(result.Issues, issue => issue.Number == 3);
        Assert.Equal(3, WorkerRoutingService.FilterIssues(configuration, result.Issues, "terra-model").EligibleIssues.Single().Number);
    }

    [Fact]
    public void No_eligible_work_explains_model_and_pending_workers()
    {
        var configuration = WorkerConfiguration();
        var result = WorkerRoutingService.FilterIssues(configuration, new[] { ReadyIssue(1, "codex:worker:luna") }, "terra-model");

        Assert.True(result.NoEligibleWork);
        Assert.Contains("Current model: terra-model", result.Message, StringComparison.Ordinal);
        Assert.Contains("Pending work exists for: luna", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void No_eligible_diagnostics_can_aggregate_multiple_priority_groups()
    {
        var configuration = WorkerConfiguration();
        var completed = WorkerRoutingService.Evaluate(configuration, ReadyIssue(1, "codex:worker:luna"), "terra-model");
        var interrupted = WorkerRoutingService.Evaluate(configuration, ReadyIssue(2, "codex:worker:luna"), "terra-model");

        var message = WorkerRoutingService.FormatNoEligibleWorkMessage("terra-model", new[] { completed, interrupted });

        Assert.Contains("Pending work exists for: luna", message, StringComparison.Ordinal);
        Assert.Equal(2, message.Split("Issue #", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task An_incompatible_model_cannot_continue_an_active_worker_claim()
    {
        var configuration = WorkerConfiguration();
        var claim = new WorkClaim
        {
            ClaimId = Guid.NewGuid(),
            Version = 1,
            OwnerSessionId = "session-a",
            IssueNumber = 1,
            WorkType = WorkClaimType.Implementation,
            WorkerProfile = "luna",
            Model = "luna-model",
            ClaimedAt = DateTimeOffset.UtcNow,
            LastUpdatedAt = DateTimeOffset.UtcNow
        };

        var result = await WorkflowService.EvaluateClaimedWorkAsync(
            configuration,
            claim,
            ReadyIssue(1, "codex:worker:luna"),
            _ => throw new InvalidOperationException("No pull request should be requested."),
            "terra-model");

        Assert.False(result.IsSuccessful);
        Assert.Contains("luna", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("terra", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Worker_labels_are_preserved_by_issue_state_transitions()
    {
        var configuration = WorkerConfiguration();
        var issue = ReadyIssue(1, "codex:worker:terra");

        var transition = IssueTransitionPlanner.Plan(issue, WorkflowState.Completed, configuration);

        Assert.DoesNotContain("codex:worker:terra", transition.LabelsToRemove, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Worker_configuration_requires_a_default_and_unique_model_assignments()
    {
        var missingDefault = new RouterConfiguration
        {
            Policies = new RouterPolicies
            {
                WorkerRouting = new WorkerRoutingPolicy
                {
                    DefaultWorker = string.Empty,
                    Workers = new Dictionary<string, WorkerProfileConfiguration>
                    {
                        ["luna"] = new() { Labels = new() { "codex:worker:luna" }, Models = new() { "luna-model" } }
                    }
                }
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(() => WorkerRoutingService.Validate(missingDefault));
        Assert.Contains("default worker", exception.Message, StringComparison.OrdinalIgnoreCase);

        var duplicateModel = WorkerConfiguration();
        duplicateModel.Policies.WorkerRouting!.Workers["terra"].Models.Add("luna-model");

        exception = Assert.Throws<InvalidOperationException>(() => WorkerRoutingService.Validate(duplicateModel));
        Assert.Contains("assigned to multiple worker profiles", exception.Message, StringComparison.OrdinalIgnoreCase);

        var whitespaceName = WorkerConfiguration();
        whitespaceName.Policies.WorkerRouting!.Workers[" luna "] = whitespaceName.Policies.WorkerRouting.Workers["luna"];
        whitespaceName.Policies.WorkerRouting.Workers.Remove("luna");
        exception = Assert.Throws<InvalidOperationException>(() => WorkerRoutingService.Validate(whitespaceName));
        Assert.Contains("leading or trailing whitespace", exception.Message, StringComparison.OrdinalIgnoreCase);

        var customLabel = WorkerConfiguration();
        customLabel.Policies.WorkerRouting!.Workers["luna"].Labels[0] = "team:luna";
        exception = Assert.Throws<InvalidOperationException>(() => WorkerRoutingService.Validate(customLabel));
        Assert.Contains("namespace", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Merged_cleanup_remains_model_independent_for_another_worker()
    {
        var configuration = WorkerConfiguration();
        var issue = new Issue
        {
            Number = 1,
            Labels = new List<GithubLabel> { new() { Name = "codex:done" }, new() { Name = "codex:worker:luna" } },
            ClosingPullRequestsReferences = new List<ClosingIssueReference> { new() { Number = 50 } }
        };

        var response = await WorkflowService.CheckIssueLinkedPullRequestsAsync(
            configuration,
            new[] { issue },
            _ => Task.FromResult(new PullRequest { Number = 50, State = "merged" }));

        Assert.Equal(WorkflowItemType.CloseIssue, response.Tasks.Single().Type);
    }

    [Fact]
    public async Task Pull_request_with_conflicting_closing_workers_is_blocked()
    {
        var configuration = WorkerConfiguration();
        var issue = ReadyIssue(1, "codex:worker:luna");
        issue.ClosingPullRequestsReferences.Add(new ClosingIssueReference { Number = 50 });
        var otherIssue = ReadyIssue(2, "codex:worker:terra");

        var response = await WorkflowService.CheckIssueLinkedPullRequestsAsync(
            configuration,
            new[] { issue },
            _ => Task.FromResult(new PullRequest
            {
                Number = 50,
                State = "open",
                Labels = new List<GithubLabel> { new() { Name = "codex:cr" } },
                ClosingIssuesReferences = new List<ClosingIssueReference>
                {
                    new() { Number = 1 },
                    new() { Number = 2 }
                }
            }),
            number => Task.FromResult(number == 2 ? otherIssue : issue));

        Assert.Equal(WorkflowItemType.UnknownPullRequestState, response.Tasks.Single().Type);
        Assert.Contains("conflicting workers", response.Tasks.Single().Status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Passive_pull_request_with_conflicting_closing_workers_remains_passive()
    {
        var configuration = WorkerConfiguration();
        var issue = ReadyIssue(1, "codex:worker:luna");
        issue.ClosingPullRequestsReferences.Add(new ClosingIssueReference { Number = 50 });
        var otherIssue = ReadyIssue(2, "codex:worker:terra");

        var response = await WorkflowService.CheckIssueLinkedPullRequestsAsync(
            configuration,
            new[] { issue },
            _ => Task.FromResult(new PullRequest
            {
                Number = 50,
                State = "open",
                Labels = new List<GithubLabel> { new() { Name = "codex:rr" } },
                ClosingIssuesReferences = new List<ClosingIssueReference>
                {
                    new() { Number = 1 },
                    new() { Number = 2 }
                }
            }),
            number => Task.FromResult(number == 2 ? otherIssue : issue));

        Assert.Equal(WorkflowItemType.AwaitingReview, response.Tasks.Single().Type);
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

    private static Issue ReadyIssue(int number, params string[] workerLabels) => new()
    {
        Number = number,
        Labels = new List<GithubLabel> { new() { Name = "codex:ready" } }
            .Concat(workerLabels.Select(label => new GithubLabel { Name = label }))
            .ToList()
    };

    private static Issue WorkingIssue(int number, params string[] workerLabels) => new()
    {
        Number = number,
        Labels = new List<GithubLabel> { new() { Name = "codex:working" } }
            .Concat(workerLabels.Select(label => new GithubLabel { Name = label }))
            .ToList()
    };
}

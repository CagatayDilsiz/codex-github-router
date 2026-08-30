using CodexGithubRouter.GitHub;
using CodexGithubRouter.Hooks;
using CodexGithubRouter.Workflow;
using Xunit;

namespace CodexGithubRouter.Tests;

[Trait("Category", "Unit")]
public sealed class AssignmentRoutingTests
{
    [Fact]
    public void Routing_is_disabled_when_no_policy_is_configured()
    {
        var identity = Identity("alice");
        var result = AssignmentRoutingService.Evaluate(new RouterConfiguration(), identity, IssueWithAssignee(1, "alice"));

        Assert.False(result.IsEnabled);
        Assert.True(result.IsEligible);
    }

    [Fact]
    public void Ignore_allow_keeps_every_issue_eligible_at_rank_zero()
    {
        var configuration = AssignmentConfiguration("ignore", "allow");
        var identity = Identity("alice");

        var assigned = AssignmentRoutingService.Evaluate(configuration, identity, IssueWithAssignee(1, "alice"));
        var unassigned = AssignmentRoutingService.Evaluate(configuration, identity, IssueWithAssignee(2));
        var other = AssignmentRoutingService.Evaluate(configuration, identity, IssueWithAssignee(3, "bob"));

        Assert.True(assigned.IsEligible);
        Assert.True(unassigned.IsEligible);
        Assert.True(other.IsEligible);
        Assert.Equal(0, assigned.SelectionRank);
        Assert.Equal(0, unassigned.SelectionRank);
        Assert.Equal(0, other.SelectionRank);
    }

    [Fact]
    public void Ignore_mode_ignores_all_assignment_state()
    {
        var configuration = AssignmentConfiguration("ignore", "exclude");
        var identity = Identity("alice");

        var assigned = AssignmentRoutingService.Evaluate(configuration, identity, IssueWithAssignee(1, "bob"));
        var unassigned = AssignmentRoutingService.Evaluate(configuration, identity, IssueWithAssignee(2));

        Assert.True(assigned.IsEligible);
        Assert.True(unassigned.IsEligible);
        Assert.Equal(0, assigned.SelectionRank);
        Assert.Equal(0, unassigned.SelectionRank);
    }

    [Fact]
    public void Prefer_allow_ranks_assigned_unassigned_then_other()
    {
        var configuration = AssignmentConfiguration("prefer", "allow");
        var identity = Identity("alice");

        var mine = AssignmentRoutingService.Evaluate(configuration, identity, IssueWithAssignee(1, "alice"));
        var unassigned = AssignmentRoutingService.Evaluate(configuration, identity, IssueWithAssignee(2));
        var other = AssignmentRoutingService.Evaluate(configuration, identity, IssueWithAssignee(3, "bob"));

        Assert.True(mine.IsEligible);
        Assert.True(unassigned.IsEligible);
        Assert.True(other.IsEligible);
        Assert.Equal(0, mine.SelectionRank);
        Assert.Equal(1, unassigned.SelectionRank);
        Assert.Equal(2, other.SelectionRank);
        Assert.True(mine.AssignedToCurrentIdentity);
        Assert.False(unassigned.AssignedToCurrentIdentity);
        Assert.False(other.AssignedToCurrentIdentity);
    }

    [Fact]
    public void Prefer_exclude_filters_unassigned_and_ranks_other_last()
    {
        var configuration = AssignmentConfiguration("prefer", "exclude");
        var identity = Identity("alice");

        var mine = AssignmentRoutingService.Evaluate(configuration, identity, IssueWithAssignee(1, "alice"));
        var unassigned = AssignmentRoutingService.Evaluate(configuration, identity, IssueWithAssignee(2));
        var other = AssignmentRoutingService.Evaluate(configuration, identity, IssueWithAssignee(3, "bob"));

        Assert.True(mine.IsEligible);
        Assert.False(unassigned.IsEligible);
        Assert.True(other.IsEligible);
        Assert.Equal(0, mine.SelectionRank);
        Assert.Equal(2, other.SelectionRank);
    }

    [Fact]
    public void Require_allow_filters_other_developers_work()
    {
        var configuration = AssignmentConfiguration("require", "allow");
        var identity = Identity("alice");

        var mine = AssignmentRoutingService.Evaluate(configuration, identity, IssueWithAssignee(1, "alice"));
        var unassigned = AssignmentRoutingService.Evaluate(configuration, identity, IssueWithAssignee(2));
        var other = AssignmentRoutingService.Evaluate(configuration, identity, IssueWithAssignee(3, "bob"));

        Assert.True(mine.IsEligible);
        Assert.True(unassigned.IsEligible);
        Assert.False(other.IsEligible);
        Assert.Contains("bob", other.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("requires the current identity", other.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, mine.SelectionRank);
        Assert.Equal(1, unassigned.SelectionRank);
    }

    [Fact]
    public void Require_exclude_only_keeps_work_assigned_to_the_current_identity()
    {
        var configuration = AssignmentConfiguration("require", "exclude");
        var identity = Identity("alice");

        var mine = AssignmentRoutingService.Evaluate(configuration, identity, IssueWithAssignee(1, "alice"));
        var unassigned = AssignmentRoutingService.Evaluate(configuration, identity, IssueWithAssignee(2));
        var other = AssignmentRoutingService.Evaluate(configuration, identity, IssueWithAssignee(3, "bob"));

        Assert.True(mine.IsEligible);
        Assert.False(unassigned.IsEligible);
        Assert.False(other.IsEligible);
    }

    [Fact]
    public void A_matching_assignee_from_any_identity_username_is_eligible()
    {
        var configuration = AssignmentConfiguration("require", "exclude");
        var identity = new AssignmentIdentity
        {
            Name = "alice",
            GitHubUsernames = new[] { "alice-mac", "alice-laptop" }
        };

        var first = AssignmentRoutingService.Evaluate(configuration, identity, IssueWithAssignee(1, "alice-mac"));
        var second = AssignmentRoutingService.Evaluate(configuration, identity, IssueWithAssignee(2, "ALICE-LAPTOP"));
        var other = AssignmentRoutingService.Evaluate(configuration, identity, IssueWithAssignee(3, "bob"));

        Assert.True(first.IsEligible);
        Assert.True(second.IsEligible);
        Assert.False(other.IsEligible);
    }

    [Fact]
    public void Ambiguous_identity_fails_closed_for_prefer_and_require()
    {
        var prefer = AssignmentConfiguration("prefer", "allow");
        var require = AssignmentConfiguration("require", "allow");

        var preferResult = AssignmentRoutingService.Evaluate(prefer, null, IssueWithAssignee(1, "bob"));
        var requireResult = AssignmentRoutingService.Evaluate(require, null, IssueWithAssignee(2));

        Assert.False(preferResult.IsEligible);
        Assert.False(requireResult.IsEligible);
        Assert.Contains("could not be resolved", preferResult.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not be resolved", requireResult.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_uses_the_git_config_identity_usernames()
    {
        var configuration = AssignmentConfiguration("prefer", "allow");

        var resolution = AssignmentRoutingService.Resolve(configuration, new[] { "alice-mac", "alice-laptop" });

        Assert.True(resolution.IsResolved);
        Assert.Equal("alice-laptop", resolution.Identity!.Name);
        Assert.Equal(new[] { "alice-laptop", "alice-mac" }, resolution.Identity.GitHubUsernames);
    }

    [Fact]
    public void Resolve_falls_back_to_the_authenticated_github_login()
    {
        var configuration = AssignmentConfiguration("prefer", "allow");

        var resolution = AssignmentRoutingService.Resolve(configuration, new[] { "authenticated-dev" });

        Assert.True(resolution.IsResolved);
        Assert.Equal("authenticated-dev", resolution.Identity!.Name);
        Assert.Equal(new[] { "authenticated-dev" }, resolution.Identity.GitHubUsernames);
    }

    [Fact]
    public void Parse_identity_usernames_splits_and_normalizes_comma_separated_values()
    {
        Assert.Equal(new[] { "ALICE-LAPTOP", "alice-mac" }, AssignmentRoutingService.ParseIdentityUsernames(" alice-mac , ALICE-LAPTOP, alice-mac "));
        Assert.Empty(AssignmentRoutingService.ParseIdentityUsernames(null));
        Assert.Empty(AssignmentRoutingService.ParseIdentityUsernames("  "));
        Assert.Empty(AssignmentRoutingService.ParseIdentityUsernames(","));
    }

    [Fact]
    public void Resolve_fails_closed_when_no_identity_is_available()
    {
        var configuration = AssignmentConfiguration("prefer", "allow");

        var resolution = AssignmentRoutingService.Resolve(configuration, Array.Empty<string>());

        Assert.True(resolution.IsEnabled);
        Assert.False(resolution.IsResolved);
        Assert.Contains("could not be resolved", resolution.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_is_not_enabled_for_ignore_mode()
    {
        var configuration = AssignmentConfiguration("ignore", "allow");

        var resolution = AssignmentRoutingService.Resolve(configuration, new[] { "authenticated-dev" });

        Assert.False(resolution.IsEnabled);
    }

    [Fact]
    public async Task Hook_identity_resolution_prefers_git_config_over_authenticated_login()
    {
        var configuration = AssignmentConfiguration("prefer", "allow");
        var dependencies = new HookExecutionDependencies
        {
            ResolveLocalIdentityAsync = (_, _) => Task.FromResult<string?>("alice-mac, ALICE-LAPTOP"),
            ResolveAuthenticatedGitHubLoginAsync = (_, _) => Task.FromResult<string?>("unrelated-login")
        };

        var resolution = await HookService.ResolveAssignmentIdentityAsync(configuration, "working-directory", dependencies, CancellationToken.None);

        Assert.True(resolution.IsResolved);
        Assert.Equal(new[] { "ALICE-LAPTOP", "alice-mac" }, resolution.Identity!.GitHubUsernames);
        Assert.DoesNotContain("unrelated-login", resolution.Identity.GitHubUsernames);
    }

    [Fact]
    public async Task Hook_identity_resolution_falls_back_to_authenticated_login()
    {
        var configuration = AssignmentConfiguration("prefer", "allow");
        var dependencies = new HookExecutionDependencies
        {
            ResolveLocalIdentityAsync = (_, _) => Task.FromResult<string?>(null),
            ResolveAuthenticatedGitHubLoginAsync = (_, _) => Task.FromResult<string?>("authenticated-dev")
        };

        var resolution = await HookService.ResolveAssignmentIdentityAsync(configuration, "working-directory", dependencies, CancellationToken.None);

        Assert.True(resolution.IsResolved);
        Assert.Equal(new[] { "authenticated-dev" }, resolution.Identity!.GitHubUsernames);
    }

    [Fact]
    public async Task Hook_identity_resolution_fails_closed_after_authenticated_login_failure()
    {
        var configuration = AssignmentConfiguration("prefer", "allow");
        var dependencies = new HookExecutionDependencies
        {
            ResolveLocalIdentityAsync = (_, _) => Task.FromResult<string?>(null),
            ResolveAuthenticatedGitHubLoginAsync = (_, _) => throw new InvalidOperationException("gh is not available")
        };

        var resolution = await HookService.ResolveAssignmentIdentityAsync(configuration, "working-directory", dependencies, CancellationToken.None);

        Assert.True(resolution.IsEnabled);
        Assert.False(resolution.IsResolved);
        Assert.Contains("could not be resolved", resolution.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Configuration_validation_rejects_invalid_settings()
    {
        Assert.Contains("mode", Assert.Throws<InvalidOperationException>(() => AssignmentRoutingService.Validate(new RouterConfiguration
        {
            Policies = new RouterPolicies { AssignmentRouting = new AssignmentRoutingPolicy { Mode = "always" } }
        })).Message, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("unassigned", Assert.Throws<InvalidOperationException>(() => AssignmentRoutingService.Validate(new RouterConfiguration
        {
            Policies = new RouterPolicies { AssignmentRouting = new AssignmentRoutingPolicy { Unassigned = "sometimes" } }
        })).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Filter_coding_tasks_ranks_and_removes_issues_by_assignment()
    {
        var configuration = AssignmentConfiguration("prefer", "allow");
        var identity = Identity("alice");
        var response = new WorkflowResponse
        {
            IsSuccessful = true,
            Tasks = new List<WorkflowItem>
            {
                new() { Type = WorkflowItemType.NewIssue, IssueNumber = 1 },
                new() { Type = WorkflowItemType.NewIssue, IssueNumber = 2 },
                new() { Type = WorkflowItemType.NewIssue, IssueNumber = 3 },
                new() { Type = WorkflowItemType.CloseIssue, IssueNumber = 4 }
            }
        };
        var issues = new[] { IssueWithAssignee(1, "alice"), IssueWithAssignee(2), IssueWithAssignee(3, "bob") };

        var filtered = AssignmentRoutingService.FilterCodingTasks(configuration, identity, issues, response);

        Assert.Equal(4, filtered.Tasks.Count);
        Assert.Equal(0, filtered.Tasks.Single(task => task.IssueNumber == 1).SelectionRank);
        Assert.Equal(1, filtered.Tasks.Single(task => task.IssueNumber == 2).SelectionRank);
        Assert.Equal(2, filtered.Tasks.Single(task => task.IssueNumber == 3).SelectionRank);
        Assert.Equal(0, filtered.Tasks.Single(task => task.IssueNumber == 4).SelectionRank);
        Assert.False(filtered.NoEligibleWork);
    }

    [Fact]
    public void Filter_coding_tasks_blocks_when_require_has_no_matching_work()
    {
        var configuration = AssignmentConfiguration("require", "exclude");
        var identity = Identity("alice");
        var response = new WorkflowResponse
        {
            IsSuccessful = true,
            Tasks = new List<WorkflowItem> { new() { Type = WorkflowItemType.NewIssue, IssueNumber = 1 } }
        };

        var filtered = AssignmentRoutingService.FilterCodingTasks(configuration, identity, new[] { IssueWithAssignee(1, "bob") }, response);

        Assert.Empty(filtered.Tasks);
        Assert.True(filtered.NoEligibleWork);
        Assert.Single(filtered.IneligibleAssignmentIssues);
        Assert.Contains("alice", filtered.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Worker_routing_and_assignment_routing_combine_in_coding_task_filtering()
    {
        var configuration = WorkerAndAssignmentConfiguration(assignmentMode: "prefer", unassigned: "allow");
        var identity = Identity("alice");
        var response = new WorkflowResponse
        {
            IsSuccessful = true,
            Tasks = new List<WorkflowItem>
            {
                new() { Type = WorkflowItemType.NewIssue, IssueNumber = 1 },
                new() { Type = WorkflowItemType.NewIssue, IssueNumber = 2 }
            }
        };
        var issues = new[]
        {
            WorkerIssue(1, "codex:worker:luna", "alice"),
            WorkerIssue(2, "codex:worker:terra", "bob")
        };

        var filtered = AssignmentRoutingService.FilterCodingTasks(
            configuration,
            identity,
            issues,
            WorkerRoutingService.FilterCodingTasks(configuration, issues, response, "terra-model"));

        var selected = Assert.Single(filtered.Tasks);
        Assert.Equal(2, selected.IssueNumber);
        Assert.Equal(2, selected.SelectionRank);
        var workerIneligible = Assert.Single(filtered.IneligibleWorkerIssues);
        Assert.Equal("luna", workerIneligible.WorkerProfile);
    }

    [Fact]
    public async Task Candidate_discovery_expands_when_assignment_makes_work_ineligible()
    {
        var configuration = AssignmentConfiguration("require", "exclude");
        var identity = Identity("alice");
        var calls = new List<int>();
        var result = await WorkerRoutingService.DiscoverCandidatesAsync(
            configuration,
            2,
            null,
            async limit =>
            {
                calls.Add(limit);
                await Task.Yield();
                return limit == 2
                    ? new[] { IssueWithAssignee(1, "bob"), IssueWithAssignee(2, "bob") }
                    : new[] { IssueWithAssignee(1, "bob"), IssueWithAssignee(2, "bob"), IssueWithAssignee(3, "alice") };
            },
            identity);

        Assert.Equal(new[] { 2, 4 }, calls);
        Assert.Contains(result.Issues, issue => issue.Number == 3);
        var filtered = AssignmentRoutingService.FilterIssues(configuration, identity, result.Issues);
        Assert.Equal(3, filtered.EligibleIssues.Single().Number);
    }

    [Fact]
    public async Task Candidate_discovery_expands_past_lower_ranked_work_to_find_preferred_work()
    {
        var configuration = AssignmentConfiguration("prefer", "allow");
        var identity = Identity("alice");
        var calls = new List<int>();
        var result = await WorkerRoutingService.DiscoverCandidatesAsync(
            configuration,
            1,
            null,
            async limit =>
            {
                calls.Add(limit);
                await Task.Yield();
                return limit == 1
                    ? new[] { IssueWithAssignee(1, "bob") }
                    : new[] { IssueWithAssignee(1, "bob"), IssueWithAssignee(2, "alice") };
            },
            identity);

        Assert.Equal(new[] { 1, 2 }, calls);
        Assert.Contains(result.Issues, issue => issue.Number == 2);
        var filtered = AssignmentRoutingService.FilterIssues(configuration, identity, result.Issues);
        Assert.Contains(filtered.EligibleIssues, issue => issue.Number == 2);
    }

    [Fact]
    public async Task Candidate_discovery_stops_immediately_when_preferred_work_is_found()
    {
        var configuration = AssignmentConfiguration("prefer", "allow");
        var identity = Identity("alice");
        var calls = new List<int>();
        var result = await WorkerRoutingService.DiscoverCandidatesAsync(
            configuration,
            1,
            null,
            async limit =>
            {
                calls.Add(limit);
                await Task.Yield();
                return new[] { IssueWithAssignee(1, "alice") };
            },
            identity);

        Assert.Equal(new[] { 1 }, calls);
        Assert.Equal(1, result.Issues.Single().Number);
    }

    [Fact]
    public async Task Candidate_discovery_prefer_scan_is_bounded_by_the_maximum_scan_limit()
    {
        var configuration = AssignmentConfiguration("prefer", "allow");
        var identity = Identity("alice");
        var calls = new List<int>();
        var result = await WorkerRoutingService.DiscoverCandidatesAsync(
            configuration,
            1,
            null,
            async limit =>
            {
                calls.Add(limit);
                await Task.Yield();
                return Enumerable.Range(1, limit)
                    .Select(number => IssueWithAssignee(number, "bob"))
                    .ToList();
            },
            identity);

        Assert.Equal(WorkerRoutingService.MaxDiscoveryScanLimit, calls.Last());
        var eligible = AssignmentRoutingService.FilterIssues(configuration, identity, result.Issues);
        Assert.DoesNotContain(eligible.EligibleIssues, issue => AssignmentRoutingService.Evaluate(configuration, identity, issue).SelectionRank == 0);
    }

    [Fact]
    public void Filter_coding_tasks_applies_assignment_to_recovery_and_pull_request_linking_work()
    {
        var configuration = AssignmentConfiguration("require", "exclude");
        var identity = Identity("alice");
        var response = new WorkflowResponse
        {
            IsSuccessful = true,
            Tasks = new List<WorkflowItem>
            {
                new() { Type = WorkflowItemType.RecoverCompletedIssue, IssueNumber = 1 },
                new() { Type = WorkflowItemType.RecoverCurrentPullRequest, IssueNumber = 2 },
                new() { Type = WorkflowItemType.LinkPullRequestsToIssues, IssueNumber = 3 },
                new() { Type = WorkflowItemType.CloseIssue, IssueNumber = 4 }
            }
        };
        var issues = new[] { IssueWithAssignee(1, "bob"), IssueWithAssignee(2, "bob"), IssueWithAssignee(3, "alice") };

        var filtered = AssignmentRoutingService.FilterCodingTasks(configuration, identity, issues, response);

        Assert.Equal(2, filtered.Tasks.Count);
        Assert.Contains(filtered.Tasks, task => task.Type == WorkflowItemType.LinkPullRequestsToIssues && task.IssueNumber == 3);
        Assert.Contains(filtered.Tasks, task => task.Type == WorkflowItemType.CloseIssue);
        Assert.DoesNotContain(filtered.Tasks, task => task.Type is WorkflowItemType.RecoverCompletedIssue or WorkflowItemType.RecoverCurrentPullRequest);
        Assert.Equal(2, filtered.IneligibleAssignmentIssues.Count);
    }

    [Fact]
    public void Filter_coding_tasks_blocks_when_recovery_work_is_excluded_for_the_current_identity()
    {
        var configuration = AssignmentConfiguration("require", "exclude");
        var identity = Identity("alice");
        var response = new WorkflowResponse
        {
            IsSuccessful = true,
            Tasks = new List<WorkflowItem> { new() { Type = WorkflowItemType.RecoverCurrentPullRequest, IssueNumber = 1 } }
        };

        var filtered = AssignmentRoutingService.FilterCodingTasks(configuration, identity, new[] { IssueWithAssignee(1, "bob") }, response);

        Assert.Empty(filtered.Tasks);
        Assert.True(filtered.NoEligibleWork);
        Assert.Single(filtered.IneligibleAssignmentIssues);
    }

    [Fact]
    public void No_eligible_work_message_describes_identity_and_assignees()
    {
        var configuration = AssignmentConfiguration("require", "allow");
        var identity = Identity("alice");
        var ineligible = AssignmentRoutingService.Evaluate(configuration, identity, IssueWithAssignee(1, "bob"));

        var message = AssignmentRoutingService.FormatNoEligibleWorkMessage(identity, new[] { ineligible });

        Assert.Contains("Current identity: alice", message, StringComparison.Ordinal);
        Assert.Contains("GitHub assignee(s): alice", message, StringComparison.Ordinal);
        Assert.Contains("bob", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Hook_router_selects_the_lowest_assignment_rank_within_a_task_type()
    {
        var tasks = new List<WorkflowItem>
        {
            new() { Type = WorkflowItemType.NewIssue, IssueNumber = 2, SelectionRank = 2 },
            new() { Type = WorkflowItemType.NewIssue, IssueNumber = 1, SelectionRank = 0 },
            new() { Type = WorkflowItemType.NewIssue, IssueNumber = 3, SelectionRank = 1 }
        };

        var decision = HookTaskRouter.Route(tasks);

        Assert.Equal(1, decision.SelectedTask!.IssueNumber);
    }

    [Fact]
    public async Task In_progress_issues_for_other_developers_are_not_resumed_in_require_mode()
    {
        var configuration = AssignmentConfiguration("require", "allow");
        var identity = Identity("alice");
        var response = await WorkflowService.EvaluateInProgressIssuesAsync(
            configuration,
            new[] { IssueWithAssignee(1, "alice"), IssueWithAssignee(2, "bob") },
            _ => throw new InvalidOperationException("No pull request should be requested."),
            null,
            null,
            identity);

        Assert.True(response.IsSuccessful);
        Assert.Single(response.Tasks);
        Assert.Equal(1, response.Tasks.Single().IssueNumber);
        Assert.Equal(0, response.Tasks.Single().SelectionRank);
    }

    private static AssignmentIdentity Identity(string name) => new()
    {
        Name = name,
        GitHubUsernames = new[] { name }
    };

    private static RouterConfiguration AssignmentConfiguration(
        string mode,
        string unassigned) => new()
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
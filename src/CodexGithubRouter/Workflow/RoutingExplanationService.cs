using CodexGithubRouter.GitHub;
using CodexGithubRouter.Hooks;
using CodexGithubRouter.Work;

namespace CodexGithubRouter.Workflow;

public static class RoutingExplanationService
{
    private static readonly HashSet<WorkflowItemType> BlockingTypes = new()
    {
        WorkflowItemType.ClosedWithoutMerge,
        WorkflowItemType.UnknownPullRequestState,
        WorkflowItemType.Unknown,
        WorkflowItemType.RepositoryGateBlock
    };

    public static IReadOnlyList<IssueRoutingExplanation> ExplainAll(RoutingEvaluationResult plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var candidateIssues = MergeCandidateIssues(plan);
        var explanations = candidateIssues.Select(issue => Explain(plan, issue)).ToList();

        return explanations
            .OrderBy(explanation => OutcomePriority(plan, explanation))
            .ThenBy(explanation => explanation.IsEligible ? explanation.SelectionRank : int.MaxValue)
            .ThenBy(explanation => explanation.IssueNumber)
            .ToList();
    }

    public static IssueRoutingExplanation Explain(RoutingEvaluationResult plan, Issue issue)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(issue);

        var stages = new List<RoutingStage>();
        var isEligible = true;

        var workflowStage = ExplainWorkflowState(plan.Configuration, issue);
        stages.Add(workflowStage);
        isEligible &= workflowStage.Verdict != RoutingVerdict.HardIneligible;

        stages.Add(ExplainDiscovery(plan, issue));

        var workerStage = ExplainWorkerRouting(plan.Configuration, issue, plan.CurrentModel);
        stages.Add(workerStage);
        isEligible &= workerStage.Verdict != RoutingVerdict.HardIneligible;

        var assignmentStage = ExplainAssignmentRouting(plan.Configuration, issue, plan.AssignmentIdentity);
        stages.Add(assignmentStage);
        isEligible &= assignmentStage.Verdict != RoutingVerdict.HardIneligible;

        var gateStage = ExplainRepositoryGate(plan, issue);
        stages.Add(gateStage);
        isEligible &= gateStage.Verdict != RoutingVerdict.HardIneligible;

        var claimStage = ExplainClaimStatus(issue, plan.ActiveClaim);
        stages.Add(claimStage);
        isEligible &= claimStage.Verdict != RoutingVerdict.HardIneligible;

        var workflowTask = SelectWorkflowTask(plan, issue);
        var outcomeStage = ExplainOutcome(plan, issue, workflowTask);
        stages.Add(outcomeStage);
        var selected = outcomeStage.Verdict == RoutingVerdict.Selected;

        var selectionRank = ResolveSelectionRank(plan.Configuration, issue, plan.AssignmentIdentity);

        return new IssueRoutingExplanation
        {
            IssueNumber = issue.Number,
            IssueTitle = issue.Title,
            IsEligible = isEligible,
            IsSelected = selected,
            SelectionRank = selectionRank,
            RoutingTaskType = workflowTask?.Type,
            Stages = stages,
            Summary = FormatSummary(isEligible, selectionRank, selected, issue, stages)
        };
    }

    public static string FormatExplanations(IReadOnlyList<IssueRoutingExplanation> explanations)
    {
        var lines = new List<string>();
        lines.Add("Routing explanations:");
        lines.Add(string.Empty);
        foreach (var explanation in explanations)
        {
            var marker = explanation.IsSelected
                ? "SELECTED"
                : explanation.IsEligible
                    ? "ELIGIBLE"
                    : "INELIGIBLE";
            lines.Add($"  #{explanation.IssueNumber} {(string.IsNullOrWhiteSpace(explanation.IssueTitle) ? "" : $"({explanation.IssueTitle}) ")}- {marker}");
            if (explanation.SelectionRank < int.MaxValue)
            {
                lines.Add($"    Selection rank: {explanation.SelectionRank}");
            }

            if (explanation.RoutingTaskType.HasValue)
            {
                lines.Add($"    Task: {FormatTaskType(explanation.RoutingTaskType.Value)}");
            }

            foreach (var stage in explanation.Stages.Where(stage => stage.Verdict != RoutingVerdict.Disabled))
            {
                var stageMarker = stage.Verdict switch
                {
                    RoutingVerdict.Selected => "*",
                    RoutingVerdict.Pass => "+",
                    RoutingVerdict.SoftPrefer => "*",
                    RoutingVerdict.SoftIneligible => "~",
                    RoutingVerdict.HardIneligible => "!",
                    _ => " "
                };
                lines.Add($"    [{stageMarker}] {stage.Name}: {stage.Message}");
            }

            lines.Add(string.Empty);
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static string FormatSingleExplanation(IssueRoutingExplanation explanation)
    {
        var lines = new List<string>();
        lines.Add($"Issue #{explanation.IssueNumber}{(string.IsNullOrWhiteSpace(explanation.IssueTitle) ? "" : $" ({explanation.IssueTitle})")}");
        lines.Add($"Eligible: {(explanation.IsEligible ? "yes" : "no")}");
        if (explanation.RoutingTaskType.HasValue)
        {
            lines.Add($"Task: {FormatTaskType(explanation.RoutingTaskType.Value)}");
        }

        if (explanation.IsSelected)
        {
            lines.Add("Selected: yes");
        }

        if (explanation.SelectionRank < int.MaxValue)
        {
            lines.Add($"Selection rank: {explanation.SelectionRank}");
        }

        lines.Add(string.Empty);
        lines.Add("Decision stages:");
        foreach (var stage in explanation.Stages)
        {
            if (stage.Verdict == RoutingVerdict.Disabled)
            {
                continue;
            }

            var label = stage.Verdict switch
            {
                RoutingVerdict.Selected => "SELECTED",
                RoutingVerdict.Pass => "PASS",
                RoutingVerdict.SoftPrefer => "PREFER",
                RoutingVerdict.SoftIneligible => "SKIP",
                RoutingVerdict.HardIneligible => "BLOCKED",
                _ => "N/A"
            };
            lines.Add($"  [{label}] {stage.Name}");
            lines.Add($"         {stage.Message}");
        }

        lines.Add(string.Empty);
        lines.Add(explanation.Summary);
        return string.Join(Environment.NewLine, lines);
    }

    private static RoutingStage ExplainWorkflowState(RouterConfiguration configuration, Issue issue)
    {
        var resolution = WorkflowStateResolver.Resolve(issue.Labels.Select(label => label.Name), configuration.States);
        if (resolution.IsAmbiguous)
        {
            return new RoutingStage
            {
                Name = "Workflow State",
                Verdict = RoutingVerdict.HardIneligible,
                Message = $"Ambiguous workflow state on issue #{issue.Number}: {resolution.DescribeConflict($"issue #{issue.Number}")}."
            };
        }

        if (resolution.MatchedLabels.Count == 0)
        {
            return new RoutingStage
            {
                Name = "Workflow State",
                Verdict = RoutingVerdict.HardIneligible,
                Message = $"Issue #{issue.Number} has no recognized workflow labels."
            };
        }

        var matchedState = resolution.MatchedLabels.Keys.First();
        var isActionable = matchedState is WorkflowState.Ready or WorkflowState.InProgress or WorkflowState.Completed;
        var labelList = string.Join(", ", resolution.MatchedLabels[matchedState].OrderBy(label => label, StringComparer.OrdinalIgnoreCase));

        return new RoutingStage
        {
            Name = "Workflow State",
            Verdict = isActionable ? RoutingVerdict.Pass : RoutingVerdict.HardIneligible,
            Message = isActionable
                ? $"Issue #{issue.Number} is {matchedState} (labels: {labelList})."
                : $"Issue #{issue.Number} is {matchedState} (labels: {labelList}), which is not actionable work."
        };
    }

    private static RoutingStage ExplainDiscovery(RoutingEvaluationResult plan, Issue issue)
    {
        var isConsidered = plan.ConsideredIssues.Any(candidate => candidate.Number == issue.Number);
        if (isConsidered)
        {
            return new RoutingStage
            {
                Name = "Candidate Discovery",
                Verdict = RoutingVerdict.Pass,
                Message = $"Issue #{issue.Number} was discovered by the production routing scan (Completed, InProgress and Ready discovery)."
            };
        }

        var hasGateTask = plan.RepositoryGateTasks.Any(task => task.IssueNumber == issue.Number);
        if (hasGateTask)
        {
            return new RoutingStage
            {
                Name = "Candidate Discovery",
                Verdict = RoutingVerdict.Pass,
                Message = $"Issue #{issue.Number} is part of the production repository-gate short-circuit."
            };
        }

        if (plan.HasRepositoryGate)
        {
            var gatedIssueNumber = plan.RepositoryGateTasks.Select(task => task.IssueNumber).OrderBy(number => number).FirstOrDefault();
            return new RoutingStage
            {
                Name = "Candidate Discovery",
                Verdict = RoutingVerdict.SoftIneligible,
                Message = $"Issue #{issue.Number} was not part of the candidate discovery: the repository gate short-circuits unrelated discovery while issue #{gatedIssueNumber} gates the repository."
            };
        }

        return new RoutingStage
        {
            Name = "Candidate Discovery",
            Verdict = RoutingVerdict.SoftIneligible,
            Message = $"Issue #{issue.Number} was not part of the production candidate discovery for this routing decision."
        };
    }

    private static RoutingStage ExplainWorkerRouting(RouterConfiguration configuration, Issue issue, string? currentModel)
    {
        var eligibility = WorkerRoutingService.Evaluate(configuration, issue, currentModel);
        if (!eligibility.IsEnabled)
        {
            return new RoutingStage
            {
                Name = "Worker Routing",
                Verdict = RoutingVerdict.Disabled,
                Message = "Worker routing is not configured."
            };
        }

        if (eligibility.IsEligible)
        {
            return new RoutingStage
            {
                Name = "Worker Routing",
                Verdict = RoutingVerdict.Pass,
                Message = $"Issue #{issue.Number} belongs to worker '{eligibility.WorkerProfile}' which matches the current model '{currentModel}'."
            };
        }

        return new RoutingStage
        {
            Name = "Worker Routing",
            Verdict = RoutingVerdict.HardIneligible,
            Message = eligibility.Message
        };
    }

    private static RoutingStage ExplainAssignmentRouting(RouterConfiguration configuration, Issue issue, AssignmentIdentity? identity)
    {
        var eligibility = AssignmentRoutingService.Evaluate(configuration, identity, issue);
        if (!eligibility.IsEnabled)
        {
            return new RoutingStage
            {
                Name = "Assignment Routing",
                Verdict = RoutingVerdict.Disabled,
                Message = "Assignment routing is not configured."
            };
        }

        if (eligibility.IsEligible)
        {
            var verb = eligibility.AssignedToCurrentIdentity
                ? $"assigned to current identity ({identity?.Name ?? "<missing>"})"
                : eligibility.IsUnassigned
                    ? "unassigned"
                    : $"assigned to other developer(s): {string.Join(", ", eligibility.IssueAssignees)}";

            var rankDescription = eligibility.SelectionRank switch
            {
                0 => "highest priority",
                1 => "medium priority",
                2 => "lower priority",
                _ => $"rank {eligibility.SelectionRank}"
            };

            return new RoutingStage
            {
                Name = "Assignment Routing",
                Verdict = eligibility.SelectionRank == 0 ? RoutingVerdict.SoftPrefer : RoutingVerdict.SoftIneligible,
                Message = $"Issue #{issue.Number} is {verb}. Rank: {rankDescription}."
            };
        }

        return new RoutingStage
        {
            Name = "Assignment Routing",
            Verdict = RoutingVerdict.HardIneligible,
            Message = eligibility.Message
        };
    }

    private static RoutingStage ExplainRepositoryGate(RoutingEvaluationResult plan, Issue issue)
    {
        var gateLabels = RepositoryGateService.GetLabels(plan.Configuration);
        if (gateLabels.Count == 0)
        {
            return new RoutingStage
            {
                Name = "Repository Gate",
                Verdict = RoutingVerdict.Disabled,
                Message = "Repository gate is not configured."
            };
        }

        if (!RepositoryGateService.IsGated(issue, plan.Configuration))
        {
            return new RoutingStage
            {
                Name = "Repository Gate",
                Verdict = RoutingVerdict.Pass,
                Message = plan.HasRepositoryGate
                    ? $"Issue #{issue.Number} is not gated, but the repository gate short-circuits unrelated discovery while the gated work is active."
                    : $"Issue #{issue.Number} is not gated."
            };
        }

        var gateTasks = plan.RepositoryGateTasks.Where(task => task.IssueNumber == issue.Number).ToList();
        if (gateTasks.Count == 0)
        {
            var issueResolution = WorkflowStateResolver.Resolve(issue.Labels.Select(label => label.Name), plan.Configuration.States);
            if (!issueResolution.IsAmbiguous && issueResolution.MatchedLabels.ContainsKey(WorkflowState.Abandoned))
            {
                return new RoutingStage
                {
                    Name = "Repository Gate",
                    Verdict = RoutingVerdict.Pass,
                    Message = $"Gated issue #{issue.Number} is abandoned; production ignores it for the repository gate."
                };
            }

            return new RoutingStage
            {
                Name = "Repository Gate",
                Verdict = RoutingVerdict.Pass,
                Message = $"Repository gate evaluation produced no blocking task for issue #{issue.Number}."
            };
        }

        var blockingGateTask = gateTasks.FirstOrDefault(task => BlockingTypes.Contains(task.Type));
        if (blockingGateTask is not null)
        {
            return new RoutingStage
            {
                Name = "Repository Gate",
                Verdict = RoutingVerdict.HardIneligible,
                Message = blockingGateTask.Status.Message
            };
        }

        var gateTask = gateTasks[0];
        return new RoutingStage
        {
            Name = "Repository Gate",
            Verdict = RoutingVerdict.Pass,
            Message = $"Issue #{issue.Number} carries the repository gate label and is the gate short-circuit work (task: {FormatTaskType(gateTask.Type)}). Unrelated discovery remains short-circuited until the gate is resolved."
        };
    }

    private static RoutingStage ExplainClaimStatus(Issue issue, WorkClaim? activeClaim)
    {
        if (activeClaim is null)
        {
            return new RoutingStage
            {
                Name = "Work Claim",
                Verdict = RoutingVerdict.Pass,
                Message = "No active work claim."
            };
        }

        if (activeClaim.IssueNumber == issue.Number)
        {
            return new RoutingStage
            {
                Name = "Work Claim",
                Verdict = RoutingVerdict.SoftPrefer,
                Message = $"Active work claim exists for issue #{issue.Number} (owner: {activeClaim.OwnerSessionId})."
            };
        }

        return new RoutingStage
        {
            Name = "Work Claim",
            Verdict = RoutingVerdict.HardIneligible,
            Message = $"Active work claim is held for issue #{activeClaim.IssueNumber}. Issue #{issue.Number} cannot be selected until the current claim is released."
        };
    }

    private static RoutingStage ExplainOutcome(RoutingEvaluationResult plan, Issue issue, WorkflowItem? workflowTask)
    {
        var blockingTask = workflowTask is not null && BlockingTypes.Contains(workflowTask.Type)
            ? workflowTask
            : plan.ActionableTasks.FirstOrDefault(task => task.IssueNumber == issue.Number && BlockingTypes.Contains(task.Type));

        if (!string.IsNullOrWhiteSpace(plan.BlockReason))
        {
            if (blockingTask is not null)
            {
                return new RoutingStage
                {
                    Name = "Routing Outcome",
                    Verdict = RoutingVerdict.HardIneligible,
                    Message = $"Blocking: production routing is blocked: {blockingTask.Status.Message}"
                };
            }

            return new RoutingStage
            {
                Name = "Routing Outcome",
                Verdict = RoutingVerdict.SoftIneligible,
                Message = $"Not routed: the production routing decision is blocked: {plan.BlockReason}"
            };
        }

        var selectedTask = plan.Decision?.SelectedTask;
        if (selectedTask is null)
        {
            return new RoutingStage
            {
                Name = "Routing Outcome",
                Verdict = RoutingVerdict.SoftIneligible,
                Message = $"Issue #{issue.Number} was not part of the production routing decision."
            };
        }

        var actionableTask = workflowTask is not null && !IsPassive(workflowTask.Type)
            ? workflowTask
            : plan.ActionableTasks.FirstOrDefault(task => task.IssueNumber == issue.Number && !IsPassive(task.Type));

        if (actionableTask is not null && actionableTask.IssueNumber == selectedTask.IssueNumber)
        {
            return new RoutingStage
            {
                Name = "Routing Outcome",
                Verdict = RoutingVerdict.Selected,
                Message = $"Selected: production selected issue #{selectedTask.IssueNumber} as the next work item (task: {FormatTaskType(selectedTask.Type)})."
            };
        }

        var selectedOrder = RouteOrder(selectedTask);
        if (actionableTask is not null && selectedOrder is not null)
        {
            var candidateOrder = RouteOrder(actionableTask);
            if (candidateOrder is not null)
            {
                if (candidateOrder.Value.Tier > selectedOrder.Value.Tier)
                {
                    return new RoutingStage
                    {
                        Name = "Routing Outcome",
                        Verdict = RoutingVerdict.SoftIneligible,
                        Message = $"Not selected: production prefers {DescribeTask(selectedTask)} (priority {RoutePriorityLabel(selectedOrder.Value.Tier)}) over this issue's {FormatTaskType(actionableTask.Type)} (priority {RoutePriorityLabel(candidateOrder.Value.Tier)})."
                    };
                }

                if (candidateOrder.Value.Rank > selectedOrder.Value.Rank)
                {
                    return new RoutingStage
                    {
                        Name = "Routing Outcome",
                        Verdict = RoutingVerdict.SoftIneligible,
                        Message = $"Not selected: this issue and {DescribeTask(selectedTask)} share priority {RoutePriorityLabel(candidateOrder.Value.Tier)}; {DescribeTask(selectedTask)} has a better selection rank ({selectedOrder.Value.Rank} vs {candidateOrder.Value.Rank})."
                    };
                }

                return new RoutingStage
                {
                    Name = "Routing Outcome",
                    Verdict = RoutingVerdict.SoftIneligible,
                    Message = $"Not selected: this issue and {DescribeTask(selectedTask)} share priority {RoutePriorityLabel(candidateOrder.Value.Tier)} and selection rank; the tie is resolved by discovery order in favor of {DescribeTask(selectedTask)}."
                };
            }
        }

        if (workflowTask is not null)
        {
            return new RoutingStage
            {
                Name = "Routing Outcome",
                Verdict = RoutingVerdict.SoftIneligible,
                Message = $"Not selected: issue #{issue.Number} is in {FormatTaskType(workflowTask.Type)}, which does not consume the routing decision."
            };
        }

        return new RoutingStage
        {
            Name = "Routing Outcome",
            Verdict = RoutingVerdict.SoftIneligible,
            Message = $"Not selected: issue #{issue.Number} was not part of the production routing decision."
        };
    }

    private static WorkflowItem? SelectWorkflowTask(RoutingEvaluationResult plan, Issue issue)
    {
        var actionable = plan.ActionableTasks
            .Where(task => task.IssueNumber == issue.Number && !IsPassive(task.Type))
            .OrderBy(task => RouteOrder(task)?.Tier ?? int.MaxValue)
            .ThenBy(task => RouteOrder(task)?.Rank ?? int.MaxValue)
            .FirstOrDefault();
        if (actionable is not null)
        {
            return actionable;
        }

        return plan.WorkflowTasks.FirstOrDefault(task => task.IssueNumber == issue.Number);
    }

    private static IReadOnlyList<Issue> MergeCandidateIssues(RoutingEvaluationResult plan)
    {
        var issuesByNumber = new Dictionary<int, Issue>();
        foreach (var issue in plan.ConsideredIssues)
        {
            issuesByNumber[issue.Number] = issue;
        }

        foreach (var task in plan.RepositoryGateTasks)
        {
            if (!issuesByNumber.ContainsKey(task.IssueNumber))
            {
                issuesByNumber[task.IssueNumber] = new Issue { Number = task.IssueNumber };
            }
        }

        return issuesByNumber.Values.OrderBy(issue => issue.Number).ToList();
    }

    private static int OutcomePriority(RoutingEvaluationResult plan, IssueRoutingExplanation explanation)
    {
        if (explanation.IsSelected)
        {
            return 0;
        }

        if (explanation.IsEligible)
        {
            return 2;
        }

        return 3;
    }

    private static int ResolveSelectionRank(RouterConfiguration configuration, Issue issue, AssignmentIdentity? identity)
    {
        var assignmentEligibility = AssignmentRoutingService.Evaluate(configuration, identity, issue);
        return assignmentEligibility.IsEnabled && assignmentEligibility.IsEligible
            ? assignmentEligibility.SelectionRank
            : int.MaxValue;
    }

    private static string FormatSummary(bool isEligible, int selectionRank, bool selected, Issue issue, IReadOnlyList<RoutingStage> stages)
    {
        if (!isEligible)
        {
            var blockedStages = stages.Where(stage => stage.Verdict == RoutingVerdict.HardIneligible).ToList();
            if (blockedStages.Count == 1)
            {
                return $"Issue #{issue.Number} is ineligible: {blockedStages[0].Message}";
            }

            return $"Issue #{issue.Number} is ineligible. Blocked by: {string.Join("; ", blockedStages.Select(stage => $"{stage.Name}: {stage.Message}"))}";
        }

        if (selected)
        {
            return $"Issue #{issue.Number} is eligible and was selected as the next work item by the production routing decision.";
        }

        if (selectionRank == 0)
        {
            return $"Issue #{issue.Number} is eligible and is a highest-priority candidate, but was not selected by this routing decision.";
        }

        return $"Issue #{issue.Number} is eligible with selection rank {selectionRank}, but was not selected by this routing decision.";
    }

    private static string DescribeTask(WorkflowItem task) =>
        task.PullRequestNumber.HasValue
            ? $"issue #{task.IssueNumber} / pull request #{task.PullRequestNumber.Value}"
            : $"issue #{task.IssueNumber}";

    private static (int Tier, int Rank)? RouteOrder(WorkflowItem task)
    {
        if (!task.Type.TryGetRouteTier(out var tier))
        {
            return null;
        }

        return (tier, task.SelectionRank);
    }

    private static bool IsPassive(WorkflowItemType type) =>
        type is WorkflowItemType.AwaitingReview or WorkflowItemType.AwaitingMerge or WorkflowItemType.Deferred or WorkflowItemType.CloseIssue or WorkflowItemType.LinkPullRequestsToIssues;

    private static string FormatTaskType(WorkflowItemType type) => type switch
    {
        WorkflowItemType.ChangeRequest => "ChangeRequest",
        WorkflowItemType.LinkPullRequestsToIssues => "LinkPullRequestsToIssues",
        WorkflowItemType.NewIssue => "NewIssue",
        WorkflowItemType.ResumeInProgressIssue => "ResumeInProgressIssue",
        WorkflowItemType.RecoverCompletedIssue => "RecoverCompletedIssue",
        WorkflowItemType.RecoverCurrentPullRequest => "RecoverCurrentPullRequest",
        WorkflowItemType.AwaitingReview => "AwaitingReview",
        WorkflowItemType.AwaitingMerge => "AwaitingMerge",
        WorkflowItemType.Deferred => "Deferred",
        WorkflowItemType.ClosedWithoutMerge => "ClosedWithoutMerge",
        WorkflowItemType.UnknownPullRequestState => "UnknownPullRequestState",
        WorkflowItemType.CloseIssue => "CloseIssue",
        WorkflowItemType.RepositoryGateBlock => "RepositoryGateBlock",
        _ => type.ToString()
    };

    private static string RoutePriorityLabel(int tier) => tier switch
    {
        0 => "blocker",
        1 => "change-request",
        2 => "recovery",
        3 => "link-pull-request",
        4 => "resume",
        5 => "new-issue",
        _ => $"tier {tier}"
    };
}

public static class RoutingTaskTypeExtensions
{
    public static bool TryGetRouteTier(this WorkflowItemType type, out int tier)
    {
        tier = type switch
        {
            WorkflowItemType.ChangeRequest => 1,
            WorkflowItemType.RecoverCurrentPullRequest or WorkflowItemType.RecoverCompletedIssue => 2,
            WorkflowItemType.LinkPullRequestsToIssues => 3,
            WorkflowItemType.ResumeInProgressIssue => 4,
            WorkflowItemType.NewIssue => 5,
            _ => 0
        };

        return type is WorkflowItemType.ChangeRequest or
            WorkflowItemType.RecoverCurrentPullRequest or
            WorkflowItemType.RecoverCompletedIssue or
            WorkflowItemType.LinkPullRequestsToIssues or
            WorkflowItemType.ResumeInProgressIssue or
            WorkflowItemType.NewIssue;
    }
}

public sealed class IssueRoutingExplanation
{
    public int IssueNumber { get; init; }
    public string IssueTitle { get; init; } = string.Empty;
    public bool IsEligible { get; init; }
    public bool IsSelected { get; init; }
    public int SelectionRank { get; init; }
    public WorkflowItemType? RoutingTaskType { get; init; }
    public IReadOnlyList<RoutingStage> Stages { get; init; } = Array.Empty<RoutingStage>();
    public string Summary { get; init; } = string.Empty;
}

public sealed class RoutingStage
{
    public string Name { get; init; } = string.Empty;
    public RoutingVerdict Verdict { get; init; }
    public string Message { get; init; } = string.Empty;
}

public enum RoutingVerdict
{
    Disabled,
    Pass,
    SoftPrefer,
    SoftIneligible,
    Selected,
    HardIneligible
}
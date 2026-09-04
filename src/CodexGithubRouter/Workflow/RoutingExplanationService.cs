using CodexGithubRouter.GitHub;
using CodexGithubRouter.Work;

namespace CodexGithubRouter.Workflow;

public static class RoutingExplanationService
{
    public static IssueRoutingExplanation Explain(
        RouterConfiguration configuration,
        Issue issue,
        string? currentModel = null,
        AssignmentIdentity? assignmentIdentity = null,
        WorkClaim? activeClaim = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(issue);

        var stages = new List<RoutingStage>();
        var isEligible = true;

        var workflowStage = ExplainWorkflowState(configuration, issue);
        stages.Add(workflowStage);
        if (workflowStage.Verdict == RoutingVerdict.HardIneligible)
        {
            isEligible = false;
        }

        var workerStage = ExplainWorkerRouting(configuration, issue, currentModel);
        stages.Add(workerStage);
        if (workerStage.Verdict == RoutingVerdict.HardIneligible)
        {
            isEligible = false;
        }

        var assignmentStage = ExplainAssignmentRouting(configuration, issue, assignmentIdentity);
        stages.Add(assignmentStage);
        if (assignmentStage.Verdict == RoutingVerdict.HardIneligible)
        {
            isEligible = false;
        }

        var gateStage = ExplainRepositoryGate(configuration, issue);
        stages.Add(gateStage);
        if (gateStage.Verdict == RoutingVerdict.HardIneligible)
        {
            isEligible = false;
        }

        var claimStage = ExplainClaimStatus(issue, activeClaim);
        stages.Add(claimStage);

        var selectionRank = ResolveSelectionRank(configuration, issue, assignmentIdentity);

        return new IssueRoutingExplanation
        {
            IssueNumber = issue.Number,
            IssueTitle = issue.Title,
            IsEligible = isEligible,
            SelectionRank = selectionRank,
            Stages = stages,
            Summary = FormatSummary(isEligible, selectionRank, issue, stages)
        };
    }

    public static IReadOnlyList<IssueRoutingExplanation> ExplainAll(
        RouterConfiguration configuration,
        IReadOnlyList<Issue> issues,
        string? currentModel = null,
        AssignmentIdentity? assignmentIdentity = null,
        WorkClaim? activeClaim = null)
    {
        return issues.Select(issue => Explain(configuration, issue, currentModel, assignmentIdentity, activeClaim)).ToList();
    }

    public static string FormatExplanations(IReadOnlyList<IssueRoutingExplanation> explanations)
    {
        var lines = new List<string>();
        lines.Add("Routing explanations:");
        lines.Add(string.Empty);
        foreach (var explanation in explanations)
        {
            lines.Add($"  #{explanation.IssueNumber} {(string.IsNullOrWhiteSpace(explanation.IssueTitle) ? "" : $"({explanation.IssueTitle}) ")}- {(explanation.IsEligible ? "ELIGIBLE" : "INELIGIBLE")}");
            if (explanation.SelectionRank < int.MaxValue)
            {
                lines.Add($"    Selection rank: {explanation.SelectionRank}");
            }

            foreach (var stage in explanation.Stages.Where(stage => stage.Verdict != RoutingVerdict.Disabled))
            {
                var marker = stage.Verdict switch
                {
                    RoutingVerdict.Pass => "+",
                    RoutingVerdict.SoftPrefer => "*",
                    RoutingVerdict.SoftIneligible => "~",
                    RoutingVerdict.HardIneligible => "!",
                    _ => " "
                };
                lines.Add($"    [{marker}] {stage.Name}: {stage.Message}");
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

    private static RoutingStage ExplainRepositoryGate(RouterConfiguration configuration, Issue issue)
    {
        var gateLabels = RepositoryGateService.GetLabels(configuration);
        if (gateLabels.Count == 0)
        {
            return new RoutingStage
            {
                Name = "Repository Gate",
                Verdict = RoutingVerdict.Disabled,
                Message = "Repository gate is not configured."
            };
        }

        var isGated = RepositoryGateService.IsGated(issue, configuration);
        return new RoutingStage
        {
            Name = "Repository Gate",
            Verdict = isGated ? RoutingVerdict.HardIneligible : RoutingVerdict.Pass,
            Message = isGated
                ? $"Issue #{issue.Number} has a repository gate label ({string.Join(", ", gateLabels)}). Remove the gate label to allow routing."
                : $"Issue #{issue.Number} is not gated."
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
            Message = $"Active work claim is held for issue #{activeClaim.IssueNumber}. Issue #{issue.Number} cannot be claimed until the current claim is released."
        };
    }

    private static int ResolveSelectionRank(RouterConfiguration configuration, Issue issue, AssignmentIdentity? identity)
    {
        var assignmentEligibility = AssignmentRoutingService.Evaluate(configuration, identity, issue);
        return assignmentEligibility.IsEnabled && assignmentEligibility.IsEligible
            ? assignmentEligibility.SelectionRank
            : int.MaxValue;
    }

    private static string FormatSummary(bool isEligible, int selectionRank, Issue issue, IReadOnlyList<RoutingStage> stages)
    {
        if (!isEligible)
        {
            var blockedStages = stages.Where(s => s.Verdict == RoutingVerdict.HardIneligible).ToList();
            if (blockedStages.Count == 1)
            {
                return $"Issue #{issue.Number} is ineligible: {blockedStages[0].Message}";
            }

            return $"Issue #{issue.Number} is ineligible. Blocked by: {string.Join("; ", blockedStages.Select(s => $"{s.Name}: {s.Message}"))}";
        }

        if (selectionRank == 0)
        {
            return $"Issue #{issue.Number} is eligible and is the highest-priority candidate.";
        }

        return $"Issue #{issue.Number} is eligible with selection rank {selectionRank}.";
    }
}

public sealed class IssueRoutingExplanation
{
    public int IssueNumber { get; init; }
    public string IssueTitle { get; init; } = string.Empty;
    public bool IsEligible { get; init; }
    public int SelectionRank { get; init; }
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
    HardIneligible
}

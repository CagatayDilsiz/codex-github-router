using CodexGithubRouter.GitHub;
using CodexGithubRouter.Hooks;
using CodexGithubRouter.Work;

namespace CodexGithubRouter.Workflow;

public sealed class RoutingEvaluationDependencies
{
    public Func<RouterConfiguration, string, Task<WorkflowResponse>> CheckRepositoryGateAsync { get; init; }
        = (configuration, workingDirectory) => WorkflowService.CheckRepositoryGateAsync(configuration, workingDirectory);

    public Func<RouterConfiguration, string, string?, AssignmentIdentity?, Task<WorkflowResponse>> CheckCompletedIssuesAsync { get; init; }
        = (configuration, workingDirectory, currentModel, assignmentIdentity) => WorkflowService.CheckCompletedIssuesAsync(
            configuration,
            workingDirectory,
            currentModel: currentModel,
            assignmentIdentity: assignmentIdentity);

    public Func<RouterConfiguration, string, string?, AssignmentIdentity?, Task<WorkflowResponse>> CheckInProgressIssuesAsync { get; init; }
        = (configuration, workingDirectory, currentModel, assignmentIdentity) => WorkflowService.CheckInProgressIssuesAsync(
            configuration,
            workingDirectory,
            currentModel: currentModel,
            assignmentIdentity: assignmentIdentity);

    public Func<RouterConfiguration, string, string?, AssignmentIdentity?, Task<WorkflowResponse>> CheckNewIssuesAsync { get; init; }
        = (configuration, workingDirectory, currentModel, assignmentIdentity) => WorkflowService.CheckNewIssuesAsync(
            configuration,
            workingDirectory,
            currentModel: currentModel,
            assignmentIdentity: assignmentIdentity);

    public Func<RouterConfiguration, string, WorkClaim, string?, Task<WorkflowResponse>> CheckClaimedWorkAsync { get; init; }
        = (configuration, workingDirectory, claim, currentModel) => WorkflowService.CheckClaimedWorkAsync(
            configuration,
            workingDirectory,
            claim,
            currentModel);

    public Func<RouterConfiguration, string, Task<AssignmentIdentityResolution>> ResolveAssignmentIdentityAsync { get; init; }
        = (_, _) => Task.FromResult(AssignmentIdentityResolution.NotEnabled);
}

/// <summary>
/// Produces the read-only production routing plan that the hook and the diagnostic
/// commands share. Every stage (repository gate, active-claim routing, discovery,
/// worker/assignment exclusion, generated workflow items and the final routing
/// decision) is evaluated through the same production code paths used by the hook.
/// Assignment identity resolution is part of the plan and is only performed on the
/// ordinary routing path, so an active repository gate never requires developer
/// identity. This service never mutates repository state: acquiring claims, closing
/// issues and writing claim files remain exclusive to the hook.
/// </summary>
public static class RoutingEvaluationService
{
    public static async Task<RoutingEvaluationResult> EvaluateAsync(
        RouterConfiguration configuration,
        string workingDirectory,
        string? currentModel = null,
        AssignmentIdentity? assignmentIdentity = null,
        WorkClaim? activeClaim = null,
        RoutingEvaluationDependencies? dependencies = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(workingDirectory);

        dependencies ??= new RoutingEvaluationDependencies();

        if (activeClaim is not null)
        {
            return await EvaluateClaimedAsync(configuration, workingDirectory, currentModel, assignmentIdentity, activeClaim, dependencies);
        }

        var identityResolution = AssignmentIdentityResolution.NotEnabled;
        var repositoryGateTasks = await dependencies.CheckRepositoryGateAsync(configuration, workingDirectory);
        if (!repositoryGateTasks.IsSuccessful)
        {
            return RoutingEvaluationResult.Failure(repositoryGateTasks.Message, configuration, workingDirectory, currentModel, assignmentIdentity, activeClaim);
        }

        IReadOnlyList<WorkflowItem> workflowTasks;
        WorkflowResponse? noEligibleWorkResponse = null;
        IReadOnlyList<WorkflowResponse> discoveryResponses;
        if (repositoryGateTasks.Tasks.Count > 0)
        {
            workflowTasks = HookService.SelectWorkflowTasks(repositoryGateTasks.Tasks, Array.Empty<WorkflowItem>());
            discoveryResponses = new[] { repositoryGateTasks };
        }
        else
        {
            if (AssignmentRoutingService.RequiresLocalIdentity(configuration) && assignmentIdentity is null)
            {
                identityResolution = await dependencies.ResolveAssignmentIdentityAsync(configuration, workingDirectory);
                if (identityResolution.IsEnabled && !identityResolution.IsResolved)
                {
                    return RoutingEvaluationResult.Failure(identityResolution.Message, configuration, workingDirectory, currentModel, assignmentIdentity, activeClaim, identityResolution);
                }

                if (identityResolution.IsEnabled && identityResolution.IsResolved)
                {
                    assignmentIdentity = identityResolution.Identity;
                }
            }

            var completedIssueTasks = await dependencies.CheckCompletedIssuesAsync(configuration, workingDirectory, currentModel, assignmentIdentity);
            if (!completedIssueTasks.IsSuccessful)
            {
                return RoutingEvaluationResult.Failure(completedIssueTasks.Message, configuration, workingDirectory, currentModel, assignmentIdentity, activeClaim, identityResolution);
            }

            var inProgressIssueTasks = await dependencies.CheckInProgressIssuesAsync(configuration, workingDirectory, currentModel, assignmentIdentity);
            if (!inProgressIssueTasks.IsSuccessful)
            {
                return RoutingEvaluationResult.Failure(inProgressIssueTasks.Message, configuration, workingDirectory, currentModel, assignmentIdentity, activeClaim, identityResolution);
            }

            var newIssueTask = await dependencies.CheckNewIssuesAsync(configuration, workingDirectory, currentModel, assignmentIdentity);
            if (!newIssueTask.IsSuccessful)
            {
                return RoutingEvaluationResult.Failure(newIssueTask.Message, configuration, workingDirectory, currentModel, assignmentIdentity, activeClaim, identityResolution);
            }

            var ordinaryResponses = new[] { completedIssueTasks, inProgressIssueTasks, newIssueTask };
            var workerIneligible = ordinaryResponses.SelectMany(response => response.IneligibleWorkerIssues).ToList();
            var assignmentIneligible = ordinaryResponses.SelectMany(response => response.IneligibleAssignmentIssues).ToList();
            noEligibleWorkResponse = ordinaryResponses.FirstOrDefault(response => response.NoEligibleWork);
            if (workerIneligible.Count > 0 || assignmentIneligible.Count > 0)
            {
                noEligibleWorkResponse = new WorkflowResponse
                {
                    NoEligibleWork = true,
                    IneligibleWorkerIssues = workerIneligible,
                    IneligibleAssignmentIssues = assignmentIneligible,
                    Message = string.Join(
                        Environment.NewLine,
                        new[]
                        {
                            workerIneligible.Count > 0 ? WorkerRoutingService.FormatNoEligibleWorkMessage(currentModel, workerIneligible) : null,
                            assignmentIneligible.Count > 0 ? AssignmentRoutingService.FormatNoEligibleWorkMessage(assignmentIdentity, assignmentIneligible) : null
                        }.Where(part => !string.IsNullOrWhiteSpace(part)))
                };
            }

            workflowTasks = HookService.SelectWorkflowTasks(
                Array.Empty<WorkflowItem>(),
                completedIssueTasks.Tasks
                    .Concat(inProgressIssueTasks.Tasks)
                    .Concat(newIssueTask.Tasks)
                    .ToList());
            discoveryResponses = ordinaryResponses;
        }

        var consideredIssues = MergeConsideredIssues(discoveryResponses);

        string? blockReason;
        HookTaskDecision? decision = null;
        IReadOnlyList<WorkflowItem> actionableTasks = Array.Empty<WorkflowItem>();

        if (workflowTasks.Count == 0)
        {
            blockReason = noEligibleWorkResponse?.Message ?? "No actionable workflow tasks found.";
        }
        else
        {
            actionableTasks = workflowTasks.Where(task => task.Type != WorkflowItemType.Deferred).ToList();
            if (actionableTasks.Count == 0)
            {
                blockReason = noEligibleWorkResponse?.NoEligibleWork == true
                    ? noEligibleWorkResponse.Message
                    : "All workflow tasks are deferred. No action is required at this time.";
            }
            else
            {
                decision = HookTaskRouter.Route(actionableTasks);
                blockReason = HookService.ResolveRoutingBlockReason(decision, noEligibleWorkResponse);
            }
        }

        return new RoutingEvaluationResult
        {
            Configuration = configuration,
            WorkingDirectory = workingDirectory,
            CurrentModel = currentModel,
            AssignmentIdentity = assignmentIdentity,
            ActiveClaim = activeClaim,
            IsSuccessful = true,
            IdentityResolution = identityResolution,
            RepositoryGateTasks = repositoryGateTasks.Tasks,
            OrdinaryTasks = workflowTasks,
            WorkflowTasks = workflowTasks,
            ActionableTasks = actionableTasks,
            Decision = decision,
            BlockReason = blockReason,
            NoEligibleWorkResponse = noEligibleWorkResponse,
            ConsideredIssues = consideredIssues
        };
    }

    private static async Task<RoutingEvaluationResult> EvaluateClaimedAsync(
        RouterConfiguration configuration,
        string workingDirectory,
        string? currentModel,
        AssignmentIdentity? assignmentIdentity,
        WorkClaim activeClaim,
        RoutingEvaluationDependencies dependencies)
    {
        var claimedWork = await dependencies.CheckClaimedWorkAsync(configuration, workingDirectory, activeClaim, currentModel);
        if (!claimedWork.IsSuccessful)
        {
            return RoutingEvaluationResult.Failure(claimedWork.Message, configuration, workingDirectory, currentModel, assignmentIdentity, activeClaim);
        }

        var workflowTasks = claimedWork.Tasks.ToList();
        var actionableTasks = workflowTasks.Where(task => task.Type != WorkflowItemType.Deferred).ToList();

        var consideredIssues = claimedWork.ConsideredIssues.Count == 0
            ? new List<Issue> { new() { Number = activeClaim.IssueNumber } }
            : claimedWork.ConsideredIssues.ToList();

        string? blockReason;
        HookTaskDecision? decision = null;
        if (workflowTasks.Count == 0)
        {
            blockReason = $"Active work claim for issue #{activeClaim.IssueNumber}{FormatPullRequest(activeClaim.PullRequestNumber)} has no actionable matching task. No unrelated work will be routed.";
        }
        else if (actionableTasks.Count == 0)
        {
            blockReason = "All workflow tasks are deferred. No action is required at this time.";
        }
        else
        {
            decision = HookTaskRouter.Route(actionableTasks);
            blockReason = string.Equals(decision.BlockReason, "No actionable workflow tasks found.", StringComparison.Ordinal)
                ? $"Active work claim for issue #{activeClaim.IssueNumber}{FormatPullRequest(activeClaim.PullRequestNumber)} has no actionable matching task. No unrelated work will be routed."
                : decision.BlockReason;
        }

        return new RoutingEvaluationResult
        {
            Configuration = configuration,
            WorkingDirectory = workingDirectory,
            CurrentModel = currentModel,
            AssignmentIdentity = assignmentIdentity,
            ActiveClaim = activeClaim,
            IsSuccessful = true,
            ClaimRoutingActive = true,
            WorkflowTasks = workflowTasks,
            ActionableTasks = actionableTasks,
            Decision = decision,
            BlockReason = blockReason,
            ConsideredIssues = consideredIssues
        };
    }

    private static string FormatPullRequest(int? pullRequestNumber) =>
        pullRequestNumber.HasValue ? $" / pull request #{pullRequestNumber.Value}" : string.Empty;

    private static IReadOnlyList<Issue> MergeConsideredIssues(IReadOnlyList<WorkflowResponse> responses)
    {
        var issuesByNumber = new Dictionary<int, Issue>();
        foreach (var response in responses)
        {
            foreach (var issue in response.ConsideredIssues)
            {
                issuesByNumber[issue.Number] = issue;
            }
        }

        return issuesByNumber.Values
            .OrderBy(issue => issue.Number)
            .ToList();
    }
}

public sealed class RoutingEvaluationResult
{
    public static RoutingEvaluationResult Failure(
        string message,
        RouterConfiguration configuration,
        string workingDirectory,
        string? currentModel,
        AssignmentIdentity? assignmentIdentity,
        WorkClaim? activeClaim,
        AssignmentIdentityResolution? identityResolution = null) => new()
        {
            Configuration = configuration,
            WorkingDirectory = workingDirectory,
            CurrentModel = currentModel,
            AssignmentIdentity = assignmentIdentity,
            ActiveClaim = activeClaim,
            IsSuccessful = false,
            IdentityResolution = identityResolution ?? AssignmentIdentityResolution.NotEnabled,
            DiscoveryFailureMessage = message
        };

    public RouterConfiguration Configuration { get; init; } = new();

    public string WorkingDirectory { get; init; } = string.Empty;

    public string? CurrentModel { get; init; }

    public AssignmentIdentity? AssignmentIdentity { get; init; }

    public WorkClaim? ActiveClaim { get; init; }

    public bool IsSuccessful { get; init; } = true;

    public string? DiscoveryFailureMessage { get; init; }

    public bool ClaimRoutingActive { get; init; }

    public AssignmentIdentityResolution IdentityResolution { get; init; } = AssignmentIdentityResolution.NotEnabled;

    public IReadOnlyList<WorkflowItem> RepositoryGateTasks { get; init; } = Array.Empty<WorkflowItem>();

    public IReadOnlyList<WorkflowItem> OrdinaryTasks { get; init; } = Array.Empty<WorkflowItem>();

    public IReadOnlyList<WorkflowItem> WorkflowTasks { get; init; } = Array.Empty<WorkflowItem>();

    public IReadOnlyList<WorkflowItem> ActionableTasks { get; init; } = Array.Empty<WorkflowItem>();

    public HookTaskDecision? Decision { get; init; }

    public string? BlockReason { get; init; }

    public WorkflowResponse? NoEligibleWorkResponse { get; init; }

    public IReadOnlyList<Issue> ConsideredIssues { get; init; } = Array.Empty<Issue>();

    public bool HasRepositoryGate => RepositoryGateTasks.Count > 0;
}
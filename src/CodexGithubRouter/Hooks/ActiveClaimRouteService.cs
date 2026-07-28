using CodexGithubRouter.Configurations;
using CodexGithubRouter.GitHub;
using CodexGithubRouter.Workflow;
using CodexGithubRouter.Work;

namespace CodexGithubRouter.Hooks;

public sealed class ActiveClaimRouteService
{
    private readonly Func<WorkClaim, string?, Task<WorkflowResponse>> checkClaimedWork;
    private readonly Func<Task<WorkClaim?>> readClaim;
    private readonly Func<WorkClaim, Task<WorkClaimAcquisitionResult>> acquireClaim;
    private readonly Func<Task<bool>> reconcileClaim;

    public ActiveClaimRouteService(
        Func<WorkClaim, Task<WorkflowResponse>> checkClaimedWork,
        Func<Task<WorkClaim?>> readClaim,
        Func<WorkClaim, Task<WorkClaimAcquisitionResult>> acquireClaim,
        Func<Task<bool>> reconcileClaim)
        : this(
            (claim, _) => checkClaimedWork(claim),
            readClaim,
            acquireClaim,
            reconcileClaim)
    {
    }

    public ActiveClaimRouteService(
        Func<WorkClaim, string?, Task<WorkflowResponse>> checkClaimedWork,
        Func<Task<WorkClaim?>> readClaim,
        Func<WorkClaim, Task<WorkClaimAcquisitionResult>> acquireClaim,
        Func<Task<bool>> reconcileClaim)
    {
        this.checkClaimedWork = checkClaimedWork;
        this.readClaim = readClaim;
        this.acquireClaim = acquireClaim;
        this.reconcileClaim = reconcileClaim;
    }

    public static ActiveClaimRouteService Create(
        string workingDirectory,
        string gitCommonDirectory,
        RouterConfiguration configuration)
    {
        return new ActiveClaimRouteService(
            (claim, model) => WorkflowService.CheckClaimedWorkAsync(configuration, workingDirectory, claim, model),
            () => WorkClaimStore.ReadAsync(gitCommonDirectory),
            requested => WorkClaimStore.TryAcquireAsync(gitCommonDirectory, requested),
            () => WorkClaimReconciliationService.ReconcileAsync(workingDirectory, gitCommonDirectory, configuration));
    }

    public async Task<HookTaskDecision?> RouteAsync(WorkClaim activeClaim, string? sessionId, string? currentModel = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return new HookTaskDecision { BlockReason = "Cannot continue repository work: the hook payload did not include a session ID." };
        }

        var currentClaim = activeClaim;
        var claimedWork = await checkClaimedWork(currentClaim, currentModel);
        if (!claimedWork.IsSuccessful)
        {
            return new HookTaskDecision { BlockReason = claimedWork.Message };
        }

        var candidatePullRequests = claimedWork.Tasks
            .Where(task => task.PullRequestNumber.HasValue)
            .Select(task => task.PullRequestNumber!.Value)
            .Distinct()
            .ToList();
        if (currentClaim.PullRequestNumber is null && candidatePullRequests.Count > 1)
        {
            return new HookTaskDecision { BlockReason = $"Active work claim for issue #{currentClaim.IssueNumber} has multiple candidate pull requests ({string.Join(", ", candidatePullRequests.Select(number => $"#{number}"))}). No work identity will be selected implicitly." };
        }

        if (currentClaim.PullRequestNumber is null && candidatePullRequests.Count == 1)
        {
            var enrichment = await acquireClaim(new WorkClaim
            {
                OwnerSessionId = sessionId,
                IssueNumber = currentClaim.IssueNumber,
                PullRequestNumber = candidatePullRequests[0],
                WorkType = currentClaim.WorkType,
                WorkerProfile = currentClaim.WorkerProfile,
                Model = currentModel
            });
            if (!enrichment.Acquired)
            {
                return new HookTaskDecision { BlockReason = enrichment.BlockReason ?? "Could not associate the active work claim with its linked pull request." };
            }

            currentClaim = await readClaim() ?? enrichment.Claim!;
            claimedWork = await checkClaimedWork(currentClaim, currentModel);
            if (!claimedWork.IsSuccessful)
            {
                return new HookTaskDecision { BlockReason = claimedWork.Message };
            }
        }

        if (IsReleaseCandidate(claimedWork))
        {
            if (await reconcileClaim())
            {
                return null;
            }

            currentClaim = await readClaim();
            if (currentClaim is null)
            {
                return null;
            }

            claimedWork = await checkClaimedWork(currentClaim, currentModel);
            if (!claimedWork.IsSuccessful)
            {
                return new HookTaskDecision { BlockReason = claimedWork.Message };
            }

            if (IsReleaseCandidate(claimedWork))
            {
                return new HookTaskDecision { BlockReason = $"Active work claim for issue #{currentClaim.IssueNumber}{FormatPullRequest(currentClaim.PullRequestNumber)} remains passive or terminal, but could not be released safely. No unrelated work will be routed." };
            }
        }

        var decision = HookTaskRouter.RouteClaimedWork(
            currentClaim,
            sessionId,
            claimedWork.Tasks.Where(task => task.Type != WorkflowItemType.Deferred).ToList());
        if (!string.IsNullOrWhiteSpace(decision.BlockReason))
        {
            return decision;
        }

        if (decision.SelectedTask is not null && HookTaskRouter.RequiresWorkClaim(decision.SelectedTask))
        {
            var acquisition = await acquireClaim(new WorkClaim
            {
                OwnerSessionId = sessionId,
                IssueNumber = decision.SelectedTask.IssueNumber,
                PullRequestNumber = decision.SelectedTask.PullRequestNumber,
                WorkType = decision.SelectedTask.Type == WorkflowItemType.ChangeRequest ? WorkClaimType.ChangeRequest : WorkClaimType.Implementation,
                Model = currentModel
            });
            if (!acquisition.Acquired)
            {
                return new HookTaskDecision { BlockReason = acquisition.BlockReason ?? "Could not continue the repository work claim." };
            }

            currentClaim = await readClaim() ?? acquisition.Claim!;
            claimedWork = await checkClaimedWork(currentClaim, currentModel);
            if (!claimedWork.IsSuccessful)
            {
                return new HookTaskDecision { BlockReason = claimedWork.Message };
            }

            if (IsReleaseCandidate(claimedWork))
            {
                if (await reconcileClaim())
                {
                    return null;
                }

                return new HookTaskDecision { BlockReason = $"Active work claim for issue #{currentClaim.IssueNumber}{FormatPullRequest(currentClaim.PullRequestNumber)} changed to a passive or terminal state but could not be released safely. No unrelated work will be routed." };
            }

            decision = HookTaskRouter.RouteClaimedWork(
                currentClaim,
                sessionId,
                claimedWork.Tasks.Where(task => task.Type != WorkflowItemType.Deferred).ToList());
        }

        return decision;
    }

    private static bool IsReleaseCandidate(WorkflowResponse response) =>
        response.Tasks.Count == 1 && response.Tasks[0].Type is
            WorkflowItemType.AwaitingReview or
            WorkflowItemType.AwaitingMerge or
            WorkflowItemType.Deferred or
            WorkflowItemType.CloseIssue or
            WorkflowItemType.ClosedWithoutMerge;

    private static string FormatPullRequest(int? pullRequestNumber) =>
        pullRequestNumber.HasValue ? $" / pull request #{pullRequestNumber.Value}" : string.Empty;
}

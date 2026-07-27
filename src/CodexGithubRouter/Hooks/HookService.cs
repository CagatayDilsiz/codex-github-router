using System.Text.Json;
using CodexGithubRouter.Autonomous;
using CodexGithubRouter.Configurations;
using CodexGithubRouter.GitHub;
using CodexGithubRouter.Workflow;
using CodexGithubRouter.Git;
using CodexGithubRouter.Work;

namespace CodexGithubRouter.Hooks;


public static class HookService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<int> RunAsync()
    {
        try
        {
            var json = await Console.In.ReadToEndAsync();

            if (string.IsNullOrWhiteSpace(json))
            {
                await WriteBlockAsync("Could not read hook payload from stdin.");
                return 0;
            }

            var payload = JsonSerializer.Deserialize<HookPayload>(
                json,
                JsonOptions);

            if (payload is null)
            {
                await WriteBlockAsync("Could not deserialize hook payload.");
                return 0;
            }

            // If this executable is accidentally bound to another hook event
            // continue without any intervention.
            if (!string.Equals(payload.HookEventName, "UserPromptSubmit", StringComparison.Ordinal))
            {
                return 0;
            }

            if (!await AutonomousService.IsAutonomousAsync(payload.Cwd))
            {
                // If autonomous mode is disabled, do not intervene in the manual prompt.
                return 0;
            }

            var configuration = await WorkflowConfigurationService.LoadOrCreateAsync();
            var gitCommonDirectory = await GitRepositoryService.GetCommonDirectoryAsync(payload.Cwd)
                ?? throw new InvalidOperationException("Not a valid Git repository.");

            await WorkClaimReconciliationService.ReconcileAsync(payload.Cwd, gitCommonDirectory, configuration);
            var activeClaim = await WorkClaimStore.ReadAsync(gitCommonDirectory);
            if (activeClaim is not null && !string.Equals(activeClaim.OwnerSessionId, payload.SessionId, StringComparison.Ordinal))
            {
                await WriteBlockAsync($"Active work claim for issue #{activeClaim.IssueNumber}{(activeClaim.PullRequestNumber.HasValue ? $" / pull request #{activeClaim.PullRequestNumber.Value}" : string.Empty)} is owned by another Codex session.");
                return 0;
            }

            if (activeClaim is not null)
            {
                var claimedDecision = await RouteActiveClaimAsync(payload.Cwd, gitCommonDirectory, configuration, payload.SessionId, activeClaim);
                if (claimedDecision is not null)
                {
                    if (!string.IsNullOrWhiteSpace(claimedDecision.BlockReason))
                    {
                        await WriteBlockAsync(claimedDecision.BlockReason);
                    }
                    else
                    {
                        await WriteAdditionalContextAsync(claimedDecision.AdditionalContext!);
                    }

                    return 0;
                }

                // The claim was safely released during claimed-work recovery. Continue through
                // the normal no-claim route in this same hook invocation.
            }

            return await RunWithoutActiveClaimAsync(payload.Cwd, gitCommonDirectory, configuration, payload.SessionId);
        }
        catch (JsonException exception)
        {
            await Console.Error.WriteLineAsync(exception.ToString());
            await WriteBlockAsync("Hook payload is not valid JSON.");

            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(exception.ToString());

            await WriteBlockAsync(
                $"Codex Github Router could not be run: {exception.Message}");

            return 0;
        }
    }

    private static async Task<HookTaskDecision?> RouteActiveClaimAsync(
        string workingDirectory,
        string gitCommonDirectory,
        RouterConfiguration configuration,
        string? sessionId,
        WorkClaim activeClaim)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return new HookTaskDecision { BlockReason = "Cannot continue repository work: the hook payload did not include a session ID." };
        }

        var currentClaim = activeClaim;
        var claimedWork = await WorkflowService.CheckClaimedWorkAsync(configuration, workingDirectory, currentClaim);
        if (!claimedWork.IsSuccessful)
        {
            return new HookTaskDecision { BlockReason = claimedWork.Message };
        }

        // A PR-less claim may be associated only with the unique PR discovered for a
        // completed issue. Working issues deliberately continue implementation even when
        // an old passive PR is still linked to them.
        var candidatePullRequests = claimedWork.Tasks
            .Where(task => task.PullRequestNumber.HasValue)
            .Select(task => task.PullRequestNumber!.Value)
            .Distinct()
            .ToList();
        if (currentClaim.PullRequestNumber is null && candidatePullRequests.Count > 1)
        {
            return new HookTaskDecision { BlockReason = $"Active work claim for issue #{currentClaim.IssueNumber} has multiple candidate pull requests ({string.Join(", ", candidatePullRequests.Select(number => $"#{number}"))}). No work identity will be selected implicitly." };
        }

        var candidatePullRequest = candidatePullRequests.SingleOrDefault();
        if (currentClaim.PullRequestNumber is null && candidatePullRequests.Count == 1)
        {
            var enrichment = await WorkClaimStore.TryAcquireAsync(gitCommonDirectory, new WorkClaim
            {
                OwnerSessionId = sessionId,
                IssueNumber = currentClaim.IssueNumber,
                PullRequestNumber = candidatePullRequest,
                WorkType = currentClaim.WorkType
            });
            if (!enrichment.Acquired)
            {
                return new HookTaskDecision { BlockReason = enrichment.BlockReason ?? "Could not associate the active work claim with its linked pull request." };
            }

            currentClaim = await WorkClaimStore.ReadAsync(gitCommonDirectory) ?? enrichment.Claim!;
            claimedWork = await WorkflowService.CheckClaimedWorkAsync(configuration, workingDirectory, currentClaim);
            if (!claimedWork.IsSuccessful)
            {
                return new HookTaskDecision { BlockReason = claimedWork.Message };
            }
        }

        if (IsReleaseCandidate(claimedWork))
        {
            if (await WorkClaimReconciliationService.ReconcileAsync(workingDirectory, gitCommonDirectory, configuration))
            {
                return null;
            }

            // A state change may have raced the reconciliation read. Refresh once and
            // route only the current claim; never fall through to unrelated work here.
            currentClaim = await WorkClaimStore.ReadAsync(gitCommonDirectory);
            if (currentClaim is null)
            {
                return null;
            }

            claimedWork = await WorkflowService.CheckClaimedWorkAsync(configuration, workingDirectory, currentClaim);
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
            var acquisition = await WorkClaimStore.TryAcquireAsync(gitCommonDirectory, new WorkClaim
            {
                OwnerSessionId = sessionId,
                IssueNumber = decision.SelectedTask.IssueNumber,
                PullRequestNumber = decision.SelectedTask.PullRequestNumber,
                WorkType = decision.SelectedTask.Type == WorkflowItemType.ChangeRequest ? WorkClaimType.ChangeRequest : WorkClaimType.Implementation
            });
            if (!acquisition.Acquired)
            {
                return new HookTaskDecision { BlockReason = acquisition.BlockReason ?? "Could not continue the repository work claim." };
            }

            // Acquisition increments the claim revision and may enrich it with a PR. Use
            // the persisted claim and fresh GitHub state for the final routing decision.
            currentClaim = await WorkClaimStore.ReadAsync(gitCommonDirectory) ?? acquisition.Claim!;
            claimedWork = await WorkflowService.CheckClaimedWorkAsync(configuration, workingDirectory, currentClaim);
            if (!claimedWork.IsSuccessful)
            {
                return new HookTaskDecision { BlockReason = claimedWork.Message };
            }

            if (IsReleaseCandidate(claimedWork))
            {
                if (await WorkClaimReconciliationService.ReconcileAsync(workingDirectory, gitCommonDirectory, configuration))
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

    private static async Task<int> RunWithoutActiveClaimAsync(
        string workingDirectory,
        string gitCommonDirectory,
        RouterConfiguration configuration,
        string? sessionId)
    {
        var completedIssueTasks = await WorkflowService.CheckCompletedIssuesAsync(configuration, workingDirectory);
        if (!completedIssueTasks.IsSuccessful)
        {
            await WriteBlockAsync(completedIssueTasks.Message);
            return 0;
        }

        var inProgressIssueTasks = await WorkflowService.CheckInProgressIssuesAsync(configuration, workingDirectory);
        if (!inProgressIssueTasks.IsSuccessful)
        {
            await WriteBlockAsync(inProgressIssueTasks.Message);
            return 0;
        }

        var newIssueTask = await WorkflowService.CheckNewIssuesAsync(configuration, workingDirectory);
        if (!newIssueTask.IsSuccessful)
        {
            await WriteBlockAsync(newIssueTask.Message);
            return 0;
        }

        var combinedTasks = new WorkflowResponse
        {
            IsSuccessful = true,
            Message = "Combined workflow tasks.",
            Tasks = completedIssueTasks.Tasks.Concat(inProgressIssueTasks.Tasks).Concat(newIssueTask.Tasks).ToList()
        };

        if (combinedTasks.Tasks.Count == 0)
        {
            await WriteBlockAsync("No actionable workflow tasks found.");
            return 0;
        }

        var actionableTasks = combinedTasks.Tasks.Where(t => t.Type != WorkflowItemType.Deferred).ToList();
        if (actionableTasks.Count == 0)
        {
            await WriteBlockAsync("All workflow tasks are deferred. No action is required at this time.");
            return 0;
        }

        // Close issues marked for closure before evaluating hook blockers.
        foreach (var closingIssueTask in actionableTasks.Where(task => task.Type == WorkflowItemType.CloseIssue))
        {
            await GitHubCliService.CloseIssueAsync(workingDirectory, closingIssueTask.IssueNumber, CancellationToken.None);
        }

        var decision = HookTaskRouter.Route(actionableTasks);
        if (!string.IsNullOrWhiteSpace(decision.BlockReason))
        {
            await WriteBlockAsync(decision.BlockReason);
            return 0;
        }

        if (decision.SelectedTask is not null && HookTaskRouter.RequiresWorkClaim(decision.SelectedTask))
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                await WriteBlockAsync("Cannot acquire repository work: the hook payload did not include a session ID.");
                return 0;
            }

            var claimType = decision.SelectedTask.Type == WorkflowItemType.ChangeRequest
                ? WorkClaimType.ChangeRequest
                : WorkClaimType.Implementation;
            var acquisition = await WorkClaimStore.TryAcquireAsync(gitCommonDirectory, new WorkClaim
            {
                OwnerSessionId = sessionId,
                IssueNumber = decision.SelectedTask.IssueNumber,
                PullRequestNumber = decision.SelectedTask.PullRequestNumber,
                WorkType = claimType
            });
            if (!acquisition.Acquired)
            {
                await WriteBlockAsync(acquisition.BlockReason ?? "Could not acquire the repository work claim.");
                return 0;
            }
        }

        await WriteAdditionalContextAsync(decision.AdditionalContext!);
        return 0;
    }

    private static Task WriteBlockAsync(string reason)
    {
        return WriteJsonAsync(new
        {
            decision = "block",
            reason
        });
    }

    private static Task WriteAdditionalContextAsync(string context)
    {
        return WriteJsonAsync(new
        {
            hookSpecificOutput = new
            {
                hookEventName = "UserPromptSubmit",
                additionalContext = context
            }
        });
    }

    private static async Task WriteJsonAsync(object response)
    {
        var json = JsonSerializer.Serialize(response);

        await Console.Out.WriteLineAsync(json);
        await Console.Out.FlushAsync();
    }
}

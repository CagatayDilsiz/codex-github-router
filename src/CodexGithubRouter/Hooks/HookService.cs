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
                var claimedWork = await WorkflowService.CheckClaimedWorkAsync(configuration, payload.Cwd, activeClaim);
                if (!claimedWork.IsSuccessful)
                {
                    await WriteBlockAsync(claimedWork.Message);
                    return 0;
                }

                var passiveClaimedTask = claimedWork.Tasks.SingleOrDefault(task => task.Type is WorkflowItemType.AwaitingReview or WorkflowItemType.AwaitingMerge or WorkflowItemType.Deferred);
                if (activeClaim.PullRequestNumber is null && passiveClaimedTask?.PullRequestNumber is not null)
                {
                    var enrichment = await WorkClaimStore.TryAcquireAsync(gitCommonDirectory, new WorkClaim
                    {
                        OwnerSessionId = payload.SessionId!,
                        IssueNumber = activeClaim.IssueNumber,
                        PullRequestNumber = passiveClaimedTask.PullRequestNumber,
                        WorkType = activeClaim.WorkType
                    });
                    if (!enrichment.Acquired)
                    {
                        await WriteBlockAsync(enrichment.BlockReason ?? "Could not associate the active work claim with its linked pull request.");
                        return 0;
                    }

                    if (await WorkClaimReconciliationService.ReconcileAsync(payload.Cwd, gitCommonDirectory, configuration))
                    {
                        await WriteBlockAsync("The active work claim was released because its linked pull request is passive.");
                        return 0;
                    }
                }

                var claimedTasks = claimedWork.Tasks.Where(task => task.Type != WorkflowItemType.Deferred).ToList();
                var claimedDecision = HookTaskRouter.RouteClaimedWork(activeClaim, payload.SessionId, claimedTasks);
                if (!string.IsNullOrWhiteSpace(claimedDecision.BlockReason))
                {
                    await WriteBlockAsync(claimedDecision.BlockReason);
                    return 0;
                }

                if (claimedDecision.SelectedTask is not null && HookTaskRouter.RequiresWorkClaim(claimedDecision.SelectedTask))
                {
                    var acquisition = await WorkClaimStore.TryAcquireAsync(gitCommonDirectory, new WorkClaim
                    {
                        OwnerSessionId = payload.SessionId!,
                        IssueNumber = claimedDecision.SelectedTask.IssueNumber,
                        PullRequestNumber = claimedDecision.SelectedTask.PullRequestNumber,
                        WorkType = claimedDecision.SelectedTask.Type == WorkflowItemType.ChangeRequest ? WorkClaimType.ChangeRequest : WorkClaimType.Implementation
                    });
                    if (!acquisition.Acquired)
                    {
                        await WriteBlockAsync(acquisition.BlockReason ?? "Could not continue the repository work claim.");
                        return 0;
                    }
                }

                await WriteAdditionalContextAsync(claimedDecision.AdditionalContext!);
                return 0;
            }

            var completedIssueTasks = await WorkflowService.CheckCompletedIssuesAsync(configuration, payload.Cwd);


            if (!completedIssueTasks.IsSuccessful)
            {
                await WriteBlockAsync(completedIssueTasks.Message);
                return 0;
            }

            var inProgressIssueTasks = await WorkflowService.CheckInProgressIssuesAsync(configuration, payload.Cwd);

            if (!inProgressIssueTasks.IsSuccessful)
            {
                await WriteBlockAsync(inProgressIssueTasks.Message);
                return 0;
            }

            var newIssueTask = await WorkflowService.CheckNewIssuesAsync(configuration, payload.Cwd);

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

            // close any issues that are marked for closure before hook blocker
            var closingIssueTasks = actionableTasks
                .Where(task => task.Type == WorkflowItemType.CloseIssue)
                .Where(task => activeClaim is null || task.IssueNumber == activeClaim.IssueNumber)
                .ToList();

            foreach (var closingIssueTask in closingIssueTasks)
            {
                await GitHubCliService.CloseIssueAsync(payload.Cwd, closingIssueTask.IssueNumber, CancellationToken.None);
            }

            var decision = HookTaskRouter.Route(actionableTasks);

            if (!string.IsNullOrWhiteSpace(decision.BlockReason))
            {
                await WriteBlockAsync(decision.BlockReason);
                return 0;
            }

            if (decision.SelectedTask is not null && HookTaskRouter.RequiresWorkClaim(decision.SelectedTask))
            {
                if (string.IsNullOrWhiteSpace(payload.SessionId))
                {
                    await WriteBlockAsync("Cannot acquire repository work: the hook payload did not include a session ID.");
                    return 0;
                }

                var claimType = decision.SelectedTask.Type == WorkflowItemType.ChangeRequest
                    ? WorkClaimType.ChangeRequest
                    : WorkClaimType.Implementation;
                var acquisition = await WorkClaimStore.TryAcquireAsync(gitCommonDirectory, new WorkClaim
                {
                    OwnerSessionId = payload.SessionId,
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

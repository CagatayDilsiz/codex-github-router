using System.Text.Json;
using CodexGithubRouter.Autonomous;
using CodexGithubRouter.Configurations;
using CodexGithubRouter.Diagnostics;
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

    public static IReadOnlyList<WorkflowItem> SelectWorkflowTasks(IReadOnlyList<WorkflowItem> repositoryGateTasks, IReadOnlyList<WorkflowItem> ordinaryTasks) =>
        repositoryGateTasks.Count > 0 ? repositoryGateTasks : ordinaryTasks;

    public static Task<int> RunAsync() => RunAsync(new HookExecutionDependencies());

    public static async Task<int> RunAsync(HookExecutionDependencies dependencies)
    {
        HookDiagnosticScope? scope = null;
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

            scope = new HookDiagnosticScope(
                payload.Cwd,
                dependencies.ResolveGitCommonDirectoryAsync,
                dependencies.ResolveDiagnosticsPolicyAsync,
                payload.Model);

            // If this executable is accidentally bound to another hook event
            // continue without any intervention.
            if (!string.Equals(payload.HookEventName, "UserPromptSubmit", StringComparison.Ordinal))
            {
                scope.Bypass();
                return 0;
            }

            var autonomousEnabled = await dependencies.IsAutonomousAsync(payload.Cwd);
            scope.SetAutonomous(autonomousEnabled);
            if (!autonomousEnabled)
            {
                // If autonomous mode is disabled, do not intervene in the manual prompt.
                scope.Bypass();
                return 0;
            }

            var configuration = await dependencies.LoadConfigurationAsync(payload.Cwd);
            scope.SetDiagnosticsPolicy(configuration.Policies.Diagnostics);
            var activationMode = ResolveActivationMode(configuration);
            var activated = AutonomousActivationService.IsActivated(configuration.Policies.AutonomousActivation, payload.Prompt);
            scope.SetActivation(activationMode, activated);
            if (!activated)
            {
                scope.Bypass();
                return 0;
            }

            var gitCommonDirectory = await dependencies.ResolveGitCommonDirectoryAsync(payload.Cwd)
                ?? throw new InvalidOperationException("Not a valid Git repository.");
            scope.SetRepository(gitCommonDirectory);

            await WorkClaimReconciliationService.ReconcileAsync(payload.Cwd, gitCommonDirectory, configuration);
            var activeClaim = await WorkClaimStore.ReadAsync(gitCommonDirectory);
            if (activeClaim is not null && !string.Equals(activeClaim.OwnerSessionId, payload.SessionId, StringComparison.Ordinal))
            {
                scope.SetClaim(activeClaim);
                var blockReason = $"Active work claim for issue #{activeClaim.IssueNumber}{(activeClaim.PullRequestNumber.HasValue ? $" / pull request #{activeClaim.PullRequestNumber.Value}" : string.Empty)} is owned by another Codex session.";
                scope.Block(blockReason);
                await WriteBlockAsync(blockReason);
                return 0;
            }

            if (activeClaim is not null)
            {
                scope.SetClaim(activeClaim);
                var claimedDecision = await RouteActiveClaimAsync(payload.Cwd, gitCommonDirectory, configuration, payload.SessionId, payload.Model, activeClaim);
                if (claimedDecision is not null)
                {
                    if (!string.IsNullOrWhiteSpace(claimedDecision.BlockReason))
                    {
                        scope.Block(claimedDecision.BlockReason);
                        await WriteBlockAsync(claimedDecision.BlockReason);
                    }
                    else
                    {
                        scope.Context(claimedDecision.SelectedTask);
                        await WriteAdditionalContextAsync(claimedDecision.AdditionalContext!);
                    }

                    return 0;
                }

                // The claim was safely released during claimed-work recovery. Continue through
                // the normal no-claim route in this same hook invocation.
            }

            return await RunWithoutActiveClaimAsync(payload.Cwd, gitCommonDirectory, configuration, payload.SessionId, payload.Model, scope);
        }
        catch (WorkClaimFileException exception)
        {
            scope?.Error(exception);
            await Console.Error.WriteLineAsync(exception.ToString());
            await WriteBlockAsync("The repository work-claim file is invalid and must be repaired before continuing.");

            return 0;
        }
        catch (JsonException exception)
        {
            scope?.Error(exception);
            await Console.Error.WriteLineAsync(exception.ToString());
            await WriteBlockAsync("Hook payload is not valid JSON.");

            return 0;
        }
        catch (Exception exception)
        {
            scope?.Error(exception);
            await Console.Error.WriteLineAsync(exception.ToString());

            await WriteBlockAsync(
                $"Codex Github Router could not be run: {exception.Message}");

            return 0;
        }
        finally
        {
            if (scope is not null)
            {
                await scope.CompleteAsync();
            }
        }
    }

    private static Task<HookTaskDecision?> RouteActiveClaimAsync(
        string workingDirectory,
        string gitCommonDirectory,
        RouterConfiguration configuration,
        string? sessionId,
        string? currentModel,
        WorkClaim activeClaim)
        => ActiveClaimRouteService.Create(workingDirectory, gitCommonDirectory, configuration)
            .RouteAsync(activeClaim, sessionId, currentModel);

    private static async Task<int> RunWithoutActiveClaimAsync(
        string workingDirectory,
        string gitCommonDirectory,
        RouterConfiguration configuration,
        string? sessionId,
        string? currentModel,
        HookDiagnosticScope scope,
        bool allowNoClaimReroute = true)
    {
        var repositoryGateTasks = await WorkflowService.CheckRepositoryGateAsync(configuration, workingDirectory);
        if (!repositoryGateTasks.IsSuccessful)
        {
            scope.Block(repositoryGateTasks.Message);
            await WriteBlockAsync(repositoryGateTasks.Message);
            return 0;
        }

        IReadOnlyList<WorkflowItem> workflowTasks;
        WorkflowResponse? noEligibleWorkResponse = null;
        if (repositoryGateTasks.Tasks.Count > 0)
        {
            // A repository gate is an explicit short-circuit. Ordinary discovery
            // must not be allowed to override or fail before gated work is routed.
            workflowTasks = SelectWorkflowTasks(repositoryGateTasks.Tasks, Array.Empty<WorkflowItem>());
        }
        else
        {
            var completedIssueTasks = await WorkflowService.CheckCompletedIssuesAsync(configuration, workingDirectory, currentModel: currentModel);
            if (!completedIssueTasks.IsSuccessful)
            {
                scope.Block(completedIssueTasks.Message);
                await WriteBlockAsync(completedIssueTasks.Message);
                return 0;
            }

            var inProgressIssueTasks = await WorkflowService.CheckInProgressIssuesAsync(configuration, workingDirectory, currentModel: currentModel);
            if (!inProgressIssueTasks.IsSuccessful)
            {
                scope.Block(inProgressIssueTasks.Message);
                await WriteBlockAsync(inProgressIssueTasks.Message);
                return 0;
            }

            var newIssueTask = await WorkflowService.CheckNewIssuesAsync(configuration, workingDirectory, currentModel: currentModel);
            if (!newIssueTask.IsSuccessful)
            {
                scope.Block(newIssueTask.Message);
                await WriteBlockAsync(newIssueTask.Message);
                return 0;
            }

            var ordinaryResponses = new[] { completedIssueTasks, inProgressIssueTasks, newIssueTask };
            noEligibleWorkResponse = ordinaryResponses.FirstOrDefault(response => response.NoEligibleWork);
            if (ordinaryResponses.SelectMany(response => response.IneligibleWorkerIssues).Any())
            {
                noEligibleWorkResponse = new WorkflowResponse
                {
                    NoEligibleWork = true,
                    IneligibleWorkerIssues = ordinaryResponses.SelectMany(response => response.IneligibleWorkerIssues).ToList(),
                    Message = WorkerRoutingService.FormatNoEligibleWorkMessage(
                        currentModel,
                        ordinaryResponses.SelectMany(response => response.IneligibleWorkerIssues).ToList())
                };
            }

            workflowTasks = SelectWorkflowTasks(
                Array.Empty<WorkflowItem>(),
                completedIssueTasks.Tasks
                    .Concat(inProgressIssueTasks.Tasks)
                    .Concat(newIssueTask.Tasks)
                    .ToList());
        }

        if (workflowTasks.Count == 0)
        {
            var noWorkBlockReason = noEligibleWorkResponse?.Message ?? "No actionable workflow tasks found.";
            scope.Block(noWorkBlockReason);
            await WriteBlockAsync(noWorkBlockReason);
            return 0;
        }

        var actionableTasks = workflowTasks.Where(t => t.Type != WorkflowItemType.Deferred).ToList();
        if (actionableTasks.Count == 0)
        {
            var deferredBlockReason = noEligibleWorkResponse?.NoEligibleWork == true
                ? noEligibleWorkResponse.Message
                : "All workflow tasks are deferred. No action is required at this time.";
            scope.Block(deferredBlockReason);
            await WriteBlockAsync(deferredBlockReason);
            return 0;
        }

        // Close issues marked for closure before evaluating hook blockers.
        foreach (var closingIssueTask in actionableTasks.Where(task => task.Type == WorkflowItemType.CloseIssue))
        {
            await GitHubCliService.CloseIssueAsync(workingDirectory, closingIssueTask.IssueNumber, CancellationToken.None);
        }

        var decision = HookTaskRouter.Route(actionableTasks);
        var blockReason = ResolveRoutingBlockReason(decision, noEligibleWorkResponse);
        if (!string.IsNullOrWhiteSpace(blockReason))
        {
            scope.Block(blockReason);
            await WriteBlockAsync(blockReason);
            return 0;
        }

        if (decision.SelectedTask is not null && HookTaskRouter.RequiresWorkClaim(decision.SelectedTask))
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                const string missingSessionReason = "Cannot acquire repository work: the hook payload did not include a session ID.";
                scope.Block(missingSessionReason);
                await WriteBlockAsync(missingSessionReason);
                return 0;
            }

            var claimType = decision.SelectedTask.Type == WorkflowItemType.ChangeRequest
                ? WorkClaimType.ChangeRequest
                : WorkClaimType.Implementation;
            var claimedIssue = await GitHubCliService.GetIssueByNumberAsync(workingDirectory, decision.SelectedTask.IssueNumber, CancellationToken.None);
            var eligibility = WorkerRoutingService.Evaluate(configuration, claimedIssue, currentModel);
            if (eligibility.IsEnabled && !eligibility.IsEligible)
            {
                scope.Block(eligibility.Message);
                await WriteBlockAsync(eligibility.Message);
                return 0;
            }

            var acquisition = await WorkClaimStore.TryAcquireAsync(gitCommonDirectory, new WorkClaim
            {
                OwnerSessionId = sessionId,
                IssueNumber = decision.SelectedTask.IssueNumber,
                PullRequestNumber = decision.SelectedTask.PullRequestNumber,
                WorkType = claimType,
                WorkerProfile = eligibility.WorkerProfile,
                Model = currentModel,
                ClaimedIssueUpdatedAt = claimedIssue.UpdatedAt
            });
            if (!acquisition.Acquired)
            {
                var acquisitionBlockReason = acquisition.BlockReason ?? "Could not acquire the repository work claim.";
                scope.Block(acquisitionBlockReason);
                await WriteBlockAsync(acquisitionBlockReason);
                return 0;
            }

            var newlyAcquiredClaim = acquisition.Claim;
            var acquiredClaim = await WorkClaimStore.ReadAsync(gitCommonDirectory) ?? newlyAcquiredClaim;
            if (newlyAcquiredClaim is null || acquiredClaim is null)
            {
                const string rereadFailureReason = "Repository work was acquired but could not be re-read safely.";
                scope.Block(rereadFailureReason);
                await WriteBlockAsync(rereadFailureReason);
                return 0;
            }

            scope.SetClaim(newlyAcquiredClaim);

            var refreshedClaimedWork = await WorkflowService.CheckClaimedWorkAsync(configuration, workingDirectory, acquiredClaim, currentModel);
            if (!refreshedClaimedWork.IsSuccessful)
            {
                var released = await WorkClaimStore.ReleaseIfMatchesAsync(gitCommonDirectory, newlyAcquiredClaim);
                var refreshBlockReason = released
                    ? refreshedClaimedWork.Message
                    : $"{refreshedClaimedWork.Message} The newly acquired claim could not be released safely because it changed concurrently; no work context was delivered.";
                scope.Block(refreshBlockReason);
                await WriteBlockAsync(refreshBlockReason);
                return 0;
            }

            if (IsReleaseCandidate(refreshedClaimedWork))
            {
                if (await WorkClaimReconciliationService.ReconcileAsync(workingDirectory, gitCommonDirectory, configuration))
                {
                    if (allowNoClaimReroute)
                    {
                        return await RunWithoutActiveClaimAsync(workingDirectory, gitCommonDirectory, configuration, sessionId, currentModel, scope, false);
                    }

                    const string rerouteDisallowedReason = "The acquired work became passive or terminal during refresh and was released; no second routing pass is allowed in this invocation.";
                    scope.Block(rerouteDisallowedReason);
                    await WriteBlockAsync(rerouteDisallowedReason);
                    return 0;
                }

                var releaseFailureReason = $"Active work claim for issue #{acquiredClaim.IssueNumber}{FormatPullRequest(acquiredClaim.PullRequestNumber)} changed to a passive or terminal state but could not be released safely. No unrelated work will be routed.";
                scope.Block(releaseFailureReason);
                await WriteBlockAsync(releaseFailureReason);
                return 0;
            }

            var refreshedDecision = HookTaskRouter.RouteClaimedWork(
                acquiredClaim,
                sessionId,
                refreshedClaimedWork.Tasks.Where(task => task.Type != WorkflowItemType.Deferred).ToList());
            if (!string.IsNullOrWhiteSpace(refreshedDecision.BlockReason))
            {
                scope.Block(refreshedDecision.BlockReason);
                await WriteBlockAsync(refreshedDecision.BlockReason);
                return 0;
            }

            scope.Context(refreshedDecision.SelectedTask);
            await WriteAdditionalContextAsync(refreshedDecision.AdditionalContext!);
            return 0;
        }

        scope.Context(decision.SelectedTask);
        await WriteAdditionalContextAsync(decision.AdditionalContext!);
        return 0;
    }

    private static bool IsReleaseCandidate(WorkflowResponse response) =>
        response.Tasks.Count == 1 && response.Tasks[0].Type is
            WorkflowItemType.AwaitingReview or
            WorkflowItemType.AwaitingMerge or
            WorkflowItemType.Deferred or
            WorkflowItemType.CloseIssue or
            WorkflowItemType.ClosedWithoutMerge;

    private static string ResolveActivationMode(RouterConfiguration configuration)
    {
        var mode = configuration.Policies.AutonomousActivation?.Mode?.Trim();
        return string.IsNullOrWhiteSpace(mode) ? "always" : mode;
    }

    public static string? ResolveRoutingBlockReason(HookTaskDecision decision, WorkflowResponse? noEligibleWorkResponse)
    {
        if (noEligibleWorkResponse?.NoEligibleWork == true && string.Equals(decision.BlockReason, "No actionable workflow tasks found.", StringComparison.Ordinal))
        {
            return noEligibleWorkResponse.Message;
        }

        return decision.BlockReason;
    }

    private static string FormatPullRequest(int? pullRequestNumber) =>
        pullRequestNumber.HasValue ? $" / pull request #{pullRequestNumber.Value}" : string.Empty;

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

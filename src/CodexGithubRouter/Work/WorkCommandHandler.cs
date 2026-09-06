using CodexGithubRouter.Configurations;
using CodexGithubRouter.Explain;
using CodexGithubRouter.Git;
using CodexGithubRouter.GitHub;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Work;

public static class WorkCommandHandler
{
    public static Task<int> HandleAsync(string[] args) => HandleAsync(args, null, null, null, null);

    public static Task<int> HandleAsync(string[] args, Func<string, Task<string?>>? commonDirectoryResolver) => HandleAsync(args, commonDirectoryResolver, null, null, null, null);

    public static async Task<int> HandleAsync(
        string[] args,
        Func<string, Task<string?>>? commonDirectoryResolver,
        TextWriter? errorWriter,
        Func<string, Task<RouterConfiguration>>? configurationLoader = null,
        Func<RouterConfiguration, string, Task<WorkflowResponse>>? repositoryGateChecker = null,
        Func<string, Task<string?>>? worktreeIdResolver = null)
    {
        errorWriter ??= Console.Error;
        configurationLoader ??= workingDirectory => WorkflowConfigurationService.LoadEffectiveAsync(workingDirectory);
        repositoryGateChecker ??= (configuration, workingDirectory) => WorkflowService.CheckRepositoryGateAsync(configuration, workingDirectory);
        if (args.Length == 0) return Usage();
        var command = args[0].ToLowerInvariant();
        var workingDirectory = ResolveWorkingDirectory(args, command) ?? Environment.CurrentDirectory;
        Func<string, Task<string?>> resolveCommonDirectory = commonDirectoryResolver ?? (workingDirectory => GitRepositoryService.GetCommonDirectoryAsync(workingDirectory));
        var commonDirectory = await resolveCommonDirectory(workingDirectory);
        if (commonDirectory is null) { await errorWriter.WriteLineAsync("Not a valid Git repository."); return 1; }
        Func<string, Task<string?>> resolveWorktreeId = worktreeIdResolver ?? (workingDirectory => GitRepositoryService.GetWorktreeIdAsync(workingDirectory));
        var worktreeId = await resolveWorktreeId(workingDirectory);
        if (worktreeId is null) { await errorWriter.WriteLineAsync("Not a valid Git repository."); return 1; }

        try
        {
            switch (command)
            {
                case "status":
                    var activeClaims = await WorkClaimStore.TryReadAllAsync(commonDirectory);
                    if (activeClaims.Count == 0) Console.WriteLine("No active work claims.");
                    else
                    {
                        var currentKey = WorkClaimStore.NormalizeWorktreeId(worktreeId);
                        foreach (var claim in activeClaims)
                        {
                            var isCurrent = string.Equals(claim.WorktreeId, currentKey, StringComparison.Ordinal);
                            Console.WriteLine(FormatClaimStatus(claim, isCurrent));
                        }
                    }

                    var configuration = await configurationLoader(workingDirectory);
                    var gateStatus = await repositoryGateChecker(configuration, workingDirectory);
                    if (gateStatus.Tasks.Count == 0) Console.WriteLine("No active repository workflow gate.");
                    else foreach (var task in gateStatus.Tasks.GroupBy(task => task.IssueNumber).Select(group => group.First()).OrderBy(task => task.IssueNumber)) Console.WriteLine($"Repository workflow gate: issue #{task.IssueNumber}. {task.Status.Message}");
                    return 0;
                case "list":
                    return await HandleListAsync(args, workingDirectory, worktreeId, configurationLoader, errorWriter);
                case "reconcile":
                    var reconcileResult = await WorkClaimReconciliationService.ReconcileAllAsync(workingDirectory, commonDirectory, await configurationLoader(workingDirectory));
                    if (reconcileResult.PrunedCount > 0) Console.WriteLine($"Pruned {reconcileResult.PrunedCount} stale work claim{(reconcileResult.PrunedCount == 1 ? "" : "s")} from removed worktrees.");
                    Console.WriteLine(reconcileResult.ReleasedCount > 0 ? $"Released {reconcileResult.ReleasedCount} passive or terminal work claim{(reconcileResult.ReleasedCount == 1 ? "" : "s")} across this repository." : "Active work claims remain unchanged.");
                    return 0;
                case "release":
                    var issueIndex = Array.FindIndex(args, value => string.Equals(value, "--issue", StringComparison.OrdinalIgnoreCase));
                    if (issueIndex < 0 || issueIndex + 1 >= args.Length || !int.TryParse(args[issueIndex + 1], out var issueNumber)) { Console.Error.WriteLine("Usage: cgr work release --issue <number> [working-directory]"); return 1; }
                    var didRelease = await WorkClaimStore.ReleaseForIssueAsync(commonDirectory, worktreeId, issueNumber);
                    Console.WriteLine(didRelease ? $"Released active work claim for issue #{issueNumber} by explicit user request." : $"No active work claim exists for issue #{issueNumber} in this worktree.");
                    return 0;
                default: return Usage();
            }
        }
        catch (WorkClaimFileException exception)
        {
            var claimPath = Path.Combine(commonDirectory, WorkClaimStore.ClaimFileName);
            await errorWriter.WriteLineAsync($"Invalid work-claim file: {claimPath}. {exception.Message}");
            await errorWriter.WriteLineAsync("Repair the file or remove it after confirming no active session owns the work, then retry the command.");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: cgr work <status|list [--model <model>]|reconcile|release --issue <number>> [working-directory]");
        return 1;
    }

    private static string? ResolveWorkingDirectory(string[] args, string command)
    {
        string? workingDirectory = null;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, command, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(argument, "--model", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal) || int.TryParse(argument, out _))
            {
                continue;
            }

            workingDirectory = argument;
        }

        return workingDirectory;
    }

    private static string? ParseModel(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--model", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static async Task<int> HandleListAsync(string[] args, string workingDirectory, string worktreeId, Func<string, Task<RouterConfiguration>> configurationLoader, TextWriter errorWriter)
    {
        try
        {
            var model = ParseModel(args);
            var configuration = await configurationLoader(workingDirectory);
            var commonDirectory = await GitRepositoryService.GetCommonDirectoryAsync(workingDirectory) ?? workingDirectory;
            var claim = await WorkClaimStore.TryReadAsync(commonDirectory, worktreeId);
            var otherWorktreeClaims = (await WorkClaimStore.TryReadAllAsync(commonDirectory))
                .Where(candidate => !string.Equals(
                    WorkClaimStore.NormalizeWorktreeId(candidate.WorktreeId),
                    WorkClaimStore.NormalizeWorktreeId(worktreeId),
                    StringComparison.Ordinal))
                .ToList();

            var plan = await RoutingEvaluationService.EvaluateAsync(
                configuration,
                workingDirectory,
                currentModel: model,
                activeClaim: claim,
                otherWorktreeClaims: otherWorktreeClaims,
                dependencies: new RoutingEvaluationDependencies
                {
                    ResolveAssignmentIdentityAsync = (config, wd) => ResolveIdentityAsync(config, wd)
                });

            if (!plan.IsSuccessful)
            {
                await errorWriter.WriteLineAsync($"Routing evaluation failed: {plan.DiscoveryFailureMessage}");
                return 1;
            }

            var explanations = RoutingExplanationService.ExplainAll(plan);
            if (explanations.Count == 0)
            {
                Console.WriteLine("No workflow issues found.");
                return 0;
            }

            Console.WriteLine(RoutingExplanationService.FormatExplanations(explanations));
            return 0;
        }
        catch (Exception exception)
        {
            await errorWriter.WriteLineAsync($"Error: {exception.Message}");
            return 1;
        }
    }

    private static async Task<AssignmentIdentityResolution> ResolveIdentityAsync(RouterConfiguration configuration, string workingDirectory)
    {
        if (!AssignmentRoutingService.RequiresLocalIdentity(configuration))
        {
            return AssignmentIdentityResolution.NotEnabled;
        }

        var gitIdentityValue = await GitRepositoryService.GetConfigValueAsync(workingDirectory, AssignmentRoutingService.LocalIdentityConfigKey, CancellationToken.None);
        var usernames = AssignmentRoutingService.ParseIdentityUsernames(gitIdentityValue);
        if (usernames.Count == 0)
        {
            string? authenticatedLogin = null;
            try
            {
                authenticatedLogin = await GitHubCliService.GetAuthenticatedUserAsync(workingDirectory, CancellationToken.None);
            }
            catch
            {
                // A missing or failing GitHub CLI must not crash the diagnostic; identity
                // resolution fails closed below when no CGR Git identity is configured.
            }

            if (!string.IsNullOrWhiteSpace(authenticatedLogin))
            {
                usernames = new[] { authenticatedLogin.Trim() };
            }
        }

        return AssignmentRoutingService.Resolve(configuration, usernames);
    }

    public static string FormatClaimStatus(WorkClaim claim, bool isCurrentWorktree = false)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var workerMetadata = new[]
        {
            string.IsNullOrWhiteSpace(claim.WorkerProfile) ? null : $"worker {claim.WorkerProfile}",
            string.IsNullOrWhiteSpace(claim.Model) ? null : $"model {claim.Model}"
        };
        var metadata = string.Join(", ", workerMetadata.Where(value => value is not null));
        var metadataSuffix = string.IsNullOrWhiteSpace(metadata) ? string.Empty : $", {metadata}";
        var worktreeMarker = isCurrentWorktree ? " (this worktree)" : string.Empty;
        return $"Active work claim: issue #{claim.IssueNumber}{(claim.PullRequestNumber.HasValue ? $" / pull request #{claim.PullRequestNumber.Value}" : string.Empty)}, {claim.WorkType}{metadataSuffix}, owner {claim.OwnerSessionId}, worktree {claim.WorktreeId}{worktreeMarker}, claimed {claim.ClaimedAt:O}, updated {claim.LastUpdatedAt:O}.";
    }
}

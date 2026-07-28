using CodexGithubRouter.Configurations;
using CodexGithubRouter.Git;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Work;

public static class WorkCommandHandler
{
    public static Task<int> HandleAsync(string[] args) => HandleAsync(args, null, null);

    public static Task<int> HandleAsync(string[] args, Func<string, Task<string?>>? commonDirectoryResolver) => HandleAsync(args, commonDirectoryResolver, null);

    public static async Task<int> HandleAsync(string[] args, Func<string, Task<string?>>? commonDirectoryResolver, TextWriter? errorWriter)
    {
        errorWriter ??= Console.Error;
        if (args.Length == 0) return Usage();
        var command = args[0].ToLowerInvariant();
        var workingDirectory = args.LastOrDefault(value => !value.StartsWith("--", StringComparison.Ordinal) && !string.Equals(value, command, StringComparison.OrdinalIgnoreCase) && !int.TryParse(value, out _)) ?? Environment.CurrentDirectory;
        Func<string, Task<string?>> resolveCommonDirectory = commonDirectoryResolver ?? (workingDirectory => GitRepositoryService.GetCommonDirectoryAsync(workingDirectory));
        var commonDirectory = await resolveCommonDirectory(workingDirectory);
        if (commonDirectory is null) { await errorWriter.WriteLineAsync("Not a valid Git repository."); return 1; }

        try
        {
            switch (command)
            {
                case "status":
                    var claim = await WorkClaimStore.ReadAsync(commonDirectory);
                    if (claim is null) Console.WriteLine("No active work claim.");
                    else Console.WriteLine(FormatClaimStatus(claim));
                    var configuration = await WorkflowConfigurationService.LoadOrCreateAsync();
                    var gateStatus = await WorkflowService.CheckRepositoryGateAsync(configuration, workingDirectory);
                    if (gateStatus.Tasks.Count == 0) Console.WriteLine("No active repository workflow gate.");
                    else foreach (var task in gateStatus.Tasks.GroupBy(task => task.IssueNumber).Select(group => group.First()).OrderBy(task => task.IssueNumber)) Console.WriteLine($"Repository workflow gate: issue #{task.IssueNumber}. {task.Status.Message}");
                    return 0;
                case "reconcile":
                    var released = await WorkClaimReconciliationService.ReconcileAsync(workingDirectory, commonDirectory, await WorkflowConfigurationService.LoadOrCreateAsync());
                    Console.WriteLine(released ? "Released a passive or terminal work claim." : "Active work claim remains unchanged.");
                    return 0;
                case "release":
                    var issueIndex = Array.FindIndex(args, value => string.Equals(value, "--issue", StringComparison.OrdinalIgnoreCase));
                    if (issueIndex < 0 || issueIndex + 1 >= args.Length || !int.TryParse(args[issueIndex + 1], out var issueNumber)) { Console.Error.WriteLine("Usage: cgr work release --issue <number> [working-directory]"); return 1; }
                    var didRelease = await WorkClaimStore.ReleaseForIssueAsync(commonDirectory, issueNumber);
                    Console.WriteLine(didRelease ? $"Released active work claim for issue #{issueNumber} by explicit user request." : $"No active work claim exists for issue #{issueNumber}.");
                    return 0;
                default: return Usage();
            }
        }
        catch (WorkClaimFileException exception)
        {
            var claimPath = Path.Combine(commonDirectory, "codex-github-router.work.json");
            await errorWriter.WriteLineAsync($"Invalid work-claim file: {claimPath}. {exception.Message}");
            await errorWriter.WriteLineAsync("Repair the file or remove it after confirming no active session owns the work, then retry the command.");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: cgr work <status|reconcile|release --issue <number>> [working-directory]");
        return 1;
    }

    public static string FormatClaimStatus(WorkClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var workerMetadata = new[]
        {
            string.IsNullOrWhiteSpace(claim.WorkerProfile) ? null : $"worker {claim.WorkerProfile}",
            string.IsNullOrWhiteSpace(claim.Model) ? null : $"model {claim.Model}"
        };
        var metadata = string.Join(", ", workerMetadata.Where(value => value is not null));
        var metadataSuffix = string.IsNullOrWhiteSpace(metadata) ? string.Empty : $", {metadata}";
        return $"Active work claim: issue #{claim.IssueNumber}{(claim.PullRequestNumber.HasValue ? $" / pull request #{claim.PullRequestNumber.Value}" : string.Empty)}, {claim.WorkType}{metadataSuffix}, owner {claim.OwnerSessionId}, claimed {claim.ClaimedAt:O}, updated {claim.LastUpdatedAt:O}.";
    }
}

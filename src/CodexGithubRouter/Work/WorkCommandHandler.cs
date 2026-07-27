using CodexGithubRouter.Configurations;
using CodexGithubRouter.Git;

namespace CodexGithubRouter.Work;

public static class WorkCommandHandler
{
    public static async Task<int> HandleAsync(string[] args)
    {
        if (args.Length == 0) return Usage();
        var command = args[0].ToLowerInvariant();
        var workingDirectory = args.LastOrDefault(value => !value.StartsWith("--", StringComparison.Ordinal) && !string.Equals(value, command, StringComparison.OrdinalIgnoreCase) && !int.TryParse(value, out _)) ?? Environment.CurrentDirectory;
        var commonDirectory = await GitRepositoryService.GetCommonDirectoryAsync(workingDirectory);
        if (commonDirectory is null) { Console.Error.WriteLine("Not a valid Git repository."); return 1; }

        switch (command)
        {
            case "status":
                var claim = await WorkClaimStore.ReadAsync(commonDirectory);
                if (claim is null) Console.WriteLine("No active work claim.");
                else Console.WriteLine($"Active work claim: issue #{claim.IssueNumber}{(claim.PullRequestNumber.HasValue ? $" / pull request #{claim.PullRequestNumber.Value}" : string.Empty)}, {claim.WorkType}, owner {claim.OwnerSessionId}, claimed {claim.ClaimedAt:O}, updated {claim.LastUpdatedAt:O}.");
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

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: cgr work <status|reconcile|release --issue <number>> [working-directory]");
        return 1;
    }
}

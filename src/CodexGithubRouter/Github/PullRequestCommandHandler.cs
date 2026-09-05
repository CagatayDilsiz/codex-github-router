using CodexGithubRouter.Configurations;
using CodexGithubRouter.Git;
using CodexGithubRouter.Helpers;
using CodexGithubRouter.Workflow;
using CodexGithubRouter.Work;
namespace CodexGithubRouter.GitHub;

public static class PullRequestCommandHandler
{
     public static Task<int> HandleAsync(string[] args) => HandleAsync(args, new PullRequestCommandDependencies());

     public static async Task<int> HandleAsync(string[] args, PullRequestCommandDependencies dependencies)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "list":
                return await ListPullRequestsAsync(args.Skip(1).ToArray(), dependencies);
            case "transition":
                return await TransitionPullRequestAsync(args.Skip(1).ToArray(), dependencies);
            default:
                PrintUsage();
                return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            Usage:
              cgr pull-request|pr list [--state <state>] [working-directory]
              cgr pull-request|pr transition <pull-request-number> <target-state> [working-directory]

            Commands:
              list        List open pull requests in the repository. (default open state)
              transition  Transition a pull request to a different state.

            Options:
              --state     Filter pull requests by state. Valid values are 'open', 'closed', 'all', or 'merged'.
              <target-state>  The state to transition the pull request to. Valid values are 'review-requested', 'changes-requested', 'awaiting-merge' or 'deferred'.
            """);
    }

    private static async Task<int> TransitionPullRequestAsync(string[] strings, PullRequestCommandDependencies dependencies)
    {
        var arguments = strings.ToList();

        if (arguments.Count < 2)
        {
            Console.Error.WriteLine("Error: Missing required arguments for transition command.");
            PrintUsage();
            return 1;
        }

        if (!int.TryParse(arguments[0], out int pullRequestNumber))
        {
            Console.Error.WriteLine($"Error: Invalid pull request number '{arguments[0]}'.");
            return 1;
        }

        var targetStateArg = arguments[1];

        if (!PullRequestStateParser.TryParse(targetStateArg, out PullRequestState targetState))
        {
            Console.Error.WriteLine($"Error: Invalid pull request state '{targetStateArg}'.");
            return 1;
        }

        var workingDirectory = arguments.Count > 2 ? arguments[2] : Environment.CurrentDirectory;

        try
        {
            var gitCommonDir = await dependencies.ResolveGitCommonDirectoryAsync(workingDirectory);

            if (gitCommonDir is null)
            {
                Console.Error.WriteLine("Not a valid Git repository.");
                return 1;
            }

            var worktreeId = await dependencies.ResolveWorktreeIdAsync(workingDirectory);

            if (worktreeId is null)
            {
                Console.Error.WriteLine("Not a valid Git repository.");
                return 1;
            }

            var routerConfig = await dependencies.LoadConfigurationAsync(workingDirectory);

            var pullRequestSelection = new PullRequestSelection()
            {
                Number = true,
                Labels = true,
                CreatedAt = true,
                HeadRefName = true,
                ClosingIssuesReferences = true,
            }; // You can customize the selection as needed

            var pullRequestToTransition = await dependencies.GetPullRequestAsync(workingDirectory, pullRequestNumber, pullRequestSelection);
          
            var pullRequestTransition = PullRequestTransitionPlanner.Plan(pullRequestToTransition, targetState, routerConfig);
            var activeClaim = await WorkClaimStore.ReadAsync(gitCommonDir, worktreeId);
            var isCurrentImplementationClaim = await IsCurrentImplementationClaimAsync(workingDirectory, activeClaim, pullRequestToTransition);

            if (pullRequestTransition.LabelsToAdd.Count == 0 && pullRequestTransition.LabelsToRemove.Count == 0)
            {
                if (activeClaim is not null)
                {
                    await WorkClaimStore.ReleaseForPullRequestTransitionAsync(gitCommonDir, worktreeId, activeClaim, pullRequestNumber, pullRequestToTransition.ClosingIssuesReferences.Select(issue => issue.Number).ToList(), WorkClaimReconciliationService.IsPassiveTarget(targetState), isCurrentImplementationClaim);
                }
                Console.WriteLine($"Pull request #{pullRequestNumber} is already in state '{targetState}'.");
                return 0;
            }

            await GitHubCliService.TransitionPullRequestAsync(workingDirectory, pullRequestTransition, CancellationToken.None);
            if (activeClaim is not null)
            {
                await WorkClaimStore.ReleaseForPullRequestTransitionAsync(gitCommonDir, worktreeId, activeClaim, pullRequestNumber, pullRequestToTransition.ClosingIssuesReferences.Select(issue => issue.Number).ToList(), WorkClaimReconciliationService.IsPassiveTarget(targetState), isCurrentImplementationClaim);
            }
            Console.WriteLine($"Successfully transitioned pull request #{pullRequestNumber} to state '{targetState}'.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        return 0;
    }

    private static async Task<bool> IsCurrentImplementationClaimAsync(string workingDirectory, WorkClaim? claim, PullRequest pullRequest)
    {
        if (claim is null || claim.PullRequestNumber.HasValue || claim.WorkType != WorkClaimType.Implementation)
        {
            return false;
        }

        if (!pullRequest.ClosingIssuesReferences.Any(reference => reference.Number == claim.IssueNumber))
        {
            return false;
        }

        try
        {
            var issue = await GitHubCliService.GetIssueByNumberAsync(workingDirectory, claim.IssueNumber, CancellationToken.None);
            return WorkflowService.IsCurrentClaimPullRequest(claim, issue, pullRequest);
        }
        catch (GitHubItemNotFoundException)
        {
            return false;
        }
    }

    private static async Task<int> ListPullRequestsAsync(string[] arguments, PullRequestCommandDependencies dependencies)
    {
        var stateArgIndex = arguments.IndexOf("--state");
        var states = new List<string> { "open", "closed", "all", "merged" };

        var filteredState = "open"; // Default state

        if (stateArgIndex != -1)
        {
            if (stateArgIndex + 1 >= arguments.Length)
            {
                Console.Error.WriteLine("Error: Missing value for --state argument.");
                return 1;
            }

            var stateValue = arguments[stateArgIndex + 1];
            if (states.Contains(stateValue.ToLowerInvariant()) is false)
            {
                Console.Error.WriteLine($"Error: Invalid state value '{stateValue}'. Valid values are 'open', 'closed', 'all', or 'merged'.");
                return 1;
            }

            filteredState = stateValue.ToLowerInvariant();

            // Remove the --state and its value from the arguments
            arguments = arguments.Where((arg, index) => index != stateArgIndex && index != stateArgIndex + 1).ToArray();
        }

        var workingDirectory = arguments.FirstOrDefault() ?? Environment.CurrentDirectory;

        try
        {
            var gitCommonDir = await dependencies.ResolveGitCommonDirectoryAsync(workingDirectory);

            if (gitCommonDir is null)
            {
                Console.Error.WriteLine("Not a valid Git repository.");
                return 1;
            }

            var prFilter = new PullRequestFilters
            {
                State = filteredState
            };

            var pullRequests = await dependencies.GetPullRequestsAsync(workingDirectory, prFilter);

            if (pullRequests.Count == 0)
            {
                Console.WriteLine("No pull requests found.");
                return 0;
            }

            foreach (var pr in pullRequests)
            {
                Console.WriteLine($"#{pr.Number} - {pr.Title} ({pr.State})");
            }           
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }

        return 0;
    }
}

public sealed class PullRequestCommandDependencies
{
    public Func<string, Task<string?>> ResolveGitCommonDirectoryAsync { get; init; } = workingDirectory => GitRepositoryService.GetCommonDirectoryAsync(workingDirectory);

    public Func<string, Task<string?>> ResolveWorktreeIdAsync { get; init; } = workingDirectory => GitRepositoryService.GetWorktreeIdAsync(workingDirectory);

    public Func<string, Task<RouterConfiguration>> LoadConfigurationAsync { get; init; } = workingDirectory => WorkflowConfigurationService.LoadEffectiveAsync(workingDirectory);

    public Func<string, int, PullRequestSelection, Task<PullRequest>> GetPullRequestAsync { get; init; } = (workingDirectory, pullRequestNumber, selection) =>
        GitHubCliService.GetPullRequestByNumberAsync(workingDirectory, pullRequestNumber, selection, CancellationToken.None);

    public Func<string, PullRequestFilters, Task<List<PullRequest>>> GetPullRequestsAsync { get; init; } = (workingDirectory, filters) =>
        GitHubCliService.GetPullRequestsAsync(workingDirectory, filters, new PullRequestSelection(), CancellationToken.None);
}

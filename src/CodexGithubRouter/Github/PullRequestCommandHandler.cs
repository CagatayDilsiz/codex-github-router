using CodexGithubRouter.Configurations;
using CodexGithubRouter.Git;
using CodexGithubRouter.Helpers;
using CodexGithubRouter.Workflow;
using CodexGithubRouter.Work;
namespace CodexGithubRouter.GitHub;

public static class PullRequestCommandHandler
{
     public static async Task<int> HandleAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "list":
                return await ListPullRequestsAsync(args.Skip(1).ToArray());
            case "transition":
                return await TransitionPullRequestAsync(args.Skip(1).ToArray());
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

    private static async Task<int> TransitionPullRequestAsync(string[] strings)
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
            var gitCommonDir = await GitRepositoryService.GetCommonDirectoryAsync(workingDirectory);

            if (gitCommonDir is null)
            {
                Console.Error.WriteLine("Not a valid Git repository.");
                return 1;
            }

            var routerConfig = await WorkflowConfigurationService.LoadOrCreateAsync();         

            var pullRequestSelection = new PullRequestSelection()
            {
                Number = true,
                Labels = true,
                ClosingIssuesReferences = true,
            }; // You can customize the selection as needed

            var pullRequestToTransition = await GitHubCliService.GetPullRequestByNumberAsync(workingDirectory, pullRequestNumber, pullRequestSelection, CancellationToken.None);
          
            var pullRequestTransition = PullRequestTransitionPlanner.Plan(pullRequestToTransition, targetState, routerConfig);

            if (pullRequestTransition.LabelsToAdd.Count == 0 && pullRequestTransition.LabelsToRemove.Count == 0)
            {
                Console.WriteLine($"Pull request #{pullRequestNumber} is already in state '{targetState}'.");
                return 0;
            }

            await GitHubCliService.TransitionPullRequestAsync(workingDirectory, pullRequestTransition, CancellationToken.None);
            if (targetState is PullRequestState.ReviewRequested or PullRequestState.AwaitingMerge)
            {
                foreach (var issue in pullRequestToTransition.ClosingIssuesReferences)
                {
                    await WorkClaimStore.ReleaseForIssueAsync(gitCommonDir, issue.Number);
                }
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

    private static async Task<int> ListPullRequestsAsync(string[] arguments)
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
            var gitCommonDir = await GitRepositoryService.GetCommonDirectoryAsync(workingDirectory);

            if (gitCommonDir is null)
            {
                Console.Error.WriteLine("Not a valid Git repository.");
                return 1;
            }

            var prFilter = new PullRequestFilters
            {
                State = filteredState
            };

           

            var pullRequests = await GitHubCliService.GetPullRequestsAsync(workingDirectory, prFilter, new PullRequestSelection(), CancellationToken.None);

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

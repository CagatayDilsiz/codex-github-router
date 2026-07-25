using CodexGithubRouter.Configurations;
using CodexGithubRouter.Git;
using CodexGithubRouter.Helpers;
using CodexGithubRouter.Workflow;
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

            Commands:
              list        List open pull requests in the repository. (default open state)

            Options:
              --state     Filter pull requests by state. Valid values are 'open', 'closed', 'all', or 'merged'.
            """);
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
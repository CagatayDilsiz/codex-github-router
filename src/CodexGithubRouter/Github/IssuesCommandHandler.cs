using CodexGithubRouter.Configurations;
using CodexGithubRouter.Git;
using CodexGithubRouter.Helpers;
using CodexGithubRouter.Workflow;
using CodexGithubRouter.Work;
namespace CodexGithubRouter.GitHub;

public static class IssuesCommandHandler
{
    public static Task<int> HandleAsync(string[] args) => HandleAsync(args, new IssueCommandDependencies());

    public static async Task<int> HandleAsync(string[] args, IssueCommandDependencies dependencies)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "list":
                return await ListIssuesAsync(args.Skip(1).ToArray(), dependencies);
            case "transition":
                return await TransitionIssueAsync(args.Skip(1).ToArray(), dependencies);
            default:
                PrintUsage();
                return 1;
        }
    }

    private static async Task<int> TransitionIssueAsync(string[] strings, IssueCommandDependencies dependencies)
    {
        var arguments = strings.ToList();

        if (arguments.Count < 2)
        {
            Console.Error.WriteLine("Error: Missing required arguments for transition command.");
            PrintUsage();
            return 1;
        }

        if (!int.TryParse(arguments[0], out int issueNumber))
        {
            Console.Error.WriteLine($"Error: Invalid issue number '{arguments[0]}'.");
            return 1;
        }

        var targetStateArg = arguments[1];

        if (!WorkflowStateParser.TryParse(targetStateArg, out WorkflowState targetState))
        {
            Console.Error.WriteLine($"Error: Invalid workflow state '{targetStateArg}'.");
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

            var routerConfig = await dependencies.LoadConfigurationAsync(workingDirectory);

            var issueToTransition = await dependencies.GetIssueByNumberAsync(workingDirectory, issueNumber);

            var issueTransition = IssueTransitionPlanner.Plan(issueToTransition, targetState, routerConfig);

            // A workflow transition can complete any worktree's claim over the affected issue.
            // The matching claim is located repository-wide by issue identity and released with
            // its own ClaimId/Version-guarded worktree, so mutating GitHub state never leaves a
            // peer worktree's claim behind.
            var matchingClaim = (await WorkClaimStore.ReadAllAsync(gitCommonDir))
                .FirstOrDefault(claim => claim.IssueNumber == issueNumber);
            var shouldRelease = WorkClaimReconciliationService.ShouldReleaseForIssueTransition(matchingClaim, issueNumber, targetState);

            if (issueTransition.LabelsToAdd.Count == 0 && issueTransition.LabelsToRemove.Count == 0)
            {
                if (shouldRelease && matchingClaim is not null)
                {
                    await WorkClaimStore.ReleaseIfMatchesAsync(gitCommonDir, matchingClaim.WorktreeId, matchingClaim);
                }

                Console.WriteLine($"Issue #{issueNumber} is already in state '{targetState}'.");
                return 0;
            }

            await dependencies.TransitionIssueAsync(workingDirectory, issueTransition);
            if (shouldRelease && matchingClaim is not null)
            {
                await WorkClaimStore.ReleaseIfMatchesAsync(gitCommonDir, matchingClaim.WorktreeId, matchingClaim);
            }

            Console.WriteLine($"Successfully transitioned issue #{issueNumber} to state '{targetState}'.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  cgr issue list [--use-configured|-c] [--state <state>] [working-directory]");
        Console.WriteLine("  cgr issue transition <issue-number> <target-state> [working-directory]");

        Console.WriteLine("Options For 'cgr issue list':");
        Console.WriteLine("  --use-configured, -c   Use the configured issue filters from the router configuration.");
        Console.WriteLine("  --state <state>        Filter issues by workflow state (Ready, InProgress, Completed, Blocked, NeedsInfo, Abandoned).");

        Console.WriteLine("Options For 'cgr issue transition':");
        Console.WriteLine("  <issue-number>         The number of the issue to transition.");
        Console.WriteLine("  <target-state>         The target workflow state to transition the issue to (Ready, InProgress, Completed, Blocked, NeedsInfo, Abandoned).");
        Console.WriteLine("  [working-directory]    Optional. The working directory of the Git repository. Defaults to the current directory if not specified.");
    }

    private static async Task<int> ListIssuesAsync(string[] args, IssueCommandDependencies dependencies)
    {
        var arguments = args.ToList();
        bool useConfiguredIssues = false;

        var configuredIssues = arguments.FirstOrDefault(arg => arg.Equals("--use-configured", StringComparison.OrdinalIgnoreCase) || arg.Equals("-c", StringComparison.OrdinalIgnoreCase));

        if (configuredIssues != null)
        {
            useConfiguredIssues = true;
            arguments.Remove(configuredIssues);
        }

        var stateArgIndex = arguments.FindIndex(arg => arg.Equals("--state", StringComparison.OrdinalIgnoreCase));

        WorkflowState state = WorkflowState.Ready;

        if (stateArgIndex != -1)
        {
            if (stateArgIndex + 1 >= arguments.Count)
            {
                Console.Error.WriteLine("Error: Missing value for --state argument.");
                return 1;
            }

            var stateValue = arguments[stateArgIndex + 1];

            if (!WorkflowStateParser.TryParse(stateValue, out state))
            {
                Console.Error.WriteLine($"Invalid workflow state: {stateValue}");
                return 1;
            }

            // Remove the --state and its value from the arguments
            arguments.RemoveAt(stateArgIndex); // Remove --state
            arguments.RemoveAt(stateArgIndex); // Remove the state value
            useConfiguredIssues = true;
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

            var issueFilters = new IssueFilters();

            if (useConfiguredIssues)
            {
                var routerConfig = await dependencies.LoadConfigurationAsync(workingDirectory);
                issueFilters = IssueFilterResolver.ByState(routerConfig, state);
            }

            var issues = await dependencies.GetIssuesAsync(workingDirectory, issueFilters);

            if (issues.Count == 0)
            {
                if (useConfiguredIssues)
                {
                    Console.WriteLine("No open issues found matching the configured filters.");
                }
                else
                {
                    Console.WriteLine("No open issues found.");
                }

                return 0;
            }

            if (useConfiguredIssues)
            {
                Console.WriteLine($"Open issues matching the configured filters ({issues.Count}):");
            }
            else
            {
                Console.WriteLine($"Open issues ({issues.Count}):");
            }

            foreach (var issue in issues)
            {
                Console.WriteLine($"#{issue.Number}: {issue.Title} ({issue.Url})");
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

public sealed class IssueCommandDependencies
{
    public Func<string, Task<string?>> ResolveGitCommonDirectoryAsync { get; init; } = workingDirectory => GitRepositoryService.GetCommonDirectoryAsync(workingDirectory);

    public Func<string, Task<RouterConfiguration>> LoadConfigurationAsync { get; init; } = workingDirectory => WorkflowConfigurationService.LoadEffectiveAsync(workingDirectory);

    public Func<string, IssueFilters, Task<List<Issue>>> GetIssuesAsync { get; init; } = (workingDirectory, filters) =>
        GitHubCliService.GetIssuesAsync(workingDirectory, filters, false, CancellationToken.None);

    public Func<string, int, Task<Issue>> GetIssueByNumberAsync { get; init; } = (workingDirectory, issueNumber) =>
        GitHubCliService.GetIssueByNumberAsync(workingDirectory, issueNumber, CancellationToken.None);

    public Func<string, IssueTransition, Task> TransitionIssueAsync { get; init; } = (workingDirectory, transition) =>
        GitHubCliService.TransitionIssueAsync(workingDirectory, transition, CancellationToken.None);
}

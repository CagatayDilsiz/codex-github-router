using CodexGithubRouter.Configurations;
using CodexGithubRouter.Git;
using CodexGithubRouter.GitHub;
using CodexGithubRouter.Work;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Explain;

public static class ExplainCommandHandler
{
    public static Task<int> HandleAsync(string[] args) => HandleAsync(args, new ExplainCommandDependencies(), CancellationToken.None);

    public static async Task<int> HandleAsync(string[] args, ExplainCommandDependencies dependencies, CancellationToken cancellationToken = default)
    {
        if (!TryParseArguments(args, out var issueNumber, out var workingDirectory, out var model, out var usageError))
        {
            dependencies.Error.WriteLine(usageError);
            return 2;
        }

        try
        {
            var commonDirectory = await dependencies.GetGitCommonDirectoryAsync(workingDirectory, cancellationToken);
            if (commonDirectory is null)
            {
                dependencies.Error.WriteLine("Not a valid Git repository.");
                return 1;
            }

            var configuration = await dependencies.LoadEffectiveConfigurationAsync(workingDirectory, cancellationToken);
            var identity = await ResolveIdentityAsync(configuration, workingDirectory, dependencies, cancellationToken);
            var activeClaim = await dependencies.ReadWorkClaimAsync(commonDirectory, cancellationToken);

            var plan = await RoutingEvaluationService.EvaluateAsync(
                configuration,
                workingDirectory,
                currentModel: model,
                assignmentIdentity: identity,
                activeClaim: activeClaim,
                dependencies: dependencies.RoutingEvaluation);

            if (!plan.IsSuccessful)
            {
                dependencies.Error.WriteLine($"Routing evaluation failed: {plan.DiscoveryFailureMessage}");
                return 1;
            }

            if (issueNumber.HasValue)
            {
                return await ExplainSingleIssueAsync(issueNumber.Value, plan, workingDirectory, dependencies, cancellationToken);
            }

            return await ExplainAllIssuesAsync(plan, dependencies);
        }
        catch (Exception exception)
        {
            dependencies.Error.WriteLine($"Error: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> ExplainSingleIssueAsync(
        int issueNumber,
        RoutingEvaluationResult plan,
        string workingDirectory,
        ExplainCommandDependencies dependencies,
        CancellationToken cancellationToken)
    {
        var issue = plan.ConsideredIssues.FirstOrDefault(candidate => candidate.Number == issueNumber);
        if (issue is null)
        {
            issue = await dependencies.GetIssueByNumberAsync(workingDirectory, issueNumber, cancellationToken);
        }

        if (issue is null)
        {
            dependencies.Error.WriteLine($"Issue #{issueNumber} not found.");
            return 1;
        }

        var explanation = RoutingExplanationService.Explain(plan, issue);
        dependencies.Output.WriteLine(RoutingExplanationService.FormatSingleExplanation(explanation));
        return 0;
    }

    private static Task<int> ExplainAllIssuesAsync(RoutingEvaluationResult plan, ExplainCommandDependencies dependencies)
    {
        var explanations = RoutingExplanationService.ExplainAll(plan);
        if (explanations.Count == 0)
        {
            dependencies.Output.WriteLine("No issues found matching the workflow configuration.");
            return Task.FromResult(0);
        }

        dependencies.Output.WriteLine(RoutingExplanationService.FormatExplanations(explanations));
        return Task.FromResult(0);
    }

    private static async Task<AssignmentIdentity?> ResolveIdentityAsync(
        RouterConfiguration configuration,
        string workingDirectory,
        ExplainCommandDependencies dependencies,
        CancellationToken cancellationToken)
    {
        if (!AssignmentRoutingService.RequiresLocalIdentity(configuration))
        {
            return null;
        }

        var gitIdentityValue = await dependencies.ResolveLocalIdentityAsync(workingDirectory, cancellationToken);
        var gitUsernames = AssignmentRoutingService.ParseIdentityUsernames(gitIdentityValue);
        if (gitUsernames.Count > 0)
        {
            var resolution = AssignmentRoutingService.Resolve(configuration, gitUsernames);
            return AssignmentRoutingService.ResolveIdentity(resolution);
        }

        try
        {
            var authenticatedLogin = await dependencies.ResolveAuthenticatedGitHubLoginAsync(workingDirectory, cancellationToken);
            if (!string.IsNullOrWhiteSpace(authenticatedLogin))
            {
                var resolution = AssignmentRoutingService.Resolve(configuration, new[] { authenticatedLogin.Trim() });
                return AssignmentRoutingService.ResolveIdentity(resolution);
            }
        }
        catch
        {
            // Identity resolution failure is not fatal for explanation; results will show the identity issue.
        }

        return null;
    }

    private static bool TryParseArguments(string[] args, out int? issueNumber, out string workingDirectory, out string? model, out string error)
    {
        issueNumber = null;
        workingDirectory = Environment.CurrentDirectory;
        model = null;
        error = string.Empty;

        var positionals = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, "--model", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length)
                {
                    error = "cgr explain: --model requires a value.";
                    return false;
                }

                model = args[++index];
                continue;
            }

            if (string.Equals(argument, "--issue", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var number))
                {
                    error = "cgr explain: --issue requires a numeric issue number.";
                    return false;
                }

                issueNumber = number;
                index++;
                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"cgr explain: unknown option: {argument}";
                return false;
            }

            positionals.Add(argument);
        }

        if (positionals.Count > 1)
        {
            error = "cgr explain: too many arguments.";
            return false;
        }

        if (positionals.Count == 1)
        {
            workingDirectory = positionals[0];
        }

        return true;
    }
}

public sealed class ExplainCommandDependencies
{
    public TextWriter Output { get; init; } = Console.Out;

    public TextWriter Error { get; init; } = Console.Error;

    public Func<string, CancellationToken, Task<string?>> GetGitCommonDirectoryAsync { get; init; }
        = (workingDirectory, cancellationToken) => GitRepositoryService.GetCommonDirectoryAsync(workingDirectory, cancellationToken);

    public Func<string, CancellationToken, Task<RouterConfiguration>> LoadEffectiveConfigurationAsync { get; init; }
        = (workingDirectory, cancellationToken) => WorkflowConfigurationService.LoadEffectiveAsync(workingDirectory, cancellationToken);

    public Func<string, int, CancellationToken, Task<Issue?>> GetIssueByNumberAsync { get; init; }
        = async (workingDirectory, issueNumber, cancellationToken) =>
        {
            try
            {
                return await GitHubCliService.GetIssueByNumberAsync(workingDirectory, issueNumber, cancellationToken);
            }
            catch (GitHubItemNotFoundException)
            {
                return null;
            }
        };

    public RoutingEvaluationDependencies RoutingEvaluation { get; init; } = new();

    public Func<string, CancellationToken, Task<WorkClaim?>> ReadWorkClaimAsync { get; init; }
        = (gitCommonDirectory, cancellationToken) => WorkClaimStore.TryReadAsync(gitCommonDirectory, cancellationToken);

    public Func<string, CancellationToken, Task<string?>> ResolveLocalIdentityAsync { get; init; }
        = (repositoryRoot, cancellationToken) => GitRepositoryService.GetConfigValueAsync(repositoryRoot, AssignmentRoutingService.LocalIdentityConfigKey, cancellationToken);

    public Func<string, CancellationToken, Task<string?>> ResolveAuthenticatedGitHubLoginAsync { get; init; }
        = (repositoryRoot, cancellationToken) => GitHubCliService.GetAuthenticatedUserAsync(repositoryRoot, cancellationToken);
}